# Tasks: neo-delegate-adapter-notfound (Wave-2 child C13)

## 1. Re-audit (VERIFY) -- DONE
- [x] Build CLI (Debug_Neo) + TestCases (Debug), UseSharedCompilation=false.
- [x] Confirm Neo failure (name filter): DelegateExtTest03 / DelegateTest14 / DelegateTest22
      all FAIL with `KeyNotFoundException: Cannot find Delegate Adapter for:<ILMethod>`.
- [x] Confirm Legacy PASS (plain Debug + useRegister=true): all 3 PASS.
- [x] Pin root cause Neo-vs-Legacy (diagnostic-pinned, see design.md):
      Legacy intercepts delegate-Invoke (`IsDelegateInvoke && this is IDelegateAdapter`)
      in the callvirt CLR dispatch (`Register.cs:3005`) -> IDelegateAdapter.ILInvoke (Dummy
      works). Neo has the interception in `Callvirt_IL` but NOT `Callvirt_CLR`; a CLR
      delegate Invoke (Action/Func, lowered to callvirt.clr) reaches the autogen binding ->
      CheckCLRTypes -> GetConvertor -> throws on the DummyDelegateAdapter. ONE root cause.

## 2. Implement (Neo-gated, single file) -- DONE
- [x] Add a Dummy-only delegate-Invoke interception at the top of the `Callvirt_CLR` case
      in `ILIntepreter.Neo.cs` (after the IL-VT array guard, before
      ResolveNeoCallvirtCLRTarget). When `targetMethod.IsDelegateInvoke` and the delegate
      `this` (`mStack[*(int*)targetBase]`) is a `DummyDelegateAdapter`, route to
      NeoRunDelegateTargetOnThis (IL target) / NeoInvokePublic (non-IL) + multicast chain.
- [x] Dummy-only discriminator (the load-bearing detail): a REAL adapter has a registered
      converter -> the existing GetConvertor path already serves it; intercepting it would
      overwrite a reused mStack this-slot via the D2 rebind (NeoStep19 regression, avoided).
- [x] `Callvirt_IL` delegate handler left byte-untouched (no refactor of working code).

## 3. Verify (truth = full-smoke number) -- DONE
- [x] Name-filter: DelegateExtTest03 / DelegateTest14 / DelegateTest22 PASS after fix.
- [x] Stash-toggle: stash ILIntepreter.Neo.cs -> 3/3 FAIL (KeyNotFound); pop -> 3/3 PASS.
- [x] NeoStep smoke: 382/0 (no regression; Callvirt_IL untouched, NeoStep19 green).
- [x] FULL smoke: **118 -> 114** (Ran 916 / 114 failed). The 3 C13 tests gone;
      "Cannot find Delegate Adapter" count = 0 (entire C13 defect class eliminated).
      A 4th test flipped green (bonus, same Dummy-Invoke defect class, fixed generally).
- [x] Legacy-neutral: `ILIntepreter.Neo.cs` is file-gated `#if ENABLE_NEO_MODE`; plain
      Debug does not compile it. Legacy NeoStep = 382/18 (pre-existing set, unaffected).

## 4. Artifacts -- DONE
- [x] proposal.md, design.md, tasks.md (this file).
