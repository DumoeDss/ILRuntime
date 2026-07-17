# Design: neo-recluster-16

## The fix: constrained-callvirt primitive-field-of-IL-instance `this` marshal

### Symptom
`GenericMethodTest11` (`GenericMethodTest.cs:302`): `SubBind : TestBind<int>`
where `TestBind<T> : IComparable<T>` has `public T data;`. The setter
`set_Data(int value)` does `if (null != data && data.CompareTo(value) == 0)`.
CIL lowering (JIT-confirmed):
```
ldflda r3, r0, data        # byref to this.data (an int FIELD of the IL instance)
push r3                    # push as `this` for CompareTo
push r1                    # push value
constrained System.Int32
callvirt IComparable<int>::CompareTo(int), thisArg=0
```
At runtime the autogen `System_IComparable_1_Int32_Binding.CompareTo_0_Neo`
throws `InvalidCastException: ILTypeInstance -> IComparable<int>`.

### Root cause (pinned via instrumented diagnostics)
The Neo Constrained arm (`ILIntepreter.Neo.cs` ~7278-7515) owns the dispatch for
`constrained. T; callvirt M`. It decodes the byref `this` source (slot 0):
```
int thisObjIdx  = *(int*)(frameBase + cmap.PrimitiveSrc[0]);     // objIdx half
int thisByteOff = *(int*)(frameBase + cmap.PrimitiveSrc[0] + 4); // byte-off half
```
For `ldflda data` on an ILTypeInstance, the byref is
`(objIdx = the IL owner's mStack index, thisByteOff = Primitives byte offset)`.
Empirically: `thisObjIdx=3 thisByteOff=0 mStack[3]=ILTypeInstance
constrainedType=System.Int32 IsPrimitive=True`.

The box-once sub-branch was:
```csharp
if (thisObjIdx >= 0)
    boxedReceiver = mStack[thisObjIdx];   // <-- grabs the ILTypeInstance (owner)
else if (constrainedType is ILType ilBoxType && ilBoxType.IsValueType) { ... }
else if (constrainedType != null) {   // frame-native CLR primitive/VT read
    if (constrainedType.IsPrimitive)
        boxedReceiver = NeoBoxReturnValue(constrainedType, frameBase + thisByteOff, psz);
    ...
}
```
The `if (thisObjIdx >= 0)` arm fired FIRST (objIdx=3 >= 0), assigning the
ILTypeInstance (the field's OWNER) as the receiver. The correct CLR-primitive
read-from-`thisByteOff` branch (the `else if`) was unreachable for this shape
(it only fires when `thisObjIdx < 0`, i.e. a frame-native byref from
`ldloca`/`ldarga`).

### The fix (Neo-only, ~18 lines, single sub-branch)
In the `if (thisObjIdx >= 0)` arm, before falling back to
`boxedReceiver = mStack[thisObjIdx]`, intercept the
primitive-field-of-IL-instance shape:
```csharp
object conRawTarget = mStack[thisObjIdx];
ILTypeInstance conIli = conRawTarget as ILTypeInstance;
if (conIli == null && conRawTarget is CrossBindingAdaptorType cbaCon)
    conIli = cbaCon.ILInstance;
if (conIli != null && constrainedType != null && constrainedType.IsPrimitive
    && conIli.Primitives != null)
{
    int conPrimSz = AppDomain.GetPrimitiveSize(constrainedType);
    fixed (byte* conPP = conIli.Primitives)
        boxedReceiver = NeoBoxReturnValue(constrainedType, conPP + thisByteOff, conPrimSz);
}
else
    boxedReceiver = conRawTarget;   // genuine already-boxed receiver / ref-type
```
Mirrors the frame-native IsPrimitive branch but sources from the instance's
`Primitives` (pinned via `fixed`) instead of the caller's frame.

### Soundness / no-regression analysis
- **A boxed primitive on mStack is a `System.Int32` (etc.), NEVER an
  ILTypeInstance.** So the `is ILTypeInstance` discriminator never intercepts a
  genuine already-boxed receiver -> the existing box-once semantics for an
  already-boxed `this` are byte-identical (falls to `else`).
- **Primitive-only guard** (`constrainedType.IsPrimitive`): a CLR value-type
  FIELD of an IL instance (e.g. a CLR struct field) stays the existing behavior
  (not intercepted). An IL value type constrained receiver (`constrainedType is
  ILType`) is `IsPrimitive == false` -> not intercepted -> the IL-VT box branches
  (7384+) and the direct-call path (7310) are untouched.
- **CrossBindingAdaptorType unwrap**: mirrors `GetNeoILInstance` (7665) and the
  raw Stfld/Ldfld handlers (child-9). An IL type that inherits a CLR base can
  flow back as its CLR adaptor wrapper.
- **`thisByteOff` is a Primitives byte offset** for this shape (the ldflda
  convention for a primitive IL field): instrumented-confirmed (thisByteOff=0
  for `data`, the first field). `NeoBoxReturnValue` reads `GetPrimitiveSize`
  bytes -> correct for any primitive width.
- The whole arm is in the file-gated Neo file -> Legacy-neutral by construction.

### Verification
- `GenericMethodTest11`: 1 fail -> 0 (standalone run).
- NeoStep: **404/0** (no regression; the Constrained arm is hot but the change
  only adds a primitive+ILTypeInstance-intercepted sub-branch, every other shape
  byte-identical).
- Full smoke: **16 -> 15** (strict subset; only GenericMethodTest11 removed).
- Legacy-neutral: structural (`ILIntepreter.Neo.cs` is `#if ENABLE_NEO_MODE`
  file-gated).

## Non-fixes documented this child (deep, for future children)
- **RegisterVMTest04** -- RE-PINNED (ground-17 was wrong). Real root: the
  callee `ILScrollRect2<T>.SetViewRect` (override in a generic instance, called
  virtually with >3 args + a default-value `action=null`) reads `action` (param
  r4) at frame SrcOffset=12 but the value is garbage (65535), not the caller's
  `ldnull r7` (-1). The generic instance's field layout is CORRECT (TRC=1,
  ManagedObjects.Count=1). The bug is in the >3-arg virtual-IL-call
  NeoCallParamMap / frame-offset computation (C7 `isNeoNewobjShape` territory
  but for a plain callvirt.il). Next child: dump `NeoCallParamMap.PrimitiveSrc/
  Dst` for the callvirt.il, find why callee param-4 reads the wrong caller
  source.
- All other 14 survivors: distinct deep roots (see fullsmoke-ground-16.md).
