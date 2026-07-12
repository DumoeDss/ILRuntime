# Tasks: neo-async-execctx-capture

Status: APPLIED + VERIFIED (completion-3 wave, child 3, lead-5). All edits Neo-only
(`#if ENABLE_NEO_MODE` file) or Neo-test-only; Legacy-neutral by construction.

## Block 0 -- reachability probe
- [x] 0.1 Confirm `AwaitOnCompleted_Neo` is unreachable on HEAD: a non-ICritical
  Task-wrapping awaiter suspends via NIE (`GetAwaiterTask` knows only `TaskAwaiter`).
- [x] 0.2 Add the `ECProbeAwaiter`/`ECProbeAwaitable` host types + `GetECProbeAwaitable`
  + `SetAL`/`GetAL` host `AsyncLocal<int>` + `CompleteIncompleteTaskNoECFlow` to
  `TestClass3.cs`.

## Block 1 -- the fix (D1 + D2 + D3)
- [x] 1.1 **D1** `AwaitOnCompleted_Neo` captures `ExecutionContext.Capture()` (defended),
  passes it to `SuspendStateMachine`. (`CLRRedirections.AsyncNeo.cs`.)
- [x] 1.2 **D2** `SuspendStateMachine(CLRMethod, ExecutionContext ecToFlow = null)`:
  when `ecToFlow != null`, wrap the resume `Action` in
  `ExecutionContext.Run(ecToFlow, _ => raw(), null)`; else unchanged.
  `AwaitUnsafeOnCompleted_Neo` calls `SuspendStateMachine(method)` (null) -- unchanged.
- [x] 1.3 **D3** `GetAwaiterTask`: after the `TaskAwaiter` checks, duck-type an `m_task`
  field of type `Task` on a custom awaiter (defended).
- [x] 1.4 Build CLI (`Debug_Neo`, 0 errors) + TestCases (`Debug`, 0 errors). Confirm
  plain-`Debug` compiles the Neo file out (Legacy-neutral).

## Block 2 -- the gates (TC13 + TC14)
- [x] 2.1 `NeoStep20_TC13_ExecCtxCustomAwaiterSuspend` -- custom-awaiter suspend/resume
  through `AwaitOnCompleted`. EXPECT PASS. (Binding: the path is reachable + correct.)
- [x] 2.2 `NeoStep20_TC14_ExecCtxFlowsAsyncLocal` -- EC flow across a no-EC-flow
  threadpool resume. EXPECT PASS (`t.Result == 7042`).
- [x] 2.3 **Stash-toggle proof:** disable the EC capture
  (`SuspendStateMachine(method, null)`) -> TC14 FAILs (`t.Result == 7000`, AsyncLocal did
  not flow). Restore -> TC14 PASSes. (Binding: the capture is load-bearing.)
- [x] 2.4 Full `NeoStep` smoke -> 241/0/0 (239 + TC13 + TC14; no regressions).
- [x] 2.5 TC14 first-cut isolation fix: complete via `UnsafeQueueUserWorkItem`
  (`CompleteIncompleteTaskNoECFlow`), not `SetResult` on the caller thread (which runs the
  continuation synchronously under the caller's EC and masks the capture).

## Block 3 -- Legacy-neutral + ship
- [x] 3.1 Legacy-neutral: plain-`Debug` CLI builds 0 errors (the Neo file compiles out).
- [x] 3.2 Write `review-report.md` + `ship-log.md`.
- [x] 3.3 Stage precisely + commit + push.

## Status
APPLIED + VERIFIED (2026-07-09, lead-5). TC13 + TC14 green; stash-toggle proves the EC
capture load-bearing; NeoStep 241/0/0; Legacy-neutral. Review APPROVED (lead-5; author =
lead-5 -- the subagent test-runner cannot run `dotnet`, so the lead authored + adversarially
self-verified with the stash-toggle + cross-thread-isolation proof).
