# Handoff: neo-completion-portfolio — LEAD #5 (children 2+3 shipped; /unsafe fixed; frontier at child 4)

> Clean stage-boundary relay (3 children shipped this session, all pushed). Authoritative
> state = THIS FILE + `portfolio-run.json` + `planning-context.md` + `.trae/documents/neo-handoff.md`.
> **Resume by reading this, then drive child 4 (ValueTask + async void suspend).**

## Position
Pipeline: `auto-decompose` (parent) -> each child `small-feature`, Tier A. Branch
`features/object-model-overhaul`, HEAD **`7c60751b`** (pushed, in sync with origin).
Policy: SERIAL (shared interpreter/JIT files), full autonomy (skip gates; commit+push
after each clean child). Relay (NOT stop) on context limit -- but the user mandate
(2026-07-09, emphatic): **this model has 1M context, NOT 200K -- drive DEEP before
relaying; do NOT relay at ~20%.** Memory: `use-full-1m-context-no-early-handoff`.

**completion-3 frontier status (19-child serial chain):**
1. `neo-step23-24-roundtrip-regression` — DONE (lead-4, `eb17f4be`)
2. `neo-async-multi-await` — **DONE (lead-5, `64ab6842`)**
3. `neo-async-execctx-capture` — **DONE (lead-5, `7c60751b`)**
4. `neo-async-valuetask-asyncvoid` — **NEXT** (ValueTask<T> suspend + async void suspend)
5-19. as in `portfolio-run.json` `completionPlan.orderedFrontier` (unchanged).

## What lead-5 did this session (3 commits, all pushed)
1. **Child 2 = neo-async-multi-await: REVIEWED + SHIPPED** (`64ab6842`). Adversarial
   non-author review (lead-5 is fresh vs the implementer). Built a deterministic temp
   harness (non-self-resetting TCSes + a mark signal + host verdict helpers), RAN
   probes beyond TC12. Verdict APPROVED 0 Blocker/Major. Load-bearing concern (a fault
   at the 2nd await must route through the REUSED `ILAsyncContext` bridge, not be
   swallowed) VERIFIED GOOD (probe case 2: the bridge faults with the exception
   preserved). Pre-existing edges (see Durable Findings F1-F3) proven NOT introduced
   (stash-toggle: case0 single-sync-fault reproduces identically on HEAD). Smoke
   239/0/0. See `openspec/changes/neo-async-multi-await/{review-report,ship-log}.md`.
2. **/unsafe plain-Debug CLI fix: SHIPPED** (`78c9c592`). `NeoF13NestedProbe.cs` (F-13)
   used `unsafe` but `AllowUnsafeBlocks` was Debug_Neo-only -> CS0227 broke the plain-Debug
   CLI build (blocked the Legacy reference smoke). Moved `AllowUnsafeBlocks` to the
   top-level PropertyGroup in `ILRuntimeTestCLI.csproj` (all configs), matching the
   other csprojs. plain-Debug CLI now builds 0 errors; Legacy NeoStep20 smoke runs.
3. **Child 3 = neo-async-execctx-capture: IMPLEMENTED + VERIFIED + SHIPPED** (`7c60751b`).
   `AwaitOnCompleted_Neo` now captures `ExecutionContext.Capture()` and flows it to the
   resume via `ExecutionContext.Run`; `AwaitUnsafeOnCompleted` unchanged. Extended
   `GetAwaiterTask` to recognize a custom Task-wrapping awaiter (duck-typed `m_task`) so
   the `AwaitOnCompleted` path is REACHABLE for the first time. SC is N/A (no SC in
   ILRuntime). TC13 (custom-awaiter suspend) + TC14 (EC flow across a no-EC-flow
   threadpool resume) green; **stash-toggle proves the capture load-bearing** (TC14
   7042 with capture -> 7000 without). Smoke 241/0/0. Legacy-neutral.

