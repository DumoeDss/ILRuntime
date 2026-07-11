## 1. Implement the parametrized Run Neo arm

- [x] 1.1 In `ILRuntime/Runtime/Intepreter/ILIntepreter.cs`, rewrote the
  `#if ENABLE_NEO_MODE` arm of `Run(ILMethod method, object instance, object[] p)`
  to mirror `DelegateAdapter.NeoInvokeSub` (`DelegateAdapter.cs:1006-1118`):
  reserve `nf.TotalRefSize` frame ref slots at a recorded `frameRefBase`
  (`ILIntepreter.cs:156-158`); zero the locals primitive region
  (`InitBlock(frameBase + nf.ParamPrimitiveSize, 0, nf.LocalsPrimitiveSize)`,
  `:139-140`); ref-init unassigned local ref slots (`:144-151`).
- [x] 1.2 Push `instance` as slot-0 `this` when `method.HasThis`: unwrap
  `CrossBindingAdaptorType` -> `ILInstance` + null-check (matching the Legacy arm),
  then `DelegateAdapter.WriteNeoCallSlot(paramInfos[0], ...)`, advance `argIdx = 1`
  (`:163-173`).
- [x] 1.3 Marshal `p` into the param region in a loop over
  `method.ParameterCount` via `DelegateAdapter.WriteNeoCallSlot` (`:176-181`).
- [x] 1.4 Reserve the return ref region (`nf.ReturnRefCount` mStack slots) at a
  `retRefBase` AFTER the frame ref region; `retDst = newEsp`; call `ExecuteNeo`
  (`:183-191`).
- [x] 1.5 Return-read with the type-discriminated branch: reference return ->
  `mStack[*(int*)retDst]` (null for idx < 0); value-type return ->
  `NeoBoxReturnValue`; void -> null. Kept the `mStack.RemoveRange(mStackBase, ...)`
  teardown (`:193-202`).
- [x] 1.6 `DelegateAdapter.WriteNeoCallSlot` is `internal static` in the same
  assembly/namespace -- no visibility change needed (called as
  `DelegateAdapter.WriteNeoCallSlot(...)`).

## 2. Adversarial probe: F-4 #3 (caught-exception instance-method re-entry)

- [x] 2.1 Added IL-side targets to `TestCases/NeoStep14Test.cs`: `MyEx.GetCode()`
  (an instance method that reads the IL-declared `Msg` field -- the override that
  must run with the right `this`) + `BuildF4Ex()` (constructs a `MyEx` with
  `Msg="f4code"` via the default ctor + direct field assignment -- the workaround
  for the known `new MyEx(string)` ctor string-arg mis-route).
- [x] 2.2 The F-4 #3 GATE runs HOST-SIDE in
  `NeoF4ParamRunCheck.Run` (CLI hook `NeoF4ParamRun`): the host invokes
  `BuildF4Ex` to obtain the instance, then invokes the instance `GetCode` via the
  PUBLIC `appdomain.Invoke(getCode, instance)` -> `Run` path -- asserting the
  result == 6 (NOT NRE). See §6 for why this is host-side, not an IL test method.
- [x] 2.3 FAIL-on-HEAD -> PASS proved: with the OLD parameterless-only Run shim,
  `GetCode Invoke threw` (the override ran with no `this` -> field-access fault);
  with the parametrized-Run arm, `GetCode returned 6`.

## 3. Adversarial probe: F-12 (reference-type return)

- [x] 3.1 Added `EchoRef()` (a parameterless static IL method returning the
  reference-type string "hi") to `TestCases/NeoStep14Test.cs`. The F-12 GATE runs
  HOST-SIDE in `NeoF4ParamRunCheck.Run`: `appdomain.Invoke(echoRef, null)` -> the
  boxed string "hi" (NOT the raw mStack index as an int). FAIL-on-HEAD -> PASS
  proved: OLD shim `expected string "hi", got Int32:0`; parametrized-Run arm ->
  `boxed string "hi"`.

## 4. Regression + Legacy-neutrality

- [x] 4.1 `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` =
  0 errors; `dotnet build TestCases/TestCases.csproj -c Debug` = 0 errors.
