# Design — neo-constrained-reftype

**Date:** 2026-07-11  **Capability:** byref (Step 17 Constrained arm) + neo-register-vm (generic call lowering)  **Wave:** MethodToken T-identity follow-up
**Status:** DONE (2026-07-11) -- Gap A applied after Gap B (commit a465f3f0) landed. The ref-type constrained `this` now dispatches end-to-end (CompareIt<string> returns the right CompareTo result). NeoStep 301/301, NeoStep17 gate 54/54, stash-toggle + Legacy-neutral all green.

> The earlier HANDOFF status (Gap A blocked by Gap B, parked to avoid a silent
> wrong-answer half-fix) is preserved verbatim below for the investigation trail.
> The resolution ("Gap A RESOLVED") is at the END of this file.

## The tasked gap (re-audited: REAL, narrow, fixable)
`constrained.callvirt` on a reference-type T (a generic param instantiated with a
ref type, e.g. `string`). The ExecuteNeo Constrained arm (the Step 17 box-once
path at `ILIntepreter.Neo.cs:~5216`) throws
`"Step 17: constrained.callvirt on a null or unsupported constrained type is not
handled (box-once no-op / ref-type this)"` for a ref-type T.

### Root cause of the tasked gap (CONFIRMED)
The box-once `else` branch sets `boxedReceiver = null` then chains `if
(thisObjIdx >= 0)` / `else if (ILType IsValueType)` / `else if (primitive)` /
`else if (CLR IsValueType)`. For a ref-type T (e.g. `System.String`, a CLRType
that is NOT a value type), NONE of those branches fire, so `boxedReceiver` stays
null -> the residual NIE at `ILIntepreter.Neo.cs:~5333`.

### Data flow for a ref-type constrained `this` (MEASURED via diagnostics)
The C# compiler emits the constrained `this` as a managed pointer: the JIT body
of `Step17CompareIt<string>` is literally
`ldarga.s r3, r0; push r3; push r1; constrained System.String; callvirt ...CompareTo`
(see `.tmp-compareit.log`). So slot-0 (the `this`) source is an 8-byte Ref Slot
(a frame-native byref `[objIdx, byteOff]`):
- `thisObjIdx = *(int*)(frameBase + cmap.PrimitiveSrc[0])` == **-1** (frame-native)
- `thisByteOff = *(int*)(frameBase + cmap.PrimitiveSrc[0] + 4)` == the receiver
  slot's byte offset (e.g. 20).
- The receiver object's mStack index is at `*(int*)(frameBase + thisByteOff)`
  (MEASURED: `bytes@20 = [4, 0, ...]`, and `mStack[4] == "abc"`).

So the fix for the `this` is a focused branch (the "missing else"): when
`constrainedType != null && !constrainedType.IsValueType && thisObjIdx < 0`,
`boxedReceiver = mStack[*(int*)(frameBase + thisByteOff)]`. This is the standard
`constrained.` ref-type semantics (ECMA III.3.19: for a reference type the
callvirt is a plain callvirt on the object, NO box), and mirrors Legacy
`ExecuteR`'s Constrained ref-type path (`ILIntepreter.Register.cs:3936-3937`:
`insIdx = objRef->Value` -- the receiver is used as-is).

**A correct narrow fix for this `this` branch was implemented and verified**
(boxedReceiver resolves to the right string object; the NIE is gone). It is
documented below in "The fix that works (Gap A)". It is NOT committed because it
is INSUFFICIENT to pass the reproducer (see Gap B).

## The blocker: a DEEPER, independent engine gap (Gap B)
After fixing Gap A, the reproducer `CompareIt<string>("abc","abc")` still fails
-- now with a WRONG RESULT (divide-by-zero assertion) instead of the NIE. The
`this` resolves correctly, but the SECOND param (the `y` argument to CompareTo)
arrives as a stale/null mStack index, so `"abc".CompareTo(<null>)` returns the
wrong sign and the assertion fails.

### Gap B is NOT Constrained-specific (PROVEN by probes)
A battery of NON-constrained probes (all added to `NeoStep17Test.cs` during the
audit, since reverted to keep HEAD clean) establish that generic reference-type
PARAM passing into ANY call is broken in Neo:
- `ProbeGenericRefParamEcho` (`return x;`, one ref param) -- **PASSES** (param
  received correctly; pure echo, no call on the param).
