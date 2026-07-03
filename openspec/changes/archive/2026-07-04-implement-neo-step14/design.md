# Design -- implement-neo-step14 (Neo Exception Handling)

Ports Legacy try/catch/finally semantics (reference: `ExecuteR` in
`ILIntepreter.Register.cs` + shared `HandleException` /
`GetCorrespondingExceptionHandler` / `FindExceptionHandlerByBranchTarget` in
`ILIntepreter.cs`) into the Neo `byte*` frame model in `ExecuteNeo`
(`ILIntepreter.Neo.cs`).

Conventions: `frameBase` = esp at entry; `newEsp = frameBase + frameSize`;
`frameRefBase` = `mStack.Count` at entry, then `totalRefSize` nulls reserved
(`ILIntepreter.Neo.cs:322-343`). `ehs = method.ExceptionHandlerRegister`
(`ILMethod.cs:114, 737-761`; the SAME `ExceptionHandler[]` Legacy uses -- shared
lowering, so Neo and Legacy see identical try/handler ranges + CatchType).

## 1. Existing scaffolding (already in place)

`ExecuteNeo` already wraps the dispatch switch in a per-iteration C# try-catch
and calls the SHARED `HandleException`:

```
ILIntepreter.Neo.cs:360-362   int finallyEndAddress = 0;
                              Exception lastCaughtEx = null;
                              var ehs = method.ExceptionHandlerRegister;
ILIntepreter.Neo.cs:2025-2058 catch (Exception ex) {
    var oriESP = (StackObject*)newEsp;
    StackObject* tmpEsp = oriESP;
    bool isJmp = HandleException(ex, ref tmpEsp, ehs, method,
        (int)(ip - ptr), ref frame, ref lastCaughtEx,
        ref unhandledException, ref finallyEndAddress,
        out int jmpTarget, out bool isCatch);
    if (isCatch) {
        int targetCount = frameRefBase + totalRefSize;
        if (mStack.Count > targetCount)
            mStack.RemoveRange(targetCount, mStack.Count - targetCount);
        // TODO: write exception object into the catch handler's slot (Step 14)
    }
    if (isJmp) { ip = ptr + jmpTarget; continue; }
    if (unhandledException) { throw; }
    unhandledException = true; returned = true;
    ... throw ILRuntimeException ...
}
```

So the handler-matching engine and the mStack truncation-on-catch already exist
and are LIVE (not dead). The gaps are: (a) no `Throw`/`Leave`/`Endfinally`/
`Rethrow` arms (they hit `default` -> NIE), (b) the catch-object write is a TODO,
(c) cross-frame propagation is broken (section 6).

## 2. The `Throw` arm

Legacy register Throw (`ILIntepreter.Register.cs:5307-5312`) reads the exception
from the throw register's mStack slot and throws it:

```
objRef = GetObjectAndResolveReference((r + ip->Register1));
var ex = mStack[objRef->Value] as Exception;
throw ex;
```

JIT lowers `Code.Throw` to `OpCodeREnum.Throw` with `op.Register1 = --baseRegIdx`
(`JITCompiler.cs:1860-1862`); the Neo body is a clone of the same `OpCodeR[]`
(`JITCompiler.cs:465`), so the opcode and `Register1` are present. In the Neo
frame model, `Register1` is a byte offset into `frameBase` holding the ref-slot
mStack index of the exception object. Neo arm:

```
case OpCodeREnum.Throw:
{
    int exIdx = *(int*)(frameBase + ip->Register1); // ref-slot index, -1 = null
    Exception ex = GetNeoException(mStack, exIdx);   // see helper below
    throw ex;  // into the outer try-catch
}
```

`GetNeoException(mStack, idx)`: `idx < 0` -> throw a CLR `NullReferenceException`
(throwing null is itself an NRE in the CLR). Else read `mStack[idx]`; if it is an
`ILRuntimeException` (an IL-thrown exception already wrapped by a deeper frame),
the shared `HandleException` unwraps it (`ILIntepreter.cs:4750-4758`) so the arm
can throw it as-is. Otherwise it must be a CLR `Exception` (e.g. the
`DivideByZeroException` produced by `Div`) -- throw directly.

(Reuse `GetNeoILInstance`'s null-guard idiom at `ILIntepreter.Neo.cs:2085-2088`,
generalized to `object` / `Exception`.)

## 3. The `Leave` / `Leave_S` arm

Legacy (`ILIntepreter.Register.cs:2765-2783`): if any finally encloses the
current address whose range straddles the leave boundary, jump into that finally
first (setting `finallyEndAddress = leave target`); otherwise jump straight to
the leave target. Neo arm is the same logic, because `ip->Operand` is the leave
target byte offset and `ehs` is the shared table:

