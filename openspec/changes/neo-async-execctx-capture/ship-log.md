# Ship Log: neo-async-execctx-capture

**Status: SHIPPED** (completion-3 wave, child 3). Lead-5 authored + verified + shipped.

## What shipped

Neo async `AwaitOnCompleted` ExecutionContext capture + flow (was a stub mirroring
`AwaitUnsafeOnCompleted` with no capture), plus the custom-non-ICritical-awaiter suspend
foundation that makes the `AwaitOnCompleted` path reachable. Three Neo-only edits in
`CLRRedirections.AsyncNeo.cs`:

1. **`AwaitOnCompleted_Neo` captures EC.** `ExecutionContext.Capture()` at suspend
   (defended), passed to `SuspendStateMachine`.
2. **`SuspendStateMachine` flows EC into the resume.** New `ecToFlow` param; when
   non-null, the resume `Action` is wrapped in `ExecutionContext.Run(ecToFlow, ...)`.
   `AwaitUnsafeOnCompleted` passes null (unchanged).
3. **`GetAwaiterTask` recognizes a custom Task-wrapping awaiter** (duck-typed `m_task`
   field of type `Task`), so a non-ICritical awaiter can suspend.

Tests: `NeoStep20_TC13_ExecCtxCustomAwaiterSuspend` (custom-awaiter suspend/resume through
`AwaitOnCompleted`) + `NeoStep20_TC14_ExecCtxFlowsAsyncLocal` (EC flow observed via a host
`AsyncLocal` across a no-EC-flow threadpool resume). Host additions in `TestClass3.cs`:
`ECProbeAwaiter`/`ECProbeAwaitable` + `GetECProbeAwaitable` + `SetAL`/`GetAL` (host
`AsyncLocal<int>`) + `CompleteIncompleteTaskNoECFlow` (`UnsafeQueueUserWorkItem`).

## Review outcome: APPROVED (0 Blocker, 0 Major)

The decisive evidence is the stash-toggle: TC14 PASSES with the EC capture, FAILS without
it (`t.Result` 7042 vs 7000 -- the AsyncLocal does not flow when the capture is disabled).
The cross-thread `UnsafeQueueUserWorkItem` completion is what makes TC14 a VALID EC-flow
test (a same-thread / `Task.Run` completion masks the capture). See `review-report.md`.

## Gates (all green)

| Gate | Result |
|------|--------|
| `NeoStep20_TC13_ExecCtxCustomAwaiterSuspend` | PASS |
| `NeoStep20_TC14_ExecCtxFlowsAsyncLocal` | PASS (`t.Result == 7042`) |
| TC14 stash-toggle (capture disabled) | FAIL (`t.Result == 7000`) -- capture load-bearing |
| Full `NeoStep` regression | **241/0/0** (239 + TC13 + TC14) |
| CLI `Debug_Neo` build | 0 errors |
| TestCases `Debug` build | 0 errors |
| Legacy-neutral (plain-`Debug` CLI) | 0 errors (`AsyncNeo.cs` compiles out) |

## Files committed

- `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs` (AwaitOnCompleted EC capture + SuspendStateMachine EC flow + GetAwaiterTask custom-awaiter)
- `TestCases/NeoStep20Test.cs` (TC13 + TC14 + the two probe bodies)
- `ILRuntimeTestBase/TestFramework/TestClass3.cs` (ECProbeAwaiter/Awaitable + GetECProbeAwaitable + SetAL/GetAL + CompleteIncompleteTaskNoECFlow)
- `openspec/changes/neo-async-execctx-capture/` (proposal/design/tasks/specs + review-report + ship-log)

Excluded: `.pdb`/`.gitignore`/`nuget.config`/`.vscode`/`CLAUDE.md` churn.

## Commit

`Neo step20 async: AwaitOnCompleted ExecutionContext capture+flow (+ custom non-ICritical
awaiter suspend foundation)`
