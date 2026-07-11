## 1. JIT-dump reconnaissance (dump-gated discipline — decide before coding)

- [ ] 1.1 Build CLI `Debug_Neo` + TestCases `Debug`; write a minimal
  `Task<int> NeoStep20_Recon() => await Task.FromResult(42);` probe, run it,
  capture the JIT dump for the async method + its state machine
  `<NeoStep20_Recon>d__N`. Confirm the state machine is an IL value-type LOCAL
  in the async method's frame (Step 12 in-frame VT) and its fields
  (`<>t__builder`, `<>1__state`, `<>u__1` awaiter) are accessed via `_Inline`
  ops.
- [ ] 1.2 From the dump, resolve OQ1: does the `AsyncTaskMethodBuilder<T>`
  struct lay out an internal `_task`/result field the `SetResult`/`get_Task`
  redirects can read/write via the in-frame `_Inline` path, or is the builder
  opaque (forcing the per-builder-address auxiliary-map fallback)? Record the
  field offsets the JIT emits for the `<>t__builder` field accesses.
- [ ] 1.3 From the dump, resolve OQ2: does the `Start`→`MoveNext` call route
  as a normal IL value-type instance-method call (frame-native Ref Slot
  `this`, the VT-THIS-ADDR / Step 12b in-frame-VT-instance-call shape), or
  does the JIT box the state machine? If boxed, STOP and re-design (likely a
  Newobj-dest-typing-style JIT seed to force in-frame recognition).
- [ ] 1.4 Resolve OQ3: trace the autogen builder stub registration order vs
  the `AppDomain` ctor. Confirm whether custom redirects register
  last-wins-after-autogen, OR the autogen builder files must be regenerated
  to skip `*Neo` registration for builder types. Decide the registration site.

## 2. Frame-to-heap hoist helper (pure primitive; ship + probe standalone)

- [ ] 2.1 Add `HoistNeoILValueToHeap(ILType smType, byte* srcFrame, int
  srcPrimOff, int srcRefBase, AutoList mStack, int srcRefOff, int refCount)`
  to the `CopyFrameToIL` family in `ILIntepreter.Neo.cs` (Neo-only,
  `#if ENABLE_NEO_MODE`). Construct
  `new ILTypeInstance(smType, initializeCLRInstance: false)`, `CopyBlock`
  primitives via `MemoryMarshal.GetReference(heap.Primitives.AsSpan())`
  (NOT `GetArrayDataReference` — .NET 5+, design §0), copy the ref region
  `mStack[srcRefBase+srcRefOff+i]` → `heap.ManagedObjects[i]`.
- [ ] 2.2 Write a unit probe `NeoStep20_HoistPreservesPrimitives` (an IL VT
  with primitive fields, populated on a frame, hoisted, assert heap fields
  match). Add to `TestCases/NeoStep20Test.cs`.
- [ ] 2.3 Write `NeoStep20_HoistPreservesReferences` (an IL VT with a
  reference field, hoisted, assert the heap `ManagedObjects` reference matches
  + is GC-reachable).
- [ ] 2.4 Write `NeoStep20_HoistHasNoAdaptor` (assert the hoisted
  `ILTypeInstance.CLRInstance` is NOT a `CrossBindingAdaptorType`).
- [ ] 2.5 Confirm the helper is NOT wired into any redirect (it ships
  standalone for the suspend slice).

## 3. ILAsyncContext<T> skeleton (IValueTaskSource<T> live; MoveNext tagged NIE)

- [ ] 3.1 Create `ILRuntime/Runtime/Intepreter/ILAsyncContext.cs` with
  `class ILAsyncContext<T> : IValueTaskSource<T>, IAsyncStateMachine`. Fields:
  `ILTypeInstance stateMachine`, `ILMethod moveNextMethod`,
  `ManualResetValueTaskSourceCore<T> core`.