```
case OpCodeREnum.Leave:
case OpCodeREnum.Leave_S:
{
    if (ehs != null)
    {
        int addr = (int)(ip - ptr);
        var eh = FindExceptionHandlerByBranchTarget(addr, ip->Operand, ehs);
        if (eh != null)
        {
            finallyEndAddress = ip->Operand;
            ip = ptr + eh.HandlerStart;
            continue;
        }
    }
    ip = ptr + ip->Operand;
    continue;
}
```

This guarantees finally runs on every Leave (try-with-return, try-with-break,
etc.) exactly as Legacy does. `FindExceptionHandlerByBranchTarget`
(`ILIntepreter.cs:4832-4845`) is reused unchanged.

## 4. The `Endfinally` arm

Legacy (`ILIntepreter.Register.cs:2785-2806`):

```
case OpCodeREnum.Endfinally:
{
    if (finallyEndAddress < 0)            // finally entered for an in-flight exception
    {
        unhandledException = true;        // (flag -- see note)
        finallyEndAddress = 0;
        throw lastCaughtEx;               // re-propagate the exception
    }
    int addr = (int)(ip - ptr);
    var eh = FindExceptionHandlerByBranchTarget(addr, finallyEndAddress, ehs);
    if (eh != null) { ip = ptr + eh.HandlerStart; continue; } // outer finally next
    ip = ptr + finallyEndAddress; finallyEndAddress = 0; continue; // done, jump to leave target
}
```

The `finallyEndAddress < 0` sentinel means "this finally was entered because an
exception was thrown, not because of a Leave" (set in `HandleException:4800`,
`finallyEndAddress = -1`). On Endfinally of such a block, we re-throw
`lastCaughtEx` so the outer try/catch search resumes. Note the `unhandledException = true`
line in Legacy is a defensive flag; the actual re-propagation is the `throw`,
which re-enters this frame's outer catch and re-runs `HandleException`. Neo
mirrors this verbatim (the logic is frame-model independent; only `ip` arithmetic
differs, and Neo already uses the same `ptr`-relative offsets).

## 5. The `Rethrow` arm

Legacy (`ILIntepreter.Register.cs:5313-5314`): `throw lastCaughtEx;`. Neo arm is
identical (one line). `lastCaughtEx` is the per-frame field already declared at
`ILIntepreter.Neo.cs:361`.

## 6. Catch-handler entry: exception-object storage (the TODO)

Legacy register mode writes the caught object into the catch block's exception
local register (`ILIntepreter.Register.cs:5326-5327`):

```
short exReg = (short)(paramCnt + locCnt);   // catch-handler's exception register
AssignToRegister(ref info, exReg, ex);
```

In Neo, the catch handler's exception variable occupies a ref slot in this
frame's reserved region. The mStack truncation is already done
(`ILIntepreter.Neo.cs:2033-2037`). The remaining TODO is to store `ex` into that
slot. Two resolution options for "which ref slot":

- **Option A (preferred, robust):** extend `HandleException`'s Neo call to also
  return the matched `ExceptionHandler` (or its handler-start address). The JIT
  already emits, as the first instruction(s) of a catch handler body, the move
  that materializes the exception local into its ref slot from the throwing
  position -- OR, more directly, store `ex` into `mStack[frameRefBase +
  totalRefSize-1]` style is unsafe. The clean path: the shared `HandleException`
  knows `eh.HandlerStart`; the Neo caller can look up the catch handler's
  declared exception variable via the handler's metadata and write `ex` into the
  frame ref slot the JIT reserved for it.

- **Option B (minimal, matches Legacy):** since `HandleException` already
  computes the jump, the Neo caller needs the catch-handler's exception ref-slot
  offset. Add a tiny helper `GetCatchHandlerExceptionSlot(ehs, handlerStart,
  method)` that returns the ref-slot offset the JIT allocated for the catch
  variable, then in the `isCatch` branch:
  ```
  int slotOff = GetCatchHandlerExceptionSlot(ehs, jmpTarget, method);
  int slotIdx = frameRefBase + slotOff;
  mStack[slotIdx] = ex;
  *(int*)(frameBase + <ex-local byte offset>) = slotIdx;
  ```
  (the byte-offset write mirrors how every ref-typed local is bound in Neo,
  `ILIntepreter.Neo.cs:333-336`).

Concrete approach for the implementer: confirm how the Neo JIT reserves the catch
exception local (inspect `JITCompiler` catch-handler prologue lowering + the
`StackSlotInfo` for the exception var), then write `ex` into that exact slot in
the `isCatch` branch. The implementer should verify against Legacy
`AssignToRegister(ref info, exReg, ex)` semantics -- the register `exReg =
paramCnt + locCnt` corresponds to the catch-handler's first local, which in Neo
is the catch variable's reserved ref slot.

