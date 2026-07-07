# Handoff: neo-completion-portfolio — LEAD #2 (PORTFOLIO COMPLETE)

> This session drove the final 5 frontier children to completion. The portfolio
> is **COMPLETE**: all 28 children done, `runnableFrontier` empty. Authoritative
> state = `portfolio-run.json` + `.trae/documents/neo-handoff.md` +
> `.trae/documents/neo-deferred-items.md`. This document carries only what those
> cannot.

## Position

Pipeline: `auto-decompose` (parent) -> each child `small-feature`, Tier A. Branch
`features/object-model-overhaul`, HEAD `997eddfc`, in sync with origin. Policy:
SERIAL (shared interpreter/JIT files), full autonomy (skip gates; commit+push
after each clean child).

**This session drove 5 children through the full pipeline, all committed + pushed:**
1. `neo-step25-s2-generic-at-load` (`f3be2788`) — generic-method AOT-load
   (CloneAndPatch from `.neo` template; no-T-identity-token slice). NeoStep25LoadExec
   11->21 (4 adversarial cells, load-bearing body-mutation stash-toggle).
2. `neo-step26-perf-validation` (`38133af8`) — the LAST numbered AOT-chain step.
   Host-side `NeoStep26BenchCheck` (5 workloads + correctness gate + adversarial
   probe) + `run-neo-bench.{ps1,sh}` + single-threaded-contract doc. DEFER D
   (debugger -> neo-debugger-neo-frame) + F-4.
3. `neo-generic-redirect-resolution` B1 (`3ec1fa37`) — TEST-ONLY partial: the
   deterministic TCS probe EXONERATED B1 (redirect resolves; TryGetRedirection
   arity-agnostic); pinned the REAL blocker = a MoveNext control-flow bug
   (suspend-follow-up scope). TC8 hang-reproducer + TC9/TC10 guards.
4. `neo-step25-s3-full-decoupling` (`70505eba`) — PARTIAL: sub-surface 1 (ILType
   layout + VTable rebuild from `.neo` NeoTypeDefRecord, same-AppDomain,
   structural-equiv + GOLD-STANDARD adversarial mutation stash-toggle; NOT
   installed on live ILType -> Legacy-neutral). DEFER sub-surfaces 2/3/4/5.
   NeoStep25LoadExec 21->28.
5. `neo-peephole-isinst` D-PEEP (`997eddfc`) — DOC-ONLY scoped-deferral: PatchKind
   exists post-Step-22 but is the WRONG shape; no fusion pass; needs a
   peephole-pass framework + liveness. `box;isinst` stays correct un-fused.

NeoStep smoke baseline is now **218/0 failed/1 ignored** (+ NeoStep25LoadExec
28/28, NeoStep26Bench 5/5, NeoStep22SelfCheck 55/55, NeoStep23Roundtrip 15/15,
NeoStep24CliRoundtrip 5/5, NeoOptHardening 24/24). Legacy-neutral throughout
(the ILType.cs shared edit in S3 was stash-toggle-proven byte-identical).

## Done / Remaining

**Done this session:** the 5 children above (details in each
`archive/2026-07-0{7,8}-neo-*/ship-log.md` + `planning-context.md`).

**Remaining (the open deferred-items, NOT blocking portfolio completion):**
- **Neo async Phase-2 suspend/resume** — the MoveNext control-flow bug (the state
  machine hangs after `get_IsCompleted` returns false, before reaching
  `AwaitUnsafeOnCompleted`/`GetResult`). Owned by `neo-step20-async-suspend`
  (resume). B1 is EXONERATED; this is now the SINGLE precise async blocker. The
  deterministic TCS probe (`NeoStep20_TC8`, `[Ignored]`) is the hang reproducer
  + the un-ignore-green target.
- **S3 sub-surfaces 2/3/4/5** — Cecil-free AppDomain load (2); cross-AppDomain
  token-hash re-resolution APPROACH 1 (3, depends on 2); static `.cctor` seeding
  (4); full CLR aqname / host-CLR-assembly registration (5, the Step-24
  TestCLREnum gap). The S3-partial (sub-surface 1) is the completeness proof 2
  will rely on.
- **F-11 / F-12** (from S2) — eager-compile stale JIT bodyRegister (F-11); Run
  ref-return (F-12, the parametrized-Run prerequisite for full V2 coverage).
- **`neo-debugger-neo-frame`** — Neo debugger variable inspection (DebugService
  reads Neo frame vars; ~8 methods).
- **F-4 / NEO-IL-EX-FIELDACCESS** — reflection-on-Neo field reads (4 broken
  paths; cross-binding-adaptor follow-up).
- **D-PEEP peephole-pass** — the `box;isinst` fusion (needs a new additive
  optimizer pass + liveness + standalone-field fused opcode; the Step-22
  PatchKind is the wrong shape).
