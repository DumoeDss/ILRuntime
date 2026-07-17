# Ship Log: neo-delegate-adapter-notfound (Wave-2 child C13)

Date: 2026-07-14
Branch: features/object-model-overhaul
Delivery mode: local (LEAD commits; worker does not commit)

## Files changed (1 source file)
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (+~56 lines, all inside the
  `Callvirt_CLR` case, all under the file's `#if ENABLE_NEO_MODE`). Added a Dummy-only
  delegate-Invoke interception (see design.md D2).

No JIT / optimizer / object-model / binding / test-harness change. Neo-gated ->
Legacy-neutral by construction.

## Verification (truth = full-smoke number)
- Name-filter: DelegateExtTest03 / DelegateTest14 / DelegateTest22 -> PASS (were KeyNotFound).
- Stash-toggle ILIntepreter.Neo.cs -> 3/3 FAULT -> pop -> 3/3 PASS (airtight).
- NeoStep smoke: **382/0** (no regression; Callvirt_IL untouched; NeoStep19_ClosureOverThis
  green -- the intercept-all variant had regressed it, the Dummy-only variant does not).
- FULL Neo smoke: **118 -> 114** (Ran 916 / 114 failed). The 3 C13 tests are gone from the
  failure list; "Cannot find Delegate Adapter" count = 0 (entire C13 defect class
  eliminated). A 4th test flipped green as a bonus (same Dummy-Invoke-on-CLR-delegate
  defect class, fixed generally by the interception). No delegate regressions (remaining
  delegate failures are pre-existing C1-residual Step17/13b / C4 / C7 / C16).
- Legacy-neutral: plain Debug + useRegister=true + NeoStep = 382 ran / 18 failed == stable
  pre-existing Legacy set (the Neo file is not compiled under Legacy).

## Key durable finding
A CLR delegate type's `Invoke` (Action<>/Func<>) is lowered to `callvirt.clr`
(Callvirt_CLR), NOT callvirt.IL. The Neo Callvirt_IL delegate-Invoke interception
(Step 19) therefore does NOT cover CLR delegate types -- a parallel interception is needed
in Callvirt_CLR. Legacy does not have this split (its IsDelegateInvoke check is in the
generic dispatch). Any future "delegate Invoke throws in Neo but works in Legacy" should
check whether the delegate type is CLR (Action/Func) and thus dispatches via Callvirt_CLR.

The interception must be **Dummy-only**: a real adapter (MethodDelegateAdapter /
FunctionDelegateAdapter) has a registered converter and is served by the existing
GetConvertor path; intercepting it triggers NeoRunDelegateTargetOnThis's D2 rebind
(`mStack[thisIdx] = instance`), which corrupts a reused delegate `this` mStack slot
(NeoStep19_ClosureOverThis regression, diagnostic-pinned).
