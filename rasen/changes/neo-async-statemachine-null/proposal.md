# proposal -- neo-async-statemachine-null (Wave-2 child C8)

## Why
4 full-Neo-smoke failures, cluster C8 (grounding 2026-07-13 Section 3,
"ArgumentNullException"):

- `TestCases.AsyncAwaitTest.TestRun`
- `TestCases.AsyncAwaitTest.TestRun1`
- `TestCases.AsyncAwaitTest.TestRun4`
- `TestCases.AsyncAwaitTest/TestClass.Show1`

All 4 throw `Value cannot be null. (Parameter 'stateMachine')` at async
bootstrap. Legacy handles them (518/519 baseline). The common shape: every
one is an `async Task` (NON-GENERIC) method.

## Root cause (pinned by stack trace, Neo-vs-Legacy)
The C# compiler lowers `async Task` (non-generic) onto the NON-GENERIC
`System.Runtime.CompilerServices.AsyncTaskMethodBuilder`. Its
`Start<TStateMachine>(ref TSM)` was NOT registered in
`CLRRedirectionsAsyncNeo.Register` -- the non-generic builder block registered
`Create`/`get_Task`/`SetException`/`SetResult`/awaiters but omitted `Start`
(the generic `AsyncTaskMethodBuilder<T>.Start` IS registered via its open
generic definition, and `AsyncVoidMethodBuilder.Start` too -- so the gap was
specific to the two non-generic task/value-task builders).

With no custom Neo redirect, dispatch falls to the AUTOGEN stub
`System_Runtime_CompilerServices_AsyncTaskMethodBuilder_Binding.Start_1_Neo`
(`ILRuntimeTestBase/AutoGenerate/...AsyncTaskMethodBuilder_Bi.cs:201`). That
stub reads the state machine as an `IAsyncStateMachineClassInheritanceAdaptor.
IAsyncStateMachineAdaptor` via `ReadNeoReference`. Under Neo the C#-emitted
state machine is a HEAP `ILTypeInstance` (dump-confirmed in
`CLRRedirectionsAsyncNeo.cs:28-34`), NOT a CLR adaptor -- so the cast yields
`null`, and the stub calls the real framework
`AsyncMethodBuilderCore.Start<TStateMachine>(ref null)` ->
`ArgumentNullException(Parameter 'stateMachine')`.

Observed stack (all 4 failures, identical):
```
System.ArgumentNullException: Value cannot be null. (Parameter 'stateMachine')
   at System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start[TStateMachine](TStateMachine& stateMachine)
   at ILRuntime.Runtime.Generated.System_Runtime_CompilerServices_AsyncTaskMethodBuilder_Binding.Start_1_Neo(...)
```

Legacy is unaffected: the autogen `Start_1` (`#else` branch) uses
`RetriveObject` + resolves the SM as a real adaptor, so it works.

## What (the fix)
Register the OPEN generic `Start` definition for the two non-generic builders
on `RedirectMapNeo`, pointing at the EXISTING `AsyncTaskMethodBuilder_Start_Neo`
/ `AsyncValueTaskMethodBuilder_Start_Neo` wrappers (which delegate to
`AsyncTaskMethodBuilder_T_Start_Neo` -- the proven generic-builder Start that
reads the SM byref from slot 1 and drives `MoveNext`). The non-generic `Start`
has the SAME CIL signature + 8-byte this-byref param layout as the generic
builder's, so the existing redirect handles it unchanged.

This uses the same first-registered-wins + `TryGetRedirection`-tries-
`GetGenericMethodDefinition()`-first lever already relied on by the generic
builder `Start` registration and child-22 `Activator.CreateInstance<T>`
(registering the open definition in the AppDomain ctor preempts every autogen
closed-generic stub).

## Scope
- Neo-gated (entire `CLRRedirections.AsyncNeo.cs` is `#if ENABLE_NEO_MODE`;
  registration uses `RegisterCLRMethodRedirectionNeo` -> the Neo-only map) ->
  Legacy-neutral by construction.
- Single file, additive (2 registration blocks; 0 removed lines).
- No JIT / optimizer / object-model / binding change.

## Verify (truth = full-smoke number)
- Name-filter `AsyncAwaitTest`: HEAD 13 ran / 4 failed (the C8 set, all ANE at
  Start) -> after fix 13 ran / 0 failed.
- FULL SMOKE: `114 -> 110` (4 flipped green; 0 regressions -- the 110 failure
  names contain no `AsyncAwait*` and no `NeoStep` test).
- NeoStep: `382 / 0` (no regression; matches documented baseline).
