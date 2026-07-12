# Tasks: neo-async-multi-await

Status: PROPOSED (planner). The implementer owns execution + the dump-gate
re-confirmation. Every engine edit is Neo-only (`#if ENABLE_NEO_MODE` or in a
Neo-only file); Legacy-neutral by construction.

## Block 0 -- dump-gate (re-confirm on HEAD before designing the fix)

- [ ] 0.1 Confirm HEAD is healthy: build CLI (`dotnet build
  ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`) + TestCases
  (`dotnet build TestCases/TestCases.csproj -c Debug`); run the `NeoStep20`
  smoke -> expect 13/0/0 (and `NeoStep` 238/0/0). If green, proceed.
- [ ] 0.2 Add the second incomplete-`Task` host cell (D4) to
  `ILRuntimeTestBase/TestFramework/TestClass3.cs`: `s_incompleteTcs2` +
  `GetIncompleteTask2()` + `CompleteIncompleteTask2(int)`, mirroring the existing
  `s_incompleteTcs` / `GetIncompleteTask` / `CompleteIncompleteTask` contract
  (self-resetting, atomic swap).
- [ ] 0.3 Add the TC12 probe + driver (D4) to `TestCases/NeoStep20Test.cs`:
  `NeoStep20_TwoIncompleteAwaitsProbe` (PRIVATE -- awaits `GetIncompleteTask()`
  then `GetIncompleteTask2()`, returns `va + vb`) +
  `NeoStep20_TC12_TwoIncompleteAwaits` (the driver; GATE 1 `!t.IsCompleted`,
  `CompleteIncompleteTask(va)`, `CompleteIncompleteTask2(vb)`, bounded spin-wait,
  GATE 2 `t.Result == va + vb`). Add `using ILRuntimeTest;` for the
  `[ILRuntimeTest(Ignored = true)]` attribute. Mark TC12 `[Ignored]`.
- [ ] 0.4 Re-confirm the HEAD failure: temporarily UN-IGNORE TC12, rebuild
  TestCases + CLI, run `dotnet run -c Debug_Neo -f net8.0 --project
  ILRuntimeTestCLI --no-build -- <dll> <patch> true NeoStep20_TC12`. EXPECT the
  tagged NIE: "Neo async multi-Task awaiter not supported (single-Task shape
  only); ... State machine hoists 2 Task fields." (the binding FAIL-on-HEAD
  proof). Re-IGNORE TC12.
- [ ] 0.5 (OPTIONAL, grounds D1/OQ1) Add a temporary `[ASYNC-DUMP]` trace to
  `AwaitUnsafeOnCompleted_Neo` printing the SM `FieldMapping` + `<>1__state`
  (read via `FieldMapping["<>1__state"]` -> `GetFieldOffset` ->
  `Marshal.ReadInt32(Primitives, primOff)`) + the `ManagedObjects` element types
  at suspend. Confirm: `<>1__state` is 0 at await1 / 1 at await2; `<>u__1`
  (`ManagedObjects` awaiter entry) is the active awaiter at BOTH suspends;
  `directTaskCount == 2` at the second suspend. REVERT the trace before ship.

## Block 1 -- the fix (D1 + D2 + D3)

- [ ] 1.1 **D1 -- `GetAwaitedTaskFromSm` awaiter-first recovery**
  (`ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs`). Reorder so the
  awaiter-derived `Task` is the PRIMARY source:
  1. Scan `ManagedObjects` for an awaiter (a boxed `TaskAwaiter` /
     `TaskAwaiter<T>` / etc. recognized by `GetAwaiterTask`). For each candidate,
     `GetAwaiterTask(o) as Task`; the FIRST non-null `Task` whose awaiter is
     non-default is the active one. (OQ1: if multiple non-null awaiters can
     coexist -- multi-awaiter-TYPE SM -- prefer the HIGHEST-index non-null
     awaiter; dump-gate this on a multi-awaiter-TYPE probe if needed.) RETURN it.
  2. If NO awaiter-derived `Task`: scan for a directly-hoisted `Task`. EXACTLY
     ONE -> return it (the single-await shape, unchanged). MORE THAN ONE ->
     ambiguous -> throw the NARROWED tagged NIE (1.3).
  3. Last fallback: the existing awaiter-`m_task` box scan (unchanged).
- [ ] 1.2 **D2 -- confirm `TaskAwaiter_T_GetResult_Neo` fallback is multi-await-
  correct.** The fallback (`task == null -> GetAwaitedTaskFromSm(sm)`) now returns
  the active `Task` (awaiter-first). NO code change expected beyond reusing 1.1;
  confirm via the TC12 resume trace (OQ2) that the SECOND resume reads `task2`,
  not stale `task1`. If stale, STOP and route the MoveNext-state reload issue to a
  follow-up (do NOT ship a silent-wrong-result fix).
