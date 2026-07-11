## Context

Step 22 is the first step of the Neo AOT toolchain (Steps 22-26), a pure
optimization layer on top of the working Neo JIT path. The design doc
(`.trae/documents/object-model-neo-design.md` §11, §8.4.2) decided the generic-
method strategy: **template + runtime instantiation** (NOT precompile-time
exhaustive expansion). A generic method DEFINITION compiles once to a
`templateBody` + a `patches[]` table; a runtime instantiation either reuses the
template verbatim (reference-type args) or `CloneAndPatch`-es it (value-type
args). Step 22 ships the in-memory mechanism + an equivalence test; the `.neo`
serialization (Step 23), the `ilrt_neoc` CLI (Step 24), the runtime loader
(Step 25), and perf (Step 26) are deferred.

**Current state (the reference path).** `ILMethod.MakeGenericMethod`
(`ILMethod.cs:1096`) creates a `GenericInstanceMethod`; each instance gets its
own `BodyRegister` via `JITCompiler.Compile` (`JITCompiler.cs:296`). The Compile
pipeline:
1. `Translate` (CIL -> register ops; token resolution; Initobj insertion).
2. Optimizer (`ForwardCopyPropagation` / `BackwardsCopyPropagation` /
   `EliminateConstantLoad`).
3. Build `res` + jump-target resolution.
4. `CleanupRegister` (register compaction).
5. `TypeSpecializeNeoOpcodes` (Neo-only: Move->Move_Vt, Ldfld specialization,
   F-10 markers, constrained resolution). **T-dependent.**
6. `AllocateLocalStackSpaces` (Neo-only: byte/ref offsets per slot).
   **T-dependent.**
7. `LowerNeoOffsets` (Neo-only: stamps byte offsets from localInfos into
   `NeoExecuteBody`). **T-dependent (transitively, via localInfos).**

`Prewarm` skips the open definition (`GenericParameterCount > 0 &&
!IsGenericInstance`, `ILMethod.cs:655`). There is NO template reuse today.

The load-bearing grounding for this design is a per-occurrence JIT **dump diff**
(captured during propose, then removed): several instantiations of
`T Probe22<T>(T v, int n) { T current = v; int sum = n + 1; return current; }`
plus a `T[]/ref T` variant, compiled at `T = int / long / S3(struct) / object /
string`. The full diff is in §2; the one-line summary:

- `Register1/2/3` are **T-INVARIANT** across `int`/`long`/`object`/`string` for
  matching instructions. Only `Operand`-level fields and the lowered byte
  offsets vary.
- `T=int` CodeBody == `T=long` CodeBody (only `localInfos[0]` size differs:
  4 vs 8).
- `T=object` NEOEXECBODY == `T=string` NEOEXECBODY **byte-for-byte**
  (ref-share confirmed, including type tokens, because the body has no
  `Box T`/`Isinst T`).
- `T=S3` CodeBody **differs structurally**: 2 extra `Initobj` ops (instr count
  7 vs 5) + type-token operands (`Op=0x10000151`).

## Goals / Non-Goals

**Goals:**
- `PatchEntry` struct whose fields are grounded in the dump diff (what ACTUALLY
  varies, not what the design doc guessed).
- Template compile (generic definition -> register-index `templateBody` +
  `PatchEntry[]`), cached per-definition.
- `CloneAndPatch(template, concreteTypeArgs)` producing a `NeoExecuteBody`
  provably equivalent to the per-occurrence JIT body for the same instantiation.
- Ref-type-share vs value-type-CloneAndPatch discrimination.
- The V1 structural-equivalence test (load-bearing gate) + V2 functional
  roundtrip (sanity).
- Additive + Neo-only + Legacy-neutral.

**Non-Goals:**
- `.neo` binary format + serializer/deserializer (Step 23).
- `ilrt_neoc` standalone precompile CLI (Step 24).
- Runtime `.neo` loader + ILType/ILMethod Cecil-decoupling dual-path (Step 25).
- Perf benchmarks (Step 26).
- Removing or changing the per-occurrence JIT instantiation path (it is the
  reference + the fallback).
- AOT-load-time specialization without Cecil (Step 22 runs in Neo JIT mode,
  Cecil IS available; CloneAndPatch may re-run the back-half).

## Decisions

### Decision 1: two KINDS of T-dependence, only one is a `PatchEntry`

The dump diff proves T-dependence splits into two cleanly separable kinds:

