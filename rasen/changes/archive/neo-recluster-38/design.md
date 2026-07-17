# Design -- neo-recluster-38

## D1. The byref-param field marshal gap (reference IL field)

`NeoMarshalByrefFieldToSlot(appdomain, mStack, objIdx, off, elemType, slot, sz, isWrite)`
is the shared byref-field marshal for a `ref`/`out` CLR-method param whose referent is
a FIELD of an mStack object. It is invoked for BOTH directions:
- forward deref: `CopyNeoCallArguments` :438, `isWrite:false` (read the field's current
  value into the callee's dest slot before the call).
- write-back: `CopyNeoCallThisBack` :744, `isWrite:true` (write the callee's result
  slot back to the field after the call).

The ILTypeInstance arm (Neo.cs:470-520, after the F-10 boxed-struct flag check) only
handled two cases:
- F-10 flag set -> boxed CLR-struct field at `ManagedObjects[refOff]` via
  ReadNeoValueType/WriteNeoValueType.
- else -> `ili.Primitives[off]` (assumes `off` is a Primitives byte offset).

The gap: a REFERENCE-typed IL field (`string`/IL-class/`object`) produced by
`ldflda &ili.<refField>` (JIT `NeoLdfldaHeapIlRefFieldMarker`, runtime Ldflda
:2129-2148) carries `(objIdx, ReferenceOffset)` with NO flag. Its storage is
`ManagedObjects[ReferenceOffset]`. The arm misrouted it to `Primitives[off]`.

### Fix
Add a reference-field branch between the F-10 check and the Primitives path,
discriminated by the byref param's element type:
```csharp
if (elemType != null && !elemType.IsValueType && !elemType.IsPrimitive)
{
    if (isWrite)
    {
        int vIdx = *(int*)slot;
        ili.ManagedObjects[off] = vIdx >= 0 ? mStack[vIdx] : null;
    }
    else
    {
        object fieldValue = ili.ManagedObjects[off];
        if (fieldValue == null) *(int*)slot = -1;
        else { int newIdx = mStack.Count; mStack.Add(fieldValue); *(int*)slot = newIdx; }
    }
    return;
}
```
This mirrors the CLR-OBJECT reference-field branch of the SAME method (Neo.cs:626-630
write, :650-664 read) byte-for-byte (read = park object on mStack + write mStack index
to the slot's leading int; write = read mStack index from the slot + store the object),
substituting `ili.ManagedObjects[off]` for the field accessor.

### Soundness
- The F-10 flag check (runs FIRST) already diverts boxed-CLR-struct fields, so the new
  branch only sees non-F-10 ILInstance byrefs.
- A primitive/value IL field has `elemType.IsValueType || elemType.IsPrimitive` -> falls
  through to the existing Primitives path unchanged (no regression).
- `elemType` is populated for the byref-param path from the CLR `ParameterInfo`
  (Optimizer.Neo.cs:1419-1420 `pinfo.ParameterType.GetElementType()`). For a null
  elemType (the Step-20 builder write-back path), the branch is skipped -> today's
  behavior preserved.
- The reference-field byref and the primitive-field byref are indistinguishable by the
  byref alone (both `(objIdx, small-int)`, no flag); `elemType` is the ONLY reliable
  discriminator. This is the same discriminator the CLR-object branch relies on.

## D2. The static CLR-struct field gap (GenericsRefOut)

`ldsflda` (Neo.cs:5247-5295) for an IL static field materializes
`ldaIlt.StaticInstance` (an ILTypeStaticInstance, derives from ILTypeInstance) into
mStack and emits `(mStackIdx, fieldOffset)` where `fieldOffset = PrimitiveOffset`
(primitive field) or `ReferenceOffset` (non-primitive). For a CLR-struct static field,
`ReferenceOffset` is correct (the boxed struct lives there) but NO F-10 flag is set, so
`NeoMarshalByrefFieldToSlot` misrouted it to `Primitives[off]`.

### Fix
In the `ldsflda` IL-static branch, OR the `NeoF10ByrefOffsetFlag` onto the offset when
the static field is a CLR struct (condition mirrors the JIT's `IsClrStructFieldOfIL` --
declaring type is already ILType here):
```csharp
if (ldaFt != null && !(ldaFt is ILType) && ldaFt.IsValueType && !ldaFt.IsPrimitive)
    ldaFieldOff |= JITCompiler.NeoF10ByrefOffsetFlag;
```
All byref consumers already handle the F-10 flag for an ILTypeInstance target:
- `NeoMarshalByrefFieldToSlot` F-10 branch (Neo.cs:478-513) -- ReadNeoValueType/
  WriteNeoValueType on `ManagedObjects[refOff]`.
- `Stobj` F-10 branch (Neo.cs:6552-6558).
- `Ldobj` F-10 branch (Neo.cs:6723-6731).

IL-struct static fields are NOT boxed (they have their own Primitives/ManagedObjects
layout in the static instance) -> the `!(ldaFt is ILType)` guard keeps them unflagged
(content-based dispatch, unchanged).

### Soundness
- Today, WITHOUT the flag, a static CLR-struct field byref reaching
  NeoMarshalByrefFieldToSlot / stobj / ldobj misroutes to Primitives -> NRE/corruption.
  So the flag only FIXES latent bugs; no working path regresses.
- The NeoStep smoke (398/0) exercises static-field access; the flag only changes
  behavior for CLR-struct static fields of IL types (a niche not previously green).

## D3. Why not the delegate cluster (deferred)
DelegateExtTest01/02 + DelegateTest01 (3 identical "Owner type: System.Int32" @
Neo.cs:4567) share a root: `obj.IntTest` (static extension method) bound as a delegate
invokes IntTest with param 0 = the int arg instead of the bound `obj`. The fix touches
the delegate Invoke arg-layout (`NeoRunDelegateTargetOnThis` headShift + the D2 rebind
condition for a bound-static target). This is delicate (multicast, F-7/F-7B byref
channels, the headShift=4-for-static convention) and high-regression-risk to the many
passing delegate tests. Deferred to focused delegate-Step-19 work -- documented, not
silently dropped.

## Verify
- Name-filter: InheritanceTest2* (5/0 after, 2 failed before), GenericsRefOut (3 ran,
  1 failed after = GenericsRefOut2 [distinct root]; 2 failed before).
- Stash-toggle: the fresh ground run (HEAD, no fix) had all 3 failing; post-fix all 3
  pass (InheritanceTest21/22/GenericsRefOut). The diff of the two full-smoke failure
  lists shows EXACTLY these 3 fixed, ZERO new failures.
- Full smoke: 38 -> 35 (delta -3, no regressions, exit 0).
- NeoStep: 398/0 (no regression).
- Legacy-neutral: both edits are in ILIntepreter.Neo.cs (file-gated
  `#if ENABLE_NEO_MODE`); Legacy compiles none of it.
