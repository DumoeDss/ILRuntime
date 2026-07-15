# Design: neo-recluster-19

## Fix 1 -- CLR-enum return sizing (DelegateTest19)

### Symptom
`DelegateTest19Sub` returns `TestCLREnum` (a CLR enum). The delegate callback's
Ret opcode OOBs at `ILIntepreter.Neo.cs:4313` inside the vt-with-ref-fields
branch: `mStack[frameRefBase + retSrcRefOff + i]` reads a non-existent ref slot.

### Root cause (JIT-dump-pinned)
`DelegateTest19Sub` JIT body:
```
0:initobj r0, TestCLREnum
1:ldc.i4.1 r0       // r0 = 1 (Test2 = underlying int 1)
3:ret r0
```
The JIT emits the enum as a FLAT int (4 primitive bytes). But
`JITCompiler.AllocateSlotForType` (the `else` branch at ~line 2691) sizes EVERY
CLR type (value or reference) as a boxed reference: `Size=4, RefCount=1`. So the
method frame's `ReturnPrimitiveSize=4, ReturnRefCount=1`.

At Ret (`ILIntepreter.Neo.cs:4273`), the handler branches on `returnRefCount`:
- `returnRefCount == 0` -> CopyBlock the primitive bytes (CORRECT for an enum).
- else -> `isSingleReferenceReturn`? For a CLR enum, `returnType.IsValueType` is
  true -> `!IsValueType` is false -> NOT single-reference -> enters
  vt-with-ref-fields -> reads `mStack[frameRefBase + retSrcRefOff + i]` -> OOB
  (the enum's ref slot was never populated; the JIT wrote only the flat int).

### The inconsistency
The Neo model already treats CLR enums as flat primitives EVERYWHERE ELSE:
- IL enums (`t is ILType && IsEnum`) take the `t.IsValueType && t is ILType`
  branch -> `TotalReferenceCount==0` (RefCount=0).
- The JIT emits CLR enum locals/returns as `ldc.i4`/`initobj` (flat int).
- `InferPrimTag` resolves IL enums to their underlying primitive.

Only `AllocateSlotForType`'s `else` branch mis-sizes CLR enums as boxed (RefCount=1).

### Fix
Add a branch in `AllocateSlotForType` BEFORE the boxed-`else` branch:
```csharp
else if (t.IsValueType && !(t is ILType) && t.TypeForCLR != null && t.TypeForCLR.IsEnum)
{
    int size = appdomain.GetPrimitiveSize(t);   // 4 for int32 enum (GetPrimitiveSize's enum branch -> GetNeoValueTypeManagedSize)
    if (size < 1) size = 4;
    slot.Offset = offset; slot.RefOffset = refOffset;
    slot.Size = size; slot.RefCount = 0;
    offset += size;
}
```
This makes a CLR enum `ReturnRefCount=0` -> the Ret handler takes the primitive
CopyBlock path (copies the 4-byte enum value to retDst). Consistent with IL enums
and with the caller's dest sizing (also via AllocateSlotForType -> RefCount=0 ->
reads 4 flat bytes).

### Safety / regressions
- Removing the unused ref slot from CLR-enum locals/params is safe: the JIT never
  writes it (enums are flat ints). Boxing (`box TestCLREnum`) is a separate opcode
  that reads the flat bytes; unaffected.
- IL enums already RefCount=0; no change. Non-enum CLR value types (e.g.
  TestVector3 structs) still take the `else` (boxed) branch -- UNCHANGED
  (deliberately narrow; CLR-struct sizing is child-28 territory).
- Verified: NeoStep 403/0 (EnumTest + all categories green); DelegateTest19 PASS;
  full smoke 19 -> 17.

---

## Fix 2 -- ExpectException double-wrap (Test05.TestForEach)

### Symptom
`TestForEach` is annotated `[ILRuntimeTest(ExpectException=typeof(NotSupportedException))]`.
`ParseOne("1")` throws NSE("error"). Under Legacy the framework honors it as
Pass; under Neo it is reported Failed.