**Kind A — Operand-level (T-IDENTITY-dependent; enumerable; captured as
`PatchEntry`).** Opcode FIELDS whose value depends on which concrete type T is
(not on T's byte size). The dump shows exactly these:
- **Type-token operands** (`Operand` field): `Initobj`/`Box`/`Unbox`/
  `Unbox_Any`/`Isinst`/`Castclass`/`Newarr`/`Stobj`/`Ldobj` carry the concrete
  T's type hash (e.g. `Op=0x10000151` for S3). `Constrained.` carries T's hash.
  `Ldelem_Any`/`Stelem_Any` carry the T-element hash for `T[]`.
- **Method-token operands** (`Operand2` field): `Call`/`Callvirt`/
  `Call_Redirect` on a T-qualified method (e.g. `constrained.callvirt T.GetHashCode`,
  `T.Equals(T)`) resolve through the concrete T to a method hash.
- **The `Move` is-ref flag** (`Operand` field, alias of `Register3` @offset 8):
  `0` for a value byte-copy, `1` for a 4-byte mStack-index ref-copy. The dump
  shows `T=object`/`string` Moves carry `Op=0x1` (ref move) while `T=int`/`long`
  Moves carry `Op=0x0`. (Stamped in the register-index body, pre-lowering.)

These are all OPCODE-FIELD patches at a fixed instruction index — exactly what a
`PatchEntry` records. The register INDEX (`Register1/2/3`) is NOT among them
(the dump confirms indices are T-invariant).

**Kind B — Size-level (T-SIZE-dependent; CUMULATIVE; re-derived, NOT a
`PatchEntry`).** The byte offsets. The dump shows `localInfos[i].Offset` is
CUMULATIVE: changing param `v` from 4 to 8 to 12 bytes shifts the `Offset` of
every subsequent slot, and `LowerNeoOffsets` stamps those shifted offsets into
EVERY affected opcode's `DstOffset`/`SrcOffset`/`OperandOffset` (and the `Move`
copy-size `Operand2` + dst-ref-offset `Operand3`). Patching these one-site-at-a-
time would require re-deriving the cumulative offset table — which IS
`AllocateLocalStackSpaces`. So they are re-derived, not patched.

```
struct PatchEntry {
    int InstrIdx;            // index into the template OpCodeR[] body
    PatchField Field;        // which OpCodeR field: Operand / Operand2 / Operand4
    PatchKind Kind;          // TypeToken / MethodToken / IsRefMoveFlag
    int  GenericParamIdx;    // which generic arg (0-based) drives the value
}

enum PatchField { Operand, Operand2, Operand4 }   // never Register1/2/3 (T-invariant)
enum PatchKind  { TypeToken, MethodToken, IsRefMoveFlag }
```

`Field` is one of the STANDALONE `OpCodeR` fields (offset 8 / 12 / 20 —
`Operand`/`Operand2`/`Operand4`). It MUST NOT be `Register1/2/3` (those alias
`DstOffset`/`SrcOffset`/`OperandOffset` and hold byte offsets post-lowering —
Kind B territory). The F-8 / OpCodeR-union gotcha (§4 of the handoff) applies:
stamping a standalone field is safe only if it does not alias a wide-immediate
field a consumer reads; the extractor MUST verify disjointness per opcode kind
(same discipline as the F-8 `LowerNeoOffsets` fix).

**Alternatives considered.**
- *Capture byte offsets as patches too.* REJECTED: the dump proves offsets are
  cumulative; patching them == re-running Allocate. No benefit, double work.
- *A single `PatchKind` enum.* REJECTED: type-token vs method-token vs is-ref-
  flag need different concrete-value derivation (type hash vs method-override
  resolution vs ref-ness check). The enum keeps the extractor + applier honest.

### Decision 2: the template stores the register-index body; `CloneAndPatch` re-runs the T-dependent back-half

The template body is the register-index `OpCodeR[]` captured AFTER
`CleanupRegister`, BEFORE `TypeSpecializeNeoOpcodes` (the T-invariant structure).
`CloneAndPatch(template, concreteTypeArgs)`:
1. Clone `templateBody`.
2. Re-run `TypeSpecializeNeoOpcodes(cloned, concreteT)` — handles Move->Move_Vt,
   F-10 markers, constrained resolution, Initobj insertion for value-T locals.
