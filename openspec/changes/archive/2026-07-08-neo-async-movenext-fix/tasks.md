# Tasks - neo-async-movenext-fix

> Apply stage. TRUE-COMPLETION target: truly-async await works end-to-end
> (suspend + resume + GetResult + SetResult), gated by the deterministic TCS
> probe TC8 (un-ignored, GREEN). All engine edits Neo-only (`#if ENABLE_NEO_MODE`
> or Neo-only files); Legacy byte-identical.

## Piece 1 - the MoveNext control-flow hang fix (the gate)

- [x] **Option A (CONFIRMED, shipped):** zero-extend the `get_IsCompleted` bool
      result to the full 8-byte dest slot in `TaskAwaiter_T_GetIsCompleted_Neo`
      (`*(long*)retDst = isCompleted ? 1 : 0`). The 8-byte `Brtrue`/`Brfalse`
      read of a pointer-reused slot is now clean when `IsCompleted == false`, so
      the SM falls through to the suspend block and REACHES
      `AwaitUnsafeOnCompleted` (probe-confirmed; previously never reached).
      File: `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs`.
- [x] **Option B (generic Call-return zero-fill) - EVALUATED, DEFERRED.**
      Dump-gated: the generic zero-fill must live either in `InvokeNeoClrMethod`
      (requires threading the dest slot width via a signature change to
      `InvokeNeoClrMethod`/`InvokeNeoCallTarget`, used by ALL CLR redirects) or
      as a per-call-site `Unsafe.InitBlock(retDstPtr, 0, localInfos[Register1].Size)`
      across the multiple CLR call/newobj dispatch sites in the ExecuteNeo switch.
      Both are moderately broad with register-numbering edge-case risk; the shared
      Neo Call dispatch is on the path of EVERY CLR redirect. Per design D1 ("if
      broad, fall back to Option A only"), Option A is the confirmed gate and the
      full `NeoStep` smoke (218/0/0) is GREEN with it; Option B is the recommended
      longer-term hardening (closes the latent general bug for every bool/int CLR
      redirect feeding a `Brtrue`/`Brfalse` on a reused slot). No forced Option C
      (optimizer root fix; too broad).

## Piece 2 - `AwaitUnsafeOnCompleted_Neo` / `AwaitOnCompleted_Neo` suspend body

- [x] Recover the heap SM via `CurrentAsyncSm` (set by `DriveMoveNextCore`).
- [x] Read the awaited `Task` via `GetAwaitedTaskFromSm` (prefers a directly-hoisted
      `Task` reference on the SM; falls back to the awaiter `<>u__1`'s `m_task`).
      Robust `GetAwaiterTask` (gates on the object actually being a
      `TaskAwaiter`/`TaskAwaiter<T>` so `GetValue` does not throw on a non-awaiter
      managed object such as the hoisted Task or the builder).
- [x] Build `ILAsyncContext<T>` (T = the builder's result type via
      `GetResultClrType`) by reflecting the internal 2-arg ctor; cache `MoveNext`.
- [x] Park the context on `SmContextMap[sm]` (ThreadStatic, parallel to
      `SmTaskMap`) so `get_Task` returns the bridge. Return WITHOUT `SetResult`.
- [x] Register the continuation via `task.GetAwaiter().UnsafeOnCompleted(action)`
      where `action` is a `Delegate.CreateDelegate` `Action` bound to
      `ILAsyncContext<T>.MoveNextInternal` (the resume entry).
- [x] `AwaitOnCompleted_Neo` mirrors `AwaitUnsafeOnCompleted_Neo` (ExecutionContext
      capture is a deferred Non-Goal).

## Piece 3 - `ILAsyncContext<T>.MoveNext()` resume + sink-swap

- [x] `ILAsyncContext<T>.MoveNextInternal` -> `CLRRedirectionsAsyncNeo.ResumeAsync`
      -> shared `DriveMoveNextCore(appdomain, sm, moveNext, sink)`.
      `DriveMoveNext` (sync Start) was refactored to call the same core with
      `sink == null`; `ResumeAsync` recovers the AppDomain from `sm.Type.AppDomain`
      (no in-flight frame on the threadpool resume).
- [x] Fresh pooled interpreter (`RequestILIntepreter`), balanced `try/finally`
      `FreeILIntepreter` (Step-19 F1 pool-leak lesson). Save/restore
      `_currentAsyncSm` AND `_currentAsyncContext` (nested-async safe).
- [x] Build a Neo frame at `intp.Stack.StackBase`, zero locals, reserve ref region,
      write the heap SM as slot-0 `this`; `ExecuteNeo(moveNext)` resumes at the
      await state -> reloads `<>u__1` -> `GetResult` -> continues -> `SetResult`.
- [x] **Sink-swap (OQ2 resolved):** `SetResult`/`SetException` redirects check
      `_currentAsyncContext` FIRST; if set, route to `ctx.CompleteResult`/
      `CompleteException` (completes the TCS bridge + the `core`) and remove
      `SmContextMap[sm]`. Verified: the resumed `SetResult(N+3)` reaches the bridge.
- [x] `get_Task` routes to the context: `RecoverSmForGetTask` now scans for BOTH
      `SmTaskMap` (completed/faulted) AND `SmContextMap` (suspended) entries;
      `get_Task` returns `ctx.GetTaskBridge()` (the `TaskCompletionSource<T>` Task,
      OQ3 first cut) when the SM suspended.
- [x] **GetResult resume fallback:** on resume the awaiter reloaded from `<>u__1`
      surfaces with a null `m_task` (the CLR-struct-with-ref-field's reference does
      not survive the heap round-trip via the F-10 boxed-struct storage - a latent
      bug). `TaskAwaiter_T_GetResult_Neo` falls back to `GetAwaitedTaskFromSm`
      (the hoisted Task). The sync path (fresh awaiter, m_task present) is unaffected.
- [x] `ILAsyncContext<T>`: `IAsyncContextSink` (non-generic sink interface so the
      redirects route without per-SetResult reflection), `IValueTaskSource<T>` core
      (completed for the future ValueTask path), `TaskCompletionSource<T>` bridge
      (`RunContinuationsAsynchronously`), `MoveNextInternal` resume entry.

## TC8 redesign (the deterministic binding gate)

- [x] `TestCLRBinding.CompleteIncompleteTask(int)` added (drives completion of the
      deterministic TCS; self-resetting via an atomic swap so `GetIncompleteTask`
      always returns an incomplete Task across test orderings / re-runs - keeps TC10
      green when it runs after TC8). File: `ILRuntimeTestBase/TestFramework/TestClass3.cs`.
- [x] `NeoStep20_TC8_TrulyAsyncSuspendResume` (renamed from the old tag-NIE reproducer):
      `var t = NeoStep20_IncompleteAwaitProbe();` -> assert `!t.IsCompleted`
      (GATE 1, the suspend gate) -> `CompleteIncompleteTask(N)` -> bounded spin-wait
      with periodic `Thread.Yield()` (capped iterations, NO real delay) ->
      assert `t.Result == N + 3` (GATE 3, the resume gate). Un-ignored (attribute
      removed). File: `TestCases/NeoStep20Test.cs`.

## Build + test verification (CRITICAL: always `-f net8.0`)

- [x] `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` -> 0 errors.
- [x] `dotnet build TestCases/TestCases.csproj -c Debug` -> 0 errors.
- [x] Neo `NeoStep20` smoke (TC8 un-ignored): **12/0/0** (TC8 GREEN, no hang).
- [x] Neo `NeoStep` full regression smoke: **218/0/0** (TC8 moved from ignored to
      passing; no regressions from the Option-A edit or the redirect changes).
- [x] Neo `NeoStep25LoadExec` AOT capstone: **28/28 cells passed, 0 failed**.
- [x] Legacy-neutral: plain `Debug` build -> 0 errors; Legacy `NeoStep20` smoke
      (TC8 now active on Legacy too): **12/0/0**. TC8 passes on Legacy (Legacy's
      async machinery handles truly-async). No Legacy regression from un-ignoring TC8.

## Open Question resolutions

- **OQ1 (Piece 1):** Option A shipped (the confirmed gate). Option B evaluated and
  DEFERRED (broad: shared Neo Call dispatch). See Piece 1 above.
- **OQ2 (sink-swap ordering):** `_currentAsyncContext` is checked FIRST (before
  `SmTaskMap`). Verified by TC8 (the resumed `SetResult` routes to the bridge).
- **OQ3 (Task bridge):** `TaskCompletionSource<T>` bridge shipped (simplest, one
  alloc). A zero-alloc custom `Task<T>` from `IValueTaskSource<T>` is a later
  optimization (Non-Goal).

## Status: DONE (TRUE-COMPLETION achieved)

Truly-async await works end-to-end on Neo: suspend (register continuation) +
resume (fresh pooled interpreter, ExecuteNeo at the await state) + GetResult +
SetResult (routed to the TCS bridge via the sink-swap). TC8 (un-ignored) is GREEN
with both binding conjuncts (`!t.IsCompleted` proves suspend; `t.Result == N+3`
proves resume). No regressions (Neo 218/0/0; AOT 28/28; Legacy 12/0/0).

## Review-loop round 1 (fixer) -- 2 MEDIUM findings fixed + regression test

The review (`review-report.md`, APPROVE-WITH-FINDINGS) flagged 2 MEDIUM findings
on the general truly-async path the green smoke is structurally blind to. Both
fixed; C/D/E recorded as accepted-known; 2 NEW deferred items surfaced while
building the regression test (documented).

- [x] **Finding A (MEDIUM, one-line + test):** `_currentAsyncContext` was set only
      `if (sink != null)` in `DriveMoveNextCore` (CLRRedirections.AsyncNeo.cs), so
      a SYNC `DriveMoveNext` (a nested async's `Start`) during a RESUME INHERITED
      the outer resume context -> the nested SM's `SetResult` misrouted its result
      to the OUTER bridge (`ctx_A.CompleteResult`), and the outer SM's own later
      `SetResult` DOUBLE-COMPLETED `ctx_A` (wrong bridge value +
      `InvalidOperationException`). **FIX:** `_currentAsyncContext = sink;`
      UNCONDITIONAL (a sync nested drive now CLEARS the outer context; the existing
      `prevCtx` save/restore reinstates it for the resumed SM's continued
      execution; top-level sync drives are a no-op). File:
      `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs`.
- [x] **Finding A regression test `NeoStep20_TC11_NestedSyncAsyncAfterSuspend`:** a
      truly-async SM (single await, TC8-style suspend) that, after resume, CALLS a
      sync nested async (fire-and-forget -- the call triggers the nested's
      `Start` -> sync `DriveMoveNext` INSIDE the resume scope = Finding A's
      scenario). GATE 1 `!t.IsCompleted` (suspend); GATE 2 `!t.IsFaulted`;
      GATE 3 `t.Result == n + 50` (sm_A's OWN value reached the bridge exactly
      once). **Stash-toggle proof:** revert the one-line fix -> TC11 FAILs at GATE 3
      (`t.Result == 6`, the misrouted nested value, `!= n+50`); restore -> PASSes.
      File: `TestCases/NeoStep20Test.cs`.
- [x] **Finding B (MEDIUM, silent-wrong -> fail-LOUD):** `GetAwaitedTaskFromSm`
      reverse-scanned the SM's fields and returned the highest-field-index `Task`,
      NOT "the currently awaited one." For a multi-`Task` SM this was SILENT-WRONG
      (wrong continuation target + wrong `GetResult`) -- the forbidden
      silent-corruption class. **FIX:** when the direct-`Task` scan is AMBIGUOUS
      (more than one `Task` field on the SM), throw a TAGGED NIE ("Neo async
      multi-Task awaiter not supported (single-Task shape only); the
      currently-awaited Task cannot be disambiguated" -> deferred multi-await
      follow-up). The SINGLE-`Task` shape (TC8 / explicit-local single await) keeps
      working. Single-Task-supported / multi-Task-deferred scope documented in the
      spec delta + here. File: `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs`.
- [x] **C/D/E accepted-known (document only):** C (Option A's unconditional 8-byte
      `*(long*)retDst` write is unsafe if the `IsCompleted` dest slot is ever 4
      bytes; Option B generic zero-fill is the proper fix, deferred). D
      (`SmContextMap[sm]` entry leaks on the driver thread -- bounded growth; the
      cross-thread resume removes it on the wrong thread). E (custom / non-`Task`
      awaiters fault at suspend, not silent -- not previously documented as a
      limitation). All three documented in the spec delta.

### Build + test verification (round 1; CRITICAL: always `-f net8.0`)

- [x] `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` -> 0 errors.
- [x] `dotnet build TestCases/TestCases.csproj -c Debug` -> 0 errors.
- [x] Neo `NeoStep20` (TC8 + TC11): **13/0/0** (TC11 GREEN; stash-toggle FAILs pre-fix).
- [x] Neo `NeoStep` full regression: **219/0/0** (218 + TC11; no regressions).
- [x] Neo `NeoStep25LoadExec` AOT capstone: **28/28 cells passed, 0 failed**.
- [x] Legacy-neutral: plain `Debug` build -> 0 errors; Legacy `NeoStep20`
      (`useRegister=true`): **13/0/0** (TC8 + TC11 GREEN; Legacy's async machinery
      handles the nested-async path independently of the Neo redirects).

## NEW deferred items (surfaced building TC11 -- for the next child)

5. **Multi-await SM (>=2 await expressions in one SM) double-suspends.** A probe
   with TWO awaits where only the FIRST is incomplete re-suspends on the
   already-completed first Task after resume (a state-machine state/awaiter-slot
   issue), looping. This is BROADER than durable finding #4's "two INCOMPLETE
   awaits" framing -- it manifests for "two awaits, one incomplete" too. TC11's
   probe is deliberately SINGLE-await to avoid it; the nested sync async is invoked
   via a plain call (not a second `await`). Proper multi-await SM support is the
   follow-up.
6. **`RecoverSmForGetTask` mStack scan misidentifies the RESUMING SM during a nested
   async call in a resume.** When a resumed SM calls a nested async, the nested's
   `get_Task` -> `RecoverSmForGetTask` mStack scan finds the RESUMING SM (still
   parked in `SmContextMap` mid-resume) instead of the nested SM, and returns the
   resuming SM's (incomplete) bridge Task. Reading that Task's result
   (`GetAwaiter().GetResult()` / `.Result`) then BLOCKS forever. This is why TC11
   uses fire-and-forget (the nested's Task is not consumed). Properly cleaning
   `SmContextMap[sm]` at resume start (or making the scan frame-scoped) is the
   follow-up; it would also let TC11 read the nested's result directly.

## Durable findings (for the next child)

1. **The awaiter's `m_task` reference is LOST in the heap round-trip.** On resume,
   `ldfld <>u__1` returns a `TaskAwaiter<T>` whose `m_task` is null (the
   CLR-struct-with-ref-field's reference does not survive the F-10 boxed-struct
   stfld/ldfld storage). `GetResult` works around this via a fallback to the
   hoisted Task; the proper fix (preserve the ref in the boxed-struct round-trip)
   is a separate F-10-area concern that would also clean up the GetResult fallback.
2. **The awaited Task is hoisted directly on the SM** (a `Task`/`Task<T>` reference
   field in `ManagedObjects`), independent of the awaiter. `GetAwaitedTaskFromSm`
   recovers it via a direct-Task scan (reverse, most-recent wins); the awaiter's
   `m_task` is the secondary, unreliable source.
3. **`GetAwaiterTask` MUST gate on the object being a `TaskAwaiter`** before
   `GetValue`, else it throws on non-awaiter managed objects (the hoisted Task, the
   builder) - this faulted the SM silently during suspend debugging.
4. **Multi-await suspend/resume is NOT exercised** (Non-Goal): the
   `GetAwaitedTaskFromSm` reverse-scan and the `SmContextMap` single-entry-per-SM
   model assume single-await; a second incomplete await in one SM would need a
   per-awaiter context slot.
