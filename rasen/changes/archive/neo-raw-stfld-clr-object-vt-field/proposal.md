# Proposal — neo-raw-stfld-clr-object-vt-field

> Child 27 of the `neo-overhaul` portfolio. Branch `features/object-model-overhaul`.
> Triage batch-2 R3 (REAL + TRACTABLE, medium). DIAGNOSE-FIRST child: the
> owner-byref encoding was pinned before the apply (see design.md).

## Why
A raw `Stfld` on a primitive field of a CLR-struct FIELD of a CLR REFERENCE
object -- `obj.Struct.value = 111` (`obj` is a CLR object, `Struct` is a CLR-
struct field of `obj`, `value` is a primitive field of `Struct`; CIL:
`ldflda Struct(on obj); stfld value(on the struct address)`) -- throws the
tagged NIE "Neo raw Stfld: unrecognized CLR value-type owner byref shape" at
`ILIntepreter.Neo.cs:4193`. Trigger: `TestCases.ExpTest_10.UnitTest_Struct2`
(`LightTester1.cs:103`). 4 full-smoke hits (all the same test).

The raw-`Stfld` CLR-value-type-owner handler (child-4) covered two owner
shapes: `objIdx == -1` (frame-local struct, box/mutate/unbox via
`ReadNeoValueType`/`WriteNeoValueType`) and `objIdx >= 0 && mStack[objIdx] is
Array` (array element, child-19, box/mutate/unbox via
`Array.GetValue`/`SetValue`). The third shape -- `objIdx >= 0` and
`mStack[objIdx]` is a CLR REFERENCE object whose struct FIELD is the owner --
fell through to the defensive NIE. This is the heap-CLR-object sibling of
child-19 (array element) and child-9 (IL-instance CLR-base field).

## What changes
- **Runtime only** (`ILIntepreter.Neo.cs`, Neo-gated): add a third branch to
  the raw-`Stfld` CLR-value-type-owner arm. When `objIdx >= 0` and the parked
  object is a non-Array CLR object, box/mutate/unbox ONE LEVEL UP: read the
  struct field via the containing object's CLRType (the byref's offset half IS
  the struct field's hash -- resolvable by `NeoReadClrObjectField`), reflection-
  write the leaf field (`f.SetValue(boxedStruct, value)`), write the mutated
  struct back (`NeoWriteClrObjectField`). The IL-instance sub-case (F-10
  ManagedObjects storage) is deferred with a tagged NIE (separate sibling).
- No JIT / optimizer / object-model / binding change.
- Probe (`TestCases/NeoStepRawStfldClrObjVtFieldTest.cs`): 2 TCs reproducing
  `owner.S.field = N` on a dedicated 3-int-field CLR struct (`NeoClrObjVtField
  Probe`) hosted on a CLR class (`NeoClrObjVtFieldOwner`), asserting via a HOST
  read-back helper. TC2 sets all three fields separately, proving the box/
  mutate/unbox preserves the other fields.

## Impact
- Removes the 4 R3 full-smoke NIE hits; `UnitTest_Struct2` PROGRESSES (no
  longer NIEs at the direct assignment; residual = the `+=` nested-ldflda path
  and the raw-`Ldfld` read sibling, both separate gaps -- see design.md "Out of
  scope").
- Neo-gated => Legacy-neutral by construction.

## Capability
`neo-value-types` (siblings child-4 raw Stfld/Ldfld CLR-owner, child-19
Stfld-array-element, child-24 Ldfld-array-element, child-26 ldobj/stobj-array-
element).
