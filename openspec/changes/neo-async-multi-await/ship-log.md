# Ship Log: neo-async-multi-await

**Status: SHIPPED** (completion-3 wave, child 2). Lead-5 drove review -> ship.
Reviewer = lead-5 (fresh, non-author -- implementer was a prior-session worker).

## What shipped

Neo async multi-await suspend/resume. A state machine with >= 2 `await` expressions
now suspends/resumes correctly at EACH await (previously the count-based
`GetAwaitedTaskFromSm` scan was ambiguous for >1 hoisted Task field and threw a
tagged NIE). Two Neo-only changes in `CLRRedirections.AsyncNeo.cs`:

1. **D1 -- `GetAwaitedTaskFromSm` awaiter-first.** Recover the currently-awaited
   Task from the single reused `<>u__1` awaiter field (Roslyn overwrites it with
   the active awaiter before each `AwaitUnsafeOnCompleted`), highest-index non-null
   wins. Unambiguous for any number of awaits. The single-Task scan is now a
   fallback; the tagged NIE is NARROWED to the genuinely-ambiguous shape (no
   resolvable awaiter AND >1 Task field).
2. **Context-reuse (the load-bearing deviation the apply phase discovered).**
   `SuspendStateMachine` now REUSES `SmContextMap[sm]` across suspends instead of
   constructing a fresh `ILAsyncContext<T>` each suspend (the fresh-per-suspend
   orphaned the first suspend's TCS bridge that the driver/get_Task observes). Only
   the first suspend constructs; later suspends re-bind the resume `Action` onto the
   same instance.

Test: `NeoStep20_TC12_TwoIncompleteAwaits` (2 genuinely-incomplete TCS-backed awaits
-> suspend/resume/resume -> `t.Result == va+vb`). Host cell: a second independent
self-resetting `TaskCompletionSource<int>` in `TestClass3.cs`.

## Review outcome: APPROVED (0 Blocker, 0 Major)

Full detail in `review-report.md`. Summary: the fix is verified correct for its
scope -- TC12 + TC8/TC11/TC1 + full `NeoStep` smoke 239/0/0; the load-bearing fault-
propagation concern (a fault at the 2nd await must route through the REUSED bridge,
not be swallowed) is VERIFIED GOOD by an adversarial probe (case 2). Mixed
completed-then-incomplete (case 3) also green.

Findings are all PRE-EXISTING (proven by stash-toggle: the failing probe shapes
reproduce identically on HEAD with the fix removed) or cosmetic -- none introduced
by this change:
- F1: `Task`->`Task<int>` ArgumentException at `InvokeNeoCallTarget:635` for certain
  async-SM shapes (single-sync-fault, 3-await). Pre-existing CLR-binding edge; means
  3+ awaits is currently unverified end-to-end (blocked upstream). Follow-up.
- F2: `conv.ovf.u2.un` (Step 6) for string concat inside an async SM. Pre-existing
  opcode gap. Follow-up.
- F3: fault exceptions are AggregateException/TargetInvocationException-wrapped (Neo
  reads `.Result` via InvokeMember); content preserved, wrapping depth differs from
  real async. Cosmetic. Follow-up.
- F4 (coverage): recommend a future change add a permanent fault-propagation TC.

## Gates (all green, lead-5 re-verified)

| Gate | Result |
|------|--------|
| `NeoStep20_TC12_TwoIncompleteAwaits` | PASS |
| `NeoStep20` slice (TC1/4/6/7/8/9/10/11/12) | 14/0/0 |
| Full `NeoStep` regression | **239/0/0**, EXIT=0 |
| CLI `Debug_Neo` build | 0 errors |
| TestCases `Debug` build | 0 errors |
| Legacy-neutral | AsyncNeo.cs is `#if ENABLE_NEO_MODE` (compiles out under plain Debug). (Plain-Debug CLI build has a PRE-EXISTING `/unsafe` break from F-13, unrelated; this change is Legacy-neutral by construction.) |
| Adversarial fault propagation (case 2) | PASS -- fault routes through the reused bridge |
| Stash-toggle regression proof (F1) | the pre-existing `Task`->`Task<int>` exception reproduces identically on HEAD |

## Files committed

- `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs` (D1 + context-reuse)
- `TestCases/NeoStep20Test.cs` (TC12 + the private probe)
- `ILRuntimeTestBase/TestFramework/TestClass3.cs` (cell2)
- `openspec/changes/neo-async-multi-await/` (proposal/design/tasks/specs + review-report + ship-log)
- `.trae/documents/neo-deferred-items.md` (STEP-20-PARTIAL multi-await -> resolved)

Excluded: `.pdb`/`.gitignore`/`nuget.config`/`.vscode`/`CLAUDE.md` churn.

## Commit

`Neo step20 async: multi-await suspend/resume (awaiter-first GetAwaitedTaskFromSm +
ILAsyncContext reuse across suspends)`
