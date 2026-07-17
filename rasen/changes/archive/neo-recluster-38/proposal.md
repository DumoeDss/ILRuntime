# Proposal -- neo-recluster-38

## Why
The full Neo smoke (fresh ground, 2026-07-14) is **38 failed** (down from 189 via 32
wave-2 children; the prior 60-grounding is stale). The grounding re-clusters the
CURRENT 38 by real exception + top Neo.cs frame and fixes the largest single-root
sub-cluster. See `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-38.md` for the
fresh cluster table.

## Largest single-root sub-cluster identified
**Cluster C: `NeoMarshalByrefFieldToSlot` NRE @ Neo.cs:519 (byref `ref`/`out` param
on a heap-IL reference field / CLR-struct static field).** 3 tests:
- InheritanceTest21 -- `Dictionary.TryGetValue(int, out inheritanceTest.crossClass)` where `crossClass` is an IL-class instance field.
- InheritanceTest22 -- (same shape).
- RefOutTest.UnitTest_GenericsRefOut -- `TestStruct.DoTest(ref str2)` where `str2` is a CLR-struct STATIC field of an IL class.

All 3 PASS on Legacy (plain Debug + useRegister=true). All 3 NRE on Neo at the same
helper.

## Root cause (Neo-vs-Legacy)
1. **Reference IL field**: `ldflda &ili.<refField>` is JIT-stamped
   `NeoLdfldaHeapIlRefFieldMarker` (JITCompiler.cs:3346) and the runtime Ldflda
   `heapIlRefFieldMarker` branch (ILIntepreter.Neo.cs:2129-2148) emits byref
   `(objIdx, ReferenceOffset)` with NO flag. The reference field's storage is
   `ManagedObjects[ReferenceOffset]`. `stind_ref`/`ldind_ref` ALREADY route this
   shape to `ManagedObjects[off]` (content-based). But the byref-param field marshal
   `NeoMarshalByrefFieldToSlot` (CopyNeoCallArguments forward deref +
   CopyNeoCallThisBack write-back) does NOT -- its ILTypeInstance branch only handles
   the F-10 boxed-struct case (flag) and otherwise treats `off` as a Primitives byte
   offset -> `ili.Primitives[ReferenceOffset]` (wrong/OOB) -> NRE.

2. **CLR-struct static field**: `ldsflda` (ILIntepreter.Neo.cs:5247-5295) emits the
   byref `(staticInstanceIdx, ReferenceOffset)` with NO F-10 flag for a CLR-struct
   static field of an IL type (it only special-cases primitive vs non-primitive).
   A CLR-struct IL-static field is stored as the boxed struct at
   `ManagedObjects[ReferenceOffset]` (the SAME storage as an instance CLR-struct
   field of IL = the F-10 shape). Without the flag, `NeoMarshalByrefFieldToSlot`
   misroutes it to `Primitives[off]` -> NRE. (All byref consumers -- stobj/ldobj and
   NeoMarshalByrefFieldToSlot -- already handle the F-10 flag for IL instances.)

## What changes (Neo-only, mirror established patterns)
- `ILIntepreter.Neo.cs` `NeoMarshalByrefFieldToSlot`: add a reference-field branch in
  the ILTypeInstance arm (after the F-10 flag check, before the Primitives path) that
  reads/writes `ManagedObjects[off]` via the mStack-index convention, discriminated by
  `elemType != null && !elemType.IsValueType && !elemType.IsPrimitive`. Mirrors the
  CLR-object reference-field branch of the SAME method (Neo.cs:609-665) and the
  `stind_ref`/`ldind_ref` IL-instance-ref-field arms.
- `ILIntepreter.Neo.cs` `Ldsflda`: stamp the `NeoF10ByrefOffsetFlag` on the byref
  offset for a CLR-struct static field of an IL type (condition mirrors the JIT's
  `IsClrStructFieldOfIL`: `!(ldaFt is ILType) && ldaFt.IsValueType &&
  !ldaFt.IsPrimitive`). IL-struct static fields are NOT boxed -> no flag.

Both edits are in `ILIntepreter.Neo.cs` (file-gated `#if ENABLE_NEO_MODE`) ->
Legacy-neutral by construction. No JIT / optimizer / object-model / binding change.

## Non-goals (documented, separate sub-roots)
- Cluster A (13 `throw`-assertion tests @ Neo.cs:6808) -- grab-bag of distinct
  test-internal assertions, NOT single-fix.
- Delegate-extension-method arg-marshal cluster (DelegateExtTest01/02, DelegateTest01
  @ Neo.cs:4567) -- 3 identical-msg tests but the fix touches the delegate Invoke
  arg-layout (bound-static headShift / param-0 write), high regression risk to the
  many passing delegate tests; deferred to focused delegate-Step-19 work.
- RefOutTest.UnitTest_GenericsRefOut2 ("constrained not followed by callvirt") --
  distinct constrained-callvirt JIT shape.
- StructTest6 (out IL-struct-with-ref-fields local write-back) -- frame-native byref
  ref-region write-back gap (sibling of the Stobj/Ldobj Step-17(b) ref-region work).
