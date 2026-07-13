# Review Report -- neo-delegate-adapter-clr-cast

Wave-2 child C1 of the `neo-overhaul` portfolio. Reviewer did NOT write this code.
All findings verified against real code reads + a real Neo/Legacy run on HEAD of
the working tree (build `Debug_Neo --no-incremental` + `TestCases -c Debug`, 0 errors).

## Verdict: APPROVE-WITH-FINDINGS

The change is correct, Legacy-parity, well-tested, and the full-smoke delta (189 -> 153)
is substantiated by the spot runs. One Minor perf note + two Trivial observations; no
Blockers, no Majors, no CHANGES-REQUESTED.

---

## What was verified (all GREEN)

### 1. Fix B byte-fidelity -- CONFIRMED (the load-bearing risk)

- Read the generator template: `BindingGeneratorExtensions.cs:244-246` emits, for a
  delegate param:
  `({realClsName})typeof({realClsName}).CheckCLRTypes(ILIntepreter.ReadNeoReference(__frameBase, ref __curPrim, __mStack), (ILRuntime.CLR.Utils.Extensions.TypeFlags)8);`
  and `MethodBindingGenerator.cs:296-298` mirrors it for a delegate-typed `this`.
- `TypeFlags.IsDelegate = 0x8` confirmed (`Extensions.cs:168`).
- `AppendArgumentCodeNeo` de-byref's `pt` at the top (`BindingGeneratorExtensions.cs:140`
  `var pt = p.IsByRef ? p.GetElementType() : p;`), so the generator ALSO emits
  `CheckCLRTypes` for `ref` delegate params -- the 3 Interlocked `CompareExchange`
  sites are byte-identical too.
- ALL 41 hand-ported sites match the template EXACTLY:
  - cast type `(T)` == `typeof(T)` type: 0 mismatches across 41 sites.
  - every site uses `(ILRuntime.CLR.Utils.Extensions.TypeFlags)8`; 0 deviations.
  - spot-checked across all 10 files (StaticGenericMethods, GenericExtensions,
    DelegateTest, IntDelegate, IntDelegate2, List_Action, List_Func,
    List_ILTypeInstance, Enumerable, Interlocked) -- shape-identical.
- Non-delegate params LEFT UNTOUCHED: the `ExtensionClass`/`List<Action<int>>`
  instances and `IEnumerable` sources are still plain `(T)ReadNeoReference(...)`.
  The `List<Action<int>>` `instance_of_this_method` correctly stays unconverted
  (`typeof(Delegate).IsAssignableFrom(List<...>)` is false -> generator does not
  emit CheckCLRTypes for it). No missed delegate sites.
- Legacy `#else` halves / `AssignFromStack_*` setters: ZERO lines touched (diff
  grep for `AssignFromStack|#else|#if` in the binding diff = empty). Legacy is
  byte-for-byte unchanged.

### 2. Fix A/C correctness (Neo.cs) -- CONFIRMED Legacy-parity

- `Extensions.CheckCLRTypes` (`Extensions.cs:281-288`) routes `IsDelegate`-typed
  objects correctly: a real `Delegate` passes through (short-circuit), an
  `IDelegateAdapter` is converted via `GetConvertor(pt)` (-> real CLR delegate).
- Fix A (`ILIntepreter.Neo.cs:4618`): `value = ft.CheckCLRTypes(value);` before
  `ct.SetStaticFieldValue(sIdx, value)`. `ft == f.FieldType`. Mirrors Legacy
  `Register.cs:3308` `t.SetStaticFieldValue(intVal, f.FieldType.CheckCLRTypes(...))`
  exactly.
- Fix C (`ILIntepreter.Neo.cs:6634-6636`): resolves `var f = ct.GetField(fieldHash)`
  and `value = f.FieldType.CheckCLRTypes(value);` before `ct.SetFieldValue`. Mirrors
  Legacy `Register.cs:3129` `SetFieldValue(fieldToken, ref obj, f.FieldType.CheckCLRTypes(...))`.
  CRITICAL SAFETY: `CLRType.SetFieldValue` ALREADY calls `GetField(hash)` internally
  (`CLRType.cs:486`), so Fix C's `GetField(fieldHash)` resolves the EXACT same field
  the store writes -- no wrong-type risk. The `f != null` guard preserves prior
  behavior on a GetField miss (Fix C is a clean no-op then).
- "ILTypeInstance -> CLRInstance unwrap" (the bonus the implementer claims): real,
  but it is Legacy-parity (`Extensions.cs:297-312` `obj is ILTypeInstance` branch ->
  `ins.CLRInstance`), NOT a Neo overfire. A CLR-base-typed static/instance field
  holding an ILTypeInstance unwraps identically in Legacy and Neo now.

### 3. No-overfire -- CONFIRMED

- Fix B: `IsDelegate` gate `(TypeFlags)8` present at every one of the 41 sites;
  non-delegate params untouched (verified above).
- Fix A/C: `CheckCLRTypes` is total -- a plain matching object returns as-is
  (`Extensions.cs:314` `return obj`), so non-delegate, non-ILTypeInstance values
  are unaffected. NeoStep 380/0 (below) confirms no regression.

### 4. C1 spot tests now GREEN under Neo (real run)

Spot-ran 5 representative C1 tests (`Debug_Neo`, `useRegister=true`):

