## Context

Neo async/await support has shipped its synchronous-completing slice (every await
observes `IsCompleted == true`) plus Phase-1 reachability unblockers (the `Nop`
dispatch, the non-generic-awaiter `void-GetResult` guard, the `Task.Delay`
redirect) and the foundation primitives (`HoistNeoILValueToHeap`,
`ILAsyncContext<T>` `IValueTaskSource<T>` surface, the builder redirects). A
prior child (`neo-generic-redirect-resolution`, B1) EXONERATED the
`AwaitUnsafeOnCompleted<TA,TSM>` redirect resolution (the redirect IS registered
on `RedirectMapNeo`; `TryGetRedirection` is arity-agnostic and correct) and
isolated the real blocker to a MoveNext control-flow bug: after `get_IsCompleted`
returns `false`, the state machine hangs before reaching `AwaitUnsafeOnCompleted`
(and before `GetResult`). This change's propose phase ran an instruction-level
trace and pinned the bug to an exact opcode. The trace is the arbiter; this
section records it verbatim because every fix decision flows from it.

### The instruction-level trace FINDING (the dump is the arbiter)

A gated instruction-level tracer was run on the async state machine's `MoveNext`
body. The reproducer is `NeoStep20_TC8_IncompleteAwaitHitsTaggedNIE` (un-ignored
locally; all temp diagnostics reverted before ship; engine byte-identical to
HEAD at propose time). The C# compiler lowers `<NeoStep20_IncompleteAwaitProbe>d__5.MoveNext()`
into a 49-opcode body. The relevant slice (from the JIT dump):

```
 9: Ldloca_S  dst=28              -- r28 = &awaiter (managed pointer, 8 bytes)
10: Call      dst=28  Boolean get_IsCompleted()    -- redirect writes *(int*)retDst (4 bytes)
11: Brtrue_S  dst=28  op=26 op2=8                  -- reads *(long*)(frameBase+28) (8-BYTE read)
12..19: SUSPEND block  (state=0; store awaiter <>u__1; Ldflda builder; Call AwaitUnsafeOnCompleted @19)
26..27: SYNC-COMPLETION block (Ldloca_S awaiter; Call GetResult @27)
```

**ON HEAD (the hang).** Index 10 `get_IsCompleted` writes a 4-byte int (`*(int*)retDst = 0`,
the bool result) into register slot r28. Index 11 `Brtrue_S` reads an **8-byte
long** (`op2=8`) from r28. The 8-byte read width is set by the optimizer at
`ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs:697-708`, which sizes
`Brtrue`/`Brfalse` by the **physical slot width** (`localInfos[r1].Size`), NOT by
the producer value's width. Slot r28 is 8 bytes because it is reused for the
`&awaiter` managed pointer at index 9 (`Ldloca_S dst=28`). The high 4 bytes of
r28 still hold stale non-zero pointer bits from index 9, so `*(long*)r28 != 0`
even though `IsCompleted == false` -> `Brtrue` **TAKES THE BRANCH to index 26**
(the sync-completion GetResult block) instead of falling through to index 12 (the
suspend block). Index 27 `Call GetResult` -> `TaskAwaiter_T_GetResult_Neo` reads
`task.Result` on the never-completed `TaskCompletionSource`-backed Task ->
`Task.Result` **BLOCKS FOREVER** = the hang.

**FIX CONFIRMED BY PROBE.** Zero-extending the `get_IsCompleted` bool result to
8 bytes makes `Brtrue` read a clean 0 -> falls through to index 12 -> executes
the suspend block -> **REACHES the `AwaitUnsafeOnCompleted` Call at index 19**
(previously NEVER reached). TC8 no longer hangs (the run exits 0 instead of
timing out). The probe was reverted; the engine is byte-identical to HEAD.

### Root-cause diagnosis (one line)

