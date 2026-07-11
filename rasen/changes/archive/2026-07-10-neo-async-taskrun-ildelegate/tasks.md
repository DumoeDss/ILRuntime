# Tasks: neo-async-taskrun-ildelegate (child 5)

## DONE

- [x] Read lead-6 handoff + the `Task.Run` reflection-fallback note
      (`CLRRedirections.AsyncNeo.cs:858-872`) + `NeoInvokeSub`
      (`DelegateAdapter.cs:1006`, the Step-19 fresh-pooled-interpreter callback).
- [x] Construct the reproducer FIRST (Q-discipline): add 5 `NeoStep20_Tr*` probes
      to `TestCases/NeoStep20Test.cs` covering Action, `Func<int>`,
      `Func<string>`, a closure-capturing lambda, a lambda calling an IL method.
      Driver reads results via BLOCKING paths only (no `await`).
- [x] Run probes on HEAD: 4/5 FAIL with `InvalidCastException` at
      `System_Threading_Tasks_Task_Binding.Run_5_Neo:312`; TR2 (Action) PASS.
- [x] Triage: root cause = STALE autogen `Task.Run` Neo binding (plain cast of a
      `FunctionDelegateAdapter<T>` to `Func<T>`). The generator already emits the
      correct `CheckCLRTypes(..., TypeFlags.IsDelegate)` unwrap; the committed
      file predates the Step-19 param-unwrap fix. NOT a missing redirect; NOT an
      engine bug. (The lead-6 "reflection fallback" note was imprecise: the
      reflection fallback works for the un-redirected `Action` shape; the
      autogen-redirected `Func<>` shapes are what was broken.)
- [x] Fix the 3 stale Neo `Task.Run` overloads in
      `System_Threading_Tasks_Task_Binding.cs` (`Run_0_Neo`, `Run_4_Neo`,
      `Run_5_Neo`) to the generator's current form. No new `Task.Run` redirect
      added (the autogen binding is the right place).
- [x] Fix the harness gap for `Func<string>`: register
      `RegisterFunctionDelegate<System.String>()` in
      `ILRuntimeTestBase/Adapters/helper.cs` (benign additive).
- [x] Redesign TR2 to use a HOST CLR cell (`TestCLRBinding`) for the side-effect
      assertion (avoids the IL-static-field cross-interpreter visibility
      question -- a separate Step-3 follow-up).
- [x] Verify: NeoStep smoke 253/0/0 (248 baseline + 5 probes, no regression);
      NeoStep20 21/0/0 (TC1-TC14 + TR1-TR5).
- [x] Stash-toggle proof: revert the 2 fix files -> rebuild -> 4/5 probes FAIL
      with the `InvalidCastException` (TR2 still PASS via the reflection
      fallback) -> restore -> 5/5 PASS. Confirms the fix is load-bearing.
- [x] Legacy-neutral: plain `Debug` CLI build 0 errors; TR1-TR5 PASS under
      Legacy plain Debug. (TC12's Legacy-plain-Debug failure is PRE-EXISTING and
      unrelated -- a Neo-specific multi-await test; my changes are Neo-gated
      binding lines + one additive delegate registration.)
- [x] Write `design.md` (reproducer finding + root cause + fix design +
      sync-vs-async-lambda scope decision + follow-ups).

## NOT IN SCOPE (recorded as follow-ups in design.md)

- [ ] `Task.Run(async () => await ...)` (async lambda) -- blocked on child 4 /
      `neo-ret-vt-with-ref-fields` (F-10 field-layout + F-3 binder NIE).
- [ ] IL-static-field cross-interpreter write visibility -- Step-3 follow-up.
- [ ] Stale-autogen-binding regeneration sweep -- out of scope.
