# Design - neo-f4-parametrized-run-entry

> Scope-AWARE dump-gate for the parametrized `ILIntepreter.Run` entry (F-4 #3 +
> F-12 TRUE-COMPLETION). Probed by static code-evidence on HEAD `cb07444d` (the
> gap is conclusive from the `Run` Neo arm vs the Step-19 `NeoInvokeSub` mirror;
> no IL probe is needed to establish the gap -- the entry is unreachable for
> parametrized calls today). Legacy is the REFERENCE; the shipped edit is
> Neo-only.

## 0. Dump-gate verdict

### 0.1 Run's Neo arm ignores `instance` + `p` (CONFIRMED; F-4 #3 root cause)

`ILIntepreter.Run(ILMethod method, object instance, object[] p)`
(`ILIntepreter.cs:104-137`), under `ENABLE_NEO_MODE` (`:111-137`):

- Builds the Neo frame at `(byte*)stack.StackBase` (`:116`) and advances `esp`
  past `nf.TotalStructSize` (`:117`) -- but NEVER populates the param region or
  the slot-0 `this`. The parameters `instance` and `p` are received as formal
  arguments and then DROPPED.
- Reserves ONLY the return ref region (`:124-128`, `for i < nf.ReturnRefCount
  mStack.Add(null)`) -- NOT the callee frame's own reference slots
  (`nf.TotalRefSize`). So even a method with ref-typed locals/params would have
  unallocated ref slots.
- Calls `ExecuteNeo(method, neoFrame, retDst, retRefBase, out _)` (`:129`) on a
  frame whose param slots are uninitialized bytes.
- The comment at `:112-113` states it explicitly: "Step 6 entry shim: only
  no-arg static methods are expected here (NeoStep6 smoke)."

So `AppDomain.Invoke(instanceMethod, e)` (`AppDomain.cs:1664` ->
`inteptreter.Run((ILMethod)m, instance, p)` `:1674`) runs the IL instance-method
override with no `this` in slot 0 -> `NullReferenceException` on the first field
access. **F-4 path #3 confirmed.**

### 0.2 The mirror pattern: `DelegateAdapter.NeoInvokeSub` (the established CLR->IL arg-marshal)

The Step-19 delegate callback `DelegateAdapter.NeoInvokeSub(object[] args)`
(`DelegateAdapter.cs:1006-1118`) IS the parametrized-Run implementation, already
in production for IL-delegate-through-CLR round-trips. It:

1. Reserves the full callee frame reference region:
   `for i < nf.TotalRefSize: mStack.Add(null)` (`:1056-1057`), recording
   `frameRefBase = mStack.Count` BEFORE the reservation (`:1055`).
2. Zeros the locals primitive region: `InitBlock(frameBase +
   nf.ParamPrimitiveSize, 0, nf.LocalsPrimitiveSize)` (`:1039-1040`).
3. Ref-inits unassigned local ref slots: `*(int*)(frameBase + localInfos[i].Offset)
   = -1` for each `localIsReference[i]` (`:1043-1052`).
4. Writes `this` as slot 0 for `method.HasThis`:
   `WriteNeoCallSlot(paramInfos[0], frameBase, mStack, frameRefBase, instance)`
   (`:1061-1065`).
5. Writes each CLR param into its param-region slot:
   `WriteNeoCallSlot(paramInfos[argIdx], frameBase, mStack, frameRefBase, arg)`
   in a loop over `paramCnt` (`:1068-1073`).
6. Reserves the return ref region AFTER the frame ref region (`:1079-1081`).
7. Calls `ExecuteNeo(method, frameBase, retDst, retRefBase, out _)` (`:1084`).
8. Reads the return with a TYPE-DISCRIMINATED branch (`:1087-1097`):
   - reference return (`!IsValueType && !Void && retSize > 0`): read
     `*(int*)retDst` as the mStack index, return `mStack[retIdx]` (`:1091-1092`);
   - value-type return: `NeoBoxReturnValue(method.ReturnType, retDst, retSize)`
     (`:1096`).
9. Tears down the mStack reservation (`mStack.RemoveRange(mStackBase, ...)`,
   `:1101`).

`WriteNeoCallSlot` (`DelegateAdapter.cs:1124-1165`) is the per-slot marshaler
(the inverse of `CopyNeoCallArguments`): a `RefCount > 0 && Size == 4` slot is a
reference -> store the object at `mStack[frameRefBase + info.RefOffset]`, write
the index into the param region (`:1127-1135`); a primitive/CLR-VT slot is
written directly by `info.Size` (`:1139-1164`, incl. the >8-byte CLR-struct
`WriteNeoValueType` path).

