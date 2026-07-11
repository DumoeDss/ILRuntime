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
sub-requirement.

**Update 2026-07-06:** the `neo-step20-async-suspend` portfolio child shipped
only its Phase-1 reachability unblockers (B3 `Nop` dispatch, B2 non-generic-
awaiter `void-GetResult` guard, `Task.Delay` redirect); the suspend/resume
machinery (`AwaitUnsafeOnCompleted` suspend + `ILAsyncContext<T>` resumption)
remains deferred to split children `neo-async-controlflow-iscompleted` +
`neo-generic-redirect-resolution`.

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

### Requirement: Neo async branch-size correctness for IsCompleted-driven control flow

A Neo CLR-redirect return value (e.g. `TaskAwaiter<T>.get_IsCompleted`) that feeds
a `Brtrue` / `Brfalse` whose condition register is a slot wider than the redirect's
write width SHALL be zero-extended to the destination slot's allocated width. The
optimizer sizes `Brtrue` / `Brfalse` by the PHYSICAL slot width
(`localInfos[r1].Size`), and an async state machine reuses the `IsCompleted` dest
slot for a managed pointer elsewhere (a `Ldloca_S` -> `&awaiter`), so the slot is 8
bytes while the `bool` redirect writes only 4. Without zero-extension the 8-byte
branch read picks up stale non-zero high pointer bits and misroutes when
`IsCompleted == false` (the state machine jumped past the suspend block into a
blocking `Task.Result` -> an infinite hang). Zero-extending the `bool` result to 8
bytes (`*(long*)retDst = isCompleted ? 1 : 0`) makes the branch read clean so the
state machine falls through to the suspend block and REACHES
`AwaitUnsafeOnCompleted`.

This requirement is the confirmed root-cause fix for the MoveNext control-flow
hang. The generic hardening (zero-filling ANY narrower CLR-redirect return to its
dest slot width in the shared Neo Call-return path) is the preferred longer-term
form and is tracked as a follow-up; the async-specific zero-extension on
`get_IsCompleted` is the confirmed minimal gate.

#### Scenario: Incomplete await falls through to AwaitUnsafeOnCompleted (not into a blocking GetResult)

- **WHEN** an async method awaits an awaitable whose `get_IsCompleted` returns
  `false` and the `IsCompleted` dest slot is reused for a managed pointer (8-byte
  slot)
- **THEN** the zero-extended `bool` result SHALL make the `Brtrue` read a clean 0,
  the state machine SHALL fall through to the suspend block, and SHALL REACH the
  registered `AwaitUnsafeOnCompleted` handler
- (PROVEN by `NeoStep20_TC8_TrulyAsyncSuspendResume` GATE 1: the deterministic
  incomplete-await probe no longer hangs and the suspend fires; previously the
  state machine hung in `Task.Result`.)

### Requirement: Neo async suspend and resumption (IMPLEMENTED)

