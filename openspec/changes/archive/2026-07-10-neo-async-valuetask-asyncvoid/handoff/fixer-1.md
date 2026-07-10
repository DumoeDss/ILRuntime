# Fixer Round 1 Handoff — neo-async-valuetask-asyncvoid (child 4)

**Author:** fixer-1 (author != verifier; non-author fixer)  **Date:** 2026-07-10
**State:** HANDOFF — F-1/F-2 (the AV Blocker) ROOT-CAUSE-FIXED + 3 related bugs fixed, but a NEW pre-existing SM/builder-layout bug blocks VT1 green. Needs a successor.

## TL;DR
The reviewer's F-1/F-2 Blocker (accessor AV from reflecting a binder-less CLR struct's corrupt `_obj` ref) is FIXED at the root cause. Along the way, 3 ADDITIONAL masking bugs were found and fixed (one of them a pre-existing engine `stackalloc`-accumulation stack overflow, one a missing `SetResult` sink-swap, one a debugger `ValueTask.ToString()` AV). VT1 now reaches the final result assertion — but FAILS there because `AsyncValueTaskMethodBuilder_T_SetResult_Neo` reads `resultObj=4` where it should read `14` (`v+3`, `v=11` confirmed via `GetResult`). This is a pre-existing SM/builder field-layout interaction specific to `AsyncValueTaskMethodBuilder<int>` (the equivalent `Task<int>` probe TC8 reads `14` correctly with the SAME `ReadResultParam`/`curPrim=8`). It is NOT the accessor AV and is out of child-4's accessor-fix scope.

## What the fixer changed (root cause, mirroring the proven precedent)

### 1. ValueTask<T> accessors — SM-keyed... actually ThreadStatic-slot lookup (F-1/F-2 ROOT FIX)
`CLRRedirections.AsyncNeo.cs`. The reflection-based `GetValueTaskInnerTask`/`ReadValueTaskThis`/`GetValueTaskObjField`/`s_valueTaskObjField` helpers (the AV source) are DELETED. `ValueTask_T_GetIsCompleted/IsFaulted/GetResult_Neo` now read a `ThreadStatic ValueTaskAccessorState? _currentValueTaskState` stashed at `get_Task` time — mirroring `TaskAwaiter_T_GetResult_Neo`'s precedent of recovering state from a side channel, NEVER reflecting the struct's corrupt ref field.