**This is the exact pattern `Run`'s Neo arm must mirror.** The only difference:
`NeoInvokeSub` builds the frame on a FRESH pooled interpreter's `StackBase`
(`:1033`); `Run` builds it on the CURRENT interpreter's stack (it is itself the
pooled interpreter, called from `AppDomain.Invoke` which already did
`RequestILIntepreter`). Both are correct -- `Run` keeps `stack.StackBase` as the
frame base, as it does today.

### 0.3 `NeoBoxReturnValue` is primitive-only (CONFIRMED; F-12 root cause)

`ILIntepreter.NeoBoxReturnValue(IType returnType, byte* retDst, int retSize)`
(`ILIntepreter.Neo.cs:4784-4813`) handles ONLY primitive CLR types (int/uint/
long/ulong/short/ushort/byte/sbyte/bool/char/float/double, `:4787-4810`). The
final fallback (`:4811-4812`) is `return retSize >= 4 ? *(int*)retDst : (int)
*retDst` -- i.e. for a reference-type return (string/object/ILType) it reads the
low 4 bytes of `retDst` AS AN INT, not as the mStack reference index the Neo
return convention actually stores there.

`Run`'s Neo arm (`:131-135`) calls `NeoBoxReturnValue` for ALL non-void
`retSize > 0` returns with no type discrimination. So a reference-type return
yields garbage (the raw mStack index reinterpreted as an int) -- **F-12 /
NEO-RUN-REF-RETURN confirmed.** The fix is NOT to extend `NeoBoxReturnValue`
itself (it correctly serves its primitive-boxing contract); it is to add the
reference-return BRANCH in `Run` before falling back to it (exactly as
`NeoInvokeSub:1087-1097` already does).

### 0.4 Scope decision: SHIP (SMALL; the mirror already exists)

The parametrized-Run extension is SMALL because the entire frame-setup +
arg-marshal + return-read machinery already exists verbatim in
`DelegateAdapter.NeoInvokeSub` (`:1026-1101`) and `WriteNeoCallSlot`
(`:1124-1165`). This change TRANSPLANTS that machinery into `Run`'s Neo arm.
There is NO deeper ABI gap: `ExecuteNeo`, the frame layout, `WriteNeoCallSlot`,
and the return convention are all unchanged and proven by the delegate path
(Step 19, shipped + regression-stable). The only new surface is making
`WriteNeoCallSlot` callable from `ILIntepreter` (it is already
`internal static` on `DelegateAdapter`, in the same assembly -- accessible
directly or via a thin forwarding static).

This change closes **F-4 #3 AND F-12 in one** (both are symptoms of the same
Step-6 parameterless-only `Run` shim: missing instance+params marshalling +
missing reference-return branch).

## 1. The parametrized-Run ABI design

Rewrite the `Run` Neo arm (`ILIntepreter.cs:111-137`) to mirror
`NeoInvokeSub`. Pseudocode (Neo-only, inside the existing `#if ENABLE_NEO_MODE`):

