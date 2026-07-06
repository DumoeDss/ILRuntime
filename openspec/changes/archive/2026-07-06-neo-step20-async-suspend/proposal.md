## Why

The Neo sync-async slice (`neo-step20-async`, archived) shipped the builder
redirections, the `Start`->`MoveNext` fresh-pooled-interpreter routing, the
awaiter/Task accessor overrides, the `SmTaskMap` stash, the
`HoistNeoILValueToHeap` primitive, and the `ILAsyncContext<T>` skeleton. It
left the **truly-async suspend/resume path** as a tagged
`NotImplementedException` in `AwaitUnsafeOnCompleted_Neo` /
`AwaitOnCompleted_Neo` (`CLRRedirections.AsyncNeo.cs`) and in
`ILAsyncContext<T>.MoveNext`. F-10 (the prerequisite -- the awaiter field
`<>u__1` is a CLR-struct field of the IL state machine) is now resolved. This
change wires the suspend path.

This is the HIGHEST-RISK child in the Neo completion portfolio: cross-frame
frame-to-heap hoist + `ILAsyncContext<T>` continuation registration +
cross-interpreter-thread resumption (the awaited task completes on a
threadpool thread; the continuation must restore the hoisted state machine to
a FRESH pooled interpreter and re-run `MoveNext`).

## Dump-gate (PROBE-FIRST, per the earned discipline)

A truly-async probe was written into `TestCases/NeoStep20Test.cs`
(`async Task<int> NeoStep20_AsyncSuspendHelper { await ...; return 43; }`
driven by a non-async `NeoStep20_TC9_AsyncSuspendProbe`), instrumented with
temp `Console.WriteLine` inside `AwaitUnsafeOnCompleted_Neo`,
`TaskAwaiter_T_GetIsCompleted_Neo`, `TaskAwaiter_T_GetResult_Neo`, and a
probe-only `Task.Delay` redirect, then run on HEAD `0aafdb34`
(NeoStep baseline 204/204 green). All instrumentation was reverted before this
proposal; the smoke was re-confirmed 204/204 green after revert.

### Dump-confirmed suspend flow (the CIL the compiler emits)

The state machine `<NeoStep20_AsyncSuspendHelper>d__9.MoveNext` JITs to (final
optimized opcodes, verbatim):

```
 0: initobj r3, TaskAwaiter
 1: ldfld.i4 r1, r0, 0x0  (<>1__state)
 2: brfalse.s r1, 4
 3: br.s 5
 4: br.s 20              # resume-entry jump table (state-driven)
 5: ldc.i4.s r6,10
 6: call Task.Delay(Int32)                 # the awaitable
 7: callvirt.clr Task.GetAwaiter()         # -> TaskAwaiter
 8: ldloca.s r6, r3
 9: call TaskAwaiter.get_IsCompleted()
10: brtrue.s r6, 25       # IsCompleted short-circuit -> completion path
    # --- SUSPEND path (IsCompleted == false) ---
11: ldc.i4.0 r1
12: stfld.i4 r0, r1, 0x0  (<>1__state = 0)            # stamp the await state
13: stfld.ref r0, r3, 0x100000004 (<>u__1 = awaiter)  # store awaiter (F-10 field)
14: move r4, r0                                          # r4 = sm (== this)
15: ldflda r6, r0, 0x00000004                            # &sm.<>t__builder
16: ldloca.s r7, r3                                      # &awaiter local
17: ldloca.s r8, r4                                      # &sm
18: call AsyncTaskMethodBuilder<int>::AwaitUnsafeOnCompleted<TaskAwaiter, IAsyncStateMachineAdaptor>(ref awaiter, ref sm)
19: leave.s 39     # return from MoveNext = SUSPENDED
    # --- COMPLETION path (IsCompleted == true, or RESUMED) ---
20: ldfld.ref r3, r0, 0x100000004 (<>u__1)              # reload awaiter
21: ldflda r6, r0, 0x100000004
22: initobj r6, TaskAwaiter                              # clear <>u__1
23: ldc.i4.m1 r1
24: stfld.i4 r0, r1, 0x0 (<>1__state = -1)
25: ldloca.s r6, r3
26: call TaskAwaiter.GetResult()                         # yield the value
27: ldc.i4.s r2,43
28: leave.s 35
    # --- CATCH (SetException) ---
29: move r5, r6
30: ldc.i4.s r7,-2
31: stfld.i4 r0, r7, 0x0 (<>1__state = -2)
32: ldflda r6, r0, 0x00000004
33: call AsyncTaskMethodBuilder<int>::SetException(ex)
34: leave.s 39
    # --- FINALLY / SetResult ---
35: ldc.i4.s r7,-2
36: stfld.i4 r0, r7, 0x0 (<>1__state = -2)
37: ldflda r6, r0, 0x00000004
38: call AsyncTaskMethodBuilder<int>::SetResult(43)
39: ret
```

