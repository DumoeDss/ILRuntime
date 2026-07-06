# Review Report — neo-step20-async-suspend (PARTIAL ship, Phase 1 only)

**Reviewer:** non-author adversarial review (`openspec-gstack-review`).
**Date:** 2026-07-06.
**Branch:** `features/object-model-overhaul`.
**Scope:** the SHIPPED Phase-1 diff only (2 files, +45 lines). Phase 2 (suspend
machinery) was correctly STOPPED per the F-10/K1 discipline; this review does NOT
cover the unwired Phase-2 body.

## VERDICT: APPROVE-WITH-FINDINGS

No Blocker. No Major. The three shipped Phase-1 items are each **independently
correct, focused, and Neo-only**. The B2 guard was proven **load-bearing** under
adversarial probe (not cosmetic). One Minor finding: the ship-workflow
documentation artifacts (ship-log.md + `neo-deferred-items.md` STEP-20-PARTIAL
update) are incomplete — this does not affect the shipped code's correctness.

## Scope check — CLEAN (no drift)

`git diff HEAD --stat` over `ILRuntime/` + `TestCases/`:

```
ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs | 38 +++++
ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs |  7 ++
2 files changed, 45 insertions(+)
```

- `ILRuntime/Runtime/CLRBinding/CLRMethod.cs` — **NOT in diff** (B1
  instrumentation reverted; no fix applied, B1 escalated as broad/entangled).
- `TestCases/NeoStep20Test.cs` — **NOT in diff** (the false-positive TC9 probe +
  helper removed).
- No other source files touched. Stated intent (3 Phase-1 reachability
  unblockers) == delivered. No scope creep.

## Findings

### [PASS] F1 — B3 `Nop` case (`ILIntepreter.Neo.cs:4021`)

`case OpCodeREnum.Nop: ip++; continue;` is correct:

- **Placement:** inside the main `ExecuteNeo` dispatch `switch`, immediately
  after `Rethrow` and before `Leave`/`Leave_S`/`Endfinally` (all real opcodes in
  the same dispatch). Confirmed by reading `ILIntepreter.Neo.cs:4006-4068`.
- **Operand width:** ECMA `nop` has no operand; in `OpCodeR` (register form,
  one struct slot per instruction) it occupies exactly one slot, so `ip++`
  advances past it and `continue` re-enters the dispatch loop WITHOUT executing
  the trailing `ip++` at `:4364` (the `continue` skips it, matching how
  `Leave`/`Endfinally` handle their own advancement). Correct.
- **Previously:** `Nop` fell through to the `default` catch-all at
  `ILIntepreter.Neo.cs:4362` → `throw new NotImplementedException("Neo: opcode
  Nop not yet implemented (Step 6)", code)`. The case now intercepts it. The
  catch-all line number shifted 4355→4362 because of the +7 insertion —
  consistent.
- **Masking risk:** none. `Nop` is semantically a no-op by ECMA definition;
  treating it as a no-op is correct regardless of how/why it surfaced. No other
  consumer expects `Nop` to NIE — the only other `OpCodeREnum.Nop` references
  are in the Optimizer (nop-stripping/lowering at `Optimizer.Utils.cs:578/885/
  1543`, `Optimizer.Neo.cs:1138`, `Optimizer.FCP.cs:42`, `Optimizer.BCP.cs:43`)
  and `JITCompiler.cs:2554` (which *sets* `op.Code = Nop` during lowering) —
  none of these is an interpreter dispatch.
- **Regression gate:** full `NeoStep` smoke **204/204** (Nop is common in
  compiled methods; a masking regression would surface here). Confirmed.

### [PASS] F2 — B2 `IsGenericType` guard (`CLRRedirections.AsyncNeo.cs:510-512`)

```csharp
Type awaiterClr = method.DeclearingType.TypeForCLR;
if (!awaiterClr.IsGenericType)
    return;
```

Correct + **proven load-bearing**:

- **Right type checked:** the guard is on
  `method.DeclearingType.TypeForCLR.IsGenericType` — i.e. the awaiter type the
  redirect was resolved against (the declaring type of the `GetResult` method
  being redirected). `RegisterAwaiterAccessors`
  (`CLRRedirections.AsyncNeo.cs:738-744`) registers the SAME handler
  (`TaskAwaiter_T_GetResult_Neo`) for BOTH `typeof(TaskAwaiter<int>)`
  (generic → `IsGenericType=true` → reads `.Result` + writes it) AND
  `typeof(TaskAwaiter)` (non-generic → `IsGenericType=false` → early return,
  writes nothing; `GetResult()` is `void`). Correct both branches.
- **Closed-but-non-generic misfire:** N/A — `TaskAwaiter` is genuinely
  non-generic; `TaskAwaiter<T>` is always generic. `IsGenericType` cleanly
  distinguishes the two. No misfire path.
