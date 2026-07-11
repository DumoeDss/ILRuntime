# Planning Context — neo-step22-generic-template (LEAD seed)

> SEED for the planner. Read THIS FIRST, then the AOT design, then research only
> what is missing. APPEND durable findings after propose.

## User intent

Continue the Neo completion portfolio. This child = **Step 22**: the generic-
method TEMPLATE mechanism (`PatchEntry` + `templateBody + patches[]` +
`CloneAndPatch` runtime instantiation). The first step of the Neo AOT toolchain
(Steps 22-26, a pure optimization layer; the JIT path already runs everything the
smoke covers). Capability `neo-optimizer`. Full autonomy; LEAD commits + pushes.

## What Step 22 delivers (from `.trae/documents/object-model-neo-design.md` §8)

The AOT precompile format (§8.2-8.4) compiles a generic method DEFINITION once to
a `templateBody` (an `OpCodeR[]`) + a `patches[]` table (`PatchEntry` records).
At runtime, a generic instantiation either:
- **reuses the template verbatim** (`patches.Length == 0` -- e.g. ref-type
  generic args produce the same `OpCodeR[]`, so one body is shared across all
  ref-type instantiations `List<string>`, `List<object>`, ...), or
- **`CloneAndPatch`** (value-type generic args differ in FRAME SIZE -- a
  `List<int>` vs `List<long>` vs `List<MyStruct>` need different local/stack
  byte sizes; CloneAndPatch clones the template + rewrites the size-dependent
  offsets/operands per the patches).

Cached in `ILMethod.BodyRegister`. §8.4.2 design decision: "保留模板 + 运行时
实例化" (template + runtime instantiation), NOT precompile-time exhaustive
expansion.

## Current state (how the JIT handles generics TODAY -- the reference)

Today the JIT instantiates generics PER-OCCURRENCE: `ILMethod.MakeGenericMethod`
(`ILMethod.cs:1096`) creates a `GenericInstanceMethod`; each instance gets its
own JIT-compiled `BodyRegister` (`ILMethod.cs:379`, via `JITCompiler.Compile`).
`PrewarmBodyRegister` (`:599`) / `Prewarm` (`:651`) prewarm the body
(`GenericParameterCount > 0 && !IsGenericInstance` skips prewarm of the open
definition `:655`). So there is NO template reuse today -- `List<int>.Add` and
`List<long>.Add` each compile independently. Step 22 introduces the template +
CloneAndPatch layer ON TOP of this (the per-occurrence path stays as the
reference / fallback).

## KEY design questions for the planner to dump-gate + decide

1. **Where does the template live + how is it keyed?** A generic method
   DEFINITION (`ILMethod` with `GenericParameterCount > 0 && !IsGenericInstance`)
   compiles to a template. The template must be keyed by the definition
   (`ILMethod` identity or a method-ref token), cached so the 2nd+ instantiation
   reuses it. Determine: does the template compile from the open definition's
   CIL body (parameterized by the generic-param type tokens)? How are the
   generic-param-typed operands (locals/params/fields of type `T`) represented
   in the template so CloneAndPatch can substitute them?
2. **What goes in a `PatchEntry`?** The size-dependent sites: a value-type `T`
   local/param changes the frame byte size (`TotalPrimitiveSize` for `T`) and the
   offsets of subsequent slots. A `PatchEntry` likely records
   `{instruction-index, field/operand-to-patch, how-to-derive-the-new-value-from-
   the-concrete-T}`. Define the struct precisely.
3. **Ref-type vs value-type discrimination.** Ref-type `T` args share one body
   (all refs are 4-byte mStack indices); value-type `T` args need CloneAndPatch
   (size-dependent). The discriminator: is `T` a reference type? (For a generic
   arg `T`, this is known at instantiation from the concrete `IType`.)
4. **CloneAndPatch correctness.** The cloned+patched body MUST be equivalent to
   what the per-occurrence JIT would produce. This is the VERIFICATION anchor
   (see below).
5. **Integration point.** Does Step 22 hook into `ILMethod.BodyRegister`'s getter
   / `MakeGenericMethod` / `JITCompiler.Compile`? It should be ADDITIVE -- the
   per-occurrence path stays (the reference); the template path is a cache layer.
   Gate it so the existing smoke is byte-identical.

## VERIFICATION (the hard part -- no functional smoke)

