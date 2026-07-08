# Review Report - neo-async-movenext-fix

**Reviewer:** verify-stage (independent; author != verifier). No subagents spawned.
**Date:** 2026-07-08.
**Diff reviewed:** `git diff -- ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs ILRuntime/Runtime/Intepreter/ILAsyncContext.cs ILRuntimeTestBase/TestFramework/TestClass3.cs TestCases/NeoStep20Test.cs` (4 files, +403 / -88).
**Skill:** `openspec-review` (Standards + Spec axes run by the reviewer directly; the skill's Greptile/Codex/frontend/E2E passes do not apply to this C# runtime change and subagents were forbidden by the task).

---

## VERDICT: APPROVE-WITH-FINDINGS

The change genuinely fixes the #1 Neo usability gap (truly-async `await` suspend + resume + GetResult + SetResult) **for the shape that is gated** (a single awaited `Task`-local whose `TaskCompletionSource` is completed from the host). The headline claim is TRUE and the binding proof holds: the Piece-1 zero-extend is load-bearing (stash-toggle re-introduces the exact hang), and the deterministic TC8 conjuncts genuinely prove suspend + resume end-to-end. All gate counts were independently reproduced. Legacy is neutral.

Two findings are real correctness gaps in the *general* truly-async path that the green smoke is structurally blind to (Finding A is a latent resume-path bug with a one-line fix; Finding B is a silent-wrong-result risk for multi-`Task` state machines). Neither blocks the gated scope, but both should be addressed or recorded as accepted-known before declaring truly-async "done" for arbitrary user code.

| # | Severity | Gated? | One-line |
|---|----------|--------|----------|
| A | MEDIUM (latent, common pattern) | No | `_currentAsyncContext` is not cleared for sync drives -> nested-async-during-resume misroutes the inner result to the outer resume context, then double-completes the bridge TCS |
| B | MEDIUM | No | `GetAwaitedTaskFromSm` reverse-scan is silent-wrong-result for SMs with >1 hoisted `Task`; transient-Task resume faults; masks an unverified F-10 object-model bug |
| C | LOW | Partially (Option B is the gate) | Option A's unconditional 8-byte write is unsafe if the `IsCompleted` dest slot is ever 4 bytes (deferred Option B is the proper fix) |
| D | LOW | No | `SmContextMap[sm]` entry leaks on the driver thread (the cross-thread resume removes it on the wrong thread) |
| E | INFO | No | Custom / non-`Task` awaiters fault at suspend (not silent); not documented as a limitation |

---

## 1. Independent gate re-runs (all reproduced; LEAD's counts confirmed)

Build: `dotnet build ILRuntimeTestCLI -c Debug_Neo` = 0 errors; `dotnet build TestCases/TestCases.csproj -c Debug` = 0 errors. **Build-cache gotcha checked and cleared:** the CLI build reported "0 errors" in 2.78 s as a correct incremental no-op only after I verified every load-bearing assembly mtime is newer than its source -- `ILRuntime.dll` (Debug_Neo) 11:06:53 > `CLRRedirections.AsyncNeo.cs` 11:06:15; `ILRuntimeTestBase.dll` (net8.0) 10:46:11 > `TestClass3.cs` 10:42:55 and contains `CompleteIncompleteTask` (symbol probe); `TestCases.dll` 11:28:40 > `NeoStep20Test.cs` 10:45:37. Each forced rebuild (after the stash-toggle edits) took 4.8-6.4 s and re-emitted `ILRuntime.dll`, confirming the JIT was not serving a stale cache.

All runs: `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true <FILTER>` (filter is a Contains substring; each run separately):

| Filter | Result | Claimed | Hang? |
|--------|--------|---------|-------|
| `NeoStep20` | **12 ran, 0 failed, 0 ignored** | 12/0/0 | No |
| `NeoStep` | **218 ran, 0 failed, 0 ignored** | 218/0/0 | No |
| `NeoStep25LoadExec` | **28/28 cells passed, 0 failed** | 28/28 | No |

TC8 was confirmed *invoked* (not silently skipped): the NeoStep20 log shows `Invoking TestCases.NeoStep20Test.NeoStep20_TC8_TrulyAsyncSuspendResume` immediately before the run summary. The "failures"/"exception" grep hits in the NeoStep/NeoStep20 logs are JIT-disassembled `ldstr` assertion-message literals and `Exception.get_Message` call instructions in test bodies -- not real test failures or thrown exceptions.

**Legacy-neutral:** plain `dotnet build ILRuntimeTestCLI -c Debug` = 0 errors (structurally clean -- every engine edit is `#if ENABLE_NEO_MODE`-wrapped: `CLRRedirections.AsyncNeo.cs` line 1 and `ILAsyncContext.cs` line 1). Legacy `NeoStep20` (`-c Debug`, `useRegister=true`) = **12/0/0**, TC8 invoked and passing. Legacy's own async machinery handles the truly-async path independently of the Neo redirects, so TC8 crosses the engine boundary correctly.

---

## 2. TC8 binding-proof assessment (the whole point -- a green TC8 alone does NOT prove it)

**Does the stash-toggle of Piece 1 re-introduce the hang? YES (binding proof holds).**

Probe procedure: I reverted the single Piece-1 line in `TaskAwaiter_T_GetIsCompleted_Neo` from `*(long*)retDst = ...` (8-byte zero-extend) back to `*(int*)retDst = ...` (the buggy 4-byte write), rebuilt Debug_Neo (confirmed `ILRuntime.dll` re-emitted, 0 errors), and ran `NeoStep20` under a 38 s hard timeout.

Result: **EXIT=124 (killed), 38 s elapsed, NO summary line reached.** The last test invoked before the hang was exactly `NeoStep20_TC8_TrulyAsyncSuspendResume`. This matches the design's root-cause trace: the 4-byte write leaves stale non-zero high pointer bits in the 8-byte `Brtrue` slot, the branch is taken when `IsCompleted == false`, execution misroutes into the sync-completion block, `TaskAwaiter_T_GetResult_Neo` calls `task.GetType().InvokeMember("Result", ...)` on the never-completed `TaskCompletionSource`-backed Task, and the real CLR `Task.Result` **blocks forever**. After restoring the fix and rebuilding, `NeoStep20` returned to 12/0/0. This is the load-bearing proof the fix is real, not a coincidence of the smoke.

**Do the `!t.IsCompleted` / `t.Result == N+3` conjuncts genuinely prove suspend + resume? YES.**

- `!t.IsCompleted` (GATE 1) fires only because the SM suspended: with the fix, `Start` registers the continuation and returns *without* `SetResult`, so `get_Task` returns the `SmContextMap` bridge TCS (still incomplete). The stash-toggle proves the negative -- without the fix, the SM does NOT suspend (it misroutes into the blocking `GetResult`) and TC8 never reaches GATE 1 at all (it hangs first). So the conjunct cleanly separates "suspended" from "misrouted-to-sync".
- `t.Result == N+3` (GATE 2) proves the resume ran `GetResult` + continued + `SetResult`, not a stale/cached value: the value `N+3` is computed only on the resume path (`NeoStep20_IncompleteAwaitProbe` returns `n + 3` where `n` is the `CompleteIncompleteTask(N)` argument chosen at GATE 1 time). The bridge TCS is completed solely by `ctx.CompleteResult` from the resumed `SetResult`; there is no other writer, and the value is data-dependent on the host-driven completion. A cached/stale value could not equal `N+3`.

The deterministic TCS probe is therefore binding, exactly as the design claims. The green smoke does not prove this; the stash-toggle + the two conjuncts do.

---

## 3. The awaiter-ref-loss workaround (Finding B -- the load-bearing correctness question)

**Implementation.** Two cooperating pieces:
1. `GetAwaitedTaskFromSm(sm)` (suspend path + GetResult resume fallback): scans `sm.ManagedObjects` **reverse** for the first directly-hoisted `Task` ("highest field index wins"); falls back to scanning for a boxed `TaskAwaiter`/`TaskAwaiter<>` and reading its `m_task`.
2. `TaskAwaiter_T_GetResult_Neo` resume fallback: when the reloaded awaiter's `m_task` is null (claimed lost in the F-10 boxed-struct round-trip), it re-runs `GetAwaitedTaskFromSm(CurrentAsyncSm)`.

**Sound for TC8 (single `Task`-local).** `NeoStep20_IncompleteAwaitProbe` assigns the incomplete `Task` to a local (`Task<int> incomplete = ...; await incomplete;`), so Roslyn hoists it as the lone `Task` field; the reverse-scan deterministically returns it on both suspend (continuation registration) and resume (GetResult). Correct.

**Silent-wrong-result risk for multi-`Task` SMs (MEDIUM).** The reverse-scan returns the *highest-field-index* `Task`, which is field-declaration order, NOT "the `Task` currently being awaited." A method such as `Task<int> b = GetB(); Task<int> a = GetA(); return await b;` may lay `a` at a higher field index than `b`; the scan returns `a`, so (i) at suspend the continuation is registered on the wrong `Task` (resume fires when `a` completes, not `b`), and (ii) at resume `GetResult` reads `a.Result` instead of `b.Result`. Both produce a **silently wrong value** (or a block on an incomplete wrong Task). The heuristic "most-recently-assigned wins" in the code comment is inaccurate -- `ManagedObjects` is field-layout order, not assignment order. The durable-finding #4 deferral ("multi-await ... assume single-await") covers *two incomplete awaits in one SM* but does NOT cover *two `Task` locals with a single await*, which is the silent-wrong case here.

**Transient-`Task` resume fault.** For `await GetTaskAsync()` where the `Task` is not a user local, Roslyn does not always hoist a separate `Task` field; the only carrier is the awaiter's `m_task`. At suspend, fallback-2 finds it (fresh awaiter, `m_task` present). At resume, `m_task` is null (the workaround's premise) and fallback-1 finds nothing -> `GetResult` throws `NullReferenceException` ("awaiter has no task"). This is a *fault*, not silent-wrong, but it is a real limitation for a common shape and is not documented.

