# Full Neo Smoke Grounding -- 2026-07-14 (FRESH 38)

GROUNDING report. All facts below are observed directly from a FRESH real run on
branch `features/object-model-overhaul` (HEAD `50c98bed`, start of neo-recluster-38).
No reliance on the STALE 60-grounding for pass/fail status -- this re-runs the full
smoke and re-clusters the CURRENT failures.

- Build: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental -p:UseSharedCompilation=false` (0 errors) + `dotnet build TestCases/TestCases.csproj -c Debug -p:UseSharedCompilation=false` (0 errors).
- Run (NO name filter = full TestCases suite, Neo build):
  `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true`
- Raw log: `.tmp-r38-ground.log` (145,161 lines; heavy JIT/optimizer output is expected under `OUTPUT_JIT_RESULT`).
- Neo mode confirmed: failure stack traces run through `ILIntepreter.ExecuteNeo(...)` / `ILIntepreter.Neo.cs`.
- Final summary line: `Ran 932 tests, 38 failded, 20 ignored, 7 todos`. Exit 0 (graceful).

The STALE 60-grounding is obsolete: 60 -> 38 via the wave-2 children (struct VTable,
PushToStack/AssignFromStack bridge, stobj/ldobj ref-type, Ldarga/Ldfld_Value seeding,
generic-method JIT patches, etc.). This file re-clusters the SURVIVING 38 by real
exception + top Neo.cs frame.

## Section 1 -- Totals

| Metric | Value |
|---|---|
| Ran | 932 |
| Failed | 38 |
| Ignored | 20 |
| Todos | 7 |
| Final line | `Ran 932 tests, 38 failded, 20 ignored, 7 todos` |
| Exit | 0 (graceful; full 38-block failure list + summary emitted) |

## Section 2 -- Cluster table (exception / top Neo.cs frame -> size)

Ordered by size. "single-root?" = whether the cluster shares ONE code root
(Y = one fix can green the whole cluster; N = grab-bag of distinct assertions).

| # | Cluster (exception + top frame) | Size | single-root? | Tests |
|---|---|---|---|---|
| A | `throw new Exception()` test-assertion @ Neo.cs:6808 (CIL Throw handler) | 13 | N (grab-bag -- each test throws its OWN assertion) | DelegateTest42, ExpTest_10.UnitTest_1020, ExpTest_20.UnitTest_TestInline01/TestFCP/TestStackRegisterTransition3, ReflectionTest25, RefOutTest.UnitTest_NestedGenericRefOut, StaticTest.UnitTest_StaticTest05, StructTests.StructTest6, StructTests.StructTest12, Test05.TestForEach (NotSupported "error"), TestValueTypeBinding.UnitTest_10046/10051 |
| B | autogen-binding / reflection invoke @ InvokeNeoClrMethod Neo.cs:1268 | 7 | N (distinct bindings) | DelegateTest36 (Type.MakeGenericType), ReflectionTest06 (FieldInfo.SetValue), ReflectionTest10 (Enum.ToObject), StructTests.StructTest11 (List.Add_0_Neo OOB), GenericMethodTest11 (constrained->IComparable cast), MyTest.Test (IEnumerator.get_Current String->IEnumerator), Test05.TestGenericMethod2 (Convert.ChangeType String->Int32) |
| C | `NeoMarshalByrefFieldToSlot` @ Neo.cs:519 (byref out/ref param field-marshal) | 3 | **Y (ref-field sub-root for 2)** | InheritanceTest21, InheritanceTest22 (out <IL ref field>), RefOutTest.UnitTest_GenericsRefOut (ref <CLR-struct static field>, possibly distinct sub-root) |
| D | raw `ldfld.i4` field-access @ Neo.cs:4567 (GetNeoILInstance "Owner type: System.Int32") | 3 | **Y (delegate-ext-method arg marshal)** | DelegateExtTest01, DelegateExtTest02, DelegateTest01 (identical msg+frame+IL) + Test05.TestStructDictionary (NRE, same frame) |
| E | raw `stfld.ref` field-access @ Neo.cs:4852/4873 | 3 | partial | Test01.UnitTest_Generics/Generics2 (4852 NRE), RegisterVMTest04 (4873 ArgOOB) |
| F | `ldind.i4` / `ldlen` misc @ Neo.cs:6186/6187/5745 | 3 | N | ExpTest_10.UnitTest_Struct (6187 IOOB), ExpTest_10.UnitTest_Struct2 (6186 NRE), ReflectionTest14 (5745 ldlen NRE) |
| G | delegate / generic callvirt misc @ Neo.cs:1505/6902/4095 | 4 | N | DelegateTest19 (4095 List.get_Item OOB), DelegateTest43 (1505 callvirt.clr this=null), RefOutTest.GenericsRefOut2 (6902 "constrained not followed by callvirt") |
| H | Hotfix patched-IL via LEGACY `Execute(StackObject*)` (ILIntepreter.cs) | 2 | Y (Neo PushToStack/AssignFromStack field-index on hotfix types) | HotfixBasicTest04 (AssignFromStack field idx 1 OOB), HotfixBasicTest05 (PushToStack field idx 0 OOB) |

(40 separator blocks appear in the log but 2 are header/footer; the precise
partition is the 38 entries in Section 4. Cluster A is the LARGEST by frame but is a
grab-bag of 13 distinct test-internal assertions -- NOT a single-fix cluster.
Cluster C is the LARGEST single-root cluster and the target of this child.)

## Section 3 -- Target cluster C detail (byref out/ref param on an IL ref field) -- the FIX target

InheritanceTest21/22 fail at `NeoMarshalByrefFieldToSlot` Neo.cs:519 (reached via
`CopyNeoCallArguments` :438, the forward byref deref before `Dictionary.TryGetValue`).
The call is `dic.TryGetValue(1, out inheritanceTest.crossClass)` where `crossClass`
is a REFERENCE-typed IL instance FIELD (`CrossClass`, an IL class).

Root cause (Neo-vs-Legacy): `ldflda &ili.<refField>` (a reference-typed field of a
heap IL class) is JIT-stamped with `NeoLdfldaHeapIlRefFieldMarker` (JITCompiler.cs
:3346-3347) and the runtime Ldflda arm's `heapIlRefFieldMarker` branch
(ILIntepreter.Neo.cs:2129-2148) produces the byref `(objIdx, ReferenceOffset)` with
NO flag bit. The reference field's storage is `ManagedObjects[ReferenceOffset]`
(NOT `Primitives[off]`). The consumers `stind_ref`/`ldind_ref` ALREADY route this
byref shape to `ManagedObjects[off]` (content-based `mStack[objIdx] is ILTypeInstance`,
ILIntepreter.Neo.cs:6277-6295 / 6340+). BUT `NeoMarshalByrefFieldToSlot` (the shared
byref-param field marshal for `ref`/`out` CLR-method params) does NOT: its
ILTypeInstance branch (Neo.cs:470-520) only handles the F-10 boxed-struct case
(flag bit) and otherwise treats `off` as a Primitives byte offset -- so a reference
field byref dereferences `ili.Primitives[ReferenceOffset]` (wrong / OOB) and NREs.

The CLR-OBJECT-field branch of the SAME method (Neo.cs:609-665) handles reference
fields correctly (elemType-based: read = park object on mStack + write mStack index
to slot's leading int; write = read mStack index from slot + store object). The fix
mirrors that branch for the IL-instance reference-field case.

| Test | byref shape | top line | exception |
|---|---|---|---|
| InheritanceTest21 | `out inheritanceTest.crossClass` (IL ref field) | 519 | NRE |
| InheritanceTest22 | `out inheritanceTest.crossClass` (IL ref field) | 519 | NRE |
| RefOutTest.UnitTest_GenericsRefOut | `ref <TestStruct static field>` (CLR-struct, F-10-ish) | 519 | NRE |

GenericsRefOut's byref targets a CLR-struct STATIC field (boxed-struct storage) --
likely a distinct sub-root (ldsflda may not set the F-10 flag for a static CLR-struct
field). The elemType-based reference-field fix is confirmed to green InheritanceTest21/
22; GenericsRefOut is a bonus if it shares the routing.

Legacy parity: InheritanceTest2* (5 ran/0 fail) and GenericsRefOut (3 ran/0 fail)
and DelegateExtTest (4 ran/0 fail) ALL PASS on plain Debug + useRegister=true.

## Section 4 -- Complete failure list (38, each with exception + top frame + failing IL)

- DelegateExtTest01 -- NIE "Step 17/13b ... Owner type: System.Int32" @ Neo.cs:4567 (ldfld.i4 in delegate-ext-method IntTest->AddValue)
- DelegateExtTest02 -- (same) @ Neo.cs:4567
- DelegateTest01 -- (same) @ Neo.cs:4567
- DelegateTest19 -- ArgOOB (List.get_Item) @ Neo.cs:4095 (ret)
- DelegateTest36 -- NotSupported "Derived classes must provide an implementation" @ InvokeNeoClrMethod Neo.cs:1268 -> 3994 (Type.MakeGenericType)
- DelegateTest42 -- Exception @ Neo.cs:6808 (throw)
- DelegateTest43 -- NRE "Neo callvirt this is null" @ ResolveNeoCallvirtCLRTarget Neo.cs:1505 -> 3967 (Action.Invoke)
- GenericMethodTest11 -- InvalidCast (ILTypeInstance->IComparable<int>) @ InvokeNeoClrMethod 1268 -> 7173 (constrained. T)
- InheritanceTest21 -- NRE @ NeoMarshalByrefFieldToSlot Neo.cs:519 -> 3883 (out IL ref field TryGetValue)
- InheritanceTest22 -- NRE @ NeoMarshalByrefFieldToSlot Neo.cs:519 -> 3883 (out IL ref field TryGetValue)
- ExpTest_10.UnitTest_Struct -- IndexOutOfRange (ldind.i4) @ Neo.cs:6187
- ExpTest_10.UnitTest_Struct2 -- NRE (ldind.i4) @ Neo.cs:6186
- ExpTest_10.UnitTest_1020 -- Exception @ Neo.cs:6808 (throw)
- ExpTest_20.UnitTest_TestInline01 -- Exception @ Neo.cs:6808 (throw)
- ExpTest_20.UnitTest_TestFCP -- Exception @ Neo.cs:6808 (throw)
- ExpTest_20.UnitTest_TestStackRegisterTransition3 -- Exception @ Neo.cs:6808 (throw)
- ReflectionTest06 -- NRE (FieldInfo.SetValue) @ InvokeNeoClrMethod 1268 -> 3994
- ReflectionTest10 -- ArgumentException "Type must be a type provided by the runtime" (Enum.ToObject) @ 1268 -> 3994
- ReflectionTest14 -- NRE (ldlen) @ Neo.cs:5745
- ReflectionTest25 -- Exception @ Neo.cs:6808 (throw)
- RefOutTest.UnitTest_GenericsRefOut -- NRE @ NeoMarshalByrefFieldToSlot Neo.cs:519 -> 3377 (ref TestStruct& DoTest)
- RefOutTest.UnitTest_GenericsRefOut2 -- NIE "Step 17: Constrained not immediately followed by a callvirt" @ Neo.cs:6902 (constrained. T)
- RefOutTest.UnitTest_NestedGenericRefOut -- Exception @ Neo.cs:6808 (throw -- nested-generic ref write-back)
- RegisterVMTest04 -- ArgOOB (List.get_Item, stfld.ref) @ Neo.cs:4873
- StaticTest.UnitTest_StaticTest05 -- Exception @ Neo.cs:6808 (throw)
- StructTests.StructTest6 -- Exception @ Neo.cs:6808 (throw -- out struct param not written back, TryGetValue out cube)
- StructTests.StructTest11 -- ArgOOB (List.Add_0_Neo) @ InvokeNeoClrMethod 1268 -> 3994
- StructTests.StructTest12 -- Exception @ Neo.cs:6808 (throw)
- Test01.UnitTest_Generics -- NRE (stfld.ref) @ Neo.cs:4852
- Test01.UnitTest_Generics2 -- NRE (stfld.ref) @ Neo.cs:4852
- MyTest.Test -- InvalidCast (String->IEnumerator) @ InvokeNeoClrMethod 1268 -> 795 -> 4049 (IEnumerator.get_Current)
- Test05.TestGenericMethod2 -- InvalidCast (Convert.ChangeType String->Int32) @ InvokeNeoClrMethod 1268 -> 795 -> 3459
- Test05.TestStructDictionary -- NRE (ldfld.i4) @ Neo.cs:4567
- Test05.TestForEach -- NotSupported "error" @ Neo.cs:6808 (throw)
- TestValueTypeBinding.UnitTest_10046 -- Exception @ Neo.cs:6808 (throw)
- TestValueTypeBinding.UnitTest_10051 -- Exception @ Neo.cs:6808 (throw)
- HotfixBasicTest04 -- TypeLoadException "Neo AssignFromStack: field index 1 out of range" @ ILIntepreter.cs:2558 [Legacy Execute path]
- HotfixBasicTest05 -- TypeLoadException "Neo PushToStack: field index 0 out of range" @ ILIntepreter.cs:2637 [Legacy Execute path]

## Section 5 -- Notable changes vs the STALE 60-grounding

- 60 -> 38 (delta -22) via the wave-2 children shipped since the 60-grounding:
  struct VTable + explicit-impl dotted-name + adaptor unwrap (cluster-C),
  ILTypeInstance PushToStack/AssignFromStack eval-stack bridge (cluster-H Legacy path),
  stobj/ldobj reference-type + ldelema null-element (cluster-G),
  Ldarga/Ldfld_Value producer seeding (cluster-E),
  GenericMethodTemplate JIT patches, the LowerNeoOffsets Newobj-shape fix (C7),
  F-10 IL-instance CLR-struct-field, and the raw-Ldfld/Stfld CLR-object-VT-field family
  (children 24/26/27/29).
- The lone AccessViolation remains GONE.
- Survivors morphed: cluster A (13 throw-assertions) is STILL the largest frame-group
  but remains a grab-bag of distinct test-internal assertions -- NOT single-fix.
- NEW clearest single-root surfaces: cluster C (byref out/ref param on an IL ref field,
  3 tests) and cluster D (delegate-extension-method arg marshal, 3 identical-msg tests).
