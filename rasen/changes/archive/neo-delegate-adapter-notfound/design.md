# Design: neo-delegate-adapter-notfound (Wave-2 child C13)

## Root cause (Neo-vs-Legacy, diagnostic-pinned)
An IL method bound to a CLR delegate type (Action<>/Func<>) whose parameter shape has NO
registered per-arity adapter (DelegateManager.FindDelegateAdapter -> dummyAdapter fallback)
is wrapped in a `DummyDelegateAdapter`. This is identical on Legacy and Neo (confirmed: 4
Dummies created for DelegateTest14 on BOTH modes; the registration set in helper.cs covers
int/bool/etc. but NOT string, string+Object, string->string).

The divergence is the delegate **Invoke** callvirt:
- Legacy intercepts `cm.IsDelegateInvoke && this is IDelegateAdapter` in the generic
  callvirt/call CLR dispatch (`ILIntepreter.Register.cs:3005-3030`) and calls
  `IDelegateAdapter.ILInvoke` directly, NEVER reaching the autogen CLR Invoke binding ->
  CheckCLRTypes is bypassed; the Dummy's ILInvoke runs the IL target.
- Neo has this interception in the `Callvirt_IL` case (`ILIntepreter.Neo.cs:3688-3756`)
  but NOT in `Callvirt_CLR`. A CLR delegate type's Invoke (Action<string>.Invoke) is
  lowered to `callvirt.clr` (JIT-confirmed: `callvirt.clr ... Action\`1[...].Invoke`), so
  the Neo Callvirt_CLR case dispatched straight to `ResolveNeoCallvirtCLRTarget` +
  `InvokeNeoClrMethod` -> autogen `Invoke_0_Neo` (or reflection CLRMethod.Invoke) ->
  `CheckCLRTypes(IsDelegate)` -> `GetConvertor` -> `DummyDelegateAdapter.NativeDelegateType`
  -> `ThrowAdapterNotFound` (KeyNotFoundException).

Diagnostic proof (CheckCLRTypes IsDelegate branch print):
- Legacy DelegateTest14: the branch is NEVER entered (interception skips it). PASS.
- Neo DelegateTest14: branch entered with obj=DummyDelegateAdapter. THROW.

ONE root cause for all 3 tests (all call Invoke on a CLR Action/Func via callvirt.clr).
Distinct from C1: C1 = a REAL adapter is found but cast to the CLR delegate type fails
(InvalidCastException). C13 = the adapter lookup yields a Dummy and the CLR-delegate-type
Invoke dispatches to the autogen binding instead of the adapter.

## The fix (Neo-gated, single file `ILIntepreter.Neo.cs`)
Add a focused interception at the TOP of the `Callvirt_CLR` case (after the IL-VT array
guard, before `ResolveNeoCallvirtCLRTarget`): when `targetMethod.IsDelegateInvoke` AND the
delegate `this` (read at `mStack[*(int*)targetBase]`, matching the Callvirt_IL handler's
offset-0 convention) is a `DummyDelegateAdapter`, route to the adapter's
`NeoRunDelegateTargetOnThis` (IL target, same-frame fast path) or `NeoInvokePublic`
(non-IL target) + the multicast next-chain, mirroring the Callvirt_IL delegate handler and
Legacy's ILInvoke. The autogen binding / CheckCLRTypes is bypassed; the Dummy's IL target
runs directly.

### Why Dummy-only (the load-bearing discriminator)
A REAL adapter (MethodDelegateAdapter / FunctionDelegateAdapter) has a registered converter
(`RegisterMethodDelegate<T>` / `RegisterFunctionDelegate<T>` each call
`RegisterDelegateConvertor<...>(defaultConverter)`), so the existing GetConvertor path
ALREADY serves it -- HEAD proves this (e.g. NeoStep19_ClosureOverThis: the `this` reaching
CheckCLRTypes is a real `FunctionDelegateAdapter<ILTypeInstance,int>`; GetConvertor returns
a real Func; it works). Intercepting a real adapter is not only unnecessary, it is HARMFUL:
`NeoRunDelegateTargetOnThis` performs the D2 rebind (`mStack[thisIdx] = instance`,
DelegateAdapter.cs-side helper at ILIntepreter.Neo.cs:961-966), which overwrites the
delegate `this` mStack slot with the bound ILTypeInstance. A later delegate Invoke on the
same (reused) frame slot then reads the ILTypeInstance instead of the adapter ->
CheckCLRTypes casts ILTypeInstance to IDelegateAdapter -> InvalidCastException. This is
exactly the NeoStep19 regression the first (intercept-all) variant introduced. Restricting
the interception to `DummyDelegateAdapter` (the only case where GetConvertor throws) leaves
the real-adapter path byte-identical to HEAD -- zero regression risk -- while fixing the
Dummy path.

### Why not register the missing adapters (string / string+Object / Func<string,string>)
Whack-a-mole: any unregistered parameter shape triggers the same KeyNotFound. Legacy does
NOT need these registrations because it intercepts Invoke. The interception is the correct,
general, Legacy-parity fix.

## Decision log
- D1 (intercept ALL DelegateAdapters in Callvirt_CLR) REJECTED: corrupts reused mStack
  slots via the D2 rebind for real adapters (NeoStep19_ClosureOverThis regression,
  diagnostic-pinned: obj=FunctionDelegateAdapter on HEAD vs obj=ILTypeInstance with the
  intercept-all variant). The real-adapter path already works via GetConvertor.
- D2 (Dummy-only interception in Callvirt_CLR) CHOSEN: minimal, safe, fixes the 3 C13
  tests, leaves real adapters on the proven HEAD path.
- D3 (factor the Callvirt_IL delegate block into a shared helper) REJECTED: it needlessly
  touches working code (Callvirt_IL), and the Callvirt_CLR arm must be Dummy-only while
  Callvirt_IL intercepts all adapters -- they cannot share one helper without a flag. The
  Callvirt_IL handler is left byte-untouched.
- D4 (Call_Redirect delegate-Invoke): not observed in the 3 failing tests; the full smoke
  will reveal if any delegate-Invoke arrives via `call` (not callvirt); deferred.

## Scope / out-of-scope
- IN: the Neo `Callvirt_CLR` case delegate-Invoke interception (Dummy-only).
- OUT: Callvirt_IL (untouched), Call_Redirect delegate-Invoke (deferred), the C1 real-adapter
  cast failures (different cluster), registering per-arity adapters (wrong fix).