**The underlying F-10 claim is unverified.** The premise -- "the CLR-struct-with-ref-field's `m_task` reference does not survive the F-10 boxed-struct stfld/ldfld storage" -- is an *assertion* about a bug in the Neo boxed-struct storage. If true, it is a general object-model defect affecting every CLR struct with a reference field (not just `TaskAwaiter`), and the proper fix is to preserve the reference in the boxed-struct round-trip (durable finding #1 acknowledges this). This review could not independently verify the claim without a debug heap trace; the workaround's correctness *depends* on fallback-1 (a hoisted `Task` local) being present, which holds for TC8 and for explicit-local awaits but not for transient awaits.

**Custom / non-`Task` awaiters (INFO).** `GetAwaiterTask` gates on the object being exactly `TaskAwaiter` / `TaskAwaiter<>`. A custom awaiter, a `ConfiguredTaskAwaitable` awaiter, or any non-`Task` awaiter returns `null` from both `GetAwaiterTask` and the direct-`Task` scan, so `SuspendStateMachine` throws "could not recover the awaited task" (fault, not silent). Not documented as a limitation.

**Assessment:** the workaround is SOUND only for the single-`Task`-local shape. For multi-`Task` SMs it is a silent-wrong-result risk; for transient/custom awaiters it faults. The m_task-loss it masks is an unverified object-model bug. Recommend: (a) record multi-`Task`-single-await as an explicit accepted-known limitation (it is narrower than the "multi-await" deferral); (b) record transient-`Task` and custom-awaiters as unsupported (fault); (c) track the F-10 boxed-struct-ref-preservation fix as the durable resolution.

---

## 4. Pool-lifecycle assessment (Step-19 F1 lesson)

**Balanced on every resume path.** `DriveMoveNextCore` (`CLRRedirections.AsyncNeo.cs:1065-1148`) acquires `ILIntepreter intp = appdomain.RequestILIntepreter()` (line 1068), and the only statements between the acquire and the `try` are non-throwing ThreadStatic reads/writes (`prevSm`/`prevCtx` reads, `_currentAsyncSm = sm`, the conditional `_currentAsyncContext` write). `appdomain.FreeILIntepreter(intp)` runs in `finally` (line 1146). So a successful acquire is always paired with a free -- on the happy path, on an `ExecuteNeo` throw (exception path), and on nested re-entry. If `RequestILIntepreter` itself throws, nothing was acquired, so no free is owed. The ThreadStatic save/restore (`_currentAsyncSm`/`_currentAsyncContext`) is also in the same `finally`, so neither leaks across drives. This correctly applies the Step-19 F1 lesson.

**Cross-thread / concurrency.** The resume fires on the threadpool thread that completed the awaited `Task`. `DriveMoveNextCore` requests a FRESH pooled interpreter whose engine stack is empty, isolating it from any in-flight frame. `RequestILIntepreter`/`FreeILIntepreter` are thread-safe (`lock (freeIntepreters)`, `AppDomain.cs:1608`), so concurrent resumes do not corrupt the pool. The ThreadStatic maps (`SmTaskMap`, `SmContextMap`, `_currentAsyncSm`, `_currentAsyncContext`) are per-thread, so cross-thread isolation holds for the values themselves. The single-await TC8 does not stress concurrency, but the structural design is sound. (See Finding D for one cross-thread *cleanup* gap that does not affect correctness of a single resume.)

---

## 5. Adversarial findings

### Finding A (MEDIUM, latent) -- `_currentAsyncContext` is not cleared for sync drives; nested-async-during-resume misroutes

`DriveMoveNextCore` sets the sink only conditionally:

```csharp
_currentAsyncSm = sm;
if (sink != null) _currentAsyncContext = sink;   // CLRRedirections.AsyncNeo.cs:1077
```

For a SYNC drive (`DriveMoveNext` from `Start`, `sink == null`), `_currentAsyncContext` is left **unchanged**, so it inherits whatever the outer scope had. During a RESUME, `_currentAsyncContext` is the outer resume's context (`ctx_A`). If the resumed SM_A's MoveNext invokes another async method SM_B (the normal `await AnotherAsyncMethod()` lowering) and SM_B completes synchronously, SM_B's `Start` -> `DriveMoveNext(sync)` -> SM_B's `SetResult` runs with `_currentAsyncContext == ctx_A` (inherited). `SetResult` checks `_currentAsyncContext` first, so it routes SM_B's result to `ctx_A.CompleteResult` -- the OUTER bridge gets the INNER result. When SM_A later reaches its own `SetResult`, `ctx_A.CompleteResult` runs a second time -> `ManualResetValueTaskSourceCore.SetResult` / `tcs.SetResult` throw `InvalidOperationException` (double-complete), propagating out of the resumed MoveNext as an unobserved threadpool exception, and the bridge Task observes the wrong (SM_B's) value.

