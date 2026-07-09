# Tasks — neo-async-valuetask-asyncvoid (child 4)

## Engine fix (CLRRedirections.AsyncNeo.cs, #if ENABLE_NEO_MODE-gated)
- [x] 1. Add `WrapBridgeAsValueTask(CLRMethod method, object bridgeTask)` helper:
  resolves T via `GetResultClrType`, closes `ValueTask<>`, `Activator.CreateInstance`
  on the `ValueTask<T>(Task<T>)` ctor. Mirrors `CreateValueTaskFromResult`'s T resolution.
- [x] 2. Rework `AsyncValueTaskMethodBuilder_T_GetTask_Neo`:
  - [x] 2a. Add the `SmContextMap` suspend-case branch (`ctx.GetTaskBridge()` →
    `WrapBridgeAsValueTask`), checked AFTER the `SmTaskMap` sync path.
  - [x] 2b. Replace `WriteReferenceReturn(vt, ...)` with `WriteValueTypeReturn(vt, ...)`
    in ALL branches (suspend / sync / faulted / defensive-default).
- [x] 3. `AsyncValueTaskMethodBuilder_GetTask_Neo` (non-generic ValueTask): replace
  `WriteReferenceReturn` with `WriteValueTypeReturn` for consistency (RefCount=0 flat
  bytes). Keep the suspend-case off this path (non-generic ValueTask builder is not
  exercised by the probes; the sync `default(ValueTask)` + faulted paths suffice).
  -> Actually checked: non-generic ValueTask does NOT have a suspend-case in the Task
     builder either (non-generic `AsyncTaskMethodBuilder_GetTask_Neo` returns
     `Task.CompletedTask` with no SmContextMap branch). For symmetry/consistency only
     the return-write is changed; no suspend-case added (out of scope, no probe).
- [x] 4. Verify `WriteValueTypeReturn` is the correct helper (RefCount=0 flat-bytes
  layout for a binder-less CLR struct — confirmed via
  `Optimizer.Neo.cs:1523-1545` + the proven-green `TaskAwaiter<T>` return path).

## Host helpers (ILRuntimeTestBase/TestFramework/TestClass3.cs)
- [x] 5. Add `GetIncompleteStringTask()` + `CompleteIncompleteStringTask(string)`:
  a self-resetting `TaskCompletionSource<string>` (mirrors `GetIncompleteTask`/
  `CompleteIncompleteTask`) for the VT4 ref-T probe.
- [x] 6. Add an async-void suspend side-effect cell if needed (reuse
  `SetAsyncVoidCell`/`GetAsyncVoidCell` — already present; no new cell needed).

## Tests (TestCases/NeoStep20Test.cs — concat-free async bodies)
- [x] 7. VT1 `NeoStep20_VT1_ValueTaskIntSuspendResume` + private
  `NeoStep20_ValueTaskIntSuspendProbe` (truly-async suspend+resume, mark-signal).
- [x] 8. VT2 `NeoStep20_VT2_ValueTaskIntSync` + probe (sync completion).
- [x] 9. VT3 `NeoStep20_VT3_ValueTaskIntFaulted` + probe (faulted).
- [x] 10. VT4 `NeoStep20_VT4_ValueTaskStringSuspend` + probe (ref-T suspend+resume).
- [x] 11. VT5 `NeoStep20_VT5_AsyncVoidSuspend` + probe (async void suspend verify+guard).
- [x] 12. VT6 `NeoStep20_VT6_ValueTaskIntSyncControl` (no-await control).

## Verification
- [x] 13. NeoStep smoke green with new probes: 248 -> 254 (248 + 6 new). Report exact.
- [x] 14. Stash-toggle (load-bearing): stash the engine file (keep tests) -> the suspend
  probes (VT1, VT4, VT5-if-it-suspends) FAIL on HEAD; pop -> PASS.
- [x] 15. Legacy-neutral: plain `Debug` builds 0 errors (file is #if ENABLE_NEO_MODE-gated);
  Legacy NeoStep unaffected.
- [x] 16. No regression: existing TC1-TC14 stay green; NeoStep20 stays green.
