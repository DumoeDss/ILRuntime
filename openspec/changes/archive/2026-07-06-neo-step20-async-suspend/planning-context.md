# Planning Context — neo-step20-async-suspend (LEAD seed)

> SEED for the planner. Read THIS FIRST, then the prior async artifacts, then
> research only what is missing. APPEND durable findings after propose.

## User intent

Continue the Neo completion portfolio. This child = the **truly-async
suspend/resume slice** (the second half of Step 20). The sync slice
(`neo-step20-async`) already shipped the builder redirects + Start->MoveNext
in-place + awaiter/Task accessor redirects + `HoistNeoILValueToHeap` helper +
`ILAsyncContext<T>` skeleton. This slice WIRES the suspend path: a real
`AwaitUnsafeOnCompleted` -> frame-to-heap hoist + `ILAsyncContext<T>`
continuation + resumption. Capability `neo-async`. Full autonomy; LEAD commits
+ pushes after.

## What is ALREADY shipped (the foundation -- do NOT re-derive; reuse)

From `openspec/changes/archive/2026-07-06-neo-step20-async/` (read its design.md
+ ship-log.md -- load-bearing prior-art):
- **Builder redirects** (`CLRRedirections.AsyncNeo.cs`): Start->MoveNext in-place
  (D2), SetResult/SetException sync, Task getter, the awaiter/Task accessor
  redirects (`TaskAwaiter_T_GetIsCompleted_Neo`, `_GetResult_Neo`,
  `ConfiguredTaskAwaitable` variants). These WORK (NeoStep20 9/9).
- **`HoistNeoILValueToHeap(ILType smType, ...)`** (`ILIntepreter.Neo.cs:4729`) --
  a PURE function: copies the in-frame state-machine bytes + ref slots into a
  fresh heap `ILTypeInstance` (byte-copy + ref-copy, the same shape as
  CopyFrameToIL). Unit-probed in the sync slice. THIS IS THE HOIST PRIMITIVE --
  call it, do not reinvent it.
- **`ILAsyncContext<T>`** (`ILAsyncContext.cs:21`) -- `IValueTaskSource<T>,
  IAsyncStateMachine`, wraps `ManualResetValueTaskSourceCore<T> core` (handles
  token races). Fields `stateMachine`, `moveNextMethod` are RESERVED for this
  slice. `SetResultSync`/`SetExceptionSync` ship. `MoveNext()` body is a TAGGED
  NIE (line 45) -- this slice implements it.
- **`AwaitUnsafeOnCompleted_Neo` / `AwaitOnCompleted_Neo`**
  (`CLRRedirections.AsyncNeo.cs:427/433`) -- TAGGED NIE stubs. This slice
  implements them.
- **F-10 (the prerequisite) is RESOLVED** (`neo-clrstruct-field-of-il`) -- the
  awaiter field `<>u__1` (a CLR struct field of the SM ILTypeInstance) is now
  addressable. The hoisted SM's awaiter field is reachable.

## The suspend path (what this slice must wire) -- CIL flow of a truly-async method

