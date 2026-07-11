## Why

Neo mode (Steps 1-18 shipped) has **no delegate support**. `ldftn` and `ldvirtftn`
are absent from `ExecuteNeo` (they hit the catch-all `NotImplementedException`),
and the `Newobj` arm throws `NotImplementedException("Neo Newobj delegate is not
implemented")` for any IL delegate type. As a result, every C# idiom involving
delegates -- `Action a = Foo;`, `Func<int,int> f = Bar;`, `list.ForEach(...)`,
event handlers, lambdas closing over an IL instance, `+=`/`-=` multicast -- fails
under Neo. Delegates are a prerequisite for Step 20 (async/await continuations)
and are pervasive in real hot-update code (UI callbacks, LINQ-style APIs,
`List.ForEach`, task continuations). The machinery to land them is already in
place: `DelegateAdapter`/`DelegateManager` exist (Legacy, `StackObject`-wired),
`CLRRedirectionDelegateNeo` was laid as the Step 9 foundation, and the Neo call
convention (Step 8), VTable (Step 10), and CLR value-type reader/writer
(`ReadNeoValueType`/`WriteNeoValueType`, Step 13b/area4) are all shipped.

## What Changes

- **`ldftn` opcode in `ExecuteNeo`**: resolve the static `IMethod` via
  `AppDomain.GetMethod(Operand2)`, store it into mStack at the dest ref slot,
  write the mStack index into the dest byte offset. (An `IMethod` is a CLR object
  -> a Neo ref slot, exactly like any reference-type local/temp.)
- **`ldvirtftn` opcode in `ExecuteNeo`**: read the `this` from `Register2`'s
  mStack slot, resolve the virtual-method override via `Type.GetVirtualMethod`
  (the Step 10 VTable path), store the resolved `IMethod` into mStack + write the
  index to the dest byte offset.
- **Delegate `Newobj` arm in `ExecuteNeo`**: replace the
  `NotImplementedException("Neo Newobj delegate is not implemented")`. Read the
  bound `this` (from `Register2`'s mStack slot) and the `IMethod` (from
  `Register3`'s mStack slot), build the `DelegateAdapter` via
  `DelegateManager.FindDelegateAdapter` (caching on the `ILMethod` /
  `ILTypeInstance` exactly as Legacy does), store the adapter into the dest ref
  slot + write the index to the dest byte offset.
- **`DelegateAdapter.InvokeILMethod` adapted to the Neo calling convention**
  (CLR -> IL direction): build a Neo `byte*` frame + frame ref region, write the
  CLR args into the callee param region (the inverse of `CopyNeoCallArguments` +
  the Step 8 Call caller-side push), call `ExecuteNeo`, and read the return value
  back into a CLR object. This is the inverse of Step 8/9 IL -> CLR; it reuses
  `ReadNeoValueType`/`WriteNeoValueType` for value-type params/returns. The
  multicast `next`-chain works unchanged once the single-invoke path is correct.
- **Adversarial probe suite** `TestCases/NeoStep19Test.cs` (`NeoStep19_*`): 9+
  cases covering static delegate, delegate with return, instance-method delegate,
  virtual-method delegate (ldvirtftn / VTable slot), multicast `+=`, multicast
  `-=`, a delegate passed to a CLR method (the CLR -> IL `InvokeILMethod`
  callback via `List.ForEach(action)`), ref/out params, and closure over an IL
  instance's `this`.

Non-goals (stays NIE-tagged, deferred to Step 20 or follow-ups): delegate
dynamic invocation (`DynamicInvoke`), open-instance delegates constructed from
static methods, generic delegate types beyond `Action<>`/`Func<>` arity <= 4/5,
and the cross-binding-adaptor field/method reads off a caught IL exception
(follow-up `[NEO-IL-EX-FIELDACCESS]`, independent).

## Capabilities

### New Capabilities
<!-- None. Delegates are a modification to the existing dispatch capability. -->

### Modified Capabilities
- `neo-dispatch`: add Neo delegate creation and invocation to the dispatch
  capability. New requirements cover the `ldftn`/`ldvirtftn` opcode handlers
  (IMethod into a Neo ref slot, with VTable slot resolution for `ldvirtftn`),
  the delegate `Newobj` arm (DelegateAdapter bound from `this` + `IMethod`), and
  the `DelegateAdapter.InvokeILMethod` Neo calling convention (CLR -> IL
  callback). The Step 19 delegation is built ON TOP of the existing Step 8 Call
  convention + Step 10 VTable + Step 9 `CLRRedirectionDelegateNeo`, so the delta
  is additive (new requirements + scenario deltas on the existing
  `neo-dispatch` requirements).

## Impact

- **`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`** -- new
  `case OpCodeREnum.Ldftn` and `case OpCodeREnum.Ldvirtftn` arms; the delegate
  branch in the `case OpCodeREnum.Newobj` arm (replace the Step 19 NIE). All
  Neo-only (Legacy `ExecuteR` is the reference, NOT modified).
- **`ILRuntime/Runtime/Intepreter/DelegateAdapter.cs`** -- the
  `InvokeILMethod`/`ILInvokeSub`/`ClearStack`/`BeginInvoke` family is currently
  `StackObject`-wired (Legacy). Add a Neo calling-convention path under
  `#if ENABLE_NEO_MODE` (a `NeoInvokeILMethod` helper + a Neo frame build + the
  return read), reusing the existing `next`-chain multicast logic (which is
  engine-agnostic). Legacy paths kept byte-identical.
- **`TestCases/NeoStep19Test.cs`** (new) -- 9+ adversarial probes.
- Possibly **`ILRuntime/Runtime/Intepreter/ILIntepreter.cs`** -- the `Run`
  Neo entry shim currently handles only no-arg static methods (Step 6). The
  `InvokeILMethod` Neo path may reuse the same frame-build shape; if a richer
  public re-entry (instance method + args) is needed, it lands here under
  `#if ENABLE_NEO_MODE`.
- No change to the JIT compiler (`ldftn`/`ldvirtftn`/`Newobj` lowering already
  resolve the method token via `InitializeFunctionParam` and stamp the right
  register operands -- confirmed against `JITCompiler.cs:2377-2396` and
  `:1845-1878`). The runtime arms are the only engine-side gap.
- Regression risk: MEDIUM. The new opcodes are additive (they currently throw
  NIE); the `Newobj` delegate branch is gated by `IsDelegate` (does not touch
  the shipped IL-VT / IL-ref / CLR newobj paths). The `DelegateAdapter` change
  is `#if ENABLE_NEO_MODE`-gated (Legacy byte-identical). Gate: full `NeoStep`
  smoke (130/130 baseline) + Legacy 518/519 for any shared-engine edit. The
  biggest design risk is the `InvokeILMethod` Neo frame build (the one untested
  corner) -- probe BEFORE finalizing the frame shape (the area4 / opt-harden-2
  dump-gated discipline).
