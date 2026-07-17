## Context

`Optimizer.Neo.cs LowerNeoOffsets` is the sole Neo pass that mutates instruction-
body LENGTH. For every `Call`/`Callvirt*`/`Call_Redirect`/`Newobj` whose register-
parameter count (`pCnt`, `HasThis` included for non-newobj calls) exceeds the 3
instruction-embedded register slots, the JIT emitted synthetic `Push` instructions
for the overflow args; `LowerNeoOffsets` deletes those Pushes (in-place shift +
`Array.Resize`) and calls `FixBranchTargetsAfterRemove(body, scanIdx, ...)` to fix
every body-indexed reference that the shift invalidated.

`FixBranchTargetsAfterRemove` (`Optimizer.Neo.cs:1703`) currently re-maps:
- branch targets + `Leave`/`Leave_S` (`op.Operand`, the `Leave` arm was the child-1
  `neo-jit-bogus-opcode` fix),
- intermediate branching (`op.Operand4`),
- `Switch` jump-table targets (`frame.SwitchTargets`),
- the `Symbols` map keys.

It does **not** re-map the exception-handler table. `Method.ExceptionHandler`
(`CLR/Method/ExceptionHandler.cs:16`) holds four body-indexed ints -- `TryStart`,
`TryEnd`, `HandlerStart`, `HandlerEnd` -- plus `HandlerType`/`CatchType`. There is
**no `FilterStart`** (IL filter blocks are unsupported: `Endfilter` throws a Step-14
NIE at `ILIntepreter.Neo.cs:5784`, and `InitCodeBody:995` throws NIE for
`ExceptionHandlerType.Filter`). The array lives on `ILMethod.exceptionHandlerR`
(field `:30`, getter `ExceptionHandlerRegister :182`).

`ExecuteNeo` consumes it as: `var ehs = method.ExceptionHandlerRegister`
(`ILIntepreter.Neo.cs:1433`); on a throw, `HandleException` ->
`GetCorrespondingExceptionHandler` (`ILIntepreter.cs:5686`) scans
`addr >= i.TryStart && addr <= i.TryEnd` (INCLUSIVE on both ends; `TryEnd` is stored
as `addr[eh.TryEnd] - 1`), picks the closest enclosing matching handler, and jumps to
`eh.HandlerStart` (`HandleException` returns `jumpTarget = eh.HandlerStart`;
`Leave`/`Endfinally` also index `eh.HandlerStart` at `Neo:5750/5774`).

The `addr` passed to `GetCorrespondingExceptionHandler` is the runtime
`(int)(ip - ptr)` into `NeoExecuteBody` (post-deletion). The EH-table indices are
`addr[...]` values from the front-half -- i.e. **CodeBody-order**. After
`LowerNeoOffsets` deletes Pushes, CodeBody-order and NeoExecuteBody-order diverge
exactly at the deleted positions, so the stale EH boundaries point one-too-high (per
deletion before them) and a throw at the (correct, runtime) `addr` can fall outside
`[TryStart, TryEnd]` -> the handler is missed -> the exception propagates unhandled
or lands in the wrong handler. Latent today (no NeoStep probe combines try/catch +
>3-arg throwing call), explicitly flagged by child-1.

Sibling precedent: child-1 (`neo-jit-bogus-opcode`, capability `neo-optimizer`) added
the `Leave`/`Leave_S` arm to this same `FixBranchTargetsAfterRemove`. This change
adds the EH-table arm to the same function -- same correctness class.

## Goals / Non-Goals

**Goals:**
- Keep the EH table's four body-indexed fields consistent with the post-deletion
  `NeoExecuteBody`, so EH dispatch routes throws to the correct handler whenever a
  method simultaneously has protected regions (try/catch/finally) AND >3-arg
  calls/newobj that trigger Push-deletion.
- A THROWING-EH NeoStep probe that FAULTS on HEAD (stash-toggle confirmed) and
  passes after the fix.
- NeoStep smoke stays green (339/0 -> 340+/0); `NeoStep14` (the EH step) and all
  other EH-touching tests must not regress.
- Legacy-neutral (Neo-gated).

**Non-Goals:**
- IL filter blocks (`filter`/`endfilter`) -- out of scope (Step-14 NIE remains).
- Changing EH-dispatch semantics (`CheckExceptionType`, catch/finally ordering,
  fault handling) -- untouched.
- Re-mapping any structure other than the four EH fields (branch/Switch/Leave/
  symbols already handled).
- The NeoStep-24 exit-0 quirk or any other unrelated optimizer item.

## Decisions

### D1 -- Re-map the EH table incrementally, in lockstep with branch targets (per-deletion, inside the loop)

