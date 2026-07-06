# neo-async

Neo mode async/await execution model: the compiler-generated state machine is
driven through custom Neo builder redirections (NOT the CLR `IAsyncStateMachine`
interface / `CrossBindingAdaptor`), the synchronous-completion path is
exercised end-to-end, and the frame-to-heap state-machine hoist primitive +
`ILAsyncContext<T>` bridge ship as foundation for the deferred suspend path.

**Proven scope (sync Task<int>):** the generic `Task<T>` single-await + nested
sync shapes (TC1 + TC7 in `TestCases/NeoStep20Test.cs`) — the full sync path
`Start` → `DriveMoveNext` (fresh pooled interpreter) → `GetAwaiter` redirect →
`get_IsCompleted` redirect → `GetResult` → `SetResult` (stash on the SM-keyed
`SmTaskMap`) → `get_Task`. The other sync shapes (non-generic `Task`,
`ValueTask`, multi-await, exception, `async void`) are blocked by the
pre-existing `[NEO-CLRSTRUCT-FIELD-OF-IL]` edge (a CLR-struct field of an IL
instance is laid out as a reference slot but the JIT `ldflda` addresses it as a
primitive offset → OOB); they ship as REQUIREMENTS here and are re-enabled when
that follow-up lands. The truly-async suspend/resume path is a DEFERRED
sub-requirement (`neo-step20-async-suspend`).

## Requirements

### Requirement: Neo async method builder redirection (synchronous-completion scope)

Neo mode SHALL support C# `async` methods by intercepting the
`AsyncTaskMethodBuilder<T>`, `AsyncTaskMethodBuilder`,
`AsyncValueTaskMethodBuilder<T>`, `AsyncValueTaskMethodBuilder`, and
`AsyncVoidMethodBuilder` builder APIs with custom Neo redirections
(`CLRRedirectionDelegateNeo`-signature) that drive the compiler-generated state
machine through the Neo call machinery WITHOUT dispatching the state machine
through the CLR `IAsyncStateMachine` interface or creating a
`CrossBindingAdaptor`. The custom redirections SHALL override (FIRST-registered-
wins) the autogen Neo builder stubs by registering in the `AppDomain` ctor
(which runs before the test-harness binding initializer). `Start<TSM>(ref sm)`
SHALL run the state machine's `MoveNext` via a fresh pooled interpreter (the
Step 19 `NeoInvokeSub` shape), and `SetResult` / `SetException` SHALL stash the
result/exception on a per-state-machine auxiliary map (`SmTaskMap`) so the
`Task` getter produces a completed/faulted task. This requirement covers the
SYNCHRONOUS-completion path only (every awaited awaitable observed
`IsCompleted == true`); the suspend/resumption path is a DEFERRED
sub-requirement tagged below.

#### Scenario: Sync-completing async method returning Task<T>

- **WHEN** Neo mode executes an `async Task<T>` method whose body is
  `return await Task.FromResult(value);` (or equivalent, where every awaitable
  is already complete)
- **THEN** the `Start` redirect SHALL run `MoveNext` to terminal state via a
  fresh pooled interpreter, `SetResult` SHALL stash the value on the
  SM-keyed `SmTaskMap`, and the `get_Task` redirect SHALL return a completed
  `Task<T>` whose `.Result` equals `value`
- (PROVEN GREEN — `NeoStep20_TC1_SyncTaskOfT`.)

#### Scenario: Sync-completing async method returning Task

- **WHEN** Neo mode executes an `async Task` method (non-generic) whose
  awaitables are all already complete
- **THEN** the redirects SHALL produce a completed `Task` (no `Result`), with
  the same `Start`→`MoveNext`→`SetResult`→`get_Task` flow
- (BLOCKED on `[NEO-CLRSTRUCT-FIELD-OF-IL]`; re-enable when that follow-up
  lands.)

#### Scenario: Sync-completing async method returning ValueTask<T>

- **WHEN** Neo mode executes an `async ValueTask<T>` method whose awaitables
  are all already complete
- **THEN** the `get_Task` redirect SHALL return a `ValueTask<T>` constructed
  via `ValueTask<T>.FromResult(value)` (the synchronous fast path), NOT via
  an `IValueTaskSource<T>` (which is the deferred suspend path)
- (BLOCKED on `[NEO-CLRSTRUCT-FIELD-OF-IL]`; re-enable when that follow-up
  lands.)

