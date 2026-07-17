# Proposal: neo-array-vt-index-oob (wave2 C10)

## Why
Full Neo smoke (2026-07-13 grounding, after wave-2 C1/C4/C2/C12) sits at **122
failed**. Cluster C10 is the array/struct index-out-of-range bucket
(ArgumentOutOfRangeException + IndexOutOfRangeException). Re-audit shows C10 is
NOT one root -- it is 6+ distinct roots that happen to share an exception type.
This child fixes the **largest sub-cluster** and reports the rest honestly.

## Largest sub-cluster: Stelem_Any / Stelem_Ref value-type-element misread
The ExecuteNeo `case OpCodeREnum.Stelem_Any / Stelem_Ref` "CLR object array"
else-branch (the path taken when the array is NOT an `ILTypeInstance[]`)
assumed EVERY such array is a reference-element array: it read the value slot's
first int as an mStack index and `mStack[vIdx]`'d it.

But for a CLR VALUE-TYPE-element array (`TestVector3[]`, or `float[]` reached
via `stelem.any !T` on a generic `T[]`), the value slot holds the struct's FLAT
MANAGED BYTES, not an mStack index. The first int is the first field's IEEE bit
pattern (e.g. `1.0f == 0x3F800000 == 1065353216`) -> `mStack[1065353216]` ->
`ArgumentOutOfRangeException` ("Index was out of range ... collection").

Failing tests sharing this exact root (Neo.cs Stelem_Any arm, all hit the same
`mStack[vIdx]` line):
- `ArrayTest05`           (`new TestVector3[] { One, One2 }` initializer)
- `UnitTest_10035`        (`new TestVector3[3] { One, One, One }` initializer)
- `TestValueTypeBinding.Test03` (`a[i] = TestVector3.One` index assign)
- `Test03.TestUsingNested`  (generic `T[]` via `stelem.any !T`, T=float)

= **4 tests** (the largest single-root sub-cluster in C10).

## The fix (Neo-gated, runtime-only, mirrors child-26 Stobj array WRITE)
Discriminate by the runtime element type (`sa.GetType().GetElementType()`):
- value-type element -> read flat bytes at `ip->Operand4` into a boxed object
  via `ILIntepreter.ReadNeoValueType` + `Array.SetValue` (SetValue unboxes).
  Mirrors the Stobj CLR-value-type-array-element WRITE arm (child-26) and the
  byref-array write-back (child-25).
- reference element -> unchanged mStack-index path.

No JIT / optimizer / object-model / binding change. ~32 lines (mostly comments).

## What this child does NOT fix (distinct roots, honestly reported)
- `StructTest8`   -- Ldfld_R4 with a bad owner mStack index (Neo.cs:4313).
- `StructTest11`  -- Callvirt_CLR `List<Anim>.Add` struct arg (Neo.cs:3796).
- `UnitTest_10036`-- Stsfld IL-static struct ref-field (Neo.cs:4731).
- `ExpTest_10.UnitTest_Struct` -- Ldind_I4 `ins.Primitives` OOB (Neo.cs:5695).
- `UnitTest_10051`-- LowerNeoOffsets JIT "Push" (Optimizer.Neo.cs) = cluster C7.
- `CLRBindingTest08` -- order-dependent flake (passes in isolation, fails in
  full suite; unchanged by this fix).

## Truth
Full smoke delta: **122 -> 118** (4 flipped, exactly the Stelem_Any cluster).
NeoStep 382/0 (380 baseline + 2 probes). Legacy-neutral (plain Debug +
useRegister=true: 4 tests + 2 probes PASS).