**Decision:** add an `ExceptionHandler[] ehs` parameter to
`FixBranchTargetsAfterRemove` and, at the end of that function (after the
branch/intermediate/Switch/symbols blocks), loop over `ehs` and decrement each of
`TryStart`/`TryEnd`/`HandlerStart`/`HandlerEnd` that is `> removedIndex`. Null
`ehs` (no protected regions) is a no-op. The call at `Optimizer.Neo.cs:1256`
already lives inside the per-deletion loop, so this runs once per deleted Push --
identical cadence to the branch-target re-map.

**Why per-deletion and not once at the end:** `FixBranchTargetsAfterRemove` is
called with `removedIndex = scanIdx`, which is a **current-body-order** index (the
body has already had prior deletions applied). The branch targets it re-maps are
ALSO current-body-order at that moment, because they were re-mapped by every prior
call. This incremental invariant is what keeps `op.Operand > removedIndex`
frame-consistent. The EH table starts in CodeBody-order (== initial body order), so
if it is re-mapped at every deletion too, it stays in the same current-body frame as
`scanIdx` and ends in final `NeoExecuteBody`-order. A single post-pass re-map would
have to convert the per-step `scanIdx` values (current-body-order) back to CodeBody-
order, which is an online order-statistics computation (verified: naively sorting
the recorded `scanIdx` values does NOT recover CodeBody-order once a single Call
deletes multiple Pushes while the next Call's deletions interleave). Re-using the
existing per-deletion hook eliminates that frame conversion entirely.

**Alternative considered (rejected): record removed indices, remap the EH table
once after `LowerNeoOffsets`.** Rejected because converting the recorded
current-body-order `scanIdx` sequence to CodeBody-order removed-index set is the
fiddly order-statistics problem above, and a post-hoc "walk CodeBody vs
NeoExecuteBody skipping Pushes" map relies on the assumption that NO Push survives
`LowerNeoOffsets` (true today but not load-bearing-guaranteed). The per-deletion
approach mirrors the proven branch-target path with zero novel index math.

### D2 -- Build the EH table BEFORE the back-half (the ordering fix)

**Decision:** `method.exceptionHandlerR` is NULL while `LowerNeoOffsets` runs
(`InitCodeBody` builds it at `:959-1000`, AFTER `Compile` at `:916`; `Compile`'s
tail is `RunNeoBackHalf` -> `LowerNeoOffsets`). So the table MUST be materialized
from `addr` before the back-half. Concretely:

1. Extract the body of `InitCodeBody:959-1000` (the `for` over
   `def.Body.ExceptionHandlers` building `ExceptionHandler` from `addr`) into a new
   `ILMethod` helper, e.g. `BuildExceptionHandlerRegister(Dictionary<Instruction,int>
   addr)`, gated `#if ENABLE_NEO_MODE` for the Neo(Register) target (it writes
   `exceptionHandlerR`). Keep the Legacy `exceptionHandler` build where it is.
2. Call the helper before the back-half at BOTH funnels into `RunNeoBackHalf`:
   - direct JIT: inside `Compile` (`JITCompiler.cs`, just before the `RunNeoBackHalf`
     call at `:664`) -- `Compile` has the `addr` parameter and `this.method`.
   - generic instance: inside `CloneAndPatch` (`GenericMethodTemplate.cs:727`),
     just before `jit.RunNeoBackHalf` -- `CloneAndPatch` has `addr` and `instance`.
3. Make the `:959` site **idempotent**: `if (exceptionHandlerR == null) { build }`.
   For the Neo path the back-half already populated it, so `:959` skips; for any path
   that reaches `:959` without a back-half (Legacy register mode, or a future path)
   it still builds. Legacy/non-Neo behaviour is byte-identical.

**Why both funnels:** `RunNeoBackHalf` is the common tail, but it has no `addr`
parameter (signature is `(ref CompiledFrame frame, List<OpCodeR> res, short
locVarRegStart, int totalRegCnt, short neoCatchExRegFinal)`). Adding `addr` to
`RunNeoBackHalf` would be the alternative, but building the EH table at the two
KNOWN call sites (which already hold `addr` + the `ILMethod`) keeps
`RunNeoBackHalf`'s signature focused on frame lowering and avoids threading `addr`
through a frame-only API.

**Why the generic path needs it too:** `GenericMethodTemplate.cs:677-717` shifts
branch/Leave/intermediate Operands, `SwitchTargets`, and `addr` values by `delta`
(the Initobj-prefix resize), then calls `RunNeoBackHalf` at `:727`. The generic
INSTANCE's EH table is built from ITS (delta-shifted) `addr` -- so after the delta
shift the EH indices are correct for the pre-deletion body, but `RunNeoBackHalf`
then deletes Pushes, re-introducing the same divergence. Building the EH table from
the delta-shifted `addr` before `:727` lets the per-deletion re-map fix it
identically to the direct path. (The delta-shift itself does NOT need an EH arm:
`addr` is shifted wholesale at `:710`, so EH built from it is delta-correct; only
the subsequent Push-deletion needs the re-map.)

### D3 -- Plumbing signature

`LowerNeoOffsets(ref CompiledFrame frame, AppDomain domain)` ->
`LowerNeoOffsets(ref CompiledFrame frame, AppDomain domain, ExceptionHandler[] ehs)`.
`RunNeoBackHalf` passes `method.ExceptionHandlerRegister` (now non-null after D2).
`FixBranchTargetsAfterRemove(...)` gains the trailing `ExceptionHandler[] ehs` param.
All Neo-gated.

### D4 -- The probe must FAULT (child-1/child-2 discipline)

The NeoStep pass criterion is "ran without throwing". A probe that merely produces a
wrong value will NOT fail the smoke. So the probe MUST construct the trigger such
that, without the fix, the exception is mis-routed and propagates unhandled out of
the probe method (the harness then records a failure). Shape:

```
// >3 register params -> JIT emits Push; LowerNeoOffsets deletes it; EH boundary
// goes stale; the throw mis-routes past the catch -> probe faults on HEAD.
static int ThrowIfEq(int a, int b, int c, int d, int e)
{
    if (a == 1) throw new InvalidOperationException("boom");
    return -1;
}
public static int NeoStepEhTableRemap_TC1()
{
    try { ThrowIfEq(1, 2, 3, 4, 5); return 0; }
    catch (Exception) { return 42; }
}
```
Assert `NeoStepEhTableRemap_TC1() == 42`. Without the fix the throw escapes the
catch (stale `TryStart`/`TryEnd`) -> unhandled -> the probe faults. A second probe
(TC2) with the throwing call NOT first in the try (a no-op statement before it)
guards the off-by-one direction and a finally+catch variant guards `HandlerStart`/
`HandlerEnd`. The exact param count / placement is tuned at apply time so the
stash-toggle confirms a FAULT on HEAD (5-6 params gives a larger index shift and a
more robust fault than the minimal 4). If the minimal shape does not fault on HEAD
(the shift might land benignly for some layouts), increase the param count and/or
add statements so the deleted Push sits before the try boundary -- the stash-toggle
is the gate, not the prose.

### D5 -- Capability home: `neo-optimizer`

The fix SITE is `LowerNeoOffsets` / `FixBranchTargetsAfterRemove` (Optimizer.Neo.cs)
-- the same function and capability child-1 used for the `Leave` re-map. The
EH-build extraction in `ILMethod.cs` is plumbing that feeds the table to the
optimizer; it does not change EH-dispatch behaviour (that is `neo-exceptions`,
untouched). So the new requirement is pinned on `neo-optimizer`.

## Risks / Trade-offs

- **[Risk] The build-order move changes EH construction for EVERY Neo method with
  protected regions, not just the buggy ones.]** -> Mitigation: the extracted helper
  is a verbatim move of the `:959-1000` body; the `:959` site becomes idempotent
  (`if (exceptionHandlerR == null)`); methods with no Push-deletion hit a no-op
  re-map (no deletions -> `FixBranchTargetsAfterRemove` never called -> EH
  untouched). `NeoStep14` (the EH step) and the full NeoStep smoke are the
  regression net -- they MUST stay green.
