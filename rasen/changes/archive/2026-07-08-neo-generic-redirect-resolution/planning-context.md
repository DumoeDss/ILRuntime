# Planning Context — neo-generic-redirect-resolution (B1)

> SEED for the planner. Read THIS FIRST, then the docs it points at, then
> research only what is missing. APPEND durable findings after propose.

## What this change is (one line)

Resolve **B1** — the async-suspend blocker: the 2-generic-arg redirect
(`AwaitUnsafeOnCompleted<TA,TSM>`) resolves on the Legacy `RedirectMap` but
reportedly NOT on `RedirectMapNeo`. The original finding (`hasRedirect=True`
contradicts "not found") MUST be re-resolved with a **DETERMINISTIC**
`TaskCompletionSource`-style probe (a `Task.Delay(N)` probe is RACY — it can
sync-complete before the JIT is set up, which is exactly the artifact that
disproved the sibling control-flow investigation). Then, IF B1 resolves,
re-attempt the **Phase 2** async suspend/resume machinery. Absorbs the disproven
`neo-async-controlflow-iscompleted` investigation (the control flow IS correct on
HEAD).

## Authoritative prior context (READ BEFORE PROPOSING — do not re-research)

1. `openspec/changes/neo-completion-portfolio/handoff/lead-1.md` — esp. "Dead
   ends & gotchas" (the async path is a deep rabbit hole; B1 + the
   deterministic-probe requirement; `AwaitUnsafeOnCompleted_Neo` /
   `ILAsyncContext.MoveNext` are tagged NIEs) + "Eliminated hypotheses" (the
   async control-flow bug was DISPROVEN; the `hasRedirect=True` contradiction).
2. `openspec/changes/archive/2026-07-06-neo-step20-async-suspend/` — the
   suspend-slice child that STOPPED at Phase 2 (2 stacked blockers). Its
   `design.md` + `ship-log.md` + `review-report.md` carry the B1 detail + the
   Phase-1 reachability unblockers that DID ship (B3 Nop case, B2 void-GetResult
   guard, Task.Delay redirect).
3. `openspec/changes/archive/2026-07-07-neo-async-controlflow-iscompleted/` —
   the DISPROVEN sibling (closed no-op; the control flow is correct on HEAD).
4. `.trae/documents/neo-deferred-items.md` — the STEP-20-PARTIAL row (§2) + §3
   detail; B1 is the live blocker.
5. `openspec/changes/neo-completion-portfolio/planning-context.md` — build/test
   commands + the async findings from prior children.

## The async path is a deep rabbit hole — the DETERMINISTIC probe is the gate

