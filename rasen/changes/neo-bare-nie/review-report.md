# Review Report — neo-bare-nie

**Reviewer:** author != verifier gate (autonomous LEAD Tier-A, dispatched report-only).
**Change:** `neo-bare-nie` (child 5 of the `neo-overhaul` portfolio).
**Verdict:** **APPROVE-WITH-FINDINGS** (see rationale at the end).

---

## TL;DR

The change is correct, narrowly scoped, and fully additive. The SIZING mapping
(the key risk — a wrong size silently corrupts call-param slots and Stobj/Ldobj
copies) is **verified correct**. All gates pass: NeoStep smoke **318/0**
(316 + 2 new probes), stash-toggle **2/2 FAIL without the fix**, full-smoke
bare-default NIE dropped **~38 -> 6** (all 6 = the deferred framework
`System.Type.GetEnumValues()` NIE, correctly out of scope), and the change is
**Legacy-neutral**. One **Minor** latent finding (a pre-existing companion
ref-count gap in the Stobj/Ldobj arm, now reachable) is recorded for the LEAD;
it does not block this change.

---

## Standards axis

### S1. SIZING correctness — the key check. CORRECT. (no finding)

`AppDomain.GetPrimitiveSize(IType)` (AppDomain.cs:2252, `#if ENABLE_NEO_MODE`
2232-2329) gained one branch before the residual throw (AppDomain.cs:2306-2308):

```csharp
else if (fieldType.IsValueType && fieldType.TypeForCLR != null
         && !(fieldType is CLR.TypeSystem.ILType))
    return Optimizer.GetNeoValueTypeManagedSize(fieldType.TypeForCLR);
```

**Helper verified** — `Optimizer.GetNeoValueTypeManagedSize(Type t)`
(Optimizer.Neo.cs:1625-1647): `null -> 0`; if `type.IsEnum` maps to
`Enum.GetUnderlyingType(type)`; then `Unsafe.SizeOf<T>()` (the GC-reference-aware
managed size, NOT `Marshal.SizeOf`); falls back to `Marshal.SizeOf` only if the
`Unsafe.SizeOf` reflection fails. Cached per-Type via `s_neoVtSizeCache`. Never
throws.

- **Enum** (e.g. `BindingFlags`, int-backed): `IsValueType`=true,
  `TypeForCLR`!=null, not an `ILType` -> captured. Size = `Enum.GetUnderlyingType`
  then `Unsafe.SizeOf<int>` = **4**. Correct.
- **CLR struct** (e.g. `TestVector3`, 3 floats): same guard -> captured. Size =
  `Unsafe.SizeOf<TestVector3>` = **12**. Correct (includes layout/padding).
- **IL value type**: `fieldType is ILType` true -> **excluded** (falls to the
  tagged throw). This is correct, not an oversight: callers size IL types BEFORE
  reaching this helper (`ilType != null ? ilType.TotalPrimitiveSize : GetPrimitiveSize(t)`),
  so an ILType reaching the residual is a caller bug; sizing an IL wrapper via
  `Unsafe.SizeOf` would be wrong. IL enums are likewise sized by callers via
  `TotalPrimitiveSize`, so excluding ILType does not regress them.

**Byte-consistency verified** (the thing that prevents silent frame corruption):

- The **enum feeder** is `Optimizer.AllocateNeoCallParamSlot` (Optimizer.Neo.cs:1565-1569):
  `if (type.IsPrimitive || (type.TypeForCLR != null && type.TypeForCLR.IsEnum))
  slot.Size = domain.GetPrimitiveSize(type);`. So the call-param slot for an enum
  is now sized by `GetNeoValueTypeManagedSize` — the SAME helper the struct branch
  of the same allocator uses (Optimizer.Neo.cs:1591) and the SAME helper all
  readers use (`ReadNeoValueType`/`WriteNeoValueType`, `CLRMethod.Invoke`, the
  autogen `AppendArgumentCodeNeo` path — see ILIntepreter.Neo.cs:142-150). Single
  size source -> reader/callee layouts stay byte-identical. No call-param drift.
- The **CLR-struct feeder** is the `ExecuteNeo` `Stobj`/`Ldobj` arms
  (ILIntepreter.Neo.cs:5155 & 5258): `int primSize = ilType != null ?
  ilType.TotalPrimitiveSize : AppDomain.GetPrimitiveSize(t);` then
  `Unsafe.CopyBlock(..., (uint)primSize)`. For a CLR struct (`ilType == null`)
  the copy now moves exactly the struct's flat managed size (12 for TestVector3).
  Correct byte count. No over/under-copy.