```csharp
// ---- frame setup (mirror NeoInvokeSub:1031-1057) ----
ref readonly var nf = ref method.CompiledFrame;
byte* frameBase = (byte*)stack.StackBase;
byte* esp = frameBase;
int frameSize = nf.TotalStructSize;
byte* newEsp = esp + frameSize;

// Zero the locals primitive region (NeoInvokeSub:1039-1040).
if (nf.LocalsPrimitiveSize > 0)
    Unsafe.InitBlock(frameBase + nf.ParamPrimitiveSize, 0, (uint)nf.LocalsPrimitiveSize);
// Ref-init unassigned local ref slots (NeoInvokeSub:1043-1052).
var localInfos = nf.LocalInfos;
var localIsRef = nf.LocalIsReference;
if (localInfos != null && localIsRef != null)
    for (int i = 0; i < localInfos.Length; i++)
        if (localIsRef[i]) *(int*)(frameBase + localInfos[i].Offset) = -1;

// Reserve the callee frame reference region (NeoInvokeSub:1055-1057).
int frameRefBase = mStack.Count;
for (int i = 0; i < nf.TotalRefSize; i++) mStack.Add(null);

// ---- marshal this + params (mirror NeoInvokeSub:1059-1073) ----
var paramInfos = nf.ParamInfos;
int argIdx = 0;
object thisObj = instance;
if (method.HasThis)
{
    // Unwrap the CLR adaptor bridge, matching the Legacy Run arm (ILIntepreter.cs:141-142).
    if (thisObj is CrossBindingAdaptorType cbat) thisObj = cbat.ILInstance;
    if (thisObj == null) throw new NullReferenceException("instance should not be null!");
    DelegateAdapter.WriteNeoCallSlot(paramInfos[0], frameBase, mStack, frameRefBase, thisObj);
    argIdx = 1;
}
int paramCnt = method.ParameterCount;
for (int i = 0; i < paramCnt; i++)
{
    object arg = (p != null && i < p.Length) ? p[i] : null;
    DelegateAdapter.WriteNeoCallSlot(paramInfos[argIdx], frameBase, mStack, frameRefBase, arg);
    argIdx++;
}

// ---- return slot + execute (mirror NeoInvokeSub:1075-1084) ----
int retSize = nf.ReturnPrimitiveSize;
int retRefCount = nf.ReturnRefCount;
byte* retDst = newEsp;
int retRefBase = mStack.Count;
for (int i = 0; i < retRefCount; i++) mStack.Add(null);

ExecuteNeo(method, frameBase, retDst, retRefBase, out unhandledException);

// ---- return-read with type discrimination (mirror NeoInvokeSub:1086-1097) ----
object result = null;
if (!method.ReturnType.IsValueType && method.ReturnType != domain.VoidType && retSize > 0)
{
    int retIdx = *(int*)retDst;
    result = (retIdx >= 0) ? mStack[retIdx] : null;          // F-12 fix
}
else if (method.ReturnType != domain.VoidType && retSize > 0)
{
    result = NeoBoxReturnValue(method.ReturnType, retDst, retSize);   // primitives, unchanged
}
mStack.RemoveRange(mStackBase, mStack.Count - mStackBase);
return result;
```

### 1.1 Why mirror `NeoInvokeSub` instead of refactoring a shared helper

- **Lowest-risk transplant.** `NeoInvokeSub` is the SAME call shape
  (host/CLR-driven -> Neo IL callee with explicit args), already shipped and
  regression-stable across the Step-19 delegate tests. Copying its frame-setup
  sequence verbatim into `Run` minimizes the chance of a layout mismatch with
  what `ExecuteNeo` expects.
- **`Run` builds on the CURRENT interpreter's stack**, not a fresh pooled one.
  `AppDomain.Invoke` already did `RequestILIntepreter` (`AppDomain.cs:1671`) and
  frees it in a `finally` (`:1677-1680`). So `Run` must NOT request/free another
  interpreter (unlike `NeoInvokeSub`, which does its own request/free pair
  `:1018`/`:1117`). This is the ONE intentional divergence.
- **`WriteNeoCallSlot` accessibility.** It is `internal static` on
  `DelegateAdapter` (`DelegateAdapter.cs:1124`), same assembly as
  `ILIntepreter`. `ILIntepreter` already calls into `DelegateAdapter` (e.g. the
  `NeoInvoke`/`ILInvoke` family); calling `DelegateAdapter.WriteNeoCallSlot(...)`
  directly is the minimal change. No new public API.

A future cleanup MAY extract a shared `NeoMarshalCall(thisObj, p, frameBase,
mStack, nf)` helper used by both `Run` and `NeoInvokeSub`, but that is a refactor
-- it is NOT required for correctness and would touch the stable delegate path.
**Sequenced as an optional follow-up** (Non-Goal below).

### 1.2 Does it close F-4 #3 AND F-12 in one change?

Yes. Both gaps are consequences of the same Step-6 parameterless-only `Run` shim:

- **F-4 #3:** fixed by the slot-0 `this` push (`method.HasThis` branch) -- the IL
  instance-method override now runs with `instance` as its receiver.
- **F-12:** fixed by the reference-return branch -- a non-value-type return is
  read as the mStack reference index, not raw bytes.

No second change is needed for either.

## 2. Adversarial probes (the gate; a green smoke does NOT prove it)

### 2.1 F-4 #3 probe: caught-exception instance-method re-entry

Construct in `TestCases/NeoStep14Test.cs`:

- An IL exception type `MyEx` with an OVERRIDDEN instance method (e.g. a virtual
  `Message` getter or a `GetCode()` instance method that reads an IL-declared
  field) -- the override is what must run with the right `this`.
