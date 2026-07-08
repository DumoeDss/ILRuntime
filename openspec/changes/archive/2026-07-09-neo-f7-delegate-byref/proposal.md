## Why

F-7 (NEO-DELEGATE-REFOUT): a delegate whose target signature carries a `ref`/`out`
parameter, invoked from IL via Neo (`del(ref v)`), throws
`ArgumentOutOfRangeException` because the byref Ref Slot `(objectIndex, offset)` is
destroyed in two places before the target runs -- the delegate-Invoke branch re-reads
the caller's correctly-marshaled args through `ReadNeoDelegateInvokeArgs` (which
funnels every arg into an `object[]`, reading only the 4-byte `objectIndex` half and
dropping the offset), then runs the target on a SEPARATE pooled interpreter whose
`frameBase`/`mStack` are unrelated to the caller's frame, so even a preserved byref
could not write back. Confirmed 3/3 FAIL on HEAD `462e635d` (ref int @
`ILIntepreter.Neo.cs:3726`, out int @ `:3636`, ref string @ `:3862`). This is a
TRUE-COMPLETION of the Neo delegate story (Step 19 left the byref-typed delegate param
deferred).

## What Changes

- Add a **same-frame delegate-Invoke fast path** to the `Callvirt_IL`
  `IsDelegateInvoke` branch (`ILIntepreter.Neo.cs:2443-2454`): when the delegate
  target is an IL method, run it on the CALLER's interpreter via the existing
  `InvokeNeoCallTarget` (which calls `ExecuteNeo` on `this`, same `frameBase`/
  `mStack`) instead of routing through `DelegateAdapter.NeoInvokePublic` ->
  `NeoInvokeSub` (the separate pooled interpreter + `object[]` funnel).
- The fast path reuses the args ALREADY marshaled into `targetBase` by
  `CopyNeoCallArguments` (`:2431`) -- which already preserves the byref Ref Slot
  correctly (the JIT-built `NeoCallParamMap` flags the byref param via
  `PrimitiveByRefSrc` for the delegate-Invoke callvirt, same as any call). No
  re-read through `object[]`, so no byref destruction.
- Write the bound `instance` into the target's slot 0 (the `this`) before running
  the target, mirroring `NeoInvokeSub`'s `WriteNeoCallSlot(paramInfos[0], ...,
  instance)` (`DelegateAdapter.cs:1063`).
- Propagate the `ref`/`out` write-back to the caller's frame via the existing
  `CopyNeoCallThisBack` machinery (`ILIntepreter.Neo.cs:543`), with the
  pre-call byref-source snapshot, exactly as the normal `Call_IL` path does
  (`:2166-2189`). This makes the caller observe the callee's mutation.
- Walk the multicast `next`-chain (singlecast is the common case; multicast with a
  byref param runs each target in order, last write-back wins for the caller's
  cell, matching Legacy `ILInvokeSub` semantics).
- The plain-primitive delegate callback shapes (no byref -- `NeoStep19_*`,
  `List.ForEach(Action<T>)`) SHALL stay byte-identical on the fast path (the fast
  path is a strict superset of the current path for IL targets; the separate-
  interpreter `NeoInvokeSub` path remains for CLR->IL callbacks where there is no
  IL caller frame).
- Mark the F-7 requirement in `specs/neo-dispatch/spec.md` (currently "NOT
  satisfied ... target of a future change") SATISFIED by this change.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `neo-dispatch`: The "Neo delegate-Invoke with a byref/out param SHALL marshal the
  byref and propagate the write-back" requirement (currently recorded as an
  accepted-known limitation / future fix) is now SATISFIED via the same-frame
  delegate-Invoke fast path. The four scenarios (ref int, out int, ref reference,
  plain-primitive regression guard, Legacy-neutrality) become enforced behavior.

## Impact

- **Code**: `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (the
  `Callvirt_IL` `IsDelegateInvoke` branch ~`:2443-2454` -- the fast path; possibly a
  small helper to run the IL target + write-back on `this` interpreter).
  `DelegateAdapter.cs` is read-only for this change (its `NeoInvokePublic`/
  `NeoInvokeSub` remains for the CLR->IL callback path where no IL caller frame
  exists).
- **Regression risk**: MODERATE. The fast path touches the green Step-19 delegate
  hot path. `NeoStep19_*` (the plain-primitive delegate shapes) is the regression
  guard -- it SHALL stay green. The separate-interpreter path is untouched (the
  CLR->IL `List.ForEach` callback keeps working).
- **Tests**: add byref probes to `TestCases/NeoStep19Test.cs` (ref int, out int,
  ref string, + a multicast-with-byref probe), renaming/clarifying the existing
  misleadingly-named `NeoStep19_RefOutParam` (which currently uses a plain int).
- **No public API change**. Neo-only (`#if ENABLE_NEO_MODE`); Legacy-neutral
  (Legacy's delegate callback uses `StackObject[]` + `ILInvokeSub`, which already
  handles byref params -- no F-7 gap there).
