# Design: neo-async-taskrun-ildelegate (child 5)

## Scope decision: SYNC IL lambda (NOT async lambda)

- **IN scope:** `Task.Run(() => ILSyncWork())` where `ILSyncWork` is a plain
  (non-async) IL method or lambda returning a value / doing a side effect. A
  sync lambda does NOT build an async state machine -> no builder/awaiter CLR-
  struct fields -> AVOIDS the foundational gaps that block child 4 (the F-10
  field-layout collision + F-3 CLR-struct-with-ref `this` binder NIE -- both
  require an async SM).
- **OUT of scope (follow-up, blocked by child 4):** `Task.Run(async () => await
  ...)`. An async lambda builds an async SM whose `<>t__builder` / `<>u__1` are
  CLR-struct fields of an IL instance -> hits the same F-10 / F-3 blockers as
  `async ValueTask<T>` suspend (HIGH#1 in lead-6). Recorded as a follow-up; not
  forced here.
- Lambda bodies are CONCAT-FREE: string concat inside a lambda (`"... " + intVar`)
  lowers to `conv.ovf.u2.un` -> NIE (Step 6 gap, HIGH#3). The probes use int
  arithmetic / direct returns only.

## Reproducer finding (Q-discipline: probe BEFORE designing a fix)

Five probes added to `TestCases/NeoStep20Test.cs` (`NeoStep20_Tr*`):
- TR1 `Task.Run(Func<int>)` -> `Task<int>`, read `.Result` (BLOCKING).
- TR2 `Task.Run(Action)` -> `Task`, read `.Wait()`; side effect via a HOST cell.
- TR3 `Task.Run(Func<string>)` -> `Task<string>`, read `.Result` (BLOCKING).
- TR4 closure-capturing lambda (captures a local).
- TR5 lambda body calls an IL method + does arithmetic.

### HEAD result (before fix): 4/5 FAIL, 1/5 PASS
- TR1/TR3/TR4/TR5 FAIL with:
  `System.InvalidCastException: Unable to cast object of type
  'FunctionDelegateAdapter`1[System.Int32]' to type 'System.Func`1[System.Int32]'`
  at `System_Threading_Tasks_Task_Binding.Run_5_Neo` line 312.
- TR2 (`Task.Run(Action)`) PASSES on HEAD.

### Root cause: a STALE autogen binding, NOT a missing redirect

The lead-6 handoff note (`CLRRedirections.AsyncNeo.cs:862-864`) claims
`Task.Run(ilLambda)` "does not round-trip through the un-redirected Task.Run
reflection fallback". This was imprecise. The actual situation:

1. **`Task.Run(Action)` IS un-redirected** (no autogen overload for the
   parameterless-`Action` shape exists in `System_Threading_Tasks_Task_Binding`).
   It falls through to the reflection fallback `CLRMethod.Invoke(byte*)`, which
   CORRECTLY unwraps the IL delegate at `CLRMethod.cs:521-522` via
   `t.CheckCLRTypes(pval, TypeFlags.IsDelegate)`. **TR2 proves this path works.**

2. **`Task.Run(Func<T>)` / `Task.Run(Func<Task>)` / `Task.Run(Func<Task<T>>`)**,
   however, ARE autogen-redirected (`Run_0_Neo`, `Run_4_Neo`, `Run_5_Neo`). The
   committed `System_Threading_Tasks_Task_Binding.cs` was generated BEFORE the
   Step-19 delegate-param-unwrap fix landed in the generator
   (`BindingGeneratorExtensions.cs:244-246` `AppendArgumentCodeNeo`). The
   generator NOW emits the correct unwrap:
   ```csharp
   typeof(Func<int>).CheckCLRTypes(
       ILIntepreter.ReadNeoReference(...), TypeFlags.IsDelegate)
   ```
   but the committed file still carries the STALE plain cast:
   ```csharp
   (Func<int>)ILIntepreter.ReadNeoReference(...)
   ```
   A plain cast of a `FunctionDelegateAdapter<int>` (which WRAPS a `Func<int>`,
   is not itself one) to `Func<int>` throws `InvalidCastException`.

   Contrast: the committed Legacy (`#else`) overloads (`Run_0`, `Run_4`, `Run_5`)
   AND the Neo `this`-arm generator (`MethodBindingGenerator.cs:296-298`) AND
   dozens of other committed Neo bindings (`System_Action_*`) ALL correctly use
   `CheckCLRTypes(..., TypeFlags)8)`. Only the Neo overloads of `Task.Run` were
   stale -- a regeneration gap.

### The fix (no new redirect needed)

Patch the three stale Neo lines in `System_Threading_Tasks_Task_Binding.cs`
(`Run_0_Neo` :137, `Run_4_Neo` :273, `Run_5_Neo` :312) to the exact form the
generator emits today (`CheckCLRTypes(..., TypeFlags)8)`). This mirrors the
already-correct Legacy overloads + the `this`-arm + every other delegate-param
Neo binding. A `Task.Run` Neo redirect in `CLRRedirections.AsyncNeo.cs` was NOT
added -- the autogen binding is the right place and already exists; it just had
to be brought in sync with the generator.

A second, orthogonal harness gap surfaced for TR3 (`Func<string>`): the
`FunctionDelegateAdapter<string>` had no registered delegate type, so
`CheckCLRTypes -> GetConvertor -> DummyDelegateAdapter` threw
`Cannot find Delegate Adapter ... RegisterFunctionDelegate<string>()`. Fixed by
registering `Func<string>` in `ILRuntimeTestBase/Adapters/helper.cs` (a benign
additive delegate registration, mirrors the existing `Func<int>` registration).

### After fix: 5/5 PASS

### Stash-toggle proof (probes FAIL on HEAD, PASS with the fix)
Reverted the two fix files (`System_Threading_Tasks_Task_Binding.cs` +
`helper.cs`), rebuilt CLI `Debug_Neo`, reran probes: 4/5 FAIL with the
`InvalidCastException` (TR2 still PASS via the reflection fallback). Restored
the fix: 5/5 PASS. Confirms the binding fix is load-bearing for the `Func<>`
shapes (TR1/3/4/5); TR2 is a genuine regression guard for the already-working
reflection-fallback `Action` shape.

## Why the callback works once the delegate unwraps

Once `Task.Run` receives a real CLR `Func<int>` (unwrapped), the rest is the
existing Step-19 machinery, UNCHANGED by this change:
- `Task.Run` schedules the `Func` on the threadpool.
- The `Func` is `DelegateAdapter.InvokeILMethod` -> `NeoInvoke(null)` ->
  `NeoInvokeSub` (`DelegateAdapter.cs:1006`), which requests a FRESH pooled
  interpreter, builds a Neo frame at StackBase, writes params/this, calls
  `ExecuteNeo`, reads the return, frees the interpreter. The fresh interpreter
  isolates the callback's frame/mStack from any in-flight caller frame (the
  Step-19 hot-path discipline).