Branch read-width vs producer write-width mismatch: `Brtrue`/`Brfalse` read the
full 8-byte register slot (the optimizer sizes `Operand2 = localInfos[r1].Size`),
but the 4-byte-producing `get_IsCompleted` redirect leaves stale high bits in a
pointer-reused slot, so the long-read branch misroutes when `IsCompleted == false`.

### Suspects CONFIRMED / REFUTED by the trace

- **The `brtrue` target after `IsCompleted`:** REFUTED. The target (index 26) IS
  the correct sync-completion block; the bug is that the branch is TAKEN when it
  should not be (`IsCompleted == false`), not that the target is wrong.
- **The `AwaitUnsafeOnCompleted` Call dispatch:** REFUTED (for the hang). The Call
  at index 19 resolves and would dispatch (B1 exonerated the redirect); it is
  simply never REACHED because the branch at 11 skips over the suspend block
  (12-19).
- **The `<>1__state` transitions:** REFUTED. State is read correctly at index 1
  (the trace shows `state == -1` -> `Brfalse` not taken -> `Br_S` to 5, the
  initial path).
- **The `<>u__1` awaiter field store/load:** REFUTED (for the hang). The awaiter
  field is stored at index 14, INSIDE the suspend block, which is not reached on
  HEAD.
- **The REAL cause** is a NEW finding not in the original suspect list: the
  branch/producer width mismatch.

This is consistent with B1's empirical trace (`B1_CALLPRE` never fired for
`AwaitUnsafeOnCompleted`; the state machine reached neither the suspend Call nor a
returning `GetResult`): the branch misroutes execution INTO `GetResult`, which
then blocks inside `Task.Result`, so no further MoveNext opcodes (and no
returning redirect) are observed.

## Goals / Non-Goals

**Goals:**
- **Piece 1 (the hang fix, CONFIRMED):** make a 4-byte CLR-redirect return that
  feeds a `Brtrue`/`Brfalse` zero-extended to its destination slot width, so the
  state machine falls through to the suspend block and REACHES
  `AwaitUnsafeOnCompleted` after `IsCompleted == false`.
- **Piece 2 (suspend machinery):** give `AwaitUnsafeOnCompleted_Neo` (and
  `AwaitOnCompleted_Neo`) a real body that registers an `ILAsyncContext<T>`
  continuation with the awaiter's task and returns without `SetResult`.
- **Piece 3 (resume machinery):** give `ILAsyncContext<T>.MoveNext()` a real body
  that restores the state machine to a fresh pooled interpreter, resumes at the
  await state, runs to `SetResult`/`SetException`, and routes the result to
  `ctx.core`; route `get_Task` to the context when the SM suspended.
- **Deterministic gate:** un-ignore `NeoStep20_TC8` and redesign it to the
  deterministic `TaskCompletionSource` completion signal; the deterministic probe
  (NOT a green smoke) is the binding success criterion.
- Legacy byte-identical (every edit `#if ENABLE_NEO_MODE` or in Neo-only files).

**Non-Goals (deferred):**
- `AwaitOnCompleted` `ExecutionContext`/`SynchronizationContext` capture (the
  `AwaitOnCompleted_Neo` body mirrors `AwaitUnsafeOnCompleted_Neo` WITHOUT the
  execution-context capture; capture is a later concern).
- Multi-await suspend/resume where TWO awaits are both incomplete in one SM.
- `ValueTask<T>` suspend path and `async void` suspend path.
- IL-delegate-through-CLR-method round-trip (`Task.Run(ilLambda)`).
- A real zero-alloc `ValueTask<T>` (the suspend path may allocate a
  `Task<T>`/`TaskCompletionSource<T>` bridge initially).

## Decisions

### D1: Piece 1 fix surface (three options; recommendation, not a mandate)

The trace proves the bug is a producer/consumer width mismatch on a shared
register slot. Three fix surfaces, each dump-gated by the implementer:

