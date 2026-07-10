# Tasks: neo-async-valuetask-zeroalloc

Status: APPLIED + VERIFIED (completion-3 wave, child 6, implementer-1). All runtime
edits Neo-only (`#if ENABLE_NEO_MODE` files) except the public `NeoAsyncAllocCounters.cs`
(intentionally non-gated -- see Block 1.4); test-harness edits are Neo-test-only.
Legacy-neutral.

## Block 0 -- assessment (probe-first: does ILAsyncContext<T> implement IValueTaskSource<T>?)
- [x] 0.1 Read `ILAsyncContext.cs`: `ILAsyncContext<T> : IValueTaskSource<T>,
      IAsyncStateMachine, IAsyncContextSink` -- YES, already implements
      `IValueTaskSource<T>` via a private `ManualResetValueTaskSourceCore<T> core` field.
      The `core` is a STRUCT field (zero heap alloc). Conclusion: the zero-alloc path is a
      FOCUSED redirect change (no IValueTaskSource implementation needed).
- [x] 0.2 Confirm the allocation that this child eliminates: the `tcs` field was a
      `readonly TaskCompletionSource<T>` initialized in the ctor -> a TCS (+ its `Task<T>`)
      allocated on EVERY context construction (once per suspended SM). The zero-alloc path
      returns a `ValueTask<T>` backed by `core` (the IValueTaskSource<T>) directly.

## Block 1 -- the zero-alloc mechanism (the IValueTaskSource-backed ValueTask<T>)
- [x] 1.1 `ILAsyncContext<T>`: make `tcs` LAZY (allocated ONLY in `GetTaskBridge()`, the
      Task-backed fallback). The ValueTask path never calls it -> no TCS alloc.
      (`ILAsyncContext.cs`.)
- [x] 1.2 `ILAsyncContext<T>.CompleteResult/CompleteException`: complete the `core`
      (IValueTaskSource) UNCONDITIONALLY; complete the TCS only if it was lazily created
      (`tcs?.SetX`). The accessor reads the `core`, not the TCS.
- [x] 1.3 `IAsyncContextSink` + `ILAsyncContext<T>`: add `GetSourceToken()` (=
      `core.Version`), `GetSourceIsCompleted(token)`, `GetSourceIsFaulted(token)`,
      `GetSourceResult(token)` -- non-generic so the redirect (non-generic, holds the sink)
      can read the IValueTaskSource state without reflection on the closed T.
- [x] 1.4 `NeoAsyncAllocCounters.cs` (NEW, non-gated public static): `BridgeTaskAllocs` +
      `ContextAllocs` counters, incremented at the exact allocation sites. NOT
      `#if ENABLE_NEO_MODE`-gated: the test harness (ILRuntimeTestBase / TestCases) compiles
      against the plain `Debug` (non-Neo) ILRuntime DLL, so the counter type must be visible
      there; the Neo-gated increments fire at run time under the Debug_Neo CLI.
- [x] 1.5 `AsyncValueTaskMethodBuilder_T_GetTask_Neo` suspend branch (the core change):
      replace `WrapBridgeAsValueTask(method, ctx.GetTaskBridge())` with
      `BuildZeroAllocValueTask(method, ctx, out token, out isZeroAlloc)` -- builds a
      `ValueTask<T>(IValueTaskSource<T>, short)` via the public ctor; stashes the source +
      token in `_currentValueTaskState.IValueTaskSource` (falls back to the TCS bridge only
      if the ctor is unexpectedly absent, with `isZeroAlloc=false`). (`CLRRedirections.AsyncNeo.cs`.)
- [x] 1.6 `ValueTaskAccessorState`: add `IValueTaskSource` + `IValueTaskSourceToken`
      fields; the three accessor redirects (`get_IsCompleted` / `get_IsFaulted` /
      `get_Result`) check `IValueTaskSource` FIRST (zero-alloc suspend), then `BridgeTask`
      (sync-faulted / Task fallback), then `SyncResult` (sync-success).

## Block 2 -- the measurement probe (NeoStep20_VT_ZeroAlloc)
- [x] 2.1 `TestClass3.cs`: add `ResetAsyncAllocCounters` / `GetAsyncBridgeTaskAllocs` /
      `GetAsyncContextAllocs` host helpers.
- [x] 2.2 `NeoStep20Test.cs`: add `NeoStep20_VT_ZeroAlloc` -- a CONTROL (a `Task<int>`
      suspend drives `BridgeTaskAllocs >= 1`, proving the counter is load-bearing) + the
      DECISIVE assertion (a `ValueTask<int>` suspend drives `BridgeTaskAllocs == 0`) +
      suspend+resume correctness gates (GATE 1: suspended; GATE 2: resumed result == n+3;
      GATE 3: bridge counter still 0 post-resume). The GC byte delta is NOT zero-asserted
      (honest residual: the shared Activator box + MoveNext frame remain; the zero-alloc
      claim is specifically the TCS/Task<T> bridge).

## Block 3 -- build + verify
- [x] 3.1 Build CLI (`Debug_Neo`, 0 errors) + TestCases (`Debug`, 0 errors). Confirm
      plain-`Debug` CLI builds 0 errors (Legacy-neutral; the non-gated counter compiles in,
      the Neo-gated async files compile out).
- [x] 3.2 VT1-VT6: 6/6 GREEN (correctness preserved -- the zero-alloc path does not break
      suspend+resume).
- [x] 3.3 `NeoStep20_VT_ZeroAlloc`: PASS (the control allocates the bridge; the ValueTask
      path does not).
- [x] 3.4 **Stash-toggle proof:** force the bridge fallback in `BuildZeroAllocValueTask`
      (`ctor = null`) -> `NeoStep20_VT_ZeroAlloc` FAILs (the decisive assertion fires: the
      bridge IS allocated). Restore -> PASS. (Binding: the zero-alloc change is
      load-bearing for the alloc delta.)
- [x] 3.5 Full `NeoStep` smoke -> 274/0/0 (273 baseline + VT_ZeroAlloc; no regressions).
