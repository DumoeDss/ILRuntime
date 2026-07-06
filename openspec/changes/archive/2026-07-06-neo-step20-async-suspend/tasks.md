## STATUS (apply, 2026-07-06) -- STOPPED at Phase 1

**Outcome:** Phase 2 DEFERRED. B1 NOT fixed (escalated as broad/entangled
with a second stacked control-flow bug). Phase-1 exit gate NOT met (TC9 did
not reach `AwaitUnsafeOnCompleted_Neo`). Shipped: B3 Nop case, B2
void-GetResult guard, Task.Delay redirect (all focused + Neo-only). Removed:
TC9 probe (false-positive green via blocking GetResult), all PROBE
diagnostics, B1 instrumentation. See planning-context.md "Findings --
neo-step20-async-suspend (apply, 2026-07-06)" for the full dump + the two
split children (`neo-async-controlflow-iscompleted`, `neo-generic-redirect-
resolution`).

- [x] 1.1, 1.3, 1.4, 1.5 -- dump-gate recon done (entry-trace confirmed NOT
      to fire; JIT emits `call AwaitUnsafeOnCompleted`; instrumentation
      reverted).
- [x] 2.1 B3 Nop -- SHIPPED.
- [x] 2.2 B2 void-GetResult -- SHIPPED.
- [ ] 2.3 B1 redirect-resolution -- NOT DONE (broad/entangled; escalated to
      `neo-generic-redirect-resolution`). STOP GATE tripped.
- [x] 2.4 Task.Delay redirect -- SHIPPED.
- [ ] 2.5 TC9 reaches NIE -- exit gate NOT MET (probe never entered the
      redirect; removed as false-positive green).
- [ ] 3.1-3.6 Phase 2 -- DEFERRED (not started).
- [ ] 4.1-4.4 AP1-AP4 -- DEFERRED (Phase 2). [x] 4.5 AP5 (Nop no-op) --
      204/204 baseline unchanged.
- [ ] 5.1-5.5 Ship gate -- partial: smoke 204/204 green, NeoStep20 9/9, 0
      PROBE lines; STOP-condition audit documents the split (this section).

## 1. Dump-gate reconnaissance (PROBE-FIRST; revert instrumentation before any commit)

- [ ] 1.1 Build CLI `Debug_Neo` + TestCases `Debug`; confirm NeoStep 204/204
  baseline green.
- [ ] 1.2 Write the truly-async probe into `TestCases/NeoStep20Test.cs`:
  `static async Task<int> NeoStep20_AsyncSuspendHelper() { await Task.Delay(10); return 43; }`
  + a non-async driver `NeoStep20_TC9_AsyncSuspendProbe` that blocks on the
  result (spin-wait with a max-iterations guard; the test method is NOT async).
- [ ] 1.3 Add temp `Console.WriteLine` at the TOP of
  `AwaitUnsafeOnCompleted_Neo` ("ENTERED") and run the probe. Confirm the
  trace does NOT fire on HEAD (the redirect is unreachable end-to-end). This
  is the load-bearing dump-gate for B1.
- [ ] 1.4 Dump the helper MoveNext JIT (final optimized opcodes). Confirm the
  `call AwaitUnsafeOnCompleted<TA,TSM>` is emitted (opcode 18) with the 3
  byref params (builder this, awaiter local, sm). Record the byref slot
  layout for the D2 wiring.
- [ ] 1.5 Revert all probe instrumentation; re-confirm NeoStep 204/204.

## 2. Phase 1 -- suspend-reachability unblockers (each focused + dump-gated)

- [ ] 2.1 **B3 (Nop):** add `case OpCodeREnum.Nop: ip++; continue;` to the
  `ExecuteNeo` switch (`ILIntepreter.Neo.cs`, Neo-only). AP5: confirm the
  204/204 baseline is unchanged (a Nop between side-effecting ops must not
  advance state).
- [ ] 2.2 **B2 (void-GetResult):** in `TaskAwaiter_T_GetResult_Neo`
  (`CLRRedirections.AsyncNeo.cs`), gate the `Result` read on
  `method.DeclearingType.TypeForCLR.IsGenericType`. For the non-generic
  `TaskAwaiter`, return without writing (GetResult is void). Confirm this
  also unblocks the sync-slice TC2/TC5-family `await Task` (non-generic)
  shape if those probes are re-enabled.
- [ ] 2.3 **B1 (load-bearing, 2-generic-arg redirect resolution):** dump-gate
  `AppDomain.TryGetRedirection` (and/or `CLRMethod`'s
  `GetGenericMethodDefinition` handling) for the closed-generic
  `AwaitUnsafeOnCompleted<TaskAwaiter, IAsyncStateMachineAdaptor>` call.
  Confirm the lookup misses the registered open-definition redirect (the
  1-arg `Start<TSM>` matches; the 2-arg `AwaitUnsafeOnCompleted<TA,TSM>`
  does not). Apply the arity-agnostic closed->open match fix.
  **STOP GATE:** if the fix touches shared dispatch broadly (affects non-async
  generic CLR redirects), STOP and split into `neo-generic-redirect-resolution`;
  do NOT proceed to Phase 2. Re-run 1.3's entry-trace probe: the "ENTERED"
  trace MUST now fire (the redirect body executes). The probe still throws the
  tagged NIE (Phase 2 implements the body) -- this is the Phase-1 exit gate.
