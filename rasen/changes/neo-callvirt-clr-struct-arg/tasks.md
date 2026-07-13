# Tasks: neo-callvirt-clr-struct-arg

## Phase 1 -- Re-audit (VERIFY) [DONE]
- [x] Build CLI (Debug_Neo) + TestCases (Debug).
- [x] Minimal probe `TestCases/NeoStepCallvirtClrStructArgTest.cs` (TC1
      `List<TestVector3>.Add`, TC2 `List<int>.Add` control, TC3 multi-add).
      Host helpers in `TestClass3.cs :: TestCLRBinding` read `list[i]` entirely
      in CLR (sidesteps IL ldfld/conv bugs). TC1/TC3 FAULT on HEAD (DivByZero,
      host receives zero), TC2 PASSES on HEAD.
- [x] Legacy control: all 3 probes PASS under plain Debug + useRegister=true.
- [x] Pin root cause via byte-dump diagnostics in `InvokeNeoClrMethod` +
      `CLRMethod.Invoke`. Result: param-map layout CORRECT (targetBase has the
      struct bytes), reflection fallback NEVER REACHED (`hasRedirectNeo=True`),
      real cause = stale autogen `Add_0_Neo` stub leaves `@item = default(...)`.

## Phase 2 -- implement + verify [DONE]
- [x] Hand-port `Add_0_Neo` in `System_Collections_Generic_List_1_TestVector3_
      Binding.cs` to `ReadNeoValueType` (post-Step-13b template, child-28 class).
- [x] Hand-port `Add_0_Neo` in `System_Collections_Generic_List_1_
      TestVector3NoBinding_Bi.cs` (identical shape/stale stub).
- [x] Remove all temporary diagnostics (CLRMethod.cs, ILIntepreter.Neo.cs,
      Enumerable binding Sum_2_Neo) -- verified clean rebuild.
- [x] Probe PASS after fix: 3/0 (Neo). Stash-toggle: HEAD -> TC1/TC3 fault,
      fixed -> 3/0 PASS.
- [x] NeoStep broad smoke: 388/0 (no regression).
- [x] Legacy-neutral: plain Debug + useRegister=true probe 3/0; change is
      `#if ENABLE_NEO_MODE`-gated (Legacy `Add_0` byte-identical).
- [x] Full smoke delta recorded: **101 -> 101 (unchanged)**. DelegateTest24
      (sole List<VT>.Add consumer) does not flip -- cascading delegate float-
      return gap (`Sum(v=>v.X)` returns 4E-45; FunctionDelegateAdapter2
      corrupts the IL lambda's VT/float marshalling). Proven: host-side
      list[i].X = 1,2,3 after the fix. Documented as surfaced follow-up
      `neo-delegate-vt-float-return` (Step-19 delegate territory, out of scope).

## Out of scope (documented)
- Delegate float/VT return marshalling in `FunctionDelegateAdapter2`
  (blocks DelegateTest24's `Sum`; Step-19 class).
- 9 remaining stale `default(...)` autogen Neo stubs (JInt, TestStruct,
  TestVector3NoBinding-methods, TestVectorClass, TestVector3.Test_3 VT-this+out,
  async-builder byref-`this`) -- each a mechanical port once its shape is
  confirmed; not blocking this child.