- [ ] 3.2 Implement the live `IValueTaskSource<T>` surface: `GetResult`,
  `GetStatus`, `OnCompleted` delegating to `core`; internal
  `SetResultSync`/`SetExceptionSync` calling `core.SetResult`/`core.SetException`.
- [ ] 3.3 Implement `IAsyncStateMachine.MoveNext` to throw
  `NotImplementedException("Neo async resumption: neo-step20-async-suspend
  (Step 20 suspend slice)")`.
- [ ] 3.4 Write a unit probe `NeoStep20_AsyncContextValueTaskSourceRoundTrip`
  (`SetResultSync(v)` + `GetResult(token)` == v; `GetStatus` == completed).
  Add to `TestCases/NeoStep20Test.cs`.
- [ ] 3.5 Write `NeoStep20_AsyncContextResumptionIsTaggedNIE` (invoke
  `MoveNext`, assert it throws the tagged NIE — use a try/catch flag, the
  DivideByZero-assertion pattern, or the harness's exception-as-failure
  convention inverted).

## 4. Custom Neo builder redirects — Create / SetStateMachine / Start

- [ ] 4.1 Create `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs`
  (Neo-only) with the builder redirect bodies. Start with `Create()` (no-op:
  write a default builder into the in-frame `<>t__builder` field via the Ref
  Slot) and `SetStateMachine(IAsyncStateMachine)` (no-op).
- [ ] 4.2 Implement `Start<TSM>(ref sm)` (D2): read the state-machine Ref
  Slot, resolve `smType.GetMethod("MoveNext")` (cache per-type), invoke
  `MoveNext` as an in-frame IL value-type instance method via the Neo call
  machinery (reuse the Step 8/12b in-frame-VT-instance-call path). NO fresh
  interpreter, NO CLR interface.
- [ ] 4.3 Register the redirects for `AsyncTaskMethodBuilder<T>` (all
  `T` arities the test harness binds), `AsyncTaskMethodBuilder`,
  `AsyncValueTaskMethodBuilder<T>`, `AsyncValueTaskMethodBuilder`,
  `AsyncVoidMethodBuilder`. Register AFTER autogen (per OQ3 resolution); add
  `RegisterNeoAsyncRedirections()` called from the `AppDomain` ctor or the
  test-harness binding init site (last-wins).
- [ ] 4.4 Write probe `NeoStep20_Start_InFrame_NoBoxing` (a sync-completing
  `async Task<int>`; assert it returns the right value AND — via a temp
  instrumentation — that no `ILTypeInstance` was created for the state
  machine, no `RequestILIntepreter` was called). Remove the instrumentation
  before completion.

## 5. Custom Neo builder redirects — SetResult / SetException / get_Task

- [ ] 5.1 Implement `SetResult(T)` / `SetResult()` per D3 (primary or
  fallback per OQ1): stash the result so `get_Task` can produce a completed
  task. Write the stashed `Task.FromResult(result)` (or faulted task for
  `SetException`) into the in-frame builder field, OR into the auxiliary map.
- [ ] 5.2 Implement `SetException(Exception)` (sync stash of a faulted task).
- [ ] 5.3 Implement `get_Task` (D3/D7): read the stashed task from the builder
  field / auxiliary map; for the sync path it is always completed/faulted.
  For `AsyncValueTaskMethodBuilder<T>.get_Task`, return
  `ValueTask<T>.FromResult(result)` (NOT `IValueTaskSource<T>` — that is the
  suspend slice).
- [ ] 5.4 Write probe `NeoStep20_SyncTaskOfT` (`async Task<int>` returning a
  constant via `await Task.FromResult`).
- [ ] 5.5 Write probe `NeoStep20_SyncTask` (`async Task`, non-generic).
- [ ] 5.6 Write probe `NeoStep20_SyncValueTaskOfT` (`async ValueTask<int>`,
  `FromResult` fast path).
- [ ] 5.7 Write probe `NeoStep20_AsyncVoid_Sync` (`async void`, fire-and-
  forget sync; assert side-effect observed).

## 6. Sync-completing probes — multi-await, value-return, nested