- **Option A (minimal, async-specific, CONFIRMED):** in
  `TaskAwaiter_T_GetIsCompleted_Neo`, zero-extend the bool result to 8 bytes
  (`*(long*)retDst = isCompleted ? 1 : 0`). Dump-confirmed safe for the async SM
  (slot r28 is 8 bytes: offsets 28..35, the next register begins at 36). This is
  the smallest change that un-hangs TC8 and reaches `AwaitUnsafeOnCompleted`.
- **Option B (generic, preferred hardening):** in the Neo `Call`/CLR-redirect
  return path, zero-extend a narrower redirect return to the destination slot's
  allocated width (thread the slot width from the optimizer to the runtime Call
  case). Closes the LATENT general bug for EVERY bool/int CLR redirect
  (`GetResult`, `Task<T>.get_Result`, etc.) that feeds a `Brtrue`/`Brfalse` or
  whose slot is reused. Neo-only, `#if ENABLE_NEO_MODE`.
- **Option C (optimizer root fix):** size `Brtrue`/`Brfalse` by the producer
  value type (4 for bool) instead of the slot width. Most semantically correct;
  largest blast radius (shared optimizer dataflow).

**Recommendation:** Option A as the confirmed-minimal gate (this child delivers
it, proven by the probe). Option B as the preferred generic hardening WITHIN this
child if the implementer deems the slot-width threading low-risk (it is the
morally correct fix and prevents the next bool-redirect silent-wrong-result).
Option C is documented as a longer-term alternative; do NOT force it in this
child (the blast radius is the shared optimizer, and the full `NeoStep` smoke is
the only regression gate for it).

**EH-routing sub-task (only if the staged tagged-NIE gate is used with piece 1
alone):** with only piece 1, TC8 reaches the tagged NIE but the NIE does not
cleanly fault the task (a probe showed `1 failed`): the redirect-thrown NIE must
be catchable by the SM's own try/catch -> `SetException` -> faulted task. This
EH-routing detail is MOOT once pieces 2+3 replace the NIE with a real suspend
body; it is noted here only so the implementer does not mis-read a piece-1-only
TC8 failure as a control-flow regression.

### D2: Piece 2 - `AwaitUnsafeOnCompleted_Neo` body (suspend)

The redirect receives (per the archived suspend-child dump): slot 0 (builder
`this` byref), slot 1 (`ref awaiter` byref, a frame-local TaskAwaiter copy), slot
2 (`ref sm` byref, `&this`). The body:

1. Recover the SM heap `ILTypeInstance` (`CurrentAsyncSm`, the same ThreadStatic
   the sync slice uses; it is set by `DriveMoveNext`).
2. Read the awaiter's task (slot 1 -> `ReadNeoValueType` -> `GetAwaiterTask`).
   If the task is null or already completed, fall back to the sync path
   (defensive against a race where the task completes between the `IsCompleted`
   check and this call).
3. Build the context: `var ctx = new ILAsyncContext<T> { stateMachine = sm,
   moveNextMethod = GetMoveNext(sm.Type) };` (reuse the sync slice's
   `GetMoveNext` cache).
4. Register the continuation: `task.UnsafeOnCompleted(ctx.MoveNextDelegate)`
   where `MoveNextDelegate` is an `Action` wrapping
   `IAsyncStateMachine.MoveNext` (no `ExecutionContext` capture, matching
   `AwaitUnsafeOnCompleted` semantics).
5. Park `ctx` on `SmContextMap[sm]` (a ThreadStatic `Dictionary<ILTypeInstance,
   ILAsyncContext<T>>`, parallel to `SmTaskMap`). Return WITHOUT `SetResult`.

The SM is already a heap `ILTypeInstance` (dump-confirmed by the prior child), so
`HoistNeoILValueToHeap` is NOT needed for the SM itself; the awaiter (`<>u__1`)
and the state (`<>1__state`) are already on the heap instance at suspend time
(MoveNext wrote them via heap `stfld`/`stfld.ref` at indices 13-14). The helper
is retained only for the edge case where an in-frame IL value-type local is
captured by the async closure.

