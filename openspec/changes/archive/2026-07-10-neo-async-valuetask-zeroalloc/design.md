# Design: neo-async-valuetask-zeroalloc

**Capability:** neo-async  **Wave:** completion-3, child 6  **Status:** APPLIED + VERIFIED

## Problem (the allocation this child eliminates)

The ValueTask<T> suspend path shipped in child 4 (`neo-async-valuetask-asyncvoid`) allocates
a `TaskCompletionSource<T>` bridge (and its `Task<T>`) on EVERY suspended async ValueTask<T>
state machine:

- `ILAsyncContext<T>` (the `IValueTaskSource<T>` / `IAsyncStateMachine` bridge) had a
  `readonly TaskCompletionSource<T> tcs = new TaskCompletionSource<T>(...)` field,
  initialized eagerly in the ctor.
- The suspend branch of `AsyncValueTaskMethodBuilder_T_GetTask_Neo` called
  `ctx.GetTaskBridge()` (returning `tcs.Task`) + `WrapBridgeAsValueTask`, which built a
  `ValueTask<T>(Task<T>)` -- a ValueTask backed by the bridge `Task<T>`.

So each suspended ValueTask<T> method allocated: (a) the `ILAsyncContext<T>` itself
(present on both old + new paths -- the IValueTaskSource holder), AND (b) the
`TaskCompletionSource<T>` + its `Task<T>` (the bridge -- the allocation this child
eliminates).

## Assessment (probe-first): does ILAsyncContext<T> already implement IValueTaskSource<T>?

**YES.** `ILAsyncContext<T> : IValueTaskSource<T>, IAsyncStateMachine, IAsyncContextSink`
already implements `IValueTaskSource<T>` via a private `ManualResetValueTaskSourceCore<T>
core` field (a STRUCT -- zero heap alloc). The live surface (`GetResult(token)`,
`GetStatus(token)`, `OnCompleted(...)`) delegates to `core`. This was wired in the
Step-20-suspend slice; child 4's OQ3 note explicitly deferred the zero-alloc path:
"A zero-alloc custom Task<T> from IValueTaskSource<T> is a later optimization (Non-Goal)."
**This child IS that later optimization.** The scope is a FOCUSED redirect change -- no
IValueTaskSource implementation needed.

## Mechanism (the IValueTaskSource-backed ValueTask<T>)

The zero-alloc path returns a `ValueTask<T>` backed DIRECTLY by the parked context's
`IValueTaskSource<T>` (the `ILAsyncContext<T>` itself) via the public
`ValueTask<T>(IValueTaskSource<T>, short)` ctor -- NO `Task<T>` / `TaskCompletionSource<T>`
bridge.

### `ILAsyncContext<T>` changes
1. **Lazy TCS.** `tcs` becomes a plain (non-readonly, non-initialized) field; it is
   allocated ONLY inside `GetTaskBridge()` (the Task-backed fallback, still used by
   `AsyncTaskMethodBuilder<T>` suspend). The ValueTask path never calls `GetTaskBridge` ->
   no TCS alloc.
2. **Complete the IValueTaskSource.** `CompleteResult` / `CompleteException` now call
   `core.SetResult` / `core.SetException` UNCONDITIONALLY (the accessor reads `core`); the
   TCS is completed only if lazily created (`tcs?.SetX`).
3. **Non-generic sink accessors.** Added to `IAsyncContextSink` + `ILAsyncContext<T>`:
   `GetSourceToken()` (= `core.Version`), `GetSourceIsCompleted(token)`,
   `GetSourceIsFaulted(token)`, `GetSourceResult(token)`. Non-generic so the redirect
   (non-generic, holds an `IAsyncContextSink`) can read the IValueTaskSource state without
   reflection on the closed T. `GetSourceIsCompleted` maps `ValueTaskSourceStatus` (any of
   Succeeded/Faulted/Canceled = completed); `GetSourceResult` returns the result (rethrows
   on a faulted source -- matches `ValueTask<T>.Result`).

### `AsyncValueTaskMethodBuilder_T_GetTask_Neo` suspend branch (the core change)
Replaced `WrapBridgeAsValueTask(method, ctx.GetTaskBridge())` with
`BuildZeroAllocValueTask(method, ctx, out token, out isZeroAlloc)`:
- Resolves `ValueTask<T>(IValueTaskSource<T>, short)` via reflection; invokes it with
  `(ctx, ctx.GetSourceToken())`.
- Stashes `ctx` (the IValueTaskSource) + the token in `_currentValueTaskState` (the
  ThreadStatic slot the accessors read).
- **Defensive fallback:** if the ctor is unexpectedly absent, `isZeroAlloc=false` and the
  caller falls back to `ctx.GetTaskBridge()` + `WrapBridgeAsValueTask` (re-introduces the
  allocation but preserves suspend+resume correctness). The ctor exists on
  netstandard2.0+ / netcoreapp3.0+, so the fallback is never taken in practice.

### Accessor redirects (`get_IsCompleted` / `get_IsFaulted` / `get_Result`)
Priority order: **IValueTaskSource** (zero-alloc suspend) > **BridgeTask** (sync-faulted /
Task fallback) > **SyncResult** (sync-success) > default. The accessor reads the
IValueTaskSource state via the non-generic sink methods.

## Measurement (the decisive proof)