### S2. The 6 guard-tags are message-only. CONFIRMED. (no finding)

Diff-confirmed: every one of the 6 sites is `throw new NotImplementedException();`
-> `throw new NotImplementedException($"... [neo-bare-nie]");`. No conditional,
no early return, no swallowed branch — each is the body of an existing `else`
that still throws. Control flow is byte-identical; only the message string
changed.

- AppDomain.cs:1717 (`GetType` token shape), :2162 (`GetMethod` method-ref shape) — shared.
- JITCompiler.cs:2915 (Ldtoken token shape) — shared.
- JITCompiler.cs:3011 (`GetLdfldCodeForType`), :3098 (`GetStfldCodeForType`) — Neo-gated.

Full-smoke evidence the guards are inert in the current suite: tagged-`[neo-bare-nie]`
count in the captured full smoke = **0** (none fired). The residual GetPrimitiveSize
throw is also 0 (the sizing branch handles everything that reaches it).

### S3. Additive. CONFIRMED. (no finding)

The helper threw for ALL non-primitives before this change. The new branch can
only convert a throw into a correct size — it cannot change the size returned for
any primitive singleton (those are matched by earlier branches, unchanged).
Working callers passed only primitives and still do; previously-broken callers
(enum call-params, CLR-struct stobj/ldobj) now get a correct size. No working
path changes shape.

### F1. [Minor] Stobj/Ldobj CLR-struct ref-count gap is now reachable (was a throw)

The Stobj/Ldobj arm computes `refCount = ilType != null ? ilType.TotalReferenceCount : 0`
(ILIntepreter.Neo.cs:5156 & 5259). For a CLR struct (`ilType == null`) refCount is
**always 0**, and the arm never consults the `ValueTypeBinder` — unlike the
call-param allocator `AllocateNeoCallParamSlot`, which DOES use
`crt.GetValueTypeSize(out _, out int managedCount)` for binder structs
(Optimizer.Neo.cs:1592-1597).

- **Before this change:** a CLR struct reaching this arm threw the bare NIE at
  `GetPrimitiveSize` (fail-stop) — the ref-count gap was unreachable.
