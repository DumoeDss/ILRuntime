## ADDED Requirements

### Requirement: Neo async method builder redirection (synchronous-completion scope)

Neo mode SHALL support C# `async` methods by intercepting the
`AsyncTaskMethodBuilder<T>`, `AsyncTaskMethodBuilder`,
`AsyncValueTaskMethodBuilder<T>`, `AsyncValueTaskMethodBuilder`, and
`AsyncVoidMethodBuilder` builder APIs with custom Neo redirections
(`CLRRedirectionDelegateNeo`-signature) that drive the compiler-generated state
machine through the Neo call machinery WITHOUT dispatching the state machine
through the CLR `IAsyncStateMachine` interface or creating a
`CrossBindingAdaptor`. The custom redirections SHALL override (last-wins) the
autogen Neo builder stubs. `Start<TSM>(ref sm)` SHALL run the state machine's
`MoveNext` as an in-frame IL value-type instance method on the caller's own
frame (no fresh interpreter), and `SetResult` / `SetException` SHALL stash the
result/exception so the `Task` getter produces a completed/faulted task. This
requirement covers the SYNCHRONOUS-completion path only (every awaited
awaitable observed `IsCompleted == true`); the suspend/resumption path is a
DEFERRED sub-requirement tagged below.

#### Scenario: Sync-completing async method returning Task<T>

- **WHEN** Neo mode executes an `async Task<T>` method whose body is
  `return await Task.FromResult(value);` (or equivalent, where every awaitable
  is already complete)
- **THEN** the `Start` redirect SHALL run `MoveNext` to terminal state on the
  caller's frame, `SetResult` SHALL stash the value, and the `get_Task`
  redirect SHALL return a completed `Task<T>` whose `.Result` equals `value`,
  with zero ILTypeInstance allocation on the synchronous path

#### Scenario: Sync-completing async method returning Task

- **WHEN** Neo mode executes an `async Task` method (non-generic) whose
  awaitables are all already complete
- **THEN** the redirects SHALL produce a completed `Task` (no `Result`), with
  the same in-frame `Start`→`MoveNext`→`SetResult`→`get_Task` flow

#### Scenario: Sync-completing async method returning ValueTask<T>

- **WHEN** Neo mode executes an `async ValueTask<T>` method whose awaitables
  are all already complete
- **THEN** the `get_Task` redirect SHALL return a `ValueTask<T>` constructed
  via `ValueTask<T>.FromResult(value)` (the synchronous fast path), NOT via
  an `IValueTaskSource<T>` (which is the deferred suspend path)

#### Scenario: async void completes synchronously

- **WHEN** Neo mode executes an `async void` method whose awaitables are all
  already complete
- **THEN** the `Start` redirect SHALL run `MoveNext` to terminal state and
  `SetResult` SHALL complete without producing a task (there is no
  `get_Task`); the method returns normally to the caller

#### Scenario: Multiple awaits, all already complete

- **WHEN** Neo mode executes an async method with two or more `await`
  expressions, each on an already-complete awaitable
- **THEN** the state machine SHALL progress through each await state (the
  `IsCompleted` short-circuit skips `AwaitUnsafeOnCompleted`), reach terminal
  state, and the result SHALL reflect the final value

#### Scenario: Synchronous exception faults the task

- **WHEN** an `async Task<T>` (or `Task`) method throws synchronously inside
  its body (before any await, or at an await whose awaiter is a faulted
  already-complete task)
- **THEN** `SetException` SHALL stash the exception and the `get_Task` getter
  SHALL return a faulted `Task<T>` whose `.Exception` carries the thrown
  exception, mirroring native C# `async` fault semantics

#### Scenario: async void synchronous exception propagates to caller

- **WHEN** an `async void` method throws synchronously
- **THEN** the exception SHALL propagate to the caller of the `async void`
  method synchronously (matching Legacy `AsyncVoidMethodBuilder` semantics),
  and SHALL NOT be swallowed or surface as an unobserved task exception

#### Scenario: Nested async (outer awaiting a sync-completing inner async)

- **WHEN** an outer `async Task<T>` method awaits an inner `async Task<U>`
  method, and the inner is sync-completing
- **THEN** the inner's `MoveNext` SHALL run to terminal state on the same
  frame as the outer (a separate in-frame state machine), the inner's
  completed `Task<U>` SHALL be observed as `IsCompleted` by the outer, and the
  outer SHALL complete synchronously with the combined result

### Requirement: Neo async Start drives MoveNext in-frame, not via the CLR interface

