# neo-async (delta for neo-async-multi-await)

This delta MODIFIES the canonical `openspec/specs/neo-async/spec.md`. It extends
the suspend/resume requirement to the multi-await shape (a state machine with
>= 2 `await` expressions) and narrows the single-`Task` NIE guard.

## MODIFIED: Requirement "Neo async suspend and resumption (IMPLEMENTED)"

The Neo async SUSPEND and RESUMPTION paths SHALL be implemented for a state
machine with ANY NUMBER of `await` expressions (previously: the single-`Task`
shape only; the multi-`Task` shape threw a tagged NIE).

On a genuinely-incomplete awaitable (`get_IsCompleted == false`), the SUSPEND
path SHALL recover the CURRENTLY-AWAITED `Task` from the compiler-generated
awaiter field (`<>u__1`, which Roslyn REUSES -- overwriting it with the active
awaiter before each `AwaitUnsafeOnCompleted` call) via `GetAwaiterTask`, so the
continuation is registered on the CORRECT `Task` at EACH suspend regardless of
how many `Task` operand fields the SM hoists. `ManagedObjects` is field-
declaration order (NOT assignment order), so a count-based scan of hoisted `Task`
fields is ambiguous for the multi-await shape and SHALL NOT be the primary
recovery path; the awaiter field is the unambiguous source (the single reused
`<>u__1` always holds the active awaiter at suspend time).

The RESUMPTION path (`ILAsyncContext<T>.MoveNext`) is UNCHANGED: the resumed
MoveNext reloads the awaiter from `<>u__1` (dispatched by `<>1__state`), calls
`GetResult`, and runs to `SetResult` / `SetException`; the
`TaskAwaiter_T_GetResult_Neo` fallback (the reloaded awaiter's `m_task` is null
after the F-10 boxed-struct heap round-trip) recovers the active `Task` via the
SAME awaiter-field-first path (state-aware, multi-await-correct).

#### Scenario: Truly-async await suspends, resumes, and completes

- (UNCHANGED -- PROVEN by `NeoStep20_TC8_TrulyAsyncSuspendResume`.)

#### Scenario: get_Task routes to the context bridge when the SM suspended

- (UNCHANGED.)

#### Scenario: A sync nested async invoked during a resume does NOT inherit the outer resume context

- (UNCHANGED -- PROVEN by `NeoStep20_TC11_NestedSyncAsyncAfterSuspend`.)

#### Scenario: A multi-await state machine suspends and resumes at EACH await correctly

- **WHEN** an async method has TWO OR MORE `await` expressions, and at least two
  of them await a genuinely-incomplete `Task` (a `TaskCompletionSource`-backed
  `Task` whose `SetResult` is NOT called before the async method reaches each
  await), so the SM suspends at the first await, resumes, then suspends AGAIN at
  the second await, then resumes again, before reaching `SetResult`
- **THEN** the SUSPEND path SHALL recover the CURRENTLY-awaited `Task` from the
  `<>u__1` awaiter field at EACH suspend (not via an ambiguous scan of the SM's
  hoisted `Task` operand fields), register the continuation on that CORRECT
  `Task`, and the RESUMPTION path SHALL call `GetResult` on the awaiter for the
  await that is resuming (driven by `<>1__state`), so the final `SetResult`
  reflects the `GetResult` values of BOTH (all) awaited `Task`s in order
- **AND** the SM SHALL NOT fault with a "multi-Task awaiter not supported" NIE
  (that NIE is narrowed to the genuinely-ambiguous shape -- see below)
- (PROVEN by `NeoStep20_TC12_TwoIncompleteAwaits`: GATE 1 `!t.IsCompleted` proves
  await1 suspended; GATE 2 `t.Result == va + vb` proves BOTH resumes ran
  `GetResult` at the correct await point with the correct awaiter. Stash-toggle:
  on HEAD the tagged NIE faults the task during the first or second suspend;
  after the fix the SM suspends/resumes/resumes correctly.)

## MODIFIED: Scenario "An ambiguous multi-Task state machine fails LOUD"