This confirms the planning-context's suspend flow precisely:
- `<>1__state` is a primitive int at `Primitives[0]`.
- `<>t__builder` is a CLR-struct field (F-10 shape) at PrimOff=4, RefOff=0.
- `<>u__1` (the awaiter) is a CLR-struct field (F-10 shape) at PrimOff=4,
  RefOff=1, operand `0x100000004`.
- At the `call AwaitUnsafeOnCompleted` (opcode 18), the three byref params are:
  slot 0 = `&sm.<>t__builder` (ldflda), slot 1 = `&awaiter_local` (ldloca the
  TaskAwaiter local r3), slot 2 = `&sm` (ldloca r4, where r4 = `this` = the SM
  heap ILTypeInstance). The awaiter byref points to a FRAME LOCAL (not a field);
  the sm byref points to `this`.

### STOP-grade finding: the suspend redirect is UNREACHABLE end-to-end on HEAD

Despite the JIT correctly emitting `call AwaitUnsafeOnCompleted` (opcode 18),
a probe-side `Console.WriteLine("PROBE: AwaitUnsafeOnCompleted_Neo ENTERED")`
at the very TOP of the redirect body NEVER printed. The redirect is registered
against the open generic definition (the same pattern that works for
`Start<TSM>`), but the closed-generic call
`AwaitUnsafeOnCompleted<TaskAwaiter, IAsyncStateMachineAdaptor>` (a 2-generic-
arg method) does NOT dispatch to it. The call falls through to the CLR
reflection fallback, which takes the Legacy `IAsyncStateMachineAdaptor` boxing
path (the exact path `object-model-neo-design.md` §26 rejects), no-ops
silently (MoveNext returns via `leave.s 39` without `SetResult`/`SetException`
-> `get_Task` returns the defensive `Task.FromResult(0)`), and when the
awaited `Task.Delay` later completes on the threadpool, the CLR-registered
continuation re-enters and throws `Neo: opcode Nop not yet implemented
(Step 6)` (the catch-all at `ILIntepreter.Neo.cs:4355`; `Nop` has NO case in
the Neo switch -- confirmed: `grep "case OpCodeREnum.Nop"` = 0 hits; `Leave`/
`Leave_S`/`Endfinally` ARE handled at :4021/:4044).

### Three stacked reachability blockers (all pre-existing, none introduced by the suspend work)

Dump-gate isolated THREE distinct edges that must close BEFORE the suspend
machinery can be exercised end-to-end:

1. **B1 (load-bearing): the closed-generic 2+-arg method redirect does not
   resolve to the open-definition redirect.** `Start<TSM>` (1 generic arg)
   resolves and its redirect fires (TC1 green); `AwaitUnsafeOnCompleted<TA,TSM>`
   (2 generic args) does NOT -- the entry trace never prints. The call falls
   to CLR reflection. Suspect: the `TryGetRedirection` / redirect-lookup path
   (`AppDomain` / `CLRMethod` `GetGenericMethodDefinition` handling) handles
   the 1-arg case but not the 2+-arg closed-generic -> open-definition match.
   **Severity: this is the load-bearing reachability blocker.** It is
   potentially shared-dispatch (could affect any 2+-arg generic CLR redirect)
   -- MUST be dump-gated; STOP+split if the fix is broad.

2. **B2: `TaskAwaiter_T_GetResult_Neo` is registered for BOTH generic
   `TaskAwaiter<T>` AND non-generic `TaskAwaiter` (via `RegisterAwaiterAccessors`
   on `typeof(TaskAwaiter)`), but it unconditionally reads `.Result`.** A
   non-generic `Task` (e.g. `Task+DelayPromise` from `Task.Delay`) has NO
   `Result` property -> `MissingMethodException: Method
   'System.Threading.Tasks.Task+DelayPromise.Result' not found`. This is the
   same family as the sync slice's TC2/TC5 redirect-coverage edges. The fix is
   focused: gate the `Result` read on `awaitType.IsGenericType` (the
   non-generic `TaskAwaiter.GetResult()` is `void`).