- [ ] 6.1 Write `NeoStep20_MultipleAwaitsAllComplete` (an async method with
  3 awaits, each on an already-complete task; assert the final value chains
  correctly).
- [ ] 6.2 Write `NeoStep20_AsyncExceptionFaultsTask` (an async method that
  throws synchronously; assert the returned task is `Faulted` with the right
  exception — read `.Exception.InnerException`).
- [ ] 6.3 Write `NeoStep20_AsyncVoidSyncExceptionPropagates` (an `async void`
  that throws; assert the exception propagates to the caller synchronously).
- [ ] 6.4 Write `NeoStep20_NestedAsyncSync` (an outer `async Task<int>`
  awaiting an inner `async Task<int>`, both sync-completing; assert the
  combined result + that the inner ran on the same frame via temp
  instrumentation, then remove it).
- [ ] 6.5 Write `NeoStep20_IncompleteAwaitHitsTaggedNIE` (an async method
  awaiting `Task.Delay(...)` — a genuinely-incomplete awaitable; assert the
  tagged `neo-step20-async-suspend` NIE is thrown, NOT an infinite loop.
  Use a tight test-timeout guard).

## 7. AwaitUnsafeOnCompleted / AwaitOnCompleted tagged-deferral stubs (scope boundary)

- [ ] 7.1 Implement `AwaitUnsafeOnCompleted<TA,TSM>` and
  `AwaitOnCompleted<TA,TSM>` Neo redirects as throw-tagged NIE stubs
  (`NotImplementedException("Neo async suspend path: neo-step20-async-suspend
  (Step 20 suspend slice)")`). Register them (so the call site resolves).
- [ ] 7.2 Confirm probe 6.5 hits exactly this stub (the machine-checkable
  scope boundary).

## 8. Regression gate + Legacy-neutral confirmation

- [ ] 8.1 Build CLI `Debug_Neo` (`--no-incremental` after adding any host
  type); build TestCases `Debug` (`--no-incremental`). Confirm 0 errors.
  Verify the built DLL mtime > source mtime (the stale-DLL gotcha).
- [ ] 8.2 Run the FULL `NeoStep` smoke (`NeoStep` filter): confirm
  181/181 baseline + the new `NeoStep20_*` probes all green. NO regressions.
  (Async bugs love to loop — any probe >10s = kill + investigate.)
- [ ] 8.3 Run the new `NeoStep20_*` probes on Legacy (plain `Debug` +
  `useRegister=true`): confirm Legacy behavior is unchanged (async on Legacy
  is the reference; the new probes should pass or fail identically to their
  pre-change state — Legacy async was already supported via the autogen
  StackObject redirects).
- [ ] 8.4 Confirm every runtime/codegen edit is `#if ENABLE_NEO_MODE`-gated;
  the Legacy `#else` autogen builder stubs are byte-identical (diff the
  autogen files — they should NOT be edited).
- [ ] 8.5 Stash-toggle the change (temporarily revert the custom redirects);
  confirm the `NeoStep20_*` probes FAIL on HEAD (the autogen stubs are
  non-functional) → proves the load-bearing fix. Restore.

## 9. Deferred-items doc + planning-context append

- [ ] 9.1 Append `## Findings -- neo-step20-async` to
  `openspec/changes/neo-completion-portfolio/planning-context.md`: the scoping
  decision (sync-first split), the builder-redirect set, the OQ1/OQ2/OQ3
  resolutions from the apply dumps, the frame-to-heap mechanism, the
  ILAsyncContext design, the explicit `neo-step20-async-suspend` follow-up
  scope.
- [ ] 9.2 Update `.trae/documents/neo-deferred-items.md`: add the
  `neo-step20-async-suspend` follow-up (the suspend/resume path) + any new
  edges surfaced during apply.
- [ ] 9.3 Update `.trae/documents/neo-handoff.md` §2 (Step 20 sync slice
  shipped) + §5/§6 (suspend slice is the next async unit) if needed.