This is reachable in an extremely common pattern: any truly-async method that, after its first I/O suspend, awaits another async method that completes synchronously. TC8 is single-level (no nested async) and TC7 (`NestedAsyncSync`) runs on the pure sync path (no resume scope), so the gate is structurally blind to it.

**Fix (one line):** assign unconditionally -- `_currentAsyncContext = sink;` (null for the sync drive) -- and rely on the existing `prevCtx` save/restore to reinstate the outer resume context. Top-level sync drives have `_currentAsyncContext == null` already, so this is a no-op for them; nested-sync-during-resume gets the required `null` isolation.

### Finding B (MEDIUM) -- see section 3

`GetAwaitedTaskFromSm` reverse-scan silent-wrong-result for multi-`Task` SMs; transient-`Task`/custom-awaiter fault; masks an unverified F-10 object-model bug.

### Finding C (LOW) -- Option A's unconditional 8-byte write

`TaskAwaiter_T_GetIsCompleted_Neo` writes `*(long*)retDst` unconditionally (line 647). This is safe for TC8 because the dest slot is 8 bytes (reused for the `&awaiter` managed pointer). But the write is not sized by the *actual* allocated slot width -- it assumes the slot is always >= 8 bytes. If the optimizer ever allocates a 4-byte slot for an `IsCompleted` result (e.g. a different SM shape where the slot is not pointer-reused), the 8-byte write clobbers the adjacent 4 bytes of the next register. This is exactly the latent general bug that Option B (generic zero-fill sized by the dest slot width) closes; Option A is async-specific and the `get_IsCompleted` redirect is registered globally, so the risk is real in principle though not exercised by any gated SM. Honestly documented as Option-B-deferred in `tasks.md` and `specs/neo-async/spec.md`.

