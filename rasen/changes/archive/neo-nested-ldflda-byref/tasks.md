# Tasks: neo-nested-ldflda-byref

- [x] 1. RE-AUDIT: build CLI (Debug_Neo --no-incremental) + TestCases (Debug).
  Confirm the 3 D4 tests: UnitTest_Struct (ldind IOOB), UnitTest_Struct2 (ldind
  NRE @ line 104), UnitTest_10051 (property-read assertion). Confirm all PASS on
  Legacy (plain Debug + useRegister=true). DONE.
- [x] 2. Dump the Neo JIT for UnitTest_Struct2; confirm the inner `ldflda value`
  (predecessor of `ldind.i4`) operates on the outer ldflda's byref and the
  runtime Ldflda arm has no byref-operand branch -> falls to `else` -> produces
  `(containingObjIdx, innerFieldHash)` -> ldind mis-resolves. DONE (JIT dump at
  .tmp-d4-struct2-neo.log lines 6842-6878: `4:ldflda r2,r0; 5:ldflda r2,r2;
  6:ldind.i4 r3,r2` -> NRE @ Neo.cs ldind arm).
- [x] 3. Add JIT marker `NeoLdfldaNestedByRefMarker = 0x10` (JITCompiler.cs
  const + stamp in `case Code.Ldflda` CLRType block when CIL predecessor is
  Ldflda/Ldsflda). DONE.
- [x] 4. Add the `NeoNestedFieldAddr` descriptor class + ResolveNeoNestedInnerField
  + WriteNeoNestedInnerField helpers (ILIntepreter.Neo.cs near
  NeoReadClrObjectField). DONE.
- [x] 5. Add the runtime Ldflda nested branch (`nestedByRefMarker && objIdx >=
  0`) that materializes the boxed struct and pushes a descriptor. DONE.
- [x] 6. Add the leading descriptor check to ldind.I4/I8/R4/R8 and
  stind.I4/I8/R4/R8. DONE.
- [x] 7. Add probe TestCases/NeoStepNestedLdfldaByrefTest.cs (3 TCs: F-10, CLR-
  object, field-preservation). DONE.
- [x] 8. Build (kill build-server + UseSharedCompilation=false for TestCases).
  CLI Debug_Neo 0 errors; TestCases Debug 0 errors; plain Debug (Legacy) 0
  errors. DONE.
- [x] 9. Stash-toggle: stash JITCompiler.cs + ILIntepreter.Neo.cs -> 3/3 probes
  FAULT (NRE) -> pop -> 3/3 PASS with asserted values (150/150/35). DONE.
- [x] 10. Name-filter verify: UnitTest_Struct PASS (prints 100, matching Legacy);
  UnitTest_Struct2 progresses past cases 1&2 (prints 222/222, exceeding Legacy)
  but still fails at case 3 (CLR static ldsflda, separate deferred item);
  UnitTest_10051 still fails its own assertion (property-read, separate shape).
  DONE.
- [x] 11. NeoStep smoke: 401/0 (398 baseline + 3 probes), no regression. DONE.
- [x] 12. FULL SMOKE: 32 -> 31 (UnitTest_Struct flipped; the 31 are a strict
  subset of the baseline; no new failures). 935 ran (932 + 3 probes) / 31 failed
  / 20 ignored / 7 todos. DONE.
- [x] 13. Legacy-neutral: plain Debug build 0 errors (all changes Neo-gated;
  ILIntepreter.Neo.cs is file-gated). DONE.