- [x] 4.2 `NeoStep14` filter: 21/0/0 (19 original + `EchoRef` + `BuildF4Ex`,
  which the harness discovers as public-static-parameterless test methods and
  which PASS by completing without throwing). The `NeoF4ParamRun` host check is
  the actual assertion gate (2/2 cells PASS).
- [x] 4.3 Full `NeoStep` smoke: 223/0/0 (was 221; +2 for `EchoRef` +
  `BuildF4Ex`). No regressions.
- [x] 4.4 Legacy-neutrality: plain `Debug` build of the dev subset = 0 errors.
  The Legacy `Run` arm is byte-identical (the parametrized extension is
  `#if ENABLE_NEO_MODE`-gated); `NeoF4ParamRunCheck.cs` is
  `#if ENABLE_NEO_MODE && DEBUG`-gated; the CLI hook is inside the existing Neo
  gate.

## 5. Deferred-items resolution

- [x] 5.1 Update `.trae/documents/neo-deferred-items.md`: mark F-4 path #3
  RESOLVED; mark F-12 / NEO-RUN-REF-RETURN RESOLVED; mark the STEP-25-PARTIAL
  "fuller Run entry" prerequisite RESOLVED. ALSO RECORDED the NEW pre-existing gap
  surfaced in §6 as F-13 / NEO-NESTED-RUN-EXECUTENEO (nested `domain.Invoke`
  from within an in-flight `ExecuteNeo` corrupts the outer frame's instruction
  pointer -- blocks IL-side probes that re-enter via `Run`; the host-side check
  is the workaround).

## 6. Implementation pivot: host-side check (NOT IL test methods) -- and the new
       pre-existing gap it surfaced

The design specified the F-4 #3 + F-12 probes as IL test methods in
`NeoStep14Test.cs`. During apply this turned out to be BLOCKED by a PRE-EXISTING
Neo re-entrancy bug (NOT the parametrized-Run change):

- Driving `appdomain.Invoke(...)` from WITHIN an IL method nests a second
  `Run`/`ExecuteNeo` inside an in-flight `ExecuteNeo`. The OUTER method's
  instruction pointer then runs off the end of its body -> a garbage opcode
  (`NotImplementedException: Neo: opcode <random> not yet implemented`, with the
  outer method's `ipOff` past its body length). Reproduced with the OLD
  parameterless-only Run shim (identical failure), so it is NOT this change.
- This affects BOTH the F-4 #3 shape (the IL probe's catch calls
  `appdomain.Invoke(getCode, e)`) and the F-12 shape (the IL probe's catch calls
  `appdomain.Invoke(echoRef)`). The F-4 #3 IL probe appeared to "PASS" only
  because its outer `catch(Exception){return -97;}` swallowed the corruption and
  the method completed -- a false positive (it returned -97, not 9).

The parametrized-Run machinery itself is CORRECT (proven by the host-side check,
which invokes `Run` exactly once per cell with no nesting). So the gates were
moved HOST-SIDE: `NeoF4ParamRunCheck.Run(appdomain)` (CLI hook `NeoF4ParamRun`)
drives `appdomain.Invoke` from CLR -- the architecturally correct shape for a
HOST -> IL re-entry (the F-4 #3 scenario is literally "host catches an IL
exception, then re-invokes"). Each cell invokes `Run` once: no nested
`ExecuteNeo`, no corruption.

Files added/changed for the pivot:
- `ILRuntime/Runtime/Intepreter/RegisterVM/NeoF4ParamRunCheck.cs` (NEW): the
  2-cell host-side self-check.
- `ILRuntimeTestCLI/Program.cs`: the `NeoF4ParamRun` exact-match CLI hook (inside
  the existing Neo gate).
- `TestCases/NeoStep14Test.cs`: `MyEx.GetCode`, `EchoRef`, `BuildF4Ex` (IL-side
  targets the host check invokes) + a comment block documenting the pivot.

The nested-`ExecuteNeo`-from-`Run` corruption is recorded as a NEW pre-existing
gap (task 5.1) -- separate change. It does NOT block the parametrized-Run
TRUE-COMPLETION (single-level host re-entry works).
