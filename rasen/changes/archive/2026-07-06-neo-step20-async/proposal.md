## Why

Neo mode (Steps 1-19) cannot execute any `async` method. The C# compiler lowers
`async` into a state-machine struct + an `Async*MethodBuilder<T>`, and the
autogen Neo builder redirects (`System_Runtime_CompilerServices_AsyncTaskMethodBuilder_*`)
shipped with the test harness are **non-functional stubs** — they read the state
machine through a CLR `IAsyncStateMachine` interface, forcing CrossBindingAdapter
boxing and `WriteBackInstance`, which the design (`object-model-neo-design.md`
§26) explicitly rejects, and they never reach a working suspension/resumption
path. Every `async` IL method therefore fails on Neo (Step 20-tagged
`NotImplementedException` / NRE / infinite-loop family). Async is the last large
roadmap feature blocking the Neo JIT path from being feature-complete.

## What Changes

This change introduces Neo async/await support by intercepting the builder API
with custom Neo redirections that keep the state machine on the Neo frame
(synchronous path) or hoist it to the heap without an adaptor (asynchronous
path), and adds a new `ILAsyncContext<T>` bridge that the awaited task's
continuation calls back into.

**SCOPING DECISION (load-bearing — see design §Scoping): SPLIT into a
sync-first child.** This change delivers ONLY the **synchronously-completing
async** path (every `await` observes an already-complete awaitable) plus the
structural prerequisites the suspend path will reuse. The **truly-async
(suspend + resume)** path is deferred to a follow-up child
`neo-step20-async-suspend`. Rationale: the sync path exercises the builder
redirect surface + `Start`→`MoveNext` routing + sync `SetResult`/`Task` getter
WITHOUT the frame-to-heap hoist, `ILAsyncContext` continuation registration, or
cross-thread resumption — the large/risky machinery. Verify-against-code
confirms the sync path is cleanly isolatable (the builder methods are
independent redirections; `AwaitUnsafeOnCompleted` simply never runs when every
awaited task is complete). The full suspend design is captured in `design.md`
§Deferred so the follow-up is warm-seeded, but NO suspend code ships here.

Deliverables (sync scope):
- **Custom Neo builder redirections** replacing the autogen stubs for
  `AsyncTaskMethodBuilder<T>`, `AsyncTaskMethodBuilder`, and (registration-only
  for now) `AsyncValueTaskMethodBuilder<T>` / `AsyncValueTaskMethodBuilder`:
  - `Create()` — no-op (return default builder; no allocation).
  - `Start<TSM>(ref sm)` — route `sm.MoveNext()` through the Neo frame/Ref-Slot
    model (the state machine is an in-frame IL value-type; `MoveNext` is the IL
    async method body). NO `IAsyncStateMachine` interface dispatch, NO adaptor.
  - `SetResult(T)` / `SetResult()` — sync path: stash the result on the frame
    (a builder-field write through the in-frame state machine) so the `Task`
    getter can produce a completed task.
  - `SetException(Exception)` — sync path: stash the exception for the getter.
  - `get_Task` / `get_Task` getter — sync-complete path: produce a completed
    `Task<T>` / `Task` carrying the stashed result/exception.
  - `SetStateMachine(IAsyncStateMachine)` — no-op.
  - `AwaitUnsafeOnCompleted` / `AwaitOnCompleted` — **skeleton only**: in the
    sync scope these are registered so a sync-completing awaiter never calls
    them, but they throw a tagged `NotImplementedException` if reached (the
    suspend path owns the real implementation; this is the explicit split
    point). Documented non-goal here.
- **Frame-to-ILTypeInstance hoist helper** (`CopyFrameToIL`-family extension):
  the inverse of `CopyFrameToIL` — copy an in-frame IL value-type state machine
  into a fresh `new ILTypeInstance(initializeCLRInstance: false)` (no adaptor).
  Shipped as a standalone helper (unit-probed) because it is the load-bearing
  primitive the suspend path reuses; NOT wired into `AwaitUnsafeOnCompleted`
  in this change.
- **`ILAsyncContext<T>` scaffolding** (NEW file, structural only): the
  `IValueTaskSource<T>` / `IAsyncStateMachine` bridge skeleton with the
  `ManualResetValueTaskSourceCore<T>` core, the `stateMachine`/`moveNextMethod`
  fields, and the sync-shortcut paths — but the `MoveNext()` resumption body
  throws a tagged NIE (suspend path owns it). Shipped so the `Task` getter has
  a concrete `IValueTaskSource<T>` to hand to `ValueTask<T>` even on the
  sync-complete fast path (zero ambiguity for the consumer).