- The result flows back into the `Task<T>` the CLR `Task.Run` constructed.
- The driver reads it via a BLOCKING path (`.Result` / `.Wait()`) -- NO await,
  so the async-suspend machinery is never entered (a sync lambda builds no SM).

No engine (interpreter/JIT/object-model) change was required. The fix is
entirely in the test framework's autogen binding + a delegate registration.

## Files changed
1. `ILRuntimeTestBase/AutoGenerate/System_Threading_Tasks_Task_Binding.cs` --
   3 Neo `Task.Run` overloads: plain cast -> `CheckCLRTypes(..., IsDelegate)`.
   (Neo-gated `#if ENABLE_NEO_MODE`; Legacy overloads were already correct.)
2. `ILRuntimeTestBase/Adapters/helper.cs` -- register `Func<string>` (+1 line,
   benign additive, mirrors the existing `Func<int>` registration).
3. `TestCases/NeoStep20Test.cs` -- 5 `NeoStep20_Tr*` regression-guard probes +
   2 sync IL work helpers.

## Follow-ups
- **`Task.Run(async () => await ...)`** (async lambda): builds an async SM ->
  hits the F-10 field-layout collision + F-3 binder NIE (child 4's blockers).
  Blocked on `neo-ret-vt-with-ref-fields` (HIGH#1). Not forced here.
- **IL-static-field cross-interpreter write visibility** (surfaced then avoided
  in TR2): an IL-side `stsfld` written by a threadpool callback (fresh
  interpreter) was not reliably visible to the driver's interpreter. TR2 was
  redesigned to use a HOST CLR cell (`TestCLRBinding`) for the side-effect
  assertion, isolating it to the `Task.Run(delegate)` path. The IL-static
  visibility question is a Step-3 follow-up (broader-type statics), not this
  change.
- **Stale-autogen-binding audit**: this is the second stale autogen binding
  found by the portfolio (the `neo-f13` nested-run work found another). A
  one-shot regeneration sweep of all committed `*_Binding.cs` against the
  current generators would surface any remaining stale files. Out of scope here.
