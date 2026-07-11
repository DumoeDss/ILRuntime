## Context

This change wires the truly-async suspend/resume path that the sync slice
(`neo-step20-async`, archived) deferred. The foundation is shipped:
- **Builder redirects** (`CLRRedirections.AsyncNeo.cs`): `Start<TSM>` ->
  `DriveMoveNext` (fresh pooled interpreter), sync `SetResult`/`SetException`/
  `get_Task`, awaiter/Task accessor overrides. The `AwaitUnsafeOnCompleted_Neo`
  / `AwaitOnCompleted_Neo` stubs throw the tagged NIE (the split point).
- **`HoistNeoILValueToHeap`** (`ILIntepreter.Neo.cs:4729`): a pure frame->heap
  copy primitive (inverse of `CopyFrameToIL`), `new ILTypeInstance(smType,
  initializeCLRInstance:false)` + byte-copy + ref-copy. Standalone, NOT wired.
- **`ILAsyncContext<T>`** (`ILAsyncContext.cs`): `IValueTaskSource<T>,
  IAsyncStateMachine`; the `IValueTaskSource<T>` surface delegates to
  `ManualResetValueTaskSourceCore<T>` (handles token races); `MoveNext()`
  throws the tagged NIE. Fields `stateMachine`, `moveNextMethod` reserved.
- **F-10 resolved**: the awaiter field `<>u__1` (a CLR-struct field of the SM
  ILTypeInstance) is addressable via the F-10 β-offset-discriminator.

**Dump-confirmed SM representation (this child's probe, HEAD `0aafdb34`):**
the C# compiler emits `<Method>d__N` as a HEAP `ILTypeInstance` (the driver
`newobj`'s it + `stfld.ref`; MoveNext accesses its fields via heap ldfld/stfld
-- the sync-slice OQ2). The SM fields:
- `<>1__state` -- primitive `int` at `Primitives[0]`.
- `<>t__builder` -- `AsyncTaskMethodBuilder<T>` CLR struct (F-10 shape),
  stored boxed at `ManagedObjects[ReferenceOffset=0]`.
- `<>u__1` -- `TaskAwaiter`/`TaskAwaiter<T>` CLR struct (F-10 shape), stored
  boxed at `ManagedObjects[ReferenceOffset=1]`.
- IL-primitive temps (e.g. `<>7__wrap1`) -- in `Primitives`.

The SM is a heap object end-to-end. MoveNext's `this` (ParamInfos[0]) is a
4-byte reference. There is NO in-frame VT to hoist for the common case -- the
"hoist" is conceptual (the heap instance already survives across the
suspension); the `HoistNeoILValueToHeap` helper is reused only if a byval
ephemeral (an in-frame VT local captured by the async closure) needs a frame->
heap copy. The design below covers both (the helper is a no-op for the pure-
heap SM but is the escape valve for the in-frame-VT-local case).

## Goals / Non-Goals

**Goals (this child):**
- Unblock the suspend-reachability path so `AwaitUnsafeOnCompleted_Neo` is
  actually entered (Phase 1: B1 B2 B3 + Task.Delay redirect).
- Wire `AwaitUnsafeOnCompleted_Neo` to register an `ILAsyncContext<T>`
  continuation with the awaiter's task and return without `SetResult` (the
  SM is suspended).
- Implement `ILAsyncContext<T>.MoveNext()` resumption: restore the SM to a
  fresh pooled interpreter, re-run MoveNext (resumes at the await state),
  complete the `ManualResetValueTaskSourceCore<T>`.
- Route the `get_Task` getter to the `ILAsyncContext<T>` / `core` when the SM
  suspended.
- A green end-to-end truly-async probe (`await Task.Delay(10); return v;`).
- Adversarial probes: locals-survive-resume, faulted-task, pool-isolation.
- Legacy byte-identical (every edit `#if ENABLE_NEO_MODE` or Neo-only).

**Non-Goals (deferred):**
- `AwaitOnCompleted` (ExecutionContext / SynchronizationContext capture).
- Multi-await suspend/resume (`await A; await B;` both incomplete).
- `ValueTask<T>` suspend path; `async void` suspend.
- IL-delegate-through-CLR-method round-trip (`Task.Run(ilLambda)`).
- A real zero-alloc `ValueTask<T>` (the suspend path may allocate a `Task<T>`
  wrapper initially; zero-alloc is a later optimization).

## Decisions

### D1: The SM is already a heap ILTypeInstance -- "hoist" is mostly conceptual

