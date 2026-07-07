# Handoff: neo-completion-portfolio — LEAD #1

> This is a PORTFOLIO-level handoff (the `auto-decompose` parent), not a single-
> change handoff. All 8 children driven this session are ARCHIVED under
> `openspec/changes/archive/2026-07-0*-neo-*`; the portfolio itself
> (`openspec/changes/neo-completion-portfolio/`) is the ongoing container. The
> authoritative state is `portfolio-run.json` + `.trae/documents/neo-handoff.md`
> + `.trae/documents/neo-deferred-items.md`. This document carries only what those
> cannot.

## Original intent

The user invoked `/openspec-opsx-auto auto-decompose` with: "首先阅读交接文档
(.trae/documents/neo-handoff.md)，继续推进完成后续所有任务！不要停下" (read the
handoff doc first, then continue and complete ALL subsequent tasks; don't stop).
Full autonomy was pre-authorized (skip gates; commit + push after each clean child).
Scope: the entire Neo completion portfolio — the AOT toolchain (Steps 22-26), the
async finish, and the smaller completions.

## Position

Pipeline: `auto-decompose` (parent) → each child runs `small-feature` (propose →
apply → verify → review-loop → ship → archive → LEAD commit+push), Tier A. Branch:
`features/object-model-overhaul`, HEAD `9c0d21fc`, in sync with origin.

**This session drove 8 children through the full pipeline, all committed + pushed:**
1. `neo-array-multidim` (smoke 190→198) — multi-dim array reflection-fallback fixes.
2. `neo-step17-generic-byref-etc` (198→204) — Step 17 (c) edges + F-10-R1 JIT gate.
3. `neo-step20-async-suspend` (PARTIAL) — Phase 1 reachability unblockers only.
4. `neo-async-controlflow-iscompleted` (closed as NO-OP) — premise disproven.
5. `neo-step22-generic-template` (204→205) — generic-method template mechanism.
6. `neo-step23-neoassembly` — `.neo` binary format + serializer/deserializer.
7. `neo-step24-ilrt-neoc` — `ilrt_neoc` standalone precompile CLI.
8. `neo-step25-runtime-loader` (PARTIAL S1) — `.neo` loader; **V2 deserialize+ExecuteNeo==JIT**.

NeoStep smoke baseline is now **210/210** (+ NeoOptHard 24/24, NeoStep20 9/9, and the
AOT self-check filters: NeoStep22SelfCheck 55/55, NeoStep23Roundtrip 15/15,
NeoStep24CliRoundtrip 5/5, NeoStep25LoadExec 11/11). Legacy-neutral throughout.

## Done / Remaining

**Done this session:** the 8 children above (details in each `archive/.../ship-log.md`
+ `planning-context.md`).

**Remaining (the runnableFrontier in `portfolio-run.json`):**
- `neo-step26-perf-validation` — benchmarks (field/call/VT/virtual/array) + reflection
  /thread-safety edges + debugger (`DebugService` reads Neo frame vars) +
  `CrossBindingAdapter` adaptation + Neo-vs-Legacy perf. Deps satisfied (Step 20-async,
  Step 25). LARGE (several sub-surfaces).
- `neo-step25-s2-generic-at-load` — generic-instantiation-at-load (CloneAndPatch from
  the `.neo` template into an AOT ILMethod). Builds on Step 25 S1.
- `neo-step25-s3-full-decoupling` — full `ILType` Cecil-decoupling (ILType AOT-init from
  `NeoTypeDefRecord`) + Cecil-free AppDomain load + cross-AppDomain token-hash
  re-resolution (Approach 1: record compile-time `GetHashCode()` per ref) + static
  `.cctor` seeding + full CLR aqname/host-CLR-assembly registration (the Step-24
  `TestCLREnum` gap). LARGE.
- `neo-generic-redirect-resolution` (B1) — the async-suspend blocker: the 2-generic-arg
  redirect (`AwaitUnsafeOnCompleted<TA,TSM>`) resolves only on the Legacy RedirectMap,
  not `RedirectMapNeo`. Needs a DETERMINISTIC probe (a `TaskCompletionSource`-style
  awaitable; `Task.Delay(N)` is racy with JIT setup). Unblocks the async suspend
  machinery (Step 20 Phase 2).
