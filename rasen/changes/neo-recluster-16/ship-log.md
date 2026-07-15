# Ship Log: neo-recluster-16

**Date:** 2026-07-16
**Wave-2 child of:** neo-overhaul
**Branch:** features/object-model-overhaul (NOT committed -- LEAD commits)

## Outcome
- Full Neo smoke: **16 -> 15** (GenericMethodTest11 fixed; strict subset, no new
  failures). Verified by re-running the full smoke AFTER the fix.
- NeoStep: **404/0** (no regression).
- Legacy-neutral: structural (fix is inside the `#if ENABLE_NEO_MODE`
  file-gated `ILIntepreter.Neo.cs`); plain Debug build VERIFIED 0 errors.

## Files changed (NOT committed, LEAD commits)
1. `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- Constrained
   arm `thisObjIdx >= 0` box-once sub-branch: intercept primitive-field-of-IL-
   instance (read+box from `Primitives` via `NeoBoxReturnValue` + `fixed` pin);
   unwrap CrossBindingAdaptorType; else byte-identical. (~18 lines added, 0
   removed. Neo-gated -> Legacy-neutral.)
2. `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-16.md` -- the fresh 16
   cluster table (with the corrected RegisterVMTest04 pin).
3. `rasen/changes/neo-recluster-16/{proposal,design,tasks,ship-log}.md`.

## Key finding (the 12-for-12 re-audit lesson holds)
ground-17's RegisterVMTest04 pin ("generic-instance field-layout -> ManagedObjects
0 ref slots") was DISPROVEN this child: the generic instance's layout is CORRECT
(TRC=1, ManagedObjects.Count=1). The real root is a >3-arg virtual-IL-call
param-marshalling bug (callee reads `action` at SrcOffset=12 as garbage instead
of the caller's ldnull=-1). Documented + re-prioritized, NOT fixed (deep).
ALWAYS re-audit a pinned root before scoping a child.

## Remaining (15, all DEEP singletons -- prioritized in fullsmoke-ground-16.md)
RegisterVMTest04 (>3-arg virtual-call param map), StructTest11 (IL-struct boxing
@ List.Add), MyTest.Test (enumerator dispatch), TestStructDictionary (ldelem.any
CLR-struct-array), UnitTest_TestFCP (CLR-struct newobj retDst), UnitTest_TestInline01
(inlined-call arg alias), ReflectionTest14 (null reflection array), StructTest6
(out-struct writeback), StructTest12 (constrained-callvirt prop on generic struct),
UnitTest_10046 (delegate VT-arg marshal), UnitTest_10051 (nested-struct prop read),
UnitTest_TestStackRegisterTransition3 / ReflectionTest25 / StaticTest05 /
DelegateTest42 (each a distinct deep root needing JIT-dump triage).