If the JIT does NOT currently reserve a distinct slot for the catch variable
(possible -- exception-handlers may share the variable-pool), the fallback is:
push `ex` onto mStack at the catch-handler's expected position
(`mStack[targetCount] = ex; targetCount++` style), matching how Legacy
`PushObject(esp, mStack, ex)` (`ILIntepreter.cs:4698`) pushes it onto the
evaluation stack and the handler reads it from its input register.

## 7. Cross-frame propagation (the structural bug)

Today, when a callee IL method finishes with `unhandledException = true`
(i.e. an exception escaped the callee and was not caught there), every
`Call`/`Newobj`/`Callvirt_IL`/`Callvirt_Interface` arm in the CALLER does:

```
if (!InvokeNeoCallTarget(..., out unhandledException))
    return null;     // ILIntepreter.Neo.cs:1396-1397, 1433-1434, 1457, 1503, 1527
```

`return null` bypasses BOTH (a) the caller's outer try-catch (so the caller's
enclosing try/catch never gets a chance to catch the propagated exception) and
(b) the bottom-of-method cleanup (`ILIntepreter.Neo.cs:2062-2071` frame-pop +
mStack restore) -- leaving the caller's frame on the frames stack and the
caller's mStack reservation un-truncated. This is wrong on two counts: the
exception is not catchable by the caller, and frame/mStack state leaks.

Legacy solves this WITHOUT a per-call `return null`: in Legacy, when a callee's
exception is unhandled, the SHARED `HandleException` walks the frames stack
itself (`ILIntepreter.cs:4770-4779`) and pops intervening frames until it reaches
the frame whose `ehs` contains a matching handler:

```
while (stack.Frames.Peek().BasePointer != frame.BasePointer)
{
    var f = stack.Frames.Peek();
    esp = stack.PopFrame(ref f, esp);
    ...
}
```