### D3: Piece 3 - `ILAsyncContext<T>.MoveNext()` resumption + sink-swap

The resumption fires on the thread that completed the awaited task. There is no
in-flight Neo frame. Mirror the sync slice's `DriveMoveNext` (which mirrors Step
19 `NeoInvokeSub`):

1. `ILIntepreter intp = appdomain.RequestILIntepreter();` (a FRESH pooled
   interpreter; its engine stack is empty, isolating it from any in-flight frame).
2. `try { ... } finally { appdomain.FreeILIntepreter(intp); }` (balanced pool
   lifecycle - the Step 19 F1 lesson; a missing free starves the pool and the
   green smoke misses it).
3. Build a Neo frame at `intp.Stack.StackBase`, zero the locals, reserve the ref
   region, write the SM heap instance as slot-0 `this`.
4. Set the ThreadStatic `_currentAsyncContext = ctx;` then
   `intp.ExecuteNeo(moveNextMethod, ...)` resumes at the await state (driven by
   `<>1__state`), reloads `<>u__1`, calls `GetResult`, and runs to the next
   terminal state (`SetResult`/`SetException`).
5. The `SetResult`/`SetException` redirects check `_currentAsyncContext` FIRST -
   if set, call `ctx.core.SetResult(value)` / `ctx.core.SetException(ex)` instead
   of stashing in `SmTaskMap` (the sink-swap). This reuses the sync redirects'
   result-read logic; only the sink changes.
6. On completion, `FreeILIntepreter` in `finally`. The caller reads the result
   via the context's `IValueTaskSource<T>` surface.

### D4: `get_Task` routes to the context when the SM suspended

The sync-slice `get_Task` reads `SmTaskMap[sm]`. When the SM suspended,
`SmTaskMap` has NO entry (`SetResult` did not run). The getter detects suspension
via `SmContextMap[sm]` and produces a `Task<T>` backed by the context. The
simplest correct first cut is a `TaskCompletionSource<T>` bridge: the context
completes the TCS via `core` -> `OnCompleted`, and the getter returns
`tcs.Task`. A zero-alloc custom `Task<T>` from `IValueTaskSource<T>` is a later
optimization (Non-Goal).

### D5: TC8 completion-signal design (the success criterion)

The deterministic TCS-backed Task is never `SetResult` by the test naturally, so
TC8 is redesigned to DRIVE completion deterministically:

- `TestCLRBinding` exposes BOTH the incomplete Task AND its owning
  `TaskCompletionSource<int>` (`GetIncompleteTask` already exists; add
  `CompleteIncompleteTask(int value)` that calls `SetResult` on the cached TCS).
- The test: `var t = NeoStep20_IncompleteAwaitProbe();` then assert
  `!t.IsCompleted` (the await TRULY SUSPENDED - the task is not sync-completed;
  this conjunct is the gate that proves the suspend happened and is the exact
  F-10/K1 silent-wrong-result guard). Then
  `TestCLRBinding.CompleteIncompleteTask(N);` Then a bounded spin-wait (capped
  iterations, NO real delay) for `t.IsCompleted`. Then assert `t.Result == N + 3`
  (the resume ran `GetResult` + continued + `SetResult`).
- A green smoke does NOT prove this gate; the deterministic TCS probe is binding.
  TC9 (sync control) + TC10 (IsCompleted diagnostic) remain active guards. A test
  taking more than ~10s is a stuck resume and is killed (the unittest_guide rule).

### D6: Sequencing (TRUE COMPLETION)

Piece 1 is the gate (must land first; alone it un-hangs TC8 and reaches
`AwaitUnsafeOnCompleted`). Pieces 2+3 build on it. The skeletons shipped
(`HoistNeoILValueToHeap`, `ILAsyncContext<T>` `IValueTaskSource<T>` surface, the
builder redirects) de-risk the work, but the suspend+resume BODIES are NIE stubs
- the bulk of the new implementation. The TARGET is full truly-async in this child
(all three pieces). If pieces 2+3 exceed one child, the handoff sequences ONLY the
remainder into the IMMEDIATE next child and it is DRIVEN NEXT (TRUE COMPLETION:
never parked indefinitely).