A truly-async method's `MoveNext` state machine:
1. Runs until it hits an await whose `awaiter.IsCompleted == false`.
2. Calls `builder.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine)` -- the
   CIL is a `call` to the builder's `AwaitUnsafeOnCompleted<TAwaiter,TSM>(
   ref sm, ref awaiter)` (a generic method). THIS HITS THE NIE STUB.
3. `MoveNext` RETURNS (the state machine is suspended mid-method; `<>1__state`
   is set to the await state; `<>u__1` holds the awaiter).
4. When the awaited `Task` completes, the runtime invokes the registered
   continuation (the `context.MoveNext` callback), which must:
   a. Restore the hoisted SM (the `ILTypeInstance` from step 2's hoist).
   b. Get a FRESH pooled interpreter (mirror Step 19's `InvokeILMethod` pool --
     DO NOT reuse the suspended frame; it's gone).
   c. Run `MoveNext` again on the restored SM (it resumes at the await state,
     reads `<>u__1.GetResult()`, continues).
   d. On terminal state, `core.SetResult` / `core.SetException`.
5. The caller's `ValueTask<T>`/`Task<T>` getter (awaiting the ILAsyncContext)
   completes.

## Probe BEFORE designing (binding: K1 / Q-NEWOBJ / F-10 / array-child / byref-child)

The LAST TWO children's propose phases DISPROVED the LEAD orientation via HEAD
dumps (array: 2/3 "gaps" were no-ops; byref: 3/4 were no-ops). PROBE FIRST:
write a truly-async probe (`async Task<int> P() { int x = await D(); return x+1; }`
where D is a genuinely-completing-on-threadpool Task), run on HEAD, capture the
EXACT NIE + the flow. Confirm: does it hit `AwaitUnsafeOnCompleted_Neo`? What
does the SM frame look like at suspend (the `<>1__state` / `<>u__1` fields)?
Dump-gate the hoist helper's output (does `HoistNeoILValueToHeap` produce a
correct heap SM?). ONLY THEN design.

## Risk profile + STOP discipline (this is the HIGHEST-RISK child)

Step 20 is "the largest runtime step" (the sync-slice design.md says so
explicitly). The suspend machinery is frame-to-heap + ILAsyncContext +
continuation registration + cross-interpreter-thread resume. Risks:
- **Cross-frame interpreter coordination**: the resumption runs MoveNext on a
  DIFFERENT interpreter instance than the one that suspended. The hoisted SM
  must be self-contained (no dangling frame pointers). Probe whether
  `HoistNeoILValueToHeap` captures EVERYTHING the resumed MoveNext needs.
- **The pooled-interpreter resumption**: mirror Step 19's
  `DelegateAdapter.InvokeILMethod` (fresh pooled interpreter + FreeILIntepreter
  in finally). Read `neo-step19-delegate` for the pool pattern.
- **ManualResetValueTaskSourceCore token races**: the core handles them; do NOT
  roll your own synchronization.
- **Cross-thread**: `AwaitUnsafeOnCompleted` = NO context capture (the
  continuation runs on the completing thread / threadpool). `AwaitOnCompleted` =
  capture ExecutionContext (+ SynchronizationContext if present). For the FIRST
  slice, scope to `AwaitUnsafeOnCompleted` (the common `Task` await); defer
  `AwaitOnCompleted` context capture if it broadens the scope.

**STOP condition (F-10 lesson):** if the dump reveals a broad blocker that
can't be fixed with a focused change (e.g. the hoist loses information the
resumption needs; the pooled-interpreter handoff can't restore the await state;
a pre-existing NIE in the resumed MoveNext path), STOP that sub-scope and split
it into its own child. Do NOT ship a half-working suspend that silently
deadlocks or returns wrong results. A green sync smoke is not worth a silent-
wrong async result.

**Scope-narrowing is explicitly OK** (mirror the sync slice's scoping section):
if the full suspend is too broad for one clean child, propose the highest-value
scoped slice (e.g. single-await `Task<T>` via `AwaitUnsafeOnCompleted` only;
defer multi-await / `AwaitOnCompleted` context capture / `ValueTask<T>` to a
round 2). Record the deferral.

## Adversarial probes MANDATORY (Step 17 B1 lesson: green smoke != correct)

- The load-bearing truly-async probe: `await Task.Delay(1)`-class then a result
  (FAIL-on-HEAD NIE -> PASS-after). MUST actually suspend + resume on the
  threadpool (not a sync-completing Task) -- a sync Task would hit the
  IsCompleted short-circuit and never test the suspend path.
- Multi-await (`await A; await B;`) -- if in scope.
- Exception from the async method -> faulted Task / caught at the awaiter.
- async void (if reachable).
- The hoist-isolation probe: the suspended SM's locals survive across the
  threadpool resume (no stale frame reads).

## Build + test (CRITICAL)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo   # CLI, 0 errors
dotnet build TestCases/TestCases.csproj -c Debug                      # TestCases.dll
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll \
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep   # ALWAYS -f net8.0; baseline 204/204
```
- Build CLI with `Debug_Neo`; NEVER TestCases with `Debug_Neo`.
- `Debug_Neo` prints huge JIT output -- grep for the summary ("Ran N tests, X
  failded"). >10s = infinite loop -> kill (an async deadlock often looks like a
  hang -- distinguish from a genuinely-slow threadpool resume with a timeout).
- For SHARED-engine edits, confirm Legacy-neutral (plain `Debug` +
  `useRegister=true`, NeoStep20 filter). NOTE: Legacy's async support is itself
  partial -- check whether Legacy supports truly-async at all before claiming
  neutrality; if Legacy also NIEs/defers, Neo-only is fine (document it).
