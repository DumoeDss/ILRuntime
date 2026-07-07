# Ship Log — neo-step26-perf-validation

> Step 26 — the Neo perf-validation + hardening capstone. Shipped 2026-07-08.
> Capability: `neo-optimizer`. Parent portfolio: `neo-completion-portfolio`.
> This is the LAST numbered AOT-chain step.

## What shipped (partial-ship — the accepted outcome for a child this broad)

Step 26 dump-gated 5 sub-surfaces; shipped the proven slice + deferred the
large remainder:

- **SHIPPED — Sub-surface A (benchmark suite, the core deliverable).** A
  host-side benchmark self-check `NeoStep26BenchCheck.Run(appdomain)`
  (`#if ENABLE_NEO_MODE && DEBUG`, driven via the `NeoStep26Bench` CLI hook)
  that invokes 5 parameterless static bench methods (`TestCases/NeoStep26BenchProbe.cs`:
  `BenchFieldAccess` / `BenchMethodCall` / `BenchValueType` /
  `BenchVirtualDispatch` / `BenchArray`) through `appdomain.Invoke`, times each
  with a HOST `Stopwatch`, asserts return == expected (the
  correctness-of-measurement gate), and emits `BENCH:<name>:<iters>:<ticks>`.
  A runner script (`scripts/run-neo-bench.{ps1,sh}`) runs both `Debug_Neo` and
  plain-`Debug`+`useRegister=true` on the SAME `TestCases.dll` and prints
  `name | neo_ms | legacy_ms | ratio` (Neo-vs-Legacy on the SAME workload).
- **SHIPPED — Sub-surface C (single-threaded-cooperative contract).** A comment
  block near `ILIntepreter.cs:56` documents that a single AppDomain + its
  interpreter pool are NOT safe for concurrent multi-threaded access (the
  `UnityMainThreadID` checks drive a cooperative coroutine pump; the pool
  isolates per-callback frame state, it does NOT enable concurrency; a host
  needing multi-threading uses one AppDomain per thread). Documentation-only;
  no behavior change.

## DEFERRED (honestly tagged, NOT promoted to "met")

- **Sub-surface D — Neo debugger variable inspection** -> new follow-up
  `neo-debugger-neo-frame`. `DebugService.cs` already degrades gracefully
  ("Neo ... inspection is not supported yet." at :207-209 / :268-271); a real
  fix reads the Neo frame (`byte* frameBase` + `AutoList mStack` +
  `CompiledFrame.LocalInfos`) across ~8 methods = substantial deep
  debugger-protocol work. (The stacktrace instruction dump IS already
  Neo-adapted at `DebugService.cs:143-148` — not deferred.) Recorded in
  `.trae/documents/neo-deferred-items.md`.
- **Sub-surface B/E — F-4 / NEO-IL-EX-FIELDACCESS** stays OPEN (NOT subsumed by
  Step 26). The four reflection-on-Neo broken read paths route to a
  cross-binding-adaptor follow-up. F-4 unchanged in `neo-deferred-items.md`.

## Files changed (all additive; the only non-new-file edits are the Program.cs
hook + the ILIntepreter.cs comment)

- `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep26BenchCheck.cs` (NEW)
- `TestCases/NeoStep26BenchProbe.cs` (NEW — 5 bench methods; names avoid the
  substring `NeoStep` so they run under the bench hook, not the regression filter)
- `ILRuntimeTestCLI/Program.cs` (the `NeoStep26Bench` special-mode hook,
  additive — mirrors the `NeoStep25LoadExec` hook)
- `scripts/run-neo-bench.ps1` + `scripts/run-neo-bench.sh` (NEW)
- `ILRuntime/Runtime/Intepreter/ILIntepreter.cs` (single-threaded-contract
  comment block near line 56; no behavior change)
- `.trae/documents/neo-deferred-items.md` (`neo-debugger-neo-frame` row added;
  F-4 confirmed OPEN)

## Verification (independent non-author re-run)

| Check | Result |
|---|---|
| `NeoStep26Bench` (Debug_Neo) | **5/5 cells PASS**; 5 `BENCH:` lines + `BENCHFREQ`; each returned == expected |
| `NeoStep` (Debug_Neo) | **215/215, 0 failed** |
| `NeoStep25LoadExec` | **21/21** (unchanged) |
| `NeoStep22SelfCheck` | **55/55** (unchanged) |
| Builds | CLI Debug_Neo 0 errors; TestCases Debug 0 errors |
| Legacy-neutral | plain-Debug 0 errors (Neo-gated code compiles out); probes run on Legacy under `NeoBenchProbe` filter 5/0; 0 probe names leak into the Neo `NeoStep` count; the 8 Legacy NeoStep failures are pre-existing |

## Adversarial probe (MANDATORY — load-bearing, PROVEN)

The bench's CORRECTNESS-OF-MEASUREMENT gate is the whole point (a bench that
reports a time for a wrong result is worthless). Reviewer mutated
`BenchFieldAccess` expected `200000L -> 200001L`, rebuilt (fresh DLL mtime
confirmed), re-ran: `[FAIL] FieldAccess: correctness gate tripped: got=200000
expected=200001`, `4/5 cells passed, 1 failed`, **non-zero exit**, and the
PASS-annotated `BENCH:` line was correctly NOT emitted. Revert -> 5/5, exit 0.
The bench does NOT silently report a timing for a wrong result. (The reviewer
also independently re-derived all 5 expected values — no off-by-one.)

## Review verdict: APPROVE-WITH-FINDINGS (0 Blocker/Major)

Ship-ready. Accepted-known (recorded; none block):
- **[Minor]** the `int x = 1/0` divide-assert is DEAD CODE (wrapped in try/catch
  and swallowed) — it has zero observable effect. The REAL load-bearing gate is
  the `ValueEquals` check + conditional `BENCH:` emission (proven by the
  adversarial probe above). The comments overstate the `1/0` as "the gate".
  Non-blocking; either delete the dead divide or fix the comments (accepted as a
  cleanup item — the gate works via `ValueEquals`).
- **[Minor]** `N=200000` is duplicated between `NeoStep26BenchProbe.N` and the
  self-check cell table (partially self-protecting — a probe-side N change trips
  the gate).
- **[Trivial]** `BENCHFREQ:` is emitted but both runner scripts hardcode 10^7
  and never parse it; comment over-claims.
- **[Trivial]** runner ratio uses the interpreted-Stopwatch self-emit despite the
  design's anti-interpreted-Stopwatch rationale (symmetric across Neo/Legacy;
  not a bug; spec framing covers it).

## Legacy impact

None. The self-check + CLI hook are `#if ENABLE_NEO_MODE`; the probe methods are
plain statics (runnable by both engines); the ILIntepreter.cs change is
comment-only. Plain-`Debug` build = 0 errors. The probe methods add 0 Legacy
failures (verified: 0 probe names in the Legacy NeoStep count).
