## ADDED Requirements

### Requirement: Neo async builder Start drives MoveNext via the Neo call convention (not the CLR interface)

The Neo async `Start<TSM>(ref sm)` builder redirection (Step 20) SHALL invoke
the compiler-generated state machine's `MoveNext` IL method through the Neo
call convention as an in-frame IL value-type instance method (Step 12 / Step
17 frame-native Ref Slot `this`), reusing the same in-frame value-type
instance-call dispatch the Step 8/12b machinery provides. The redirect SHALL
NOT dispatch `MoveNext` through the CLR `IAsyncStateMachine` interface, SHALL
NOT create or consult a `CrossBindingAdaptor`, and SHALL NOT allocate a fresh
pooled interpreter for the synchronous path (`Start` runs on the caller's own
`ExecuteNeo` frame). The async resumption path (deferred to
`neo-step20-async-suspend`) SHALL reuse the Step 19
`DelegateAdapter.InvokeILMethod` / `NeoInvokeSub` fresh-pooled-interpreter
frame-build pattern (build at `StackBase`, write `this` + params,
`ExecuteNeo`, read return, `FreeILIntepreter` in `finally`) when a hoisted
state machine is resumed on a CLR continuation thread that has no in-flight
Neo frame.

#### Scenario: Start invokes MoveNext in-frame

- **WHEN** the Neo `Start<TSM>(ref sm)` builder redirect executes for a
  sync-completing async method
- **THEN** `MoveNext` SHALL be invoked as an in-frame IL value-type instance
  method on the caller's `ExecuteNeo` frame, with `this` supplied as a
  frame-native Ref Slot addressing the state-machine local, and the call SHALL
  NOT route through any CLR interface, adaptor, or fresh interpreter

#### Scenario: Async resumption reuses the Step 19 fresh-interpreter pattern

- **WHEN** a hoisted state machine is resumed on a CLR continuation callback
  (the deferred suspend path), where the callback thread has no in-flight Neo
  frame
- **THEN** the resumption entry SHALL build a Neo frame at a fresh pooled
  interpreter's `StackBase` (the `NeoInvokeSub` shape from Step 19), write the
  hoisted state machine as `this`, run `ExecuteNeo` on the `MoveNext` IL
  method, read the result, and return the interpreter to the pool on every
  exit path (`FreeILIntepreter` in `finally`), mirroring the Step 19
  `DelegateAdapter.InvokeILMethod` Neo lifecycle

#### Scenario: Builder redirect overrides the autogen stub

- **WHEN** the Neo mode builder redirections are registered for
  `AsyncTaskMethodBuilder<T>` (and the other builder types)
- **THEN** the custom Neo redirects SHALL override (last-wins) the autogen
  `*Neo` builder stubs for the same `MethodBase`s, so the in-frame
  `Start`→`MoveNext` path is the active implementation and the CLR-interface-
  dispatching autogen stubs are NOT invoked
