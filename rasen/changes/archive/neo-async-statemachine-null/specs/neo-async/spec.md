## MODIFIED (delta) -- Requirement: Neo async method builder redirection (synchronous-completion scope)

(Rationale: the requirement prose already names the non-generic
`AsyncTaskMethodBuilder` / `AsyncValueTaskMethodBuilder`, but the `Register()`
implementation omitted `Start<TSM>` for both non-generic builders, so `async
Task` / `async ValueTask` methods fell to the autogen `Start_1_Neo` stub ->
`ArgumentNullException('stateMachine')`. This delta pins the non-generic
`Start` registration as a SHALL and un-blocks the non-generic Task scenario.)

The Neo builder redirection SHALL register the `Start<TStateMachine>(ref TSM)`
open generic definition for ALL FIVE builder types on `RedirectMapNeo`,
INCLUDING the non-generic `AsyncTaskMethodBuilder` and
`AsyncValueTaskMethodBuilder` (not only the generic
`AsyncTaskMethodBuilder<T>` / `AsyncValueTaskMethodBuilder<T>` and
`AsyncVoidMethodBuilder`). The non-generic `Start` redirect SHALL route to the
SAME implementation as the generic builder's `Start` (the non-generic
`Start<TSM>` has an identical CIL signature and an identical 8-byte this-byref
param layout, so the existing `AsyncTaskMethodBuilder_T_Start_Neo` redirect --
which skips slot 0 (8-byte builder-this byref) and reads the SM byref from
slot 1 -- handles it unchanged). The non-generic `Start` registration SHALL
override (FIRST-registered-wins, open-generic-definition match via
`TryGetRedirection`'s `GetGenericMethodDefinition()` first-try) the autogen
`Start_1_Neo` stub, which reads the state machine as an
`IAsyncStateMachineAdaptor` (null under Neo -- the SM is a heap
`ILTypeInstance`) and calls the framework `AsyncMethodBuilderCore.Start(ref
null)` -> `ArgumentNullException(Parameter 'stateMachine')`.

#### Scenario: Non-generic AsyncTaskMethodBuilder.Start and AsyncValueTaskMethodBuilder.Start are Neo-redirected

- **WHEN** Neo mode executes an `async Task` (non-generic) or `async ValueTask`
  (non-generic) method, whose C#-lowered builder is the non-generic
  `AsyncTaskMethodBuilder` / `AsyncValueTaskMethodBuilder` struct
- **THEN** the builder's `Start<TSM>(ref sm)` SHALL be intercepted by the
  custom Neo redirect (registered as the open generic definition in the
  AppDomain ctor), the autogen `Start_*_Neo` stub SHALL NOT be invoked, and the
  state machine SHALL be driven through the Neo call machinery (fresh pooled
  interpreter, `MoveNext`, heap `ILTypeInstance` SM) WITHOUT throwing
  `ArgumentNullException(Parameter 'stateMachine')`
- (PROVEN -- `AsyncAwaitTest.TestRun/TestRun1/TestRun4/TestClass.Show1` flip
  green; full-smoke 114 -> 110.)

#### Scenario: Sync-completing async method returning Task (UN-BLOCKED)

- **WHEN** Neo mode executes an `async Task` method (non-generic) whose
  awaitables are all already complete (e.g. `await Task.CompletedTask`)
- **THEN** the redirects SHALL produce a completed `Task` (no `Result`), with
  the same `Start` -> `MoveNext` -> `SetResult` -> `get_Task` flow
- (PREVIOUSLY BLOCKED on `[NEO-CLRSTRUCT-FIELD-OF-IL]`; now reachable after the
  non-generic `Start` redirect registration. PROVEN by
  `AsyncAwaitTest.TestClass.Show1` -- `await Task.CompletedTask` sync path.)