- **[Risk] The probe might not fault on HEAD for a given param count/placement (the
  stale boundary could still enclose the throw by luck).]** -> Mitigation: the
  stash-toggle is the gate; tune param count (use 5-6) and statement ordering until
  HEAD faults. If no shape faults, STOP and re-investigate (do not ship an
  unverified probe -- per child-1/child-2, a non-faulting probe is worthless).
- **[Risk] The generic `CloneAndPatch` path builds EH from a delta-shifted `addr`;
  getting the build-vs-delta-shift order wrong double-shifts.]** -> Mitigation: build
  EH AFTER the `:677-717` delta-shift block (which already finalizes `addr`), before
  `:727`. The direct-JIT path has no delta. Both paths then feed the same
  (correct-for-pre-deletion-body) table to the per-deletion re-map.
- **[Risk] A `Method.ExceptionHandler` index that legitimately equals `removedIndex`
  is left un-decremented (the rule is strictly `> removedIndex`).]** -> This is
  correct and matches the branch-target rule exactly: the instruction AT
  `removedIndex` is the one being deleted (it is a synthetic `Push`, never a try/
  handler boundary), so no EH boundary can equal it; boundaries strictly after it
  shift down by one. No special-casing needed.
- **[Trade-off] Two EH-build call sites (Compile + CloneAndPatch) instead of one.]**
  -> Accepted: both already hold `addr` + the `ILMethod`, and centralizing in
  `RunNeoBackHalf` would force an `addr` parameter onto a frame-only API. The
  extracted helper is the single source of truth for HOW to build.
