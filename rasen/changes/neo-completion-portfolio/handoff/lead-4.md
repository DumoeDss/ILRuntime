# Handoff: neo-completion-portfolio — LEAD #4 (regression gate shipped; async multi-await apply-done uncommitted; review+ship async, then drive the wave)

> Session hit the context limit mid-wave. This is a clean stage-boundary handoff (all
> workers returned DONE; no worker in-flight). Authoritative state = THIS FILE +
> `portfolio-run.json` + `planning-context.md` + `.trae/documents/neo-handoff.md`.
> **Resume by reading this, then drive the frontier (review+ship async-multi-await first).**

## Position
Pipeline: `auto-decompose` (parent) -> each child `small-feature`, Tier A. Branch
`features/object-model-overhaul`, HEAD **`eb17f4be`** (pushed, in sync with origin).
Policy: SERIAL (shared interpreter/JIT files), full autonomy (skip gates; commit+push
after each clean child). Relay (NOT stop) on context limit — the user mandate is
"complete ALL tasks, no deferral, don't stop until done."

**completion-3 wave (lead-4):** the lead-3 handoff exposed the completion-2
"Non-Goals/deferred" over-claim — the 18 harder variants are REAL must-do children.
lead-4 registered **19 children** (1 regression gate + 18 Remaining) as a strict serial
chain. Full plan + scopes + DAG = `portfolio-run.json` `completionPlan.orderedFrontier`
+ `children`.

## What lead-4 did this session
1. **Env-health probe** found a RED baseline lead-3 over-claimed: NeoStep23Roundtrip
   **13/15** + NeoStep24CliRoundtrip **4/5** (lead-3 said 15/15 + 5/5). Registered a
   front regression-gate child.
2. **Child 1 = neo-step23-24-roundtrip-regression: DONE + committed + pushed (`eb17f4be`).**
   Root cause (dump-gate): (a) `BuildMethodDef` `ReturnTypeRefIdx` (S3-2, commit f32f816e)
   cast an open-generic method's generic-param return as CLRType ->
   `InvalidCastException ILGenericParameterType -> CLRType`; fix =
   `ResolveReturnTypeCecilRef` (handles ILType/ILGenericParameterType/CLRType). (b)
   `Optimizer.LowerR1` indexed `localInfos[0]` on the void-Ret empty `.cctor` (phantom
   `Register1=0`, exposed when S3-4 force-compiles the .cctor) -> `IndexOutOfRange`; fix
   = Neo-only bounds-check. NeoStep23Roundtrip 15/15 + NeoStep24CliRoundtrip 5/5 +
   NeoStep 238/0/0; stash-toggle RED->GREEN; LEAD re-verified post-concurrent-workers.
   3 files +41/-2, all `#if ENABLE_NEO_MODE`.
3. **Child 2 = neo-async-multi-await: APPLY-COMPLETE, UNCOMMITTED, NOT yet reviewed/shipped.** (See IN-FLIGHT below.)