- A test method that throws `new MyEx(...)`, catches it as `e`, then invokes the
  instance method via the bound `AppDomain.Invoke`: resolve the `ILMethod`
  (`domain.LoadedTypes[...].GetMethod("GetCode")`), then
  `domain.Invoke(getCode, e)` -- and asserts the result equals the override's
  value (NOT NRE, NOT the base default).
- On HEAD this NREs (the override runs with no `this`); after the fix it returns
  the override's value. Stash-toggle: restoring the Step-6 shim -> NRE; the Neo
  arm -> PASS.

This is the success criterion named in the planning context. It must go through
the bound `AppDomain.Invoke` (so it hits the public `Run` entry), not a direct
`ExecuteNeo` drive (which would bypass the gap).

### 2.2 F-12 probe: parameterless reference-type return

Construct a parameterless IL method returning a reference type (e.g. a
`static string EchoRef()` returning a literal string), invoke it via
`domain.Invoke(method, null)`, and assert the returned object is the string
(`"hi"`), not `0`/garbage. On HEAD this returns the raw mStack index as an int
(`0`); after the fix it returns the boxed reference.

### 2.3 Regression: full NeoStep smoke

The full `NeoStep` filter (currently 221/0/0 per `neo-f4-reflection-on-neo`)
SHALL stay green. The parametrized-Run arm is on the host-re-entry path only;
it does not alter IL->IL dispatch. Legacy plain-`Debug` build = 0 errors
confirms Legacy-neutrality.

## 3. Goals / Non-Goals

**Goals:**

- `AppDomain.Invoke(instanceMethod, instance, p)` works under Neo for IL instance
  methods (F-4 #3) -- the override runs with the right `this`.
- A reference-type (or value-type) return from a `Run`-driven invocation is boxed
  correctly (F-12).
- Legacy `Run` byte-identical (Neo-only edit).

**Non-Goals:**

- Extracting a shared `NeoMarshalCall` helper between `Run` and `NeoInvokeSub`
  (refactor; the transplant is correct without it). Sequenced as optional.
- The two NEW pre-existing gaps discovered during the F-4 apply (design.md
  section 8 of `neo-f4-reflection-on-neo`): (a) the `op_Equality` null-operand
  gap (general Neo) and (b) the `new MyEx(string)` ctor string-arg mis-route.
  Both are SEPARATE changes; the F-4 #3 probe works around (b) by constructing
  via the default ctor + a direct field assignment (the established
  `NeoStep14_ILEx_IndexerFieldRead` workaround pattern).
- Reading the IL TYPE projection from `Object.GetType` (the redirect-key
  mismatch recorded in `neo-f4-reflection-on-neo` design section 7 -- needs a
  normalized map key; unrelated to `Run`).

## 4. Risks / Trade-offs

- **[Frame layout mismatch with `ExecuteNeo`]** -> Mitigation: the frame-setup
  sequence is COPIED from `NeoInvokeSub`, which `ExecuteNeo` already accepts for
  the delegate path. Field names (`TotalStructSize`, `TotalRefSize`,
  `ParamPrimitiveSize`, `LocalsPrimitiveSize`, `LocalInfos`,
  `LocalIsReference`, `ParamInfos`, `ReturnPrimitiveSize`, `ReturnRefCount`)
  are all confirmed public on `CompiledFrame` (`JITCompiler.cs:81-92`).
- **[mStack base teardown correctness]** -> Mitigation: `Run` already records
  `mStackBase = mStack.Count` on entry (`ILIntepreter.cs:107`) and tears down
  with `mStack.RemoveRange(mStackBase, mStack.Count - mStackBase)` (`:136`); the
  redesign keeps both, so the frame ref region + return ref region added between
  them are reclaimed identically to today (and to `NeoInvokeSub:1101`).
- **[Regression on existing parameterless `Run` callers]** -> Mitigation: the
  NeoStep6 smoke (the original parameterless caller) exercises the SAME arm; a
  parameterless static method has `HasThis == false` and `paramCnt == 0`, so the
  new `this`-push and param loops are both skipped, and the only behavioral
  change is the locals-zeroing + frame-ref-region reservation (which a
  parameterless method needs anyway if it has locals). The NeoStep smoke is the
  gate.
- **[`WriteNeoCallSlot` visibility]** -> Mitigation: it is already `internal
  static` in the same assembly; no visibility change needed. If a future split
  moves `DelegateAdapter` to another assembly, promote to `public static` then.

## 5. Open Questions

- None blocking. The `Run` -> `NeoInvokeSub` transplant is fully specified by
  the mirror. (Optional: a later refactor to share the marshal helper is
  recorded as a Non-Goal.)