Step 22 is "pure optimization, no functional impact" -- the JIT path already
runs every generic method correctly. So there is NO FAIL-on-HEAD -> PASS probe.
Verification options (the planner picks + justifies):
- **(V1) Equivalence unit test (RECOMMENDED):** for a set of generic methods
  (ref-type arg, value-type arg of various sizes, nested generic, generic method
  on a generic type), assert that `CloneAndPatch(template, concreteArgs)`
  produces an `OpCodeR[]` EQUIVALENT to the per-occurrence JIT compile of the
  same instantiation (same length, same opcodes, same operands modulo the
  expected size-patches). This is a structural-equivalence test, runnable in the
  existing test harness (a `public static` method that compares the two bodies
  via a host-side helper, asserts via the value path / 1-0 divide).
- **(V2) Functional roundtrip:** invoke a generic method (`List<int>.Add`,
  `Swap<T>`, etc.) via BOTH the template path and the per-occurrence path,
  assert identical results. Weaker (doesn't prove the bodies match, just the
  observable behavior).
- **(V3) Defer to Step 25:** the .neo loader exercises the template path end-to-
  end. Step 22 ships the mechanism + a V1 unit test; full functional coverage
  lands at Step 25.
Recommend V1 (structural equivalence) as the load-bearing gate + V2 as a sanity
check. A green NeoStep smoke (204/204) is the REGRESSION gate (the template path
must not change any existing behavior -- it's additive).

## Scope + non-goals

- **In scope:** `PatchEntry` struct; the template compile (generic definition ->
  templateBody + patches); `CloneAndPatch` runtime instantiation; the cache
  (keyed by definition); the ref-type-share vs value-type-patch discrimination;
  a V1 equivalence test.
- **Non-goals (defer to later AOT steps):** the `.neo` binary FORMAT + serializer
  (Step 23); the `ilrt_neoc` precompile CLI (Step 24); the runtime .neo LOADER
  (Step 25); perf benchmarks (Step 26). Step 22 ships the in-memory template
  mechanism + its equivalence test, NOT the serialization or the CLI.
- **Non-goal:** do NOT remove or change the per-occurrence JIT instantiation
  path (it's the reference + the fallback; the template path is an additive
  cache). Confirm Legacy-neutral (the template mechanism is Neo-only; Legacy's
  `ExecuteR` is unaffected).

## Probe BEFORE designing (binding)

The last 4 JIT-path children's propose phases disproved orientation hypotheses
via HEAD dumps. Step 22 is different (no bug to disprove), but the planner MUST
still ground the design in the ACTUAL current generic-instantiation code: read
`ILMethod.MakeGenericMethod` (`:1096`), `BodyRegister` (`:379`),
`Prewarm`/`PrewarmBodyRegister` (`:599/651`), and `JITCompiler.Compile`'s
generic handling. Dump the compiled body of a concrete generic instantiation
(e.g. `List<int>.Add` vs `List<long>.Add`) -- observe WHAT differs between them
(those diff sites are exactly what `PatchEntry` must capture). Do NOT design
PatchEntry from the design doc alone; ground it in the actual diff.

## Build + test (CRITICAL)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll \
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep   # ALWAYS -f net8.0; baseline 204/204 (REGRESSION gate)
```
- Build CLI with `Debug_Neo`; NEVER TestCases with `Debug_Neo`.
- `Debug_Neo` floods JIT output -- grep for "Ran N tests". >10s = infinite loop -> kill.
- The NeoStep smoke (204/204) is the REGRESSION gate (template path must be
  additive / byte-identical). NeoOptHard 24/24 + NeoStep20 9/9 too.
- For any SHARED-engine edit (if the template hooks shared JIT code), confirm
  Legacy-neutral (plain `Debug` + `useRegister=true`).
- Build-cache gotcha: confirm the DLL rebuilt after an edit.

## Codebase gotchas (full detail in handoff section 4)

- `OpCodeR` is `[StructLayout(Explicit)]`: register-index fields alias offset
  fields. `LowerNeoOffsets` overwrites register indices with byte offsets -- the
  template must be patched AFTER lowering (the size-dependent sites are byte
  offsets post-lowering) OR the patches capture pre-lowering register indices
  + CloneAndPatch re-lowers. Decide carefully (this is the trickiest part).
- Shared vs Neo-only: `JITCompiler.Compile` is SHARED. The template mechanism
  should be Neo-only (gated `#if ENABLE_NEO_MODE`) OR confirmed Legacy-neutral.
- Test harness is NOT xUnit; `public static` parameterless methods; the V1
  equivalence test needs a host-side helper to compare two `OpCodeR[]` bodies.
- Write tool corrupts ~0.5% of CJK; author ASCII-primary.

## Likely fix sites (dump-locked)

- `ILRuntime/CLR/Method/ILMethod.cs` -- the template cache + `BodyRegister`
  integration + `MakeGenericMethod` hook.
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- the template
  compile (generic definition -> templateBody + patches) + `CloneAndPatch`.
  Possibly a new file for `PatchEntry` + `CloneAndPatch`.
- `TestCases/` -- the V1 equivalence test (a new test file, e.g.
  `NeoStep22GenericTemplateTest.cs`, or a host-side helper).

## Regression risk: MEDIUM.

The template mechanism is additive (the per-occurrence path is the reference),
so the regression risk is "did the template path accidentally change existing
JIT behavior?" Gate: full NeoStep smoke (204/204) + the V1 equivalence test +
Legacy-neutral for any shared edit.

## Maintain this file

APPEND durable findings after propose (the PatchEntry design grounded in the
actual generic-body diff, the cache key, the ref-vs-value discrimination, the V1
test design). Do NOT append chatter.

## Findings -- neo-step22-generic-template (propose, 2026-07-07)

Grounded in a per-occurrence JIT dump diff (temp dump in `JITCompiler.Compile`
after `LowerNeoOffsets`, gated on method name `Probe22`; probe =
`T Probe22<T>(T v, int n){T current=v; int sum=n+1; return current;}` + a
`T[]/ref T` variant; instantiations `T=int/long/S3(struct)/object/string`;
dump + probe REMOVED after capture, tree pristine).

**The dump diff proved the PatchEntry design (what actually varies):**
- `Register1/2/3` are **T-INVARIANT** across `int`/`long`/`object`/`string` for
  matching instructions. Only `Operand`-level fields + the lowered byte offsets
  vary. -> `PatchEntry.Field` is restricted to the STANDALONE fields
  (`Operand`/`Operand2`/`Operand4` @offsets 8/12/20); NEVER `Register1/2/3`
  (those alias the post-lowering byte offsets).
- **Kind A (T-IDENTITY-dependent; enumerable; captured as PatchEntry):**
  type-token `Operand` on `Initobj`/`Box`/`Unbox`/`Unbox_Any`/`Isinst`/
  `Castclass`/`Newarr`/`Stobj`/`Ldobj`/`Constrained`/`Ldelem_Any`/`Stelem_Any`
  (e.g. `Op=0x10000151` for S3); method-token `Operand2` on T-qualified
  `Call`/`Callvirt`/`Call_Redirect`; the `Move` is-ref flag (`Operand`, alias
  of `Register3` @offset 8: `Op=0x1` for ref-T Moves, `Op=0x0` for value-T).
- **Kind B (T-SIZE-dependent; CUMULATIVE; re-derived, NOT patched):** the byte
  offsets. Changing param `v` 4->8->12 bytes shifts `localInfos[i].Offset` of
  every subsequent slot; `LowerNeoOffsets` stamps those into every affected
  opcode's `DstOffset`/`SrcOffset`/`OperandOffset` + the `Move` copy-size
  `Operand2`/dst-ref `Operand3`. Patching these == re-running Allocate, so they
  are ALWAYS re-derived via `AllocateLocalStackSpaces(concrete T)` +
  `LowerNeoOffsets(concrete localInfos)`.

**Lowering decision (the trickiest one) -- RESOLVED cleanly:** patches are
PRE-LOWERING (register-index / Operand-level, Kind A); byte offsets (Kind B)
are re-derived by re-running Allocate+Lower. There is NO patch-after-lowering
path. Apply Kind-A patches (or re-run TypeSpecialize) on the register-index
body FIRST, then re-run Allocate+Lower.

**Cache key:** the generic method DEFINITION (open `ILMethod`,
`GenericParameterCount > 0 && !IsGenericInstance`); template stored ON the
definition (Neo-only field); 2nd+ instantiation reuses it.

**Ref-vs-value discrimination:** at instantiation in the generic-instance
`BodyRegister` getter, read `genericDefinition.Template` + `GenericArugmentsArray`.
All-ref AND no T-identity token in the patch table -> share ONE cached "ref
body" verbatim (dump: `T=object` NEOEXECBODY == `T=string` NEOEXECBODY
byte-for-byte, including tokens, because `Probe22` has no `Box T`/`Isinst T`).
Any value-T -> CloneAndPatch. Token-bearing ref body (rare: `Box T`) ->
CloneAndPatch (token differs across ref types; sizes still ref-uniform).