The Neo async SUSPEND and RESUMPTION paths SHALL be implemented (previously
DEFERRED). On a genuinely-incomplete awaitable (`get_IsCompleted == false`), the
SUSPEND path SHALL run inside `AwaitUnsafeOnCompleted_Neo` / `AwaitOnCompleted_Neo`:
recover the heap state-machine instance (`CurrentAsyncSm`), read the awaited `Task`,
build an `ILAsyncContext<T>` (T = the builder's result type), register the
continuation via `task.GetAwaiter().UnsafeOnCompleted(resumeAction)` (an `Action`
bound to the context's resume entry), park the context on a per-SM
`SmContextMap` (a ThreadStatic `Dictionary<ILTypeInstance, IAsyncContextSink>`
parallel to `SmTaskMap`), and return WITHOUT calling `SetResult`. The SM is a heap
`ILTypeInstance` whose `<>1__state` and `<>u__1` survive the suspension, so
`HoistNeoILValueToHeap` is NOT needed for the SM itself (retained for the in-frame-
VT-local edge case).

The RESUMPTION path SHALL run in `ILAsyncContext<T>.MoveNext` (the continuation
target) on the thread that completes the awaited `Task`: acquire a FRESH pooled
interpreter (`RequestILIntepreter`, balanced by `FreeILIntepreter` in `finally`),
restore the heap SM as slot-0 `this`, set the ThreadStatic `_currentAsyncContext`
sink, and run `ExecuteNeo(moveNext)` which resumes at the await state, reloads the
awaiter, calls `GetResult`, and runs to `SetResult` / `SetException`. The
`SetResult` / `SetException` redirects SHALL check `_currentAsyncContext` FIRST
(the sink-swap) and route the terminal result to `ctx.CompleteResult` /
`ctx.CompleteException` (completing the `TaskCompletionSource<T>` bridge) instead of
stashing on `SmTaskMap`. The `get_Task` getter SHALL detect suspension via
`SmContextMap` and return the context's `Task<T>` bridge (a
`TaskCompletionSource<T>`; a zero-alloc custom `Task<T>` from
`IValueTaskSource<T>` is a later optimization).

#### Scenario: Truly-async await suspends, resumes, and completes

- **WHEN** an async method awaits a `TaskCompletionSource`-backed `Task` whose
  `SetResult` is NOT called before the async method returns, and the owning TCS is
  then completed from the host
- **THEN** the async method's `get_Task` SHALL return a `Task<T>` that is NOT
  completed (the await TRULY SUSPENDED), the suspend path SHALL have registered a
  continuation on the awaited `Task`, completing the TCS SHALL fire the resumption,
  the resumed `MoveNext` SHALL call `GetResult` + continue + `SetResult`, and the
  bridge `Task<T>.Result` SHALL equal the resumed value
- (PROVEN by `NeoStep20_TC8_TrulyAsyncSuspendResume`: GATE 1 `!t.IsCompleted`
  proves suspend; GATE 3 `t.Result == N + 3` proves resume ran `GetResult` +
  continued + `SetResult` end-to-end.)

#### Scenario: get_Task routes to the context bridge when the SM suspended

- **WHEN** the SM suspended on a genuinely-incomplete awaitable and `get_Task` is
  called (in the driver frame, after `Start` returned)
- **THEN** `get_Task` SHALL recover the SM (scanning for `SmContextMap` entries,
  not only `SmTaskMap`) and return the context's `TaskCompletionSource<T>`-backed
  `Task<T>` bridge, which completes when the resumption routes `SetResult` to the
  context

#### Scenario: A sync nested async invoked during a resume does NOT inherit the outer resume context

- **WHEN** a resumed SM's `MoveNext` invokes another async method that completes
  synchronously (its `Start` -> sync `DriveMoveNext` runs INSIDE the resume scope,
  with `sink == null`)
- **THEN** the sync nested drive SHALL run with `_currentAsyncContext == null`
  (CLEARED, not inherited from the outer resume), so the nested SM's `SetResult`
  stashes in `SmTaskMap` (the correct sync path) instead of misrouting to the
  outer resume context's bridge; the outer SM's own later `SetResult` completes the
  outer bridge EXACTLY ONCE with the outer SM's value
- **AND** the implementation SHALL assign `_currentAsyncContext = sink`
  UNCONDITIONALLY in `DriveMoveNextCore` (not `if (sink != null)`), relying on the
  existing `prevCtx` save/restore to reinstate the outer resume context after the
  nested sync drive
- (PROVEN by `NeoStep20_TC11_NestedSyncAsyncAfterSuspend` GATE 3: the outer bridge
  value is the outer SM's own result (`n + 50`), not the nested's misrouted value
  (`6`). Stash-toggle: reverting the one-line fix makes GATE 3 FAIL with
  `t.Result == 6` -- the nested's `SetResult` misrouted to the outer bridge, then
  the outer `SetResult` double-completed it.)

#### Scenario: An ambiguous multi-Task state machine fails LOUD (single-Task shape only)

- **WHEN** `GetAwaitedTaskFromSm` scans a suspended/resuming SM's heap fields and
  finds MORE THAN ONE directly-hoisted `Task` / `Task<T>` reference field (the
  currently-awaited `Task` cannot be disambiguated -- `ManagedObjects` is
  field-declaration order, not assignment order)
- **THEN** the scan SHALL throw a TAGGED `NotImplementedException` ("Neo async
  multi-Task awaiter not supported (single-Task shape only); the currently-awaited
  Task cannot be disambiguated") rather than silently returning the wrong
  (highest-field-index) `Task` -- a recovery-miss MUST fail the SAME way across all
  shapes (loud NIE, never silent-skip)
- **AND** the SINGLE-`Task` shape (one hoisted `Task` / `Task<T>` field -- the
  common case: an explicit-local single await, e.g. TC8) SHALL keep working
  unchanged (the scan returns the one `Task`)
- (SCOPE: multi-`Task` SM support is DEFERRED to the multi-await follow-up. The NIE
  converts the prior silent-wrong-result into a loud, tagged failure for the
  deferred case.)

## Accepted-known limitations (review findings C/D/E + 2 deferred items)

The truly-async path is SOUND for its gated scope (a single `Task`-local whose
`TaskCompletionSource` is completed from the host, single-await SM). The following
are accepted-known limitations recorded honestly (not hidden):

- **C (Option A 8-byte write, LOW):** `TaskAwaiter_T_GetIsCompleted_Neo` writes
  `*(long*)retDst` (8 bytes) unconditionally. This is safe for the gated async SM
  (the `IsCompleted` dest slot is 8 bytes -- reused for a managed pointer) but is
  NOT sized by the actual allocated slot width. If the optimizer ever allocates a
  4-byte slot for an `IsCompleted` result, the 8-byte write clobbers the adjacent
  register. Option B (generic zero-fill sized by the dest slot width, in the shared
  Neo Call-return path) is the proper fix and is tracked as a follow-up; Option A
  is the confirmed minimal gate.
- **D (`SmContextMap` driver-thread leak, LOW):** `SetResult` / `SetException`
  remove `SmContextMap[sm]` on resume, but the resume runs on a threadpool thread
  whose ThreadStatic `SmContextMap` is empty (the entry was parked on the DRIVER
  thread during `Start` -> suspend). `get_Task` (driver thread) reads but does not
  remove it. So the driver thread's `SmContextMap[sm]` entry is never cleaned up
  (bounded growth: one entry per distinct suspended SM per thread). Not a
  correctness issue for a single resume.
- **E (custom / non-`Task` awaiters, INFO):** `GetAwaiterTask` / the direct-`Task`
  scan gate on the object being a `Task` / `TaskAwaiter` / `TaskAwaiter<T>`. A
  custom awaiter, a `ConfiguredTaskAwaitable` awaiter, or any non-`Task` awaiter
  returns `null` from both, so `SuspendStateMachine` throws "could not recover the
  awaited task" (a FAULT at suspend, not silent). Not previously documented.
- **Deferred (multi-await SM):** an SM with >= 2 `await` expressions re-suspends on
  the already-completed first Task after resume (state-machine state/awaiter-slot
  issue), looping. Broader than the original "two INCOMPLETE awaits" deferral; it
  manifests for "two awaits, one incomplete" too. (TC11 uses a single-await SM and
  invokes the nested sync async via a plain call to avoid this.)
- **Deferred (nested-call-in-resume `get_Task` scan):** when a resumed SM calls a
  nested async, the nested's `get_Task` -> `RecoverSmForGetTask` mStack scan
  misidentifies the RESUMING SM (still parked in `SmContextMap` mid-resume) and
  returns its (incomplete) bridge Task; reading that Task's result then BLOCKS.
  Cleaning `SmContextMap[sm]` at resume start (or a frame-scoped scan) is the
  follow-up.
