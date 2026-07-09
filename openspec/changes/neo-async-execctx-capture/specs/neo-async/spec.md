# neo-async (delta for neo-async-execctx-capture)

This delta MODIFIES the canonical `openspec/specs/neo-async/spec.md`. It adds the
`AwaitOnCompleted` ExecutionContext-capture requirement (previously an accepted-known
deferral) and makes the `AwaitOnCompleted` suspend path reachable for a custom
non-`ICriticalNotifyCompletion` awaiter.

## ADDED: Requirement "Neo async AwaitOnCompleted captures and flows ExecutionContext"

The Neo async SUSPEND path for `AwaitOnCompleted` (the builder redirect invoked when the
awaiter implements `INotifyCompletion` but NOT `ICriticalNotifyCompletion`) SHALL capture
the current `ExecutionContext` at suspend and flow it to the resume, so values held in
the execution context (e.g. `AsyncLocal<T>`) that were set before the `await` are VISIBLE
in the continuation. The `AwaitUnsafeOnCompleted` path (ICritical awaiters such as
`TaskAwaiter`) SHALL NOT capture/flow the EC (it opts out, per .NET async semantics).

#### Scenario: A custom non-ICritical awaiter suspends via AwaitOnCompleted

- **WHEN** an async method awaits a custom awaitable whose awaiter implements
  `INotifyCompletion` but NOT `ICriticalNotifyCompletion` (so the C# compiler lowers the
  `await` to `AwaitOnCompleted`, not `AwaitUnsafeOnCompleted`), and the awaiter wraps a
  `Task` (exposing it via an `m_task` field, the `TaskAwaiter` convention)
- **THEN** the SUSPEND path SHALL resolve the wrapped `Task` (the awaiter is recognized
  by its `m_task` field), register the continuation on it, and capture the current
  `ExecutionContext`
- **AND** the RESUMPTION path SHALL run the resume callback via
  `ExecutionContext.Run(capturedEC, ...)`, so the continuation observes the pre-await
  execution-context state
- (PROVEN by `NeoStep20_TC13_ExecCtxCustomAwaiterSuspend`: the custom-awaiter await
  suspends, the wrapped Task's completion resumes it, and `GetResult` reads the result.)

#### Scenario: An AsyncLocal value set before an AwaitOnCompleted await is visible in a cross-thread continuation

- **WHEN** an async method sets an `AsyncLocal<T>` value, then awaits a custom
  non-ICritical awaitable (the `AwaitOnCompleted` path), and the awaited Task completes
  on a thread whose `ExecutionContext` is NOT the caller's (e.g. a threadpool work item
  queued via `UnsafeQueueUserWorkItem`, which does not flow EC)
- **THEN** the continuation SHALL observe the pre-await `AsyncLocal<T>` value (the
  captured EC was flowed via `ExecutionContext.Run`); WITHOUT the capture the value would
  be the default (the resume thread's EC), proving the capture is load-bearing
- (PROVEN by `NeoStep20_TC14_ExecCtxFlowsAsyncLocal`: `t.Result == 7042` with the capture
  (AsyncLocal==42 flowed); stash-toggle (disable capture) -> `t.Result == 7000`
  (AsyncLocal==0) -> FAIL.)

## MODIFIED: Accepted-known limitations

- The "Deferred (`ExecutionContext`/`SynchronizationContext` capture for
  `AwaitOnCompleted`)" entry is REMOVED (resolved by this change). The
  `SynchronizationContext` portion remains N/A (ILRuntime runs no SC) -- it is not a
  deferral, it does not apply.
- **Custom-awaiter `get_IsCompleted` redirect (INFO):** the custom awaiter's
  `get_IsCompleted` rides the raw `callvirt.clr` path (the Piece-1 zero-extension
  redirect is `TaskAwaiter`-specific). It worked for the ECProbe awaiter; a custom
  awaiter whose `IsCompleted` dest slot is 8-byte-reused (the Piece-1 shape) could
  misroute. Accepted-known; a redirect is a follow-up if a probe needs it.

## UNCHANGED requirements (in scope, no delta)

- "Neo async suspend and resumption (IMPLEMENTED)" (the multi-await shape, resolved by
  neo-async-multi-await, is unchanged).
- "Neo async method builder redirection", "Neo async Start drives MoveNext", "Neo async
  Task getter", "Neo async frame-to-heap hoist", "Neo ILAsyncContext bridge", "Neo async
  branch-size correctness".
