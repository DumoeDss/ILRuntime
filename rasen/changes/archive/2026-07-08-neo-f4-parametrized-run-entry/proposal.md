## Why

Under `ENABLE_NEO_MODE`, the public host re-entry path `AppDomain.Invoke(method,
instance, params)` -> `ILIntepreter.Run(ILMethod, instance, object[])` is a
Step-6 PARAMETERLESS-ONLY shim: it marshals NEITHER `instance` (no slot-0 `this`
push) NOR `p` (no param-region population) into the Neo callee frame, and it
returns ALL non-void results via `NeoBoxReturnValue`, which boxes PRIMITIVES only
and reads raw bytes for a reference-type return. This leaves two gaps open:

- **F-4 path #3:** invoking an IL instance METHOD off a caught exception via
  `appdomain.Invoke(instanceMethod, caughtException)` runs the override with no
  `this` -> `NullReferenceException` on the first field access. (F-4 paths
  #1/#2/#4 shipped in `neo-f4-reflection-on-neo`; path #3 was SEQUENCED to this
  follow-on.) This is the last blocked caught-exception reflection path.
- **F-12 / NEO-RUN-REF-RETURN:** a parameterless IL method that returns a
  REFERENCE type (string/object/ILType) invoked via `appdomain.Invoke` returns
  garbage -- the raw low 4 bytes of the return slot instead of the boxed
  reference. Surfaced by the Step-25-S2 `WrapEchoRef` cell.

`neo-async-movenext-fix` routed truly-async resumption AROUND `Run` (via
`DriveMoveNextCore` + a pooled interpreter calling `ExecuteNeo` directly), so
`Run` itself is still the wide-open parametrized gap on HEAD `cb07444d`.

## What Changes

- **Marshal `object[] p` into the Neo callee param region** in `ILIntepreter.Run`
  under `ENABLE_NEO_MODE`, mirroring `DelegateAdapter.NeoInvokeSub`'s per-parameter
  `WriteNeoCallSlot(paramInfos[i], frameBase, mStack, frameRefBase, arg)` loop
  (the Step-19 CLR->IL callback arg-marshal, the established pattern).
- **Push `instance` as the slot-0 `this`** for an instance method
  (`method.HasThis`): unwrap `CrossBindingAdaptorType` -> `ILInstance` (matching
  the Legacy `Run` arm), then `WriteNeoCallSlot(paramInfos[0], ..., instance)`
  before the param loop.
- **Reserve the full callee frame reference region** (`nf.TotalRefSize` mStack
  slots, not just the return ref slots) and **zero the locals primitive region**
  + ref-init unassigned locals, mirroring `NeoInvokeSub` (`:1038-1057`), so the
  parametrized frame matches what `ExecuteNeo` expects.
- **Extend the return-read** in `Run`'s Neo arm to the reference-return shape:
  branch on `method.ReturnType` -- for a non-value-type non-void return, read
  `*(int*)retDst` as the mStack index and return `mStack[retIdx]` (the
  `NeoInvokeSub:1087-1093` reference-return logic); keep `NeoBoxReturnValue` for
  value-type returns. This closes F-12.
- All edits are **Neo-only** (`#if ENABLE_NEO_MODE` inside the shared `Run`
  entry); the Legacy `Run` arm already marshals `instance`+`p` and returns
  correctly, so Legacy is byte-identical.

## Capabilities

### New Capabilities

_(none -- this extends the existing Neo dispatch/invocation contract)_

### Modified Capabilities

- `neo-dispatch`: the host -> IL re-entry invocation contract (`AppDomain.Invoke`
  -> `ILIntepreter.Run`) under Neo mode. Currently the spec covers VTable
  dispatch for IL->IL calls; this adds the requirement that the shared `Run`
  entry marshal `instance` + `p` into the Neo frame and return reference-type
  results correctly, so an IL instance method invoked from the host runs with
  the right `this` and returns a boxed reference.

## Impact

- **Source (Neo-only):** `ILRuntime/Runtime/Intepreter/ILIntepreter.cs:104-137`
  (the `Run` Neo arm -- the only behavior change). Reuses the existing
  `DelegateAdapter.WriteNeoCallSlot` helper (made accessible) +
  `CompiledFrame.{ParamInfos,TotalRefSize,LocalsPrimitiveSize,LocalInfos,
  LocalIsReference,ReturnPrimitiveSize,ReturnRefCount}` (all already public on
  the struct) + `ILIntepreter.NeoBoxReturnValue` (unchanged, primitive-only).
- **Legacy:** byte-identical (the Legacy `Run` arm at `:139-157` is untouched).
- **Callers:** `AppDomain.Invoke(IMethod, object, params object[])`
  (`AppDomain.cs:1664-1681`) -- the public host re-entry -- now works for IL
  instance methods and reference-returning methods. The Step-19
  `DelegateAdapter.NeoInvokeSub` path is unaffected (it already marshals
  correctly; this change only brings the shared `Run` arm up to parity).
- **Regression surface:** small and Neo-gated. The full `NeoStep` smoke
  (currently 221/0/0 per `neo-f4-reflection-on-neo`) is the regression gate;
  Legacy plain-`Debug` build = 0 errors confirms Legacy-neutrality.
- **Deferred-items resolution:** closes the F-4 row (path #3, the last open
  path), the F-12 / NEO-RUN-REF-RETURN row, and the STEP-25-PARTIAL "fuller Run
  entry" prerequisite recorded on the Step-25-S2/S3 parametrized-Run notes.
