## Why

The Neo interpreter (`ExecuteNeo` in `ILIntepreter.Neo.cs`) has no working
exception handling: the opcodes `Throw`, `Leave`/`Leave_S`, `Endfinally`,
`Rethrow` (and `Endfilter`) fall through to the switch `default` arm and throw
`NotImplementedException(...) (Step 6)`. Any IL method body containing a
try/catch/finally therefore cannot run under Neo. Step 14 ports the Legacy
exception semantics (already proven in `ExecuteR` / `ILIntepreter.cs`) into the
Neo `byte*` frame model, completing structured-exception support for IL code on
the new object model. This is a prerequisite for Step 20 (async) and is on the
critical path for general IL code to run under Neo.

## What Changes

- Implement the `Throw` arm in `ExecuteNeo`: read the exception object from the
  throw register's ref slot and `throw` it into the existing outer C# try-catch.
- Implement the `Leave` / `Leave_S` arm: jump to `ip->Operand` while honoring any
  enclosing finally block (route through `FindExceptionHandlerByBranchTarget`,
  set `finallyEndAddress`, like Legacy `ILIntepreter.Register.cs:2765`).
- Implement the `Endfinally` arm: if `finallyEndAddress < 0` re-throw the
  in-flight exception (`throw lastCaughtEx`); otherwise jump to the leave target
  / next outer finally (Legacy `ILIntepreter.Register.cs:2785`).
- Implement the `Rethrow` arm: `throw lastCaughtEx`.
- Complete the `isCatch` branch in the outer C# catch block
  (`ILIntepreter.Neo.cs:2029-2039`): on catch-handler entry, restore
  `mStack.Count = frameRefBase + totalRefSize` (already done) AND write the
  caught exception object into the catch handler's pre-allocated ref slot
  (currently `TODO: write exception object into the catch handler's slot (Step 14)`).
- Fix cross-frame propagation: today, when a callee returns
  `unhandledException = true`, the `Call`/`Newobj`/`Callvirt` arms do
  `return null` immediately (`ILIntepreter.Neo.cs:1396-1397, 1433-1434, ...`),
  bypassing this caller frame's enclosing try/catch and leaking the frame +
  mStack reservation (state corruption). Step 14 makes an unhandled exception
  in a callee be caught by the caller's outer try-catch and matched against the
  caller's `ehs`, mirroring Legacy (`ILIntepreter.cs:4770-4779` frame-pop loop).
- Frames-stack discipline on unhandled: do NOT pop the frame in the per-call
  cleanup when an exception is propagating; `HandleException` finds the matching
  frame and batch-pops intervening frames.
- `Endfilter` / IL `filter` blocks: NOT in scope (see design Non-Goals).
- All new runtime code stays behind `#if ENABLE_NEO_MODE`. **Legacy
  (`ExecuteR`, `ILIntepreter.cs` `HandleException`/`GetCorrespondingExceptionHandler`)
  is NOT modified** -- the core handler-matching logic is already shared and
  reused as-is.

### Throw / newobj dependency finding (key input for test design)

`throw new System.Exception(...)` is **NOT yet testable** under Neo, because:

- The Neo `Newobj` arm (`ILIntepreter.Neo.cs:1411-1413`) throws
  `NotImplementedException("Neo Newobj CLR type is not implemented (Step 9)")`
  for any non-IL target. `System.Exception` (and `DivideByZeroException`,
  `NullReferenceException`, etc.) are CLR types, so `new T(...)` for them fails.
- IL-type `newobj` IS available (`1411` IL branch works).

Therefore the Step 14 test suite must throw exceptions that do **not** require CLR
newobj. Testable throw sources:

1. **Implicit CLR exceptions** (no newobj in the IL): `int x = 1 / 0;` lowers to
   a `Div` opcode and the C# `DivideByZeroException` is raised by the host runtime
   inside the execute loop, caught by the outer try-catch and matched by
   `GetCorrespondingExceptionHandler`. This is the primary, robust test vector
   (already used by `NeoStep6Test` / `NeoStep12bTest`).
2. **NullReferenceException** from a null ref-slot dereference (e.g. `Ldfld` on a
   null IL instance throws NRE inside `GetNeoILInstance`, `ILIntepreter.Neo.cs:2088`).
3. **`throw` of an IL-constructed exception object** is OUT of reach this step
   (needs CLR newobj; deferred to the Step that adds CLR newobj). Likewise
   `throw new T(...)` for any CLR exception type.

Validation cases (from `neo-implementation-steps.md` Step 14) are covered by
source 1/2:
- basic try-catch (catch a DivideByZeroException from `1/0`, access ex object);
- try-finally (finally always runs -- trigger with `1/0` inside try);
- nested try-catch (inner `1/0` caught by inner handler; outer not entered);
- cross-method propagation (callee does `1/0`, caller catches);
- catch-object access (read `e.Message` / `e.GetType()` in catch -- CLR method
  calls on a CLR exception object work via Step 9 redirection / reflection).

## Capabilities

### New Capabilities
- `neo-exceptions`: try/catch/finally/throw handling for IL code in the Neo
  interpreter (`ExecuteNeo`): throw, catch-handler entry, mStack restore,
  exception-object storage, Leave/Endfinally/Rethrow, nested + cross-frame
  propagation, finally-guaranteed execution.

### Modified Capabilities
(none -- `neo-exceptions` is a fresh capability; the Legacy exception spec is
untouched.)

## Impact

- **Code:** `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`
  (outer try-catch completion + new `Throw`/`Leave`/`Endfinally`/`Rethrow`
  arms + cross-frame propagation fix in `Call`/`Newobj`/`Callvirt` arms). The
  shared `HandleException` / `GetCorrespondingExceptionHandler` /
  `FindExceptionHandlerByBranchTarget` in `ILIntepreter.cs` are REUSED, not
  modified.
- **Tests:** new `TestCases/NeoStep14Test.cs` (ASCII), covering the five cases
  above using only no-newobj throw sources.
- **Regression risk:** HIGH -- exception handling touches the core execute loop
  and the frames/mStack state shared by ALL calls. A bug here can corrupt
  frame/mStack state for every call. Gate = full NeoStep smoke (was 49/49 after
  Step 13; new NeoStep14 cases add, NO existing case regresses). Note: some
  previously-failing tests that use try/catch may now turn green -- expected
  improvement, not regression.
- **Out of scope:** async exceptions (Step 20), IL `filter` blocks / `Endfilter`,
  CLR `newobj` (separate Step 18), stack-overflow guard (Step 26).