- Build-cache gotcha: confirm the DLL rebuilt after an edit.
- Dump-noise gotcha: temp `Console.WriteLine` INSIDE a runtime arm to dump-gate.

## Codebase gotchas (full detail in handoff section 4)

- `OpCodeR` is `[StructLayout(Explicit)]`; snapshot `preOp = op` before a
  lowering case mutates.
- The SM is an ILTypeInstance (heap, after hoist). Its fields include
  `<>1__state` (int), `<>t__builder` (CLR struct), `<>u__1` (CLR struct awaiter),
  `<>7__wrap1` (locals). F-10 made the CLR-struct fields addressable.
- CLR binding `*Neo` variants only; `CLRRedirections.AsyncNeo.cs` is Neo-only.
- Legacy `ExecuteR` is the REFERENCE -- read how Legacy's async/CrossBinding
  adapts async (if it does) for the resumption SEMANTICS. The CLR
  `AsyncTaskMethodBuilder` / `ManualResetValueTaskSourceCore` semantics are the
  reference for the suspend/resume protocol.
- Test harness is NOT xUnit: `public static` parameterless methods. An async
  method's return value is read via the Task/ValueTask result -- the test method
  itself is NOT async (it must block on the result, e.g. `.Result` or a
  spin-wait). Check how the existing NeoStep20 probes block on async results.
- Write tool corrupts ~0.5% of CJK on large payloads; author ASCII-primary.

## Likely fix sites (dump-locked; do NOT commit until probed)

- `CLRRedirections.AsyncNeo.cs:427` `AwaitUnsafeOnCompleted_Neo` -- read the
  awaiter + ref SM, hoist via `HoistNeoILValueToHeap`, build the ILAsyncContext,
  register `task.UnsafeOnCompleted(context.MoveNext)`, return (suspend).
- `ILAsyncContext.cs:43` `MoveNext()` -- the resumption: restore SM, fresh pooled
  interpreter, run MoveNext, complete core.
- Possibly `ILIntepreter.Neo.cs` -- a resumption entry that runs ExecuteNeo on
  the restored SM (mirror Step 19 `InvokeILMethod`).
- The `ValueTask<T>`/`Task<T>` getter that wraps the ILAsyncContext (if not
  already shipped by the sync slice).

## Regression risk: HIGH.

Touches the async suspend/resume path (cross-frame, cross-thread). Gate: full
`NeoStep` smoke (204/204 baseline) + NeoStep20 9/9 (the sync slice MUST stay
green) + NeoOptHard 24/24. Adversarial probes MANDATORY. STOP if a focused fix
isn't possible -- split rather than ship a broken suspend.

## Maintain this file

APPEND durable findings after propose (the dump-confirmed suspend flow, the
scoped slice chosen, locked fix sites, any STOP/split decision). Do NOT append
chatter.

## Findings -- neo-step20-async-suspend (propose, 2026-07-06)

### Dump-gate executed (HEAD `0aafdb34`, NeoStep baseline 204/204 green)

A truly-async probe (`async Task<int> NeoStep20_AsyncSuspendHelper { await
Task.Delay(10); return 43; }` + a non-async `NeoStep20_TC9_AsyncSuspendProbe`
driver) was written into `TestCases/NeoStep20Test.cs` and run on HEAD with
temp `Console.WriteLine` instrumentation inside `AwaitUnsafeOnRegistered_Neo`
(entry trace + SM-state dump), `TaskAwaiter_T_GetIsCompleted_Neo`,
`TaskAwaiter_T_GetResult_Neo`, and a probe-only `Task.Delay` redirect. ALL
instrumentation was reverted; the smoke was re-confirmed 204/204 green after
revert. The probe + driver were also removed (they are re-added as the keeper
`NeoStep20_TC9_AwaitTaskDelay` in Phase 1).

### Dump-confirmed suspend flow (the CIL the compiler emits -- verbatim)

`<NeoStep20_AsyncSuspendHelper>d__9.MoveNext` final optimized opcodes (the
suspend path is opcodes 11-19):

