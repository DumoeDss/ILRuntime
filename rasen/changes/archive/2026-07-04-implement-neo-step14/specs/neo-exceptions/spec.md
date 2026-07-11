# neo-exceptions

Structured exception handling for IL code executed by the Neo register VM
(`ENABLE_NEO_MODE`): `throw`, `catch`, `finally`, `leave`, and `rethrow`, with
correct nesting and cross-frame propagation. This capability is implemented in
`ExecuteNeo` (`ILIntepreter.Neo.cs`) and reuses the shared handler-matching
engine (`HandleException` / `GetCorrespondingExceptionHandler` /
`FindExceptionHandlerByBranchTarget` in `ILIntepreter.cs`) that Legacy
(`ExecuteR`) already uses; it does NOT modify Legacy or the shared engine.

This capability is distinct from `neo-dispatch` (vtable/interface) and
`neo-value-types`: it governs control-flow unwinding and per-frame mStack/frames
discipline on the Neo `byte*` frame model, not dispatch or value storage.

IL `filter` blocks and `endfilter` are explicitly out of scope (rare in C#;
remain `NotImplementedException`). CLR-typed `throw new T(...)` is bounded by the
availability of CLR `newobj` (a separate Step 18 concern); this capability is
specified and validated using exception sources that do not require newobj
(CLR-raised exceptions from arithmetic/null-deref, rethrow of a caught object).

## ADDED Requirements

### Requirement: Throw opcode executes under Neo

The `Throw` `ExecuteNeo` arm SHALL read the exception object from the throw
instruction's register-1 ref slot (the mStack index stored at
`frameBase + ip->Register1`) and throw it into the enclosing per-iteration C#
try-catch so that `HandleException` searches the current frame's exception
handler table (`method.ExceptionHandlerRegister`). Throwing a null exception
object (ref index `-1`) SHALL throw a CLR `NullReferenceException`.

#### Scenario: An exception thrown inside a try block is offered to the frame's handlers
- **WHEN** an IL method under `ENABLE_NEO_MODE` executes an instruction inside a
  `try` region that throws (for example `int x = 1 / 0;`, whose host-raised
  `DivideByZeroException` enters the outer catch), and the method has a matching
  catch handler
- **THEN** `HandleException` is invoked against the current frame's handler
  table with the throw address inside the try range, the handler is found, and
  execution jumps to the handler start -- not to the method's `default`/
  `NotImplementedException` path.

### Requirement: Catch-handler entry restores mStack and stores the exception object

`ExecuteNeo` SHALL, on entry to a catch handler (`isCatch == true` from
`HandleException`), truncate `mStack.Count` to `frameRefBase + totalRefSize`
(discarding any temporary ref slots pushed inside the faulted try region) AND
SHALL install the caught exception object into the catch handler's reserved
reference slot so the catch body can read the exception variable. The exception
object placed in the slot SHALL be the unwrapped inner exception when the
propagated object was an `ILRuntimeException` (matching Legacy behavior).

#### Scenario: The caught exception is reachable inside the catch body
- **WHEN** an IL method under `ENABLE_NEO_MODE` catches an `Exception e` thrown
  by `int x = 1 / 0;` and the catch body inspects `e` (for example tests
  `e is DivideByZeroException`)
- **THEN** the catch body observes the same exception instance that was thrown
  (the `is`/cast check succeeds), proving the exception object was correctly
  stored in the catch handler's ref slot.

#### Scenario: mStack does not leak across a caught exception
- **WHEN** an IL method under `ENABLE_NEO_MODE` repeatedly enters a try region
  that pushes temporary reference slots, throws, and catches
- **THEN** `mStack.Count` is restored to `frameRefBase + totalRefSize` on each
  catch entry and the method can be called many times without `mStack` growing
  without bound.

### Requirement: Leave executes enclosing finally blocks

The `Leave` / `Leave_S` `ExecuteNeo` arm SHALL jump to the leave target
(`ip->Operand`) but SHALL first divert into any finally block whose try range
encloses the leave instruction's address and is exited by the leave (determined
via `FindExceptionHandlerByBranchTarget`), recording the leave target in
`finallyEndAddress` so the subsequent `Endfinally` resumes at the leave target.
This matches Legacy `Leave` semantics and guarantees that a finally block always
runs when control leaves its try region, whether by normal control flow or by
leaving on account of a `return`/`break`/`continue`.

#### Scenario: finally runs on a normal Leave with no exception
- **WHEN** an IL method under `ENABLE_NEO_MODE` runs `int r = 0; try { r = 1; }
  finally { r += 10; }` (no exception is thrown)
- **THEN** the finally body executes and the method yields `11`, proving the
  `Leave` arm routed control through the finally before reaching the leave
  target.

### Requirement: Endfinally re-propagates or resumes correctly