The sync-slice OQ2 (re-confirmed by this child's dump) established the SM is a
heap `ILTypeInstance`, not an in-frame VT. So at the `AwaitUnsafeOnCompleted`
call, slot 2 (`&sm` via `ldloca r4` where `r4 = this` = the heap reference)
points to a heap object whose state is already durable across the suspension.
The `HoistNeoILValueToHeap` helper is NOT needed for the SM itself in the
common case. The "hoist" obligation reduces to: ensure the awaiter (stored in
`<>u__1` at opcode 13 just before the suspend call) and the state (`<>1__state`
= 0 at opcode 12) are on the heap instance at suspend time -- which they are,
because MoveNext wrote them via heap `stfld`/`stfld.ref`.

The helper IS retained for the edge case where an in-frame IL value-type local
is captured by the async closure (a `struct` local referenced after the
await). For the green-target probes (heap SM + primitive locals) it is not
exercised; it ships as the escape valve.

### D2: `AwaitUnsafeOnCompleted_Neo` body -- read the awaiter's task, build the context, register the continuation

The redirect receives (per the dump):
- slot 0 (builder `this` byref, 8 bytes) -- the SM identity (recover via
  `RecoverSmFromBuilderByref` or the `CurrentAsyncSm` ThreadStatic, as the
  sync slice does).
- slot 1 (`ref awaiter` byref, 8 bytes) -- `&awaiter_local` (a frame-local
  TaskAwaiter, NOT the `<>u__1` field; the C# compiler awaits via a local
  copy). Read the awaiter via `ReadNeoValueType(typeof(TaskAwaiter<T>),
  frameBase+slot1Off, ...)`, then `GetAwaiterTask(awaiter)` -> the `Task`/
  `Task<T>` being awaited.
- slot 2 (`ref sm` byref, 8 bytes) -- `&this` (ldloca of `this`); the objIdx
  half is the SM's mStack index in the CURRENT (fresh pooled) interpreter
  frame. `mStack[objIdx]` is the SM heap ILTypeInstance.

The body:
1. Recover the SM heap ILTypeInstance (slot 2's objIdx -> mStack, or
   `CurrentAsyncSm`).
2. Read the awaiter's task (slot 1 -> `ReadNeoValueType` -> `GetAwaiterTask`).
   If the task is null or already completed, fall back to the sync path
   (defensive -- the `IsCompleted` short-circuit should have prevented this,
   but a race where the task completes between the `IsCompleted` check and the
   `AwaitUnsafeOnCompleted` call is possible).
3. Build the `ILAsyncContext<T>`: `var ctx = new ILAsyncContext<T> { 
   stateMachine = sm, moveNextMethod = GetMoveNext(sm.Type) };` Reuse the
   sync slice's `GetMoveNext` cache.
4. Register the continuation: `task.UnsafeOnCompleted(ctx.MoveNextDelegate)`
   where `MoveNextDelegate` is an `Action` wrapping the
   `IAsyncStateMachine.MoveNext` body (D3). (`UnsafeOnCompleted` = no
   ExecutionContext capture -- matches the `AwaitUnsafeOnCompleted` semantics.)
5. Return WITHOUT calling `SetResult`/`SetException` (the SM is suspended; the
   `SmTaskMap` is NOT populated; `get_Task` will route to the context per D4).

**Reentrancy / token:** the `ManualResetValueTaskSourceCore<T>` inside the
context handles the token races across concurrent `GetStatus`/`OnCompleted`/
`GetResult`. The continuation registration is single-shot (one
`UnsafeOnCompleted` per suspend).

### D3: `ILAsyncContext<T>.MoveNext()` resumption -- fresh pooled interpreter, restore SM, ExecuteNeo, complete core

The resumption fires on the threadpool thread that completed the awaited task
(`UnsafeOnCompleted` contract). There is NO in-flight Neo frame. Mirror the
sync slice's `DriveMoveNext` (which mirrors Step-19 `NeoInvokeSub`):

1. `ILIntepreter intp = appdomain.RequestILIntepreter();` -- a FRESH pooled
   interpreter (its engine stack is empty; no in-flight frame to clobber).
2. `try { ... } finally { appdomain.FreeILIntepreter(intp); }` -- balanced
   pool lifecycle (the Step-19 F1 lesson: missing free = pool starvation; the
   green smoke misses it, so AP4 instrumentation is mandatory).
3. Inside the `try`: build a Neo frame at `intp.Stack.StackBase`, zero locals,
   reserve the ref region, write the SM heap instance as slot-0 `this`
   (`mStack[frameRefBase + thisSlot.RefOffset] = stateMachine;
   *(int*)(frameBase + thisSlot.Offset) = thisRefIdx;`).
4. `intp.ExecuteNeo(moveNextMethod, frameBase, retDst, retRefBase, out
   unhandled)` -- MoveNext resumes at the await state (driven by `<>1__state`),
   reloads `<>u__1`, calls `GetResult`, runs to the next terminal state
   (`SetResult`/`SetException` at opcodes 38/33).
5. The `SetResult`/`SetException` redirects fire on the fresh interpreter
   (they use `CurrentAsyncSm` to key `SmTaskMap`). To route the result to the
   `ILAsyncContext<T>.core` instead of the `SmTaskMap`, set
   `_currentAsyncSm` to a sentinel OR have the context register a one-shot
   SetResult hook. **Cleanest:** the resumption sets a ThreadStatic
   `_currentAsyncContext` before `ExecuteNeo`; the `SetResult`/`SetException`
   redirects check it FIRST -- if set, call `ctx.core.SetResult(value)` /
   `ctx.core.SetException(ex)` instead of stashing in `SmTaskMap`. This
   reuses the sync redirects' result-read logic and only swaps the sink.
6. On completion, `FreeILIntepreter` in `finally`. The caller's
   `ValueTask<T>.GetResult` (or the `Task<T>` wrapper's continuation) reads
   `core.GetResult(token)`.

**Multicast / nested suspend:** the resumption is single-shot per suspend. A
multi-await SM will re-enter `AwaitUnsafeOnCompleted` on the next incomplete
await (the context is reused or a fresh one is built). Multi-await is a
deferral (Non-Goal), but D3 does not preclude it.

### D4: `get_Task` routes to the context when the SM suspended

The sync-slice `get_Task` reads `SmTaskMap[sm]`. When the SM suspended,
`SmTaskMap` has NO entry (SetResult didn't run). The getter must detect
suspension and produce a `Task<T>` backed by the `ILAsyncContext<T>.core`:
- The suspend path (D2 step 5) parks the `ILAsyncContext<T>` on a
  `SmContextMap[sm]` (a ThreadStatic `Dictionary<ILTypeInstance,
  ILAsyncContext<T>>`, parallel to `SmTaskMap`).
- `get_Task`: if `SmTaskMap[sm]` exists -> completed/faulted task (sync path,
  unchanged). Else if `SmContextMap[sm]` exists -> build a `Task<T>` from the
  context's `IValueTaskSource<T>` (`Task<T>.Create` wrapping, or a
  `TaskCompletionSource`-style bridge -- the simplest correct shape is a
  `Task<T>` constructed via `System.Threading.Tasks.Task.FromCancellation`-
  style factory is NOT applicable; use a helper that calls
  `ctx.OnCompleted` + `ctx.GetResult`). Else -> defensive default (sync slice
  behavior).
- The `ValueTask<T>` builder's `get_Task` (for `AsyncValueTaskMethodBuilder<T>`)
  can directly return `new ValueTask<T>(ctx, ctx.Token)` -- the zero-alloc
  fast path. (Deferred to the ValueTask suspend slice; the Task path ships
  first.)

### D5: Phase-1 reachability unblockers (the prerequisite -- each dump-gated)

- **D5a (B3) `Nop` case:** add `case OpCodeREnum.Nop: ip++; continue;` to the
  `ExecuteNeo` switch (Neo-only). A true no-op. AP5 confirms no perturbation.
- **D5b (B2) void-GetResult guard:** in `TaskAwaiter_T_GetResult_Neo`, gate
  the `Result` read on `method.DeclearingType.TypeForCLR.IsGenericType`; for
  the non-generic `TaskAwaiter`, return without writing (GetResult is void).
  Closes the sync-slice TC2/TC5-family `await Task` (non-generic) edge.
- **D5c (B1, load-bearing) 2-generic-arg redirect resolution:** dump-gate the
  `TryGetRedirection` path for a closed-generic method with 2 generic args.
  The 1-arg `Start<TSM>` resolves (TC1 green); the 2-arg
  `AwaitUnsafeOnCompleted<TA,TSM>` does not (entry trace never prints).
  Suspect: `AppDomain.TryGetRedirection` / `CLRMethod`'s
  `GetGenericMethodDefinition` handling matches the 1-arg case but not the
  2+-arg. The focused fix is in the redirect-lookup (make the closed-generic
  -> open-definition match arity-agnostic). **STOP if the fix is in shared
  dispatch affecting non-async generic redirects broadly** -> split into
  `neo-generic-redirect-resolution`.
- **D5d Task.Delay redirect:** permanent `Task.Delay(int)` Neo redirect in
  `Register()` (the awaitable source for the green test). Small, test-infra.

### D6: Shared-vs-Neo gating + Legacy stance

Every edit is `#if ENABLE_NEO_MODE` or in Neo-only files
(`CLRRedirections.AsyncNeo.cs`, `ILAsyncContext.cs`, `ILIntepreter.Neo.cs`).
Legacy `ExecuteR` + the autogen Legacy `#else` arms are untouched.

**Legacy stance:** Legacy async uses the `IAsyncStateMachineAdaptor`
CrossBindingAdaptor (the boxing model the Neo design rejects). Whether Legacy
supports truly-async suspend/resume at all is OUT OF SCOPE for this child; the
Neo suspend machinery is Neo-only. If Legacy also defers truly-async, Neo-only
is acceptable (document it). The regression reference for shared-engine edits
is Legacy 518/519 (confirm Legacy-neutral).

## Risks / Trade-offs

- **[B1 is shared-dispatch]** -> the 2-generic-arg redirect-resolution fix
  COULD touch the generic-method-redirect lookup used by ALL generic CLR
  redirects (not just async). Mitigation: dump-gate FIRST (P1.3 before any
  Phase-2 work); if the fix is in `TryGetRedirection` and is arity-agnostic,
  blast-radius is contained (other generic redirects either already work via
  a different path or are unaffected). STOP+split if broad. The full `NeoStep`
  smoke (204/204) is the regression gate.
- **[Cross-thread interpreter state]** -> the resumption runs on a threadpool
  thread; `ExecuteNeo` mutates the engine's `esp`/`mStack.Count`. The FRESH
  pooled interpreter (D3) isolates this (its engine stack is empty). Risk: if
  the SAME pooled interpreter is concurrently used by another resumption
  (reentrancy). Mitigation: each resumption `RequestILIntepreter`s its own;
  the pool hands out distinct instances. `ManualResetValueTaskSourceCore`
  handles the token races.
- **[Pool leak on the async hot path]** -> the Step-19 F1 lesson. Mitigation:
  AP4 instrumentation (pool alloc/hit counters) verifies balanced
  request/free on every resume path (happy, exception, nested). `finally`
  block is non-negotiable.
- **[Infinite loop / hang]** -> a malformed resumption that does not reach
  terminal state loops; a deadlock looks like a hang. Mitigation: the green
  probe spin-waits with a max-iterations guard; >10s = kill. Distinguish a
  genuinely-slow threadpool resume (sub-second) from a deadlock (infinite).
- **[The awaiter is a frame local, not the `<>u__1` field]** -> the dump shows
  slot 1 is `ldloca r3` (the awaiter LOCAL), not `ldflda <>u__1`. D2 reads the
  awaiter from the local. The `<>u__1` field is stored separately at opcode 13
  (for the resumed MoveNext to reload at opcode 20). Both must be consistent;
  the resumed MoveNext reads `<>u__1` (opcode 20), so opcode 13's store must
  land on the heap instance (it does -- heap `stfld.ref`).
- **[The `Nop` gap may have friends]** -> the async MoveNext's catch/
  SetException/finally/SetResult structure is denser than normal methods.
  Dump-gate confirmed `leave.s`/`Endfinally`/`initobj` are handled; if the
  adversarial probes surface another unhandled opcode, add it (focused).

## Open Questions

- **OQ1 (resolve at Phase 1):** is the B1 redirect-resolution failure in
  `TryGetRedirection`'s `GetGenericMethodDefinition` handling, or in how the
  JIT emits the `call` operand for a 2-generic-arg method? The dump (entry
  trace never prints) narrows it to the redirect-lookup path, but the exact
  site is Phase-1 apply work.
- **OQ2 (resolve at Phase 2):** does the resumed MoveNext's `SetResult`/
  `SetException` correctly route to `ctx.core` via the `_currentAsyncContext`
  ThreadStatic (D5 step 5), or does the `CurrentAsyncSm`/`SmTaskMap` path
  intercept first? Decide the sink-swap mechanism at apply.
- **OQ3 (resolve at Phase 2):** for the `Task<T>` wrapper backed by the
  context, is a `TaskCompletionSource<T>` bridge (simplest, one alloc) or a
  custom `Task<T>` derived from `IValueTaskSource<T>` (zero-alloc, complex)
  the right first cut? Recommend the TCS bridge for the first green; optimize
  later.

## Migration Plan

No migration (pure feature add for Neo; Legacy unchanged). Rollback = revert
the change directory + source edits; the `AwaitUnsafeOnCompleted_Neo` /
`ILAsyncContext<T>.MoveNext` return to throwing the tagged NIE, and the sync
slice's green probes (TC1/TC4/TC6/TC7) are unaffected (they never suspend).