```
11: ldc.i4.0 r1
12: stfld.i4 r0, r1, 0x0            # <>1__state = 0 (await state)
13: stfld.ref r0, r3, 0x100000004   # <>u__1 = awaiter (F-10 field, RefOff=1)
14: move r4, r0                      # r4 = sm (== this)
15: ldflda r6, r0, 0x00000004        # &sm.<>t__builder
16: ldloca.s r7, r3                  # &awaiter LOCAL (not the <>u__1 field)
17: ldloca.s r8, r4                  # &sm
18: call AsyncTaskMethodBuilder<int>::AwaitUnsafeOnCompleted<TaskAwaiter, IAsyncStateMachineAdaptor>(ref awaiter, ref sm)
19: leave.s 39                       # return from MoveNext = SUSPENDED
```

SM fields: `<>1__state` (int @ Primitives[0]); `<>t__builder` (CLR struct, F-10,
RefOff=0); `<>u__1` (TaskAwaiter CLR struct, F-10, RefOff=1, operand
`0x100000004`). The SM is a HEAP ILTypeInstance end-to-end (sync-slice OQ2
re-confirmed). At the `call` (opcode 18): slot 0 = `&sm.<>t__builder`; slot 1
= `&awaiter_local` (a frame local, NOT `<>u__1`); slot 2 = `&sm` (this).

### STOP-grade finding: the suspend redirect is UNREACHABLE end-to-end on HEAD

A probe-side `Console.WriteLine("PROBE: AwaitUnsafeOnRegistered_Neo ENTERED")`
at the very TOP of the redirect body NEVER printed, despite the JIT correctly
emitting `call AwaitUnsafeOnCompleted` (opcode 18). The redirect is registered
against the open generic definition (same pattern as `Start<TSM>` which works
-- TC1 green), but the closed-generic call
`AwaitUnsafeOnCompleted<TaskAwaiter, IAsyncStateMachineAdaptor>` (2 generic
args) does NOT dispatch to it. The call falls to CLR reflection -> the Legacy
`IAsyncStateMachineAdaptor` boxing path (the path design §26 rejects) ->
silent no-op (MoveNext returns via `leave.s 39` without SetResult/SetException
-> `get_Task` returns defensive `Task.FromResult(0)`). When the awaited
`Task.Delay` later completes on the threadpool, the CLR-registered
continuation re-enters and throws `Neo: opcode Nop not yet implemented
(Step 6)` (the catch-all at `ILIntepreter.Neo.cs:4355`; `Nop` has NO case in
the Neo switch -- `Leave`/`Leave_S`/`Endfinally` ARE handled at :4021/:4044).

### Three stacked reachability blockers (all pre-existing, none introduced by the suspend work)

- **B1 (load-bearing): closed-generic 2+-arg method redirect does not resolve
  to the open-definition redirect.** `Start<TSM>` (1 arg) resolves;
  `AwaitUnsafeOnRegistered<TA,TSM>` (2 args) does not. Suspect:
  `TryGetRedirection` / `GetGenericMethodDefinition` handling is arity-specific.
  Potentially shared-dispatch -- dump-gate FIRST; STOP+split if broad.
- **B2: `TaskAwaiter_T_GetResult_Neo` reads `.Result` unconditionally but is
  registered for the non-generic `TaskAwaiter` too** (whose GetResult is
  void; a non-generic Task has no Result) -> `MissingMethodException`. Same
  family as the sync-slice TC2/TC5 edges. Focused fix (IsGenericType gate).
- **B3: `Nop` has no case in the `ExecuteNeo` switch** (catch-all throws).
  Normal Neo JIT strips nops; the async MoveNext surfaces one. Trivial fix
  (`case Nop: ip++; continue;`).

Secondary: `Task.Run(ilLambda)` does NOT work as a suspend source (the IL
lambda is a Step-19 DelegateAdapter, not a real CLR `Func<int>`; doesn't
round-trip through un-redirected `Task.Run` reflection). A permanent
`Task.Delay` redirect is the clean awaitable source for the green test.

### Scoped slice chosen (PHASED, with a hard STOP gate)

**Phase 1 (reachability unblockers, each dump-gated + focused):** B3 Nop case;
B2 void-GetResult guard; B1 redirect-resolution (STOP if broad -> split into
`neo-generic-redirect-resolution`); Task.Delay redirect. Phase-1 exit gate:
the `NeoStep20_TC9_AwaitTaskDelay` probe reaches `AwaitUnsafeOnRegistered_Neo`
and throws the tagged NIE (the redirect body is now entered -- a
machine-checkable scope boundary).

