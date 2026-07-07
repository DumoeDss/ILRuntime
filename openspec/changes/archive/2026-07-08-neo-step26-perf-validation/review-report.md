# Review Report: neo-step26-perf-validation

> Independent verifier pass (author != verifier). Verifier did NOT write the
> implementation. Re-ran every gate from scratch + the load-bearing adversarial
> probe. Branch `features/object-model-overhaul`, base `master`, no PR (local
> OpenSpec change). Reviewed diff = tracked M files (`Program.cs`,
> `ILIntepreter.cs`, `neo-deferred-items.md`) + untracked new files
> (`NeoStep26BenchCheck.cs`, `NeoStep26BenchProbe.cs`, `scripts/run-neo-bench.*`,
> openspec artifacts). `.gitignore`/`.pdb` churn excluded (pre-existing noise).

## VERDICT: APPROVE-WITH-FINDINGS

Ship- and archive-eligible. Every load-bearing gate holds and was independently
reproduced. All findings below are **Minor / Trivial** (non-blocking polish);
none block archive. The correctness-of-measurement gate is genuinely
load-bearing (proven adversarially, see section 3).

---

## 1. Independent re-run counts (vs LEAD's claim)

| Filter | Config | LEAD claim | Verifier re-run | Match |
|---|---|---|---|---|
| `NeoStep26Bench` (host self-check) | Debug_Neo | 5/5 cells, BENCH lines | **5/5 cells passed, 0 failed**; 5 BENCH lines + BENCHFREQ:10000000 | YES |
| `NeoStep` (regression smoke) | Debug_Neo | 215/215 | **Ran 215 tests, 0 failed** | YES |
| `NeoStep25LoadExec` | Debug_Neo | 21/21 | **21/21 cells passed, 0 failed** | YES |
| `NeoStep22SelfCheck` | Debug_Neo | 55/55 | **55/55 cells passed, 0 failed** | YES |

Build: `ILRuntimeTestCLI -c Debug_Neo` = 0 errors; `TestCases -c Debug` = 0
errors. Confirmed ILRuntime.dll mtime (07-08 00:16 after the mutation rebuild;
07-08 00:17 after revert) is newer than `NeoStep26BenchCheck.cs` in both
rebuilds -- no build-cache false-negative (the gotcha the prompt flagged).

Expected-value math re-derived independently by the verifier (no off-by-one):
- FieldAccess: N adds of +1 from 0 = N = 200000. OK.
- MethodCall: sum(i+1), i=0..N-1 = N(N+1)/2 = 20000100000. OK.
- ValueType: sum(i+2), i=0..N-1 = N(N+3)/2 = 20000300000. OK.
- VirtualDispatch: sum(2i), i=0..N-1 = N(N-1) = 39999800000. OK.
- Array: 4 slots, N/4 adds of +2 each -> 4 * (N/2) = 2N = 400000. OK.
Runtime confirmed each returned exactly the expected primitive.

## 2. Adversarial probe (MANDATORY -- design task 5.4) -- PROVEN

Mutated `FieldAccess` expected in `NeoStep26BenchCheck.cs` cell table:
`200000L -> 200001L`. Rebuilt (confirmed fresh ILRuntime.dll mtime). Re-ran
`NeoStep26Bench`:

- BEFORE (correct expected 200000): `5/5 cells passed, 0 failed`, exit 0.
- AFTER (mutated expected 200001):
  `[FAIL] FieldAccess: correctness gate tripped: got=200000 expected=200001`,
  `NeoStep26 bench: 4/5 cells passed, 1 failed`, **exit non-zero**.
  The PASS-annotated BENCH line for FieldAccess was NOT emitted (only the probe
  self-emit `BENCH:FieldAccess:...` appeared; the host-side PASS line did not).
  The other 4 benches still passed.
- REVERT (back to 200000L): `5/5 cells passed, 0 failed`, exit 0.

The bench did NOT silently report a PASS/timing for the now-wrong result -- the
gate caught it, recorded the cell failed, omitted the BENCH line, and failed the
CLI (non-zero exit). **The correctness-of-measurement gate is load-bearing.**
Mutation fully reverted (verified: cell is `200000L`, no stray markers).

