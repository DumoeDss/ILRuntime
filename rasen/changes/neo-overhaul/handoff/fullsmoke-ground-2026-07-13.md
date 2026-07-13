# Full Neo Smoke Grounding -- 2026-07-13

GROUNDING report. All facts below are observed directly from a real run. No
root-cause inference. No reliance on prior documents for pass/fail status.

- Build: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental` (0 errors) + `dotnet build TestCases/TestCases.csproj -c Debug` (0 errors).
- Run (NO name filter = full TestCases suite, Neo build): `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true`
- Raw log kept at: `.tmp-fullsmoke-ground.log` (142,840 lines; heavy JIT/optimizer output is expected under `OUTPUT_JIT_RESULT`).
- Neo mode confirmed: `Debug_Neo` defines `ENABLE_NEO_MODE`; `ILIntepreter.ExecuteNeo(` appears in failure stack traces (see Appendix).

## Section 1 -- Totals

| Metric | Value |
|---|---|
| Ran | 914 |
| Failed | 189 |
| Ignored | 20 |
| Todos | 6 |
| Final line printed | `Ran 914 tests, 189 failded, 20 ignored, 6 todos` |
| Exit | Graceful -- the final summary line is emitted (the CLI prints it only on normal completion). Numeric exit code was NOT captured (run was detached via `nohup`); no crash/hang is evidenced: the full 189-block failure list AND the final summary line were both emitted. |

## Section 2 -- Failure count by exception type (189 tests, each counted once)

| Exception type | Count |
|---|---|
| System.InvalidCastException | 56 |
| System.NullReferenceException | 45 |
| System.MissingMethodException | 23 |
| System.Exception (generic) | 19 |
| System.NotSupportedException | 10 |
| System.ArgumentException | 10 |
| System.ArgumentOutOfRangeException | 9 |
| System.Reflection.TargetException | 4 |
| System.ArgumentNullException | 4 |
| System.Collections.Generic.KeyNotFoundException | 3 |
| System.NotImplementedException | 2 |
| System.IndexOutOfRangeException | 2 |
| System.AccessViolationException | 1 |
| (RESULT-FALSE / no exception; test returned False) | 1 |
| **Total** | **189** |

Observed (no inference): only 2 of 189 failures are `NotImplementedException`. The bulk are `InvalidCastException` (56), `NullReferenceException` (45), `MissingMethodException` (23). The `System.Exception` (19) bucket includes 3 tests whose message is `Neo lowering could not find expected Push instructions for Call/Newobj.` (thrown from `Optimizer.Neo.cs:LowerNeoOffsets`); the rest are bare `Exception of type 'System.Exception' was thrown.` or custom assert messages.

## Section 3 -- Complete failure list (grouped by exception type)

Each row: `TestFullName` -- observed exception message (truncated ~130 chars). Messages are quoted verbatim from the run; not interpreted.

### System.InvalidCastException (56)

- `TestCases.CLRBindingTest.CLRBindingTest07` -- Unable to cast object of type 'System.String' to type 'ILRuntimeTest.TestFramework.TestCLRBinding'.
- `TestCases.DelegateExtTest.DelegateExtTest01` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`1[System.Int32]' to type 'ILRuntimeTest.TestF...
- `TestCases.DelegateInnerTest.TestRun` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.FunctionDelegateAdapter`2[System.Int32,System.Boolean]' to type 'Sy...
- `TestCases.DelegateTest.DelegateTest01` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`1[System.Int32]' to type 'ILRuntimeTest.TestF...
- `TestCases.DelegateTest.DelegateTest03` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.FunctionDelegateAdapter`2[System.Int32,System.Int32]' to type 'ILRu...
- `TestCases.DelegateTest.DelegateTest06` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`1[System.Int32]' to type 'ILRuntimeTest.TestF...
- `TestCases.DelegateTest.DelegateTest07` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`1[System.Int32]' to type 'ILRuntimeTest.TestF...
- `TestCases.DelegateTest.DelegateTest11` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`1[ILRuntimeTest.TestFramework.BaseClassTest]'...
- `TestCases.DelegateTest.DelegateTest18` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`1[ILRuntimeTest.TestFramework.TestCLREnum]' t...
- `TestCases.DelegateTest.DelegateTest19` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.FunctionDelegateAdapter`1[ILRuntimeTest.TestFramework.TestCLREnum]'...
- `TestCases.DelegateTest.DelegateTest20` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`1[System.Int32]' to type 'System.Action`1[Sys...
- `TestCases.DelegateTest.DelegateTest21` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.FunctionDelegateAdapter`2[System.Int32,System.Int32]' to type 'Syst...
- `TestCases.DelegateTest.DelegateTest41` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`2[System.Int64,System.Int64]' to type 'onChan...
- `TestCases.DelegateTest.DelegateTest43` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`3[System.Single,System.Double,System.Int32]' ...
- `TestCases.DelegateTest.DelegateTest45` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`3[System.Single,System.Double,System.Int32]' ...
- `TestCases.EnumTest.Test20` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.ILEnumTypeInstance' to type 'System.Enum'.
- `TestCases.EnumTest.Test22` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.ILTypeInstance' to type 'System.Enum'.
- `TestCases.GenericMethodTest.GenericExtensionMethod1Test1` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`1[System.Object]' to type 'System.Action`1[Sy...
- `TestCases.GenericMethodTest.GenericExtensionMethod1Test2` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`2[ILRuntimeTest.TestBase.ExtensionClass,Syste...
- `TestCases.GenericMethodTest.GenericExtensionMethod2Test1` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`1[System.Exception]' to type 'System.Action`1...
- `TestCases.GenericMethodTest.GenericExtensionMethod2Test2` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`2[ILRuntimeTest.TestBase.ExtensionClass,Syste...
- `TestCases.GenericMethodTest.GenericExtensionMethod2Test3` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`1[System.ArgumentException]' to type 'System....
- `TestCases.GenericMethodTest.GenericExtensionMethod2Test4` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`2[ILRuntimeTest.TestBase.ExtensionClass,Syste...
- `TestCases.GenericMethodTest.GenericExtensionMethod2Test5` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`1[System.Exception]' to type 'System.Action`1...
- `TestCases.GenericMethodTest.GenericExtensionMethod2Test6` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`2[ILRuntimeTest.TestBase.ExtensionClass`1[Sys...
- `TestCases.GenericMethodTest.GenericExtensionMethod2Test7` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`1[System.ArgumentException]' to type 'System....
- `TestCases.GenericMethodTest.GenericExtensionMethod2Test8` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`2[ILRuntimeTest.TestBase.ExtensionClass`1[Sys...
- `TestCases.GenericMethodTest.GenericMethodTest11` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.ILTypeInstance' to type 'System.IComparable`1[System.Int32]'.
- `TestCases.GenericMethodTest.GenericMethodTest14` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.FunctionDelegateAdapter`2[System.Collections.Generic.KeyValuePair`2...
- `TestCases.GenericMethodTest.GenericMethodTest15` -- Specified cast is not valid.
- `TestCases.GenericMethodTest.GenericMethodTest9` -- Specified cast is not valid.
- `TestCases.GenericMethodTest.GenericStaticMethodTest1` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter' to type 'System.Action'.
- `TestCases.GenericMethodTest.GenericStaticMethodTest2` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`1[ILRuntimeTest.TestBase.ExtensionClass]' to ...
- `TestCases.GenericMethodTest.GenericStaticMethodTest3` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.FunctionDelegateAdapter`1[System.Int32]' to type 'System.Func`1[Sys...
- `TestCases.GenericMethodTest.GenericStaticMethodTest4` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.FunctionDelegateAdapter`2[ILRuntimeTest.TestBase.ExtensionClass,Sys...
- `TestCases.GenericMethodTest.GenericStaticMethodTest5` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.FunctionDelegateAdapter`1[System.Threading.Tasks.Task]' to type 'Sy...
- `TestCases.GenericMethodTest.GenericStaticMethodTest6` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.FunctionDelegateAdapter`2[ILRuntimeTest.TestBase.ExtensionClass,Sys...
- `TestCases.GenericMethodTest.GenericStaticMethodTest7` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.FunctionDelegateAdapter`1[System.Threading.Tasks.Task`1[System.Int3...
- `TestCases.GenericMethodTest.GenericStaticMethodTest8` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.FunctionDelegateAdapter`2[ILRuntimeTest.TestBase.ExtensionClass,Sys...
- `TestCases.InheritanceTest.InheritanceTest01` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.ILTypeInstance' to type 'ILRuntimeTest.TestFramework.ClassInheritan...
- `TestCases.InheritanceTest.InheritanceTest02` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.ILTypeInstance' to type 'ILRuntimeTest.TestFramework.ClassInheritan...
- `TestCases.InheritanceTest.InheritanceTest03` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.ILTypeInstance' to type 'Adaptor'.
- `TestCases.InheritanceTest.InheritanceTest04` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.ILTypeInstance' to type 'ILRuntimeTest.TestFramework.ClassInheritan...
- `TestCases.InheritanceTest.InheritanceTest06` -- Specified cast is not valid.
- `TestCases.InheritanceTest.InheritanceTest10` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.FunctionDelegateAdapter`2[ILRuntime.Runtime.Intepreter.ILTypeInstan...
- `TestCases.InheritanceTest.InheritanceTest14` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.ILTypeInstance' to type 'ILRuntimeTest.TestFramework.ClassInheritan...
- `TestCases.InheritanceTest.InheritanceTest16` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.ILTypeInstance' to type 'ILRuntimeTest.TestFramework.TestClass4'.
- `TestCases.InheritanceTest.InheritanceTest21` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.ILTypeInstance' to type 'Adaptor'.
- `TestCases.InheritanceTest.InheritanceTest22` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.ILTypeInstance' to type 'Adaptor'.
- `TestCases.RefOutTest.UnitTest_OutTest` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.ILTypeInstance' to type 'Adaptor'.
- `TestCases.StructTests.StructTest6` -- Unable to cast object of type 'System.String' to type 'ILRuntime.Runtime.Intepreter.ILTypeInstance'.
- `TestCases.Test03.Test05` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.FunctionDelegateAdapter`2[System.Byte,System.Boolean]' to type 'Sys...
- `TestCases.Test05.TestGenericMethod2` -- Invalid cast from 'System.String' to 'System.Int32'.
- `TestCases.TestAs.TestAs03` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.ILTypeInstance' to type 'Adaptor'.
- `TestCases.TestIs.TestInterface` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.ILTypeInstance' to type 'System.IDisposable'.
- `TestCases.TestValueTypeBinding.UnitTest_10046` -- Unable to cast object of type 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`1[ILRuntimeTest.TestFramework.TestVector3]' t...

### System.NullReferenceException (45)

- `TestCases.ArrayTest.ArrayBindTest` -- Object reference not set to an instance of an object.
- `TestCases.ArrayTest.ArrayTest09` -- Object reference not set to an instance of an object.
- `TestCases.DelegateTest.DelegateTest16` -- Object reference not set to an instance of an object.
- `TestCases.DelegateTest.DelegateTest17` -- Object reference not set to an instance of an object.
- `TestCases.DelegateTest.DelegateTest23` -- Object reference not set to an instance of an object.
- `TestCases.ExpTest_10.UnitTest_10022` -- Object reference not set to an instance of an object.
- `TestCases.ExpTest_10.UnitTest_10023` -- Object reference not set to an instance of an object.
- `TestCases.ExpTest_10.UnitTest_1008` -- Object reference not set to an instance of an object.
- `TestCases.ExpTest_10.UnitTest_Struct2` -- Object reference not set to an instance of an object.
- `TestCases.ExpTest_20.UnitTest_TestFCP2` -- Object reference not set to an instance of an object.
- `TestCases.GCTest.TestDicEnumerator` -- Object reference not set to an instance of an object.
- `TestCases.GenericMethodTest.GenericMethodTest18` -- Neo callvirt this is null.
- `TestCases.GenericMethodTest.GenericStaticMethodTest19` -- Object reference not set to an instance of an object.
- `TestCases.GenericMethodTest.GenericTest` -- Object reference not set to an instance of an object.
- `TestCases.InheritanceTest.InheritanceTest24` -- Object reference not set to an instance of an object.
- `TestCases.JsonTest.JsonTest2` -- Object reference not set to an instance of an object.
- `TestCases.MyTest.Test` -- Object reference not set to an instance of an object.
- `TestCases.MyTest.UnitTest_Test1` -- Neo callvirt this is null.
- `TestCases.RefOutTest.UnitTest_ArrayReferenceTest` -- Object reference not set to an instance of an object.
- `TestCases.RefOutTest.UnitTest_GenericsRefOut` -- Object reference not set to an instance of an object.
- `TestCases.RefOutTest.UnitTest_GenericsRefOut2` -- Object reference not set to an instance of an object.
- `TestCases.RefOutTest.UnitTest_RefOutNull2` -- Object reference not set to an instance of an object.
- `TestCases.ReflectionTest.ReflectionTest04` -- Neo callvirt this is null.
- `TestCases.ReflectionTest.ReflectionTest06` -- Object reference not set to an instance of an object.
- `TestCases.ReflectionTest.ReflectionTest14` -- Object reference not set to an instance of an object.
- `TestCases.ReflectionTest.ReflectionTest19` -- Neo callvirt this is null.
- `TestCases.RegisterVMTest.RegisterVMTest04` -- Object reference not set to an instance of an object.
- `TestCases.SimpleTest.EqualsTest` -- Object reference not set to an instance of an object.
- `TestCases.SimpleTest.StaticTest` -- Neo callvirt this is null.
- `TestCases.StaticTest.UnitTest_StaticTest03` -- Neo callvirt this is null.
- `TestCases.StaticTest.UnitTest_StaticTest05` -- Object reference not set to an instance of an object.
- `TestCases.StructTests.StructTest14` -- Object reference not set to an instance of an object.
- `TestCases.StructTests.StructTest3` -- Object reference not set to an instance of an object.
- `TestCases.StructTests.StructTest4` -- Object reference not set to an instance of an object.
- `TestCases.Test01.UnitTest_Generics` -- Object reference not set to an instance of an object.
- `TestCases.Test01.UnitTest_Generics2` -- Object reference not set to an instance of an object.
- `TestCases.Test05.TestForEach` -- Object reference not set to an instance of an object.
- `TestCases.Test05.TestForEachTry` -- Object reference not set to an instance of an object.
- `TestCases.Test05.TestReturn` -- Object reference not set to an instance of an object.
- `TestCases.Test05.TestStructDictionary` -- Object reference not set to an instance of an object.
- `TestCases.Test05.UnitTest_Out2` -- Object reference not set to an instance of an object.
- `TestCases.TestValueTypeBinding.UnitTest_10025` -- Object reference not set to an instance of an object.
- `TestCases.TestValueTypeBinding.UnitTest_10030` -- Neo callvirt this is null.
- `TestCases.TestValueTypeBinding.UnitTest_10034` -- Object reference not set to an instance of an object.
- `TestCases.Vector3.get_One` -- Object reference not set to an instance of an object.

### System.MissingMethodException (23)

- `TestCases.DelegateTest.DelegateTest36` -- Neo callvirt cannot resolve VTable slot for System.Type GetType() on TestCases.DelegateTest/A.
- `TestCases.DelegateTest.DelegateTest37` -- Neo callvirt cannot resolve VTable slot for System.Type GetType() on TestCases.DelegateTest/A.
- `TestCases.DelegateTest.DelegateTest38` -- Neo callvirt cannot resolve VTable slot for System.Type GetType() on TestCases.DelegateTest/A.
- `TestCases.DelegateTest.DelegateTest39` -- Neo callvirt cannot resolve VTable slot for System.Type GetType() on TestCases.DelegateTest/A.
- `TestCases.DelegateTest.DelegateTest40` -- Neo callvirt cannot resolve VTable slot for System.Type GetType() on TestCases.DelegateTest/A.
- `TestCases.EnumTest.Test15` -- Neo callvirt cannot resolve VTable slot for System.Type GetType() on TestCases.EnumTest/SystemType.
- `TestCases.EnumTest.Test30` -- Neo callvirt cannot resolve VTable slot for Boolean Equals(System.Object) on TestCases.EnumTest/TestEnumFlag.
- `TestCases.EnumTest.Test32` -- Neo callvirt cannot resolve VTable slot for Boolean Equals(System.Object) on TestCases.EnumTest/TestEnumFlag.
- `TestCases.GenericMethodTest.GenericMethodTest4` -- No parameterless constructor defined for type 'ILRuntime.Runtime.Intepreter.ILTypeInstance'.
- `TestCases.InheritanceTest.InheritanceTest19` -- Neo callvirt cannot resolve VTable slot for Void Dispose() on TestCases.InheritanceTest/TestExplicitInterface.
- `TestCases.InheritanceTest.InheritanceTest_Interface` -- Neo Callvirt_Interface: type TestCases.TestCls3 does not implement interface TestCases.InterfaceTest2 (method slot 0).
- `TestCases.InheritanceTest.InheritanceTest_Interface2` -- Neo Callvirt_Interface: type TestCases.InheritanceTest/TestA does not implement interface TestCases.InheritanceTest/ITest (meth...
- `TestCases.JsonTest.JsonTest3` -- No parameterless constructor defined for type 'ILRuntime.Runtime.Intepreter.ILTypeInstance'.
- `TestCases.JsonTest.JsonTest4` -- No parameterless constructor defined for type 'ILRuntime.Runtime.Intepreter.ILTypeInstance'.
- `TestCases.JsonTest.JsonTest6` -- No parameterless constructor defined for type 'ILRuntime.Runtime.Intepreter.ILTypeInstance'.
- `TestCases.JsonTest.JsonTest8` -- No parameterless constructor defined for type 'ILRuntime.Runtime.Intepreter.ILTypeInstance'.
- `TestCases.JsonTest.JsonTest9` -- No parameterless constructor defined for type 'ILRuntime.Runtime.Intepreter.ILTypeInstance'.
- `TestCases.ReflectionTest.ReflectionTest03` -- Neo callvirt cannot resolve VTable slot for System.Type GetType() on TestCases.ReflectionTest/TestCls.
- `TestCases.ReflectionTest.ReflectionTest10` -- Neo callvirt cannot resolve VTable slot for System.Type GetType() on TestCases.ReflectionTest/Tx.
- `TestCases.ReflectionTest.ReflectionTest11` -- Neo callvirt cannot resolve VTable slot for System.Type GetType() on TestCases.ReflectionTest/test24Class.
- `TestCases.ReflectionTest.TestMethodParametersInfo` -- Neo callvirt cannot resolve VTable slot for System.Type GetType() on TestCases.ReflectionTest/TestAttribute.
- `TestCases.StaticTest.UnitTest_StaticTest01` -- Neo callvirt cannot resolve VTable slot for System.Type GetType() on TestCases.StaticTest/Test_A.
- `TestCases.Test05.TestGenericStruct` -- Neo Callvirt_Interface: type TestCases.Test05/MM does not implement interface TestCases.Test05/IInterface (method slot 0).

### System.Exception (19)

- `TestCases.DelegateTest.DelegateTest24` -- Neo lowering could not find expected Push instructions for Call/Newobj.
- `TestCases.DelegateTest.DelegateTest42` -- Exception of type 'System.Exception' was thrown.
- `TestCases.EnumTest.Test11` -- Different string value: Enum4 vs. TestCases.EnumTest/TestEnum
- `TestCases.EnumTest.Test33` -- Exception of type 'System.Exception' was thrown.
- `TestCases.ExpTest_10.UnitTest_1020` -- Exception of type 'System.Exception' was thrown.
- `TestCases.ExpTest_20.UnitTest_TestFCP` -- Exception of type 'System.Exception' was thrown.
- `TestCases.ExpTest_20.UnitTest_TestInline01` -- Exception of type 'System.Exception' was thrown.
- `TestCases.ExpTest_20.UnitTest_TestStackRegisterTransition3` -- Exception of type 'System.Exception' was thrown.
- `TestCases.RefOutTest.UnitTest_NestedGenericRefOut` -- Exception of type 'System.Exception' was thrown.
- `TestCases.RefOutTest.UnitTest_RefCLREnum` -- key
- `TestCases.ReflectionTest.ReflectionTest25` -- Exception of type 'System.Exception' was thrown.
- `TestCases.StructTests.StructTest12` -- Exception of type 'System.Exception' was thrown.
- `TestCases.StructTests.StructTest7` -- Neo lowering could not find expected Push instructions for Call/Newobj.
- `TestCases.TestCLREnum.Test06` -- Exception of type 'System.Exception' was thrown.
- `TestCases.TestValueTypeBinding.UnitTest_10027` -- Neo lowering could not find expected Push instructions for Call/Newobj.
- `TestCases.TestValueTypeBinding.UnitTest_10039` -- Exception of type 'System.Exception' was thrown.
- `TestCases.TestValueTypeBinding.UnitTest_10042` -- Exception of type 'System.Exception' was thrown.
- `TestCases.TestValueTypeBinding.UnitTest_10048` -- cls.Vector2.Z == 0
- `TestCases.TestValueTypeBinding.UnitTest_10050` -- Exception of type 'System.Exception' was thrown.

### System.NotSupportedException (10)

- `HotfixBasicTestCases.Test02` -- System.String Format(System.String, System.Object, System.Object) is not bound!
- `HotfixBasicTestCases.Test03` -- Void .ctor(Int32) is not bound!
- `HotfixBasicTestCases.Test07` -- Void .ctor() is not bound!
- `HotfixBasicTestCases.Test08` -- HotfixAOT.TestVector3 get_One2() is not bound!
- `HotfixTestGenericTestCases.Test01` -- Void Insert(Int32, Int32) is not bound!
- `HotfixTestGenericTestCases.Test02` -- System.String get_FullName() is not bound!
- `HotfixTestInheritanceTestCases.Test02` -- Int32 CalculateValue(Int32) is not bound!
- `TestCases.JsonTest.JsonTest1` -- Not supported opcode Ldfld_I4
- `TestCases.ReflectionTest.ReflectionTest09` -- Not supported opcode Ldfld_U4
- `TestCases.ReflectionTest.ReflectionTest23` -- Not supported opcode Ldfld_I4

### System.ArgumentException (10)

- `TestCases.DelegateTest.DelegateTest25` -- Type must be a runtime Type object. (Parameter 'type')
- `TestCases.DelegateTest.DelegateTest28` -- Type must be a runtime Type object. (Parameter 'type')
- `TestCases.DelegateTest.DelegateTest29` -- Type must be a runtime Type object. (Parameter 'type')
- `TestCases.DelegateTest.DelegateTest30` -- Type must be a runtime Type object. (Parameter 'type')
- `TestCases.DelegateTest.DelegateTest31` -- Type must be a runtime Type object. (Parameter 'type')
- `TestCases.DelegateTest.DelegateTest32` -- Type must be a runtime Type object. (Parameter 'type')
- `TestCases.DelegateTest.DelegateTest33` -- Type must be a runtime Type object. (Parameter 'type')
- `TestCases.DelegateTest.DelegateTest34` -- Type must be a runtime Type object. (Parameter 'type')
- `TestCases.DelegateTest.DelegateTest35` -- Type must be a runtime Type object. (Parameter 'type')
- `TestCases.EnumTest.Test21` -- Type must be a type provided by the runtime. (Parameter 'enumType')

### System.ArgumentOutOfRangeException (9)

- `HotfixBasicTestCases.Test04` -- Index was out of range. Must be non-negative and less than the size of the collection. (Parameter 'index')
- `TestCases.ArrayTest.ArrayTest05` -- Index was out of range. Must be non-negative and less than the size of the collection. (Parameter 'index')
- `TestCases.CLRBindingTest.CLRBindingTest08` -- Index was out of range. Must be non-negative and less than the size of the collection. (Parameter 'index')
- `TestCases.StructTests.StructTest11` -- Index was out of range. Must be non-negative and less than the size of the collection. (Parameter 'index')
- `TestCases.StructTests.StructTest8` -- Index was out of range. Must be non-negative and less than the size of the collection. (Parameter 'index')
- `TestCases.Test03.TestUsingNested` -- Index was out of range. Must be non-negative and less than the size of the collection. (Parameter 'index')
- `TestCases.TestValueTypeBinding.Test03` -- Index was out of range. Must be non-negative and less than the size of the collection. (Parameter 'index')
- `TestCases.TestValueTypeBinding.UnitTest_10035` -- Index was out of range. Must be non-negative and less than the size of the collection. (Parameter 'index')
- `TestCases.TestValueTypeBinding.UnitTest_10036` -- Index was out of range. Must be non-negative and less than the size of the collection. (Parameter 'index')

### System.Reflection.TargetException (4)

- `TestCases.InheritanceTest.InheritanceTest05` -- Object does not match target type.
- `TestCases.InheritanceTest.InheritanceTest07` -- Object does not match target type.
- `TestCases.InheritanceTest.InheritanceTest15` -- Object does not match target type.
- `TestCases.InheritanceTest.InheritanceTest18` -- Object does not match target type.

### System.ArgumentNullException (4)

- `TestCases.AsyncAwaitTest.TestRun` -- Value cannot be null. (Parameter 'stateMachine')
- `TestCases.AsyncAwaitTest.TestRun1` -- Value cannot be null. (Parameter 'stateMachine')
- `TestCases.AsyncAwaitTest.TestRun4` -- Value cannot be null. (Parameter 'stateMachine')
- `TestCases.AsyncAwaitTest/TestClass.Show1` -- Value cannot be null. (Parameter 'stateMachine')

### System.Collections.Generic.KeyNotFoundException (3)

- `TestCases.DelegateExtTest.DelegateExtTest03` -- Cannot find Delegate Adapter for:TestCases.DelegateExtObjMethod.Void(TestCases.DelegateExtObj obj, System.String str), Please a...
- `TestCases.DelegateTest.DelegateTest14` -- Cannot find Delegate Adapter for:TestCases.DelegateTest.TestString(System.String a), Please add following code:
- `TestCases.DelegateTest.DelegateTest22` -- Cannot find Delegate Adapter for:TestCases.DelegateTest.DelegateTest22Sub2(System.String str, System.Object obj), Please add fo...

### System.NotImplementedException (2)  [NIE detail below]

- `HotfixTestInheritanceTestCases.Test03` -- The method or operation is not implemented.
- `TestCases.DelegateExtTest.DelegateExtTest02` -- Step 17/13b: field/element access on a CLR object via the IL-instance path is deferred (CLR field-hash plumbing lands in Step 1...

### System.IndexOutOfRangeException (2)

- `TestCases.ExpTest_10.UnitTest_Struct` -- Index was outside the bounds of the array.
- `TestCases.TestValueTypeBinding.UnitTest_10051` -- Index was outside the bounds of the array.

### System.AccessViolationException (1)

- `TestCases.TestValueTypeBinding.UnitTest_10037` -- Attempted to read or write protected memory. This is often an indication that other memory is corrupt.

### (RESULT-FALSE / no exception, test returned False) (1)

- `HotfixBasicTestCases.Test05` -- Running HotfixBasicTestCases.Test05(IsPatched=True)

### NIE detail -- Step number / shape in the 2 NotImplementedException messages

- `TestCases.DelegateExtTest.DelegateExtTest02` -- full message: `Step 17/13b: field/element access on a CLR object via the IL-instance path is deferred (CLR field-hash plumbing lands in Step 13b). Owner type: System.Int32` (tagged-NIE carrying a Step label `17/13b` and a shape description).
- `HotfixTestInheritanceTestCases.Test03` -- full message: `The method or operation is not implemented.` (generic NIE, no Step number; the stack frame is `ILIntepreter.Execute(...StackObject*...)`).

## Section 4 -- Cross-check vs `surfacedFollowups` (appearance only; no judgment)

Source: `rasen/changes/neo-overhaul/portfolio-run.json` key `surfacedFollowups` (19 items). For each item, an associated test name (where one is identifiable in the item's `scope`) was checked against the 189 failures. "APPEARED" = that test name is in the failure list; "NOT APPEARED" = it is not; "NOT MATCHABLE" = the scope names no specific test.

### Open / needs-reaudit items (the ones expected to still be failing)

| surfacedFollowup id | Associated test named in scope | Observed in this run |
|---|---|---|
| neo-clr-struct-newobj-retdest-null | (none named; "CLR struct newobj ... retDst=null") | NOT MATCHABLE by name |
| neo-nested-ldflda-compound-assign | `UnitTest_Struct2` (scope: "...line 104") | APPEARED -- `TestCases.ExpTest_10.UnitTest_Struct2` fails (NullReferenceException) |
| neo-ldelem-any-clr-struct-array | (none named; "plain a = arr[i] on a CLR-struct array") | NOT MATCHABLE by name |
| neo-il-instance-clr-struct-field-f10 | (none named; "tagged NIE") | APPEARED -- the `Step 17/13b` tagged NIE fires (see `DelegateExtTest02`) |
| neo-step19-delegates | `DelegateExtTest02` (scope names it explicitly) | APPEARED -- `DelegateExtTest02` fails (NotImplementedException, `Step 17/13b ... Owner type: System.Int32`) |
| neo-legacy-ilruntime-type-getenumvalues-dispatch | (Legacy-scoped; no Neo test named) | NOT MATCHABLE -- item is Legacy-mode; this run is Neo-mode. (8 `EnumTest` tests do fail here, all with Neo VTable/GetType messages, not the Legacy GetEnumValues-override shape.) |

### Resolved / disproven / unreachable items -- spot-check of named tests

| surfacedFollowup id (status) | Associated test named in scope | Observed in this run |
|---|---|---|
| neo-ldind-stind-byref-clr-struct (resolved) | `TestValueTypeBinding.Test00` | NOT APPEARED |
| neo-ceq-null-instance-field (resolved) | `ActivatorCreateInstanceWithArgsTest` | NOT APPEARED (no `ActivatorCreateInstance*` test fails) |
| neo-il-static-field-roundtrip (disproven) | `SimpleTest.TestStaticFieldInstance` | NOT APPEARED |
| neo-ceq-null-sentinel (resolved) | `RegisterVMTest04` | APPEARED -- but with a generic NRE message (`Object reference not set...`), NOT the ceq-null message |
| neo-il-instance-clr-base-field (resolved) | `TestCls..ctor` ("10 full-smoke hits") | NOT MATCHABLE by name |

(Fact only: these appearances/non-appearances are reported without judging whether a given follow-up is actually fixed or re-broken.)

## Appendix -- Observed executor frames inside the 189 failure blocks

Extracted from stack-trace text in the failure blocks. Observation only; not interpreted.

| Frame | Occurrences |
|---|---|
| `ILIntepreter.ExecuteNeo(` | 172 |
| `ILIntepreter.InvokeNeoClrMethod(` | 81 |
| `ILIntepreter.InvokeNeoCallTarget(` | 41 |
| `ILIntepreter.ResolveNeoGenericCallvirtTarget(` | 17 |
| `ILIntepreter.ResolveNeoCallvirtILTarget(` | 17 |
| `ILIntepreter.Execute(` (StackObject* signature) | 9 |
| `ILIntepreter.ResolveNeoCallvirtCLRTarget(` | 7 |
| `ILIntepreter.Run(` | 4 |
| `ILIntepreter.ResolveNeoCallvirtInterfaceTarget(` | 3 |
| `ILIntepreter.ExecuteR(` | 3 |

Other: exactly one `System.AccessViolationException` in the entire failure set -- `TestCases.TestValueTypeBinding.UnitTest_10037` ("Attempted to read or write protected memory..."). No other crash/AV markers observed in the failure region.

---

NeoStep reference (separate filter run, same DLL/patch/build): `dotnet run ... true NeoStep` -> `Ran 380 tests, 0 failded, 0 ignored, 0 todos` (log: `.tmp-neostep-ground.log`). 0 of the 189 full-smoke failures have `NeoStep` in their name.