## IN-FLIGHT: neo-async-multi-await (apply done; needs review -> ship -> commit)
The planner (dump-gate) confirmed HEAD fails a 2-incomplete-await probe (NIE: "Neo
async multi-Task awaiter not supported..."). The implementer applied design D1-D4
(all Neo-only `#if ENABLE_NEO_MODE`):
- **D1+D3** `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs`
  `GetAwaitedTaskFromSm`: AWAITER-FIRST — scan `ManagedObjects` for the single reused
  `<>u__1` awaiter -> `GetAwaiterTask` -> the active Task (unambiguous for any # of
  awaits); the single-Task scan is now a fallback; the multi-Task NIE narrowed to
  "fires only when no awaiter resolves AND >1 Task field".
- **LOAD-BEARING DEVIATION** `SuspendStateMachine` (same file): was creating a FRESH
  `ILAsyncContext<T>` per suspend (orphaning the FIRST suspend's bridge that the
  driver/test observes); fixed to **REUSE `SmContextMap[sm]` across suspends** (only the
  first suspend constructs; later suspends re-bind the resume `Action` onto the same
  instance). The design's "D5: no ILAsyncContext change" was WRONG — dump-isolated.
  D2 (`GetResult`) unchanged (reuses D1).
- Test: `NeoStep20_TC12_TwoIncompleteAwaits` + a 2nd independent TCS host cell
  (`GetIncompleteTask2`/`CompleteIncompleteTask2` in `TestClass3.cs`). NeoStep20
  **14/0/0** + NeoStep **239/0/0** + stash-toggle RED->GREEN.

**UNCOMMITTED files (confirm with `git status`):**
- `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs`
- `TestCases/NeoStep20Test.cs`
- `ILRuntimeTestBase/TestFramework/TestClass3.cs`
- `openspec/changes/neo-async-multi-await/` (proposal/design/tasks/specs + design.md
  "## Apply findings")

**NEXT for the successor (lead-5) — IN THIS ORDER:**
1. **Non-author REVIEW (adversarial — async is where "green smoke != correct gate" bit
   before):** construct multi-await edge cases BEYOND TC12 and run them — 3+ sequential
   incomplete awaits; a Task that FAULTS at the 2nd await (fault must propagate, not be
   swallowed/orphaned by the reused bridge); an await inside a LOOP (repeated
   suspend/resume on the same SM); mixed completed-then-incomplete; a `Task<int>` result
   across 2 awaits (result marshals correctly through the reused bridge). Confirm the
   narrowed NIE still fails-loud on a genuinely-unsupported shape. Write
   `review-report.md`. (Author = the implementer worker; reviewer MUST be a fresh worker.)
2. If clean (0 Blocker/Major) -> **SHIP**: write `ship-log.md`, mark portfolio child
   `done`, advance frontier to child 3, **commit** (message: `Neo step20 async:
   multi-await suspend/resume (awaiter-first GetAwaitedTaskFromSm + ILAsyncContext reuse
   across suspends)`), **push**. If findings -> review-loop (cap 3).
3. **Then child 3 = neo-async-execctx-capture** (AwaitOnCompleted ExecutionContext /
   SynchronizationContext capture — `AwaitOnCompleted_Neo` currently mirrors
   `AwaitUnsafeOnCompleted` without the capture).

## PRE-EXISTING issue flagged (fix when convenient; NOT any completion-3 change's fault)
**`NeoF13NestedProbe.cs` needs `/unsafe`, enabled only under `Debug_Neo`** (from the F-13
child, commit 6d0efe68). This breaks the **plain-`Debug` CLI build**, so the Legacy
`useRegister=true` regression smoke CANNOT currently be run via the CLI. Impact: blocks
the Legacy-neutral stash-toggle proof for any change. NOTE: async-multi-await is
Legacy-neutral BY CONSTRUCTION (`CLRRedirections.AsyncNeo.cs` is `#if ENABLE_NEO_MODE` —
compiled out under plain Debug), so it does NOT need the Legacy smoke. But the /unsafe
break should be fixed (likely enable `AllowUnsafeBlocks` in the relevant csproj for all
configs, or `#if`-gate the probe's unsafe block) to restore the Legacy reference build.
Track as a small fix-up child, or fold into the next ship. **Verify with a plain-`Debug`
CLI build first** (`dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug`) to
confirm it's still broken before fixing.

## completion-3 frontier (serial chain; portfolio-run.json completionPlan.orderedFrontier)
1. `neo-step23-24-roundtrip-regression` — **DONE** (`eb17f4be`)
2. `neo-async-multi-await` — **APPLY-COMPLETE (in-flight)** <-- RESUME HERE
3. `neo-async-execctx-capture` — AwaitOnCompleted ExecutionContext/SyncContext capture
4. `neo-async-valuetask-asyncvoid` — ValueTask<T> + async void suspend path
5. `neo-async-taskrun-ildelegate` — Task.Run(ilLambda) IL-delegate-through-CLR round-trip
6. `neo-async-valuetask-zeroalloc` — zero-alloc ValueTask<T> (perf)
7. `neo-aot-clrbase-iface` — Cecil-free CLR base/interface resolution (NEO-AOT-ADAPTOR-SKIP inverse)
8. `neo-aot-generic-cecilfree` — Cecil-free generic instances (S2 T-identity-token)
9. `neo-aot-crossprocess` — cross-PROCESS .neo load
10. `neo-aot-multi-hotfix` — multi-hotfix-assembly cross-references
11. `neo-debugger-ilvt-local` — IL-value-type-LOCAL reconstruction
12. `neo-debugger-aot-body` — AOT-body variable inspection (serialize var metadata into .neo)
13. `neo-debugger-cli-protocol` — CLI debugger-protocol capstone (VSCode DAP)
14. `neo-byref-clr2il-delegate` — CLR->IL delegate callback with a byref param (List.ForEach)
15. `neo-byref-ldind-ref-heap` — Step-17 ldind_ref heap-IL-ref-field read (ILIntepreter.Neo.cs:3862)
16. `neo-aot-byref-wireup` — AOT (ilrt_neoc) wire-up of the F-7 byref map
17. `neo-array-multidim-ilvt` — IL VT-element multi-dim array [,] + multi-dim Address (ldelema)
18. `neo-qstruct-reproducer` — Q-STRUCT reproducer (fix if reproduces, else confirmed-closed)
19. `neo-qlong-reproducer` — Q-LONG reproducer (fix if reproduces, else confirmed-closed)

## Build/test (CRITICAL — ALWAYS `-f net8.0`; build CLI with `Debug_Neo`, NEVER TestCases with `Debug_Neo`)
```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo   # transitively builds ILRuntime/ILRuntimeTestBase/LitJson
dotnet build TestCases/TestCases.csproj -c Debug                      # -> TestCases/bin/Debug/netstandard2.1/TestCases.dll
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep   # 239/0/0 with the uncommitted async fix; 238/0/0 at HEAD eb17f4be
```
`Debug_Neo` prints LOTS of JIT/optimizer output (`OUTPUT_JIT_RESULT`) — normal; grep for
the summary. A run >10-60s usually = interpreter infinite loop -> KILL. NOTE:
`dotnet run --no-build` runs the LAST successful binary — confirm `0 errors` before
trusting it. The sln CANNOT build whole (the VS2022 VSIX can't consume netstandard2.1) —
build ONLY the dev subset.

## Baseline (HEAD `eb17f4be`)
NeoStep **238/0/0** (at HEAD); **239/0/0** with the uncommitted async-multi-await fix.
NeoStep20 **14/0/0** (with the fix; 13/0/0 at HEAD). NeoStep23Roundtrip **15/15**;
NeoStep24CliRoundtrip **5/5**; NeoStep25CecilFreeLoad 7/7; NeoStep25LoadExec 28/28;
NeoStep26Bench 5/5; NeoDebuggerFrame 4/4; NeoF4ParamRun 2/2; NeoStep22SelfCheck 55/55;
NeoOptHardening 24/24. (Legacy plain-Debug CLI build is BROKEN by the /unsafe issue above.)

## Durable findings (full set in planning-context.md "completion-3 durable findings")
- **ILGenericParameterType is a THIRD IType kind** (alongside ILType/CLRType) — route it
  like ILType (use its `.TypeReference`); never fall through to a `(CLRType)` cast.
- **`dotnet run --no-build` uses the last binary** — confirm `0 errors` first (stale-binary trap).
- **shared JIT `Code.Ret` leaves `Register1=0` for void methods** — Neo lowering consumers
  that index `frame.LocalInfos[register]` must bounds-check.
- **async:** the active await identity lives in the single reused `<>u__1` awaiter FIELD
  (NOT the Task operand fields); `<>1__state` is the resume discriminator; a multi-await
  SM reuses ONE `ILAsyncContext` across suspends (the driver observes the first suspend's
  bridge — constructing a fresh one per suspend orphans it).
- **CONCURRENCY:** after concurrent workers, RE-VERIFY the working tree (`git diff --stat`
  + grep fix markers + re-run gates) before committing — a co-worker's cleanup can
  mask/revert an uncommitted fix (here benign; LEAD re-ran the gates to prove it).

## Next action — successor (lead-5)
Review + ship `neo-async-multi-await` (the in-flight child above), then drive the
completion-3 wave to FULL completion (no deferral) — child 3 onward. Read
`portfolio-run.json` (`completionPlan.orderedFrontier` + each child's scope) for the
plan; `.trae/documents/neo-handoff.md` for the workflow/gotchas. Commit + push after each
clean child. Relay (not stop) on the next context limit.