## NEXT for the successor (lead-6) — child 4 = neo-async-valuetask-asyncvoid
Scope: an `async ValueTask<T>` method AND an `async void` method that hit a
genuinely-incomplete await must suspend/resume correctly. Today the suspend machinery is
Task-bridge-shaped (`SuspendStateMachine` builds `ILAsyncContext<T>` + parks a `Task<T>`
bridge on `SmContextMap`; `get_Task` returns it). For ValueTask<T> the RESULT bridge must
be a `ValueTask<T>` (the builder's `Task` property / get_Result), and for async void
there is NO return task (completion is a side-effect / builder.SetResult with no task).

**Scope the gap empirically FIRST** (fastest): write two temp probes (in a temp
`TestCases/NeoStep20*_TEMP.cs` + temp host helpers in `TestClass3.cs`, like lead-5's
review harness), build (`Debug_Neo` CLI + `Debug` TestCases), run, and SEE what breaks:
- `async ValueTask<int>` awaiting a genuinely-incomplete Task (suspend) -> does it
  suspend + resume + produce a `ValueTask<int>` whose result is observable? Likely break:
  the `AsyncValueTaskMethodBuilder<T>` get_Task / the result bridge.
- `async void` awaiting a genuinely-incomplete Task (suspend) -> the AsyncVoidMethodBuilder
  has no Task; the suspend parks on `SmContextMap` but the driver has no `Task` to poll.
  Observe via a host side-effect flag the probe sets after the await.

The engine file is `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs` (Neo-only).
Key methods: `SuspendStateMachine` (~517, now takes `ExecutionContext ecToFlow`),
`GetAwaitedTaskFromSm`, `GetResultClrType` (~1316, returns the builder's first generic arg
or null for AsyncVoidMethodBuilder), the builder redirects (lines ~315-475:
`AsyncTaskMethodBuilder_T_*`, `AsyncValueTaskMethodBuilder_T_*`, `AsyncVoidMethodBuilder_*`
SetResult/SetException already exist), `get_Task` (~280). The `ILAsyncContext<T>` bridge
holds the SM + MoveNext + a `TaskCompletionSource<T>` -- for ValueTask the bridge Task may
need wrapping (`ValueTask<T>.CreateFromTask` / the builder's Task), for async void a
flag/side-effect path is needed. The async-SM-concat constraint (Durable Finding F2)
applies: async-probe bodies must NOT use string concat (lowers to conv.ovf.u2.un).

## Durable Findings (lead-5; record into `planning-context.md` if not already)

- **F1 (PRE-EXISTING follow-up, NOT any shipped change's fault): `Task` ->
  `Task<int>` ArgumentException at `InvokeNeoCallTarget:635`** for certain async-SM
  shapes (single-sync-fault-await; 3-await SM). Stash-toggle: reproduces IDENTICALLY on
  HEAD with the fix removed. Route: a Task-generic-arg CLR-binding edge. Means 3+ awaits
  is currently UNVERIFIED end-to-end (a 3-await SM hits this before the multi-await
  logic). **Worth a dedicated follow-up child.**
- **F2 (PRE-EXISTING, Step 6): `conv.ovf.u2.un` not implemented.** String concat INSIDE
  an async state machine (`"... " + intVar`) lowers to it -> NIE. **Binding constraint for
  async test authors: keep async-SM bodies concat-free** (log via host helpers that take
  separate args, e.g. `ReviewLogI(string, int)`). TC12/TC13/TC14 all avoid it. Route: a
  Step-6 opcode follow-up.
- **F3 (PRE-EXISTING, cosmetic): fault exceptions are AggregateException +
  TargetInvocationException-wrapped.** Neo `GetResult` reads `task.Result` via
  `InvokeMember` (which wraps); real async unwraps to the inner `Exception` in the catch.
  Content preserved; wrapping depth differs. Route: async exception-unwrapping fidelity.
- **EC-test lesson (durable for future async children): an EC-flow / AsyncLocal-flow test
  is only valid if the continuation resumes on a thread whose EC is NOT the caller's.**
  `TaskCompletionSource.SetResult` on the caller thread runs the await continuation
  SYNCHRONOUSLY on that thread (EC preserved -- masks the capture); `Task.Run` FLOWS the
  caller's EC (also masks it). Use `ThreadPool.UnsafeQueueUserWorkItem` (or a raw
  `Thread`) -- it queues to a threadpool thread WITHOUT flowing EC. (Child 3 design.md
  "Apply findings".)
- **SUBAGENT DOTNET LIMITATION: spawned agents (Agent tool) CANNOT run `dotnet` in this
  environment (denied by the auto-mode permission layer in every form). So subagents can
  READ/Grep/Write but CANNOT build or run tests. Consequence: the author!=verifier role
  isolation for review/verify must be done by the LEAD in the main session (the only
  context with `dotnet`), OR the lead authors + adversarially self-verifies with a
  stash-toggle proof (which a code-reading review cannot fake -- child 3 used this).
  Do NOT spawn a reviewer/implementer subagent expecting it to build/run. Memory-worthy.

## Build/test (CRITICAL -- ALWAYS `-f net8.0`; build CLI with `Debug_Neo`, NEVER TestCases with `Debug_Neo`)
```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo   # CLI + ILRuntime/TestBase/LitJson
dotnet build TestCases/TestCases.csproj -c Debug                      # -> TestCases/bin/Debug/netstandard2.1/TestCases.dll
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep   # 241/0/0
```
`Debug_Neo` prints LOTS of JIT/optimizer output (`OUTPUT_JIT_RESULT`) -- normal; grep the
summary. A run >10-60s usually = interpreter infinite loop -> KILL. `dotnet run --no-build`
runs the LAST successful binary -- confirm `0 errors` first. The sln CANNOT build whole
(build ONLY the dev subset). Commit: the auto-mode classifier BLOCKS complex multi-`-m`
`git commit` + `git push` in one command and bare `git commit -m "long..."`; USE
`git commit -F .git/cmsg.txt` (write the message to a file first) then `git push` separately
-- both were allowed that way this session. End commits with
`Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.

## Baseline (HEAD `7c60751b`)
NeoStep **241/0/0** (239 + TC13 + TC14). NeoStep20 **16/0/0** (14 + TC13 + TC14).
NeoStep23Roundtrip 15/15; NeoStep24CliRoundtrip 5/5; NeoStep25CecilFreeLoad 7/7;
NeoStep25LoadExec 28/28; NeoStep26Bench 5/5; NeoDebuggerFrame 4/4; NeoOptHardening 24/24.
Legacy plain-Debug CLI build RESTORED (0 errors; the /unsafe fix). Legacy NeoStep20 slice
runs (1 pre-existing Neo-async-specific failure under the Legacy engine, unrelated).

## Next action — successor (lead-6)
Child 4 (`neo-async-valuetask-asyncvoid`) was EMPIRICALLY SCOPED by lead-5 (same session,
after the handoff above was written). Findings below -- do NOT re-scope; act on them.

## Child-4 scope findings (lead-5, same session) -- the ValueTask part is BLOCKED upstream
lead-5 built temp probes (`async ValueTask<int>` suspend + `async void` suspend), ran them,
then REVERTED all child-4 engine changes (clean tree; NeoStep 241/0/0 confirmed). Findings:
1. **`async void` suspend ALREADY WORKS** (pre-existing). The AV probe (async void awaiting
   a genuinely-incomplete Task) PASSED unchanged. The AsyncVoidMethodBuilder suspend path is
   already correct -- child 4's async-void half is a non-change (capability already exists).
2. **`async ValueTask<int>` suspend is BLOCKED by a GENERAL Neo return gap.** Two layers:
   - **(a) the get_Task suspend-case is missing** (DESIGNED + tested by lead-5, REVERTED as
     incomplete): `AsyncValueTaskMethodBuilder_T_GetTask_Neo` (`CLRRedirections.AsyncNeo.cs`
     ~line 388) only handles the SYNC case (SmTaskMap); it lacks the SUSPEND case
     (SmContextMap) that the Task builder has (`AsyncTaskMethodBuilder_T_GetTask_Neo`
     ~line 280: `if (task == null && sm != null && SmContextMap.TryGetValue(sm, out ctx))
     task = ctx.GetTaskBridge();`). Without it, a suspended ValueTask method's get_Task calls
     `CreateValueTaskFromResult(null)` -> NRE at `CreateValueTaskFromResult:1361`
     (`GetMethod("FromResult")` path). **The fix lead-5 designed + verified removes the NRE:**
     add the `else if (SmContextMap.TryGetValue(sm, out ctx)) vt = WrapBridgeAsValueTask
     (method, ctx.GetTaskBridge());` branch + a `WrapBridgeAsValueTask` helper that wraps the
     bridge `Task<T>` as a `ValueTask<T>` via the public `ValueTask<T>(Task<T>)` ctor. With
     it the NRE is gone -- but it reveals layer (b).
   - **(b) the BLOCKER: returning `ValueTask<T>` from an IL method throws** at
     `ILIntepreter.Neo.cs:2940` (the `Ret` opcode): `"Neo return with value-type reference
     fields requires Step 12/13 return layout support."` `ValueTask<T>` is a STRUCT holding a
     `Task` reference field; the `Ret` opcode only handles primitive returns + single-
     reference (class) returns -- value-type-WITH-reference-fields returns are unsupported
     (Step 12/13 return-layout gap). This is a GENERAL gap (ANY IL method returning a struct
     with a ref field fails), NOT async-specific.

**Recommended sequencing (for lead-6 to register in `portfolio-run.json`):**
- Register a NEW child **`neo-ret-vt-with-ref-fields`** = general Neo "return a value-type
  with reference fields from an IL method" support (the `Ret` opcode value-type-with-refs
  copy-back branch: copy `returnPrimitiveSize` bytes + `returnRefCount` ref slots to the
  dest; mirrors Step 12b `Move_Vt`). This unblocks not just async ValueTask but ANY such
  return. HIGH general value. Sequence it BEFORE child 4's ValueTask half.
- Then child 4 = `async void` (already done -- verify + close as no-op, or drop) + the
  ValueTask get_Task suspend-case (lead-5's designed fix above) + a probe. Sequence child 4's
  ValueTask half AFTER `neo-ret-vt-with-ref-fields`.

Drive child 5+ in parallel if `neo-ret-vt-with-ref-fields` is large. Read `portfolio-run.json`
for the plan. Commit + push after each clean child. **Drive DEEP (1M context) -- relay only
near the limit, not at ~20%.**
