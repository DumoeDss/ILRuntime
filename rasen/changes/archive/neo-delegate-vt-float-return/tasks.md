# Tasks: neo-delegate-vt-float-return

## Phase 1 -- RE-AUDIT (DONE)
- [x] Build CLI (Debug_Neo --no-incremental) + TestCases (Debug). 0 errors.
- [x] Confirm DelegateTest24 FAILS on HEAD: `res = 4E-45` (the bit-reinterpret
      signature), throws. Confirmed via name-filter run.
- [x] Build a minimal probe (`TestCases/NeoStepDelegateVtFloatReturnTest.cs` +
      host helpers in `TestCLRBinding`): TC1 control `Func<int,int>`,
      TC2 `Func<TestVector3,float>` `v => v.X` (THE BUG), TC3 list.Sum mirror.
- [x] Pin the corruption via a diagnostic in `NeoInvokeSub`: the TestVector3
      param is laid out as a BOXED REFERENCE (`paramInfo Size=4/RefCount=1`,
      bytes `01 00 00 00` = mStack idx), but the raw-Ldfld IsValueType arm read
      the slot as FLAT BYTES -> reinterpreted the index as field X
      (`post-ExecuteNeo retDst = 01 00 00 00` = `*(float*)&1`).

## Phase 2 -- implement + verify (DONE)
- [x] `JITCompiler.cs`: new const `NeoRawLdfldBoxedRefOwnerMarker = 0x4`; stamp
      it in `case Code.Ldfld` (CLRType branch) when `ins.Previous` is an `ldarg`
      (new `IsLdargCode` helper, Neo-gated).
- [x] `ILIntepreter.Neo.cs`: new `else if` branch in the raw-Ldfld IsValueType
      block; dereferences `mStack[objIdx]` when `ct.TypeForCLR.IsInstanceOfType`
      holds, else falls back to the flat-bytes `ReadNeoValueType` read.
- [x] Remove the temporary `NeoInvokeSub` diagnostic.
- [x] Stash-toggle: fix stashed -> DelegateTest24 + TC2 + TC3 FAULT, TC1 PASS;
      restored -> all PASS. (Captured by the baseline-73 vs final-70 full smokes.)

## The regression caught + fixed (DONE)
- [x] First draft (assume marker == always-boxed-ref) REGRESSED
      `UnitTest_10039` (`arg = TestVector3.One2; arg.X` -- an in-method `starg`
      overwrites the param slot with flat bytes). Full-smoke diff surfaced it.
- [x] Hybrid fix: runtime type-check + flat-bytes fallback inside the marker
      branch. UnitTest_10039 PASS restored; DelegateTest24 + TC2/TC3 still PASS.

## Verify (DONE -- truth = full-smoke number)
- [x] DelegateTest24: `4E-45` -> `6`. PASS.
- [x] NeoStep smoke: **394/0** (391 baseline + 3 probes; no regression).
- [x] **FULL SMOKE: 73 -> 70 (delta -3).** Baseline = HEAD with fix stashed
      (73 = 71 pre-existing + 2 HEAD-failing probes). Final = hybrid fix (70).
      FIXED: DelegateTest24 + TC2 + TC3. REGRESSIONS: 0 (clean diff).
      In pre-existing terms: 71 -> 70 (DelegateTest24 flipped green; no other
      delegate-VT-return test was hitting this exact `ldarg; ldfld`-on-boxed-
      ref-param shape in the suite).
- [x] Legacy-neutral: structurally (JIT marker stamp + `IsLdargCode` helper +
      runtime branch all `#if ENABLE_NEO_MODE`-gated; the file `ILIntepreter.Neo.cs`
      is file-gated; the const is harmless dead code under Legacy) AND empirically
      (plain Debug + useRegister=true + NeoStep = 394 ran / 18 failed == the
      documented Legacy baseline; the 3 probes PASS under Legacy).

## Files
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` (+const, +IsLdargCode
  helper, +the stamp in `case Code.Ldfld` CLRType branch).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (+the marker
  branch in the raw-Ldfld IsValueType block).
- `ILRuntimeTestBase/TestFramework/TestClass3.cs` (+3 host helpers in
  `TestCLRBinding`: HostInvokeVTFloatBits, HostInvokeIntInt, HostCheckSelectorSum6).
- `TestCases/NeoStepDelegateVtFloatReturnTest.cs` (new; 3 TCs).