For Neo this frame-pop loop is what makes cross-frame catch work: a thrown
exception in a callee propagates up, `HandleException` is entered in the DEEPEST
frame that has the outer try-catch still on the C# stack, and it pops the
intervening Neo frames. But Neo re-enters `ExecuteNeo` recursively per call (each
call = its own C# stack frame + its own outer try-catch), so the propagation
model must be:

**Design (cross-frame):** When `InvokeNeoCallTarget` returns
`unhandledException = true` from an IL callee, the caller arm must RE-THROW the
pending exception into ITS OWN outer try-catch so THIS caller's `ehs` gets a
chance to match -- instead of `return null`. Concretely:

1. `ExecuteNeo` needs to recover the in-flight exception object. Today the callee
   swallows it into `unhandledException` (a bool) and the object is lost.
   Extend the callee->caller return to carry the exception object: change
   `InvokeNeoCallTarget`'s contract (or add an `out Exception pendingEx`) so the
   caller can `throw pendingEx`. The callee, on the unhandled path
   (`ILIntepreter.Neo.cs:2045-2057`), already has `ex` -- return it instead of
   only flipping the bool.
2. In each call arm, replace `return null` with: restore this caller's
   `ip` to the call instruction (so `(int)(ip - ptr)` points at the call, which
   is inside any enclosing try range), then `throw pendingEx`. The outer catch
   then runs `HandleException` against THIS frame's `ehs` and the call's address
   -- which is exactly the Legacy semantics (the exception "happened at the call
   site").
3. The shared `HandleException` frame-pop loop (`4770-4779`) then handles the
   case where neither this frame nor any frame below has a matching handler: it
   pops down to the deepest matching frame. Because Neo frames are pushed with
   `frame.BasePointer = (StackObject*)frameBase` (`ILIntepreter.Neo.cs:350`), the
   `BasePointer`-equality comparison works across Neo frames exactly as it does
   for Legacy frames -- provided the intervening Neo frames have been left in a
   consistent state (their `mStack` already truncated to their `frameRefBase` at
   the bottom of their own `ExecuteNeo`, which runs when they `throw` out).

The implementer must verify the mStack discipline during a multi-frame unwind:
each Neo `ExecuteNeo` invocation's bottom cleanup (`2062-2071`) truncates mStack
to ITS `frameRefBase`. If the C# `throw` propagates out of a Neo `ExecuteNeo`
that did NOT run its bottom cleanup (because it `throw`re from inside the catch
block at `2047`), the cleanup is skipped. To keep mStack correct, the unhandled
path must ensure cleanup runs -- either by routing through the bottom cleanup on
every exit (including the `throw;` rethrow at `2047`), or by having the parent
frame's `HandleException` frame-pop loop truncate mStack as it pops each Neo
frame (use each popped `StackFrame.ManagedStackBase` = that frame's
`frameRefBase`, `ILIntepreter.Neo.cs:353`).

**Simplest correct approach:** in the unhandled re-throw path, before `throw;`
(`2047`), do NOT skip cleanup -- instead `throw` AFTER the method has truncated
its own mStack/popped its own frame. Restructure so the bottom cleanup
(`2062-2071`) runs on ALL exit paths (normal return, rethrow-unhandled), then if
unhandled, re-throw outside the `fixed` block. This makes every Neo frame
self-cleaning, and the parent's `HandleException` frame-pop loop becomes a
no-op for already-cleaned frames (it just won't find them on the frames stack).

## 8. Frames-stack discipline

Per the roadmap: "do NOT Pop on unhandled exception; `HandleException` finds the
matching handler then batch-Pops." This is satisfied by sections 6-7:
`HandleException`'s frame-pop loop (`4770-4779`) is the batch-pop. The Neo
per-iteration catch must NOT pop the frame itself; only the bottom-of-method
cleanup (`2062-2071`) pops this frame, and only on actual method exit.

## 9. Nested try/catch

Already handled by the shared `GetCorrespondingExceptionHandler` nearest-match
algorithm (`ILIntepreter.cs:5598-5622`): among all handlers whose `[TryStart,
TryEnd]` contains `addr` and whose `CatchType` matches, it picks the one with the
smallest `addr - TryStart` (innermost). No Neo-specific work; reuse as-is.

## 10. Exception in finally / rethrow / filter -- scope honestly

- **Exception thrown inside a finally block:** the finally is itself a region;
  `finallyEndAddress = -1` records that we are unwinding. If the finally body
  throws, that new exception enters the outer catch and is matched normally. This
  works once sections 2-7 land (no extra code, but MUST be smoke-tested).
- **`rethrow` (IL `rethrow` opcode -> `Rethrow`):** section 5, one-liner. Smoke
  test: catch, rethrow, outer catch sees same exception. (Testable WITHOUT
  newobj only if the originally-thrown object is reachable -- since we can't
  `throw new` yet, rethrow testing is limited; cover via rethrow of a caught
  `DivideByZeroException`.)
- **IL `filter` blocks / `Endfilter`:** OUT OF SCOPE. Filters are rare in
  hand-written C# (mostly VB / re-filtered exceptions). `Endfilter` will remain
  NIE (Step-tagged). Documented non-goal; not on the Step 14 validation list.

## 11. Non-goals

- async exception propagation (Step 20 -- needs the async state-machine work).
- CLR `newobj` (Step 18) -- therefore `throw new T(...)` for CLR types is not
  testable this step (see proposal throw/newobj finding).
- IL `filter` / `Endfilter`.
- stack-overflow guard (`ILIntepreter.Neo.cs:324` TODO; Step 26).
- Modifying any Legacy code (`ExecuteR`, shared `HandleException`, etc.).

## 12. Test design (`TestCases/NeoStep14Test.cs`, ASCII)

All cases throw via no-newobj sources (proposal section "throw/newobj
dependency"). Assert using the established NeoStep convention (value-compare +
`throw new`-free signal; note CLR `newobj` is unavailable, so assertion is via
returned value compare / `Console.WriteLine` + a sentinel, matching
`NeoStep6Test`/`NeoStep12bTest`). The exception type is `DivideByZeroException`
for `1/0` and `NullReferenceException` for null-deref.

- **TC1 basic try-catch:** `try { int x=1/0; return -1; } catch { return 7; }`
  -> 7 (catch hit, divide result not returned).
- **TC2 catch-object access:** catch `Exception e`, return a marker only if
  `e is DivideByZeroException` (CLR `is`/cast works via Step 9 reflection path on
  a CLR exception object). Verify the exception object is correctly stored in the
  catch slot (section 6).
- **TC3 try-finally runs:** `int r=1; try { int x=1/0; } catch { } finally { r=2; }`
  -> 2 (finally executed even though catch swallowed).
- **TC4 finally on normal Leave:** `int r=0; try { r=1; } finally { r+=10; }`
  -> 11 (no exception; Leave path runs finally, section 3).
- **TC5 nested try-catch innermost wins:** outer try { inner try { 1/0 } catch
  (DivideByZeroException) { return 5; } } catch { return -1; } -> 5.
- **TC6 cross-frame propagation:** `int Caller() { try { Callee(); return -1; }
  catch { return 9; } } void Callee() { int x=1/0; }` -> 9 (section 7 fix).
- **TC7 cross-frame finally in caller:** caller's finally must run when callee
  throws and caller catches.
- **TC8 NullReferenceException catch:** null IL instance field access -> NRE
  caught.

Each TC must run in <10s (infinite-loop guard; exception-unwind bugs tend to
loop). Full NeoStep smoke is the regression gate (49/49 + new cases; some
previously-NIE try/catch tests may turn green -- expected).