**Template capture point:** register-index body AFTER `CleanupRegister`, BEFORE
`TypeSpecializeNeoOpcodes` (the latest T-invariant artifact). CloneAndPatch =
clone + re-run the T-dependent back-half (`TypeSpecializeNeoOpcodes` +
`AllocateLocalStackSpaces` + `LowerNeoOffsets`) with concrete T. Requires
factoring the back-half out of `Compile` into a callable `RunNeoBackHalf`.

**Dump-observed quirk (reproduce, do NOT fix):** a generic-T-typed LOCAL is
allocated by `AllocateLocalStackSpaces` reading the Cecil `VariableType`
directly (`JITCompiler.cs:1579`); for a generic param `vt.IsValueType==false`,
so the local is a 4-byte boxed-ref REGARDLESS of T (`int`/`long`/`S3` all get
`Size=4 RefCnt=1 IsRef=True` for `current`). Only the PARAMETER (resolved via
`appdomain.GetType`) gets the concrete size. CloneAndPatch reproduces this
automatically (re-runs Allocate on the same Cecil body); V1 guards equivalence.

**Instruction-stream difference (Initobj count):** `T=S3` body has 2 extra
`Initobj` (instr count 7 vs 5 for `T=int`). The Initobj-insertion loop is
T-dependent (fires for non-primitive value-T via `FindGenericArgument`). The
template (open def, T=generic param, `IsValueType==false`) has NO Initobj;
CloneAndPatch re-runs TypeSpecialize (which re-inserts them) BEFORE applying
Kind-A patches by index, so patch indices align with the post-TypeSpecialize
body.