### Root cause (trace-pinned)
The framework (`BaseTestUnit.cs:144-165`) catches `ILRuntimeException e` and
checks `e.GetInnerException().GetType() == expectingEx`. `GetInnerException()`
returns the DIRECT inner (`ILRuntimeException.cs:89-92`).

The exception flow for `foreach (var i in a) { ParseOne(i); }` (foreach lowers to
try/finally for the enumerator):
1. `ParseOne` throws NSE -> its frame's bottom-of-method re-throw wraps it:
   `pendingThrow = new ILRuntimeException(nse)` (`Neo.cs:7598`). Propagates.
2. `TestForEach`'s per-iteration catch catches `ILRuntimeException(nse)`.
   `HandleException` finds the finally handler. At `ILIntepreter.cs:4889`:
   `lastCaughtEx = ex is ILRuntimeException ? ex : new ILRuntimeException(...)`.
   `ex` IS already an ILRuntimeException -> `lastCaughtEx = ILRuntimeException(nse)`
   (NOT unwrapped -- correct for finally, which must re-throw the original object).
3. The finally body runs (`Dispose`). `Endfinally` (`Neo.cs:7160`) `throw lastCaughtEx`.
4. `TestForEach`'s per-iteration catch catches `ILRuntimeException(nse)` again.
   `HandleException` at the Endfinally address: no catch/finally matches ->
   returns false.
5. `Neo.cs:7598`: `pendingThrow = new ILRuntimeException(ex.Message, ..., ex)`
   where `ex` = `ILRuntimeException(nse)` -> **DOUBLE-WRAPPED**:
   `ILRuntimeException(ILRuntimeException(nse))`.
6. The framework catches the outer ILRuntimeException; `GetInnerException()` =
   the inner ILRuntimeException; `.GetType()` = ILRuntimeException != NSE ->
   MISMATCH -> Failed.

### Fix
At `Neo.cs:7598`, do not re-wrap an already-wrapped exception (mirror the
finally-branch guard at `ILIntepreter.cs:4889`):
```csharp
pendingThrow = ex is ILRuntimeException ? ex : new ILRuntimeException(ex.Message, this, method, oriESP, ex);
```
With the fix: `pendingThrow = ILRuntimeException(nse)` (not re-wrapped). The
framework's `GetInnerException().GetType()` = NSE = expectingEx -> Pass.

### Safety / regressions
- Changes behavior ONLY when `ex` is already an ILRuntimeException at the
  bottom-of-method re-throw (the propagated+finally-rethrown path). Keeping it
  verbatim is strictly more correct: no information is lost (the existing
  ILRuntimeException already carries the message + context; HandleException
  appended the frame's stack/this/local info to the inner's Data at
  `ILIntepreter.cs:4843`).
- A raw (non-wrapped) exception is still wrapped exactly as before -- no change
  for the normal single-frame-throw path.
- Verified: NeoStep 403/0 (NeoStep14 EH 26/26 + all categories green -- exception
  propagation/finally/catch unregressed); TestForEach + TestForEachTry PASS; full
  smoke 19 -> 17.
- Legacy-neutral: `ILIntepreter.Neo.cs` is file-gated `#if ENABLE_NEO_MODE`;
  Legacy `ExecuteR` (`ILIntepreter.Register.cs`) is untouched.

## Verification (truth = full-smoke number)
- FULL SMOKE: **19 -> 17** (`Ran 937 tests, 17 failded, 20 ignored, 7 todos`;
  the 17 are a strict subset of the 19 -- DelegateTest19 + TestForEach gone, no
  new failures).
- NeoStep: **403/0** (no regression; EnumTest green for fix 1; NeoStep14 EH +
  finally/catch paths green for fix 2).
- Legacy-neutral: plain `Debug` CLI build = 0 errors (both fixes Neo-gated /
  file-gated; Legacy compiles none of it).