Note on exit code: failing run reports non-zero (polarity correct: 0 on pass,
non-zero on fail). The exact value translated oddly through dotnet/bash-on-Windows
but the 0-vs-non-zero contract -- the part a CI gate checks -- is correct.

## 3. Standards axis (code-review over the diff)

Scope check: **CLEAN**. SHIP slice = A (bench suite) + C (single-threaded-contract
comment); DEFER = D (debugger) + F-4 (reflection) + E. Delivered exactly that.
No scope creep, no missing requirements.

Pass 1 (Critical): all N/A for this change. No SQL/DB; no new concurrency (the
change ADDS a single-threaded-contract comment, does not introduce threads); no
LLM trust boundary; the one new dispatch string (`nameFilter == "NeoStep26Bench"`)
is handled in the same hunk (exact-match branch), no enum spread.

Pass 2 (Informational) findings:

- **[Minor] Dead code: the divide-assert is vestigial.**
  `NeoStep26BenchCheck.cs:118-126`: `int zero = 0; int _ = 1 / zero;` is wrapped
  in `try { ... } catch (DivideByZeroException) { }` and the exception is
  swallowed. It has NO observable effect -- the cell outcome is fully determined
  by the `correct` boolean (from `ValueEquals`) computed BEFORE the divide, via
  the `if (correct && timingOk) PASS else FAIL` branch. The comments
  (file header + inline) repeatedly frame the `1/0` divide as "the gate" / "the
  gate tripping". The ACTUAL load-bearing gate is the `ValueEquals` equality
  check + the conditional BENCH-line emission, not the divide. The gate WORKS
  (section 2 proves it); this is purely a clarity/dead-code issue. The spec
  delta's "the divide-assert trips" language is matched only symbolically.
  Suggested fix: either delete the dead try/catch divide (the equality check is
  the real gate) or stop calling it the gate in the comments. Non-blocking.

- **[Minor] Magic-number duplication: N=200000 is pinned in two places.**
  `NeoBenchProbe.N = 200000` (probe) and the self-check cell table `Iterations`
  column (`NeoStep26BenchCheck.cs:80-84`) duplicate N. Comments acknowledge this
  ("the two MUST stay in sync"). Partial self-protection: if N changes in the
  probe, the return value changes, the pinned `Expected` goes stale, and the
  correctness gate trips -- so a probe-side N change is caught. But changing
  ONLY the self-check `Iterations` field (the displayed count) without changing
  the probe would make the BENCH line report a wrong iteration count while still
  passing. Low risk (the table is static), noted for maintainability.

- **[Trivial] BENCHFREQ is emitted but the runner scripts do not consume it.**
  `NeoStep26BenchCheck.cs:91` emits `BENCHFREQ:<Stopwatch.Frequency>` and the
  comment claims this lets the runner "convert ElapsedTicks -> ms EXACTLY ...
  rather than assuming the standard 10^7". But both runners hardcode the
  frequency (`run-neo-bench.ps1` param default 10000000.0; `.sh` FREQ=10000000)
  and never parse BENCHFREQ. On Windows/Linux the standard frequency IS 10^7, so
  the numbers are correct; the over-claim is in the comment. Either consume
  BENCHFREQ in the scripts or drop the comment's "rather than assuming" clause.

- **[Trivial] Timing-methodology / design-rationale tension.**
  design.md section 3.1 motivates HOST-side timing on the grounds that an
  interpreted Stopwatch is unreliable. The host-side self-check honors this
  (real Stopwatch). BUT the runner script -- the artifact that actually produces
  the Neo-vs-Legacy RATIO -- uses the PROBE SELF-EMIT timing, which under Neo is
  measured by an INTERPRETED Stopwatch (the probe runs inside ExecuteNeo). This
  is symmetric (both engines use the same interpreted-Stopwatch mechanism, so
  systematic bias cancels in the ratio) and the Stopwatch.StartNew/Stop calls
  sit OUTSIDE the timed loop, so the measurement is clean. It is not a bug. It
  is a coherence gap: the ratio the runner reports comes from the very
  interpreted-Stopwatch path the design dismissed. The spec's own framing ("the
  ratio is the signal, only meaningful on a dedicated baseline host; absolute
  timings are noise") covers this. No action required; noted for honesty.