- **Adversarial proof of "load-bearing" (the load-bearing check):** I temporarily
  disabled the guard (`if (!awaiterClr.IsGenericType) return;` commented out),
  rebuilt, and ran `Task.Delay(10).GetAwaiter().GetResult()` (the **exact**
  await shape of the suspend slice's target) as a probe. Result:
  ```
  Rethrown as Exception: System.MissingMethodException:
    Method 'System.Threading.Tasks.Task+DelayPromise.Result' not found.
  Ran 3 tests, 1 failded
  ```
  — precisely the error the proposal/planning-context documented. With the guard
  restored, the probe passes. **B2 prevents a real MissingMethodException on the
  `Task.Delay` awaiter (the suspend slice's chosen awaitable source); it is not
  cosmetic.** (Note: `Task.CompletedTask.GetAwaiter().GetResult()` did NOT throw
  even with the guard disabled — its runtime task type apparently exposes a
  `Result` property — but the `DelayPromise` case is the one that matters for
  the suspend slice, and it throws without the guard.)
- **Regression gate:** `NeoStep20` sync slice **9/9** (TC1 + TC7 exercise the
  generic `TaskAwaiter<T>` path through the same redirect — the guard does not
  perturb the generic branch). Confirmed.

### [PASS] F3 — `Task.Delay(int)` redirect (`CLRRedirections.AsyncNeo.cs:572-583` + register `:732-736`)

```csharp
public static void Task_Delay_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
    CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
{
    int curPrim = 0;
    int ms = *(int*)(frameBase + curPrim);
    WriteReferenceReturn(Task.Delay(ms), retDst, retRefBase, mStack);
}
```

Correct:

- Reads the single `int` arg from the first primitive frame slot, returns the
  **real** `Task.Delay(ms)` via `WriteReferenceReturn` (`:1014-1022`: writes the
  Task to `mStack[retRefBase]` + the ref index to `retDst`). The real
  `Task.Delay(int)` completes on the threadpool after the delay — a genuine
  awaitable source whose `IsCompleted` is false at the await check (the trigger
  for the suspend path).
- **Registration:** `typeof(Task).GetMethod("Delay", flag, null, new[]{
  typeof(int) }, null)` → the `Task.Delay(int)` overload specifically (not the
  `TimeSpan` / `CancellationToken` arities), registered via
  `RegisterCLRMethodRedirectionNeo` onto the Neo map. Correct signature + map.
- **Leak:** none — the Task is handed to the caller's frame ref slot; standard
  ref-return lifecycle (same shape as `Task_GetCompletedTask_Neo`).
- **Standing-alone safety:** it is a suspend *source*, but with Phase 2 not
  landed it is only exercised here as a plain Task source. Adversarial
  round-trip probe `Task.Delay(10).Wait()` passed (returns a real, completing
  Task); 204/204 smoke clean.
- **Deferred-NIE comment** on `AwaitUnsafeOnCompleted_Neo` (`:430-435`): a
  clear, accurate comment explaining the body is deferred + why the redirect is
  currently unreachable (the 2 stacked blockers). Good documentation of the
  partial-ship boundary.

### [PASS] F4 — No stray diagnostics

- `grep -rn "REVIEWPROBE\|PROBE\|Console.WriteLine"` in both shipped files =
  **0 hits**.
- The 9 `PROBE` occurrences in `TestCases/NeoStep13bTest.cs` (1) and
  `TestCases/NeoStep16Test.cs` (8) are all `//` comments (pre-existing
  adversarial-probe section headers from prior steps). Confirmed — not the
  implementer's diagnostics.
- Run output contains **0 `PROBE` lines**.

### [PASS] F5 — Regression gates (all green)

| Gate | Result |
|---|---|
| `NeoStep` full smoke (`Debug_Neo`, useRegister=true) | **204/204** |
| `NeoStep20` sync slice | **9/9** |
| `NeoOptHardening` | **24/24** |
| Legacy-neutral (plain `Debug` CLI build) | **0 errors** |

Both shipped files begin with `#if ENABLE_NEO_MODE` (`CLRRedirections.AsyncNeo.cs:1`,
`ILIntepreter.Neo.cs:1`) — compiled out entirely in plain `Debug`, so Legacy is
byte-identical by construction. Confirmed by a clean plain-`Debug` CLI build
(only the pre-existing unrelated `CS1668` LIB-env warnings).

### [MINOR] F6 — Ship-workflow documentation incomplete (task 5.5)

- **In-change deferral documentation: thorough.** `planning-context.md` "Findings
  -- neo-step20-async-suspend (apply, 2026-07-06)" records: (a) the 2 stacked
  blockers (B1 closed-generic redirect resolution + the `brtrue.s`-after-
  `get_IsCompleted` control-flow bug), (b) the **false-positive-probe lesson**
  (TC9 was green via a *blocking* `GetResult` on an incomplete `Task.Delay(10)`,
  not a genuine suspend+resume — "the classic F-10/K1 silent-wrong-result"), (c)
  the 2 proposed split children, (d) the STOP decision. `tasks.md` STATUS mirrors
  this. This is the authoritative record and it is complete.
- **`ship-log.md` is absent** (tasks.md 5.5 says to write it; not present in the
  change directory).
- **`neo-deferred-items.md` not yet updated for THIS slice's STOP:** the
  `STEP-20-PARTIAL` entry (`neo-deferred-items.md:1006-1051`) still describes the
  *prior* sync-slice partial; it references `neo-step20-async-suspend` as the
  future owner of the suspend path but does NOT record that this slice itself
  STOPPED at Phase 1 with 2 split children (`neo-async-controlflow-iscompleted`,
  `neo-generic-redirect-resolution`).

This is a documentation-completion gap, **not** a defect in the shipped Phase-1
code. It should be closed (write `ship-log.md`; add a STEP-20-SUSPEND-PARTIAL
sub-entry to `neo-deferred-items.md` + handoff §5/§6) before the change is
archived. For a PARTIAL ship pausing here with Phase 2 rerouted to split
children, the in-change record (planning-context + tasks STATUS) is sufficient
to not lose context.

## Adversarial probes (all temporary — written, verified, REMOVED)

Per the Step-17-B1 lesson ("green smoke != correct"), I wrote three temporary
probes into `TestCases/NeoStep20Test.cs`, rebuilt, ran, and **removed them
before writing this report**. Working tree re-verified clean (+45 across the 2
expected files only).

| Probe | What it exercises | Result |
|---|---|---|
| `NeoStep20_Rev_NonGenTaskAwaiterGetResult` (`Task.CompletedTask.GetAwaiter().GetResult()`) | non-generic `TaskAwaiter.GetResult()` redirect (B2 void branch) — redirect IS entered (`IsGenericType=False`), CompletedTask's task did not throw even w/o guard | PASS (with guard); confirms redirect reachable for struct-local call |
| `NeoStep20_Rev_TaskDelayRoundTrip` (`Task.Delay(10).Wait()`) | `Task_Delay_Neo` returns a real completing Task | PASS |
| `NeoStep20_Rev_TaskDelayGetResult` (`Task.Delay(10).GetAwaiter().GetResult()`) | non-generic awaiter of a `Task+DelayPromise` (the suspend target's awaitable) | **FAILS without B2** (`MissingMethodException: Task+DelayPromise.Result`) → **PASSES with B2** |

The REV-C probe is the load-bearing one: it reproduces the exact
`MissingMethodException` the proposal documented, on the exact awaitable source
the suspend slice will use, and proves the B2 guard eliminates it.

## Confirmed-correct reverts

- `CLRMethod.cs` — `git diff HEAD` = empty. The +52 from the prior PARTIAL state
  was entirely dump instrumentation; reverted to HEAD with no lookup-logic change
  (B1 root cause unpinned → correctly escalated, not half-fixed).
- `TestCases/NeoStep20Test.cs` — `git diff HEAD` = empty. The false-positive TC9
  probe + `NeoStep20_AsyncSuspendHelper` removed (would have asserted suspend
  works when it does not).

## Partial-ship deferral — properly documented

The STOP is well-reasoned and the deferral is recorded:

1. **`neo-async-controlflow-iscompleted` (NEW, highest value):** the
   `brtrue.s`-after-`TaskAwaiter_T_GetIsCompleted_Neo` completion-branch-when-
   `IsCompleted=false` bug (suspected DstOffset-vs-SrcOffset register mismatch).
   This is the EARLIER blocker — the suspend opcode is never reached at all.
2. **`neo-generic-redirect-resolution` (B1 split):** the closed-generic
   2-arg `AwaitUnsafeOnCompleted<TA,TSM>` call not dispatching to
   `RedirectionNeo` during execution while 1-arg `Start<TSM>` does. Re-dump-gate
   AFTER child 1 closes (it currently masks B1).
3. **Phase 2 (this child, resumed):** once 1+2 close and a probe reaches the
   `AwaitUnsafeOnCompleted_Neo` tagged NIE, implement the suspend machinery per
   design D2/D3/D4 + AP1-AP4.

The **false-positive-probe lesson** (green via blocking `GetResult`, not genuine
suspend) is explicitly recorded in `planning-context.md` as "the classic F-10/K1
silent-wrong-result" — the discipline held.

## Conclusion

The Phase-1 diff is small, focused, and each item is independently correct and
Neo-only. B2 was proven load-bearing under direct adversarial probe (the
`Task+DelayPromise.Result` MissingMethodException is real and the guard
eliminates it). B3 is a textbook no-op. The Task.Delay redirect is behaviorally
identical to the reflection fallback. No Blocker/Major open. The implementer's
STOP at Phase 1 (given the 2 stacked pre-existing blockers) is the correct call
under the F-10/K1 discipline, and the deferral is thoroughly recorded in the
change artifacts. The only follow-up is the Minor ship-workflow documentation
(ship-log.md + deferred-items/handoff update, task 5.5) before archive.