- `neo-peephole-isinst` — `box T; isinst U` fusion; BLOCKED on patch-infra (neither the
  fusion pass nor `PatchKind` exists). Lowest priority.

The `STEP-25-PARTIAL`, `STEP-20-PARTIAL`, and `D-ARR`/`D-CONSTRAINED` rows in
`neo-deferred-items.md` are the authoritative per-item tracker.

## Key decisions (and why) — do NOT re-litigate

- **Probe before designing (the dump-gate).** Every JIT-path child's propose phase
  ran a HEAD probe before designing. It disproved the orientation hypothesis 5× this
  session (array 2/3 no-ops; byref 3/4 no-ops; controlflow fully disproven; Step 22's
  open-question premise; Step 25's "decouple ExecuteNeo" → "init-only"). Binding: the
  dump is the arbiter, not the hypothesis.
- **A green smoke does NOT prove a gate correct** (Step 17 B1 / OPT-HARDEN K1 lesson).
  Every verify/review-loop constructed ADVERSARIAL probes for the specific property,
  not just re-ran the smoke. The review-loop caught real Blockers the smoke hid
  (Step 22 Constrained-token; Step 25 the body-mutation proof).
- **STOP at the dump-gate, don't force a fix** (F-10/K1). `neo-step20-async-suspend`
  correctly STOPPED at Phase 2 (2 stacked blockers) instead of shipping a silent-wrong
  suspend; `neo-async-controlflow-iscompleted` was closed as no-op (premise disproven).
- **Partial-ship is an accepted outcome** (precedent: `neo-step20-async` sync slice,
  `neo-step20-async-suspend` Phase 1, `neo-step25-runtime-loader` S1). Ship the
  proven slice; defer the rest to a documented follow-up child; keep the canonical spec
  honest (suspend/S2/S3 stay NIE/deferred, NOT promoted to "met").
- **The AOT chain design hinge:** Cecil-coupling is ZERO at execution (`ExecuteNeo`
  reads only `CompiledFrame.NeoExecuteBody` + token hashes via AppDomain maps). So
  Step 25 is init-only (populate CompiledFrame from `.neo`, bypass JIT), NOT a change
  to `ExecuteNeo`. This made S1 tractable.
- **`OpCodeR` serialization = raw 24-byte little-endian** via `MemoryMarshal.AsBytes`
  (captures all `[StructLayout(Explicit)]` union aliases; avoids the F-8/OPT-HARDEN-K1
  per-opcode canonical-field defects). Versioned header (Version=1).
- **The `public NeoCompiler` seam** (Step 24): all NeoAOT types + `ILMethod.BodyRegister`
  + `ForceBuildTemplate` are `internal` with NO `InternalsVisibleTo`, so the in-assembly
  `public` class is the only way the standalone tool project reaches them (cleaner than
  IVT or broad visibility-widening).
- **F-10-R1 fix = the JIT-discriminator gate** (`TypeSpecializeNeoOpcodes case Ldflda:`
  clears the F-10 marker when F-6 stamps; keys on the operand value-category, NOT
  `!IsValueType`). The runtime F-6-before-F-10 reorder was DISPROVEN (broke 6 probes).
- **Cross-AppDomain token re-resolution = Approach 1** (record compile-time
  `GetHashCode()` per ref entry; loader re-registers resolved refs under the recorded
  hash). Approaches 2/3 (name-based `GetHashCode`) REJECTED — identity-uniqueness is
  relied on by SHARED `mapTypeToken`/`mapMethod`/`fieldTokenMapping`/`jumptables`
  (Legacy regression risk). S3 owns the implementation.

## Dead ends & gotchas

- **The async path is a deep rabbit hole.** `neo-step20-async-suspend` found the suspend
  redirect UNREACHABLE (2 stacked blockers: a control-flow issue + B1). The control-flow
  issue turned out to be a RACY `Task.Delay(N)` probe (sometimes sync-completing), NOT
  an engine bug — `neo-async-controlflow-iscompleted` closed it as no-op. B1 (the
  2-generic-arg redirect) + the deterministic-probe requirement remain. The async
  machinery (Phase 2) is NOT wired; `AwaitUnsafeOnCompleted_Neo` / `ILAsyncContext.MoveNext`
  are tagged NIEs.
- **The inference gateway (cc.toregames.com) was intermittent** — recurring 502/504/
  socket-close on Agent dispatches mid-session. Recovery: check residual working-tree
  state (the worker may have started before the timeout), retry the dispatch, or fall
  back to LEAD-driven (Tier C) for small/test-only work. Push also hit one connection
  reset (retry succeeded).
- **The `openspec` CLI is broken in this env** (`runCli` export error; `openspec validate
  --all` shows 9 of 10 specs failing — a PRE-EXISTING "Missing `## Purpose`/`##
  Requirements` sections" condition in the other capabilities, NOT introduced by this
  work; `neo-optimizer` is the one that validates). Archive merges + moves were done
  MANUALLY (the handoff authorizes a manual merge + plain `mv` when the validator false-
  positives). The `openspec-archive-change` skill's validator is a heuristic, not gating.
- **openspec validator traps (for spec authoring):** (1) rejects ANY non-ASCII byte in
  spec.md (a U+00A7 silently broke parsing); (2) scans only the FIRST hard-wrapped line
  of a requirement body for SHALL/MUST. Author spec.md PURE ASCII + start every
  requirement body with "... SHALL ..." on the first line.
- **The Write tool corrupts ~0.5% of CJK on large payloads.** All OpenSpec artifacts
  authored ASCII-primary. The `.trae/documents/*.md` (CJK-heavy) edits were small/targeted.
- **The sln CANNOT build whole** (VS2022 debugger VSIX NU1201). Build only the dev subset;
  the new `ILRuntimeNeoCompiler/` tool project builds STANDALONE (`dotnet build
  ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj -c Debug_Neo`), NOT in the sln.
- **The Step-6 Neo host entry `ILIntepreter.Run` is PARAMETERLESS-ONLY** (doesn't marshal
  `object[] p` into the frame). The Step 25 V2 probe is parameterless; a parametrized V2
  (needed for S2/S3) requires a fuller Run entry or an internal-call-driven invocation.
- **`AppDomain.Dispose` is REQUIRED** for the CLI's fresh AppDomain — `AsyncJITCompileWorker`
  starts a FOREGROUND thread; without Dispose the process hangs on exit (Step 24 gotcha,
  fixed).
- **Throwaway fixtures**: the Step 24 `Step24V1BSample/` was DELETED (not referenced by
  committed automation). Don't re-create throwaway project fixtures unless a committed
  test uses them.

## Eliminated hypotheses

- **"brtrue always takes the completion path" (the async control-flow bug)** — DISPROVEN:
  on HEAD, `get_IsCompleted` returns False, `Brtrue` reads the same offset the redirect
  wrote, and correctly falls through to `AwaitUnsafeOnCompleted` (reached, hasRedirect=True).
  The report was an artifact of a sync-completing task before the Task.Delay redirect
  shipped. (`neo-async-controlflow-iscompleted` closed no-op.)
- **"The F-10-R1 fix is a runtime F-6-before-F-10 reorder"** — DISPROVEN: broke 6
  NeoStep17 F-6-only probes (190→184). The fix is the JIT-discriminator gate (producer-
  side). Reaffirmed in `neo-step17-generic-byref-etc`.
- **"Step 22 CloneAndPatch re-runs TypeSpecialize, so PatchEntry-apply is redundant"** —
  DISPROVEN at apply: TypeSpecialize does NOT insert Initobj (front-half step); the back-
  half does NOT re-derive T-identity tokens (set in Translate). PatchEntry-apply + re-run-
  TypeSpecialize are COMPLEMENTARY. Plus: compiling the open generic definition corrupts
  shared caches → capture from the first concrete instantiation.
- **"The full-TestCases.dll CLI run stalls on a Step-19 JIT loop"** — DISPROVEN: it fails
  with a CLR-type-resolution fatal (`TestCLREnum`, a host CLR assembly the standalone CLI
  doesn't register) = D7 "V1 = BCL refs only" Step-25 boundary. Corrected in handoff.
- **"The array/byref/controlflow children have real engine gaps"** — DISPROVEN: array 2/3
  were no-ops, byref 3/4 were no-ops, controlflow fully disproven. The dump found the
  REAL (smaller) gaps each time.
- **Current best hypothesis for the async suspend blocker**: B1 (2-generic-arg redirect
  resolution on `RedirectMapNeo`) is the real remaining blocker, gated behind a
  deterministic-probe requirement. The `hasRedirect=True` contradiction (the redirect is
  "found" but may not DISPATCH to `AwaitUnsafeOnCompleted_Neo`) needs a deterministic
  `TaskCompletionSource`-style probe to resolve.

## Working set

**Build/test commands (CRITICAL — always `-f net8.0`; build CLI with `Debug_Neo`, NEVER
TestCases with `Debug_Neo`):**
```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep   # 210/210
# AOT self-checks: replace the filter with NeoStep22SelfCheck | NeoStep23Roundtrip | NeoStep24CliRoundtrip | NeoStep25LoadExec
# Legacy-neutral check: plain `Debug` + useRegister=true, relevant filter
```

**Core code map (the AOT chain):**
- `ILRuntime/Runtime/NeoAOT/` — `NeoAssembly.cs` (format), `NeoAssemblyWriter.cs`,
  `NeoAssemblyReader.cs`, `NeoCompiler.cs` (the public CLI seam), `NeoAssemblyLoader.cs`.
- `ILRuntime/Runtime/Intepreter/RegisterVM/GenericMethodTemplate.cs` (Step 22 template +
  CloneAndPatch), `NeoStep2{2,3,4,5}*Check.cs` (the host-side self-checks).
- `ILRuntimeNeoCompiler/` — the `ilrt_neoc` standalone tool project (thin `Program.cs`).
- `ILRuntime/CLR/Method/ILMethod.cs` — the `isNeoAotBody` dual-path (Step 25) +
  `RunNeoBackHalf`/`InitCodeBody` (Step 22).
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` — the F-10-R1 gate
  (`TypeSpecializeNeoOpcodes case Ldflda:`), the template capture.
- `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs` — the async redirects
  (AwaitUnsafeOnCompleted_Neo is a tagged NIE; the sync-slice redirects work).

**State files:** `openspec/changes/neo-completion-portfolio/portfolio-run.json` (the
children map, executionOrder, runnableFrontier, completedChildren), `.trae/documents/
neo-handoff.md` (the repo-wide handoff, updated to HEAD `9c0d21fc`), `.trae/documents/
neo-deferred-items.md` (the per-item tracker; STEP-25-PARTIAL, STEP-20-PARTIAL rows).

## Next action

Pick the next child from the runnableFrontier. Recommended order (value × tractability):

1. **`neo-step25-s2-generic-at-load`** (highest value, builds directly on the proven S1) —
   wire CloneAndPatch from the `.neo` template into an AOT ILMethod's BodyRegister at
   load. The S1 V2 self-check + the body-mutation guard extend naturally. First step:
   read `openspec/changes/archive/2026-07-07-neo-step25-runtime-loader/{design.md,
   planning-context.md}` + `GenericMethodTemplate.cs`, then dump-gate how a generic
   method call on an AOT-loaded type resolves its template today (it currently keeps JIT
   — S1 skips generic-defs).
2. **`neo-step26-perf-validation`** — the benchmark suite (well-scoped, additive; deps
   satisfied). Completes the numbered AOT chain.
3. **`neo-generic-redirect-resolution` (B1)** — unblocks the async finish; needs the
   deterministic-probe discipline.

Resume by starting a fresh session and running `/opsx:auto neo-completion-portfolio`
(or `openspec pipeline resume neo-completion-portfolio --json` manually) — it reports
the `sessionHandoff` pointer (below) + the runnableFrontier; read THIS document first.