### Finding D (LOW) -- `SmContextMap` entry leaks on the driver thread

`SetResult`/`SetException` do `SmContextMap.Remove(sm)` on resume (lines 230, 258), but the resume runs on a threadpool thread whose ThreadStatic `SmContextMap` is *empty* (the entry was parked on the DRIVER thread during `Start`->suspend). `get_Task` (which runs on the driver thread) reads the `SmContextMap` entry but does not remove it. So the driver thread's `SmContextMap[sm]` entry is never cleaned up. Bounded growth (one entry per distinct suspended SM per thread); not a correctness issue for a single resume, but a latent per-thread map leak on a long-lived driver thread that creates many async SMs.

### Finding E (INFO) -- custom / non-`Task` awaiters fault at suspend

See section 3. Not silent, but the limitation is not documented.

---

## 6. Spec coherence

The implementation matches `specs/neo-async/spec.md`:
- **Branch-size correctness requirement** -> `*(long*)retDst` zero-extend in `TaskAwaiter_T_GetIsCompleted_Neo` (Piece 1, Option A). The generic hardening is recorded as a tracked follow-up, not hidden. Matches the spec's "async-specific zero-extension ... is the confirmed minimal gate" framing.
- **Suspend/resume IMPLEMENTED requirement** -> `AwaitUnsafeOnCompleted_Neo`/`AwaitOnCompleted_Neo` real bodies via `SuspendStateMachine`; `ILAsyncContext<T>.MoveNextInternal` resume via `ResumeAsync`->`DriveMoveNextCore`; sink-swap (`_currentAsyncContext` checked FIRST) in `SetResult`/`SetException`; `get_Task` consults `SmContextMap` and returns the TCS bridge. Matches D3 step 5 / OQ2 ("`_currentAsyncContext` wins") and OQ3 (TCS bridge first cut).
- **Scenario: truly-async await suspends, resumes, completes** -> proven by TC8 (section 2). **Scenario: get_Task routes to the context bridge** -> the `RecoverSmForGetTask` scan now consults both `SmTaskMap` and `SmContextMap`; verified in the run (`get_Task` returns the bridge, observed incomplete at GATE 1, complete at GATE 2).
- **REMOVED requirement** (tagged NIE deferral) -> the NIE stubs are gone, replaced by the real bodies. Coherent.

