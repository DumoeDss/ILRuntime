## Why

`GetNeoILInstance` (`ILIntepreter.Neo.cs`, the guard every typed
`Ldfld_*`/`Stfld_*`/`Stfld_Value`/`Ldfld_Value` heap arm and the
`Stobj`/`Ldobj` IL-instance fallback call to resolve the owner
`ILTypeInstance`) throws a single `"Step 17/13b ... deferred"`
`NotImplementedException` for **any** non-`ILTypeInstance` owner. Full-smoke
diagnosis (5 hits: `DelegateExtObjMethod.IntTest`, `JsonTest2`,
`RegisterVMTest04`, `TestStaticFieldInstance`, `StructTest14`) disproved the
task's "a CLR object routes here" hypothesis: the owners are **not** raw CLR
objects. They are (a) a **null** mStack slot, or (b) a
**`CrossBindingAdaptorType`** wrapper. The guard must be CLR-faithful: a null
owner is a `NullReferenceException` (not a deferred feature), and an adaptor
wrapper unwraps to its `ILInstance` (the field was JIT-classified IL-declared,
so it lives on the `ILTypeInstance`). The current NIE masks both shapes behind
a misleading "deferred" message and blocks the adaptor case outright.

## What Changes

- `GetNeoILInstance` is reworked to discriminate three owner shapes:
  1. **null** mStack entry -> throw `NullReferenceException` (CLR semantics for
     `ldfld`/`stfld`/`ldobj`/`stobj` on null). Replaces the misleading NIE.
  2. **`CrossBindingAdaptorType`** -> return `cba.ILInstance` (unwrap; the
     typed arm's IL-declared field lives on the underlying `ILTypeInstance`).
     Mirrors the raw `Ldfld`/`Stfld` handler added by child 9
     (`neo-il-instance-clr-base-field`). A null `ILInstance` also throws NRE.
  3. **any other CLR shape** -> keep the defensive Step-tagged NIE (the byref
     consumers and the raw `Ldfld`/`Stfld` handlers route genuine CLR objects to
     the field-hash accessor `NeoReadClrObjectField`/`NeoWriteClrObjectField`
     BEFORE reaching here, so this arm stays a fail-loud guard).
- Add `TestCases/NeoStepClrObjIlPathTest.cs` with two probes that FAULT on HEAD
  (the NIE is not an `NRE`, so the probe's `catch(NullReferenceException)` does
  not match and the NIE propagates -> test fails) and PASS after the fix (the
  null-owner `ldfld` throws `NRE`, which is caught). Covers the `Ldfld_I4` and
  `Ldfld_Ref` typed arms.
- Neo-gated (the helper lives in `ILIntepreter.Neo.cs`, compiled under
  `ENABLE_NEO_MODE`); Legacy-neutral by construction.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `neo-value-types`: pin the owner-resolution contract of the Neo typed
  field-access guard (`GetNeoILInstance`): a null owner SHALL surface as a
  `NullReferenceException` and a `CrossBindingAdaptorType` owner SHALL resolve
  to its `ILInstance` (the typed arms are IL-declared-field-only). Consistent
  with the sibling field-access children 4 (`neo-raw-stfld-ldfld`) and 9
  (`neo-il-instance-clr-base-field`), which also home under `neo-value-types`.

## Impact

- **Code**: `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`
  (`GetNeoILInstance`, ~`ILIntepreter.Neo.cs:6070`). ~25 lines net; no JIT,
  optimizer, or object-model change. No new helper.
- **Tests**: new `TestCases/NeoStepClrObjIlPathTest.cs` (2 probes). NeoStep
  smoke 328 -> 330 (both new probes pass); Legacy NeoStep 328 ran / 17 failed
  -> 330 ran / 17 failed (both probes pass; the 17-failure baseline holds).
- **Scope note (important for the LEAD)**: this change makes the guard
  CLR-faithful and unblocks the adaptor case, but does NOT make all 5
  full-smoke hits pass -- 3 of them are blocked by a deeper, separate upstream
  root cause (see `design.md` "Out of scope"): a `ceq`/`brfalse`-on-reference
  null-comparison gap (the `if (x == null)` lazy-init pattern) that skips the
  `stsfld`, and the IL value-type `newobj` `this` ([VT-THIS-ADDR]). The
  `DelegateExt` hit is Step 19 (delegates). These are documented as follow-ups;
  the 5 specific `Step 17/13b` tagged-NIE occurrences are eliminated (nulls now
  throw NRE; the adaptor unwraps).
