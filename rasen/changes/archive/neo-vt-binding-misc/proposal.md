# neo-vt-binding-misc (wave-2 child C11 of neo-overhaul)

## Problem
At the 80-failure full Neo smoke baseline (2026-07-14), the C11 cluster
(TestValueTypeBinding + StructTests misc) still has ~14 failing tests. Re-audit
(a REAL full smoke run, `.tmp-c11-baseline.log`, `Ran 922 tests, 80 failed`)
sub-clusters them by root. The LARGEST single-root sub-cluster is the recurring
child-28 stale-autogen-binding class: Neo `*_Neo` stubs that were never
regenerated post-Step-13b and therefore DROP a CLR value-type RETURN or MISREAD
a CLR value-type ARGUMENT (`// TODO: CLR value type return in reflection
fallback: Step 13` / `// TODO: ByRef or unsupported ValueType parameters in
Neo`). The Legacy `#else` arms are correct (PushObject/ParseValue); only the
Neo arms are stale.

## The largest sub-cluster (3 C11 tests, one defect class)
- `TestValueTypeBinding.UnitTest_10039` -- `arg = TestVector3NoBinding.one;`
  routes to `get_one_0_Neo` which reads `.one` but never writes the return ->
  `arg` stays default -> `arg.x != 1` -> throw.
- `TestValueTypeBinding.UnitTest_10050` -- `structs[i]` routes to
  `List<TestVector3NoBinding>.get_Item_1_Neo` which reads `instance[index]` but
  never writes the return -> `item` stays default -> `item.x == 0` -> throw.
- `TestValueTypeBinding.UnitTest_10048` -- `cls.Vector2` routes to
  `TestVectorClass.get_Vector2_0_Neo` which reads the property but never writes
  the return -> `cls.Vector2.Z == 0` (and `set_Vector2_2_Neo` misreads the value
  arg as `default`). RESULT-FALSE.

## Fix
TWO sub-clusters fixed (both Neo-gated -> Legacy-neutral by construction).

### Fix A -- stale autogen Neo VT stubs (child-28 class; 3 C11 tests)
Hand-port the stale Neo stubs to the post-Step-13b generator template (mirror
the child-28 `ILRuntimeTest_TestFramework_TestVector3_Binding.cs` ports): read
VT args via `ILIntepreter.ReadNeoValueType(typeof(T), frameBase, ref curPrim,
GetNeoValueTypeManagedSize(typeof(T)))`; write VT returns via
`ILIntepreter.WriteNeoValueType(result, retDst, GetNeoValueTypeManagedSize(...))`
(guarded `if (__retDst != null)`, identical to child-28). All edits inside
`#if ENABLE_NEO_MODE`.
Files:
- `ILRuntimeTestBase/AutoGenerate/ILRuntimeTest_TestFramework_TestVector3NoBinding_Binding.cs`
  (get_one, get_zero, op_Multiply, op_Addition)
- `ILRuntimeTestBase/AutoGenerate/System_Collections_Generic_List_1_TestVector3NoBinding_Bi.cs`
  (get_Item)
- `ILRuntimeTestBase/AutoGenerate/ILRuntimeTest_TestFramework_TestVectorClass_Binding.cs`
  (get_Vector2, set_Vector2)

### Fix B -- Stsfld/Ldsfeld of an IL-static CLR-value-type field (engine; 3 tests)
An IL-class static field whose type is a CLR value type (e.g.
`static TestVector3NoBinding m_curPos`) is laid out by the Neo static-field
offset pass as a BOXED struct in `ManagedObjects` (the pass's `else` arm does
`ReferenceOffset++` for a non-IL ValueType -- only ILTypes carry
TotalPrimitiveSize). The `Stsfld`/`Ldsfeld` IL-static arms had branches only for
`IsPrimitive`, `ft is ILType`, and reference; a CLR-VT fell to the reference
branch, which read the struct's flat bytes as an mStack index ->
`ArgumentOutOfRangeException` at `List.get_Item`. Fix: a new
`else if (ft != null && ft.IsValueType)` branch in BOTH IL-static arms (after
the `ft is ILType vtil` branch -> matches a CLR ValueType exclusively) that
boxes/unboxes via `ReadNeoValueType`/`WriteNeoValueType` on `ft.TypeForCLR` at
`off.ReferenceOffset` -- byte-for-byte the CLR-static VT branch pattern
(`NeoClrVtStaticFieldIsUnsafe` guard retained for ref-field CLR structs).
File: `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`
(Stsfld arm ~4875, Ldsfeld arm ~5033).

## Verify (truth = full-smoke count, REAL run)
- Name-filter: the fixed tests PASS after each fix (FAIL on the 80-baseline).
- **FULL SMOKE delta: 80 -> 74 (-6).** Flipped green: UnitTest_10039/10048/10050
  (Fix A), UnitTest_10036/10037 + bonus TestCLREnum.Test06 (Fix B; a non-C11
  test that also reads/writes a CLR-VT IL-static field). 0 NEW RED (no
  regressions; failure-set diff confirmed).
- NeoStep **388/0** (no regression; was 388/0 pre-fix).
- Legacy-neutral: plain `Debug`+`useRegister=true` name-filter
  (`TestValueTypeBinding.UnitTest_100*`) = 30 ran / 0 failed (all 5 fixed tests
  pass under Legacy). Structural too: Fix A edits are inside `#if
  ENABLE_NEO_MODE`; Fix B is in the file-gated `ILIntepreter.Neo.cs`.

## Other C11 sub-clusters (REPORTED, not fixed here -- distinct roots)
- Stfld_Value with an in-frame (non-heap) owner -> NRE @ :4736 (StructTest3/
  StructTest14; the arm assumes a heap ILTypeInstance owner; an IL-struct LOCAL
  or an IL-struct `this` is emitted as plain Stfld_Value with no _Inline form).
- F-10 ManagedObjects tagged NIE @ :4601 (StructTest7/UnitTest_10051; the
  explicitly-deferred IL-instance CLR-struct-field shape, child-27/29 sibling).
- C10 List/array index OOB (StructTest8/StructTest11; autogen List binding
  `Add_0_Neo`/`get_Item` internal indexing).
- Grab-bag assertion failures (StructTest6 Dict value-semantics; StructTest12
  struct auto-property backing-field boxing; UnitTest_10046 delegate VT-return
  / byval-VT-param reassign).

## NOTE -- the broader stale-stub surface (follow-up)
20 autogen binding files still carry the `// TODO: CLR value type return in
reflection fallback: Step 13` (or `ByRef or unsupported ValueType parameters`)
stale Neo stubs (the Fix A class). Only the 3 C11-relevant ones were ported
here (TestVector3NoBinding, List<TestVector3NoBinding>, TestVectorClass
Vector2). The rest (JInt, TestStructB, Fixed64, Fixed64Vector2, DateTime,
IEnumerator KVP variants, the async builders [mostly bypassed by C8's
redirects], etc.) are not exercised by currently-failing C11 tests; a full GUI
regen (or a mechanical sweep) is the durable fix -- same recurring child-28
defect class.