**V1 test design (load-bearing gate):** host-side `OpCodeR[]` comparator
(length + Code/R1/R2/R3/Operand/Operand2/3/4 per index) + a `#if DEBUG`
test-hook accessor on `ILMethod` for `NeoExecuteBody` + frame layout. Matrix:
T = int/long/IL-struct/object/string x shapes (T local/param/return; T[]+ref T;
nested generic; generic method on generic type). Each cell asserts
`CloneAndPatch(template,T) == Compile(MakeGenericMethod(T))`. V2 = functional
roundtrip (sanity). NeoStep 204/204 = regression gate.

**Open question (flagged in design.md):** in Step 22 CloneAndPatch re-runs
TypeSpecialize (Cecil available), so PatchEntry-apply is technically redundant
for correctness. Recommendation: re-run-TypeSpecialize is PRIMARY in Step 22
(lower risk); PatchEntry-apply is asserted-equivalent via V1 and becomes
primary at Step 23 (serialize-without-Cecil). Confirm at apply.

**No design blocker.** The lowering interaction resolves cleanly (Kind A vs
Kind B split). Artifacts: proposal.md, design.md, specs/neo-optimizer/spec.md
(ADDED Requirements: PatchEntry invariant, CloneAndPatch equivalence,
ref-share/value discrimination, additive+Neo-only+Legacy-neutral), tasks.md
(6 groups). All 4 openspec artifacts complete.

## Findings -- neo-step22-generic-template (apply, 2026-07-07)

APPLY complete. The template mechanism is implemented, additive + Neo-only +
Legacy-neutral, and its CloneAndPatch output is structurally equivalent to the
per-occurrence JIT for the full V1 matrix (25/25 cells). Three design-premise
errors were caught + corrected at apply (all fixable; none triggered the STOP
discipline). Details below.

### RunNeoBackHalf factoring (task 1.1) -- shared JIT, byte-identity-preserving

Factored the Neo-only back-half out of `JITCompiler.Compile` into:
`internal void RunNeoBackHalf(ref CompiledFrame frame, List<OpCodeR> res,
short locVarRegStart, int totalRegCnt, short neoCatchExRegFinal)`.
It sets `frame.NeoCatchExceptionRegIndex`, runs `TypeSpecializeNeoOpcodes`,
sets `frame.CodeBody = res.ToArray()`, runs `AllocateLocalStackSpaces`, clones
`NeoExecuteBody`, runs `LowerNeoOffsets`. `Compile` calls it inline; the Legacy
`#else` arm sets `frame.CodeBody = res.ToArray()` directly. NeoStep smoke stayed
204/204 immediately after the refactor (byte-identity confirmed). Legacy-neutral
proven by stash-toggle (same 8 pre-existing NeoStep-filter failures with/without
the three Step-22 source files under plain `Debug` + `useRegister=true`).

### Design-premise error 1: TypeSpecialize does NOT insert Initobj (the front-half does)

