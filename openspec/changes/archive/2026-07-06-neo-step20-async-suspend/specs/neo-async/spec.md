## ADDED Requirements

### Requirement: Neo async suspend via AwaitUnsafeOnCompleted (single-await Task, no context capture)

Neo mode SHALL implement the truly-async SUSPEND path for the single-await
`Task<T>` / `Task` shape via `AwaitUnsafeOnRegistered_Neo`: when an async
state machine's `MoveNext` reaches an await whose awaiter reports
`IsCompleted == false`, the C# compiler emits a `call` to
`builder.AwaitUnsafeOnCompleted<TAwaiter,TStateMachine>(ref awaiter, ref sm)`.
The Neo redirect SHALL (a) recover the state-machine heap `ILTypeInstance`
(the SM is a heap object, per the sync-slice OQ2), (b) read the awaiter's
wrapped `Task`/`Task<T>` (the awaiter arrives as a frame-local byref; its
`m_task` field is read via the shipped `GetAwaiterTask` helper), (c) construct
an `ILAsyncContext<T>` holding the SM + the cached `MoveNext` ILMethod, (d)
register the continuation via `task.UnsafeOnCompleted(continuation)` (no
ExecutionContext/SynchronizationContext capture -- the `Unsafe` semantics), and
(e) return from `MoveNext` WITHOUT calling `SetResult`/`SetException` (the SM
is suspended; `SmTaskMap` is not populated). This requirement depends on the
closed-generic-method redirect resolution matching the registered open-
definition redirect for `AwaitUnsafeOnCompleted<TA,TSM>` (a 2-generic-arg
method); if that resolution does not hold, the redirect is unreachable and the
suspend path cannot execute.

#### Scenario: Genuinely-incomplete Task await suspends the state machine

- **WHEN** an async method awaits a `Task`/`Task<T>` whose `IsCompleted` is
  `false` at the await point (e.g. `await Task.Delay(ms)`), reaching
  `AwaitUnsafeOnCompleted`
- **THEN** the Neo redirect SHALL register a continuation with the awaited
  task (via `UnsafeOnCompleted`), return from `MoveNext` without stashing a
  result in `SmTaskMap`, and the returned `Task<T>` SHALL be a hot
  (incomplete) task backed by the `ILAsyncContext<T>` -- NOT a completed
  default task
- (FAIL-on-HEAD: the redirect is unreachable -- the closed-generic 2-arg call
  does not resolve to the open-definition redirect, and the call falls to CLR
  reflection. Target: PASS-after Phase 1 + Phase 2.)

#### Scenario: The suspend redirect is actually entered

- **WHEN** the Neo redirect-lookup resolves a closed-generic
  `AwaitUnsafeOnCompleted<TA,TSM>` call to the registered open-definition
  `AwaitUnsafeOnCompleted_Neo` redirect
- **THEN** the redirect body SHALL execute (a probe-side trace at the top of
  the body SHALL fire), NOT fall through to the CLR reflection fallback /
  Legacy `IAsyncStateMachineAdaptor` boxing path
- (FAIL-on-HEAD: the redirect body is never entered for the 2-generic-arg
  closed method. Target: PASS-after Phase 1 D5c.)

### Requirement: Neo async resumption via ILAsyncContext.MoveNext (fresh pooled interpreter)

Neo mode SHALL implement the async RESUMPTION path via
`ILAsyncContext<T>.MoveNext` (the `IAsyncStateMachine.MoveNext` body that was
a tagged NIE in the sync slice). When the awaited task completes on a
threadpool thread, the registered continuation SHALL invoke the resumption,
which SHALL (a) obtain a FRESH pooled interpreter (`RequestILIntepreter` --
the resumption fires on a threadpool thread with no in-flight Neo frame),
(b) build a Neo frame and write the hoisted state-machine heap instance as
slot-0 `this`, (c) `ExecuteNeo` the cached `MoveNext` ILMethod (it resumes at
the await state via `<>1__state`, reloads the `<>u__1` awaiter field, calls
`GetResult`, and continues to the next terminal state), (d) route the terminal
`SetResult`/`SetException` to the `ILAsyncContext<T>`'s
`ManualResetValueTaskSourceCore<T>` (NOT the sync-slice `SmTaskMap`), and (e)
`FreeILIntepreter` in `finally` on EVERY exit path (happy, exception, nested).
The `ManualResetValueTaskSourceCore<T>` SHALL handle token races across
concurrent `GetStatus`/`OnCompleted`/`GetResult`.

