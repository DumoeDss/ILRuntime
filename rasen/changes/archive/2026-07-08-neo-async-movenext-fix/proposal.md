## Why

A truly-async `await` (one whose awaiter reports `IsCompleted == false`) HANGS the
Neo interpreter indefinitely. This is the single biggest real Neo usability gap:
async/await is core C#, and any await on a genuinely-incomplete awaitable
(I/O, `Task.Delay`, a pending `TaskCompletionSource`) freezes the runtime instead
of suspending and resuming. An instruction-level trace (this change's propose
phase) pinpointed the root cause: a branch read-width vs producer write-width
mismatch that misroutes the state machine past the suspend block into a blocking
`Task.Result`. The fix is small and confirmed; it unblocks the deferred
suspend/resume path that two prior children could only scaffold.

## What Changes

- **MoveNext control-flow hang fix (the core).** A `Brtrue`/`Brfalse` whose
  condition register is an 8-byte slot (reused for a managed pointer) but whose
  immediate producer is a 4-byte CLR-redirect return (e.g. `get_IsCompleted`
  writing `*(int*)retDst`) reads stale non-zero high bits, so the branch is taken
  when the real value is `false`. The redirect's bool/int return SHALL be
  zero-extended to the destination slot width so the 8-byte branch read is clean.
  This makes the state machine fall through to the suspend block and REACH
  `AwaitUnsafeOnCompleted` (confirmed by probe; previously never reached).
- **Suspend machinery.** `AwaitUnsafeOnCompleted_Neo` (and `AwaitOnCompleted_Neo`)
  SHALL get a real body: recover the heap state-machine instance, read the
  awaiter's task, build an `ILAsyncContext<T>`, register the continuation via
  `task.UnsafeOnCompleted`, and return WITHOUT `SetResult` (the SM is suspended).
  The tagged `NotImplementedException` is removed.
- **Resume machinery.** `ILAsyncContext<T>.MoveNext()` SHALL get a real body: on
  the continuation, acquire a fresh pooled interpreter, restore the state machine
  as slot-0 `this`, run `ExecuteNeo` (resumes at the await state, reloads the
  awaiter, calls `GetResult`, runs to `SetResult`/`SetException`), and route the
  result to `ctx.core` via a `_currentAsyncContext` ThreadStatic sink-swap. The
  `get_Task` getter SHALL produce a `Task<T>` backed by the context when the SM
  suspended. The tagged `NotImplementedException` is removed.
- **Deterministic gate.** `NeoStep20_TC8` is un-ignored and REDESIGNED to the
  deterministic completion signal: assert the await truly suspended (task NOT
  sync-completed), then complete the owning `TaskCompletionSource` and assert the
  resumed `Task.Result`. `TestCLRBinding` exposes the TCS so the test can drive
  completion without a real delay.

## Capabilities

### New Capabilities
<!-- None. -->

### Modified Capabilities
- `neo-async`: The "Reaching the tagged NIE without hanging is DEFERRED (the
  MoveNext control-flow blocker)" scenario is RESOLVED (the branch-size mismatch
  is diagnosed and fixed; the state machine reaches `AwaitUnsafeOnCompleted`).
  A new requirement pins the branch-size correctness (a CLR-redirect return
  feeding a `Brtrue`/`Brfalse` SHALL be zero-extended to the slot width). The
  suspend/resume requirement moves from DEFERRED to IMPLEMENTED
  (`AwaitUnsafeOnCompleted_Neo` suspend body + `ILAsyncContext<T>.MoveNext`
  resumption + `get_Task` context routing), gated by the deterministic TCS probe.

## Impact

- **Engine (Neo-only, `#if ENABLE_NEO_MODE`):**
  `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs` (piece 1
  zero-extend; piece 2 `AwaitUnsafeOnCompleted_Neo` body; piece 3
  `_currentAsyncContext` sink-swap in `SetResult`/`SetException` + `get_Task`
  `SmContextMap` branch + the `SmContextMap` field);
  `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (piece 1 generic
  Call-return zero-fill if the implementer picks that option; the existing
  `HoistNeoILValueToHeap` helper is reused only for the in-frame-VT-local edge
  case);
  `ILRuntime/Runtime/Intepreter/ILAsyncContext.cs` (piece 3 `MoveNext()` body +
  `MoveNextDelegate` field).
- **Tests:** `TestCases/NeoStep20Test.cs` (un-ignore + redesign `TC8` to the
  deterministic completion signal); `ILRuntimeTestBase/TestFramework/TestClass3.cs`
  (`TestCLRBinding.CompleteIncompleteTask` to expose the TCS).
- **Regression risk:** piece 1 option B (generic Call-return zero-fill) touches
  the shared Neo `Call`/CLR-redirect dispatch used by ALL CLR redirects, not just
  async; mitigated by dump-gating the slot-width threading and the full `NeoStep`
  smoke (218/218) as the gate. Pieces 2+3 are Neo-only files; the sync slice
  (TC1/TC4/TC6/TC7 + TC9/TC10) is untouched and stays green (it never suspends).
- **Legacy-neutral:** every engine edit is `#if ENABLE_NEO_MODE` or in Neo-only
  files; Legacy `ExecuteR` and the autogen Legacy arms are untouched (plain
  `Debug` build = 0 errors). Legacy is the reference.