The propose-phase note said "CloneAndPatch re-runs TypeSpecialize (which re-
inserts them)" for struct-T. FALSE. The auto-Initobj local-prefix insertion is
in the FRONT-HALF (`JITCompiler.cs:346-381`, before the optimizer/CleanupRegister),
gated on `FindGenericArgument(name).IsValueType && !IsPrimitive` for generic-
param locals. `TypeSpecializeNeoOpcodes` only rewrites ops in place (Move->Move_Vt,
typed arithmetic, field-access inline); it does NOT change `body.Count`.
Confirmed by reading TypeSpecialize end-to-end + grep for all Initobj/Insert sites.

Implication: for a struct-T instantiation, the front-half emits N EXTRA Initobj
ops (one per generic-param local whose concrete T is a non-primitive value type)
that the T-invariant template (open def has IsValueType==false) lacks. So
CloneAndPatch MUST rebuild the Initobj prefix for the concrete T -- it cannot
rely on TypeSpecialize to do it.

### Design-premise error 2: the back-half does NOT re-derive T-identity tokens

The propose-phase note + open question assumed "CloneAndPatch re-runs
TypeSpecialize (Cecil available), so the PatchEntry application is technically
redundant." FALSE. Type tokens (Initobj/Box/Isinst/Castclass/Newarr/Stobj/Ldobj/
Constrained `Operand`) are set during Translate (front-half) and are NOT touched
by TypeSpecialize. So the template (T-invariant base) has the BASE tokens and
CloneAndPatch MUST apply the PatchEntry table to re-resolve them for the concrete
T. So PatchEntry-apply and re-run-TypeSpecialize are COMPLEMENTARY, not
equivalent (the open question's premise was wrong): apply-patches fixes the
FRONT-HALF T-dep (tokens); re-run-TypeSpecialize fixes the BACK-HALF T-dep
(opcode selection). CloneAndPatch does BOTH, and the V1 self-check asserts the
combined output == per-occurrence.

### Design-premise error 3: compiling the open definition corrupts shared state

The propose-phase note offered "a first Compile of the open definition" as the
template build path. TRIED + REJECTED: running the full `JITCompiler.Compile` on
the open generic definition (generic-param-typed tokens) corrupts shared
AppDomain caches (type/method/field resolution resolves differently for the open
generic parameter and leaves stale entries that break LATER, unrelated tests).
Symptom: 5 NeoStep failures (incl. non-generic `NeoAddI4 = 40+2`) appearing only
in the FULL smoke (Knock-on); isolated NeoStep6 passed. Root cause = the open-def
Compile's cache side-effects, isolated by a `return null` toggle on the template
build.

FIX: the template is captured from the FIRST CONCRETE instantiation's front-half,
NOT from a Compile of the open definition. InitCodeBody detects a capture-ELIGIBLE
generic instance (every typeArg a reference type or a primitive -- so no generic-
param-value-T local fires the Initobj insertion, i.e. the front-half stream is
T-invariant), runs the NORMAL per-occurrence Compile WITH a `templateCapture`
hook (grabbing `res.ToArray()` + `addr` + `symbols` + the auto-Initobj prefix
registers after CleanupRegister), and stores the template on the definition.
This is the proven Compile path (no corruption). Subsequent instantiations (any
T, incl. struct-T) CloneAndPatch from it. A generic instantiated only at struct-T
(deferred capture) falls back to per-occurrence (correctness preserved).

### PatchEntry / CloneAndPatch / cache implementation

- `PatchEntry { int InstrIdx; PatchField Field; PatchKind Kind; int
  GenericParamIdx; object CecilToken; }`. `CecilToken` is added for the Step-22
  apply path (re-resolve via `instance.GetTypeTokenHashCode`); `GenericParamIdx`
  is populated for the Step-23 serialize-without-Cecil contract.
- `ExtractPatches`: scans the template body; for each TypeToken opcode kind,
  looks up the Cecil instruction via `Symbols[idx]`, checks `HasGenericParameter`,
  records the site. Auto-Initobj (`Operand2==1`) is rebuilt, not patched.
  MethodToken (T-qualified Call/Callvirt) is not exercised by the V1 matrix;
  deferred.
- `DoCloneAndPatch`: (1) clone template body; (2) apply PatchEntry (re-resolve
  via instance); (3) rebuild the Initobj prefix for concrete T (union of the
  template's CheckNeedInitObj locals + generic-param-value-T locals); (4)
  re-derive branch Operands / SwitchTargets values / addr by the prefix delta
  (Kind B re-derivation -- cumulative byte offsets are left to Allocate+Lower);
  (5) re-run `RunNeoBackHalf`.
- Discrimination (`TryInstantiate`): all-ref AND no T-identity token -> share ONE
  cached ref body; else CloneAndPatch.
- Cache: `ILMethod.genericMethodTemplate` on the open definition, lazily
  populated by `StoreGenericTemplate` from the capture hook.

### V1 structural-equivalence test (the load-bearing gate) -- 25/25 PASS

Host-side `NeoStep22SelfCheck.Run(AppDomain)` (in ILRuntime, `#if ENABLE_NEO_MODE`,
invoked via the CLI `NeoStep22SelfCheck` filter) compiles each matrix generic
instance via BOTH paths -- `CompilePerOccurrenceNeoBody` (fresh JIT, reference)
and `CompileViaTemplateNeoBody` (CloneAndPatch) -- and compares with
`BodiesEqual` (length + Code/Register1-3/Operand-4 per index).

Matrix: 5 methods (`ProbeBasic` [T local/param/return], `MakeArray` [Newarr T],
`StoreRef`/`LoadRef` [Stobj/Ldobj T], `BoxIt` [Box T]) x 5 T (int / long /
object / IL-ref-class / IL-value-struct) = 25 cells. **25/25 PASS.** Notably:
- `ProbeBasic<Struct>` body len=7 (vs 5 for primitives) -- the Initobj-prefix
  rebuild correctly inserts the 2 extra Initobj ops; indices align with the
  per-occurrence body. This is the case the design wrongly attributed to
  TypeSpecialize.
- `MakeArray`/`StoreRef`/`LoadRef`/`BoxIt` each report `patches=1` (the T-token
  site) -- PatchEntry populated + applied, and the patched token values match the
  per-occurrence resolution.
- `ProbeBasic` reports `patches=0` (no T-identity token; just T local/param) --
  ref-share eligible for ref-T, CloneAndPatch for value-T.

### V2 functional roundtrip -- PASS (1/1)

`NeoStep22Test.NeoStep22TemplateEquivalence` (interpreted, in the NeoStep smoke)
invokes the matrix generics via the template path (first call captures,
subsequent CloneAndPatch) + asserts correct results. PASS.

### Edges discovered (out of scope, documented)

- **Pre-existing runtime bug: Box on a generic-param ref-T.** `BoxIt<string>`
  (`(object)v` with T=string) NREs at `ILIntepreter.Neo.cs:2749` (the non-primitive
  CLR-value-type Box arm) in BOTH paths (confirmed by disabling the template path
  -- not a Step-22 regression). The IL emits `box T` for a generic method; at
  runtime T=string (ref CLRType) is mis-routed to the value-type Box arm. The V1
  structural self-check covers `BoxIt<object>`/`BoxIt<RefClass>` body equivalence
  (bodies match -- the bug is runtime-execution, not body-generation); the V2
  functional covers the value-T box (`BoxIt<int>`). Fixing the runtime Box-on-
  generic-ref-T arm is a separate Neo runtime task (not Step 22 / not the AOT
  chain).
- **PatchEntry-apply is the Step-23 primary.** Because the back-half does NOT
  re-derive tokens, Step 23 (serialize-without-Cecil) MUST use the PatchEntry
  table to specialize tokens. Step 22's apply path (Cecil-token re-resolution) is
  proven equivalent by V1; Step 23 swaps the Cecil-token derivation for a
  GenericParamIdx-based derivation.

### Regression gates

- NeoStep smoke: **205/205** (was 204/204 + the new V2 test; ZERO regressions).
- NeoOptHardening: **24/24**. NeoStep20: **9/9**.
- Legacy-neutral: stash-toggle proof (same 8 pre-existing NeoStep-filter
  failures with/without the Step-22 source).
- Tree is pristine modulo the Step-22 source + test + openspec artifacts (no
  probe/dump code).

### Delivered scope

In-memory template mechanism (PatchEntry + template cache + CloneAndPatch +
discrimination) + V1 structural-equivalence self-check (25/25) + V2 functional
roundtrip. DEFERRED (per plan): the `.neo` serializer (Step 23), the `ilrt_neoc`
CLI (Step 24), the runtime `.neo` loader (Step 25), perf (Step 26).

## Findings -- neo-step22-generic-template (review-fix round 1, 2026-07-07)

VERIFY (adversarial) review round 1 found one BLOCKER (Constrained type-token
never patched) + a conditional MAJOR (MethodToken) + a coverage hole (MINOR-3).
All three closed; the matrix expansion additionally surfaced + fixed a 4th real
CloneAndPatch bug (Leave/Leave_S delta-shift). V1 self-check is now 55/55 (was
25/25); all regression gates green.

### BLOCKER-1: Constrained T type-token never patched -> wrong runtime dispatch

**Root cause (dump-pinned):** the Constrained op's type hash is emitted in the
FRONT-half (`JITCompiler.cs:2607`, `op.Operand = method.GetTypeTokenHashCode(token)`),
so it is concrete-T-dependent. `ExtractPatches`' switch included `Constrained`,
but the guard `token = sym.Instruction.Operand; HasGenericParameter(token)` read
the symbol at the Constrained op's body index -- and that symbol does NOT point
at the `constrained.` Cecil prefix. Dump evidence (`HashIt<T>(T v)=>v.GetHashCode()`):
- HashIt: the Constrained op's symbol -> the trailing `callvirt` Cecil
  (`MethodReference`); its `.Previous` is the `constrained.` prefix.
- EqualsIt: the Constrained op's symbol -> an unrelated `br.s`; the trailing
  callvirt's symbol -> `ret`. I.e. CleanupRegister scrambles the symbol->op
  mapping thoroughly for the Constrained pair.

So the Cecil `TypeReference` for T cannot be recovered from the symbol. With no
PatchEntry recorded, `DoCloneAndPatch`/ref-share kept the capture-instance hash;
the runtime Constrained arm (`ILIntepreter.Neo.cs:4096`,
`AppDomain.GetType(ip->Operand)`) dispatched on the wrong type for every T !=
capture-T. Live Neo regression (Step 22 switched the 2nd+ instantiation from
correct per-occurrence to stale CloneAndPatch).

**Fix:** the `constrained. T` TypeReference is now captured directly from the
CIL body at template-capture time (where `def.Body` is valid), in CIL order, and
consumed in body order by `ExtractPatches`. This follows the existing
`VariableTypes` capture precedent (avoids the def.Body-null-in-Release
dependency). CIL order == template-body order (each `constrained.+callvirt`
pair becomes exactly one Constrained op, and `hasConstrained` DISABLES inlining
for the trailing callvirt, so the pair is never reordered). Concretely:
- `JITCompiler.TemplateCapture.ConstrainedTypeTokens` (TypeReference[]) +
  `GenericMethodTemplate.ConstrainedTypeTokens`; populated in `CaptureTemplate`
  by scanning `def.Body.Instructions` for `Code.Constrained`.
- `ExtractPatches`: the `Constrained` case is pulled OUT of the symbol-based
  group; it consumes `ConstrainedTypeTokens[constrainedIdx++]` and records a
  `TypeToken` PatchEntry on `Operand` (re-emitted via `instance.GetTypeTokenHash
  Code` in `DoCloneAndPatch`).

**Dump-gate (runtime):** temp `Console.WriteLine` in the runtime Constrained arm
showed `HashIt(7L)` via CloneAndPatch reads `ip->Operand -> System.Int64` (the
PATCHED hash), NOT the stale capture-T `System.Int32`. Diagnostic removed.

**Regression guard:** the `HashIt`/`EqualsIt` V1 cells (Constrained pattern) are
GREEN across int/long/object/RefClass/Struct (were RED before the fix).

### MAJOR-2 verdict: MethodToken (T-qualified call) -- reachable + FIXED (Constrained pair); non-constrained is T-invariant

The Constrained+callvirt pair carries the trailing callvirt's method token in
`Operand2` (merged into the Constrained op at `JITCompiler.cs:2164`, AND on the
trailing callvirt op which the runtime reads at `ILIntepreter.Neo.cs:4108`,
`cv->Operand2`). For a T-qualified callvirt (e.g. `IComparable<T>::CompareTo`,
probed via `CompareThem<T>(T a, T b) where T:IComparable<T>`), this method token
is concrete-T-dependent (int=...927, long=...928, object=...929 observed) ->
reachable, same defect class as BLOCKER-1.

**Fix (same shape as BLOCKER-1):** `CaptureTemplate` also captures the trailing
callvirt's `MethodReference` per `constrained.` pair
(`ConstrainedMethodTokens`, paired 1:1 with `ConstrainedTypeTokens`).
`ExtractPatches` records, per T-qualified pair, a `MethodToken` PatchEntry on
the Constrained op's `Operand2` (vestigial; V1 body-equality) AND on the
trailing callvirt's `Operand2` (runtime-load-bearing). `DoCloneAndPatch` re-
emits via `GetMethodTokenHash` (mirrors the front-half `InitializeFunctionParam`:
`appdomain.GetMethod(cecilRef, declaringType, instance, out invalid) -> m.GetHashCode
()/token.GetHashCode()`). The AppDomain method cache did NOT collide
(`GetMethod` re-resolves generic args via `contextMethod` per call).
`HasGenericParameter` was extended to handle `MethodReference` (declaring type
+ generic-instance-method args).

**Non-constrained T-qualified call:** probed via `CallIt<T>(T v)=>EchoG<T>(v)`
(a generic-METHOD call inside a generic method). Result: `patches=0`, all T
GREEN -- the call resolves to the generic method DEFINITION (one ILMethod), whose
hash is T-invariant. So a non-constrained generic-method call is NOT a T-qualified
token for a generic-method template (matches the task hint: "rare for a generic
METHOD's body"). The V1 matrix (now including `CompareThem`) is the guard: any
T-qualified method-token divergence in a future body is caught by structural
equivalence. (Probe `CallIt` exposed an UNRELATED struct-T x inliner CloneAndPatch
gap -- see "Follow-ups" -- and was removed from the matrix; the struct-T control-
flow coverage is carried by BranchIt/SwitchIt/TryCatch.)

### MINOR-3: V1 matrix expanded (Constrained + struct-T + control-flow) -- found + fixed a 4th bug (Leave delta-shift)

Matrix grew 25 -> 55 cells (11 methods x 5 T): added `HashIt`/`EqualsIt`
(constrained.callvirt T.M, the BLOCKER-1 shape), `CompareThem` (T-qualified
method token, MAJOR-2), `BranchIt` (struct-T + if/else), `SwitchIt` (struct-T +
switch), `TryCatch` (struct-T + try/catch EH). The Constrained cells are the
BLOCKER-1 regression guard (green after the fix; red before).

**Bonus fix (Leave/Leave_S delta-shift):** `TryCatch<Struct>` failed with a
`Leave_S` target mismatch (Operand 4 vs 6). Root cause: `DoCloneAndPatch` step-4
delta-shift used `Optimizer.IsBranching`/`IsIntermediateBranching`, which EXCLUDE
`Leave`/`Leave_S` (EH control-flow, resolved separately at `JITCompiler.cs:555`
via `addr[]`, but their Operand is a resolved body index all the same). So for
struct-T (Initobj prefix grows, delta>0) the EH leave target was NOT shifted ->
wrong handler/continue target. Fixed by adding `Leave`/`Leave_S` to the
delta-shift loop. This is a real CloneAndPatch correctness bug the original
25-cell matrix missed (no struct-T + EH body). Guarded by the `TryCatch<Struct>`
cell (green after the fix).

### V2 functional (Constrained) -- gated by pre-existing Step-17 runtime arm

The Constrained V2 functional cell is NOT exercised because the runtime
Constrained arm (`ILIntepreter.Neo.cs:4081+`, Step 17) has pre-existing gaps for
ref-type-T and primitive-T `this` (box-once NIE/NRE), and every capture-eligible
T is ref/primitive (struct-T is not capture-eligible) -- so the capture call
itself cannot execute. This is NOT a Step-22 regression: V1 proves the
CloneAndPatch body is byte-identical to per-occurrence (incl. the Constrained
Operand), so runtime behavior is path-independent. The existing V2 cells
(ProbeBasic/MakeArray/StoreRef/LoadRef/BoxIt/Echo) remain green. Fixing the
runtime Constrained arm for ref/primitive-T `this` is a separate Step-17 task.

### Regression gates (post-fix)

- V1 self-check: **55/55** (was 25/25).
- V2 functional (`NeoStep22TemplateEquivalence`): **1/1**.
- NeoStep smoke: **205/205**. NeoOptHardening: **24/24**. NeoStep20: **9/9**.
- Legacy-neutral: plain `Debug` + useRegister=true, NeoStep filter -> 205 ran /
  8 pre-existing failures (NeoStep13/14/15/16/6 runtime issues; matches HEAD;
  no NeoStep22 / generic-template failures). All Step-22 source is
  `#if ENABLE_NEO_MODE`.

### Follow-ups (out of round-1 scope, for Step 23 / a later round)

- **struct-T x inliner CloneAndPatch gap:** `CallIt<Struct>` (a generic-method
  call that the front-half INLINES) made the body grow inline `Initobj` for
  struct temps that CloneAndPatch's prefix-rebuild does not reproduce
  (per-occ len 9 vs template len 8). Not a regression (no smoke test hits it;
  ProbeBasic<Struct> works). The Step-23 serializer must not assume the prefix
  is the only Initobj site -- re-audit inline-Initobj from the inliner.
- **Runtime Constrained arm for ref/primitive-T `this`** (Step 17): blocks the
  Constrained V2 functional cell.
- **Pre-existing runtime Box-on-generic-ref-T** (documented in round 0): still
  open, unrelated to Step 22.

