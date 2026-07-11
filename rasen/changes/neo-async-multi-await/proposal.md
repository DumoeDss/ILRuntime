# Proposal: neo-async-multi-await

## Why

Neo async/await (Step 20) shipped the truly-async suspend/resume path for a
state machine with a SINGLE genuinely-incomplete await
(`neo-async-movenext-fix`: suspend via `AwaitUnsafeOnCompleted_Neo`, resume via
`ILAsyncContext<T>.MoveNext`, sink-swap, `TaskCompletionSource<T>` bridge -- all
PROVEN GREEN by `NeoStep20_TC8` + `TC11`).

The remaining gap (recorded in the canonical spec's Accepted-known limitations
and in `neo-deferred-items.md` STEP-20-PARTIAL): **a state machine with >= 2
`await` expressions fails.** The suspend machinery's
`GetAwaitedTaskFromSm` reverse-scans the SM's `ManagedObjects` for a directly-
hoisted `Task` reference field and picks ONE; when the SM hoists MORE THAN ONE
`Task` field (which it does the moment a second await operand is captured), the
scan cannot tell which `Task` is the CURRENTLY-awaited one, so it throws a
TAGGED `NotImplementedException` ("multi-Task awaiter not supported
(single-Task shape only)"). The await faults the task instead of suspending.

This is the single-Task guard that `neo-async-movenext-fix` review Finding B
installed (converting a silent-wrong pick into a loud NIE) and explicitly routed
to THIS follow-up. Multi-await is a COMMON async shape (any method that awaits
two things sequentially); leaving it throwing is not acceptable for "full async
support."

## What Changes

Make the suspend path disambiguate the currently-awaited `Task` for a multi-await
state machine, using the compiler-generated awaiter field (Roslyn reuses a single
`<>u__1` awaiter field, overwriting it before each `AwaitUnsafeOnCompleted`), so
the continuation registers on the CORRECT `Task` and the resumed `GetResult`
reads the CORRECT awaiter -- across N sequential suspends.

**DUMP-GATE outcome (HEAD `311a6915`, this planner):** a deterministic
reproducer (`NeoStep20_TC12`: an SM awaiting TWO genuinely-incomplete
`TaskCompletionSource`-backed Tasks at two distinct await points) FAILS on HEAD
with exactly the predicted tagged NIE -- `"Neo async multi-Task awaiter not
supported (single-Task shape only); the currently-awaited Task cannot be
disambiguated. State machine hoists 2 Task fields."` -- thrown from
`GetAwaitedTaskFromSm` during the suspend. The instruction-level dump confirms
the root cause and the fix surface (see design.md "The dump-gate finding").

**Scope (this change):**
- `GetAwaitedTaskFromSm` recovers the active `Task` from the `<>u__1` awaiter
  field's `m_task` (the awaiter is ALWAYS the active one at suspend time), making
  the count-based "pick one" scan + its tagged NIE unnecessary for the common
  multi-await shape. The continuation is registered on the correct `Task` at each
  suspend.
- `TaskAwaiter_T_GetResult_Neo`'s resume-time fallback (when the reloaded
  awaiter's `m_task` is null after the F-10 boxed-struct heap round-trip) becomes
  multi-await-correct: it reads the active `Task` the same awaiter-field way
  (state-aware), not via the ambiguous scan.
- The single-Task NIE guard is RETAINED only for the genuinely-ambiguous shape
  (no resolvable awaiter field at all), converted from "throws for >1 Task field"
  to "throws only when NO awaiter-derived Task can be recovered" -- a strictly
  narrower, fail-loud condition.

**Out of scope (deferred, recorded):** `ExecutionContext` / `SynchronizationContext`
capture (`AwaitOnCompleted` mirrors `AwaitUnsafeOnCompleted` without capture);
`ValueTask<T>` / `async void` SUSPEND paths (sync only today); custom /
non-`Task` awaiters (already fail-loud at suspend); the zero-alloc custom
`Task<T>` (the TCS bridge remains). Legacy is byte-identical (every edit is
`#if ENABLE_NEO_MODE` or in a Neo-only file).

## Impact

- **Affected capability spec:** `neo-async` (the suspend/resume requirement gains
  a multi-await scenario + the `GetAwaitedTaskFromSm` MODIFIED behavior; the
  single-Task NIE scenario is narrowed).
- **Affected code:** `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs`
  (Neo-only file; `GetAwaitedTaskFromSm` + the `GetResult` fallback). No JIT
  change, no optimizer change, no shared-engine change.
- **Tests:** `TestCases/NeoStep20Test.cs` gains `NeoStep20_TC12_TwoIncompleteAwaits`
  (deterministic TCS-based, multi-await suspend/resume/resume) + a host-side
  second incomplete-`Task` cell pair in `ILRuntimeTestBase` (`TestClass3.cs`).
- **Deferred-items doc:** STEP-20-PARTIAL's multi-await row moves to resolved.

## Non-Goals (accepted-known, recorded honestly, NOT "deferred parking")

These are SEQUENCED (real future children), not parked indefinitely, and each is
narrow:

- `ExecutionContext` / `SynchronizationContext` capture for `AwaitOnCompleted`.
- `ValueTask<T>` / `async void` SUSPEND path (the sync paths work; suspend does not).
- IL-delegate-through-CLR round-trip (`Task.Run(ilLambda)`).
- A zero-alloc custom `Task<T>` from `IValueTaskSource<T>` (the TCS bridge is the
  first cut; one alloc per suspended SM).