- Other accepted-known Minors recorded per-child (the dead-divide-assert in
  NeoStep26BenchCheck; the naturalAlignment simple-types-only re-derivation in
  S3; etc.) — see each ship-log.

The `.trae/documents/neo-deferred-items.md` rows (STEP-20-PARTIAL, STEP-25-PARTIAL,
D-PEEP, F-4, F-11, F-12, neo-debugger-neo-frame) are the authoritative per-item
tracker.

## Key decisions (and why) — do NOT re-litigate

- **The dump-gate discipline held across all 5 children.** Every child's
  propose/apply ran a HEAD probe before designing. It disproved the orientation
  hypothesis 3x this session (B1 "redirect doesn't resolve" -> EXONERATED; D-PEEP
  "PatchKind doesn't exist" -> exists-but-wrong-shape; step26 "sub-surface X is a
  real gap" -> 3/5 were no-op/doc-only). Binding: the dump is the arbiter.
- **STOP / partial-ship is the accepted outcome for LARGE / blocked work.**
  `neo-generic-redirect-resolution` (B1) shipped test-only (exoneration + guards),
  deferring the MoveNext fix; `neo-step25-s3` shipped sub-surface 1 only;
  `neo-peephole-isinst` shipped doc-only (scoped-deferral). Forcing the full fix
  past the dump-gate is the anti-pattern (re-affirmed).
- **A green smoke does NOT prove a gate correct.** Every verify/review-loop
  constructed ADVERSARIAL probes: S2's body-mutation stash-toggle; step26's
  correctness-gate mutation; S3's GOLD-STANDARD rebuild-reads-Cecil break; B1's
  deterministic TCS probe (NOT a racy Task.Delay). The review-loops were
  load-bearing (the S3 reviewer's adversarial break DECISIVELY proved the
  mutation cells read the record, not a Cecil cache).
- **The shared-ILType.cs edit (S3) is Legacy-neutral by construction + proof.**
  The Neo-only builder is entirely `#if ENABLE_NEO_MODE`; the stash-toggle
  (WITH vs WITHOUT) produced a byte-identical NeoStep failure set. This is the
  pattern for any future shared-engine Neo edit.
- **Author != verifier held across all children.** Each verify/review-loop was a
  fresh reviewer worker; the implementer never verified its own output. Two
  children's applies landed during a "failed" dispatch (S2, step26 — the worker
  completed despite the 502/socket-close return) and were INDEPENDENTLY
  re-verified rather than trusted.

## Dead ends & gotchas (this session)

- **The inference gateway (cc.toregames.com) was intermittently 502/504/socket-
  close/429 on Agent dispatches.** Recovery: back off 60-130s + retry (every
  dispatch eventually landed); OR check the working tree (a "failed" dispatch
  may have started a worker that completed — S2 + step26 both landed this way
  and were independently re-verified). Push hit transient connection resets /
  LFS-lock-verify failures (retry succeeded). LEAD-driven (Tier C) for ship +
  archive (deterministic, no dispatch needed) kept the pipeline moving through
  the gateway flakiness.
- **The openspec CLI `validate` is flaky** (pre-existing 9/10 false-failures on
  non-`neo-optimizer` capabilities; the canonical `neo-async` spec carries 57
  pre-existing non-ASCII bytes — em-dashes/arrows in prose). Archive spec-merges
  were done MANUALLY via PowerShell `System.IO.File` (UTF8-no-BOM), ASCII-only
  replacement blocks; the per-`neo-optimizer` deltas validated clean.
- **The CJK/§ corruption caveat holds.** All OpenSpec artifacts authored
  ASCII-primary; the `.trae` doc edits were targeted ASCII (the LEAD-introduced
  `§` in one planning-context is harmless — planning-context is not validated).

## Working set

**Build/test commands (CRITICAL — always `-f net8.0`; build CLI with `Debug_Neo`,
NEVER TestCases with `Debug_Neo`):**
```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep   # 218/0/1
# AOT capstone / perf / self-checks: replace the filter with NeoStep25LoadExec | NeoStep26Bench | NeoStep22SelfCheck | NeoStep23Roundtrip | NeoStep24CliRoundtrip
# Legacy-neutral: plain `Debug` + useRegister=true, relevant filter
```

**State files:** `openspec/changes/neo-completion-portfolio/portfolio-run.json`
(frontier EMPTY; 28 completedChildren), `.trae/documents/neo-handoff.md` (repo
handoff), `.trae/documents/neo-deferred-items.md` (per-item tracker).

## Next action

The portfolio is COMPLETE. If a future session resumes, the open work is the
**deferred items** above (async Phase-2 MoveNext fix is the highest-value next
item — it's now the SINGLE precise async blocker). Pick from
`.trae/documents/neo-deferred-items.md`. Each is independently shippable.
