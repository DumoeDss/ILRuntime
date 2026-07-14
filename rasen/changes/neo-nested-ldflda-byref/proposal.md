# Proposal: neo-nested-ldflda-byref

Wave-2 child of `neo-overhaul` (branch `features/object-model-overhaul`). Neo =
ExecuteNeo under ENABLE_NEO_MODE. BACKGROUND run.

## Problem
`outer.Struct.field += N` (and the read form) lowers to CIL
`ldflda Struct(on outer); ldflda field(on the struct byref); ldind.i4; add; stind.i4`.
The INNER `ldflda field` operates on the BYREF produced by the outer ldflda, but
the Neo runtime `Ldflda` arm has no branch for "my operand is a struct-field
byref". It treats the byref's objIdx half as a direct heap-object index and
re-stamps its own fieldPrimOff into the offset half, producing a byref
`(containingObjIdx, innerFieldHash)` that points at the CONTAINING object (e.g.
TestClass3) but carries the INNER field's hash (e.g. TestStruct.value) -- the
two belong to different types. The following `ldind.i4` then calls
`NeoReadClrObjectField(containingObj, innerFieldHash)`, which fails to resolve
`innerFieldHash` on the containing object's CLRType -> NRE / IOOB.

This is the D4 cluster: `ExpTest_10.UnitTest_Struct` (ldind.i4 IOOB),
`ExpTest_10.UnitTest_Struct2` (ldind.i4 NRE at LightTester1.cs:104), and
`TestValueTypeBinding.UnitTest_10051` (property-read form). All three PASS under
Legacy (plain Debug + useRegister=true); the Legacy JIT is byte-identical (same
`ldflda; ldflda; ldind; addi; stind` sequence) -- the difference is purely the
runtime Ldflda arm.

Full Neo smoke baseline: 32 failed. Target: drop (32 -> lower).

## Root cause (pinned, JIT-dump-confirmed)
The Neo `Ldflda` runtime arm (ILIntepreter.Neo.cs ~2126) dispatches on the
operand slot's objectIndex half (`objIdx = *(frameBase + operandSlotOff + 0)`):
  * heapIlRefFieldMarker / clrStructFieldMarker (F-10) when objIdx >= 0,
  * `objIdx == -1` frame-native (ldloca chain -- already handles nested
    in-frame VT addresses correctly via `vtBase + fieldOff`),
  * inlineMarker (F-6 in-frame flat bytes),
  * `else` heap/CLR-object branch -> produces `(objIdx, fieldPrimOff)`.

For the nested case the operand is the OUTER ldflda's byref
`(containingObjIdx, structFieldOff)`. `objIdx >= 0`, no marker matches (the
inner ldflda's declaring type is a CLRType, so only 0x8 is stamped, and 0x8 is
consulted solely inside the `objIdx == -1` branch) -> falls to `else` ->
produces `(containingObjIdx, innerFieldHash)`, dropping the struct-field
indirection entirely. The consumer then mis-resolves.

Like child-24/29 (raw Ldfld on a byref owner), runtime detection is UNSAFE: the
operand representation (heap object vs byref) is a JIT-time dataflow fact and the
untyped Neo frame cannot distinguish a byref's objIdx half from a heap object's
mStack index. A JIT marker is required.

## Fix (Neo-gated, Legacy-neutral by construction)
1. JIT (JITCompiler.cs `case Code.Ldflda`): stamp a new Operand4 bit
   `NeoLdfldaNestedByRefMarker = 0x10` (next free bit in the Ldflda Operand4
   space {0x1,0x2,0x4,0x8}) when the inner ldflda's CIL predecessor is an
   address-producer (Ldflda / Ldsflda) -- i.e. its operand is a byref, not a
   heap object. Mutually exclusive signal (one CIL predecessor per instruction);
   prefixes (readonly./constrained.) precede the address-producer and never sit
   between it and the inner ldflda.
2. Runtime (ILIntepreter.Neo.cs `case Ldflda`): add a branch
   `else if (nestedByRefMarker && objIdx >= 0)` BEFORE the `objIdx == -1` branch.
   It materializes the boxed struct field into a TEMP mStack slot and produces a
   byref `(tempIdx, innerFieldHash)`. The generic ldind/stind NeoIsClrObject
   branches then read/write the inner field on the boxed struct with ZERO
   consumer change (the boxed struct is a CLR object; NeoReadClrObjectField /
   NeoWriteClrObjectField resolve the inner field by hash on the struct's own
   CLRType). Containing-object shapes:
     - IL instance + F-10 flag: boxed struct lives at ManagedObjects[refOff]
       (same reference as the stored slot) -- read it directly; seed a default
       box on null (Activator.CreateInstance). Read/write persist via the shared
       reference.
     - CLR object: boxed struct via NeoReadClrObjectField (FieldInfo.GetValue,
       a copy). Read correct; write lands on the copy (matches Legacy, which
       also does not back-propagate the boxed-copy mutation for a CLR-class
       struct field -- the tests have no assertion on this).
     - null containing object -> NullReferenceException (faithful).
   The `objIdx == -1` frame-native nested chain (ldloca; ldflda; ldflda) is
   UNAFFECTED (the new branch is gated on `objIdx >= 0`; the existing frame-
   native branch keeps handling the in-frame chain).

## Scope / out of scope
- IN: the inner-ldflda-on-struct-field-byref for CLR-struct inner fields
  (TestStruct.value, Fixed64) on F-10 (IL instance) and CLR-object owners.
- OUT (noted follow-ups): CLR STATIC struct field via ldsflda (UnitTest_Struct2
  case 3 `TestStruct.instance.value += 111` -- ldsflda still defers CLR statics;
  separate child); IL-struct inner fields; the UnitTest_10051 constrained-
  callvirt property read (separate shape, may benefit incidentally).

## Capability
`neo-value-types` (owns the byref/field-access surface; siblings child-15/24/26/
27/29/F-10 all live here).

## Verify
- Name-filter: the D4 tests PASS after fix (stash-toggle FAIL on HEAD).
- FULL SMOKE: `32 -> N` (record delta; expect lower).
- NeoStep 0-failures (no regression; broad -- 12/13/17/byref).
- Legacy-neutral: plain Debug + useRegister=true unaffected (Neo-gated).