- [ ] 1.3 **D3 -- narrow the tagged NIE.** Update the NIE message to reflect the
  narrowed trigger: "Neo async multi-Task awaiter not supported (no resolvable
  awaiter and an ambiguous multi-Task scan); the currently-awaited Task cannot be
  disambiguated." The NIE fires ONLY when (a) no awaiter yielded a `Task` AND (b)
  `directTaskCount > 1`. The common multi-await shape (resolvable awaiter) does
  NOT throw.
- [ ] 1.4 Build the CLI (`Debug_Neo`, 0 errors) + TestCases (`Debug`, 0 errors).
  Confirm plain-`Debug` compiles the Neo file out (Legacy-neutral).

## Block 2 -- the deterministic gate (TC12 green)

- [ ] 2.1 UN-IGNORE `NeoStep20_TC12_TwoIncompleteAwaits`. Rebuild TestCases +
  CLI. Run `NeoStep20_TC12` -> EXPECT PASS (GATE 1 `!t.IsCompleted`; GATE 2
  `t.Result == va + vb`). This is the binding success criterion (NOT a green
  smoke).
- [ ] 2.2 **Stash-toggle proof:** revert ONLY the `GetAwaitedTaskFromSm` change
  (Block 1.1), rebuild, run `NeoStep20_TC12` -> EXPECT FAIL (the tagged NIE
  faults the task OR the wrong-Task continuation gives a wrong/hanging result).
  Restore the change. (The binding FAIL-on-HEAD -> PASS-after proof.)
- [ ] 2.3 Run the FULL `NeoStep20` smoke -> EXPECT 14/0/0 (13 + TC12, no
  regressions to TC1/TC4/TC6/TC7/TC8/TC9/TC10/TC11).
- [ ] 2.4 Run the FULL `NeoStep` smoke -> EXPECT 239/0/0 (238 + TC12; no
  regressions). Run `NeoOptHardening` -> EXPECT 24/24 (no optimizer change).
- [ ] 2.5 (Adversarial, OPTIONAL) Construct `NeoStep20_TC13_MultiAwaitDiffTypes`
  (`await taskInt; await taskString;` -- two different awaiter types) to probe
  OQ1 (the multi-awaiter-TYPE disambiguation). If it PASSES, add as a keeper; if
  it reveals a stale-awaiter shadow bug, record it as a follow-up (the
  highest-index preference in 1.1 is the mitigation).

## Block 3 -- Legacy-neutral + ship

- [ ] 3.1 Confirm Legacy-neutral: build the CLI with plain `Debug` (0 errors --
  the Neo file is `#if ENABLE_NEO_MODE`-gated). Optionally run Legacy
  `NeoStep20` (`Debug` + `useRegister=true`) -> EXPECT 13/0/0 (no Legacy change).
- [ ] 3.2 Update the deferred-items doc (`.trae/documents/neo-deferred-items.md`):
  move STEP-20-PARTIAL's "multi-await" bullet to RESOLVED (cross-ref this
  change). Update the canonical `neo-async/spec.md` Accepted-known section is
  handled at archive (the delta syncs it).
- [ ] 3.3 Write `ship-log.md`: the dump-gate outcome (HEAD NIE), the root cause
  (count-based scan vs the single reused `<>u__1` awaiter field), the fix (D1),
  the stash-toggle proof, the final counts, the accepted-known limitations.
- [ ] 3.4 Stage precisely: `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs`
  + `TestCases/NeoStep20Test.cs` + `ILRuntimeTestBase/TestFramework/TestClass3.cs`
  + the `openspec/changes/neo-async-multi-await/` artifacts + the deferred-items
  doc. EXCLUDE the `.pdb` / `.gitignore` / `nuget.config` / `.claude/` /
  `.vscode/` / `CLAUDE.md` churn. Commit message: `Neo step 20: multi-await
  suspend/resume (the <>u__1 awaiter-field disambiguation)`. End with the
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>` trailer.

## Status

**APPLIED (2026-07-09, completion-3 wave).** All blocks done. D1 + D2 + D3
applied as designed; D4 applied + EXPOSED a load-bearing piece the design
under-specified (D5 asserted "no ILAsyncContext change" but `SuspendStateMachine`
created a FRESH context per suspend, orphaning the driver's bridge at suspend #2)
-- FIXED: the context is now REUSED across suspends via `SmContextMap[sm]`.
TC12 GREEN + un-ignored. See `design.md` "Apply findings" for the full record
(file:line, the deviation + why, the stash-toggle before/after, durable
findings). Final gates: NeoStep20 14/0/0, NeoStep 239/0/0, NeoOptHardening 24/0/0,
Legacy-neutral. NOT YET COMMITTED (the LEAD ships per the handoff contract).