3. **B3: `Nop` has no case in the `ExecuteNeo` switch** (catch-all at
   `ILIntepreter.Neo.cs:4355` throws `opcode Nop not yet implemented`). Normal
   Neo JIT strips nops, but the async state machine's exception-region
   structure surfaces one. The fix is trivially focused: add
   `case OpCodeREnum.Nop: ip++; continue;` (a true no-op). Out-of-scope is
   whether OTHER opcodes in the async MoveNext's catch/SetException/
   finally/SetResult structure are unhandled -- dump-gate confirms `leave.s`,
   `Endfinally`, `initobj`, `ldc.i4.s`, `ldc.i4.m1`, `stfld.ref`, `ldflda`,
   `ldloca.s`, `br.s`, `brfalse.s`, `brtrue.s` are ALL already handled.

### Awaitable-source reachability (secondary, probe-only finding)

`Task.Run(() => 42)` does NOT work as a suspend source: the IL lambda becomes
a Step-19 `DelegateAdapter` (an `IDelegateAdapter`, not a real CLR
`Func<int>`), which does not round-trip through the un-redirected `Task.Run`
reflection fallback. `Task.Delay(10)` works only with a probe-only redirect.
A permanent awaitable source for suspend tests needs EITHER a `Task.Delay`
redirect (small, permanent test-infra) OR the IL-delegate-through-CLR-method
round-trip (a separate, broader fix -- the F-7/NeoInvokeSub delegate family).
For the slice's own green test, a `Task.Delay` redirect is the clean choice.

## What Changes (scoped slice + STOP/split discipline)

### Primary recommendation: PHASED within this change, with a hard STOP gate

**Phase 1 -- suspend-reachability unblockers (each dump-gated, focused):**
- P1.1 `Nop` case in `ExecuteNeo` (1 line; `#if ENABLE_NEO_MODE`).
- P1.2 `TaskAwaiter_T_GetResult_Neo` void-GetResult guard for the non-generic
  `TaskAwaiter` (the B2 fix; also closes the sync slice's TC2/TC5 edge for the
  `await Task.Delay` shape -- fold them in).
- P1.3 **The B1 redirect-resolution fix** (the load-bearing one). Dump-gate
  the `TryGetRedirection` path for a 2-generic-arg closed method. **STOP
  condition:** if the fix touches shared dispatch broadly (not a focused
  generic-method-definition lookup fix), STOP and split B1 into its own child
  (`neo-generic-redirect-resolution`); the suspend machinery cannot proceed
  without it.
- P1.4 A permanent `Task.Delay(int)` Neo redirect (test-infra; the awaitable
  source for the suspend green test). Registers in `Register()`.

Phase 1 delivers a **reachable, dump-confirmed NIE**: a `NeoStep20_TC9_AwaitTaskDelay`
probe that reaches `AwaitUnsafeOnCompleted_Neo` and throws the tagged NIE
(FAIL-on-HEAD before Phase 1; reachable-NIE after).