- **[Trivial] Double BENCH lines under the `NeoStep26Bench` filter.**
  Each bench emits two BENCH lines there: the probe self-emit + the host-side
  self-check emit. The runner sidesteps this by using the `NeoBenchProbe` filter
  (generic loop -> only probe self-emit, symmetric). Confirmed: under
  `NeoBenchProbe`, exactly one BENCH line per bench under each engine. Not a
  problem for the runner; only a cosmetic clutter under the self-check filter.

Fowler smells: no real Duplicated Code (the 5 bench methods share a shape but
each does different work in the loop -- extracting would obscure the work).
`NeoBenchProbe` (class) vs `NeoStep26BenchProbe.cs` (file) naming split is
intentional and documented (avoids the `NeoStep` substring so the regression
filter cannot match). No Feature Envy / Shotgun Surgery / Speculative Generality.

## 4. Spec axis (does the diff faithfully implement proposal/tasks?)

CLEAN. Every SHIP task (2.1-2.4, 3.1-3.2, 4.1-4.2) is implemented and verified:
- 2.1 probe: 5 parameterless static methods, no `[ILRuntimeTest]`, class/method
  names avoid the substring `NeoStep`, runnable on both engines (verified Legacy
  run: 5/5).
- 2.2 self-check: gated `#if ENABLE_NEO_MODE && DEBUG`, returns result struct
  (Passed/Failed/TotalCells/Failures + BenchLines), divide-assert present,
  emits BENCH lines.
- 2.3 CLI hook: additive, mirrors Step-25 hook, under `#if ENABLE_NEO_MODE`.
- 2.4 runners: both created; ran clean (finite positive ratios, section 5).
- 2.5 optional README: not done (explicitly OPTIONAL). N/A.
- 3.1/3.2 comment: present, compiles under both Debug_Neo and plain Debug.
- 4.1/4.2 deferred-items: `neo-debugger-neo-frame` row added; F-4 still OPEN.

Spec deviations (none material):
- spec/design described a string-based `appdomain.Invoke("ILRuntimeTest.NeoBenchProbe",
  methodName, null, null)`; impl uses `probeType.GetMethod(..., 0)` + `appdomain.Invoke(m, null)`.
  Functionally equivalent / cleaner. Not a finding.
- spec scenario "self-check SHALL fail ONLY if a bench throws, returns a wrong
  value (divide-assert trips), or produces a non-positive timing": impl matches
  exactly on OUTCOME (throws -> ThrownMarker -> ValueEquals false -> FAIL; wrong
  value -> FAIL; non-positive ticks -> FAIL). Mechanism differs (equality check,
  not the literal divide) -- see Standards finding [Minor] above.

## 5. Task 5.5 -- runner script (finite positive ratio)

Ran `scripts/run-neo-bench.ps1` end-to-end. Output (dev host, NOT a baseline
host; ratios are illustrative only):

```
name                neo_ms   legacy_ms   ratio
FieldAccess          56.84       83.84    0.678
MethodCall           87.41      103.37    0.846
ValueType           124.48      161.76    0.769
VirtualDispatch    252.51      332.24    0.760
Array                67.99       68.33    0.995
```

All 5 ratios finite and positive. No NaN / Infinity / MISSING. CAVEAT line
printed. Task 5.5 satisfied.

## 6. Legacy-neutral confirmation

- Plain-Debug CLI build: **0 errors** (the `#if ENABLE_NEO_MODE` self-check +
  CLI hook compile out; the shared `ILIntepreter.cs` comment compiles).
- Probes run on the Legacy engine: `NeoBenchProbe` filter, plain Debug +
  useRegister=true -> **Ran 5 tests, 0 failed**, all 5 BENCH lines emitted
  (apples-to-apples requirement met).
- Probe methods do NOT leak into the `NeoStep` regression filter: under plain
  Debug, `NeoStep` filter -> **Ran 215 tests**, and **0 lines** mention any probe
  name (NeoBenchProbe/BenchFieldAccess/BenchMethodCall/BenchArray). The 215
  count is identical to the Neo run -- the new probe types added 0 test units.
