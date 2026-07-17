# Proposal: neo-delegate-adapter-notfound (Wave-2 child C13)

## Problem
Full Neo smoke (post C1/C4/C2/C12/C10) is 118 failed. Cluster C13 = 3 tests fail
with `KeyNotFoundException: Cannot find Delegate Adapter for:<ILMethod>`:
- `TestCases.DelegateExtTest.DelegateExtTest03` (Func<string,string>)
- `TestCases.DelegateTest.DelegateTest14` (Action<string>, a static IL method target)
- `TestCases.DelegateTest.DelegateTest22` (Action<string,Object>, a 2-arg IL method target)

All 3 PASS on Legacy (plain Debug + useRegister=true). All 3 FAIL on Neo.

## Root cause (re-audited, Neo-vs-Legacy evidence)
An IL method bound to a CLR delegate type (Action<>/Func<>) whose parameter shape has
NO registered per-arity adapter (helper.cs registers int/bool/etc. but NOT string,
string+Object, string->string) is wrapped in a `DummyDelegateAdapter` at the delegate
`newobj` (DelegateManager.FindDelegateAdapter -> dummy fallback). This happens IDENTICALLY
on Legacy and Neo (confirmed via diagnostic: 4 Dummies created for DelegateTest14 on both).

The divergence is the delegate **Invoke** callvirt:
- Legacy (`ILIntepreter.Register.cs:3005-3030`) INTERCEPTS `cm.IsDelegateInvoke` in the
  generic callvirt/call CLR dispatch: when `this is IDelegateAdapter` it calls
  `IDelegateAdapter.ILInvoke` directly and NEVER reaches the autogen CLR Invoke binding
  (so CheckCLRTypes -> GetConvertor is bypassed; the Dummy's ILInvoke runs the IL method).
- Neo has this interception in the `Callvirt_IL` case (`ILIntepreter.Neo.cs:3688-3756`)
  BUT NOT in the `Callvirt_CLR` case. A CLR delegate type's Invoke (Action<string>.Invoke)
  is lowered to `callvirt.clr` (JIT-confirmed) -> the Neo Callvirt_CLR case dispatches
  straight to `ResolveNeoCallvirtCLRTarget` + `InvokeNeoClrMethod` -> the autogen
  `Invoke_0_Neo` binding -> `CheckCLRTypes(IsDelegate)` -> `GetConvertor` ->
  `DummyDelegateAdapter.NativeDelegateType` -> `ThrowAdapterNotFound` (KeyNotFoundException).

Diagnostic proof: under Legacy the `CheckCLRTypes` IsDelegate branch is NEVER entered for
DelegateTest14 (the interception skips it); under Neo it IS entered with obj=DummyDelegateAdapter.

This is ONE root cause for all 3 tests (all call Invoke on a CLR Action/Func via callvirt.clr).
Distinct from C1: C1 = a REAL adapter (MethodDelegateAdapter) is found but the cast to the
CLR delegate type fails (InvalidCastException). C13 = the adapter lookup is fine, but the
CLR-delegate-type Invoke dispatches to the autogen binding instead of the adapter.

## Fix (Neo-gated, mirrors Legacy + the existing Neo Callvirt_IL handler)
Factor the delegate-Invoke interception (the body of the Callvirt_IL delegate block) into a
shared helper `TryNeoDelegateInvoke(...)`, and call it from BOTH `Callvirt_IL` (replacing the
inline body -- behavior-identical) AND `Callvirt_CLR` (the missing arm). When `this` is an
`IDelegateAdapter`, the helper routes to the adapter's `NeoRunDelegateTargetOnThis` (IL target)
or `NeoInvokePublic` (non-IL target) + multicast chain, exactly as Legacy's ILInvoke path does,
and the autogen binding (with its CheckCLRTypes) is bypassed. 100% under `ENABLE_NEO_MODE`.

## Out of scope
- The `Call_Redirect` delegate-Invoke path (a `call`-not-`callvirt` on a delegate Invoke) -- not
  observed in the 3 failing tests; will surface in the full smoke if present, handled then.
- Registering the missing per-arity adapters (string/string+Object) -- wrong fix (whack-a-mole;
  Legacy does not need them because it intercepts Invoke).

## Verify (truth = full-smoke number)
- Name-filter: DelegateExtTest03 / DelegateTest14 / DelegateTest22 PASS (stash-toggle FAULT->PASS).
- FULL smoke: record delta 118 -> N (expect 118 - 3 = 115, possibly more if the same arm fixes
  other delegate-Invoke shapes).
- NeoStep smoke 0 failures (no regression; Callvirt_IL refactor covered by NeoStep19 delegate tests).
- Legacy-neutral (Neo-gated; plain Debug unaffected).