GC byte deltas are muddied by SHARED allocations present on both old + new paths (the boxed
`ValueTask<T>` return via `Activator.CreateInstance` + the `MoveNext` frame + JIT caches).
So the decisive measurement is a DIRECT bridge-allocation counter:
`NeoAsyncAllocCounters.BridgeTaskAllocs` (public, non-gated static), incremented at the
exact TCS allocation site (`GetTaskBridge`). A second counter, `ContextAllocs`, counts the
`ILAsyncContext<T>` ctor (present on both paths -- proves the context IS still allocated;
the zero-alloc claim is specifically about the BRIDGE).

`NeoStep20_VT_ZeroAlloc` (the probe):
- **CONTROL:** a `Task<int>` suspend drives `BridgeTaskAllocs >= 1` (the Task path still
  allocates the bridge) -- proves the counter is load-bearing and non-trivially zero.
- **DECISIVE:** a `ValueTask<int>` suspend drives `BridgeTaskAllocs == 0` (the zero-alloc
  path) + `ContextAllocs >= 1` (the IValueTaskSource holder is still allocated -- the
  honest scope).
- **Correctness gates:** GATE 1 (truly suspended), GATE 2 (resumed, result == n+3), GATE 3
  (bridge counter still 0 post-resume -- `CompleteResult` did not lazily allocate a TCS).
- **Stash-toggle proof:** force the bridge fallback -> probe FAILs (the decisive assertion
  fires); restore -> PASS. The zero-alloc change is load-bearing for the alloc delta.

## Honest residual (true zero-alloc vs reduced-alloc)

This is NOT a fully zero-alloc ValueTask<T> path. Per suspend, these allocations REMAIN
(out of scope for this child; present on both old + new paths):
- The `Activator.CreateInstance(ValueTask<T>, ...)` BOX in `get_Task` (the boxed
  `ValueTask<T>` written via `WriteNeoValueType`). This is a SHARED allocation (both the
  bridge path and the IValueTaskSource path go through `Activator`). Eliminating it would
  require a non-reflective `ValueTask<T>` construction (a typed fast-path per closed T).
- The `ILAsyncContext<T>` itself (the IValueTaskSource holder -- counted by `ContextAllocs`).
- The `Action` resume delegate + the `MoveNext` sub-frame.

What this child ELIMINATES is the `TaskCompletionSource<T>` + its `Task<T>` (the bridge) --
the allocation that was SPECIFIC to the bridge path. This is the reduction the OQ3 note
targeted ("A zero-alloc custom Task<T> from IValueTaskSource<T>").

## Files
- `ILRuntime/Runtime/Intepreter/ILAsyncContext.cs` (Neo-gated): lazy `tcs`; `core`-first
  completion; the 4 non-generic sink accessors; counter increments.
- `ILRuntime/Runtime/Intepreter/NeoAsyncAllocCounters.cs` (NEW, non-gated public): the
  counters.
- `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs` (Neo-gated): the suspend
  branch + `BuildZeroAllocValueTask` + the accessor priority order.
- `ILRuntimeTestBase/TestFramework/TestClass3.cs`: the counter host helpers.
- `TestCases/NeoStep20Test.cs`: `NeoStep20_VT_ZeroAlloc` + the Task-suspend baseline.

## Legacy-neutral
All runtime edits are Neo-gated EXCEPT `NeoAsyncAllocCounters.cs` (intentionally non-gated:
the test harness compiles against the non-Neo ILRuntime DLL, so the counter type must be
visible there; the counter is a pure static with zero behavior on Legacy, and the
Neo-gated increments fire only under the Debug_Neo CLI). Plain-`Debug` CLI build: 0 errors.

## Durable findings
1. **`ILAsyncContext<T>` already implements `IValueTaskSource<T>`** (via a struct
   `ManualResetValueTaskSourceCore<T>` field) -- the zero-alloc ValueTask path was always a
   focused redirect change, never a "implement IValueTaskSource" task. The OQ3 deferral in
   child 4 was the explicit handoff to this child.
2. **The bridge-specific allocation is the `TaskCompletionSource<T>` (eagerly allocated in
   the ctor), NOT the `ValueTask<T>(Task<T>)` ctor call.** Making the TCS lazy (allocated
   only in `GetTaskBridge`) is the lever; the `WrapBridgeAsValueTask` box is a SHARED
   allocation (both paths go through `Activator.CreateInstance`), so it is NOT the
   bridge-specific saving.
3. **A non-generic sink accessor is the clean way to read the IValueTaskSource state from
   the redirect.** The redirect holds an `IAsyncContextSink` (non-generic); reading
   `core.GetStatus(token)` would otherwise need reflection on the closed T. Adding
   `GetSourceIsCompleted/IsFaulted/GetResult(token)` to the sink interface avoids that.
4. **The token is `core.Version`, stable for the single-completion suspend model.** The
   `ManualResetValueTaskSourceCore` requires `Reset()` before reuse; but the suspend model
   completes the core exactly once (multi-await REUSES the context, but the driver observes
   only the first suspend's ValueTask), so no `Reset()` is needed and the token is stable.
5. **A direct allocation counter beats GC byte deltas for a focused alloc claim.** GC byte
   deltas conflate the target alloc with shared allocs (the Activator box, the MoveNext
   frame); a counter incremented at the exact alloc site is decisive. The counter MUST be
   public + non-Neo-gated if the test harness compiles against the non-Neo runtime DLL.