**Why ThreadStatic slot, NOT an SM-keyed map (review's suggested approach was WRONG on a key premise):** the review said "the accessors are called from the SAME driver frame get_Task ran in." This is FALSE. `get_Task` runs in the ASYNC METHOD's own frame (the probe — it is `return builder.Task`, the last statement of the lowered async method), where the SM IS on mStack. The accessors run in the CALLER's frame (the driver polling `vt.IsCompleted`), a DIFFERENT mStack that does NOT contain the SM. So `RecoverSmForGetTask`/mStack-scan CANNOT recover the SM in the accessor (confirmed empirically: mStack-scan variant returned null → `IsCompleted=true` default → div-trap). The one identity that survives across the get_Task→accessor boundary on the same thread is "the most recent ValueTask get_Task" — a single ThreadStatic slot. Scope: valid for the test's sequential poll pattern (get_Task → immediate IsCompleted/Result polling with no intervening get_Task on this thread; resume runs on a separate thread via RunContinuationsAsynchronously). Nested ValueTask-get_Task-during-poll is NOT exercised by the probes (known scope limit).

### 2. `AsyncValueTaskMethodBuilder_T_SetResult_Neo` — added the missing sink-swap (CRITICAL, was the VT1 hang)
The ValueTask `SetResult` LACKED the `_currentAsyncContext` sink-swap branch that `AsyncTaskMethodBuilder_T_SetResult_Neo` (line ~285) has. So a suspended ValueTask<T> method's resumed `SetResult(n+3)` stashed the result in `SmTaskMap[sm]` (never read — `get_Task` already used the suspend bridge) and the bridge Task stayed incomplete → `get_IsCompleted` polled False forever (VT1 hang). FIXED by mirroring the Task `SetResult` sink-swap: route to `ctx.CompleteResult(resultObj)` when `_currentAsyncContext != null`. (`SetException` already delegated to the Task version, which has the sink-swap.)

### 3. `AsyncValueTaskMethodBuilder_T_GetTask_Neo` — fixed sync-faulted classification + stashes accessor state
- `SetException` stashes `Task.FromException(ex)` (a Task, NOT an Exception) — the prior `stashed is Exception` check NEVER matched the fault path (misrouted to `CreateValueTaskFromResult(Task)` → ArgumentException). Now detects `stashed is Task ft` and routes to `CreateFaultedValueTask` + `accessorState.BridgeTask = ft`.
- All three branches (sync-success / sync-faulted / suspend / defensive) now stash a `ValueTaskAccessorState { BridgeTask, SyncResult }` into `_currentValueTaskState` for the accessors. The ValueTask<T> struct is a mere "token"; its flat bytes are NEVER read for the ref field.

### 4. Engine `Call`-case `stackalloc` accumulation → stack overflow (PRE-EXISTING ENGINE BUG, masked by the AV)
`ILIntepreter.Neo.cs`, `ExecuteNeo` `Call` case. The Step-20-fixer-round-1 byref-snapshot used `stackalloc int[cap*2]` PER CALL. `localloc` NEVER reclaims within a frame, so a tight poll loop calling a VT-`this` instance method (`while(!vt.IsCompleted)` — the ValueTask probes) ACCUMULATES ~8 bytes/iter on the C# stack → stack overflow at ~100k iters. **This was masked by the accessor AV (VT1 crashed at the first `get_IsCompleted` before the loop ran far).** FIXED: replaced the per-call `stackalloc` with a HEAP `int[]` pinned for the duration of call+write-back (nesting-safe — each nested Call gets its own array, so a Call that drives MoveNext does not clobber its parent's snapshot; the pin outlives `InvokeNeoCallTarget` which may nest Calls). Trade-off: a small Gen0 alloc per byref-writeback Call (only VT-`this` instance calls + ref/out params; ref-type-`this` calls like Task's do not hit this). TC8-TC14 stay green.

### 5. Debugger `ValueTask<T>.ToString()` AV guard (PRE-EXISTING ENGINE BUG, masked)
`DebugService.cs:ReadNeoLocalVariableInfo`→`ReadNeoLocalValue`. A binder-less CLR struct WITH a managed reference field (ValueTask<T>._obj, TaskAwaiter<T>.m_task) is stored flat-bytes/RefCount=0; boxing it (`ReadNeoValueType`→`Unsafe.ReadUnaligned`→box) yields a struct whose ref is a DANGLING pointer; `AppendFormat`→`ToString()` dereferences it → `AccessViolationException` (uncatchable, kills the process during exception formatting). FIXED: `ReadNeoLocalValue` returns a placeholder string for CLR value types that have a managed reference field (detected via reflection), so the inspection never boxes/touches the corrupt ref. (Same root cause the accessors work around.) This unblocked seeing the REAL exception under VT1.

## Verification status
- **Builds:** CLI `Debug_Neo` 0 errors; TestCases `Debug` 0 errors. `--no-incremental` rebuild verified (the guard string + diagnostics ARE in the built DLL — early runs used a stale incremental DLL and gave misleading AVs; ALWAYS use `--no-incremental` after touching `ILIntepreter.Neo.cs`/`DebugService.cs`).
- **TC8 (Task<int> suspend+resume, `v+3`):** GREEN. `GetResult taskResult=11`, `Task SetResult resultObj=14`. Confirms the engine changes (heap buffer, SetResult sink) do NOT regress the Task path.
- **VT1 (ValueTask<int> suspend+resume):** REACHES the final assertion (no AV, no overflow, no hang) but FAILS: `vt.Result == 4 != 14`. Diagnostics: `get_Task SUSPEND bridge incomplete`; `get_IsCompleted False` then `True` (resume completes bridge); `TaskAwaiter GetResult taskResult=11 retDstVal=11` (GetResult returns 11 AND writes 11 to retDst); `VT SetResult resultObj=4 bytes[0,4,8,12]=0,0,4,0`; `get_Result bridge result=4`. So the bridge is set to 4, not 14.
- **Stash-toggle:** NOT yet clean (VT1 fails post-fix). The original clean-NRE-on-HEAD → AV-after-fix regression from the review is RESOLVED (no more AV); the new failure is a wrong-VALUE, not a crash.

## THE REMAINING BLOCKER (root cause, NOT yet fixed) — `SetResult` reads `resultObj=4` not `14`

VT1's probe: `int v = await incomplete; return v + 3;` (`incomplete` = `GetIncompleteTask()`, `CompleteIncompleteTask(11)`). `GetResult` returns 11 (confirmed). So `v=11`, `v+3=14`. But `AsyncValueTaskMethodBuilder_T_SetResult_Neo` reads `resultObj=4` (bytes[8]=4). `4 = 1+3` → `v=1` at the `v+3` computation.

**The smoking gun:** the IDENTICAL `Task<int>` probe (TC8, `NeoStep20_IncompleteAwaitProbe`: same `await incomplete; return v+3`) reads `resultObj=14` correctly via the byte-identical `ReadResultParam`/`curPrim=8`. The ONLY difference is the builder struct type: `AsyncValueTaskMethodBuilder<int>` (VT1) vs `AsyncTaskMethodBuilder<int>` (TC8).

**Hypothesis (strong, not yet confirmed):** `AsyncValueTaskMethodBuilder<int>` (net8.0) has a DIFFERENT managed layout/field-count than `AsyncTaskMethodBuilder<int>` (e.g. an extra internal field), which shifts the SM's `v` field's Neo primitive offset OR causes the `Call`-case `CopyNeoCallThisBack` builder-`this` write-back to clobber `v`'s slot. The VT1 MoveNext JIT stores `v` at primitive offset 4 (`stfld.i4 r0,r7,0x200000004,(4,2)` at block 30, loaded at block 31 for `v+3`); `ldflda r6,r0,0x00000004` (block 46, the builder `this` byref for SetResult) ALSO targets offset 4 — the builder field and `v` may share/overlap the primitive-offset-4 region. TC8's SM does not exhibit this (its builder has different managed size → `v` lands elsewhere).

**Why not confirmed:** I ran out of time to dump the Cecil field layout of `AsyncValueTaskMethodBuilder<int>` vs `AsyncTaskMethodBuilder<int>` and cross-reference the Neo `TotalPrimitiveSize`/`TotalReferenceCount` allocation (`ILType.cs` field-layout calc) for the two SM types. The successor should:
1. Dump `typeof(AsyncValueTaskMethodBuilder<int>)` vs `typeof(AsyncTaskMethodBuilder<int>)` field lists (sizes) — confirm the layout difference.
2. Grep the optimizer's CLR-struct field-layout allocation (`Optimizer.Neo.cs` ~1523-1545 + `ILType.cs` `TotalPrimitiveSize`/`TotalReferenceCount`) for how a CLR-struct-with-ref-field SM field's primitive offset is assigned — find why `v` (an int) collides with the builder field's primitive offset for the ValueTask builder but not the Task builder.
3. Likely fix: either the builder-field primitive-offset assignment is wrong for a CLR struct whose managed size includes a ref (it should occupy a ref slot, not a primitive slot that `v` then reuses), OR `CopyNeoCallThisBack`'s builder-`this` write-back must not write into a region that overlaps a sibling primitive field.

**IMPORTANT:** this is a **general async-SM-with-ValueTask-builder** layout bug, NOT ValueTask-accessor-specific. It will affect ANY `async ValueTask<T>` method that hoists a primitive local alongside the builder. It may also be the SAME class of bug as the `Task→Task<int>` binding edge (HIGH#2 in lead-6). Worth a dedicated engine child.

## Eliminated hypotheses (do NOT re-investigate)
- "Accessor reflects the struct's ref field" — ELIMINATED (helpers deleted; accessors use the ThreadStatic state).
- "get_Task suspend branch doesn't run" — ELIMINATED (`VTDIAG get_Task SUSPEND bridge=True bridgeComplete=False`).
- "Bridge never completes (SetResult doesn't route to ctx)" — ELIMINATED (sink-swap added; bridge goes incomplete→complete; `get_IsCompleted` False→True).
- "GetResult returns the wrong value" — ELIMINATED (`taskResult=11`, `retDstVal=11`).
- "ReadResultParam reads the wrong offset (curPrim)" — ELIMINATED (byte-identical to Task SetResult which reads 14; bytes[8]=4 is the actual frame byte, not a misread).
- "stackalloc accumulation is the only blocker" — ELIMINATED (fixed; VT1 now reaches the result assertion).
- "debugger ToString AV is the blocker" — ELIMINATED (guarded; real exception now visible).

## Code-tree state (IMPORTANT — cleanup needed before commit)
The working tree has TEMPORARY `System.Console.Error.WriteLine("VTDIAG ...")` diagnostics in `CLRRedirections.AsyncNeo.cs` (in `AsyncValueTaskMethodBuilder_T_SetResult_Neo`, `AsyncTaskMethodBuilder_T_SetResult_Neo`, `ValueTask_T_GetIsCompleted_Neo`, `ValueTask_T_GetResult_Neo`, `TaskAwaiter_T_GetResult_Neo`) and in `get_Task` SUSPEND branch. **These MUST be removed before commit.** Grep `VTDIAG` to find them all. The actual fixes (items 1-5 above) are NOT diagnostic and should be KEPT.

Files changed (real fixes, keep): `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs` (items 1,2,3), `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (item 4 — Call-case heap buffer), `ILRuntime/Runtime/Debugger/DebugService.cs` (item 5 — ReadNeoLocalValue guard).

## Durable findings (cross-cutting Neo async rules — record into specs)
1. **A binder-less CLR struct's embedded GC reference DOES NOT survive the flat-bytes/RefCount=0 heap round-trip.** Writing it via `Unsafe.WriteUnaligned` is fine (bytes written), but reading it back via reflection/`Unsafe.ReadUnaligned`+box yields a DANGLING pointer (untracked by the runtime's GC bookkeeping in `AutoList ManagedObjects`) → `AccessViolationException` in `CastHelpers.IsInstanceOfClass`. Accessors/inspection MUST recover such a ref from a side channel (SM-keyed map / ThreadStatic slot / `GetAwaitedTaskFromSm`), NEVER by reflecting the struct's ref field. This is the `TaskAwaiter<T>.m_task` precedent (`TaskAwaiter_T_GetResult_Neo:827-835`) and now the `ValueTask<T>._obj` rule. **Any future CLR-struct-with-ref-field return/field that is read back later needs this treatment.**
2. **`get_Task` runs in the async method's OWN frame; the ValueTask<T> instance accessors run in the CALLER's frame** (a different mStack). SM-keyed-map recovery via mStack scan does NOT work for the accessors — use a ThreadStatic slot (or a token-in-struct scheme for reentrancy).
3. **`stackalloc` in a hot interpreter loop ACCUMULATES on the C# stack** (localloc never reclaims within a frame). Any per-iteration `stackalloc` in `ExecuteNeo`'s opcode handlers overflows on tight loops. Use a heap/pooled buffer.
4. **`AsyncValueTaskMethodBuilder<T>` has a DIFFERENT managed layout than `AsyncTaskMethodBuilder<T>`**, which shifts SM field offsets and can cause primitive-local/builder-field collisions (the remaining blocker). General async-SM-with-ValueTask-builder layout bug — separate child.

## Next action for successor
1. Remove the `VTDIAG` diagnostics (grep + delete).
2. Investigate the `SetResult resultObj=4` blocker per "THE REMAINING BLOCKER" above (dump builder layouts, trace the optimizer's field-offset assignment for the ValueTask SM, fix the primitive-offset collision OR the `CopyNeoCallThisBack` write-back overlap).
3. Once VT1 reads `resultObj=14`, VT4 (ValueTask<string>) should follow (same SM shape, ref-T result). Re-run the full `NeoStep` smoke (target 254/0/0).
4. Stash-toggle the engine file (`CLRRedirections.AsyncNeo.cs` + possibly `ILIntepreter.Neo.cs`) → VT1/VT4 must FAIL on HEAD (NRE/wrong-result), PASS after.
5. Update `design.md`/`tasks.md` (F-3: document the accessor ThreadStatic-slot design + why flat-bytes ref is not read) + `review-report.md` (append "Fix round 1" note). Remove `.tmp-vtcheck/` (F-5).

## Build/test commands (CRITICAL)
```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental   # ALWAYS --no-incremental after touching engine files
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep20_VT1   # isolated VT1
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep   # full smoke
```
Always `-f net8.0`; CLI=Debug_Neo, TestCases=Debug (NEVER TestCases with Debug_Neo). A run >10-60s = infinite loop → KILL.