| Test | Result | Exercises |
|---|---|---|
| `DelegateTest01` | PASS (1/0) | Fix A (Stsfld CLR-static delegate field) |
| `GenericStaticMethodTest1` | PASS | Fix B (StaticMethod_2_Neo Action param) |
| `GenericExtensionMethod1Test1` | PASS (1/0) | Fix B (Method1_0_Neo Action param) |
| `DelegateTest41` | PASS (1/0) | Fix C (Stfld CLR instance delegate field) |
| `CLRBindingTest07` | PASS (1/0) | (was a C1-adjacent cast) |

(GenericStaticMethodTest1's substring filter also matched GenericStaticMethodTest10-19;
the single failure in that run was GenericStaticMethodTest19 with a PRE-EXISTING C16
NRE at `Neo.cs:4876` (Unbox_Any arm) -- unrelated to this PR and Neo-specific: it
PASSES under Legacy. See PRE-EXISTING below.)

### 5. NeoStep smoke -- 380/0 CONFIRMED (no regression)

`dotnet run ... true NeoStep` -> `Ran 380 tests, 0 failded`. Unchanged vs the
established 380/0 baseline.

### 6. Progressed (NOT regressed) tests -- CONFIRMED honest

The implementer notes a few C1-listed tests now reach a LATER different-cluster error.
Verified each genuinely PROGRESSED (different exception type), not regressed:

| Test | Before (ground truth) | After this PR | Cluster |
|---|---|---|---|
| `DelegateTest43` | cast MethodDelegateAdapter`3 -> onChangeWithOldVal | "Neo callvirt this is null" | -> C4 |
| `DelegateTest19` | cast FunctionDelegateAdapter`1[TestCLREnum] | IndexOutOfRange | -> C10 |
| `UnitTest_10046` | cast MethodDelegateAdapter`1[TestVector3] | generic Exception | downstream |

The C1 delegate-cast was their FIRST error; fixing it unblocked them to the next,
unrelated bug. Honest reporting, not a hidden regression.

### 7. Legacy-neutral -- CONFIRMED via real run

Built plain `Debug` CLI (0 errors), ran under Legacy (`useRegister=true`):
`DelegateTest01` 1/0, `GenericStaticMethodTest1` 11/0 (all 11 incl. test19 PASS),
`GenericExtensionMethod1Test1` 1/0. Fix is Neo-gated; binding edits are in
`#if ENABLE_NEO_MODE` regions only.

---

## Findings by severity

### Blocker
(none)

### Major
(none)

### Minor

- **[M1] Fix C double field-lookup on a hot path (this PR, perf only -- correctness fine).**
  `NeoWriteClrObjectField` now calls `ct.GetField(fieldHash)` (Fix C, `Neo.cs:6634`)
  and then `ct.SetFieldValue(fieldHash, ...)`, and `SetFieldValue` ITSELF calls
  `GetField(hash)` again internally (`CLRType.cs:486`). So every CLR-object field
  write resolves the field twice. `NeoWriteClrObjectField` is on the Stfld/stind
  hot path (call sites at `Neo.cs:630, 4282, 4308, 4324, 5409-5464, 5610, 5784`).
  Correctness is unaffected (both resolve the same field); this is a per-write
  dictionary-lookup cost. Suggest a future follow-up share the resolved `FieldInfo`
  (e.g. a `SetFieldValue` overload that accepts a pre-resolved `FieldInfo`, or move
  the conversion into `SetFieldValue` itself). Non-blocking.

### Trivial

- **[T1] Interlocked CompareExchange binding is out-of-cluster scope (this PR).**
  The 3 sites in `System_Threading_Interlocked_Binding.cs` (`CompareExchange_0_Neo`)
  are byte-identical to the generator and correct, but no C1-listed test exercises
  `CompareExchange` on delegates. Slight scope expansion beyond the C1 defect.
  Defensible (complete that binding's delegate params while touching it) and harmless.

- **[T2] Latent stale non-delegate bindings left as-is (PRE-EXISTING class, out of scope).**
  The 10 touched files may contain OTHER non-delegate autogen sites that are also
  stale vs the current generator. This PR correctly scopes to delegate-param sites
  only (the C1 defect class); a full regen (GUI-bound, per child-28) is the
  follow-up that would catch the rest. Not this PR's job -- noted for transparency.

### PRE-EXISTING (surfaced by this PR's fix, NOT caused by it)
- `GenericStaticMethodTest19` -- NRE at `Neo.cs:4876` (Unbox_Any `srcIdx<0`/`obj==null`),
  C16 NRE bucket. Neo-specific (passes under Legacy).
- `DelegateTest43` -- C4 "Neo callvirt this is null".
- `DelegateTest19` -- C10 ArgumentOutOfRange.
- `UnitTest_10046` -- downstream generic Exception.
All are separate-cluster bugs the C1 fix unblocked; each belongs to its own Wave-2 child.

---

## Why APPROVE-WITH-FINDINGS (not CHANGES-REQUESTED)

- The defect class is correctly identified and the fix is Legacy-parity at both
  engine sites (A/C) and byte-fidelity-verified across all 41 binding sites (B).
- 5/5 C1 spot tests green under Neo; NeoStep 380/0 (no regression); Legacy-neutral.
- Progressed tests are honestly reported (different-cluster residuals).
- The only actionable note (M1) is a perf optimization on a hot path with zero
  correctness impact -- worth a follow-up, not a blocker.

## Suggested follow-ups (route to implementer / future child)
1. (perf, M1) Eliminate the double `GetField` in `NeoWriteClrObjectField` -> `SetFieldValue`.
2. (scope, T2) Full autogen binding regen once GUI-bound, to clear all latent stale sites.
3. The PRE-EXISTING cluster residuals above (C4/C10/C16) are owned by their respective Wave-2 children.
