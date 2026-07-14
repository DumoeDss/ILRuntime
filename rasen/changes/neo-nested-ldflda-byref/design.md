# Design: neo-nested-ldflda-byref

## The gap (JIT-dump-confirmed)
`outer.Struct.field += N` lowers to CIL
`ldflda Struct(on outer); ldflda field(on the struct byref); ldind.i4; add; stind.i4`.
The INNER `ldflda field` operates on the BYREF produced by the outer ldflda. The
Neo `Ldflda` runtime arm had no branch for "my operand is a struct-field byref":
it read the byref's objIdx half as a direct heap index, re-stamped its own
`fieldPrimOff` into the offset half, and produced `(containingObjIdx,
innerFieldHash)` -- pointing at the CONTAINING object but carrying the INNER
field's hash (two different types). The following `ldind.i4` then called
`NeoReadClrObjectField(containingObj, innerFieldHash)`, which failed to resolve
`innerFieldHash` on the containing object's CLRType -> NRE.

Legacy is byte-identical at the JIT level (same `ldflda; ldflda; ldind; addi;
stind`) -- the difference is purely the runtime Ldflda arm. Legacy's arm
produces a `FieldReference` chain that `RetriveObject`/`StoreValueToFieldReference`
walks with box/mutate/unbox.

## The fix (3 parts, all Neo-gated -> Legacy-neutral)

### 1. JIT marker (JITCompiler.cs)
New const `NeoLdfldaNestedByRefMarker = 0x10` (next free bit in the Ldflda
Operand4 space {0x1 inline, 0x2 clrStructField/F-10, 0x4 heapIlRefField, 0x8
clrStructLocalField}). Stamped in `case Code.Ldflda` inside the `else if (type
is CLRType)` block when `ins.Previous` is `Ldflda` or `Ldsflda` (the CIL
predecessor is an address-producer -> the operand is a byref, not a heap
object). Gated on `type is CLRType` so an IL-struct inner field takes its
existing path. The CIL-predecessor signal is reliable (one predecessor per
instruction; prefixes precede the address-producer and never sit between it and
the inner ldflda) -- same dataflow link child-24/29 use for raw-Ldfld on a
byref owner. Runtime detection is UNSAFE (the untyped Neo frame cannot
distinguish a byref's objIdx half from a heap object's mStack index), so a JIT
marker is mandatory (the recurring marker-vs-runtime crux).

### 2. Self-describing descriptor (ILIntepreter.Neo.cs)
A small sealed class `NeoNestedFieldAddr` carrying the FULL nested path:
`ContainingObj`, `StructFieldOff` (refOff|flag for F-10, or the struct field
hash for a CLR object), `InnerFieldHash`, `InnerDeclTypeHash`, `BoxedStruct`
(the materialized boxed struct), `IsF10`. The inner Ldflda branch
(`nestedByRefMarker && objIdx >= 0`, placed BEFORE the `objIdx == -1`
frame-native branch) materializes the boxed struct and pushes a descriptor onto
mStack, producing byref `(tempIdx, 0)`. Materialization by owner shape:
- F-10 (ILTypeInstance + flag): boxed struct at `ManagedObjects[refOff]`; seed a
  default box on null (Activator.CreateInstance of the inner declaring CLR type).
- ILTypeInstance without flag (IL-struct field): tagged deferred NIE (distinct
  shape, fail-loud).
- CLR object: `NeoReadClrObjectField(containingObj, structFieldOff)` (a boxed
  copy via FieldInfo.GetValue).

CRITICAL soundness rule (mirrors child F-10 / child-27): the TYPE check
(`containingObj is ILTypeInstance`) comes FIRST, not the F-10 flag bit -- a
CLR-object owner's structFieldOff is the struct field's FieldInfo.GetHashCode()
which can have the flag bit (0x40000000) set by chance.

The descriptor lives on mStack -> reclaimed with the frame (NO process-static
leak). The frame-native nested chain (`ldloca; ldflda; ldflda`, objIdx == -1)
is UNAFFECTED (the new branch is gated on `objIdx >= 0`; the existing
`objIdx == -1` branch keeps handling the in-frame chain via `vtBase + fieldOff`).

### 3. ldind/stind descriptor branches (ILIntepreter.Neo.cs)
Each ldind/stind primitive arm gains a leading
`if (objIdx >= 0 && mStack[objIdx] is NeoNestedFieldAddr nfa) { ...; break; }`
BEFORE its existing if/else chain:
- ldind (I4/I8/R4/R8): `(T)ResolveNeoNestedInnerField(AppDomain, nfa)` ->
  `f.GetValue(boxedStruct)` (reflection read of the inner field).
- stind (I4/I8/R4/R8): `WriteNeoNestedInnerField(AppDomain, nfa, v)` ->
  `f.SetValue(boxedStruct, v)` (mutates the box IN PLACE, child-27 proven) +
  EXPLICIT origin write-back: F-10 -> `ili.ManagedObjects[refOff] = boxedStruct`;
CLR object -> `NeoWriteClrObjectField(containingObj, structFieldOff, boxedStruct)`.

The write-back is what the 8-byte byref cannot encode and is the reason a
descriptor (not a plain temp) is required: a plain boxed-struct temp would read
correctly via the existing NeoIsClrObject branch but the stind mutation would
NOT persist (the CLRType.SetFieldValue setter delegate reboxes into a local, and
a CLR-object owner's box is a copy). The descriptor's explicit write-back makes
the nested `+=` persistent -- matching Legacy for F-10 (Legacy prints the
persisted value) and EXCEEDING Legacy for CLR-object owners (Legacy does not
back-propagate a CLR-class struct-field boxed-copy mutation).

## Scope / deferred (documented honestly)
- IN: inner-ldflda-on-CLR-struct-field-byref for F-10 (IL instance) and CLR-
  object owners, primitive inner fields (int/long/float/double).
- OUT (separate children): (a) CLR STATIC struct field via ldsflda -- the
  ldsflda arm still throws "Neo Ldsflda: CLR static field address deferred" for a
  CLRType declaring type; this blocks `UnitTest_Struct2` case 3
  (`TestStruct.instance.value += 111`) and is a distinct gap (the ldsflda byref
  must also feed the raw Stfld/Ldfld consumers, which have their own byref
  handling). (b) The UnitTest_10051 `.x.RawValue` constrained-callvirt property
  read on a nested struct field (a different shape from the `+=` ldflda path).
  (c) Small-int ldind (I1/U1/I2/U2) and Stind_Ref/Ldind_Ref descriptor branches
  (not exercised by the D4 tests; symmetric to the added arms if needed).
  (d) IL-struct inner fields (the ILTypeInstance-without-flag case throws a
  tagged deferred NIE).

## Probes (TestCases/NeoStepNestedLdfldaByrefTest.cs)
- TC1 F-10 IL-instance owner: `h.Struct.value = 100; += 50;` assert == 150.
- TC2 CLR-object owner (TestClass3.Struct): assert == 150 (exceeds Legacy).
- TC3 field preservation: two `+=` on the same owner; assert == 35.
All 3 FAULT on HEAD (ldind NRE, stash-toggle-confirmed); PASS after with the
asserted values (write-back proven).
