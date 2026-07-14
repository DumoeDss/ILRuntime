# Design: neo-ldind-stobj-cluster-g

## The defect (the crux)
`stobj` / `ldobj` of a **reference-type T** (an IL class, e.g. `TestGenrRef`,
`TestClass222`) is semantically a **reference store / load** through the
managed-pointer (byref) operand -- NOT a value copy. The Neo `Stobj` / `Ldobj`
arms only handled value-type T:

- `Stobj` computed `primSize`/`refCount` from the ILType and did a flat
  `CopyBlock` of `primSize` bytes (frame-native byref) or routed to the
  ILTypeInstance-`Primitives` value-copy path. For a reference T, this either
  NRE'd (`GetNeoILInstance` on a null ref slot) or silently copied primitive
  bytes into the wrong target.
- `Ldobj` mirrored the same value-copy logic and NRE'd / corrupted for a
  reference T.

## The byref shapes (dump-confirmed via a diagnostic NIE)
Diagnostic thrown at the top of the Stobj arm for `!t.IsValueType` captured
the exact dest byref `(objIdx, off)` for each failing test:

| Test | objIdx | off | flag | srcVal | mStack[objIdx] | byref kind |
|---|---|---|---|---|---|---|
| RefOutNull2 (`res = p as T`, out param, p=null) | 9 | 0x40000000 | Y | -1 | null | F-7B caller-owned ref slot |
| GenericsRefOut2 (`return dest`, local ref, dest was null) | 10 | 0x40000000 | Y | 13 | null | F-7B caller-owned ref slot |
| GenericsRefOut (`obj = new T()`, ref static field ttt) | 12 | 0 | N | 9 | ILTypeStaticInstance | IL-instance (static) ref field |

(`off = 0x40000000` = `JITCompiler.NeoF10ByrefOffsetFlag`; the real ref offset
is `off & ~flag` = 0.)

These are the SAME byref shapes `Stind_Ref` / `Ldind_Ref` already dispatch on
for reference store / load. The fix delegates the reference-type case to that
exact dispatch.

## The fix (mirror Stind_Ref / Ldind_Ref, Neo-gated)
Add a `t != null && !t.IsValueType` branch at the TOP of each arm. The value-
type body is preserved verbatim under `else if` / `else` -- the discriminator
is mutually exclusive (`!t.IsValueType`), so no value-type path changes.

### Stobj reference-type branch
Source = the mStack index at `ip->SrcOffset` (-1 = null). Dest byref =
`(objIdx, off)` at `ip->DstOffset`. Dispatch (byte-identical to `Stind_Ref`
6216-6267):
1. `objIdx == -1` -> frame-native: `*(int*)(frameBase + off) = stRefVIdx`.
2. `(off & NeoF10ByrefOffsetFlag) != 0` -> F-7B: `mStack[objIdx] = stRefVal`.
3. `mStack[objIdx] is Array` -> `stRefArr.SetValue(stRefVal, off)`.
4. `NeoIsClrObject` -> `NeoWriteClrObjectField`.
5. `mStack[objIdx] is ILTypeInstance` (covers `ILTypeStaticInstance`, which
   `: ILTypeInstance`) -> `stRefIns.ManagedObjects[off] = stRefVal`.
6. else -> tagged NIE.

### Ldobj reference-type branch
Source byref = `(objIdx, off)` at `ip->SrcOffset`. Materialize the referent
into the dest ref slot. `Operand3` IS stamped for `Ldobj` by `LowerNeoOffsets`
(the `Ldind_Ref` / `Ldobj` shared case at `Optimizer.Neo.cs:1188-1196` sets
`op.Operand3 = localInfos[r1].RefOffset`), so the dest ref slot is known.
Dispatch (mirror `Ldind_Ref` 6274-6360):
1. `objIdx == -1` -> read `srcIdx = *(int*)(frameBase + off)`; materialize
   `mStack[srcIdx]` (or -1).
2. flag set -> `mStack[objIdx]`.
3. Array -> `GetValue(off)`.
4. CLR object -> `NeoReadClrObjectField`.
5. ILTypeInstance -> `ManagedObjects[off]`.
Write `mStack[frameRefBase + ip->Operand3] = elem; *(int*)(DstOffset) = dstIdx`
(non-null) or `*(int*)(DstOffset) = -1` (null). Tagged NIE on an unsupported
shape.

