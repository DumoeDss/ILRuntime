# Ship Log — neo-async-taskrun-ildelegate (child 5)

**Date:** 2026-07-10  **Capability:** neo-async  **Wave:** completion-3, child 5
**Status:** SHIPPED (LEAD-verified)  **Scope note:** proceeded despite child 4 being PARKED —
the SYNC-lambda scope avoids child 4's foundational blockers (no async SM builder/awaiter fields).

## Delivered
`Task.Run(syncILLambda)` end-to-end. **Root cause = a STALE AUTOGEN Neo binding, NOT an engine
bug.** 3 `Task.Run` overloads in `ILRuntimeTestBase/AutoGenerate/System_Threading_Tasks_Task_Binding.cs`
did a plain cast `(Func<T>)ILIntepreter.ReadNeoReference(...)` → `InvalidCastException` (the
`FunctionDelegateAdapter<T>` wraps a `Func<T>` but isn't one). These committed bindings pre-date
the Step-19 arg-unwrap fix (`BindingGeneratorExtensions.cs:244-246` now emits
`CheckCLRTypes(..., TypeFlags.IsDelegate)`). **Fix:** sync the 3 bindings to
`(Func<T>)typeof(Func<T>).CheckCLRTypes(ReadNeoReference(...), (TypeFlags)8)`. `#if ENABLE_NEO_MODE`-
gated. No engine file touched; no new redirect (the autogen binding is the correct site — just
needed to be brought in sync with the generator). `Task.Run(Action)` ALREADY worked via the
reflection fallback — lead-6's "Task.Run can't round-trip via reflection fallback" note was
inaccurate (the fallback works for the un-redirected `Action` shape; the `Func<T>` shape was broken
in the autogen binding, which the fallback never reaches).

## Verification (LEAD-verify — simple test-side binding sync, no engine change)
- NeoStep smoke: 248 -> **253/0/0** (+5 probes TR1-TR5). No regression.
- TR probes isolated (LEAD re-ran): `Ran 5 tests, 0 failed`. TR1-TR5 PASS.
- Stash-toggle (implementer): stash the binding fix file -> 4/5 TR probes FAIL with
  `InvalidCastException` (TR2 `Task.Run(Action)` still passes via reflection); pop -> 5/5 PASS.
  Load-bearing.
- Legacy-neutral: the 3 binding lines are `#if ENABLE_NEO_MODE`-gated; plain `Debug` builds 0
  errors; TR1-TR5 PASS on plain Debug. (TC12 Legacy plain-Debug fail is pre-existing/unrelated — a
  Neo-specific multi-await test.)

## Probes (TestCases/NeoStep20Test.cs — concat-free SYNC-lambda bodies)
TR1 `Task.Run(Func<int>)` -> result; TR2 `Task.Run(Action)` side-effect (host CLR cell — avoids the
IL-stsfld cross-interpreter visibility gap); TR3 `Task.Run(Func<string>)`; TR4 closure-capturing
lambda; TR5 lambda calling an IL instance method. (Sync lambdas only — no async SM, so no
builder/awaiter CLR-struct fields, avoiding child 4's F-10/F-3 blockers.)

## Files
- `ILRuntimeTestBase/AutoGenerate/System_Threading_Tasks_Task_Binding.cs`: 3 Neo `Task.Run` overloads
  synced to `CheckCLRTypes` IsDelegate (the only stale-binding site for Task.Run).
- `ILRuntimeTestBase/Adapters/helper.cs`: +1 `RegisterFunctionDelegate<string>()` (TR3).
- `TestCases/NeoStep20Test.cs`: 5 TR probes + 2 sync IL work helpers.

## Follow-ups
- `Task.Run(async () => await ...)` (async lambda) — blocked by child 4 (F-10 field-layout + F-3
  binder NIE). Not forced.
- IL static field cross-interpreter visibility (a threadpool callback's `stsfld` not reliably
  visible to the driver's interpreter) — Step 3 follow-up. TR2 sidesteps it via a host CLR cell.
- Stale-autogen-binding regen sweep (2nd stale binding found, after neo-f13) — out of scope; worth a
  one-off pass regenerating all committed `*_Binding.cs` against the current generator.

## Review
LEAD-verify (the binding diff matches the codegen `CheckCLRTypes` IsDelegate pattern; the TR gate
5/5 green was re-run by the LEAD; Legacy-neutral by Neo-gating). Simple test-side binding sync with
no engine change — LEAD diff-read in lieu of a full non-author reviewer dispatch.
