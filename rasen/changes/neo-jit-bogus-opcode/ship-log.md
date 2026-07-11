# Ship Log — neo-jit-bogus-opcode (neo-overhaul child 1)

**Date:** 2026-07-11  **Pipeline:** auto-decompose -> small-feature  **Tier:** A
**Delivery:** local commit + push (user directive: commit+push after each clean child).
**Outcome:** SHIPPED. NeoStep **306/0** (301 baseline + 5 probes). Legacy-neutral.

## What shipped
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` — `FixBranchTargetsAfterRemove`:
  remap `Leave`/`Leave_S` `Operand` (the EH control-flow target) after a `Push`-deletion, the
  ONE target category the helper missed (branches / intermediate / Switch were handled). This
  is the root cause of the `opcode 2359324` garbage-opcode failure.
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` — permanent `ExecuteNeo`
  dispatch guard (defense-in-depth): (a) cached `NeoOpCodeCount`; (b) loop-head bounds check
  BEFORE the `ip->Code` deref so an overrun never reads OOB; (c) `default:` out-of-range-`Code`
  check that dumps the register/operand fields (named-but-unimplemented opcodes like `ldtoken`
  still fall through to the existing Step-6 message).
- `TestCases/NeoStepBogusLeaveTest.cs` — 5-TC regression probe; `TC_RET5_Return5ArgRef` is the
  deterministic detector.

## Root cause (H1, confirmed via instrumented full-smoke dump)
Every `2359324` hit was an **overrun** (`idx == body.Length`) in async state-machine
`MoveNext` (`AsyncAwaitTest/<TestRun2|3>d__N`). `LowerNeoOffsets` deletes synthetic `Push`
instructions for Call/Newobj with >3 register params and re-maps control-flow targets via
`FixBranchTargetsAfterRemove`; that helper remapped branches/intermediate/Switch but NOT
`Leave`/`Leave_S`. The deletion shifted indices below a Leave target, which then pointed past
the shortened body -> `ip` overran -> `ip->Code` read adjacent heap = `0x23F70C`. H2
(un-terminated body) and H3 (corrupt in-range slot) REFUTED by the dump.

## Evidence
- **Stash-toggle (reviewer-independent, Optimizer.Neo.cs-only, guard kept):** FAIL-on-HEAD ->
  `Neo: ip ran past body end in ...TC_RET5_Return5ArgRef() at index 12/11` (1/5 failed);
  restore -> 5/5 PASS. Exact match to implementer's claim.
- **NeoStep smoke:** 306 ran, 0 failed (reviewer re-ran; LEAD re-confirmed after F-2/F-3 polish).
- **Legacy-neutral:** both changed source files are entirely inside `#if ENABLE_NEO_MODE`;
  plain `Debug` compiles them out (Legacy binary byte-identical).

## Review verdict
APPROVE-WITH-FINDINGS (reviewer != implementer). 0 Blocker; 1 Major PRE-EXISTING/deferred;
2 Minor FIXED inline by LEAD (F-2: guard moved before the `ip->Code` deref; F-3: contiguity
invariant documented on `NeoOpCodeCount`); 1 Trivial accepted-known (Leave Operand ==
removedIndex vacuous edge).

## Deferred / surfaced (new portfolio children)
- **F-1 -> `neo-overhaul-eh-table-remap`:** `method.ExceptionHandlerRegister`
  (TryStart/TryEnd/HandlerStart/HandlerEnd, body-indexed) is NOT remapped by `LowerNeoOffsets`
  Push-deletion. Trigger: try/catch/finally + >3-arg call/newobj that THROWS -> mis-routed
  exception. Same family as this fix. Pre-existing (not introduced here), NOT triggered by the
  current 306 smoke (no throwing EH + multi-arg-call method), so deferred to its own child with
  a THROWING-EH probe rather than risk a blanket LowerNeoOffsets rewrite.
- async `Task` is blocked by an unrelated `AsyncTaskMethodBuilder.Start` ArgumentNullException
  (byref state-machine marshalling) -> child-6 `neo-valuetask-marshal` territory; async is not
  usable as a NeoStep regression probe yet, so synchronous try/catch + >3-arg-call probes are
  the viable detector pattern.
