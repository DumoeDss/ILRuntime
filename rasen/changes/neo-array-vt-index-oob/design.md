# Design: neo-array-vt-index-oob (Stelem_Any value-type element)

## Root cause (pinned by JIT dump + stack trace, Neo-vs-Legacy)
`case OpCodeREnum.Stelem_Any / Stelem_Ref` in ExecuteNeo decodes:
- `srcIdx = *(int*)(frameBase + ip->DstOffset)`  // array mStack index
- `si     = *(int*)(frameBase + ip->SrcOffset)`  // element index
- value lives at `frameBase + ip->Operand4`

When the array is not `ILTypeInstance[]`, the old code unconditionally did:
```
int vIdx = *(int*)(frameBase + ip->Operand4);
object vObj = vIdx >= 0 ? mStack[vIdx] : null;
sa.SetValue(vObj, si);
```
This treats the value slot as an mStack REFERENCE INDEX. For a value-type
element the slot holds the struct/primitive FLAT MANAGED BYTES (the Neo object
model stores CLR value types as flat bytes in the frame, per child-12/15). The
leading int is the first field's bit pattern:
- `TestVector3.One = (1.0f, 1.0f, 1.0f)` -> first int = 0x3F800000 = 1065353216
- `float` 0.5f -> 0x3F000000 = 1056964608
=> `mStack[huge]` -> `ArgumentOutOfRangeException`.

Confirmed by JIT dump (ArrayTest05): `stelem.any ILRuntimeTest...TestVector3
(JIT_0004: stelem.any r0,r28,r4)` reaches the buggy `mStack[vIdx]` line
(Neo.cs:5559 on HEAD); Legacy (ExecuteR Stelem_Any) handles it correctly
(these 4 tests PASS under plain Debug + useRegister=true).

## Why Stelem_Any (not a typed Stelem_R4 etc.) reaches this for VT arrays
- A concrete `TestVector3[]` initializer / index-assign emits `stelem.any
  TestVector3` (there is no typed stelem for an arbitrary struct).
- A generic `T[].set` emits `stelem.any !T` even when T is a primitive (float),
  so the primitive does NOT take the Stelem_R4 arm. This is the
  `SampleDisposableClass<float>` indexer `arr[index] = value` shape in
  TestUsingNested.
So the Stelem_Any arm is the single dispatch point for all value-type-element
stores into CLR arrays.

## Fix (mirror child-26 Stobj array-element WRITE)
Discriminate at runtime by `sa.GetType().GetElementType()`:
```
Type elemType = sa.GetType().GetElementType();
if (elemType != null && elemType.IsValueType) {
    int cur = ip->Operand4;
    object boxed = ILIntepreter.ReadNeoValueType(elemType, frameBase, ref cur,
        Optimizer.GetNeoValueTypeManagedSize(elemType));
    sa.SetValue(boxed, si);
} else {
    int vIdx = *(int*)(frameBase + ip->Operand4);
    object vObj = vIdx >= 0 ? mStack[vIdx] : null;
    sa.SetValue(vObj, si);
}
```
`ReadNeoValueType` reads sz managed bytes at `frameBase+cur` into a boxed object
via a cached `Unsafe.ReadUnaligned<T> + Box` delegate; `cur` is a throwaway local
(the helper also advances it, but we read at a fixed offset). `GetNeoValueType
ManagedSize` = `Unsafe.SizeOf<T>`. `Array.SetValue` unboxes value types. This
is byte-for-byte the pattern child-26 used for Stobj's
`mStack[objIdx] is Array` arm (Neo.cs:5959-5972).

## Soundness / reach
- Reference-element arrays (`object[]`, `string[]`, class[]): `elemType.Is
  ValueType` is false -> unchanged path. A boxed value type stored into
  `object[]` is boxed BEFORE stelem.ref (C# semantics), so the slot genuinely
  holds an mStack index -> still correct.
- Value-type elements incl. primitives: the VT reader (`Unsafe.ReadUnaligned<T>
  + Box`) handles float/int/long/struct identically; `stelem.any !T` on a
  float[] lands here.
- Ref-field CLR structs: `ReadNeoValueType` copies bytes incl. any GC-ref slots
  (which under Neo are mStack indices, not real pointers). This is the SAME
  pre-existing Neo-wide concern guarded by `NeoClrStructHasRefFields` elsewhere;
  the typed Stelem_I4/R4 arms do not guard either. Not reached by the C10
  tests (TestVector3 / float / int are blittable, no ref fields). Left as-is to
  match the typed arms; a ref-field struct here would not crash (SetValue
  accepts the boxed object).
- `GetElementType()` on any array Type returns the element Type; null only for
  a non-array (impossible -- `sa is Array`).

## Probe (FAULT on HEAD, PASS after; host read-back)
`TestCases/NeoStepStelemAnyVtElementTest.cs` -- TC1 initializer store, TC2
explicit-size + index-assign store. Both store `TestVector3.One = (1,1,1)` into
a `TestVector3[]` and assert via host `TestCLRBinding.SumTestVector3ArrayElems`
(CLR-side `arr[i].X+Y+Z`), so verification does NOT depend on Neo Ldelem or Neo
float arithmetic. Expected sum 6 ((1+1+1)*2). On HEAD both FAULT with the exact
ArgumentOutOfRange at the stelem.any store; after the fix the host reads back 6.
The generic `stelem.any !T` shape is covered by TestUsingNested itself.

## Verify matrix
- Name-filter (Neo): ArrayTest05 / UnitTest_10035 / TestValueTypeBinding.Test03
  / TestUsingNested all PASS after fix.
- Stash-toggle: stash ONLY ILIntepreter.Neo.cs -> 2/2 probe TCs FAULT
  (ArgumentOutOfRange); pop -> 2/2 PASS.
- NeoStep smoke: 382/0 (380 baseline + 2 probes, no regression).
- FULL SMOKE: 122 -> 118 (4 flipped, exactly the Stelem_Any cluster).
- Legacy-neutral: plain Debug + useRegister=true -> 4 tests + 2 probes PASS.

## Files
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (Stelem_Any
  else-branch, ~5552-5594; Neo-gated by file -> Legacy-neutral by construction).
- `ILRuntimeTestBase/TestFramework/TestClass3.cs` (+9: host helper
  `SumTestVector3ArrayElems` in TestCLRBinding).
- `TestCases/NeoStepStelemAnyVtElementTest.cs` (new, 2 TCs).