#### Scenario: async void completes synchronously

- **WHEN** Neo mode executes an `async void` method whose awaitables are all
  already complete
- **THEN** the `Start` redirect SHALL run `MoveNext` to terminal state and
  `SetResult` SHALL complete without producing a task (there is no
  `get_Task`); the method returns normally to the caller
- (BLOCKED on `[NEO-CLRSTRUCT-FIELD-OF-IL]`; re-enable when that follow-up
  lands.)

#### Scenario: Multiple awaits, all already complete

- **WHEN** Neo mode executes an async method with two or more `await`
  expressions, each on an already-complete awaitable
- **THEN** the state machine SHALL progress through each await state (the
  `IsCompleted` short-circuit skips `AwaitUnsafeOnCompleted`), reach terminal
  state, and the result SHALL reflect the final value
- (BLOCKED on `[NEO-CLRSTRUCT-FIELD-OF-IL]`; re-enable when that follow-up
  lands.)

#### Scenario: Synchronous exception faults the task

- **WHEN** an `async Task<T>` (or `Task`) method throws synchronously inside
  its body (before any await, or at an await whose awaiter is a faulted
  already-complete task)
- **THEN** `SetException` SHALL stash the exception and the `get_Task` getter
  SHALL return a faulted `Task<T>` whose `.Exception` carries the thrown
  exception, mirroring native C# `async` fault semantics
- (BLOCKED on `[NEO-CLRSTRUCT-FIELD-OF-IL]`; re-enable when that follow-up
  lands.)

#### Scenario: async void synchronous exception propagates to caller

- **WHEN** an `async void` method throws synchronously
- **THEN** the exception SHALL propagate to the caller of the `async void`
  method synchronously (matching Legacy `AsyncVoidMethodBuilder` semantics),
  and SHALL NOT be swallowed or surface as an unobserved task exception
- (BLOCKED on `[NEO-CLRSTRUCT-FIELD-OF-IL]`; re-enable when that follow-up
  lands.)

#### Scenario: Nested async (outer awaiting a sync-completing inner async)

- **WHEN** an outer `async Task<T>` method awaits an inner `async Task<U>`
  method, and the inner is sync-completing
- **THEN** the inner's `MoveNext` SHALL run to terminal state (a separate heap
  state-machine instance, driven via its own fresh-interpreter `Start`), the
  inner's completed `Task<U>` SHALL be observed as `IsCompleted` by the outer,
  and the outer SHALL complete synchronously with the combined result
- (PROVEN GREEN — `NeoStep20_TC7_NestedAsync`.)

### Requirement: Neo async Start drives MoveNext via the Neo call convention (not the CLR interface)

The Neo async `Start<TSM>(ref sm)` builder redirection SHALL invoke the state
machine's `MoveNext` IL method through the Neo call convention. The state
machine is a HEAP `ILTypeInstance` (loaded as a reference type despite the C#
`struct`). `Start` SHALL obtain a fresh pooled interpreter
(`RequestILIntepreter`), write the SM heap reference into the fresh frame's
slot-0, run `ExecuteNeo`, read the result, and `FreeILIntepreter` in `finally`
(the Step 19 `NeoInvokeSub` shape). The redirect SHALL NOT box the state
machine to an adaptor, SHALL NOT obtain or call a CLR `IAsyncStateMachine`
adaptor, and SHALL NOT recurse `ExecuteNeo` in-place on the caller's in-flight
frame (the in-place premise corrupted the caller's frame between `Start` and
`get_Task`). The async resumption path (deferred to `neo-step20-async-suspend`)
reuses the same fresh-pooled-interpreter pattern.

#### Scenario: Start invokes MoveNext via a fresh pooled interpreter

- **WHEN** the `Start` redirect executes for a sync-completing async method
- **THEN** `MoveNext` SHALL execute via a fresh pooled interpreter, with the
  state-machine heap instance as `this`, and the call SHALL NOT create an
  adaptor, SHALL NOT call `RequestILIntepreter` more than once per `Start`,
  and SHALL NOT dispatch through any CLR interface

#### Scenario: Builder redirect overrides the autogen stub

- **WHEN** the Neo mode builder redirections are registered for
  `AsyncTaskMethodBuilder<T>` (and the other builder types)
