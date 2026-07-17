# Tasks: neo-il-instance-clr-struct-field-f10

## 1. RE-AUDIT (DONE)
- [x] Build CLI (Debug_Neo --no-incremental) + TestCases (Debug). 0 errors.
- [x] Confirm the F-10 NIE at :4601 on HEAD: StructTest7 ("Field X on
      TestVector3") and UnitTest_10051 ("Field x on Fixed64Vector2") both hit
      `Neo raw Stfld: ... F-10 ManagedObjects storage` (name-filter runs).
- [x] Pin root cause: IL-instance owner + CLR-struct field = boxed struct at
      ManagedObjects[refOff]; the raw Stfld/Ldfld VT-owner arm NIE'd on
      ILTypeInstance owners instead of routing to ManagedObjects.

## 2. IMPLEMENT (DONE)
- [x] raw Stfld VT-owner arm (`ILIntepreter.Neo.cs` ~:4621): TYPE-FIRST -- when
      `mStack[objIdx] is ILTypeInstance||CrossBindingAdaptorType`, box/mutate/
      unbox via `ManagedObjects[off & ~flag]` (Activator seed on null); else the
      child-27 NeoReadClrObjectField path.
- [x] raw Ldfld VT-owner byref arm (~:4360): symmetric TYPE-FIRST read.
- [x] Fix the flag-first regression: initial draft tested `(off & flag)!=0`
      before the type check -> broke the 4 child-27/29 probes whose struct-field
      hash carries bit 0x40000000. Type-first structure (mirror Stobj :6199 /
      NeoMarshalByrefFieldToSlot :470) fixed it.

## 3. PROBE (DONE)
- [x] `TestCases/NeoStepIlInstanceClrStructFieldTest.cs` (3 probes):
      TC1 Stfld write + Ldfld_Ref whole-struct read = 149;
      TC3 raw Ldfld field-of-field float read = 149;
      TC4 null-slot seed write = 149.
      (TC2 removed: `.x.RawValue` readback is a separate pre-existing gap.)

## 4. VERIFY (DONE -- truth = full-smoke number)
- [x] StructTest7 PASS (was F-10 NIE).
- [x] FULL SMOKE: 74 -> 73. F-10 NIE count 0. child-27/29 intact. 3 probes pass.
- [x] NeoStep 388 -> 391/0 (no regression).
- [x] Legacy-neutral by construction (ExecuteNeo, #if ENABLE_NEO_MODE).
