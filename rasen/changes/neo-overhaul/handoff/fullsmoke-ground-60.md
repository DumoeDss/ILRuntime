# Full Neo Smoke Grounding -- 2026-07-14 (FRESH 60)

GROUNDING report. All facts below are observed directly from a FRESH real run on
branch `features/object-model-overhaul` (HEAD at start of neo-recluster-60). No
reliance on the STALE 189-grounding for pass/fail status -- this re-runs the full
smoke and re-clusters the CURRENT failures.

- Build: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental -p:UseSharedCompilation=false` (0 errors) + `dotnet build TestCases/TestCases.csproj -c Debug` (0 errors).
- Run (NO name filter = full TestCases suite, Neo build):
  `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true`
- Raw log: `.tmp-recluster60-ground.log` (144,913 lines; heavy JIT/optimizer output is expected under `OUTPUT_JIT_RESULT`).
- Neo mode confirmed: failure stack traces run through `ILIntepreter.ExecuteNeo(...)` / `ILIntepreter.Neo.cs`.

## Section 1 -- Totals

| Metric | Value |
|---|---|
| Ran | 928 |
| Failed | 60 |
| Ignored | 20 |
| Todos | 7 |
| Final line | `Ran 928 tests, 60 failded, 20 ignored, 7 todos` |
| Exit | 0 (graceful; full 60-block failure list + summary emitted) |

The STALE 189-grounding is obsolete: 189 -> 60 via the 25+ wave-2 children
(C1 delegate-adapter, C2 ILType-cast, C4 callvirt-this, etc. are all FIXED).
This file re-clusters the SURVIVING 60 by real exception + top Neo.cs frame.

## Section 2 -- Cluster table (exception / top Neo.cs frame -> size)

Ordered by size. "single-root?" = whether the cluster shares ONE code root
(Y = one fix can green the whole cluster; N = grab-bag of distinct assertions).

| # | Cluster (exception + top frame) | Size | single-root? | Tests |
|---|---|---|---|---|
| A | `throw new Exception()` test-assertion @ Neo.cs:6637 (System.Exception) | 13 | N (grab-bag) | DelegateTest42, JsonTest9, ExpTest_10.UnitTest_1020, ExpTest_20.UnitTest_TestInline01/UnitTest_TestFCP/UnitTest_TestStackRegisterTransition3, ReflectionTest25, RefOutTest.UnitTest_NestedGenericRefOut, StaticTest.UnitTest_StaticTest05, StructTests.StructTest6, StructTests.StructTest12, TestValueTypeBinding.UnitTest_10046, TestValueTypeBinding.UnitTest_10051 |
| B | `unbox.any T` reference-type T @ Neo.cs:5447(NRE)/5456(InvCast)/5484(InvCast) | **6** | **Y** | GenericMethodTest9, GenericMethodTest15, InheritanceTest06, InheritanceTest18, GenericMethodTest.GenericStaticMethodTest19, RefOutTest.UnitTest_RefOutNull2 |
| H | Hotfix patched-IL via LEGACY `Execute(StackObject*)` (not Neo.cs) | 6 | ? (Legacy-path) | HotfixBasicTestCases.Test03/Test04/Test05/Test07, HotfixTestGenericTestCases.Test02, HotfixTestInheritanceTestCases.Test03 |
| E | raw ldfld/stfld field-access @ Neo.cs:4533/4549/4794/4810/4818/4839 | 9 | N (sub-split) | DelegateExtTest01/02, DelegateTest01 (4533 NIE "CLR obj via IL-instance path, Owner Int32"), Test05.TestStructDictionary (4533 NRE), ExpTest_10.UnitTest_10023 (4549), UnitTest_1008 (4794), UnitTest_10022 (4810), Test01.UnitTest_Generics/Generics2 (4818), RegisterVMTest04 (4839) |
| D | autogen-binding invoke @ Neo.cs:3960/4015/3425 (callvirt.clr -> *_Neo stub) | 8 | N | CLRBindingTest07/08 (LoadAsset_1_Neo), ReflectionTest06 (FieldInfo.SetValue), ReflectionTest10 (Enum.ToObject runtime-Type), StructTests.StructTest11 (List.Add_0_Neo), MyTest.Test (IEnumerator.get_Current), RefOutTest.UnitTest_RefCLREnum (enum& marshal), Test05.TestGenericMethod2 (Convert.ChangeType) |
| G | ldind/stobj/ldelema/ldlen misc @ Neo.cs:5684/6125/6126/6407/6422/6597 | 6 | N | ReflectionTest14 (ldlen), ExpTest_10.UnitTest_Struct/UnitTest_Struct2 (ldind.i4), RefOutTest.UnitTest_GenericsRefOut/GenericsRefOut2 (stobj), RefOutTest.UnitTest_ArrayReferenceTest (ldelema) |
| C | interface callvirt @ Neo.cs:1446/1453 (ResolveNeoCallvirtInterfaceTarget) | 4 | partial | InheritanceTest_Interface, InheritanceTest_Interface2, Test05.TestGenericStruct (1453 "does not implement interface slot 0"), TestAs03 (1446 "requires ILTypeInstance this, CLR object") |
| F | `NeoMarshalByrefFieldToSlot` @ Neo.cs:519 (Dict.TryGetValue out param) | 2 | Y | InheritanceTest21, InheritanceTest22 |
| I | delegate/generic callvirt misc @ Neo.cs:1471/7002 + MakeGenericType | 3 | N | DelegateTest43 (1471 callvirt.clr this=null), DelegateTest36 (Type.MakeGenericType NotSupported), GenericMethodTest11 (7002 constrained->IComparable binding cast) |
| J | solo | 1 | - | DelegateTest19 (Neo.cs:4061 List.get_Item via binding, IndexOOB) |
| K | solo AV-class | (0) | - | (no AccessViolation in this run -- the prior lone AV is gone) |

(Totals overlap slightly where a test could be read two ways; the precise
partition is the 60 individual entries below. The cluster table is for
triage. Cluster B is the LARGEST single-root cluster and the target of this
child.)

## Section 3 -- Target cluster B detail (unbox.any reference-type T) -- the FIX target

All 6 fail in the `case OpCodeREnum.Unbox: case OpCodeREnum.Unbox_Any:` arm
(`ILIntepreter.Neo.cs:5441-5548`). The Neo arm ONLY handles value-type / enum /
primitive unboxing and THROWS for a reference-type T (IL class / interface),
whereas Legacy (`ILIntepreter.Register.cs:4032-4158`) handles a reference-type T
as a plain `AssignToRegister(..., obj)` reference copy and treats a null source
as a no-op ("Nothing to do with null").

| Test | T (the unbox.any target) | top line | exception |
|---|---|---|---|
| GenericMethodTest9 | `TestInterface` (interface) | 5484 | InvalidCastException |
| GenericMethodTest15 | `Man` (IL class) | 5484 | InvalidCastException |
| InheritanceTest06 | `MyClass` (IL class) | 5484 | InvalidCastException |
| InheritanceTest18 | `TestCls5` (IL class, byref generic) | 5456 | InvalidCastException |
| GenericStaticMethodTest19 | `testConstrainsA` (IL class; source null) | 5447 | NullReferenceException |
| RefOutTest.UnitTest_RefOutNull2 | `TestClass222` (IL class; source null) | 5447 | NullReferenceException |

Root cause (Neo-vs-Legacy): Roslyn lowers `(T)result` / `p as T` / generic
returns on a generic type parameter T as `unbox.any T` (valid for both ref and
val T). For a reference-type T, `unbox.any` is semantically `castclass` (a
reference copy; null passes through). The Neo arm resolves `t`, then if
`ilType != null` and the type is a class (not enum/prim/valuetype) it falls to
`throw new InvalidCastException()` at 5484; and it throws `NullReferenceException`
at 5447 for `srcIdx < 0` BEFORE knowing whether T is a reference type (so a null
source for a ref-type T throws instead of yielding null).

FIX (Neo-only, mirrors Legacy): add a reference-type branch at the TOP of the
Unbox/Unbox_Any arm -- when `t != null && !t.IsValueType`, write the source
reference to the dest ref slot (or `-1` for null), then break. Discriminator
`!t.IsValueType` exactly mirrors Legacy's `t.IsValueType` branch boundary, so
all value-type / enum / primitive paths are untouched. Reference-write
convention mirrors the Isinst/Castclass arms (Neo.cs:5569-5576):
`mStack[frameRefBase + dstRefOffset] = obj; *(int*)(frameBase+DstOffset) = dstIdx;`
else `-1`.

## Section 4 -- Complete failure list (60, each with exception + top frame)

- CLRBindingTest07 -- InvalidCast (String->TestCLRBinding) @ LoadAsset_1_Neo -> InvokeNeoClrMethod Neo.cs:1268 -> ExecuteNeo Neo.cs:3960
- CLRBindingTest08 -- ArgumentOutOfRangeException (List.get_Item) @ LoadAsset_1_Neo -> Neo.cs:3960
- DelegateExtTest01 -- NIE "Step 17/13b ... CLR object via IL-instance path ... Owner type: System.Int32" @ Neo.cs:4533
- DelegateExtTest02 -- (same) @ Neo.cs:4533
- DelegateTest01 -- (same) @ Neo.cs:4533
- DelegateTest19 -- ArgumentOutOfRangeException (List.get_Item) @ Neo.cs:4061
- DelegateTest36 -- NotSupportedException (Type.MakeGenericType) @ binding -> Neo.cs:3960
- DelegateTest42 -- Exception @ Neo.cs:6637 (throw handler)
- DelegateTest43 -- NRE "Neo callvirt this is null" @ ResolveNeoCallvirtCLRTarget Neo.cs:1471
- GenericMethodTest9 -- InvalidCastException (unbox.any T) @ Neo.cs:5484
- GenericMethodTest11 -- InvalidCast (ILTypeInstance->IComparable<int>) @ binding -> Neo.cs:7002
- GenericMethodTest15 -- InvalidCastException (unbox.any T) @ Neo.cs:5484
- GenericStaticMethodTest19 -- NRE (unbox.any T null) @ Neo.cs:5447
- InheritanceTest_Interface -- MissingMethod "Callvirt_Interface ... slot 0" @ Neo.cs:1453
- InheritanceTest_Interface2 -- (same) @ Neo.cs:1453
- InheritanceTest06 -- InvalidCastException (unbox.any T) @ Neo.cs:5484
- InheritanceTest18 -- InvalidCastException (unbox.any T) @ Neo.cs:5456
- InheritanceTest21 -- NRE @ NeoMarshalByrefFieldToSlot Neo.cs:519
- InheritanceTest22 -- NRE @ NeoMarshalByrefFieldToSlot Neo.cs:519
- TestAs03 -- InvalidOperationException "Callvirt_Interface requires ILTypeInstance this (CLR object)" @ Neo.cs:1446
- JsonTest9 -- Exception @ Neo.cs:6637
- ExpTest_10.UnitTest_Struct -- IndexOutOfRange (ldind.i4) @ Neo.cs:6126
- ExpTest_10.UnitTest_Struct2 -- NRE (ldind.i4) @ Neo.cs:6125
- ExpTest_10.UnitTest_10022 -- NRE (stfld.r4) @ Neo.cs:4810
- ExpTest_10.UnitTest_10023 -- NRE (ldfld.r4) @ Neo.cs:4549
- ExpTest_10.UnitTest_1008 -- NRE (stfld.i4) @ Neo.cs:4794
- ExpTest_10.UnitTest_1020 -- Exception @ Neo.cs:6637
- ExpTest_20.UnitTest_TestInline01 -- Exception @ Neo.cs:6637
- ExpTest_20.UnitTest_TestFCP -- Exception @ Neo.cs:6637
- ExpTest_20.UnitTest_TestStackRegisterTransition3 -- Exception @ Neo.cs:6637
- ReflectionTest06 -- NRE (ILRuntimeFieldInfo.SetValue) @ binding -> Neo.cs:3960
- ReflectionTest10 -- ArgumentException "Type must be a type provided by the runtime" (Enum.ToObject) @ binding -> Neo.cs:3960
- ReflectionTest14 -- NRE (ldlen) @ Neo.cs:5684
- ReflectionTest25 -- Exception @ Neo.cs:6637
- RefOutTest.UnitTest_RefOutNull2 -- NRE (unbox.any T null) @ Neo.cs:5447
- RefOutTest.UnitTest_GenericsRefOut -- NRE (stobj) @ Neo.cs:6422
- RefOutTest.UnitTest_GenericsRefOut2 -- NRE (stobj) @ Neo.cs:6407
- RefOutTest.UnitTest_RefCLREnum -- ArgumentException (Int32->TestCLREnum&) @ CLRMethod.Invoke -> Neo.cs:3425
- RefOutTest.UnitTest_ArrayReferenceTest -- NRE (ldelema) @ Neo.cs:6597
- RefOutTest.UnitTest_NestedGenericRefOut -- Exception @ Neo.cs:6637
- RegisterVMTest04 -- ArgumentOutOfRangeException (List.get_Item, stfld.ref) @ Neo.cs:4839
- StaticTest.UnitTest_StaticTest05 -- Exception @ Neo.cs:6637
- StructTests.StructTest6 -- Exception @ Neo.cs:6637
- StructTests.StructTest11 -- ArgumentOutOfRangeException (List.Add_0_Neo) @ binding -> Neo.cs:3960
- StructTests.StructTest12 -- Exception @ Neo.cs:6637
- Test01.UnitTest_Generics -- NRE (stfld.ref) @ Neo.cs:4818
- Test01.UnitTest_Generics2 -- NRE (stfld.ref) @ Neo.cs:4818
- MyTest.Test -- InvalidCast (String->IEnumerator) @ binding -> Neo.cs:4015
- Test05.TestGenericMethod2 -- InvalidCast (Convert.ChangeType String->Int32) @ binding -> Neo.cs:3425
- Test05.TestStructDictionary -- NRE (ldfld.i4) @ Neo.cs:4533
- Test05.TestForEach -- NotSupportedException "error" @ Neo.cs:6637
- Test05.TestGenericStruct -- MissingMethod "Callvirt_Interface ... slot 0" @ Neo.cs:1453
- TestValueTypeBinding.UnitTest_10046 -- Exception @ Neo.cs:6637
- TestValueTypeBinding.UnitTest_10051 -- Exception @ Neo.cs:6637
- HotfixBasicTestCases.Test03 -- result=False (soft fail, no exception) [Legacy Execute path]
- HotfixBasicTestCases.Test04 -- ArgumentOutOfRangeException (RuntimeStack.PopFrame) @ ILIntepreter.cs:4819 [Legacy Execute(StackObject*)]
- HotfixBasicTestCases.Test05 -- result=False [Legacy Execute path]
- HotfixBasicTestCases.Test07 -- result=False [Legacy Execute path]
- HotfixTestGenericTestCases.Test02 -- NRE (CLRMethod.Invoke StackObject*) @ ILIntepreter.cs:2260 [Legacy Execute path]
- HotfixTestInheritanceTestCases.Test03 -- NIE "The method or operation is not implemented." @ ILIntepreter.cs:1317 [Legacy Execute path]

## Section 5 -- Notable changes vs the STALE 189-grounding

- The lone AccessViolation (UnitTest_10037) is GONE.
- C1 (delegate-adapter cast ~32), C2 (ILType->CLR cast ~14), C4 (callvirt-this/VTable ~20),
  C5 (parameterless-ctor 7), C7 (Push-missing JIT throw), C10/C11 (VT-array/struct OOB ~21),
  C12 (Type-must-be-runtime 10), C13 (delegate-adapter KeyNotFound 3) are all RESOLVED.
- Survivors morphed: the old C1 delegate-adapter cast is now a `ldfld` "CLR object via IL-
  instance path" NIE (3 tests, cluster E) -- a different, narrower shape. The Hotfix cluster
  (C9, was "is not bound!" 10) is now 6 tests running on the Legacy Execute(StackObject*) path.
- NEW dominant surface: cluster A (13 `throw`-handler assertions) -- these are 13 distinct
  test-internal assertions, each its own bug; NOT a single-fix cluster.