- **THEN** the custom Neo redirects SHALL override (FIRST-registered-wins) the
  autogen `*Neo` builder stubs for the same `MethodBase`s by registering in
  the `AppDomain` ctor (which runs before the test-harness binding
  initializer), so the in-frame `Start`→`MoveNext` path is the active
  implementation and the CLR-interface-dispatching autogen stubs are NOT
  invoked

### Requirement: Neo async Task getter produces a completed task on the synchronous path

The Neo `get_Task` builder redirection SHALL produce a completed task on the
synchronous-completion path. On that path it MUST return a completed `Task<T>`
/ `Task` carrying the result stashed by `SetResult`, or a faulted task carrying
the exception stashed by `SetException`. Because the CLR
`AsyncTaskMethodBuilder<T>` struct's internal `_task` field is opaque to the
Neo frame (a CLR-struct field nested in the SM), the stash mechanism SHALL use
a per-state-machine auxiliary map (`SmTaskMap`, a ThreadStatic
`Dictionary<ILTypeInstance, object>`) keyed by the state-machine heap instance;
the owning SM is recovered from the builder byref via
`RecoverSmFromBuilderByref`. The getter SHALL NOT touch `ILAsyncContext<T>` /
`IValueTaskSource<T>` on the synchronous path (those are the deferred suspend
path).

#### Scenario: Completed Task<T> after SetResult

- **WHEN** `SetResult(value)` ran during the synchronous `MoveNext`, followed
  by the `get_Task` getter
- **THEN** the getter SHALL return a `Task<T>` in the `RanToCompletion` state
  whose `.Result` equals `value`

#### Scenario: Faulted Task after SetException

- **WHEN** `SetException(ex)` ran during the synchronous `MoveNext`, followed
  by the `get_Task` getter
- **THEN** the getter SHALL return a `Task<T>` in the `Faulted` state whose
  `.Exception.InnerException` equals `ex`

### Requirement: Neo async AwaitUnsafeOnCompleted is a tagged deferral (synchronous scope boundary)

The Neo `AwaitUnsafeOnCompleted<TA,TSM>` and `AwaitOnCompleted<TA,TSM>` builder
redirections SHALL be REGISTERED (so a call site resolving to them does not
crash) but SHALL throw a `NotImplementedException` tagged
`neo-step20-async-suspend` if reached at runtime. Reaching these redirections
indicates a genuinely-incomplete awaitable (an await whose awaiter reports
`IsCompleted == false`), which is the DEFERRED suspend/resumption scope. This
is the explicit, machine-checkable scope boundary: the synchronous scope is
correct and complete for sync-completing async; the suspend scope is a
follow-up.

#### Scenario: Incomplete awaiter reaches the tagged deferral

- **WHEN** an async method awaits an awaitable whose `IsCompleted` is `false`
  (a genuinely asynchronous operation), triggering `AwaitUnsafeOnCompleted`
- **THEN** the Neo redirect SHALL throw a `NotImplementedException` whose
  message references `neo-step20-async-suspend`, and SHALL NOT infinite-loop,
  silently hang, or produce a wrong result

#### Scenario: Sync-completing await never reaches AwaitUnsafeOnCompleted

- **WHEN** an async method awaits an awaitable whose `IsCompleted` is `true`
- **THEN** the C# compiler's lowered `IsCompleted` short-circuit SHALL skip
  the `AwaitUnsafeOnCompleted` call entirely, and the synchronous scope SHALL
  complete without invoking the tagged deferral

### Requirement: Neo async frame-to-heap state-machine hoist primitive

Neo mode SHALL provide a frame-to-heap hoist helper (`HoistNeoILValueToHeap`)
that copies an in-frame IL value-type state machine into a fresh
`ILTypeInstance` constructed with `initializeCLRInstance: false` (no
`CrossBindingAdaptor`), preserving the state machine's primitive byte region
and reference-field region. The helper SHALL be a pure function (frame source
+ ref-region base + ILType → `ILTypeInstance`) — the inverse of
`CopyFrameToIL`. This helper is the load-bearing primitive for the deferred
suspend path (where `AwaitUnsafeOnCompleted` hoists the in-frame state machine
so it survives across the suspension); it SHALL ship standalone in the
synchronous scope so the suspend path can reuse a proven primitive, and SHALL
NOT be wired into any sync-path redirect in this scope.

#### Scenario: Hoist preserves primitive fields

- **WHEN** an IL value-type state machine with primitive fields is populated
  on a Neo frame and hoisted via the helper
