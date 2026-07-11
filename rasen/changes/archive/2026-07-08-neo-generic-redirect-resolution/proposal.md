## Why

The truly-async suspend/resume path is the largest remaining gap in the Neo
async/await model. It is blocked behind **B1**: a prior session reported that
the 2-generic-arg builder redirect `AwaitUnsafeOnCompleted<TA,TSM>` resolves on
the Legacy `RedirectMap` but NOT on the Neo `RedirectMapNeo`, while the
1-generic-arg `Start<TSM>` resolves on both. That report was made with a RACY
`Task.Delay(N)` probe (it can sync-complete before the JIT is set up, which is
exactly the artifact that disproved the sibling control-flow investigation), so
the B1 claim is currently UNFALSIFIABLE. This change resolves B1 the only way
that binds: by designing a DETERMINISTIC `TaskCompletionSource`-style probe that
the test controls explicitly, then acting on what the probe shows.

## What Changes

- **A deterministic truly-incomplete-awaiter probe** (a `TaskCompletionSource`-
  backed `Task` whose `SetResult` is NOT called before the assertion) is added
  to `TestCases/NeoStep20Test.cs`. Its `IsCompleted` is deterministically
  `false`, so the await is FORCED through `AwaitUnsafeOnCompleted`. This is the
  adversarial falsification harness for B1 (and the successor to the removed
  `NeoStep20_TC8_IncompleteAwaitHitsTaggedNIE`).
- **The B1 redirect-resolution verdict is established and pinned** by the probe:
  the closed-generic `AwaitUnsafeOnCompleted<TA,TSM>` (2 generic args) resolves
  on `RedirectMapNeo` via the SAME arity-agnostic `TryGetRedirection` path as
  `Start<TSM>` (1 arg). The lookup branches on `IsGenericMethod &&
  !IsGenericMethodDefinition` only; there is NO arity-specific handling.
- **Scope is decided by the dump-gate (partial-ship EXPECTED).** Three outcomes
  are laid out in `design.md`; the static evidence favors outcome 3 (B1 not
  reproducible as a resolution bug; the redirect resolves to the tagged NIE).
  The probe confirms which outcome holds before any engine edit is allowed.
- **IF outcome 3 holds (expected):** ship the deterministic probe as a
  regression guard + the verdict documentation. NO engine change. The only
  remaining async gap is the suspend MACHINERY (Phase 2), which is a separate
  LARGE child.
- **IF outcome 2 holds (probe reveals a real resolution bug):** ship the minimal
  arity/key fix in the redirect-lookup (or the JIT call-operand construction) +
  the probe as guard. The suspend machinery still DEFERS.
- **DEFERRED in all outcomes:** the Phase-2 suspend/resume machinery
  (`AwaitUnsafeOnCompleted_Neo` body + `ILAsyncContext<T>.MoveNext` resumption +
  `get_Task` context branch). That is the `neo-step20-async-suspend` resume
  scope and is out of scope for B1 (redirect RESOLUTION) alone.

## Capabilities

### New Capabilities

(None.)

### Modified Capabilities

- `neo-async`: the existing requirement "Neo async AwaitUnsafeOnCompleted is a
  tagged deferral (synchronous scope boundary)" is MODIFIED. Its "Incomplete
  awaiter reaches the tagged deferral" scenario is strengthened to REQUIRE a
  deterministic `TaskCompletionSource`-style probe (NOT a racy `Task.Delay`),
  and to REQUIRE that the 2-generic-arg `AwaitUnsafeOnCompleted<TA,TSM>`
  redirect resolves on the Neo redirect map (the B1 verdict: arity-agnostic
  resolution, same path as `Start<TSM>`). The scenario is currently
  unproven (its prior test, TC8, was removed); this change makes it proven and
  falsifiable.

## Impact

- **TestCases** (`TestCases/NeoStep20Test.cs`): one new deterministic probe
  (the TC8 successor) + a sync-completing control. Outcome-3-shipped or
  outcome-2-guard.
- **ILRuntimeTestBase** (only if the probe needs a host-side incomplete-Task
  cell): a small `TestCLRBinding` accessor returning a never-completed
  `TaskCompletionSource`-backed `Task`. Apply-time decision (see design D3).
- **ILRuntime** (`ILRuntime/CLR/Method/CLRMethod.cs` `TryGetRedirection`, OR
  the JIT call-operand construction in
  `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`): touched ONLY in
  outcome 2 (a real resolution bug). MODERATE blast-radius (shared generic-
  redirect dispatch); gated by the full `NeoStep` smoke.
- **No change** to `CLRRedirections.AsyncNeo.cs` (the tagged NIE stays; Phase 2
  owns its body) or `ILAsyncContext.cs`.
- **Regression gates:** `NeoStep20` 9/9, full `NeoStep` smoke (215/215 on
  HEAD `38133af8`), `NeoOptHardening` 24/24, Legacy-neutral (plain `Debug`
  build = 0 errors). Legacy is the reference; all edits Neo-only.
