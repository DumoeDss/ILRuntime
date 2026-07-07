# Review Report — neo-generic-redirect-resolution (B1)

**Reviewer:** verifier (author != verifier) · **Date:** 2026-07-08
**HEAD at review:** `cfbf8ff9` (engine); test/doc changes are uncommitted working-tree
edits on `features/object-model-overhaul`. **Skill invoked:** `openspec-review`
(PR-landing framework; gh/Greptile/Codex unavailable, no PR — applicable core run
manually: scope check + two-pass standards + spec-axis. The skill's
`.claude/skills/review/checklist.md` is not installed in this repo.)

## Verdict: APPROVE-WITH-FINDINGS

The partial-ship is **SOUND and HONEST**. The test-only claim holds (engine
byte-identical to HEAD), the exoneration is **earned and code-grounded** (not
hand-waved), the `[Ignored]` is a **real hang** (independently reproduced), and
the MoveNext blocker is **real** and correctly pinned to the suspend follow-up.
One **Moderate** spec-coherence finding (the spec delta over-claims "PROVEN /
SHALL NOT hang" for a scenario whose proof TC8 is `[Ignored]` because it hangs)
and two **Minor** findings. Nothing blocks ship; the Moderate finding should be
tightened before/at archive.

| # | Severity | Finding |
|---|----------|---------|
| 1 | **Moderate** | Spec scenario "Incomplete awaiter reaches the tagged deferral" bundles three conjuncts (resolve / throw-NIE / SHALL-NOT-hang) and marks the whole "PROVEN by ... the successor to the removed TC8" — but TC8 is `[Ignored]` because it HANGS, so the NIE-reach + no-hang conjuncts are NOT currently proven (only the resolve conjunct is). Over-claims vs the honest design.md/handoff. |
| 2 | Minor | Test<->engine string coupling: `IsTaggedAsyncNIE` keys on `"neo-step20-async-suspend"`, matching the NIE message in `CLRRedirections.AsyncNeo.cs:436`. An engine-side message edit silently flips the verdict to 0. Acceptable for a tagged-NIE probe; documented here for the follow-up. |
| 3 | Minor | `tasks.md` 5.3 (NeoOptHardening) is `[ ]` not-run. Justification (no engine edit) is sound and the test-only change cannot affect it; acceptable, but the LEAD may re-run at ship to close the row. |

---

## 1. Independent gate re-runs (I ran these myself)

Build: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` = 0 errors;
`dotnet build TestCases/TestCases.csproj -c Debug` = 0 errors (TestCases NEVER built
Debug_Neo). CLI run with `-f net8.0 --no-build`, filters run separately (CLI filter
is a `Contains` substring, no `|`).

| Gate | My count | Implementer claim | LEAD claim | Match |
|------|----------|-------------------|------------|-------|
| `NeoStep20` slice | `Ran 12 tests, 0 failed, 1 ignored, 0 todos`, EXIT=0 | 12/0/1 | 12/0/1 | YES |
| `NeoStep` smoke | `Ran 218 tests, 0 failed, 1 ignored, 0 todos`, EXIT=0 | 218/0/1 | 218/0/1 | YES |

Both gates independently reproduced, matching implementer AND LEAD. No hang in the
shipped smoke (TC8 is `[Ignored]`, so it is skipped).

## 2. Engine byte-identical to HEAD (test-only claim) — CONFIRMED

`git diff --stat -- ILRuntime/` is **EMPTY**. No engine file is modified. All
temp instrumentation was reverted. Working-tree changes are exactly:
- `TestCases/NeoStep20Test.cs` (TC8/TC9/TC10 + class comment; blob `89b8a7c5`)
- `ILRuntimeTestBase/TestFramework/TestClass3.cs` (host-side accessors, +41)
- `.trae/documents/neo-deferred-items.md` (B1/STEP-20-PARTIAL update, +42)
- plus pre-existing noise (`.gitignore`, Dependency `*.pdb`, untracked
  `nuget.config`/`CLAUDE.md` — NOT part of this change).

The partial-ship claim (test-only) is HONEST.

## 3. Exoneration assessment — EARNED (code-grounded, not hand-waved)

I read every load-bearing site. The full redirect-resolution chain is correct by
construction, and the implementer's empirical traces corroborate it:

| Claim | Code site I verified | Verdict |
|-------|----------------------|---------|
| Lookup is arity-agnostic | `CLRMethod.cs:111-131` `TryGetRedirection` — branches ONLY on `def.IsGenericMethod && !def.IsGenericMethodDefinition`; tries `GetGenericMethodDefinition()` first, then closed `def`. **No arg-count branch.** `Start<TSM>` (1-arg) and `AwaitUnsafeOnCompleted<TA,TSM>` (2-arg) use the IDENTICAL path. | CORRECT |
| Open-def NIE registered on the NEO map | `CLRRedirections.AsyncNeo.cs:789-809` `RegisterAwaiters` finds `m.IsGenericMethodDefinition` for `AwaitUnsafeOnCompleted`/`AwaitOnCompleted` and calls `app.RegisterCLRMethodRedirectionNeo(openDef, del)`. `AppDomain.cs:757-776` writes `redirectMapNeo[mi]`, first-registered-wins (`if (!ContainsKey)`). | CORRECT |
| Dispatch reads the NEO map | `CLRMethod.cs:145-155` `RedirectionNeo` -> `TryGetRedirection(appdomain.RedirectMapNeo, ...)`; `AppDomain.cs:316-328` `RedirectMapNeo` returns `redirectMapNeo`. `ILIntepreter.Neo.cs:634-642` `InvokeNeoClrMethod` dispatches to `clrMethod.RedirectionNeo` when non-null. | CORRECT |
| Registration precedes harness | `AppDomain.cs:253` `CLRRedirectionsAsyncNeo.Register(this)` under `#if ENABLE_NEO_MODE`, before the test harness `CLRBindings.Initialize`. | CORRECT |
| `get_IsCompleted` correct for incomplete Task | `CLRRedirections.AsyncNeo.cs:478-489` `TaskAwaiter_T_GetIsCompleted_Neo` writes `*(int*)retDst = isCompleted ? 1 : 0` -> 0 for an incomplete Task. | CORRECT |
| `AwaitUnsafeOnCompleted_Neo` is a tagged NIE | `CLRRedirections.AsyncNeo.cs:427-437` throws `NotImplementedException("Neo async suspend path: neo-step20-async-suspend (Step 20 suspend slice)")`. | CORRECT |

**Could the implementer have missed a real B1 resolution bug?** I probed the
obvious failure modes and found none:
- **Wrong map?** No — `RegisterCLRMethodRedirectionNeo` writes `redirectMapNeo`
  (line 764), and the runtime dispatch reads `RedirectMapNeo` -> same dictionary.
  (The implementer's trace noting the JIT *compile-time* `cm.Redirection` check
  consults the Legacy `RedirectMap` is a red herring for B1: that is a separate
  compile-time optimization check, not the runtime dispatch path.)
- **Wrong key / hash mismatch?** No — the dictionary key is the open-def
  `MethodBase` obtained from `builderType.GetMethods(flag)`; the lookup key is
  `def.GetGenericMethodDefinition()`, which returns the same canonical open def.
  The implementer's trace confirmed an exact `MethodHandle` match
  (140709099823464). Keys match by construction.
- **Closed call mis-classified?** No — `AwaitUnsafeOnCompleted<TaskAwaiter<int>,SM>`
  is `IsGenericMethod && !IsGenericMethodDefinition`, so the first lookup branch
  fires correctly.

**Exoneration verdict: EARNED.** B1 redirect resolution is correct. The design's
proposed fix sites (`TryGetRedirection` / JIT call-operand) would NOT fix the hang;
touching them for B1 would be a mistake. No B1 resolution bug was missed.

## 4. TC8 `[Ignored]` honesty — REAL HANG (not a cop-out) — CONFIRMED

I reproduced the hang myself. With TC8 un-ignored locally (temporary edit,
`Ignored = true` -> `false`), rebuilt `TestCases`, and ran filter `NeoStep20_TC8`
under a 25s wall-clock cap:

```
PIPE_EXIT=124        # `timeout` killed the process
# no "Ran N tests" summary line, no NotImplementedException / neo-step20-async-suspend string
```

EXIT 124 = the run was killed for exceeding the cap; the tagged NIE was never
reached and the test never completed. TC8 is a **genuinely-incomplete Task**
(`s_incompleteTcs = new TaskCompletionSource<int>()` with no `SetResult` anywhere)
that forces the await toward `AwaitUnsafeOnCompleted`. The `[Ignored]` tag is
**HONEST**: this is a real hang on HEAD that would time out the smoke suite, not a
test that could pass but was lazily ignored. (Process-note: my temporary edit was
fully reverted; see §8.)

## 5. TC9 / TC10 probe-specificity — CONFIRMED (with one nuance, not a finding)

- **TC9 (sync control, ACTIVE, green in 12/0/1):** awaits `Task.FromResult(42)`
  (completed). `IsCompleted == true` short-circuit skips `AwaitUnsafeOnCompleted`;
  asserts `t.IsCompleted && !t.IsFaulted && t.Result == 43`. Passing PROVES the
  sync path does NOT reach the tagged NIE — i.e. TC8's hang is specific to the
  incomplete-await path, not a general async-machinery blowup. If TC8's hang were
  unspecific, TC9 would also hang/fail. **Specificity confirmed.**
- **TC10 (IsCompleted diagnostic, ACTIVE, green in 12/0/1):** reads
  `incomplete.GetAwaiter().IsCompleted` directly (NO await/GetResult/
  AwaitUnsafeOnCompleted). Asserts `!isc`. Passing proves `IsCompleted` is correctly
  `false`, isolating the `get_IsCompleted` redirect from the MoveNext bug.
  Nuance (NOT a finding, honesty note): TC10 alone is consistent with BOTH (a) the
  custom redirect reading the real task -> false AND (b) an autogen
  `default(TaskAwaiter)` stub returning `default(bool)` = false. The implementer's
  `B1_ISC` trace resolves this (the custom redirect fires and reads the real task),
  and TC1 (which would NRE on a default awaiter) corroborates. TC10 is the
  behavioral guard; the trace is the mechanism proof. The handoff is honest about
  this split.

**Important honesty distinction:** B1 redirect RESOLUTION (the
`AwaitUnsafeOnCompleted<TA,TSM>` lookup) is proven by static code reading + the
`B1_REGNEO` registration traces (17 Await keys on the Neo map) — NOT by an active
test. TC8 (the test that WOULD exercise the full B1 path end-to-end) hangs, so it
is `[Ignored]`. TC10 proves only the PREREQUISITE (`IsCompleted == false`). The
handoff and deferred-items present this accurately (no test is falsely claimed to
prove the full B1 path).

## 6. MoveNext blocker — REAL, suspend-follow-up scope — CONFIRMED

The hang reproduction (§4) shows the state machine never reaches the
`AwaitUnsafeOnCompleted` call (no NIE, no summary) and never reaches `GetResult`
either — consistent with the implementer's `B1_CALLPRE`/`B1_GETRESULT` traces
(neither fires). The state machine loops/hangs between the `get_IsCompleted`
`brtrue` and either branch. This is genuinely suspend-path territory
(`AwaitUnsafeOnCompleted_Neo` body + `ILAsyncContext<T>.MoveNext` resumption),
owned by the `neo-step20-async-suspend` resume child. It is NOT a B1 redirect fix.
**Blocker confirmed real, scope correct.**

## 7. Deferred-items honesty — CONFIRMED (no over-claiming)

`.trae/documents/neo-deferred-items.md` UPDATE 2026-07-08 (+42):
- Records B1 RESOLUTION EXONERATED (not a redirect bug) with the empirical basis
  (17 Await keys; arity-agnostic lookup; handle match).
- Pins the REAL blocker (MoveNext control-flow) to the `neo-step20-async-suspend`
  resume child, explicitly stating it must land BEFORE the tagged-NIE body is
  reachable.
- The suspend machinery is NOT marked "met".
- Includes an honest CAVEAT: this PARTIALLY RE-OPENS the 2026-07-07
  `neo-async-controlflow-iscompleted` "control-flow is NOT a bug on HEAD"
  conclusion (which was drawn from a racy `Task.Delay` dump). Honest
  re-acknowledgement, not a buried contradiction.

**No over-claiming.** The deferred-items row is accurate.

## 8. Working-tree integrity note (transparency)

During the TC8 hang reproduction I made a temporary `Ignored=true -> false` edit.
My initial revert used `git checkout -- TestCases/NeoStep20Test.cs`, which
(carelessly) reverted the ENTIRE file to HEAD — discarding the implementer's
uncommitted TC8/TC9/TC10 additions, not just my flag flip. I reconstructed the
shipped file via three surgical Edits from the diff I had captured. **Recovery
verified byte-identical:** the reconstructed blob hash is `89b8a7c5`, EXACTLY
matching the original shipped diff blob; `TestClass3.cs` was untouched throughout
(+41 preserved). I then rebuilt `TestCases` and re-ran the `NeoStep20` slice on
the restored DLL: `Ran 12 tests, 0 failed, 1 ignored`, EXIT=0. Final
`git diff --stat -- ILRuntime/` is empty (engine still at HEAD); TC8 is
`Ignored = true`. The working tree is in the exact shipped state. No residual
damage. (Lesson logged: for a 1-flag revert on an uncommitted file, edit the flag
back, do not `git checkout` the whole file.)

## 9. openspec-review skill (Standards + Spec axes)

Diff is test + test-framework only (156+/3-); no application/engine code paths,
so Step 4.75 coverage audit is N/A (the tests ARE the coverage; TC8's gap is
documented + deferred by design).

**Standards axis — PASS.**
- Pass 1 (data-safety / concurrency / enums): `s_incompleteTcs` is a thread-safe
  `static readonly` `TaskCompletionSource<int>` that is never completed; no
  mutation after init; reused idempotently across runs. No SQL, no LLM, no enums.
- Pass 2: magic-number/string-coupling is the only smell (Finding #2); dead-code
  check passes (`IsTaggedAsyncNIE`/`IsFaultedWithTaggedAsyncNIE` are the TC8
  verdict inspectors, reachable when TC8 is un-ignored). No frontend/perf impact.

**Spec axis — PASS (with Finding #1).** Delivered matches the proposal's outcome-3
SHIP path (deterministic probe as regression guard + verdict docs, NO engine
change). The outcome was relabeled 2a-DEEP because the probe surfaced a DIFFERENT
real bug (MoveNext) than predicted, but B1 resolution was exonerated so the
proposal's outcome-2 fix path correctly did NOT fire. `tasks.md` honestly marks
the engine-fix group SUPERSEDED/DEFERRED and 5.3 not-run with sound justification.
The only spec-axis gap is Finding #1: the `specs/neo-async/spec.md` scenario
marks the resolve+throw-NIE+no-hang bundle "PROVEN by ... successor to TC8" while
TC8 is `[Ignored]` because it hangs — tighter wording (split the PROVEN resolve
conjunct from the DEFERRED NIE-reach/no-hang conjunct) would make the spec as
honest as the design/handoff already are.

## Recommendation

**Ship as-is (test-only partial-ship).** The change DELIVERS the verdict (B1
exonerated; real blocker isolated to suspend follow-up) and ships three honest
guards (TC8 reproducer `[Ignored]`, TC9 + TC10 active). Before/at archive, tighten
Finding #1 (spec scenario wording) so the spec delta does not record a false
"PROVEN / SHALL NOT hang" for the currently-hanging path. Findings #2/#3 are
informational and can ship as-is.