**Honesty of the "met" claim.** The deferrals are recorded as accepted-known, not hidden: Option B (Finding C), multi-await (durable finding #4), `ValueTask<T>`/`async void` suspend, `ExecutionContext` capture, and the awaiter-ref-loss (durable finding #1) are all in `tasks.md` / `design.md`. **Gap in the honesty record:** multi-`Task`-single-await (Finding B's silent-wrong case), transient-`Task` fault, and custom-awaiter fault are NOT called out -- the "multi-await" deferral is narrower than these. Recommend adding them so the "truly-async works" claim is not over-stated for arbitrary user code.

---

## 7. Standards + Spec axes (openspec-review)

**Standards axis:** PASS with notes. Neo-only scoping is correct (both engine files `#if ENABLE_NEO_MODE` at line 1; test edits are plain C#, no Neo dependency). `try/finally` pool lifecycle is balanced. ThreadStatic save/restore is present for both `_currentAsyncSm` and `_currentAsyncContext`. No SQL/IO; no untrusted input. The `catch { return null; }` in `GetAwaiterTask` (line 622) swallows broadly but is acceptable for a reflective field read guarded by a preceding type check. Minor: `Activator.CreateInstance` + `Delegate.CreateDelegate` + `MakeGenericType` on every suspend is allocation/reflection-heavy on the async hot path (perf, not correctness).

**Spec axis:** PASS for the gated scope. Every Piece-1/2/3 task in `tasks.md` is implemented and the [x] claims match the code; the deterministic-gate task (TC8 redesign + `CompleteIncompleteTask`) is implemented exactly as D5 specifies. The over-claim gap (Finding B's un-documented shapes) is the only Spec-axis note.

---

## 8. Recommendations to the LEAD

1. **Land Finding A's one-line fix** (`_currentAsyncContext = sink;` unconditional) before declaring truly-async ready for general nested-async use. It is the highest-value, lowest-risk change and closes a real silent-wrong-result/double-complete path the gates cannot see. Optionally add a NeoStep20 test that resumes an SM which itself awaits a sync-completing nested async (this would gate Finding A).
2. **Record Finding B's scope honestly** in `tasks.md` / the spec: multi-`Task`-single-await is silent-wrong-result; transient-`Task` and custom/non-`Task` awaiters fault. Track the F-10 boxed-struct-ref-preservation fix as the durable resolution (it also removes the GetResult fallback).
3. Findings C/D/E are acceptable as documented/low-severity follow-ups; they do not block this change.
4. The change is **safe to ship for its gated scope** (single `Task`-local truly-async await). The binding proof (stash-toggle + TC8 conjuncts) is solid, all gates reproduce, and Legacy is neutral.

---

## 9. Artifacts

- `review-report.md` (this file) -- written.
- Working tree left clean: the stash-toggle edit was fully reverted; `git diff --stat` over the four files shows the original +403/-88 (no leftover `REVIEWER STASH-TOGGLE` comment; line 647 restored to `*(long*)retDst`). Debug_Neo rebuilt green (12/0/0) after restore.