- **Adversarial probes** (`TestCases/NeoStep20Test.cs`, NEW): sync-completing
  `Task<T>`; sync-completing `Task`; sync-completing `ValueTask<T>`; multiple
  awaits all already-complete; async method returning a value; `async void`
  (fire-and-forget sync); async exception propagation (sync throw → faulted
  task); nested async (an async method awaiting another sync-completing async
  method). Plus a standalone probe for the frame-to-heap hoist helper.

Non-goals (explicitly deferred to `neo-step20-async-suspend`):
- The truly-async suspend/resumption path (`AwaitUnsafeOnCompleted` real impl,
  `ILAsyncContext<T>.MoveNext` resumption, the awaited task's continuation
  callback, cross-interpreter-thread resume).
- `ValueTask<T>` / `ValueTask` builder *redirection* beyond registration (the
  sync path's `get_Task` returns a `Task`; a full `ValueTask` zero-alloc path
  depends on the suspend machinery). The `AsyncValueTaskMethodBuilder` methods
  are registered as no-op/throw-tagged stubs so loading an IL assembly
  containing them does not crash; a real `ValueTask`-returning async method is
  supported via the `Task`-bearing sync path where the C# shape allows, else
  NIE-tagged.

## Capabilities

### New Capabilities

- `neo-async`: Neo async/await execution model — the builder redirection
  contract, the synchronous-completion path, the frame-to-heap state-machine
  hoist primitive, and the `ILAsyncContext<T>` bridge structure. (Synchronous
  scope only; suspension/resumption is a deferred sub-requirement tagged
  `DEFERRED` inside this capability so the follow-up child merges the rest.)

### Modified Capabilities

- `neo-dispatch`: Step 19 landed `ldftn`/`ldvirtftn`/delegate newobj/
  `InvokeILMethod` (CLR→IL callback via a fresh pooled interpreter). The async
  `Start`→`MoveNext` routing reuses the **same** in-frame IL value-type
  dispatch + the `NeoInvokeSub` frame-build inverse (build at `StackBase`,
  write `this`+params, `ExecuteNeo`, read return, free interpreter). The
  `MoveNext` continuation entry shares the Step 19 fresh-interpreter pattern.
  This change ADDS a requirement documenting that the builder
  `Start<TSM>(ref sm)` redirect SHALL drive the state machine's `MoveNext`
  through the Neo call machinery (not the CLR interface), and that the async
  resumption path (deferred) reuses the Step 19 `NeoInvokeSub` fresh-pooled-
  interpreter entry.

## Impact

- **New file** `ILRuntime/Runtime/Intepreter/ILAsyncContext.cs` — the
  `ILAsyncContext<T>` bridge skeleton (sync paths live; resumption tagged NIE).
- **New file** `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs` (or
  extend `CLRRedirections.cs`) — the custom Neo builder redirections
  (`AsyncTaskMethodBuilder_*_Neo`, etc.) + `AppDomain` registration.
- **`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`** — a
  frame-to-heap hoist helper (extend the `CopyFrameToIL`/`CopyILToIL` family;
  Neo-only) + any `MoveNext`-routing support the builder `Start` redirect
  needs.
- **`ILRuntime/Runtime/Enviorment/AppDomain.cs`** — register the custom Neo
  builder redirections (override/replace the autogen stub registrations for
  the builder types; built-in, not test-harness, mirroring the
  `ExceptionAdaptor` precedent from `neo-il-exception-throw`).
- **`TestCases/NeoStep20Test.cs`** (NEW) — the `NeoStep20_*` probes.
- **No Legacy change.** Every runtime/codegen edit is `#if ENABLE_NEO_MODE`-
  gated; `ExecuteR` is the reference. The autogen `*Neo` builder stubs live in
  `ILRuntimeTestBase/AutoGenerate/` and are overridden at registration time by
  the custom redirects (the autogen files themselves are not edited — the
  custom redirect registers AFTER and wins, mirroring how built-in redirects
  override autogen elsewhere).
- **Regression risk: MEDIUM-HIGH.** Async sits on top of the Step 8 Call
  convention, Step 12 in-frame VT, Step 13 Box, Step 17 Ref Slot, and Step 19
  delegate machinery — any latent bug in those surfaces here. Async bugs love
  to infinite-loop (test >10s = loop; kill). Gate: full `NeoStep` smoke
  (181/181 baseline) + Legacy 518/519 for any shared-engine edit. Adversarial
  probes MANDATORY (green smoke does NOT prove the async gate correct — Step
  17 B1 / OPT-HARDEN K1 / Step 19 F1 lessons).