**Phase 2 -- the suspend machinery (ONLY after Phase 1 produces a reachable NIE):**
- P2.1 Wire `HoistNeoILValueToHeap` into `AwaitUnsafeOnCompleted_Neo`: read the
  awaiter byref (slot 1) + the sm byref (slot 2 -> the SM heap ILTypeInstance),
  hoist the SM's current state into the existing heap instance (the SM is
  ALREADY a heap `ILTypeInstance` per the sync-slice OQ2 dump -- "hoist" here =
  ensure the in-flight MoveNext's mutations are on the heap instance, which
  they already are; the helper is available if a frame->heap copy is needed
  for any byval ephemeral), build an `ILAsyncContext<T>` holding the SM + the
  cached `MoveNext` ILMethod, and register the continuation
  `task.UnsafeOnCompleted(context.MoveNext)` where `task` is read from the
  awaiter (the awaiter's `m_task` field via `GetAwaiterTask`). Return without
  calling `SetResult` (the SM is suspended).
- P2.2 Implement `ILAsyncContext<T>.MoveNext()` (the `IAsyncStateMachine.MoveNext`
  resumption): obtain a FRESH pooled interpreter (`RequestILIntepreter`,
  Step-19 `NeoInvokeSub` / sync-slice `DriveMoveNext` shape), build a Neo
  frame, write the SM heap instance as slot-0 `this`, `ExecuteNeo` the
  `MoveNext` ILMethod (it resumes at the await state via `<>1__state`,
  reloads `<>u__1`, calls `GetResult`, continues), on terminal state call
  `core.SetResult` / `core.SetException`, `FreeILIntepreter` in `finally`.
  Guard reentrancy + token races via the existing
  `ManualResetValueTaskSourceCore<T>`.
- P2.3 Route the `get_Task` getter to `new ValueTask<T>(context, token)` (or a
  `Task<T>` wrapper) when the SM suspended (the `core` owns the result).
- P2.4 The `NeoStep20_TC9_AwaitTaskDelay` probe turns GREEN (suspend + resume +
  result == 43).

### Scope deferrals (recorded, NOT shipped in this child)

- **`AwaitOnCompleted` (context capture)**: ExecutionContext + (if present)
  SynchronizationContext capture. Deferred -- `AwaitUnsafeOnCompleted` (no
  context capture) is the common `Task` await and ships here. Tagged NIE
  remains for `AwaitOnCompleted_Neo`.
- **Multi-await suspend** (`await A; await B;` with both incomplete): the
  multi-suspend/resume cycle. Deferred to a round 2 (the single-await machinery
  is the load-bearing primitive; multi-await reuses it). The sync-slice TC5
  probe (multi-await all-complete) stays green via the IsCompleted short-circuit.
- **`ValueTask<T>` suspend path**: the `AsyncValueTaskMethodBuilder<T>`
  suspend. Deferred (the `Task<T>` path is the primitive). The sync-slice
  `ValueTask` fast-path stays.
- **`async void` suspend**: deferred (`AsyncVoidMethodBuilder` has no `Task`;
  the continuation completes a side-effect). The sync-slice TC4 stays green.
- **Exception from a resumed async method -> faulted task**: in-scope for the
  single-await machinery (P2.2's `core.SetException` path) but the adversarial
  probe is mandatory, not the primary green target.
- **The IL-delegate-through-CLR-method round-trip** (so `Task.Run(ilLambda)`
  works as a suspend source): deferred (the F-7 family; the green test uses
  `Task.Delay` instead).

### Alternative considered: ship the suspend machinery untested (REJECTED)

Shipping the hoist wiring + `ILAsyncContext<T>.MoveNext` WITHOUT the Phase-1
reachability unblockers would mean the machinery is compile-proven only --
NO end-to-end green test exercises it. This violates the F-10 / OPT-HARDEN K1
lesson (untested async machinery = the silent-wrong-result / deadlock class
the discipline forbids). The sync slice shipped the hoist + skeleton as
standalone PROVEN primitives specifically to avoid this; the wiring MUST have a
green end-to-end test. Phase 1 first is non-negotiable.

## Adversarial-probe plan (MANDATORY -- Step 17 B1 / F-10 lesson)

- **AP1 (the load-bearing truly-async probe):** `async Task<int> P() { await
  Task.Delay(10); return 43; }` driven by a non-async method that blocks on
  the result. FAIL-on-HEAD (NIE, then the B1/B2/B3 stacked edges) ->
  PASS-after (suspend + threadpool resume + result == 43).
- **AP2 (hoist-isolation / locals survive across threadpool resume):** an
  async method with a LOCAL before and after the await (`int x = 7; await
  Task.Delay(1); return x + 36;`) -- asserts the resumed MoveNext reads the
  pre-await local (no stale frame).
- **AP3 (exception from the resumed method -> faulted task):** `async Task<int>
  P() { await Task.Delay(1); throw new Exception("x"); }` -- asserts the
  returned Task is Faulted with the exception (the `core.SetException` path).
- **AP4 (cross-thread / pool interpreter isolation):** confirm via pool
  instrumentation (mirroring the Step-19 F1 verification) that the resumed
  MoveNext runs on a FRESH pooled interpreter and `FreeILIntepreter` fires on
  every resume (no pool leak on the async hot path). Instrumentation removed
  before ship.
- **AP5 (Nop no-op correctness):** confirm adding the `Nop` case does not
  perturb the 204/204 baseline (a `Nop` between two side-effecting ops must
  not advance state).

## Regression risk: HIGH

Touches the async suspend/resume path (cross-frame, cross-thread, pool). Gate:
full `NeoStep` smoke (204/204 baseline) + `NeoStep20` 9/9 (the sync slice MUST
stay green) + `NeoOptHardening` 24/24. Legacy untouched (all edits
`#if ENABLE_NEO_MODE` or Neo-only files). Shared-dispatch risk is ISOLATED to
the B1 redirect-resolution fix (the only edit that could touch shared dispatch)
-- dump-gate it first; STOP+split if broad.

## STOP decision

**STOP the Phase-2 suspend machinery until Phase-1 B1 is dump-gated as a
focused fix.** If B1 (the 2-generic-arg redirect resolution) turns out to be a
broad shared-dispatch change, split it into `neo-generic-redirect-resolution`
and reroute this child to wait on it. The LEAD may alternatively pivot to the
AOT chain and return to this later. The Phase-1 B2/B3 fixes (void-GetResult,
Nop case) are independently shippable and close sync-slice TC2/TC5-family
edges regardless of the suspend outcome.