## Why this is sound (no regression)
- The branch fires ONLY for `!t.IsValueType`. Every value-type / enum /
  primitive stobj/ldobj is byte-identical to HEAD (the existing arms are under
  `else`).
- The dispatch reuses the proven `Stind_Ref` / `Ldind_Ref` byref semantics
  (the same 5 byref kinds, same content-based ordering: flag-first, then
  Array, then CLR-object, then ILTypeInstance last to avoid collision with
  non-deterministic CLR field-hash offsets).
- `Operand3` for `Ldobj` is guaranteed set by `LowerNeoOffsets` (verified:
  `Ldobj` shares the `Ldind_Ref` lowering case).
- Legacy parity: `ExecuteR` Stobj `StackObjectReference`/`StaticFieldReference`
  reference-store branches; Ldobj `StackObjectReference` ->
  `CopyToRegister(dest, resolveReference)`.

## Verify
- Diagnostic dump confirmed the 3 byref shapes before the fix.
- RefOutNull2 PASSES after the Stobj fix (deterministic; the `res = p as T`
  stores null into the out-param ref slot, then `res = new T()` stores the new
  instance -- both via branch 2 (F-7B) since srcVal=-1 then a real idx).
- GenericsRefOut2 PROGRESSES past stobj AND ldobj (the `return dest` ldobj now
  materializes the referent) -- then hits a SEPARATE `constrained.` gap (out
  of scope).
- NeoStep 398/0 (no regression).
- Full smoke 48 -> 46 (RefOutNull2 + ArrayReferenceTest deterministic flips; 0
  new failures). RegisterVMTest04 is flaky (order-dependent, same-code
  pass/fail across runs) -- not a deterministic effect.

## Out of scope (remaining, with diagnosis)
1. **ldind.i4 nested-ldflda (UnitTest_Struct/Struct2, 2 tests):** the
   `a.Struct.value += N` pattern -- `ldflda Struct(on IL instance); ldflda
   value; ldind.i4`. The INNER `ldflda` operates on the byref produced by the
   outer `ldflda` (a byref to a boxed CLR struct at `ManagedObjects[refOff]`).
   child-27 explicitly deferred this ("inner ldflda-on-byref gap"). Needs a
   byref-aware ldflda (JIT marker, child-24/29 lineage, or runtime detection).
2. **ldlen / GetFields-returns-null (ReflectionTest14, 1 test):** the ldlen
   arm is CORRECT (NRE on a null array is CLR-faithful). The bug is upstream:
   `Type.GetFields()` returns null on this IL type under Neo (the
   `PropertyInfo[]` from `GetProperties()` is non-null, but `FieldInfo[]` from
   `GetFields()` is null). Separate reflection scope.
3. **ldelema null-element `out arr[i]` (UnitTest_ArrayReferenceTest, 1 test):**
   `dict.TryGetValue(k, out arr[i])` on a freshly-allocated IL-class array.
   `arr[i]` is null and the ILTypeInstance[] ldelema branch threw NRE. FIXED:
   for a null element, emit the `(arrIdx, elementIdx)` array-element byref
   (same encoding as the CLR-array `else` branch) so the write-back Array arm
   SetValues the out-param target. Non-null elements keep the materialize-for-
   read path (unchanged). (A first attempt was reverted over a suspected
   RegisterVMTest04 regression, but re-audit proved RegisterVMTest04 is
   FLAKY/order-dependent -- it fails in isolation and in baseline/run3 with
   the SAME code, and passed only once by noise -- so the ldelema fix is a
   clean deterministic flip with no real regression.)
4. **constrained. on a reference-type T (GenericsRefOut/GenericsRefOut2):**
   once stobj/ldobj are fixed, these progress to `dest.GetString()` which
   lowers to `constrained. T; <non-callvirt>`. The Neo JIT lowers a non-virtual
   callvirt on a reference-type T to a plain `Call`, leaving the `constrained.`
   orphaned; the Constrained arm (specialized for value-type box-once
   dispatch) rejects it. Fix = handle the reference-type-T constrained case
   (constrained. is a no-op for ref types; skip and let the trailing Call/
   Callvirt execute). Separate defect (Step 17 D-CONSTRAINED).
5. **NeoMarshalByrefFieldToSlot NRE (GenericsRefOut):** progresses to
   `t2.DoTestRef()` -> `call TestStruct::DoTest(TestStruct&)` -> NRE @
   `NeoMarshalByrefFieldToSlot:519` (a CLR-struct byref param). Separate byref-
   marshal gap.