- **THEN** the resulting `ILTypeInstance.Primitives` SHALL byte-for-byte match
  the source frame region, and reading any primitive field back through the
  heap instance SHALL yield the same value

#### Scenario: Hoist preserves reference fields

- **WHEN** an IL value-type state machine with one or more reference-type
  fields (e.g. a string local, an object captured by the async closure) is
  populated on a Neo frame and hoisted
- **THEN** the resulting `ILTypeInstance.ManagedObjects` SHALL contain the
  same object references as the source frame's mStack ref region, in the same
  order, and the references SHALL remain valid (GC-reachable) after the hoist

#### Scenario: Hoisted instance has no CrossBindingAdaptor

- **WHEN** the hoist helper constructs the target `ILTypeInstance`
- **THEN** the construction SHALL pass `initializeCLRInstance: false` so no
  `CrossBindingAdaptor` is created, and the `ILTypeInstance.CLRInstance` SHALL
  NOT be a CLR-side adaptor wrapper (the heap instance is held only by the
  async machinery, never exposed to CLR code as a CLR interface)

### Requirement: Neo ILAsyncContext bridge skeleton

Neo mode SHALL provide an `ILAsyncContext<T>` class implementing
`IValueTaskSource<T>` and `IAsyncStateMachine` as the async-continuation
bridge. The `IValueTaskSource<T>` surface (backed by a
`ManualResetValueTaskSourceCore<T>`) SHALL be live (`SetResultSync`/
`SetExceptionSync` + `GetResult`/`GetStatus`/`OnCompleted` round-trip). The
`IAsyncStateMachine.MoveNext` resumption body SHALL throw a
`NotImplementedException` tagged `neo-step20-async-suspend` (the resumption is
the deferred suspend scope's load-bearing deliverable). The synchronous scope
does NOT route the `Task` getter through `ILAsyncContext<T>` (it uses
`Task<T>`/`ValueTask<T>.FromResult` directly); the skeleton ships to de-risk
the suspend path's continuation plumbing and to give the suspend path a
concrete `IValueTaskSource<T>` type.

#### Scenario: IValueTaskSource synchronous round-trip

- **WHEN** `SetResultSync(value)` is called on an `ILAsyncContext<T>`, then
  `GetResult(token)` is called with the matching token
- **THEN** `GetResult` SHALL return `value`, and `GetStatus(token)` SHALL
  report a completed status

#### Scenario: Resumption is tagged-deferred

- **WHEN** `IAsyncStateMachine.MoveNext` is invoked on an `ILAsyncContext<T>`
- **THEN** it SHALL throw a `NotImplementedException` referencing
  `neo-step20-async-suspend`, and SHALL NOT infinite-loop or silently return

### Requirement: Neo async suspend and resumption (DEFERRED)

The Neo async SUSPEND and RESUMPTION paths SHALL be deferred to a follow-up
change (`neo-step20-async-suspend`). The SUSPEND path
(`AwaitUnsafeOnCompleted`/`AwaitOnCompleted` real implementation — wire the
`HoistNeoILValueToHeap` helper + register an `ILAsyncContext<T>` continuation
with the awaiter's `UnsafeOnCompleted`) and RESUMPTION path
(`ILAsyncContext<T>.MoveNext` restoring a hoisted state machine to a fresh
pooled interpreter, resuming at the await state, and completing the
`ManualResetValueTaskSourceCore<T>`) are DEFERRED. The synchronous scope
(requirements above) ships the builder redirection surface, the
`Start`→`MoveNext` fresh-interpreter routing, the sync `SetResult`/`get_Task`
paths, the frame-to-heap hoist primitive, and the `ILAsyncContext<T>`
skeleton precisely so the suspend path warm-seeds from proven primitives.
**This requirement also depends on `[NEO-CLRSTRUCT-FIELD-OF-IL]`** (the
awaiter field `<>u__1` is a CLR-struct field of the IL state machine — the same
addressing defect).

#### Scenario: Genuinely-asynchronous await (deferred)

- **WHEN** an async method awaits an awaitable that completes asynchronously
  (e.g. a `Task.Delay`-backed task, a pending I/O operation)
- **THEN** the suspend path (deferred to `neo-step20-async-suspend`) SHALL
  hoist the state machine, register the continuation, return a hot task, and
  on resumption restore + complete the task. In the synchronous scope this
  scenario hits the tagged `AwaitUnsafeOnCompleted` deferral (the scope
  boundary) and is NOT supported.