**Phase 2 (suspend machinery, ONLY after Phase 1's entry-trace fires):** wire
`HoistNeoILValueToHeap` into `AwaitUnsafeOnRegistered_Neo` (recover SM, read
awaiter task, build ILAsyncContext, register `task.UnsafeOnRegistered`,
return); implement `ILAsyncContext<T>.MoveNext` resumption (fresh pooled
interpreter, restore SM, ExecuteNeo, route SetResult/SetException to
`core.SetResult`/`core.SetException`, FreeILIntepreter in finally); route
`get_Task` to the context when suspended. The `NeoStep20_TC9_AwaitTaskDelay`
probe turns GREEN.

### Locked fix sites (dump-confirmed; do NOT move without re-dumping)

- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- (B3) `case
  OpCodeREnum.Nop` in the switch; (Phase 2) none (resumption reuses
  `ExecuteNeo` + the Step-19/DriveMoveNext frame-build shape).
- `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs` -- (B2)
  `TaskAwaiter_T_GetResult_Neo` IsGenericType guard; (B1) none directly (the
  fix is in the redirect-lookup, likely `AppDomain.TryGetRedirection` /
  `CLRMethod`); (Task.Delay) `Task_Delay_Neo` + Register entry; (Phase 2)
  `AwaitUnsafeOnRegistered_Neo` body + `SmContextMap` + the
  `_currentAsyncContext` sink-swap in SetResult/SetException; (Phase 2)
  `AsyncTaskMethodBuilder_T_GetTask_Neo` context-routing branch.
- `ILRuntime/Runtime/Intepreter/ILAsyncContext.cs` -- (Phase 2) `MoveNext`
  body + `MoveNextDelegate`.
- `ILRuntime/Runtime/Enviorment/AppDomain.cs` -- (B1, if the redirect-lookup
  fix lives here) `TryGetRedirection` closed-generic handling.
- `TestCases/NeoStep20Test.cs` -- the keeper `NeoStep20_TC9_AwaitTaskDelay` +
  AP1-AP3 probes.

### STOP / split decision

**STOP the Phase-2 suspend machinery until Phase-1 B1 is dump-gated as a
focused fix.** If B1 (2-generic-arg redirect resolution) is a broad shared-
dispatch change, split it into `neo-generic-redirect-resolution` and reroute
this child to wait on it. The Phase-1 B2/B3 fixes are independently shippable
(B2 closes the sync-slice TC2/TC5-family edge; B3 is a 1-liner) regardless of
the suspend outcome -- they may ship as a minimal Step-20-round-2 patch even
if the full suspend is deferred. LEAD may alternatively pivot to the AOT chain
and return to this later.

### Spec delta shape

`specs/neo-async/spec.md`: ADDED "Neo async suspend via AwaitUnsafeOnOnCompleted
(single-await Task, no context capture)" + "Neo async resumption via
ILAsyncContext.MoveNext (fresh pooled interpreter)" + "Neo async get_Task
routes to the context on the suspend path"; MODIFIED the
AwaitUnsafeOnRegistered deferral requirement (RESCIND for the single-await
Task shape; KEEP for AwaitOnRegistered); REMOVED the blanket suspend deferral
(narrowed to AwaitOnRegistered/multi-await/ValueTask/async-void).

### Adversarial-probe plan

AP1 (load-bearing `await Task.Delay; return v`), AP2 (locals-survive-resume),
AP3 (faulted task from resumed method), AP4 (pool-isolation via temp
alloc/hit counters -- the Step-19 F1 lesson), AP5 (Nop no-op correctness).
MANDATORY before ship.

## Findings -- neo-step20-async-suspend (apply, 2026-07-06) -- STOP at Phase 1

### Recovery context

A prior implementer was interrupted by a server 504 mid-flight. The on-disk
state was PARTIAL + INSTRUMENTED: build clean, NeoStep smoke 205/205, but
with ~10 stray `Console.WriteLine("PROBE ...")` dump-gating diagnostics.
Assessed the PROBE output (the frozen dump) BEFORE cleanup.

### B1 status: ONLY PROBED, NOT FIXED (root cause NOT pinned -> broad)

`CLRMethod.cs` `TryGetRedirection` had ZERO real changes -- the entire +52
was dump instrumentation (the lookup logic was unchanged: still
`def.GetGenericMethodDefinition()` + `def` fallback). The PROBE output
confirmed the SYMPTOM but not the fix site:

- `RegisterAwaiters` DID register the open-definition
  `AwaitUnsafeOnCompleted<TA,TSM>` (genArgs=2) on `RedirectMapNeo` for all
  7 builder types (Register END: AwaitUnsafeOnCompletedKeys=7). So the Neo
  map HAS the open-def redirects.
- The closed-generic `Start<TSM>` (1 arg) resolved on the NEO map (HIT).
- The closed-generic `AwaitUnsafeOnCompleted<TA,TSM>` (2 args) was looked
  up ONLY on the LEGACY map (mapCount=47, where it was never registered) ->
  MISS, and its `RedirectionNeo` was NEVER accessed during execution (no
  `[NEO]` lookup, no `InvokeNeoClrMethod` probe).

The exact dispatch edge (WHY the 2-arg closed-generic call never reaches
`InvokeNeoClrMethod`/`RedirectionNeo` during execution while the 1-arg
`Start` does) was NOT pinned. The LEGACY lookups are JIT-time (method
resolution), and during execution the `call AwaitUnsafeOnCompleted` opcode
is never reached at all (see the stacked control-flow bug below). Verdict:
**B1 is NOT a confirmed focused fix -- the root cause is entangled with a
second bug, and the fix site is unresolved.** Treated as BROAD -> escalated
+ deferred (per the STOP gate).

### Stacked control-flow bug (discovered this apply -- masks B1)

Running the TC9 probe with PROBE lines in revealed the suspend path is
blocked by a SECOND issue INDEPENDENT of B1:

- JIT emits `call AwaitUnsafeOnCompleted[...]` as a normal `Call` opcode
  (confirmed in the JIT dump, e.g. `14:call -, r6, r7, r8,
  AsyncTaskMethodBuilder<int>::AwaitUnsafeOnCompleted[...]`).
- `TaskAwaiter_T_GetIsCompleted_Neo` ran and wrote `isCompleted=False`
  (Task.Delay(10) not yet complete) -> suspend path SHOULD trigger.
- But the `call AwaitUnsafeOnCompleted` opcode was NEVER executed: no
  `Call-handler` probe and no `AwaitUnsafeOnCompleted_Neo ENTERED` line.
  Execution went straight IsCompleted=False -> GetResult -> SetResult ->
  get_Task (the COMPLETION path).

So the `brtrue.s` after `get_IsCompleted` is taking the completion branch
even though IsCompleted is false -- a register/dest mismatch in the
IsCompleted redirect wiring (the redirect writes `*(int*)retDst` at
DstOffset; `brtrue` reads SrcOffset; if those diverge, brtrue reads a
stale/nonzero register and branches). The probe then blocks ~10ms inside
`TaskAwaiter.GetResult()` on the incomplete real `Task.Delay(10)`, the
completion path sets Result=43, and `get_Task` returns a completed task.
**The probe is green for the WRONG reason -- a blocking GetResult, not a
genuine suspend+resume.** This is the classic F-10/K1 silent-wrong-result.

This control-flow bug + B1 are TWO stacked blockers; neither is pinned to a
focused fix. Phase 2 (suspend machinery) cannot be made genuinely green
until BOTH close.

### Exit-gate result: NOT MET

The Phase-1 exit gate ("TC9 reaches `AwaitUnsafeOnCompleted_Neo` and throws
the tagged NIE") was NOT reached: the redirect body was never entered
(ENTERED never printed), because the suspend opcode is unreachable
(control-flow bug) and the redirect is unresolved (B1). The +1 green probe
was a FALSE POSITIVE (blocking GetResult), not a genuine suspend.

### Phase-2 status: DEFERRED / STOPPED (not started)

`AwaitUnsafeOnCompleted_Neo` body is the tagged NIE (no hoist wiring, no
`ILAsyncContext<T>` registration, no `MoveNext` resumption, no `get_Task`
routing). `ILAsyncContext<T>.MoveNext` is still the skeleton NIE. Stopped
per the proposal's STOP gate: B1 is not a confirmed focused fix and a
second stacked control-flow bug exists -- proceeding to Phase 2 would ship
compile-proven-only machinery with NO genuine end-to-end green test.

### Shipped (Phase-1 items, each focused + correct + Neo-only)

- **B3 Nop case** -- `ILIntepreter.Neo.cs`: `case OpCodeREnum.Nop: ip++;
  continue;` (a true no-op; the async SM exception-region structure can
  surface one). Independently correct (AP5: 204/204 baseline unchanged).
- **B2 void-GetResult guard** -- `CLRRedirections.AsyncNeo.cs`
  `TaskAwaiter_T_GetResult_Neo`: gate the `Result` read on
  `awaiterClr.IsGenericType`; the non-generic `TaskAwaiter.GetResult()` is
  void (a `Task.Delay` DelayPromise has no Result -> was
  MissingMethodException). Exercised + correct in isolation.
- **Task.Delay(int) redirect** -- `CLRRedirections.AsyncNeo.cs`
  `Task_Delay_Neo` + `Register()` entry: returns the real `Task.Delay(ms)`
  (the awaitable source for the eventual Phase-2 green test). Trivially
  correct (behaviorally identical to the reflection fallback).

All three are in `#if ENABLE_NEO_MODE` files (`ILIntepreter.Neo.cs`,
`CLRRedirections.AsyncNeo.cs`) -> Legacy byte-identical by construction.

### Removed / reverted (not shipped)

- **`CLRMethod.cs` B1 instrumentation** -- reverted to HEAD (no fix applied;
  B1 escalated as broad/entangled).
- **`NeoStep20Test.cs` TC9 probe + helper** -- removed. It was green via
  blocking GetResult (false positive); keeping it would assert suspend
  works when it does not. Re-add when Phase 2 lands.
- **ALL ~10 stray `Console.WriteLine("PROBE ...")` diagnostics** --
  removed from `CLRMethod.cs` (reverted), `CLRRedirections.AsyncNeo.cs`
  (AwaitUnsafeOnCompleted_Neo, GetIsCompleted, Task_Delay_Neo, Register
  START/END, RegisterAwaiters), and `ILIntepreter.Neo.cs`
  (InvokeNeoClrMethod, Call_Redirect-handler, Call-handler). `grep -r
  PROBE ILRuntime/` = 0 hits; `grep Console.WriteLine
  CLRRedirections.AsyncNeo.cs` = 0 hits.

### Final verification (post-cleanup)

- `dotnet build ILRuntimeTestCLI -c Debug_Neo` = 0 errors.
- `dotnet build TestCases -c Debug` = 0 errors.
- NeoStep smoke (`Debug_Neo`, useRegister=true, filter `NeoStep`): **Ran 204
  tests, 0 failded** (the pre-TC9 baseline restored; TC9 removed).
- NeoStep20 sync slice: **9/9 green** (TC1/TC7 regression guards intact).
- 0 PROBE lines in the run output.
- Run completes in seconds (no async deadlock/hang).

### Lines changed (final, uncommitted)

- `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs` (+38): B2
  guard in `TaskAwaiter_T_GetResult_Neo`; `Task_Delay_Neo` + Register
  entry; deferred-NIE comment on `AwaitUnsafeOnCompleted_Neo`.
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (+7): B3
  `case OpCodeREnum.Nop`.
- (`CLRMethod.cs` and `NeoStep20Test.cs` reverted to HEAD.)

### Escalation / next steps (for the split children)

1. **`neo-async-controlflow-iscompleted` (NEW, highest value):** pin why
   `brtrue.s` after `TaskAwaiter_T_GetIsCompleted_Neo` takes the completion
   branch when IsCompleted=false. Suspect a DstOffset-vs-SrcOffset register
   mismatch in the IsCompleted redirect dest write vs the brtrue source
   read. Dump-gate the actual opcode offsets. This is INDEPENDENT of B1 and
   is the earlier blocker (the suspend opcode is never reached at all).
2. **`neo-generic-redirect-resolution` (B1, split):** pin why the 2-arg
   closed-generic `AwaitUnsafeOnCompleted` call never dispatches to
   `RedirectionNeo` during execution while 1-arg `Start` does. Re-dump-gate
   AFTER the control-flow bug closes (it currently masks B1).
3. **Phase 2 (this child, resumed):** once 1+2 close and TC9 reaches the
   NIE, implement the suspend machinery per design D2/D3/D4 + AP1-AP4.

