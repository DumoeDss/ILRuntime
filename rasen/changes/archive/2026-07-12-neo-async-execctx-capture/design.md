# Design: neo-async-execctx-capture

## Context

The truly-async suspend path (`AwaitUnsafeOnCompleted_Neo` -> `SuspendStateMachine`) is
proven (neo-async-movenext-fix + neo-async-multi-await). `AwaitOnCompleted_Neo` was a
stub that mirrored it without the EC capture that distinguishes the two per .NET async
semantics. Two gaps: (a) no EC capture/flow; (b) the path was unreachable (no resolvable
non-ICritical awaiter existed -- `GetAwaiterTask` knew only `TaskAwaiter`).

## Root cause (one line)

`AwaitOnCompleted_Neo` did not capture/flow `ExecutionContext`, and `GetAwaiterTask`
could not resolve a custom non-ICritical Task-wrapping awaiter, so the EC-capturing path
was both wrong and unreachable.

## Decisions

### D1: `AwaitOnCompleted_Neo` captures `ExecutionContext`
`ExecutionContext.Capture()` at suspend (defended -- `Capture` can return null / throw in
a suppressed-flow or sandbox host; null => resume without EC flow, a safe degradation).
Passed to `SuspendStateMachine`. `AwaitUnsafeOnCompleted_Neo` is UNCHANGED (no capture).

### D2: `SuspendStateMachine` flows the EC into the resume
New optional param `ExecutionContext ecToFlow = null`. At continuation registration:
- `ecToFlow != null` -> `task.GetAwaiter().UnsafeOnCompleted(() => ExecutionContext.Run(ecToFlow, _ => resumeAction(), null))`.
- else -> `task.GetAwaiter().UnsafeOnCompleted(resumeAction)` (unchanged).

`ExecutionContext.Run` restores the captured EC for the resume callback, so `AsyncLocal`
reads in the continuation see the pre-await values.

### D3: `GetAwaiterTask` recognizes a custom Task-wrapping awaiter (reachability)
After the `TaskAwaiter`/`TaskAwaiter<T>` checks, if the object is NOT one of those, look
for an instance field named `m_task` of type `Task` (the `TaskAwaiter` convention) and
return its value. Duck-typed + `try/catch`-defended so a non-awaiter object (a hoisted
`Task` local, the builder) does NOT throw -- it is simply not a suspensible awaiter.

### D4: `SynchronizationContext` is N/A
ILRuntime runs no SC (`SC.Current` is null). SC would be captured by the builder at
`Start`, not per-await. No SC work in this change.

### D5: TC13 (custom-awaiter suspend) + TC14 (EC flow) + the no-EC-flow completion host cell
- **TC13** `NeoStep20_TC13_ExecCtxCustomAwaiterSuspend`: `await GetECProbeAwaitable()`
  (a custom non-ICritical Task-wrapping awaiter) suspends via `AwaitOnCompleted`; the
  driver completes the wrapped Task; the resume `GetResult` reads its result. Proves the
  `AwaitOnCompleted` suspend/resume + the custom-awaiter `GetAwaiterTask` resolution work.
- **TC14** `NeoStep20_TC14_ExecCtxFlowsAsyncLocal`: the EC-capture semantics. The probe
  `SetAL(42)`, awaits the custom awaitable, reads `GetAL()` in the continuation, returns
  `v*1000 + GetAL()`. The driver completes via `CompleteIncompleteTaskNoECFlow`
  (`UnsafeQueueUserWorkItem` -- does NOT flow EC, unlike `Task.Run`), so the continuation
  resumes on a threadpool thread under a DEFAULT EC. WITH the EC capture `GetAL()==42`
  (the resume ran via `ExecutionContext.Run(capturedEC)`); WITHOUT it `GetAL()==0` (the
  default EC) -> `t.Result` differs (7042 vs 7000). Binding stash-toggle: disabling the
  capture (`SuspendStateMachine(method, null)`) flips TC14 PASS -> FAIL.

## Apply findings (earned during verification)

- **TC14's first cut did NOT isolate EC flow.** Completing via `CompleteIncompleteTask`
  (`TaskCompletionSource.SetResult` on the main/driver thread) runs the await
  continuation SYNCHRONOUSLY on that same thread, whose EC already has `AsyncLocal==42`.
  So TC14 passed even with the EC capture disabled -- it was not a valid EC-flow test.
  Fix: complete via `ThreadPool.UnsafeQueueUserWorkItem` (queues to a threadpool thread
  WITHOUT flowing EC), forcing the resume onto a default-EC thread where the captured EC
  is the ONLY way `AsyncLocal==42` is visible. With that, the stash-toggle (disable
  capture) correctly flips TC14 to FAIL. **Lesson (durable): EC-flow tests MUST resume on
  a thread whose EC is NOT the caller's -- `SetResult` on the caller thread or `Task.Run`
  both preserve the caller's EC (the former synchronously, the latter by flowing it), so
  neither isolates the capture. Use `UnsafeQueueUserWorkItem` (or a raw `Thread`).**
- The custom awaiter's `get_IsCompleted` rode the raw `callvirt.clr` path (no redirect)
  and worked for this probe. The Piece-1 zero-extension redirect is `TaskAwaiter`-specific;
  a custom awaiter whose IsCompleted dest slot is 8-byte-reused could misroute -- a
  follow-up if a probe hits it.

## Risks / Trade-offs

- **[EC `Capture` returns null]** -> `ecToFlow=null` -> resume without EC flow (safe
  degradation; matches `AwaitUnsafeOnCompleted`).
- **[Duck-typed `m_task` on a non-awaiter]** -> `try/catch`-defended; returns null -> the
  existing single-Task scan / NIE. No silent wrong pick.
- **[Synchronous-continuation EC preservation masks the capture]** -> mitigated by the
  `UnsafeQueueUserWorkItem` completion in TC14 (D5 apply finding). The capture is
  load-bearing for genuinely cross-thread resumes.

## Migration Plan

Pure feature add for Neo (Legacy byte-identical). Rollback = revert the three engine
edits; `AwaitUnsafeOnCompleted` + the standard Task-await path are unaffected (the EC
param defaults null), and the custom-awaiter path reverts to the (pre-existing)
unreachable state.
