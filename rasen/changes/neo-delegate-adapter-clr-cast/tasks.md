# Tasks: neo-delegate-adapter-clr-cast

## 1. Root-cause re-audit (DONE)

- [x] Build CLI `Debug_Neo --no-incremental` + TestCases `Debug` (0 errors).
- [x] Confirm `DelegateTest01` FAILS on Neo (InvalidCastException @
      `ExecuteNeo:4607` Stsfld -> `set_IntDelegateTest_0` `(IntDelegate)v`).
- [x] Confirm `DelegateTest01` PASSES on Legacy (plain `Debug` +
      `useRegister=true`, 1/0).
- [x] Confirm `GenericStaticMethodTest1` FAILS on Neo (InvalidCastException @
      `StaticMethod_2_Neo:250` `(Action)ReadNeoReference(...)` via
      `InvokeNeoClrMethod:1098`).
- [x] Pin Site A: Neo Stsfld CLR-static arm omits `f.FieldType.CheckCLRTypes`
      (Legacy `Register.cs:3308` applies it).
- [x] Pin Site B: committed autogen Neo bindings omit `CheckCLRTypes` for
      delegate params; the GENERATOR already emits it (`BindingGeneratorExtensions
      .cs:244-246`, `MethodBindingGenerator.cs:296-298`) -> stale bindings
      (child-28 class). Reflection fallback `CLRMethod.Invoke:519-524` already
      converts.
- [x] Pin Site C (instance field, surfaced by DelegateTest41): raw Stfld CLR-ref
      owner -> `NeoWriteClrObjectField` -> `SetFieldValue` -> setter
      `(delegate)v` omits the conversion too.

## 2. Implement Fix A (engine Stsfld, Neo-gated)

- [x] `ILIntepreter.Neo.cs` Stsfld CLR-static reference-field branch (~:4606):
      `value = ft.CheckCLRTypes(value);` before `ct.SetStaticFieldValue`.

## 3. Implement Fix C (engine instance-field, Neo-gated)

- [x] `ILIntepreter.Neo.cs` `NeoWriteClrObjectField` (~:6617): resolve the field
      via `ct.GetField(fieldHash)` and `value = f.FieldType.CheckCLRTypes(value);`
      before `ct.SetFieldValue`.

## 4. Implement Fix B (hand-port stale autogen bindings -- generator already correct)

- [x] `ILRuntimeTest_TestBase_StaticGenericMethods_Binding.cs` (8 delegate params).
- [x] `ILRuntimeTest_TestBase_GenericExtensions_Binding.cs` (10 delegate params).
- [x] `ILRuntimeTest_TestFramework_DelegateTest_Binding.cs` (4 IntDelegate + 2
      Action<float,double,int> params).
- [x] `ILRuntimeTest_TestFramework_IntDelegate_Binding.cs` (Invoke `this`).
- [x] `ILRuntimeTest_TestFramework_IntDelegate2_Binding.cs` (Invoke `this`).
- [x] `System_Collections_Generic_List_1_Action_1_Int32_Binding.cs` (Add item).
- [x] `System_Collections_Generic_List_1_Func_2_Int32_Int32_Binding.cs` (Add item).
- [x] `System_Collections_Generic_List_1_ILTypeInstance_Binding.cs` (Predicate +
      Comparison params: RemoveAll / Sort).
- [x] `System_Linq_Enumerable_Binding.cs` (8 Func selector/predicate params).
- [x] `System_Threading_Interlocked_Binding.cs` (3 Action params).
- All transforms are `(T)ReadNeoReference(...)` ->
      `(T)typeof(T).CheckCLRTypes(ReadNeoReference(...), IsDelegate)` (byte-identical
      to the current generator). Non-delegate ref params (e.g. the `ExtensionClass`
      instance, `List<Action<int>>` instance) correctly left untouched.

## 5. Verify (DONE -- the truth = the full-smoke number)

- [x] Name-filtered: DelegateTest01/03/06/07/11/18/20/21/41/45, DelegateInnerTest,
      GenericStaticMethodTest1-8, GenericExtensionMethod1/2Test*, GenericMethodTest14,
      Test03.Test05, InheritanceTest10 -> PASS under Neo.
- [x] FULL smoke: **189 -> 153** (drop of 36). Delegate-adapter cast messages
      **~32 -> 0** (the entire C1 delegate-cast cluster eliminated).
      InvalidCastException lines 56 -> 40.
- [x] Stash-toggle: stash (Neo.cs + 10 bindings), rebuild -> DelegateTest01 /
      GenericStaticMethodTest1 / DelegateTest41 / InheritanceTest10 all FAIL with
      the exact delegate-cast errors; pop -> rebuild -> PASS (airtight).
- [x] NeoStep smoke `... true NeoStep` -> **380/0** unchanged (no regression).
- [x] Legacy-neutral: DelegateTest01 / GenericStaticMethodTest1 /
      GenericExtensionMethod1Test1 / DelegateTest41 under plain `Debug` +
      `useRegister=true` -> still PASS (fix is Neo-gated; binding edits are in
      `#if ENABLE_NEO_MODE` regions, Legacy `#else` halves untouched).

## Note on progressed (not regressed) tests

A few C1-listed tests no longer throw the delegate cast but now reach a LATER,
pre-existing different-cluster error (the delegate cast was their FIRST error;
fixing it unblocked the test to proceed to the next, unrelated bug). These are
NOT regressions -- the C1 defect is fixed; the residual belongs to another
cluster:
- `DelegateTest19` -> IndexOutOfRange (C10).
- `DelegateTest43` -> "Neo callvirt this is null" (C4).
- `DelegateTest01` (full smoke only, via shared static state) -> Step-17/13b
  deferred NIE on `DelegateExtObj.AddValue` (surfaced follow-up).
- `UnitTest_10046` -> generic Exception (downstream).
