# Tasks -- neo-callvirt-il-target-residual (Wave-2 child)

## Phase 1 -- Re-audit (DONE)
- [x] Build CLI (`Debug_Neo --no-incremental`) + TestCases (`Debug`) with build-server
      shutdown + `UseSharedCompilation=false`.
- [x] Confirm full-smoke baseline = `Ran 928 tests, 63 failed` (exit 127 = known crash).
- [x] Extract the exact 3 tests failing with NRE at the `ResolveNeoCallvirtILTarget`
      frame: `DelegateTest16`, `DelegateTest17`, `SimpleTest.EqualsTest`. Each is an
      inherited `System.Object` method (GetType/GetHashCode/Equals) callvirt on an
      `Action` delegate `this`, `vslot=65535`.
- [x] Instrument `ResolveNeoCallvirtILTarget` (temp throw at Neo.cs:1382) to dump
      `thisObj`/`instance`/`instance.Type`/`declaredMethod`. Result: `thisObj` is a
      `MethodDelegateAdapter`, `instance.Type == <NULL TYPE>`, `declaredMethod` is a
      CLRMethod on `System.Object`. Root cause pinned.
- [x] Confirm all 3 tests PASS on Legacy (`Debug` + `useRegister=true`).

## Phase 2 -- Implement + verify (DONE)
- [x] Add guard at the top of `ResolveNeoCallvirtILTarget`'s
      `if (thisObj is ILTypeInstance instance)` body: if `instance.Type == null`
      (delegate-adapter shape), and `declaredMethod is CLRMethod`, return it for CLR
      dispatch (mirrors the C4 `TryGetNeoVTableSlot`-miss fallback); else throw a clear
      `MissingMethodException`. Covers both call sites. (`ILIntepreter.Neo.cs`, ~18 lines.)
- [x] Remove the diagnostic throw.
- [x] Name-filter verify: 3/3 PASS after fix.
- [x] Stash-toggle (airtight): disable guard (`if (false && ...)`) -> rebuild -> 3/3 FAIL
      (NRE returns) -> restore -> rebuild -> 3/3 PASS.
- [x] Full smoke (truth): `63 -> 60` (delta -3; 3 target tests flip green; 0 real
      regressions). A flaky `InheritanceTest07` "is not bound!" blip in one run is
      crash-order noise (ExecuteR, passes alone, absent on the second run).
- [x] NeoStep broad-green: `394/0` (no regression).
- [x] Legacy-neutral: plain `Debug` build 0 errors; 3 tests PASS on Legacy.

## Files
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`
  (`ResolveNeoCallvirtILTarget`: +1 guard block, Neo-gated, ~18 lines, 0 removed).
- `rasen/changes/neo-callvirt-il-target-residual/design.md` (this change).
