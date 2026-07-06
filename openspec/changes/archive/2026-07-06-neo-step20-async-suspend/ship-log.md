# Ship Log — neo-step20-async-suspend (PARTIAL: Phase 1 reachability unblockers)

> Change: `neo-step20-async-suspend` (portfolio child of `neo-completion-portfolio`)
> Capability: `neo-async`
> Branch: `features/object-model-overhaul`
> Date: 2026-07-06
> Pipeline: small-feature, Tier A, full autonomy.
> Status: **PARTIAL SHIP** — Phase 1 (reachability unblockers) delivered; Phase 2
> (suspend machinery) deferred to 2 split children.

## Verdict

**SHIPPED (partial).** Review verdict **APPROVE-WITH-FINDINGS**, 0 Blocker / 0 Major
(review-loop clean; 1 Minor workflow artifact, closed by this log + the
deferred-items update). The implementer correctly **STOPPED at Phase 1** per the
F-10/K1 discipline: the suspend redirect is unreachable end-to-end due to 2
stacked pre-existing blockers, and a focused fix was not pinnable in this
child's scope. A false-positive probe (green via blocking `GetResult` on an
incomplete `Task.Delay`, NOT a genuine suspend+resume) was caught and removed.

## Delivered scope (Phase 1 — reachability unblockers, 3 items)

The planner dump-gated HEAD and found the `AwaitUnsafeOnCompleted_Neo` redirect
is NEVER entered at runtime. Three independently-correct unblockers shipped
(all Neo-only, `#if ENABLE_NEO_MODE`-gated, Legacy-neutral):

1. **B3 — `Nop` case** (`ILIntepreter.Neo.cs`, `ExecuteNeo` dispatch switch):
   `case OpCodeREnum.Nop: ip++; continue;`. ECMA `nop` has no operand; the
   catch-all NIE previously threw on any Nop. Regression-proven by 204/204
   (Nop is common in compiled methods).
2. **B2 — `void-GetResult` guard** (`CLRRedirections.AsyncNeo.cs`,
   `TaskAwaiter_T_GetResult_Neo`): the handler is registered for BOTH the generic
   `TaskAwaiter<T>` (`.Result` exists) and the non-generic `TaskAwaiter` (void
   `GetResult`, no `.Result`). The unconditional `.Result` read via
   `InvokeMember("Result", ...)` threw `MissingMethodException` for the
   non-generic awaiter. Guard the `.Result` read on
   `method.DeclearingType.TypeForCLR.IsGenericType`; the non-generic path returns
   void (no result write). **Reviewer independently proved this load-bearing**
   (disabled the guard → reproduced the exact `MissingMethodException:
   System.Threading.Tasks.Task+DelayPromise.Result not found`).
3. **Task.Delay redirect** (`CLRRedirections.AsyncNeo.cs`, `Task_Delay_Neo` +
   Register entry): a permanent `Task.Delay(int)` redirect so a suspend probe has
   a real threadpool-completing suspend source (`Task.Run(ilLambda)` can't suspend
   — an IL lambda is a DelegateAdapter that doesn't round-trip). Round-trip
   confirmed (returns a real threadpool-completing Task).

**Diff scope:** `ILIntepreter.Neo.cs` +7, `CLRRedirections.AsyncNeo.cs` +38.
Pure additions. `CLRMethod.cs` + `TestCases/NeoStep20Test.cs` reverted to HEAD
(the B1 instrumentation + the false-positive probe were removed).

## Verification evidence

- **Neo `NeoStep` smoke: 204/204** (baseline preserved; the false-positive TC9
  probe was removed — it was green via blocking `GetResult`, not suspend).
- **NeoStep20 sync slice: 9/9** (TC1/TC7 generic-awaiter regression guards intact).
- **NeoOptHardening: 24/24.**
- **Legacy-neutral:** both shipped files are `#if ENABLE_NEO_MODE`-gated; plain
  `Debug` compiles them out (0 errors).
- **0 stray diagnostics** in shipped files (the 9 `PROBE` hits in
  `TestCases/NeoStep13b/16Test.cs` are pre-existing `//` comments, unrelated).

## Deferred scope (Phase 2 — suspend machinery) → 2 split children

The suspend machinery (wire `AwaitUnsafeOnCompleted_Neo` + `ILAsyncContext<T>`
resumption + `get_Task` context branch) is DEFERRED. Two stacked pre-existing
blockers prevent it; neither was pinnable to a focused fix in this child:

1. **Control-flow blocker (masks B1; highest value):** the `AwaitUnsafeOnCompleted`
   call is never executed. Despite `get_IsCompleted` writing `isCompleted=false`,
   `brtrue.s` takes the completion path — a **register/dest mismatch** in the
   IsCompleted redirect wiring (the redirect writes `DstOffset`; `brtrue` reads
   `SrcOffset`). So `AwaitUnsafeOnCompleted_Neo` is never reached. → split child
   **`neo-async-controlflow-iscompleted`** (focused-ish; dump-confirm the
   SrcOffset/DstOffset wiring).
2. **B1 (2-generic-arg redirect resolution):** the closed-generic
   `AwaitUnsafeOnCompleted<TA,TSM>` (2 generic args) resolves only on the Legacy
   `RedirectMap`, NOT on `RedirectMapNeo` — so the registered open-definition Neo
   redirect is never used. `Start<TSM>` (1 generic arg) resolves on Neo (TC1
   green). Suspected: `TryGetRedirection` / `GetGenericMethodDefinition` arity-
   specific handling. Broad/entangled (shared dispatch). → split child
   **`neo-generic-redirect-resolution`** (re-dump-gate AFTER the control-flow
   child lands, since it masks B1).

`AwaitUnsafeOnCompleted_Neo` / `AwaitOnCompleted_Neo` remain tagged NIE stubs;
`ILAsyncContext<T>.MoveNext()` remains the skeleton NIE. The foundation
(`HoistNeoILValueToHeap`, the builder redirects, the awaiter accessor redirects)
is unchanged and proven.

## Lessons reaffirmed

- **The dump-gate + STOP discipline caught a false-positive probe.** The +1 green
  probe was green via a BLOCKING `TaskAwaiter.GetResult()` on the incomplete
  `Task.Delay(10)` — NOT a genuine suspend+resume. The classic F-10/K1 silent-
  wrong-result. The implementer caught it (the `AwaitUnsafeOnCompleted_Neo
  ENTERED` probe never printed) and removed the probe rather than ship a green-
  looking but wrong result. **A green async smoke is NEVER proof of suspend —
  confirm the redirect actually ran.**
- **Probe before designing (reaffirmed).** The propose-phase plan assumed the
  suspend redirect was reachable; the dump found 3 stacked blockers that made it
  unreachable. The phased plan + STOP gate turned a potential broken-ship into a
  clean partial + 2 well-scoped split children.
- **A green smoke does not prove a fix load-bearing (Step 17 B1).** B2 looked
  cosmetic; the reviewer proved it load-bearing by disabling it and reproducing
  the exact `MissingMethodException`.
