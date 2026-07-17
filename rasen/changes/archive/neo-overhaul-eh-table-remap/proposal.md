## Why

`Optimizer.Neo.cs LowerNeoOffsets` is the only Neo pass that changes instruction-
body LENGTH: it deletes synthetic `Push` instructions emitted for `Call`/`Newobj`
with more than 3 register parameters, and re-maps the body-indexed targets that
shift as a result. `FixBranchTargetsAfterRemove` already re-maps branch targets,
intermediate branches, `Switch` jump tables, `Leave`/`Leave_S`, and symbols after
each deletion (the sibling child-1 `neo-jit-bogus-opcode` fix added the `Leave`
arm). It does **NOT** re-map the exception-handler table (`method.ExceptionHandlerRegister`:
`TryStart`/`TryEnd`/`HandlerStart`/`HandlerEnd`, all body-indexed). After a Push
deletion those four indices stay stale (CodeBody-order) while `ExecuteNeo` indexes
into `NeoExecuteBody` (deletions applied), so a throw inside a try whose boundaries
were shifted past falls outside `[TryStart, TryEnd]` and the handler is missed
(`GetCorrespondingExceptionHandler`, `ILIntepreter.cs:5686`) -- the exception is
mis-routed or becomes unhandled. This is a **LATENT CORRECTNESS** landmine (child-1
flagged it explicitly), not triggered by the current 339-test NeoStep smoke because
no current probe combines a try/catch with a >3-arg call that THROWS. It must be
closed before any such pattern ships.

## What Changes

- **Re-map the EH table through the Push-deletion pass.** Extend
  `FixBranchTargetsAfterRemove` (`Optimizer.Neo.cs`) to also decrement
  `TryStart`/`TryEnd`/`HandlerStart`/`HandlerEnd` (rule identical to branch
  targets: value `> removedIndex` -> `value--`), applied **at each deletion, in
  lockstep** with the existing branch-target re-map (inside the deletion loop at
  `Optimizer.Neo.cs:1256`). Per-deletion (not once at the end) is load-bearing:
  branch targets are re-mapped incrementally, so the EH table must be too to stay
  in the same body-index frame.
- **Plumb the EH table to the optimizer.** Add an `ExceptionHandler[]` parameter
  to `LowerNeoOffsets` and to `FixBranchTargetsAfterRemove`. `RunNeoBackHalf`
  (`JITCompiler.cs:701`) already holds `this.method` and passes
  `method.ExceptionHandlerRegister`.
- **Fix the build ordering so the EH table exists when the optimizer runs.**
  Today `method.exceptionHandlerR` is built in `InitCodeBody` (`ILMethod.cs:959`)
  **after** `Compile` (`:916`), but `Compile`'s back-half (`RunNeoBackHalf` ->
  `LowerNeoOffsets`) runs first, so the table is NULL during the deletion pass.
  Extract the EH-build into an `ILMethod` helper and call it **before** the
  back-half in **both** funnels into `RunNeoBackHalf` -- direct JIT
  (`JITCompiler.cs:664`) and generic `CloneAndPatch` (`GenericMethodTemplate.cs:727`);
  make the existing `:959` build idempotent (skip if already built) so Legacy and
  non-EH paths are byte-identical.
- **THROWING-EH NeoStep regression probe.** A `try`/`catch` whose try body calls a
  >3-register-parameter method that THROWS; the catch sets a sentinel the probe
  asserts. Without the fix the stale EH boundaries mis-route the throw and the
  probe FAULTS (unhandled); with the fix it passes.
- All code changes are gated `#if ENABLE_NEO_MODE` (the deletion pass, `ExecuteNeo`,
  and the Neo JIT back-half are Neo-only), so the change is **Legacy-neutral by
  construction** (Legacy `ExecuteR` never runs `LowerNeoOffsets`, never deletes
  Pushes, and its EH table is untouched).

## Capabilities

### New Capabilities
<!-- None. -->

### Modified Capabilities
- `neo-optimizer`: ADD a requirement that `LowerNeoOffsets`' Push-deletion re-map
  MUST keep the body-indexed exception-handler table (`method.ExceptionHandlerRegister`:
  `TryStart`/`TryEnd`/`HandlerStart`/`HandlerEnd`) consistent with the post-deletion
  body, in lockstep with the existing branch/Switch/Leave target re-map. This is the
  same correctness class as child-1's `Leave`/`Leave_S` re-map fix in the same
  function. (The EH-dispatch logic itself -- `CheckExceptionType`, catch/finally
  semantics in `neo-exceptions` -- is unchanged; only the index-remap invariant in
  the optimizer is pinned.)

## Impact

- **`Optimizer.Neo.cs`**: `LowerNeoOffsets` gains an `ExceptionHandler[]` param;
  `FixBranchTargetsAfterRemove` gains the same param and a new EH-remap block.
  Behaviour for methods with no EH table (`ehs == null`) and no Push deletions is
  unchanged (the remap is a no-op).
- **`JITCompiler.cs`**: `RunNeoBackHalf` passes `method.ExceptionHandlerRegister`
  to `LowerNeoOffsets`; the EH table is built from `addr` before the back-half is
  invoked (Compile call site ~`:664`).
- **`GenericMethodTemplate.cs`**: the `CloneAndPatch` path (~`:727`) builds the EH
  table from the (delta-shifted) `addr` before calling `RunNeoBackHalf`, covering
  generic instances (same latent bug -- `RunNeoBackHalf` deletes Pushes after the
  delta-shift).
- **`ILMethod.cs`**: extract the EH-build (`:959-1000`) into a reusable helper;
  the `:959` site becomes idempotent (Neo back-half already populated it; Legacy
  still builds it there).
- **`TestCases/`**: new `NeoStep*EhTableRemap*` probe(s) (`NeoStep` name so the
  smoke filter picks them up).
- **No FilterStart**: `Method.ExceptionHandler` has only the four body-indexed
  fields; IL filter blocks are unsupported (`Endfilter` NIE), so no `FilterStart`
  is introduced or remapped.
- **NeoStep smoke**: 339/0 -> 340+/0 (new probe passes); `NeoStep14` (EH step)
  must NOT regress -- it exercises the new build-order path heavily.