- `ProbeGenericRefParamEchoSecond` (`return y;`, two ref params) -- **PASSES**
  (both params received correctly by the echo).
- `ProbeGenericRefParamNonConstrained` (`((string)(object)x).Length`) -- FAILS
  (NRE).
- `ProbeGenericRefParamObjCall` (pass `T x` to a method taking `object`, NO
  cast/box/constrained) -- **FAILS (NRE)**. This is the cleanest isolation: a
  plain call passing a generic ref param to a differently-typed param.
- `ProbeGenericRefParamStringCall` (`(string)(object)x` then call) -- FAILS.
- `ProbeGenericRefParamTTCall` (pass `T x` to another generic method expecting
  `T x`) -- FAILS.

So the echo (return the param) works, but the moment a generic ref param flows
into a CALL (constrained or not, cast or not, T->object / T->string / T->T),
the callee receives a null/garbage reference. This is a broad pre-existing
engine gap in **generic-method reference-type param call lowering**, of which
the Constrained ref-type arm is only one facet.

### Gap B root-cause hypothesis (strong evidence, not yet pinned)
The generic method's frame sizes a reference-type generic param T (T=string) as
an **8-byte slot** (MEASURED via localInfos dump: the `y` param register is
`s8, rc1`, NOT `s4`). For a NON-generic ref local/param the slot is `s4` with
the mStack index in the primitive bytes, and the call-arm param copy
(`CopyNeoCallArguments` / the Constrained arm's primitive loop) reads those 4
bytes correctly. For a generic ref param the index is NOT in the first 4 bytes
of the 8-byte slot (MEASURED: the param's primitive bytes hold a stale/wrong
index while the live object is reachable via the slot's ref region), so the
4-byte primitive copy reads garbage -> NRE/wrong-ref in the callee.

The likely fix surface is the **generic-method frame param sizing** (a ref-type
generic param, once specialized to a concrete ref type, should size as `s4` like
a non-generic ref param) OR the **call-arm param copy** must read a generic ref
param from its ref slot (not its primitive bytes) when the slot is 8-byte. Both
are in `Optimizer.Neo.cs` / `JITCompiler.cs` (AllocateLocalStackSpaces /
AllocateNeoCallParamSlot), NOT in the Constrained arm.

### Gap B RESOLVED (2026-07-11) -- root cause was Box, NOT param sizing
A fresh-eyes re-audit pinned the ACTUAL root cause, and it is **NOT** the param
sizing/copy site the hypothesis pointed at. The generic-method frame param
sizing is correct: when the instantiated `Echo<string>` / `CallObjHelper<string>`
is JIT-compiled, `appdomain.GetType` resolves the generic-param `T` to the
concrete `string` (the instance's `genericParameters` dict has the concrete arg),
and `AllocateSlotForType`/`AllocateNeoCallParamSlot` route a `string` param to
their final `else` -> `Size=4, RefCount=1` (a ref slot), IDENTICAL to a
non-generic `string` param. No s8 slot exists for a ref-type generic param; the
prior "s8/rc1" measurement was a red herring.

The real defect is the **Neo `Box` opcode mishandling a CLR REFERENCE type**.
When a generic param `T` flows into an `object`/base-class param, the C# compiler
emits `box !!T` (the JIT body of `CallObjHelper<string>` is literally
`0: box r2, r0, System.String; 1: brfalse.s ...; 2: callvirt GetHashCode`).
Specialized to T=string, this is `box System.String` -- boxing a CLR REFERENCE
type, which ECMA III.4.3 defines as an IDENTITY (no-op; the same instance flows
through).

The Neo `Box` handler (`ILIntepreter.Neo.cs` OpCodeREnum.Box, the CLR `else`
branch at ~line 3401) had NO ref-type arm: it branched only on
`clrBoxType.IsPrimitive` (-> read flat bytes) and `else` (-> `ReadNeoValueType`
struct read). A CLR ref type fell into the struct arm and ran
`ReadNeoValueType(typeof(string), frameBase, ...)` = `Unsafe.ReadUnaligned<string>`
over the slot's raw bytes -- reinterpreting the 4-byte mStack index (or garbage)
as a `string` reference, producing a wrong/null object and (often) an OOB
`mStack[dstIdx]` write (`List.set_Item` at the `mStack[dstIdx] = boxed` line).

The F-MAJ-1 comment on the struct arm assumed "a ref-type source never reaches
Box (re-boxing an object is a compiler no-op)". That holds for direct re-boxing
of a KNOWN-ref expression but is FALSE for a GENERIC-PARAM `box !!T`: the
compiler cannot elide it (T may be a value type at another instantiation), so it
emits `box !!T` unconditionally and the ref-type instantiation reaches Box.

The fix (1 new `else if` arm in the Box CLR branch):
```csharp
else if (!clrBoxType.TypeForCLR.IsValueType)
{
    // Gap B: box on a CLR REFERENCE type is an IDENTITY (ECMA III.4.3). The
    // source slot holds the object's mStack index in its first 4 bytes -- read
    // the object as-is (NO ReadNeoValueType). Mirrors Legacy ExecuteR Box
    // (ILIntepreter.Register.cs:3880-3883: obj = mStack[objRef->Value]).
    int boxSrcIdx = *(int*)(frameBase + ip->SrcOffset);
    boxed = boxSrcIdx >= 0 ? mStack[boxSrcIdx] : null;
}
```
Gated only inside `ENABLE_NEO_MODE` (the file is Neo-only). Legacy `ExecuteR`
already handles this correctly (Register.cs:3880-3883).

Probe results (5 new probes in `TestCases/NeoStepGapBProbe.cs`, all PASS after
the fix; all 3 call-arm probes FAILED on HEAD with NRE/OOB at the Box site):
- `NeoStepGapB_EchoString` (return T) -- PASS (control: no box-on-param).
- `NeoStepGapB_ObjCall` (`box !!T` -> HashOf(object)) -- PASS (was NRE/OOB).
- `NeoStepGapB_StringCall` (`(string)(object)x` -> LenOf(string)) -- PASS.
- `NeoStepGapB_NonGenericObjCall` (HashOf("control")) -- PASS (control).
- `NeoStepGapB_EchoInt` (Echo<int>) -- PASS (value-type generic UNAFFECTED;
  the `!IsValueType` guard keeps the int box path untouched).
- The TT-call facet (`x.GetHashCode()` on a generic T) compiles to
  `constrained !!T; callvirt` and is Gap A (the Step-17 constrained-arm
  ref-type `this`), NOT Gap B -- left to the neo-constrained-reftype re-apply.

NeoStep smoke: **298/298** (293 baseline + 5 Gap-B probes). Legacy-neutral
(plain `Debug` build, 0 errors). Stash-toggle: HEAD -> ObjCall NRE; fix ->
PASS. Value-type generic (`Echo<int>`) unaffected.

## Scope decision: HANDOFF (do NOT ship a half-fix)
Shipping Gap A alone turns a clean Step-17 NIE (an explicit, labeled TODO) into a
SILENT WRONG ANSWER for `constrained.callvirt` on a ref type -- strictly worse
than HEAD. The reproducer cannot pass until Gap B is fixed. Per the 11-for-11
lesson and the task's "if genuinely deep, PARK" directive, both source files
were reverted to HEAD; the engine is back to its clean NIE state (NeoStep 293/0,
NeoStep17 gate green, Legacy-neutral -- all re-verified after revert).

The next worker should fix **Gap B first** (generic ref-param call lowering --
the higher-leverage fix, unblocks multiple probes), THEN re-apply Gap A (the
narrow Constrained ref-type `this` branch, ready below) to close the
Constrained facet.

## The fix that works (Gap A) -- ready to re-apply after Gap B
In `ILIntepreter.Neo.cs`, the box-once `else` branch (currently starts
`object boxedReceiver = null;` then `if (thisObjIdx >= 0)`). Insert BEFORE the
`if (thisObjIdx >= 0)`:

```csharp
// Reference-type constrained T: the receiver is ALREADY a ref object (ECMA
// constrained. ref-type semantics -- plain callvirt on the object, NO box). The
// JIT emits the this as a managed pointer (ldarga/ldloca), so slot 0's source is
// an 8-byte frame-native byref; deref it to get the object's mStack index. Mirrors
// Legacy ExecuteR Constrained ref-type path (insIdx = objRef->Value).
if (boxedReceiver == null && constrainedType != null && !constrainedType.IsValueType && thisObjIdx < 0)
{
    int recvIdx = *(int*)(frameBase + thisByteOff);
    boxedReceiver = (recvIdx >= 0 && recvIdx < mStack.Count) ? mStack[recvIdx] : null;
}
```

This was verified to resolve `boxedReceiver` to the correct string object (the
NIE is eliminated; the dispatch proceeds). It only fails to produce the right
final result because of Gap B (the `y` param).

## Reproducer (the JIT path, NOT Cecil-free -- isolates the engine gap)
Add to `TestCases/NeoStep17Test.cs`:
```csharp
static int Step17CompareIt<T>(T x, T y) where T : IComparable<T> { return x.CompareTo(y); }
public static void NeoStep17_ConstrainedRefTypeString()
{
    int r1 = Step17CompareIt<string>("aaa", "bbb");
    int r2 = Step17CompareIt<string>("bbb", "aaa");
    int s1 = r1 < 0 ? -1 : (r1 > 0 ? 1 : 0);
    int s2 = r2 < 0 ? -1 : (r2 > 0 ? 1 : 0);
    if (s1 != -1 || s2 != 1) { int z = 1; int d = 0; int _ = z / d; }
}
public static void NeoStep17_ConstrainedRefTypeStringEqual()
{
    int r = Step17CompareIt<string>("abc", "abc");
    if (r != 0) { int z = 1; int d = 0; int _ = z / d; }
}
```
On HEAD: both throw `Step 17: ... null or unsupported constrained type ...`.
With Gap A only: both fail with divide-by-zero (wrong result, y is null).
With Gap A + Gap B: expected to pass (sign-normalized -1/+1; equal -> 0).

## Verification status (at revert / HANDOFF)
- NeoStep smoke: **293 tests, 0 failed** (HEAD baseline held; no regression).
- NeoStep17 gate: **0 failed** (value-type constrained cohorts {a,d,M2,b,K6,K7}
  all green -- the ref-type work touched nothing they depend on).
- Legacy-neutral: plain `Debug` build, 0 errors.
- Stash-toggle: N/A (no code shipped; both files at HEAD).

## Key files
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- the Constrained
  arm (Gap A site: box-once `else` at ~line 5216; the NIE at ~5333).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Register.cs:3898-4031` --
  the Legacy Constrained arm (the reference for ref-type semantics).
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs:1195-1403`
  (NeoCallParamMap builder) + `1503-1573` (`AllocateNeoCallParamSlot`) -- Gap B
  fix surface (call-arm param copy + param sizing).
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs:1848+`
  (`AllocateLocalStackSpaces`) -- Gap B fix surface (generic-method frame layout).

## Gap A RESOLVED (2026-07-11) -- applied after Gap B; reproducer passes end-to-end

With Gap B (Box ref-type identity) landed in commit `a465f3f0`, the READY Gap A
branch was re-applied AND a second, adjacent bug in the same Constrained box-once
path was found and fixed. The reproducer `CompareIt<string>` now returns the
correct `CompareTo` result.

### The Gap A branch (as designed -- applied verbatim)
In `ILIntepreter.Neo.cs`, the box-once `else` branch, inserted BEFORE the
`if (thisObjIdx >= 0)`:
```csharp
if (boxedReceiver == null && constrainedType != null &&
    !constrainedType.IsValueType && thisObjIdx < 0)
{
    int recvIdx = *(int*)(frameBase + thisByteOff);
    boxedReceiver = (recvIdx >= 0 && recvIdx < mStack.Count) ? mStack[recvIdx] : null;
}
```
Verified via diagnostics: for `CompareIt("abc","abc")` this resolves
`boxedReceiver` to the correct `String:abc` (recvIdx=4 -> mStack[4]="abc"; the
NIE is gone). This branch alone is CORRECT for the `this`.

### Second bug found + fixed: the F3 "accepted-known" ref-region copy loop
Gap A eliminated the NIE, but the reproducer still returned a WRONG result
(`"abc".CompareTo("abc")` returned 1, not 0). Root-caused via mStack + localInfos
dumps: the box-once path's ref-region copy loop (the F3 "accepted-known" code
landed in 8bafaaef, never exercised for a ref-type T before Gap A) was BOTH
semantically broken AND destructive:

```csharp
// the removed loop:
for (int i = slot0RefCount; i < cmap.RefSrc.Length; i++)
{
    int rSrcIdx = *(int*)(frameBase + cmap.RefSrc[i]);   // BUG (a)
    mStack[frameRefBase + cmap.RefDst[i]] = (...); ? mStack[rSrcIdx] : null;  // BUG (b)
}
```

- **BUG (a)** -- mis-reads the source: `cmap.RefSrc[i]` is a `RefOffset` (an index
  into `mStack[frameRefBase + ...]`, the param's ref region -- exactly how
  `CopyFrameToIL` at `ILIntepreter.Neo.cs:5792` consumes it: `mStack[frameRefBase +
  refOffset]`). Treating it as a FRAME BYTE OFFSET (`*(int*)(frameBase + RefSrc[i])`)
  reads garbage (it deref'd into an unrelated primitive param's bytes -> srcIdx=0
  -> mStack[0]=null). The prior "param sized as 8-byte slot" framing in the Gap B
  hypothesis was a mis-measurement of THIS bug, not a real 8-byte param slot.
- **BUG (b)** -- overwrites the caller's own object: `frameRefBase` is the CURRENT
  (caller) frame's ref base, and `cmap.RefDst[i]` for the `y` param is its RefOffset
  (1). So the write lands at `mStack[frameRefBase + 1] = mStack[5]` -- which is
  EXACTLY the caller's `y` object slot (placed there by `ldstr r6`). The loop thus
  NULLED the very object the callee's primitive index (5) points at. `InvokeNeoClrMethod`
  -> `CLRMethod.Invoke` reads `*(int*)(targetBase + curPrim)` = 5 -> `mStack[5]` =
  null -> `"abc".CompareTo(null)` -> wrong sign.

The standard call paths prove the ref-region copy is unnecessary: `Callvirt_CLR` /
`Callvirt_Interface` call `CopyNeoCallArguments`, which copies ONLY the primitive
bytes for a ref-typed param (the mStack index) and lets the callee read the object
by that index. The callee (CLR `Invoke`, `CLRMethod.cs:510-515`: `idx = *(int*)
(targetBase + curPrim); pval = idx < 0 ? null : mStack[idx]`) reads the object by
the primitive index -- it never touches a separate callee ref region.

**Fix:** removed the loop entirely (the box-once path now copies only the primitive
bytes for non-`this` args, exactly like `CopyNeoCallArguments`). The IL-VT-with-ref-
fields `this` case is owned by the DIRECT-CALL path above (the
`constrainedSlot0Seed` hook), not the box-once path, so dropping this loop loses
nothing. This mirrors Legacy `ExecuteR`, whose Constrained arm does not touch the
non-`this` ref region.

### Reproducer result (CompareIt<string>) -- PASS
The 3 wrappers in `TestCases/NeoStep17Test.cs` (the JIT path, NOT Cecil-free --
isolates the engine gap from the MethodToken T-identity work):
- `NeoStep17_ConstrainedRefTypeString` -- `CompareIt("aaa","bbb")` -> -1,
  `CompareIt("bbb","aaa")` -> +1. **PASS** (sign-normalized).
- `NeoStep17_ConstrainedRefTypeStringEqual` -- `CompareIt("abc","abc")` -> 0.
  **PASS** (was the wrong-result case before the F3-loop removal).
- `NeoStep17_ConstrainedRefTypeGetHashCode` -- `x.GetHashCode()` on a generic T
  (compiles to `constrained !!T; callvirt Object::GetHashCode`). **PASS** (this is
  the TT-call facet the Gap B implementer noted as Gap A's territory -- now green).

### Verification (at DONE)
- NeoStep smoke: **301/301** (298 baseline incl. the 5 Gap-B probes + 3 new Gap-A
  wrappers). NeoStep17 gate: **54/54** (51 baseline + 3 new wrappers; the value-
  type constrained cohorts {a,d,M2,b,K6,K7} all green -- no regression).
- Stash-toggle: HEAD (Gap A branch + loop removed stashed) -> 3/3 wrappers throw
  the explicit Step-17 NIE; fix restored -> 3/3 PASS.
- Legacy-neutral: plain `Debug` build, 0 errors.
- Files: `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (+ Gap A
  branch / - F3 loop); `TestCases/NeoStep17Test.cs` (+ 3 wrappers + 2 helpers).
  NOT committed (LEAD commits).