3. Re-run `AllocateLocalStackSpaces(frame, concreteT)` — concrete `localInfos` +
   `TotalStructSize`/`TotalRefSize`.
4. Clone -> `LowerNeoOffsets(frame, concrete localInfos)` — concrete byte offsets.
5. (AOT-direction) Apply the `PatchEntry[]` table to fix Kind-A operands. In
   Step 22 (Cecil available) step 2 already resolves most Kind-A sites, so the
   PatchEntry APPLICATION is exercised as the equivalence witness (the V1 test
   asserts apply-patches == re-run-TypeSpecialize); Step 23+ uses the table to
   specialize WITHOUT Cecil.

**Why capture before TypeSpecialize, not after.** TypeSpecialize is T-dependent
(Move->Move_Vt fires when the slot is a value type WITH refs; the Initobj loop
fires for non-primitive value-type T). The pre-TypeSpecialize register body is
the latest T-INVARIANT artifact in the pipeline, so it is the natural template.
The Initobj-insertion loop runs pre-optimizer in the per-occurrence JIT; for the
template (open definition, T = generic parameter, `IsValueType == false`) NO
Initobj is inserted, so CloneAndPatch must re-run that decision for concrete T
(step 2 covers it).

**Why this is equivalent to per-occurrence JIT (the V1 anchor).** The Compile
pipeline is deterministic. The front-half (Translate + optimizer + CleanupRegister)
is T-invariant, so its output (the template) is identical whether produced
once (cached) or per-occurrence. The back-half (TypeSpecialize + Allocate +
Lower) is a pure function of `(templateBody, concreteT)`. Therefore
`CloneAndPatch(template, T) == Compile(MakeGenericMethod(T)).NeoExecuteBody`.
The V1 test asserts this for a matrix of (generic method, concrete T).

**Alternatives considered.**
- *Capture the template AFTER lowering for a canonical T, then patch the lowered
  body.* REJECTED by the dump: the lowered byte offsets are cumulative, and the
  instruction STREAM itself differs (Initobj count: 5 vs 7 for int vs S3). A
  lowered-template + offset-patch approach cannot handle instruction-count
  differences without an INSERT-patch kind, and re-deriving cumulative offsets
  is Allocate anyway. Re-running the back-half on a register-index template is
  strictly simpler and provably correct.
- *Capture the template after TypeSpecialize for T=object (ref canonical), share
  for all ref-T, patch for value-T.* Partially works (ref-share holds for the
  dump's `Probe22`), but breaks for bodies with T-identity tokens (`Box T`,
  `Isinst T`: `T=object` vs `T=string` differ in the token). The register-index
  pre-TypeSpecialize template sidesteps this entirely.

### Decision 3: lowering interaction — patches are PRE-LOWERING; byte offsets are re-derived (NEVER patched)

This is the trickiest decision (flagged in the planning context). The dump
resolves it cleanly:

- `PatchEntry` records **PRE-LOWERING register-index / Operand-level** sites
  (Kind A). These live in the register-index `CodeBody`, are T-invariant in
  position, and survive unchanged through `NeoExecuteBody = CodeBody.Clone()`
  (LowerNeoOffsets does not touch the standalone Operand fields except where it
  deliberately stamps copy-size/dst-ref on `Move`/`Move_Vt` — those are Kind B,
  re-derived, not patched).
- `LowerNeoOffsets` stamps byte offsets into `Register1/2/3` (aliased as
  `DstOffset`/`SrcOffset`/`OperandOffset` @offsets 4/6/8). Those are Kind B and
  are ALWAYS re-derived by re-running Allocate+Lower on the concrete
  `localInfos`. A `PatchEntry` NEVER captures a post-lowering byte offset.

So the ordering is: **apply Kind-A patches (or re-run TypeSpecialize) on the
register-index body FIRST, THEN re-run Allocate+Lower to derive Kind-B byte
offsets.** There is no "patch after lowering" path.

### Decision 4: cache key + ref-type-share vs value-type-CloneAndPatch discrimination

**Cache key:** the generic method DEFINITION — the open `ILMethod` with
`GenericParameterCount > 0 && !IsGenericInstance`. Identity = the `ILMethod`
instance (the definition object is the natural key; `GetHashCode()` is already
identity-based). The template is stored ON the definition `ILMethod` (a new
field, Neo-only). The 2nd+ instantiation of the same definition reuses it.

