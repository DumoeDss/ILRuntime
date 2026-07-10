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

## Implementation round 2 (the REAL blockers — 2026-07-10, child-4 finish)
Fixer-1's accessor rework (ThreadStatic `_currentValueTaskState`) + the Call-case
heap-buffer + DebugService guard (items 1-5 in `handoff/fixer-1.md`) were correct and
KEPT. The remaining failures were THREE distinct root causes, all now fixed:

- [x] 17. **curPrim/byref-`this` marshalling (VT1/VT2/VT6 — the REAL blocker; B1 disproven).**
  The `AsyncValueTaskMethodBuilder_T_SetResult_Neo` did `curPrim += 8` to skip the
  builder `this`, then `ReadResultParam` read the int result at `frameBase[8]`. But the
  builder `this` is a VALUE TYPE passed byref; the engine's call-arg lowering
  (`CopyNeoCallArguments`, byRefSrc slot 0) DEREFERENCES the byref and copies the struct's
  FLAT MANAGED BYTES (`Unsafe.SizeOf<T>` via `Optimizer.GetNeoValueTypeManagedSize`) into
  the callee param region — NOT an 8-byte byref. `AsyncValueTaskMethodBuilder<T>` is 16
  bytes; `AsyncTaskMethodBuilder<T>` is 8. So `+= 8` undershot by 8 for the ValueTask
  builder → `ReadResultParam` read stale struct residue (`frameBase[8]`=4=old `v=1`)
  instead of the real result (`frameBase[16]`=14=`v+3`). TC8 (Task<int>) worked because
  the Task builder happens to be 8 bytes.
  **Fix:** new `BuilderThisManagedSize(method)` helper returns
  `Optimizer.GetNeoValueTypeManagedSize(method.DeclearingType.TypeForCLR)`; `SetResult`
  (Task + ValueTask) and `SetException` (Task + ValueTask, since ValueTask delegates to
  the Task helper) now skip the ACTUAL struct size. No-op for the Task builder (size 8),
  correct (16) for the ValueTask builder. Mirrors the `TaskAwaiter_T_GetIsCompleted_Neo`
  size-resolution precedent (:904). **B1 (the ILType field-layout-collision hypothesis) is
  DISPROVEN** — the shared `PrimitiveOffset` is benign (disjoint `Primitives[]`/
  `ManagedObjects[]` storage). See `../neo-clrstruct-sm-field-layout/blocked.md`.
- [x] 18. **B3 — `CreateFaultedValueTask` AmbiguousMatchException (VT3).**
  `typeof(Task).GetMethod("FromException", new[]{ typeof(Exception) })` is ambiguous
  because `Task` has TWO `FromException` overloads that BOTH take `(Exception)`: the
  non-generic `Task.FromException(Exception)` and the GENERIC
  `Task.FromException<T>(Exception)`. Fix: resolve the GENERIC definition explicitly via
  `Array.Find(..., m => m.Name == "FromException" && m.IsGenericMethod)`, then close it
  with T (`MakeGenericMethod(t)`) to produce a real faulted `Task<T>`.
- [x] 19. **B2 — registration-completeness gap (VT4, ValueTask<string>). NOT a foundational
  binder gap — a registration miss.** VT4's string SM calls `AsyncValueTaskMethodBuilder
  <string>.Start/SetResult/SetException`, `TaskAwaiter<string>.get_IsCompleted/GetResult`,
  and `Task<string>.GetAwaiter/get_Result`. NONE were registered for T=string (only
  `<int>` + `<ILTypeInstance>` + the non-generic variants). Unregistered calls fell to the
  reflection fallback, whose Area-4b guard NIEs on a struct-`this`-with-reference-field
  (`TaskAwaiter<string>` / `AsyncValueTaskMethodBuilder<string>` have a `T`-typed field
  that IS a reference field for T=string, unlike T=int). **Fix:** add `<string>` to the
  `RegisterValueTaskBuilderT`, `RegisterAwaiterAccessors`, and `RegisterTaskAccessors`
  registrations (mirrors the existing `<int>`/`<ILTypeInstance>` pattern). The redirect
  path bypasses reflection entirely (reads the SM via `CurrentAsyncSm` + the builder-this
  via `BuilderThisManagedSize`), so the Area-4b guard is never reached.

## Verification (round 2)
- [x] 20. NeoStep smoke green: **273 ran, 0 failed** (HEAD + round-2 fixes). VT1-VT6 ALL
  GREEN. No regression (TC1-TC14 + all prior NeoStep stay green).
- [x] 21. Stash-toggle (load-bearing): stash ONLY `CLRRedirections.AsyncNeo.cs`, rebuild,
  run `NeoStep20_VT` -> **5 fail (VT1/VT2/VT3/VT4/VT6; VT5 async-void unaffected)**; pop
  -> 6/6 green. Confirms the engine file is load-bearing for VT1-VT4,VT6.
- [x] 22. Legacy-neutral: plain `Debug` build of the CLI -> 0 errors (file is
  `#if ENABLE_NEO_MODE`-gated end-to-end; `git diff HEAD -- ILType.cs` empty).