- The 8 Legacy `NeoStep` failures are pre-existing Neo-feature tests unrelated to
  Step 26: `NeoStep15Test.NeoStep15_TC6_CastclassFailureCaught` (ArgumentOutOfRangeException),
  `NeoStep6Test.NeoNaNR8` (nan==nan), and siblings. None involve the bench.
- The only Legacy-visible changes are provably behavior-neutral: the `ILIntepreter.cs`
  edit is comment-only (inside `Break()`, between `ClearDebugState();` and the
  `#if DEBUG` block -- no statement added/removed); the new TestCases types are
  not discovered by the `NeoStep` filter; the Neo-gated code compiles out.
  Conclusion: Step 26 introduces **0 new Legacy failures**.

## 7. Deferred-items honesty

- **F-4 (NEO-IL-EX-FIELDACCESS) is still OPEN**: `.trae/documents/neo-deferred-items.md`
  line 79 status `**future** (Step 13 Area 4 / cross-binding-adaptor follow-up)`;
  the F-4 detail section (line 528+) describes the 4 broken read paths and is
  unchanged. Step 26 did NOT promote F-4 to resolved. OK.
- **D (debugger) deferred, not promoted**: new `neo-debugger-neo-frame` row added
  (status `**future**`) with suspect sites pinned
  (`DebugService.cs:203-261, 263-300, 442-731, 678-740`) and the note that
  graceful degradation already ships. OK.
- **DebugService.cs is UNMODIFIED** by this change (not in git status). The
  existing "not supported yet" guards remain verbatim (`DebugService.cs:207-209`
  GetThisInfo, `:268-271` GetLocalVariableInfo); the stacktrace instruction dump
  reads `CompiledFrame.NeoExecuteBody` (`:143-148`). Matches spec scenario
  "DebugService.cs SHALL be unmodified". OK.
- **E deep** = F-4 path 1, routed to the same cross-binding-adaptor follow-up. OK.

## 8. Blast radius

The only non-new-file edits are `ILRuntimeTestCLI/Program.cs` and
`ILRuntime/Runtime/Intepreter/ILIntepreter.cs`:
- `Program.cs`: the hook is purely ADDITIVE (a new `if (nameFilter == "NeoStep26Bench")`
  branch inserted before the generic test loop, under `#if ENABLE_NEO_MODE`).
  It mirrors the existing `NeoStep25LoadExec` hook shape (try/Run/catch/dispose/return).
  No existing branch altered.
- `ILIntepreter.cs`: COMMENT-ONLY (a 17-line `// ...` block inside `Break()`,
  between `ClearDebugState();` and the `#if DEBUG && !NO_PROFILER` check). No
  statement added, removed, or reordered. Zero behavior change. Confirmed by the
  diff hunk.

## 9. Findings summary

| # | Severity | Axis | Finding | Action |
|---|---|---|---|---|
| 1 | Minor | Standards | Divide-assert is dead code (caught+swallowed); comments overstate it as "the gate". Real gate is the ValueEquals check. | Delete dead divide OR fix comments. Non-blocking. |
| 2 | Minor | Standards | N=200000 duplicated (probe const + self-check Iterations); static table, partial self-protection. | Optional: derive Iterations from probe. Non-blocking. |
| 3 | Trivial | Standards | BENCHFREQ emitted but runners hardcode 10^7 and never parse it; comment over-claims. | Consume BENCHFREQ or soften comment. |
| 4 | Trivial | Standards | Runner ratio uses interpreted-Stopwatch self-emit despite design's anti-interpreted-Stopwatch rationale. Symmetric; not a bug. | None (spec framing covers it). |
| 5 | Trivial | Standards | Double BENCH lines under `NeoStep26Bench` filter (cosmetic; runner uses `NeoBenchProbe`). | None. |

No Blockers. No Majors. The correctness gate, the regression smoke, the
Legacy-neutrality, the deferred-items honesty, and the spec coherence all hold.

## 10. Recommendation

**APPROVE-WITH-FINDINGS.** Proceed to ship + archive (tasks 6.1-6.3). The
findings are non-blocking polish; finding 1 (dead divide-assert) is the only one
worth a future cleanup pass -- the comments slightly misrepresent the mechanism,
but the gate itself is correct and was proven load-bearing adversarially.
EOF