The `Endfinally` `ExecuteNeo` arm SHALL behave according to why the finally was
entered. When the finally was entered because an exception is in flight
(`finallyEndAddress < 0`, set by `HandleException` for finally/fault), the arm
SHALL re-throw `lastCaughtEx` so the exception-propagation search resumes from
this frame. When the finally was entered because of a `Leave`
(`finallyEndAddress >= 0`), the arm SHALL jump to the recorded leave target
(routing through any further enclosing finally via
`FindExceptionHandlerByBranchTarget`) and reset `finallyEndAddress` to 0.

#### Scenario: try-finally runs the finally then propagates an uncaught exception
- **WHEN** an IL method under `ENABLE_NEO_MODE` runs `try { int x = 1 / 0; }
  finally { sideEffect(); }` with no enclosing catch in the same method
- **THEN** the finally body executes (`sideEffect` runs) and the
  `DivideByZeroException` continues to propagate (it is not swallowed by the
  absence of a catch).

#### Scenario: nested finally blocks run outermost on leave
- **WHEN** a `Leave` crosses both an inner and an outer finally region
- **THEN** both finally bodies run (inner then outer) before control reaches the
  leave target, via successive `Endfinally` diversions.

### Requirement: Rethrow re-raises the in-flight exception

The `Rethrow` `ExecuteNeo` arm SHALL throw the current frame's `lastCaughtEx`,
re-entering the outer catch so the exception propagates as if originally thrown
at the catch site.

#### Scenario: rethrow propagates to an outer handler
- **WHEN** an IL method under `ENABLE_NEO_MODE` catches an exception, executes
  `rethrow`, and an outer try/catch surrounds the inner
- **THEN** the outer catch observes the same exception instance and the inner
  catch's post-rethrow code does not run.

### Requirement: Cross-frame propagation offers the exception to caller handlers

The Neo caller frame SHALL, when a callee IL method invoked from a `Call` /
`Newobj` / `Callvirt` /
`Callvirt_Interface` / `Callvirt_CLR` arm finishes with an unhandled exception
(the callee's `ExecuteNeo` set `unhandledException = true`), the CALLER frame
SHALL offer that exception to the caller's own exception handler table rather
than silently unwinding. Concretely, the caller SHALL re-enter its outer
try-catch with the pending exception object and the call-site instruction
address, so `HandleException` searches the caller's `method.ExceptionHandlerRegister`
at the call address (which lies inside any caller-side enclosing try range). The
caller SHALL NOT execute a `return null` that bypasses both the caller's
handlers and the caller's frame/mStack cleanup.

#### Scenario: an exception thrown in a callee is caught in the caller
- **WHEN** an IL method `Caller` under `ENABLE_NEO_MODE` contains
  `try { Callee(); return -1; } catch { return 9; }` and `Callee` executes
  `int x = 1 / 0;` (no local catch)
- **THEN** `Caller` returns `9`, proving the exception propagated across the
  call boundary and was matched against `Caller`'s handler table.

#### Scenario: a caller-side finally runs when a callee throws
- **WHEN** `Caller` has `try { Callee(); } catch { } finally { sideEffect(); }`
  and `Callee` throws
- **THEN** the caller-side catch runs AND the caller-side finally runs (the
  caller frame's try/finally state is intact, not corrupted by the cross-frame
  unwind).

### Requirement: Frame and mStack state stay consistent across unwinds

`ExecuteNeo` SHALL, on every exit path -- normal `Ret`, caught-and-resumed
execution, and unhandled-exception re-throw -- leave the shared
runtime state consistent: the current frame is popped from the frames stack
exactly once, and `mStack.Count` is truncated back to the entry `frameRefBase`.
A thrown exception that propagates out of `ExecuteNeo` SHALL NOT leave the
current frame on the frames stack or leave the frame's reserved mStack region
allocated. The shared `HandleException` frame-pop loop remains the authority for
batch-popping intervening frames when a handler is found in an ancestor frame.

#### Scenario: a method that throws unhandled leaves no frame/mStack leak
- **WHEN** an IL method under `ENABLE_NEO_MODE` throws an exception that is not
  caught anywhere and is allowed to escape to the host
- **THEN** after the escape the frames stack and `mStack.Count` are back to the
  state before the method was entered (no leaked frame, no leaked reserved ref
  slots), so subsequent invocations are unaffected.

### Requirement: Legacy exception handling is unchanged

This change SHALL NOT modify the Legacy interpreter path (`ExecuteR` in
`ILIntepreter.Register.cs`) or the shared handler-matching engine
(`HandleException`, `GetCorrespondingExceptionHandler`,
`FindExceptionHandlerByBranchTarget` in `ILIntepreter.cs`); Neo reuses the
shared engine as-is.

#### Scenario: Legacy behavior is byte-for-byte preserved
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE`
- **THEN** all pre-existing Legacy exception-handling tests continue to pass
  unchanged (no behavioral regression in `ExecuteR`).