- [ ] 2.4 **Task.Delay redirect (test-infra):** add a permanent
  `Task_Delay_Neo` redirect + `Register()` entry for `Task.Delay(int)` in
  `CLRRedirections.AsyncNeo.cs` (returns the real `Task.Delay(ms)`). This is
  the awaitable source for the suspend green test.
- [ ] 2.5 Add `NeoStep20_TC9_AwaitTaskDelay` probe (the helper + driver from
  1.2). Confirm it FAILS with the tagged NIE (redirect now reachable, body
  still throws). Commit Phase 1 (the sync slice stays green; the NIE is now
  reachable -- a machine-checkable scope boundary).

## 3. Phase 2 -- suspend machinery (ONLY after 2.3's entry-trace fires)

- [ ] 3.1 Wire `AwaitUnsafeOnRegistered_Neo` body (`CLRRedirections.AsyncNeo.cs`):
  recover the SM heap ILTypeInstance (slot 2 objIdx -> mStack, or
  `CurrentAsyncSm`); read the awaiter (slot 1 -> `ReadNeoValueType` ->
  `GetAwaiterTask` -> the Task); construct `ILAsyncContext<T> { stateMachine
  = sm, moveNextMethod = GetMoveNext(sm.Type) }`; register
  `task.UnsafeOnCompleted(ctx.MoveNextDelegate)`; park the context on
  `SmContextMap[sm]`; return WITHOUT `SetResult` (suspend).
- [ ] 3.2 Add the `MoveNextDelegate` (an `Action`) + the `SmContextMap`
  (ThreadStatic `Dictionary<ILTypeInstance, ILAsyncContext<T>>`) to
  `CLRRedirections.AsyncNeo.cs` / `ILAsyncContext.cs`.
- [ ] 3.3 Implement `ILAsyncContext<T>.MoveNext()` (`ILAsyncContext.cs`):
  `RequestILIntepreter`; `try { build frame, write SM as slot-0 this,
  ExecuteNeo(moveNextMethod, ...), route SetResult/SetException to core via
  _currentAsyncContext ThreadStatic } finally { FreeILIntepreter }`.
- [ ] 3.4 Add the `_currentAsyncContext` ThreadStatic sink-swap: the
  `SetResult`/`SetException` redirects check `_currentAsyncContext` FIRST; if
  set, call `ctx.core.SetResult(value)` / `ctx.core.SetException(ex)` instead
  of stashing in `SmTaskMap`. (Resolves OQ2.)
- [ ] 3.5 Route `get_Task` (`AsyncTaskMethodBuilder_T_GetTask_Neo`): if
  `SmTaskMap[sm]` exists -> sync path (unchanged); else if
  `SmContextMap[sm]` exists -> build a `Task<T>` backed by the context (TCS
  bridge -- OQ3's recommended first cut); else -> defensive default.
- [ ] 3.6 `NeoStep20_TC9_AwaitTaskDelay` turns GREEN (suspend + threadpool
  resume + Result == 43).

## 4. Adversarial probes (MANDATORY before ship)

- [ ] 4.1 **AP1 load-bearing:** `await Task.Delay(10); return 43;` ->
  Result == 43 (FAIL-on-HEAD -> PASS-after).
- [ ] 4.2 **AP2 locals-survive-resume:** `int x = 7; await Task.Delay(1);
  return x + 36;` -> Result == 43 (the resumed MoveNext reads the pre-await
  local from the heap SM).
- [ ] 4.3 **AP3 faulted task:** `await Task.Delay(1); throw new Exception("x");`
  -> returned Task is Faulted with the exception.
- [ ] 4.4 **AP4 pool-isolation:** add temp pool alloc/hit counters to
  `RequestILIntepreter`/`FreeILIntepreter`; confirm the resumed MoveNext runs
  on a FRESH pooled interpreter and `FreeILIntepreter` fires on every resume
  path (happy, exception, nested). Remove instrumentation before ship.
- [ ] 4.5 **AP5 Nop no-op:** confirm the `Nop` case did not perturb the
  204/204 baseline (run early; re-confirm at ship).

## 5. Ship gate

- [ ] 5.1 Full `NeoStep` smoke: 204/204 (pre-existing) + the new
  `NeoStep20_TC9_AwaitTaskDelay` + AP1-AP3 = the new baseline. No regression
  in NeoStep20 9/9 (sync slice), NeoOptHardening 24/24.
- [ ] 5.2 Legacy (plain `Debug` + `useRegister=true`): NeoStep20 filter
  unchanged (Neo-only edit; Legacy byte-identical).
- [ ] 5.3 All probe instrumentation removed; working tree contains only the
  production edits (Nop case, void-GetResult guard, B1 redirect-resolution
  fix, Task.Delay redirect, AwaitUnsafeOnRegistered body, ILAsyncContext
  MoveNext, get_Task routing) + the keeper probes.
- [ ] 5.4 STOP-condition audit: if B1 was split out, document the split in
  `ship-log.md` + `neo-deferred-items.md`; if B1 landed focused, document the
  redirect-resolution fix's blast-radius sweep.
- [ ] 5.5 Write `ship-log.md` (verification evidence + review verdict +
  delivered/deferred scope). Update `neo-deferred-items.md`
  (STEP-20-PARTIAL) + the handoff §5/§6.
