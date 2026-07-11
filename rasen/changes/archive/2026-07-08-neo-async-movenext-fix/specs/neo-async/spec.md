## MODIFIED Requirements

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

## REMOVED Requirements

### Requirement: Neo async AwaitUnsafeOnCompleted is a tagged deferral (synchronous scope boundary)

(Removed: the tagged `NotImplementedException` deferral is replaced by the real
suspend body above. The redirect-resolution + deterministic-probe characterization
that this requirement carried is subsumed by the IMPLEMENTED suspend/resume
requirement and the branch-size correctness requirement; the "reaching the tagged
NIE without hanging is DEFERRED" scenario is resolved by `NeoStep20_TC8` now
un-ignored and GREEN.)
