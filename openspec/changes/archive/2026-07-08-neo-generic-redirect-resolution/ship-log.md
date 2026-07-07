# Ship Log — neo-generic-redirect-resolution (B1)

> B1 — the async-suspend blocker investigation. Shipped 2026-07-08 as a
> **TEST-ONLY PARTIAL-SHIP**. Capability: `neo-async`. Parent portfolio:
> `neo-completion-portfolio`.

## What shipped (test-only; engine byte-identical to HEAD)

The deterministic `TaskCompletionSource`-style probe **disproved** the design's
outcome-3 prediction and surfaced **outcome 2a-DEEP**:

- **B1 redirect RESOLUTION is EXONERATED** (NOT the bug). The custom
  `AwaitUnsafeOnCompleted` / `AwaitOnCompleted` open-def NIE redirects ARE
  correctly registered on `RedirectMapNeo` (17 Await keys confirmed
  empirically); `CLRMethod.TryGetRedirection` (`CLRMethod.cs:111-131`) is
  arity-agnostic and correct (the closed-generic call's
  `GetGenericMethodDefinition()` handle matches the registered open def); and
  `get_IsCompleted` correctly returns `false` for the incomplete Task. The
  design's proposed fix sites (`TryGetRedirection` / the JIT call-operand) are
  BOTH exonerated — no engine edit is warranted.
- **The REAL blocker is a MoveNext control-flow bug.** After `get_IsCompleted`
  returns `false`, the state machine HANGS — it never reaches
  `AwaitUnsafeOnCompleted` (nor `GetResult`). This is suspend-path territory,
  owned by the `neo-step20-async-suspend` resume follow-up (a NEW, more
  precisely-located blocker than the prior "B1 + a control-flow issue").

Shipped (all test-only; engine at HEAD `38133af8`):
- `NeoStep20_TC8_IncompleteAwaitHitsTaggedNIE` `[Ignored]` — the deterministic
  hang reproducer (a `TaskCompletionSource`-backed `Task`, `SetResult` never
  called). `[Ignored]` because it HANGS on HEAD (confirmed: un-ignored → the
  process times out, `PIPE_EXIT=124`). It SHALL un-ignore green once the
  MoveNext control-flow bug is fixed.
- `NeoStep20_TC9_SyncControlSkipsAwaitUnsafeOnCompleted` — control: a completed
  Task must NOT reach `AwaitUnsafeOnCompleted` (the sync short-circuit). PASSES.
  Proves the probe is SPECIFIC to the incomplete-await path.
- `NeoStep20_TC10_IncompleteTaskIsCompletedIsFalse` — diagnostic: an incomplete
  Task → `IsCompleted == false`. PASSES. Isolates the B1 redirect/IsCompleted
  conjunct (PROVEN) from the MoveNext reach conjunct (DEFERRED).

## DEFERRED (honestly tagged, NOT promoted to "met")

- **Phase-2 suspend/resume machinery** — the `AwaitUnsafeOnCompleted_Neo` body +
  `ILAsyncContext<T>.MoveNext` + `get_Task` context branch. Owned by
  `neo-step20-async-suspend` (resume). NOT pursued here (STOP/partial-ship
  discipline — `neo-step20-async-suspend` correctly STOPPED at 2 stacked
  blockers; this child RESOLVED one of them: B1 is exonerated, leaving the
  MoveNext control-flow bug as the single precise remaining blocker).

## Files changed (test-only)

- `TestCases/NeoStep20Test.cs` — TC8 `[Ignored]` + TC9 + TC10 + helpers.
- `ILRuntimeTestBase/TestFramework/TestClass3.cs` — the
  `TaskCompletionSource`-backed incomplete-Task host helper.
- `.trae/documents/neo-deferred-items.md` — B1 marked EXONERATED; the MoveNext
  control-flow blocker pinned to the suspend follow-up.
- (NO engine file modified — `git diff -- ILRuntime/` is empty.)

## Verification (independent non-author re-run)

| Check | Result |
|---|---|
| `NeoStep20` (Debug_Neo) | **12/0 failed/1 ignored** (9 baseline + TC9 + TC10 + TC8-ignored) |
| `NeoStep` (Debug_Neo) | **218/0 failed/1 ignored** (215 baseline + TC9 + TC10 + TC8-ignored) |
| Engine at HEAD | `git diff -- ILRuntime/` EMPTY (test-only confirmed) |
| Legacy-neutral | plain-Debug CLI 0 errors; Legacy NeoStep20 12/0/1 (no new failures) |

## The deterministic probe is the binding adversarial proof

`Task.Delay(N)` is a RACY probe (it can sync-complete, which is what disproved
the sibling control-flow investigation). The `TaskCompletionSource`-backed probe
(`SetResult` never called) makes `IsCompleted` deterministically `false`, FORCING
the await through `AwaitUnsafeOnCompleted` with no race. TC9 (the sync control)
proves the probe is SPECIFIC (the sync path does NOT hang/NIE). The probe
falsified the design's outcome-3 prediction — exactly the falsification it was
built for (the prior session's binding lesson: a `Task.Delay` probe cannot
resolve B1; only a deterministic one can).

## Review verdict: APPROVE-WITH-FINDINGS (0 Blocker)

Ship-ready. Findings:
- **#1 Moderate (spec coherence) — FIXED AT ARCHIVE.** The spec delta's
  "Incomplete awaiter reaches the tagged deferral" scenario over-claimed
  (bundled resolve + throw-NIE + SHALL-NOT-hang as PROVEN, but only the resolve
  conjunct is proven; TC8 hangs). FIXED in the canonical `neo-async` spec at
  archive: split into a PROVEN scenario (the 2-generic-arg redirect resolves,
  arity-agnostic) and a DEFERRED scenario (reaching the tagged NIE without
  hanging — the MoveNext control-flow blocker).
- **#2 Minor (accepted-known):** test↔engine string coupling
  (`"neo-step20-async-suspend"` in `IsTaggedAsyncNIE` ↔ the NIE message at
  `AsyncNeo.cs:436`). Acceptable for a tagged-NIE probe.
- **#3 Minor (accepted-known):** `tasks.md` 5.3 NeoOptHardening not re-run
  (justified — no engine edit; 24/24 baseline holds).

## Legacy impact

None. The change is test-only (engine byte-identical to HEAD). The new probe
methods add 0 Legacy failures (Legacy NeoStep20 12/0/1). Plain-`Debug` build =
0 errors.

## Note on canonical spec validation

The canonical `openspec/specs/neo-async/spec.md` carries 57 pre-existing
non-ASCII bytes (em-dashes / arrows in other requirements' prose) — a
PRE-EXISTING condition (the documented "9/10 specs validator false-failures"),
NOT introduced by this change. The merge applied an ASCII-only replacement block.
Archive merge done manually per the handoff (the validator false-positives on the
pre-existing non-ASCII).