#### Scenario: Resumed MoveNext completes the task with the awaited result

- **WHEN** an async method suspended at `await Task.Delay(ms); return v;` and
  the awaited task completes
- **THEN** the resumption SHALL re-run `MoveNext` on a fresh pooled
  interpreter, the resumed `MoveNext` SHALL call `SetResult(v)` (routed to
  `ctx.core.SetResult(v)`), the returned `Task<T>` SHALL complete with
  `Result == v`, and the pooled interpreter SHALL be freed (no pool leak)

#### Scenario: Exception from the resumed method faults the task

- **WHEN** an async method suspended and, on resumption, throws inside the
  post-await body
- **THEN** the resumption SHALL route the exception to
  `ctx.core.SetException(ex)`, and the returned `Task<T>` SHALL be Faulted
  with the exception

#### Scenario: Locals survive across the threadpool resume

- **WHEN** an async method computes a local before an incomplete await and
  reads it after the resume
- **THEN** the resumed `MoveNext` SHALL read the pre-await local's value from
  the hoisted state-machine heap instance (no stale frame read, no
  cross-thread corruption)

### Requirement: Neo async get_Task routes to the context on the suspend path

The Neo `get_Task` builder redirection SHALL detect when the state machine
suspended (no `SmTaskMap` entry; an `ILAsyncContext<T>` parked on a
`SmContextMap[sm]`) and produce a hot `Task<T>` / `Task` backed by the
context's `IValueTaskSource<T>` (the `core`), so the caller's await of the
returned task completes when the resumed SM completes. On the sync path
(`SmTaskMap[sm]` populated by `SetResult`/`SetException`), the getter SHALL
remain unchanged (the sync-slice behavior). The getter SHALL NOT produce a
silent default-completed task on the suspend path.

#### Scenario: Suspended SM yields a hot task

- **WHEN** `get_Task` runs after the SM suspended (the context is parked on
  `SmContextMap[sm]`)
- **THEN** the getter SHALL return a `Task<T>` whose completion is driven by
  the context's `core`, NOT `Task.FromResult(default)`

## MODIFIED Requirements

### Requirement: Neo async AwaitUnsafeOnCompleted is a tagged deferral (synchronous scope boundary)

*(Modified: the `AwaitUnsafeOnCompleted` deferral is RESCinded for the
single-await `Task<T>`/`Task` shape, which is now implemented per the ADDED
"Neo async suspend via AwaitUnsafeOnCompleted" requirement above. The
`AwaitOnCompleted` deferral REMAINS -- context capture is deferred.)*

Neo mode SHALL implement `AwaitUnsafeOnRegistered_Neo` for the single-await
`Task<T>`/`Task` shape (no ExecutionContext capture) per the ADDED suspend
requirement. The `AwaitOnCompleted_Neo` redirect (ExecutionContext +
SynchronizationContext capture) SHALL remain a tagged
`NotImplementedException` referencing `neo-step20-async-suspend` round 2 until
context capture lands. Multi-await suspend, `ValueTask<T>` suspend, and
`async void` suspend SHALL remain deferred (the single-await `Task<T>`
machinery is the primitive they reuse).

#### Scenario: AwaitOnCompleted remains deferred

- **WHEN** an async method's lowered code calls `AwaitOnCompleted` (the
  context-capturing variant)
- **THEN** the Neo redirect SHALL throw a `NotImplementedException`
  referencing `neo-step20-async-suspend` round 2, and SHALL NOT silently
  no-op or produce a wrong result

#### Scenario: Multi-await suspend is deferred

- **WHEN** an async method with two or more incomplete awaits suspends more
  than once
- **THEN** the single-await machinery SHALL apply to the FIRST suspend; the
  multi-suspend cycle is a DEFERRED round-2 scope (the adversarial probe for
  the single-await path is the green target; multi-await-all-sync stays green
  via the `IsCompleted` short-circuit)

## REMOVED Requirements

### Requirement: Neo async suspend and resumption (DEFERRED)

*(Removed: the blanket deferral of the suspend/resume path is superseded by
the ADDED suspend/resume requirements above for the single-await `Task<T>`/
`Task` shape. The deferral is NARROWED to `AwaitOnCompleted` context capture,
multi-await, `ValueTask<T>` suspend, and `async void` suspend per the
MODIFIED requirement above.)*
