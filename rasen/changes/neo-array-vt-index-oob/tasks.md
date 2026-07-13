# Tasks: neo-array-vt-index-oob

## Phase 1 -- RE-AUDIT (VERIFY)  [DONE]
- [x] Build CLI (Debug_Neo) + TestCases (Debug), 0 errors.
- [x] Confirm failure on Neo (name filter) for representatives:
      ArrayTest05, StructTest8, StructTest11, UnitTest_10035, UnitTest_10036,
      TestUsingNested, TestValueTypeBinding.Test03, UnitTest_Struct,
      UnitTest_10051 -- all FAIL on Neo; confirm exception site per test.
- [x] Pin exception sites (Neo.cs lines): 5559 (Stelem_Any) x4, 4313
      (Ldfld_R4), 3796 (Callvirt_CLR), 4731 (Stsfld), 5695 (Ldind_I4),
      Optimizer.Neo.cs (LowerNeoOffsets). => 6+ DISTINCT roots.
- [x] Group by root; Stelem_Any value-type-element is the largest (4 tests).

## Phase 2 -- implement + verify  [DONE -- largest sub-cluster]
- [x] Implement Stelem_Any value-type-element branch in ILIntepreter.Neo.cs
      (discriminate by element type; VT -> ReadNeoValueType + Array.SetValue;
      ref -> unchanged). Mirrors child-26 Stobj array WRITE.
- [x] Add host helper `SumTestVector3ArrayElems` to TestCLRBinding
      (TestClass3.cs) for host read-back.
- [x] Add NeoStep probe `NeoStepStelemAnyVtElementTest.cs` (TC1 initializer,
      TC2 index-assign; assert via host).
- [x] Rebuild CLI (Debug_Neo) + TestCases (Debug) with build-server shutdown +
      UseSharedCompilation=false (ILRuntimeTestBase touched).
- [x] Name-filter: ArrayTest05 / UnitTest_10035 / TestValueTypeBinding.Test03 /
      TestUsingNested all PASS after fix.
- [x] Stash-toggle: stash ILIntepreter.Neo.cs -> 2/2 probe FAULT
      (ArgumentOutOfRange); pop -> 2/2 PASS.
- [x] NeoStep smoke: 382/0 (380 baseline + 2 probes, no regression).
- [x] FULL SMOKE: 122 -> 118 (4 flipped = Stelem_Any cluster).
- [x] Legacy-neutral: plain Debug + useRegister=true -> 4 tests + 2 probes PASS.

## Out of scope (distinct roots, reported honestly -- future children)
- [ ] StructTest8   -- Ldfld_R4 bad owner index (Neo.cs:4313).
- [ ] StructTest11  -- Callvirt_CLR List<struct>.Add struct arg (Neo.cs:3796).
- [ ] UnitTest_10036-- Stsfld IL-static struct ref-field (Neo.cs:4731).
- [ ] ExpTest_10.UnitTest_Struct -- Ldind_I4 ins.Primitives OOB (Neo.cs:5695).
- [ ] UnitTest_10051-- LowerNeoOffsets JIT "Push" = cluster C7.
- [ ] CLRBindingTest08 -- order-dependent flake (passes alone, fails in suite).