**Discrimination (at instantiation, in the `BodyRegister` getter of a generic-
instance `ILMethod`):**
- Let `def = this.genericDefinition` (the open definition; set by
  `MakeGenericMethod` at `ILMethod.cs:1124`). If `def == null`, fall through to
  per-occurrence JIT.
- Let `typeArgs = this.GenericArugmentsArray`.
- **All-ref case:** if EVERY `typeArgs[i]` is a reference type AND the cached
  template's patch table contains NO `TypeToken`/`MethodToken` entry whose
  concrete value would differ across ref types (i.e. the body has no T-identity
  operand) -> share ONE cached "ref body" verbatim (no clone). The dump proves
  `object` == `string` byte-for-byte for such bodies.
- **Value-T case:** if ANY `typeArgs[i]` is a value type -> `CloneAndPatch(def.
  Template, typeArgs)`. (Even a single value-T arg changes frame sizes, so no
  sharing is possible across different value-T's.)
- **Mixed / token-bearing ref case:** if all ref-T BUT the body has a
  T-identity token (rare: `Box T`, `Isinst T`) -> `CloneAndPatch` with the
  ref-T args (the patch fixes the token; sizes are ref-uniform). Cheaper than
  full per-occurrence JIT (skips the optimizer), not free like the all-ref
  share.

**Quirk observed in the dump (must be reproduced, NOT "fixed").** A generic-T-
typed LOCAL is allocated by `AllocateLocalStackSpaces` reading the Cecil
`VariableType` directly (`JITCompiler.cs:1579`); for a generic parameter
`vt.IsValueType == false`, so it falls to the reference branch and the local is
a 4-byte boxed-ref REGARDLESS of whether T is `int`/`long`/`S3`. Only the
PARAMETER (resolved via `appdomain.GetType`) gets the concrete size. This is
pre-existing per-occurrence JIT behavior; `CloneAndPatch` reproduces it
automatically (it re-runs Allocate on the same Cecil body), and the V1 test
guards the equivalence. Step 22 does NOT change this allocation.

### Decision 5: integration point — additive hook in `ILMethod.BodyRegister` getter

The hook lives in the `BodyRegister` getter (`ILMethod.cs:379`) / the
`InitCodeBody(true)` path (`ILMethod.cs:684`), Neo-only. For a generic-instance
`ILMethod` whose `genericDefinition` has a cached template, route through
`CloneAndPatch` instead of `new JITCompiler(...).Compile(...)`. The per-occurrence
`Compile` path STAYS (it is the fallback when no template is cached, and the
reference the V1 test compares against). The template itself is BUILT by a
first `Compile` of the open definition (or a dedicated `BuildTemplate` entry
that runs Compile and captures the body at the pre-TypeSpecialize point). All
gated `#if ENABLE_NEO_MODE`; Legacy `ExecuteR` is byte-identical (the template
field + the hook compile out).

**Compile pipeline refactor.** `JITCompiler.Compile` currently inlines steps
5-7 (TypeSpecialize + Allocate + Lower). To let `CloneAndPatch` re-run the
back-half on a cloned body, factor steps 5-7 into a callable
`RunNeoBackHalf(res, locVarRegStart, totalRegCnt, ...)` (Neo-only). The
per-occurrence `Compile` calls it inline; `CloneAndPatch` calls it on the cloned
template body. This refactor is mechanical and the V1 test proves byte-identity.

### Decision 6: V1 equivalence test design (the load-bearing gate)

The V1 test asserts `CloneAndPatch(template, T) == per-occurrence JIT(T)` for a
matrix of (generic method, concrete T). Concretely:
- **Comparison:** a host-side helper `OpCodeREquality.AssertEqual(OpCodeR[] a,
  OpCodeR[] b)` compares length + (Code, Register1, Register2, Register3,
  Operand, Operand2, Operand3, Operand4) per index. The test METHOD (a
  `public static` parameterless method in `TestCases/`) cannot directly read
  `OpCodeR[]` from the interpreted side, so it drives BOTH paths to produce a
  body, then asserts via the **value/divide path**: it invokes the same generic
  instantiation via both paths and checks identical results (V2), AND a
  host-side comparator (callable because the test harness runs on the host
  CLR, with `ILRuntime.dll` referenced) asserts structural equality of the two
  `NeoExecuteBody` arrays exposed via a test-hook accessor on `ILMethod`.
- **Matrix:** `T = int (primitive), long (8-byte primitive), S3 (IL struct,
  no refs), object/string (ref, ref-share), nested generic
  (GenericType<T>.Method), generic method on a generic type`. Each shape
  exercises a distinct T-dependence profile (size-only, struct-Initobj,
  ref-share, nested).
- **The V1 gate is load-bearing** because Step 22 has no FAIL-on-HEAD functional
  probe (the JIT path already works). Structural equivalence of the two bodies
  IS the correctness statement.

V2 (functional roundtrip): invoke `Probe22<int>(...)`, `Probe22<S3>(...)`, etc.
via the template path and the per-occurrence path, assert identical observable
results. Sanity check (does not prove body equivalence, only behavior).

## Risks / Trade-offs

- **[The template body is captured at the wrong pipeline stage]** -> the V1 test
  fails for some T (the cloned+back-half'd body != per-occurrence body).
  Mitigation: the dump already proved the pre-TypeSpecialize point is the latest
  T-invariant artifact; the V1 matrix covers the T-profiles that exercise each
  T-dependent back-half arm. If V1 fails, the fallback is the per-occurrence
  path (correctness preserved; only the optimization is lost).
- **[A Kind-A site is missed by the PatchEntry extractor]** -> the AOT direction
  (Step 23) would serialize an incomplete patch table. Mitigation: Step 22's V1
  test exercises the PatchEntry-apply path AND the re-run-TypeSpecialize path
  and asserts they agree; a missed site diverges in V1. The extractor is
  table-driven per opcode kind (the same kinds the dump enumerated).
- **[OpCodeR-union field aliasing (F-8 gotcha)]** -> a `PatchEntry` that stamps
  a field aliasing a wide immediate corrupts an unrelated consumer. Mitigation:
  `PatchField` is restricted to the STANDALONE fields (`Operand`/`Operand2`/
  `Operand4` @8/12/20); the extractor verifies per opcode kind that the field is
  not alias-read by another arm (F-8 discipline). `Register1/2/3` (aliased with
  the byte offsets) are explicitly excluded.
- **[The Initobj-instruction-count difference breaks a fixed-index patch table]**
  -> if CloneAndPatch applied patches to a cloned-but-not-re-TypeSpecialized
  body, the indices would be wrong. Mitigation: CloneAndPatch RE-RUNS
  TypeSpecialize (which re-inserts Initobj) BEFORE applying Kind-A patches by
  index; the patch indices are computed against the post-TypeSpecialize body
  (the same body the per-occurrence JIT produces), so they align.
- **[Regression: the template hook changes existing JIT behavior]** -> MEDIUM
  risk. Mitigation: the hook is additive (no template cached -> per-occurrence
  path unchanged); Neo-only (`#if ENABLE_NEO_MODE`); Legacy-neutral (compile
  gate + a stash-toggle plain-`Debug` NeoStep-filter run); full NeoStep smoke
  (204/204) is the regression gate.
- **[Per-definition cache memory growth]** -> one template per generic
  definition. Bounded by the number of generic method definitions in the loaded
  assemblies (typically small). Not a Step-22 concern; revisit at Step 26.

## Migration Plan

None. The change is additive and Neo-only. The per-occurrence JIT path remains
the default when no template is cached; the template cache populates lazily and
only changes the SPEED of generic instantiation, not its result. Rollback =
revert the change (the per-occurrence path is untouched).

## Open Questions

- **PatchEntry table completeness vs re-run-TypeSpecialize redundancy (Step 22
  scope).** In Step 22 CloneAndPatch re-runs TypeSpecialize (Cecil available),
  so the PatchEntry APPLICATION is technically redundant for correctness. The
  proposal keeps the PatchEntry table IN scope (populated + asserted-equivalent)
  because it is the contract Step 23 serializes. Open: should CloneAndPatch's
  PRIMARY path be "apply patches" (and re-run-TypeSpecialize only as a
  development cross-check) or vice-versa? Recommendation: re-run-TypeSpecialize
  is primary in Step 22 (lower risk — it is the proven path); PatchEntry-apply
  is asserted-equivalent via V1 and becomes primary at Step 23. Confirm at
  apply.
- **Where to expose `NeoExecuteBody` for the V1 host-side comparator.** Need a
  test-hook accessor on `ILMethod` (e.g. an internal `GetNeoExecuteBodyForTest`
  property, `#if DEBUG`). Confirm the accessor does not perturb the production
  layout at apply.