The Neo `Start<TSM>(ref sm)` builder redirection SHALL invoke the state
machine's `MoveNext` IL method as an in-frame IL value-type instance method
using the Neo call convention, treating the state machine (an in-frame IL
value-type local in the caller's frame) as the `this` operand via a frame-
native Ref Slot (Step 17). The redirect SHALL NOT box the state machine to an
`ILTypeInstance`, SHALL NOT obtain or call a CLR `IAsyncStateMachine`
adaptor, and SHALL NOT allocate a fresh interpreter (the call runs on the
caller's own `ExecuteNeo` frame). The `MoveNext` body's field accesses
(`<>1__state`, the awaiter field, user locals, the `<>t__builder` field) SHALL
resolve via the Step 12 in-frame value-type `_Inline` field operations.

#### Scenario: Start runs MoveNext on the caller's frame

- **WHEN** the `Start` redirect executes for a sync-completing async method
- **THEN** `MoveNext` SHALL execute as an in-frame IL value-type instance
  method call with `this` = the state machine's frame-native address, and
  SHALL NOT create an `ILTypeInstance` for the state machine, SHALL NOT call
  `RequestILIntepreter`, and SHALL NOT dispatch through any CLR interface

#### Scenario: State machine fields accessed in-frame

- **WHEN** `MoveNext` reads or writes a state-machine field (`<>1__state`,
  the awaiter, a user local, the `<>t__builder`)
- **THEN** the access SHALL resolve to the in-frame byte/ref region of the
  state machine local via the Step 12 `_Inline` field operations, producing
  the same values the C# compiler's lowered state machine expects

### Requirement: Neo async Task getter produces a completed task on the synchronous path

The Neo `get_Task` builder redirection SHALL produce a completed task on the synchronous-completion path.
On that path it MUST return a
completed `Task<T>` / `Task` carrying the result stashed by `SetResult`, or a
faulted task carrying the exception stashed by `SetException`. The stash
mechanism SHALL read/write the in-frame builder struct's fields via the Neo
in-frame field operations OR, if the builder struct is opaque to the JIT, a
per-builder auxiliary map keyed by the builder's frame address. The getter
SHALL NOT touch `ILAsyncContext<T>` / `IValueTaskSource<T>` on the synchronous
path (those are the deferred suspend path).

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

The Neo `AwaitUnsafeOnCompleted<TA,TSM>` and `AwaitOnCompleted<TA,TSM>` builder redirections SHALL be REGISTERED (so a call site resolving to them does not crash) but SHALL throw a `NotImplementedException` tagged
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

Neo mode SHALL provide a frame-to-heap hoist helper that copies an in-frame IL
value-type state machine into a fresh `ILTypeInstance` constructed with
`initializeCLRInstance: false` (no `CrossBindingAdaptor`), preserving the
state machine's primitive byte region and reference-field region. The helper
SHALL be a pure function (frame source + ref-region base + ILType →
`ILTypeInstance`) and SHALL be unit-probed in isolation. This helper is the
load-bearing primitive for the deferred suspend path (where
`AwaitUnsafeOnCompleted` hoists the in-frame state machine so it survives
across the suspension); it SHALL ship standalone in the synchronous scope so
the suspend path can reuse a proven primitive, and SHALL NOT be wired into any
sync-path redirect in this scope.

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
`ManualResetValueTaskSourceCore<T>`) SHALL be live and unit-probed
(`SetResultSync`/`SetExceptionSync` + `GetResult`/`GetStatus`/`OnCompleted`
round-trip). The `IAsyncStateMachine.MoveNext` resumption body SHALL throw a
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

The Neo async SUSPEND and RESUMPTION paths SHALL be deferred to a follow-up change (`neo-step20-async-suspend`). The SUSPEND path (`AwaitUnsafeOnCompleted`/`AwaitOnCompleted` real implementation) and RESUMPTION path (`ILAsyncContext<T>.MoveNext` restoring a
hoisted state machine to a fresh pooled interpreter, resuming at the await
state, and completing the `ManualResetValueTaskSourceCore<T>`) are DEFERRED to
a follow-up change (`neo-step20-async-suspend`). The synchronous scope
(requirements above) ships the builder redirection surface, the in-frame
`Start`→`MoveNext` routing, the sync `SetResult`/`get_Task` paths, the
frame-to-heap hoist primitive, and the `ILAsyncContext<T>` skeleton precisely
so the suspend path warm-seeds from proven primitives. This requirement is a
placeholder so the follow-up merges the rest of the design §26 contract.

#### Scenario: Genuinely-asynchronous await (deferred)

- **WHEN** an async method awaits an awaitable that completes asynchronously
  (e.g. a `Task.Delay`-backed task, a pending I/O operation)
- **THEN** the suspend path (deferred to `neo-step20-async-suspend`) SHALL
  hoist the state machine, register the continuation, return a hot task, and
  on resumption restore + complete the task. In the synchronous scope this
  scenario hits the tagged `AwaitUnsafeOnCompleted` deferral (the scope
  boundary) and is NOT supported.