The prior session's binding lesson on async: **`Task.Delay(N)` is a racy probe**
(it can sync-complete, making the await path appear to take the completion
branch when it does not). The deterministic alternative is a
**`TaskCompletionSource`-style awaitable** the test controls explicitly
(SetResult AFTER the await registers), so the await is FORCED through
`AwaitUnsafeOnCompleted`. This child's propose phase MUST design that
deterministic probe FIRST; without it, any B1 "fix" is unfalsifiable (the
prior session's exact failure mode).

The planner MUST resolve, from a dump on HEAD `38133af8`:
- Does the 2-generic-arg redirect (`AwaitUnsafeOnCompleted<TA,TSM>`) actually
  fail to resolve on `RedirectMapNeo`, or does it resolve-but-not-DISPATCH?
  (The `hasRedirect=True` contradiction: the redirect may be "found" but
  dispatch to the wrong handler / a NIE.) Cite file:line in
  `CLRRedirections.AsyncNeo.cs` + the redirect-lookup path.
- Is `AwaitUnsafeOnCompleted_Neo` a tagged NIE today, or partially wired?
- What is the minimal fix that makes the deterministic probe's truly-async
  await dispatch correctly? (Focused redirect-map resolution? A redirect
  signature/arity mismatch?)

## Scope guidance (the dump-gate decides; partial-ship is the EXPECTED outcome)

The async machinery is LARGE. Realistic outcomes, best-first:
1. **B1 resolved + Phase 2 suspend machinery proven on the deterministic probe.**
   (Best case — a working truly-async await. Likely LARGE; may need frame->heap
   hoist + `ILAsyncContext<T>` continuation + resumption. The `HoistNeoILValueToHeap`
   helper + `ILAsyncContext<T>` skeleton shipped in `neo-step20-async` to de-risk.)
2. **B1 resolved (Phase 1.5) + Phase 2 deferred.** Ship the redirect fix + the
   deterministic probe as a regression guard; defer the full suspend/resume to a
   follow-up. (The likely outcome — the precedent: `neo-step20-async-suspend`
   shipped Phase 1 reachability unblockers + STOPPED at Phase 2.)
3. **B1 NOT reproducible on HEAD with the deterministic probe** (the
   `hasRedirect=True` contradiction resolves to "it works"). Then this child is
   a NO-OP / TEST-ONLY close (precedent: `neo-async-controlflow-iscompleted`,
   `neo-k2fam-bridge`) — ship the deterministic probe as a regression guard,
   document the resolution, and the suspend machinery becomes the only remaining
   gap.

**STOP / partial-ship is the binding discipline.** Do NOT force a suspend fix
past the dump-gate (the prior `neo-step20-async-suspend` correctly STOPPED at 2
stacked blockers instead of shipping a silent-wrong suspend). Expect outcome 2
or 3.

## Build + test (CRITICAL — copy from portfolio planning-context)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll \
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep20   # the async slice (9/9 baseline)
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll \
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep     # 215/215 regression
```
ALWAYS `-f net8.0`; CLI filter is a `Contains` substring (no `|`).
`Debug_Neo` prints huge JIT output — normal. A test taking >10s often = the
async state machine looping / a stuck await — kill + investigate. Truly-async
tests are TIME-SENSITIVE — use the deterministic `TaskCompletionSource` probe,
NOT real delays.

## Spec authoring traps (from handoff)

- `specs/neo-async/spec.md` delta PURE ASCII; start every requirement body with
  "... SHALL ..." on the FIRST hard-wrapped line.
- Capability: **neo-async**. NOTE: the openspec validator is flaky in this env
  (pre-existing 9/10 false-failures on capabilities OTHER than `neo-optimizer`).
  `neo-async` likely false-fails `validate` — that is PRE-EXISTING, NOT
  introduced; the LEAD does manual archive merges. Note it + proceed.
- Legacy is the REFERENCE. Neo-only changes gated `#if ENABLE_NEO_MODE`. Keep
  Legacy-neutral.

## Deliverables

`proposal.md`, `design.md` (the deterministic-probe design + the B1 dump-gate
verdict + the SHIP-vs-DEFER scope decision), `specs/neo-async/spec.md` (delta),
`tasks.md`. Scope honestly; partial-ship is the expected outcome.

---

## Append: apply-phase findings (HEAD `38133af8`)

### Verdict: outcome 2a-DEEP (NOT the predicted outcome 3)

The deterministic probe (TC8, `NeoStep20_TC8_IncompleteAwaitHitsTaggedNIE`,
backed by `TestCLRBinding.GetIncompleteTask` -- a `TaskCompletionSource<int>`
whose `SetResult` is never called) was run on HEAD `38133af8`. It does NOT
throw the tagged NIE. It HANGS (the test process times out after the
`get_IsCompleted` step; `InvokeNeoClrMethod` is never reached for
`AwaitUnsafeOnCompleted`). The design's static outcome-3 prediction is
REFUTED by the probe -- exactly the falsification the probe was built for.

### B1 redirect RESOLUTION is EXONERATED (not the bug)

Instrumented traces (temp, reverted before ship) confirm:
- The custom `AwaitUnsafeOnCompleted` / `AwaitOnCompleted` open-def NIE
  redirects ARE registered on `RedirectMapNeo`. `B1_POSTREG mapCount=69
  awaitKeys=17`; `B1_REGNEO` shows the registrations landing on the Neo map
  (hash 48285313), growing to 245+ entries through the autogen pass.
- `CLRMethod.TryGetRedirection` (`CLRMethod.cs:111-131`) is arity-agnostic and
  correct: for the closed-generic `AwaitUnsafeOnCompleted<TaskAwaiter<int>,
  IAsyncStateMachineAdaptor>` it tries `GetGenericMethodDefinition()` (open def)
  first -- whose `MethodHandle` EXACTLY matches the registered open def's handle
  -- so the lookup WOULD resolve. (The "Legacy map consulted / 47 entries / no
  Await keys" traces are the Legacy `RedirectMap`, read by the JIT's
  compile-time `cm.Redirection` check at `JITCompiler.cs:2123`/`2215` -- a
  red herring for B1; the Neo `RedirectMapNeo` has the keys.)
- `TaskAwaiter_T_GetIsCompleted_Neo` is correctly dispatched (first-registered-
  wins; custom beats the autogen `default(TaskAwaiter<int>)` stub -- proven by
  TC1 not NRE-ing) and returns `false` for the incomplete Task (proven by TC10
  and the `B1_ISC taskCompleted=False` trace).

So the design's proposed fix sites (`TryGetRedirection` / the JIT call-operand)
are both exonerated and would NOT fix the hang. NO engine edit is warranted.

### The REAL blocker: a MoveNext control-flow bug (suspend-path scope)

After `get_IsCompleted` returns false, the state machine NEVER reaches the
`AwaitUnsafeOnCompleted` call -- its `Call` handler never fires (traced via a
`B1_CALLPRE` print in `ExecuteNeo`'s `Call` case) -- AND never reaches
`GetResult` either (`B1_GETRESULT` never fires). It hangs in a loop / block
transition between the `brtrue` on the `IsCompleted` result and either branch.
This is the prior `neo-step20-async-suspend` session's note ("a stacked
control-flow issue routes MoveNext to the completion path regardless of
IsCompleted"), which the disproven `neo-async-controlflow-iscompleted` sibling
FAILED to exercise because it only used sync-completing awaits (the same
accident that makes TC9 pass). This is suspend-path territory, owned by the
`neo-step20-async-suspend` resume child -- out of scope for B1 (redirect
resolution).

### Ship state (partial-ship, as expected)

- TC8 (`NeoStep20_TC8_IncompleteAwaitHitsTaggedNIE`) is MARKED
  `[ILRuntimeTest(Ignored = true)]` -- the documented hang-reproducer, excluded
  from the smoke so it cannot hang the run. Un-ignore once the control-flow bug
  is fixed (the redirect already resolves, so the tagged NIE will fire and TC8
  will pass).
- TC9 (`NeoStep20_TC9_SyncControlSkipsAwaitUnsafeOnCompleted`) -- active guard:
  a sync-completing await must NOT reach `AwaitUnsafeOnCompleted`.
- TC10 (`NeoStep20_TC10_IncompleteTaskIsCompletedIsFalse`) -- active guard: the
  incomplete Task's awaiter reports `IsCompleted == false` (isolates the B1
  redirect resolution + the `get_IsCompleted` redirect from the MoveNext bug).
- The two async helper bodies (`NeoStep20_IncompleteAwaitProbe`,
  `NeoStep20_CompletedAwaitControl`) are PRIVATE so the harness does not
  auto-discover the hanging helper.
- ALL temp engine instrumentation was reverted (engine at HEAD); this is a
  TEST-ONLY change.

### Gates

- NeoStep20 slice: `Ran 12 tests, 0 failed, 1 ignored` (EXIT=0, no hang).
- NeoStep smoke: `Ran 218 tests, 0 failed, 1 ignored` (215 baseline + TC9 + TC10
  + TC8-ignored; no regression).
- Legacy-neutral: plain `Debug` CLI build = 0 errors; Legacy NeoStep20 =
  `Ran 12 tests, 0 failed, 1 ignored`.
- NeoOptHardening: not re-run (no engine edit landed; 24/24 baseline unchanged).

### Handoff

`handoff/implementer-1.md` carries the full diagnostic trace + the recommended
next action for the LEAD (defer the MoveNext control-flow fix to the suspend
follow-up; this change is a test-only partial-ship that converts the prior
UNFALSIFIABLE B1 claim into a PROVEN, isolated finding).