The single-`Task` NIE guard (review Finding B) is NARROWED. It no longer fires
merely because the SM hoists MORE THAN ONE `Task` reference field (the common
multi-await shape, which now resolves via the `<>u__1` awaiter field). The TAGGED
`NotImplementedException` SHALL fire ONLY when the active `Task` CANNOT be
recovered by ANY means -- i.e. no awaiter field yielded a `Task` AND the direct-
`Task` scan found MORE THAN ONE `Task` (genuinely ambiguous, no disambiguator).

- **WHEN** `GetAwaitedTaskFromSm` scans a suspended/resuming SM's heap fields and
  (a) NO awaiter field (`<>u__1` / `<>u__2` / ...) carries a non-null `m_task`
  (the awaiter-derived `Task` could not be recovered), AND (b) the direct-`Task`
  scan finds MORE THAN ONE hoisted `Task` / `Task<T>` reference field (the
  currently-awaited `Task` cannot be disambiguated by the remaining signal)
- **THEN** the scan SHALL throw a TAGGED `NotImplementedException` ("Neo async
  multi-Task awaiter not supported (no resolvable awaiter and an ambiguous
  multi-Task scan); the currently-awaited Task cannot be disambiguated") rather
  than silently returning the wrong `Task` -- a recovery-miss MUST fail the SAME
  way across all shapes (loud NIE, never silent-skip)
- **AND** the COMMON multi-await shape (>= 2 `Task` operand fields BUT a
  resolvable `<>u__1` awaiter) SHALL resolve via the awaiter and SHALL NOT throw
  (the NIE is no longer triggered by `directTaskCount > 1` alone)
- (SCOPE: the narrowed NIE covers the residual unsolvable shape -- e.g. a custom
  awaiter type that does not expose a `Task` and an SM that hoists multiple such
  operands. The single-`Task` shape and the common multi-await shape are
  unaffected.)

## UNCHANGED requirements (in scope, no delta)

- "Neo async method builder redirection (synchronous-completion scope)"
- "Neo async Start drives MoveNext via the Neo call convention"
- "Neo async Task getter produces a completed task on the synchronous path"
- "Neo async frame-to-heap state-machine hoist primitive"
- "Neo ILAsyncContext bridge skeleton"
- "Neo async branch-size correctness for IsCompleted-driven control flow"

## Accepted-known limitations (MODIFIED -- the multi-await deferral is RESOLVED)

The "Deferred (multi-await SM)" entry is REMOVED (resolved by this change). The
remaining accepted-known limitations are UNCHANGED:

- **C (Option A 8-byte write, LOW):** unchanged.
- **D (`SmContextMap` driver-thread leak, LOW):** unchanged. (For a multi-await
  SM, the SAME `sm` key is re-parked on each suspend -- only one context per SM
  at a time -- so the per-SM leak does not compound across awaits; the bounded
  per-thread growth stands.)
- **E (custom / non-`Task` awaiters, INFO):** unchanged. (A multi-await SM whose
  awaiters are custom / non-`Task` types faults at the FIRST such suspend --
  fail-loud, not silent -- because `GetAwaiterTask` returns null; the narrowed
  NIE covers the residual ambiguous case.)
- **Deferred (nested-call-in-resume `get_Task` scan):** UNCHANGED -- when a
  resumed SM calls a nested async, `RecoverSmForGetTask` may misidentify the
  resuming SM (still parked in `SmContextMap` mid-resume). TC12 does not exercise
  this (no nested async call during a resume); the follow-up stands.

## NEW accepted-known limitations (multi-await-specific, this change)

- **Multi-awaiter-TYPE disambiguation (INFO):** when an SM awaits awaitables of
  DIFFERENT awaiter types (e.g. `await taskInt; await taskString;`), Roslyn
  generates `<>u__1`, `<>u__2`, .... The awaiter-field scan recovers the active
  `Task` ONLY if unused awaiter fields are zero-init (so their null `m_task` is
  skipped) or the scan prefers the highest-index non-null awaiter (most-recently-
  written = active). The common same-type multi-await shape (TC12) is unaffected;
  the multi-awaiter-TYPE shape is unverified by a green test in this change (the
  implementer records the disambiguation decision from a dump; a
  `NeoStep20_TC13` keeper is OPTIONAL).