- **After this change:** the SIZE is now correct, so the copy proceeds — but
  `refCount` stays 0. For a pure-primitive CLR struct (the probe's `TestVector3`)
  this is exactly right. For a CLR struct WITH managed reference fields, the
  primitive `CopyBlock` would run but the ref-region copy (ILIntepreter.Neo.cs:5169-5188
  / 5218-5247) is skipped, leaving the destination's ref slots stale/untracked —
  a potential missed GC root.

Why this is Minor, not Major/Blocker: (a) no working path is broken — the path
threw before; (b) no test exercises a ref-field CLR struct via raw stobj/ldobj
(NeoStep 318/0); (c) the probe correctly uses a pure-primitive struct; (d) the
`refCount` line is unchanged by this PR (pre-existing inconsistency); (e) the
design explicitly scopes these arms to blittable-or-binder structs. The exposure
requires an uncommon IL pattern (raw `stobj`/`ldobj` on a CLR struct that has
managed ref fields). Recorded for the LEAD rather than blocking.

**Recommended follow-up (not required for this change):** in the Stobj/Ldobj arm,
either (a) compute the CLR-struct ref count from the ValueTypeBinder (parity with
`AllocateNeoCallParamSlot:1592`), or (b) until that is wired, guard a
CLR-struct-with-ref-fields copy with a tagged NIE (parity with the Step-13b
call-param guard) so it fails loud instead of copying with refCount=0.

### F2. [Trivial] TC1 probe asserts non-empty, not exact enum round-trip

TC1 (`NeoStepBareNie_TC1_EnumCallArg`) asserts `ms.Length > 0` on
`typeof(int).GetMethods(BindingFlags.Public | BindingFlags.Instance)`. This proves
the method JIT-compiled and the call executed (the pre-fix failure mode is a JIT
throw, which the probe does catch loudly), and it also catches a zeroed arg
(`GetMethods(0)` returns empty). It would not catch a corruption of the enum to a
different non-zero flag. Acceptable under the child-1/child-2 FAULT discipline
(the failure mode is a throw, not a wrong value), noted only for completeness.

## Spec axis

Against `proposal.md` / `tasks.md` / `specs/neo-value-types/spec.md`:

- **IMPLEMENT (site 1, tasks 1.1-1.3):** DONE and correct. Branch + tagged
  residual throw present; helper reachability verified (CLI build 0 errors => the
  `Optimizer` reference resolves).
- **GUARD (sites 2-6, tasks 2.1-2.5):** DONE and message-only. All 5 tagged.
- **DEFER (site 7, framework Enum.GetValues):** correctly deferred; the spec's
  deferred scenario matches the runtime (full-smoke shows the 6 residual bare-NIE
  hits are exactly `System.Type.GetEnumValues()` via `System_Enum_Binding.GetValues_0_Neo:77`).
- **PROBES (tasks 3.1-3.3):** DONE. TC1 enum call-arg, TC2 CLR-struct
  stobj/ldobj. Stash-toggle verified (task 3.3) — see Evidence.
- **VERIFY (tasks 4.1-4.4):** DONE. Build 0 errors, NeoStep 318/0, Legacy-neutral
  baseline holds, full-smoke bare-NIE down to the deferred framework NIE.

No spec requirement is missing or partial; no scope creep (all 5 touched sites +
2 probes map 1:1 to the proposal's site list; no opcode emission or object-model
change).

## Scope check

CLEAN. Intent: locate the Neo-reachable bare-NIE sites; implement the dominant
one (GetPrimitiveSize), guard the rest, defer the framework one. Delivered:
exactly that — one real fix, 6 message tags, 2 probes, 1 documented deferral.

---

## Evidence (re-verified independently this session)

**Build (always `-f net8.0`):**
- `dotnet build ILRuntimeTestCLI -c Debug_Neo --no-incremental` -> **0 errors** (270 pre-existing warnings).
- `dotnet build TestCases -c Debug` -> **0 errors** (TestCases NOT built with Debug_Neo).

**NeoStep smoke (fix in):** `... true NeoStep` -> **Ran 318 tests, 0 failed**
(316 prior + 2 new probes).

**Stash-toggle (fix out, probes + tags kept):** temporarily set the sizing
branch's guard to `false && ...`, rebuilt CLI, ran `... true NeoStepBareNie` ->
**Ran 2 tests, 2 failed**. The fault is the (now-tagged) `GetPrimitiveSize` NIE,
for TC2 via the `Ldobj` arm (`ILIntepreter.Neo.cs:5258`); TC1 faults too (2/2).
Restored the guard (verified no toggle marker remains via `git diff`), rebuilt ->
**318/0** restored. This proves the sizing branch is load-bearing for BOTH probes.
(Note: because the tags ship in the same change, the toggled throw carries the
`[neo-bare-nie]` tag, not the bare default message; the bare default message only
exists pre-change. The fault/no-fault result is what the toggle proves.)

**Full-smoke spot check (fix in, unfiltered):** the full run segfaulted
mid-stream (exit 139 — the pre-existing unrelated crash noted in the design); the
5.0 MB pre-crash log was grepped with `-a` (NUL bytes):
- bare default-message NIE count = **6** (down from ~38).
- All 6 are the SAME deferred framework NIE: `System.Type.GetEnumValues()` via
  `System_Enum_Binding.GetValues_0_Neo:77` (3 distinct test sites, each printed
  twice). Exactly site 7, correctly deferred.
- `Neo GetPrimitiveSize: unsupported IType` hits = **0** (sizing branch handles
  every enum/struct that reaches it).
- tagged `[neo-bare-nie]` hits = **0** (none of the 6 guards fired — message-only
  confirmed). No NEW bare-NIE site surfaced.

**Legacy-neutral:**
- `GetPrimitiveSize` is fully inside `#if ENABLE_NEO_MODE` (AppDomain.cs:2232-2329)
  -> not compiled under plain Debug. The 3 shared tags (AppDomain :1717/:2162,
  JIT :2915) are string-only swaps with no logic change.
- plain `Debug` NeoStep -> **Ran 318 tests, 17 failed** == the child-4 baseline
  (the 17 are Neo-specific tests that don't pass under Legacy; pre-existing).
- plain `Debug` NeoStepBareNie -> **Ran 2 tests, 0 failed** — both new probes pass
  under Legacy too.

---

## Verdict: APPROVE-WITH-FINDINGS

The change does exactly what it claims, the SIZING mapping is correct, the
additive-safety argument holds, the guard-tags are genuinely message-only, and
every gate (NeoStep 318/0, stash-toggle 2/2, full-smoke 38->6 all-deferred,
Legacy-neutral) is green on independent re-verification. The only finding (F1,
Minor) is a pre-existing companion ref-count gap in the Stobj/Ldobj arm that this
change makes *reachable* (not worse) for an unexercised edge case; it is recorded
as a follow-up and does not gate the change. Ship it; track F1 as a surfaced
follow-up (it is sibling to the Step-13b/ValueTypeBinder ref-tracking work).