## Risks / Trade-offs

- **[Piece 1 Option A is async-specific]** -> it fixes `get_IsCompleted` but
  leaves the latent general bug for other bool/int CLR redirects. Mitigation: the
  implementer evaluates Option B (generic zero-fill) in the same child; the full
  `NeoStep` smoke is the regression gate for any shared-dispatch edit.
- **[Piece 1 Option B touches shared Neo Call dispatch]** -> the generic
  Call-return zero-fill is on the path used by ALL CLR redirects. Mitigation:
  dump-gate the slot-width threading; the change is in the Neo-only Call arm; the
  full `NeoStep` smoke (218/218) catches regressions. If broad, fall back to
  Option A.
- **[Cross-thread interpreter state on resume]** -> the resumption runs on a
  threadpool thread; `ExecuteNeo` mutates engine `esp`/`mStack.Count`. Mitigation:
  the FRESH pooled interpreter isolates this (its engine stack is empty); each
  resumption `RequestILIntepreter`s its own; `ManualResetValueTaskSourceCore`
  handles token races.
- **[Pool leak on the async hot path]** -> the Step 19 F1 lesson. Mitigation: the
  `try/finally` with `FreeILIntepreter` is non-negotiable on every resume path
  (happy, exception, nested).
- **[Infinite loop / deadlock masquerading as a hang]** -> a malformed resumption
  that does not reach a terminal state loops. Mitigation: TC8's bounded spin-wait
  with a max-iterations guard; more than ~10s = kill and investigate.
- **[The awaiter is a frame local, not the `<>u__1` field]** -> slot 1 is the
  awaiter LOCAL (`ldloca`), not `ldflda <>u__1`. D2 reads the awaiter from the
  local. The `<>u__1` field is stored separately at index 14 for the resumed
  MoveNext to reload; both must be consistent.
- **[Sink-swap ordering]** -> if `SetResult`/`SetException` check `SmTaskMap`
  before `_currentAsyncContext`, the resume result is lost. Mitigation: the
  redirects check `_currentAsyncContext` FIRST (D3 step 5); an adversarial probe
  (locals survive resume + nested async) verifies.

## Migration Plan

No migration (pure feature add for Neo; Legacy unchanged). Rollback = revert the
change directory + the source edits; `AwaitUnsafeOnCompleted_Neo` /
`AwaitOnCompleted_Neo` / `ILAsyncContext<T>.MoveNext()` return to the tagged NIE,
and the sync slice (TC1/TC4/TC6/TC7 + TC9/TC10) is unaffected (it never
suspends).

## Open Questions

- **OQ1 (resolve at apply, piece 1):** does the implementer ship Option A
  (per-redirect), Option B (generic zero-fill), or both? Decide from a dump of
  whether the generic Call-return path can cheaply read the dest slot width at
  runtime. If yes, B is preferred; if not, A is the gate and B is a follow-up.
- **OQ2 (resolve at apply, piece 3):** does the resumed MoveNext's `SetResult`/
  `SetException` route to `ctx.core` via the `_currentAsyncContext` ThreadStatic
  (D3 step 5), or does the `CurrentAsyncSm`/`SmTaskMap` path intercept first?
  Decide the sink-swap ordering at apply with an adversarial probe.
- **OQ3 (resolve at apply, piece 3):** for the `Task<T>` backed by the context,
  is a `TaskCompletionSource<T>` bridge (simplest, one alloc) or a custom
  `Task<T>` from `IValueTaskSource<T>` (zero-alloc, complex) the right first cut?
  Recommend the TCS bridge for the first green; optimize later.
