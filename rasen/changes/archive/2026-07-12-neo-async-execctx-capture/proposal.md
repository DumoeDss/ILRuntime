# Proposal: neo-async-execctx-capture

## Why

`AwaitOnCompleted_Neo` (the async builder redirect for an awaiter that implements
`INotifyCompletion` but NOT `ICriticalNotifyCompletion`) currently MIRRORS
`AwaitUnsafeOnCompleted_Neo` -- it calls `SuspendStateMachine(method)` with NO
`ExecutionContext` capture. Per .NET async semantics, `AwaitOnCompleted` SHALL
capture the current `ExecutionContext` and flow it to the resume (so `AsyncLocal<T>`
values set before the await are visible in the continuation); `AwaitUnsafeOnCompleted`
(ICritical awaiters such as `TaskAwaiter`) intentionally does NOT. The two redirects
were behaviorally identical, so the EC-capturing path was missing.

The path was also UNREACHABLE: `GetAwaiterTask` recognized only `TaskAwaiter` /
`TaskAwaiter<T>` (both `ICriticalNotifyCompletion` -> `AwaitUnsafeOnCompleted`), so a
custom non-ICritical awaiter (the only thing that triggers `AwaitOnCompleted`) could
not suspend -- `GetAwaitedTaskFromSm` returned null -> NIE.

## What Changes

1. **`AwaitOnCompleted_Neo` captures EC.** `ExecutionContext.Capture()` at suspend;
   passed to `SuspendStateMachine`.
2. **`SuspendStateMachine` flows EC to the resume.** New `ecToFlow` param; when
   non-null, the resume `Action` is wrapped in `ExecutionContext.Run(ecToFlow, ...)`.
   `AwaitUnsafeOnCompleted` passes null (unchanged -- no EC flow).
3. **`GetAwaiterTask` recognizes a custom Task-wrapping awaiter** (duck-typed `m_task`
   field of type `Task`), so a non-ICritical awaiter that wraps a Task can suspend --
   making the `AwaitOnCompleted` path reachable + testable for the first time.

`SynchronizationContext` capture is N/A: ILRuntime runs no SC (`SC.Current` is always
null), and SC would be captured by the builder at `Start` (not per-await) regardless.

## Impact

- **Affected capability spec:** `neo-async` (MODIFIED -- the `AwaitOnCompleted` EC-
  capture requirement is added; was an accepted-known deferral).
- **Affected code:** `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs`
  (Neo-only file; `AwaitOnCompleted_Neo` + `SuspendStateMachine` + `GetAwaiterTask`).
- **Tests:** `TestCases/NeoStep20Test.cs` gains `NeoStep20_TC13_ExecCtxCustomAwaiterSuspend`
  (custom-awaiter suspend through `AwaitOnCompleted`) + `NeoStep20_TC14_ExecCtxFlowsAsyncLocal`
  (EC flow observed via a host `AsyncLocal` across a no-EC-flow threadpool resume).
- **Host:** `ILRuntimeTestBase/TestFramework/TestClass3.cs` gains `ECProbeAwaiter` /
  `ECProbeAwaitable` (a non-ICritical Task-wrapping awaiter) + `GetECProbeAwaitable` +
  `SetAL`/`GetAL` (host `AsyncLocal<int>`) + `CompleteIncompleteTaskNoECFlow` (completes
  via `UnsafeQueueUserWorkItem`, which does NOT flow EC, isolating the capture).
- Legacy byte-identical (every engine edit in a Neo-only `#if ENABLE_NEO_MODE` file;
  compiles out under plain `Debug`).

## Non-Goals (sequenced)

- Full custom-awaitable-`GetAwaiter` redirect coverage (a custom awaiter's
  `get_IsCompleted` here rides the raw `callvirt.clr` path; it worked for this probe
  but a custom awaiter with an 8-byte-reused IsCompleted dest slot could need the
  Piece-1 zero-extension redirect -- follow-up if a probe needs it).
- `SynchronizationContext` capture/post (N/A -- no SC in ILRuntime).
- `ValueTask<T>` / `async void` suspend paths (separate children).
