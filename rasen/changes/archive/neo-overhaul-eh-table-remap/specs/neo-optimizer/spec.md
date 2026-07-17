## ADDED Requirements

### Requirement: LowerNeoOffsets Push-deletion MUST keep the exception-handler table consistent with the post-deletion body

The Neo optimizer pass `Optimizer.LowerNeoOffsets` SHALL keep the body-indexed
exception-handler table (`method.ExceptionHandlerRegister`: each
`Method.ExceptionHandler`'s `TryStart`, `TryEnd`, `HandlerStart`, and `HandlerEnd`)
consistent with the post-deletion `NeoExecuteBody`, by re-mapping those four indices
in lockstep with the existing branch/Switch/Leave target re-map on every Push
deletion. `ExecuteNeo` indexes into the post-deletion body, so a stale EH boundary
MUST NOT cause a thrown exception to miss its handler or route to a wrong handler.

`Optimizer.Neo.cs LowerNeoOffsets` is the sole Neo pass that changes instruction-body
LENGTH: it deletes the synthetic `Push` instructions the JIT emitted for the overflow
register arguments of a `Call` / `Callvirt*` / `Call_Redirect` / `Newobj` whose
parameter count exceeds the 3 instruction-embedded register slots. Each deletion
shifts every body index after it down by one. `FixBranchTargetsAfterRemove` already
re-maps the branch targets, `Leave`/`Leave_S`, intermediate branches, `Switch`
jump-table targets, and the `Symbols` map. The exception-handler table
(`method.ExceptionHandlerRegister`: the body-indexed `TryStart` / `TryEnd` /
`HandlerStart` / `HandlerEnd` of every `Method.ExceptionHandler`) MUST be re-mapped
by the same pass, so that `ExecuteNeo` -- which indexes into the post-deletion
`NeoExecuteBody` -- routes a thrown exception to the correct handler.

The re-map SHALL apply the identical rule used for branch targets: for each deleted
Push at `removedIndex`, every one of `TryStart`, `TryEnd`, `HandlerStart`, and
`HandlerEnd` that is strictly greater than `removedIndex` SHALL be decremented by
one. The re-map SHALL be applied at each deletion, inside the existing per-deletion
loop (in lockstep with the branch-target re-map), NOT as a single post-pass -- the
branch targets are re-mapped incrementally against the current-body frame at each
step, and the EH table MUST stay in that same frame to remain comparable. The
`ExceptionHandler[]` passed SHALL be `method.ExceptionHandlerRegister`, materialized
from the front-half `addr` map BEFORE the back-half runs (it is NULL during
`LowerNeoOffsets` today because `InitCodeBody` builds it after `Compile`). Methods
with no protected regions (`ehs == null`) and methods whose calls do not trigger
Push-deletion SHALL be untouched (the re-map is a no-op for them). This requirement
is Neo-only: it SHALL be a no-op for Legacy `ExecuteR` (which never runs
`LowerNeoOffsets`, never deletes Pushes). `Method.ExceptionHandler` has no
`FilterStart` field (IL filter blocks are unsupported); this requirement covers
exactly the four body-indexed fields.

#### Scenario: A throw inside a try routes to the correct catch after a Push-deletion shifts the body
- **WHEN** a Neo method has a `try`/`catch` whose try body contains a call with more
  than 3 register parameters, AND the call throws at runtime
- **THEN** the exception SHALL be delivered to the enclosing `catch` (the catch
  sentinel is observed), NOT propagated unhandled and NOT delivered to a wrong handler
- **AND** this SHALL hold because `TryStart` / `TryEnd` / `HandlerStart` /
  `HandlerEnd` were each decremented once for every Push deleted at an index before
  them, so the runtime throw address remains inside `[TryStart, TryEnd]`

#### Scenario: No-Throw and no-deletion methods are byte-identical to before
- **WHEN** a Neo method has protected regions but none of its calls trigger
  Push-deletion (no call exceeds 3 register parameters)
- **THEN** `FixBranchTargetsAfterRemove` SHALL never be invoked for that method
- **AND** its `ExceptionHandlerRegister` entries SHALL equal the CodeBody-order
  values built from `addr` (no re-map applied)

#### Scenario: Methods with no exception handlers are untouched
- **WHEN** `method.ExceptionHandlerRegister` is null (the method has no protected
  regions)
- **THEN** the EH-table re-map SHALL be a null-check no-op
- **AND** the existing branch / Switch / Leave / symbols re-map SHALL behave
  identically to before this change

#### Scenario: Generic-instance methods are covered on the CloneAndPatch path
- **WHEN** a generic-instance method is built via `CloneAndPatch`
  (`GenericMethodTemplate.cs`), which delta-shifts `addr` and then calls
  `RunNeoBackHalf` (deleting Pushes after the delta-shift)
- **THEN** the instance's `ExceptionHandlerRegister` SHALL be built from the
  delta-shifted `addr` BEFORE the back-half
- **AND** the per-deletion re-map SHALL fix the subsequent Push-deletion divergence,
  so a throwing >3-arg call inside a try in a generic method routes to its catch

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and runs the Legacy
  register VM
- **THEN** `LowerNeoOffsets` / `FixBranchTargetsAfterRemove` SHALL not exist (Neo-
  only) and the Legacy EH table SHALL be built and consumed exactly as before

#### Scenario: Regression guard fails on HEAD without the fix, passes with it
- **WHEN** the THROWING-EH NeoStep regression probe (a `try`/`catch` whose try body
  calls a >3-register-parameter method that throws, asserting the catch sentinel) is
  run on HEAD with the fix stashed
- **THEN** the probe SHALL fault (the throw escapes the catch through the stale EH
  boundary)
- **AND WHEN** the fix is applied
- **THEN** the probe SHALL pass, and the full NeoStep smoke SHALL remain green with
  no regression in `NeoStep14` (the EH step) or any other EH-touching test
