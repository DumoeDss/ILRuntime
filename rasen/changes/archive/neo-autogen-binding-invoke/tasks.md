# Tasks: neo-autogen-binding-invoke

## 1. Re-audit cluster D at the 55 baseline  [DONE]
- Built CLI (Debug_Neo --no-incremental) + TestCases (Debug).
- Ran full smoke: 931 ran / 55 failed (baseline confirmed).
- Extracted the 8 D-cluster tests (Neo.cs:3960/4015/3425) with exact exceptions
  + autogen-stub frames.
- Confirmed all 8 PASS on Legacy (plain Debug+useRegister=true) -> Neo-specific.

## 2. Sub-cluster by root  [DONE]
- byref-enum marshal (UnitTest_RefCLREnum) -- 1 test, clean/mechanical.
- generic-method redirect (CLRBindingTest07/08) -- JIT generic-arg specialization.
- IL-struct in CLR generic (StructTest11) -- implicit boxing gap.
- boxed-struct interface (MyTest.Test) -- boxed-struct-to-interface marshal.
- reflection SetValue (ReflectionTest06) -- arg corruption (suspected box aliasing).
- ILRuntimeType vs framework (ReflectionTest10, TestGenericMethod2).
- Conclusion: multi-rooted; byref-enum is the one clean low-risk fix.

## 3. Fix the byref-enum marshal  [DONE]
- `ILRuntime/CLR/Method/CLRMethod.cs`: split the `int || IsEnum` read arm. For a
  byref enum param, box as the enum type via `Enum.ToObject` (the `Int32` box was
  rejected by `MethodBase.Invoke`'s `CheckValue` against `EnumType&`). By-value
  enums unchanged. Write-back (`:675` `et.IsEnum`) already correct.

## 4. Add a NeoStep regression probe  [DONE]
- `TestCases/NeoStepRefClrEnumTest.cs`: `NeoStep_RefClrEnum_TC1` calls
  `TestCLREnumClass.TestCLREnumRef(out uint, out TestCLREnum)` (the byref-enum
  reflection path) and asserts `key==2 && tag==TestCLREnum.Test2`. Must FAULT on
  HEAD (ArgumentException) and PASS after the fix.

## 5. Verify  [DONE]
- Name-filter: UnitTest_RefCLREnum + probe PASS after fix.
- Stash-toggle (revert CLRMethod.cs ONLY): probe FAILS (ArgumentException Int32
  -> TestCLREnum&) -> pop -> PASS (airtight).
- FULL SMOKE: 55 -> 54 (UnitTest_RefCLREnum flipped; 0 regressions; failure-set
  diff = exactly that one test removed, nothing added).
- NeoStep: 398 ran / 0 failed (probe included; no regression).
- Legacy-neutral: plain Debug+useRegister=true NeoStep = 398 ran / 18 failed
  (pre-existing set); probe PASSes under Legacy.

## 6. Report the other 7 (follow-up children, do NOT bundle)  [DONE]
See proposal.md table. Each is a distinct deep root.
