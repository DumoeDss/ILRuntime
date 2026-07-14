# Full Neo Smoke Grounding -- 2026-07-15 FRESH re-cluster (28 failing)

## POST-FIX STATUS (this child shipped a fix)
- **DELTA: 28 -> 27** (UnitTest_Struct2 / Cluster F fixed). Verified by a fresh
  full smoke: `Ran 935 tests, 27 failed, 20 ignored, 7 todos`. The 27 are a
  STRICT SUBSET of the ground 28 (diff = only UnitTest_Struct2 removed; zero
  new failures). NeoStep 401/0 (no regression). Legacy-neutral (plain Debug
  build 0 errors).
- Fix = `NeoClrStaticFieldAddr` descriptor for `ldsflda <CLR static struct
  field>` + additive `is NeoClrStaticFieldAddr` guards in the raw Stfld/Ldfld
  VT-owner byref arms + the nested ldflda branch (NO JIT marker -- runtime
  content-detection on a NEW mStack type, collision-free). See
  `rasen/changes/neo-recluster-28/ship-log.md`.
- CRITICAL Legacy-parity gotcha (load-bearing): the nested-`+=` on a CLR
  static struct field (`ldflda <inner>; ldind; add; stind`) MUST NOT persist
  under Neo -- Legacy does NOT persist it (Legacy's ldflda-CLR-static yields a
  non-persistent address; verified: Test01 `TestVector3.One.X += vec.X` leaves
  One.X unchanged under Legacy). Persisting it (.NET-correct via
  SetStaticFieldValue) mutates the shared static and breaks later tests
  (UnitTest_10047 reads TestVector3.One). So WriteNeoNestedInnerField's
  IsClrStatic branch is an intentional NO-OP write-back (mutates only the
  descriptor's local boxed copy). The raw Stfld/Ldfld CLR-static arms (the
  `= v` / read forms) DO persist (Legacy parity: Struct2 `instance.value=222`
  persists; the nested `+=111` does not -> Struct2 prints 222 for instance,
  matching Legacy).

Worker: neo-recluster-28 (Wave-2 child of neo-overhaul). Branch
`features/object-model-overhaul`. HEAD before this child's edit:
`68a4256f` (NeoInvokeSub IsExtend, wave2 D1). This doc supersedes the stale
D1-D11 table in `fullsmoke-ground-34-postfix.md` (D1/D2/D4 were already fixed
by commits 68a4256f / 2608e797 / 11b3bdb2 after that doc was written; the
remaining-34 batch fixed DelegateTest36).

## Fresh ground (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch)
`Ran 935 tests, 28 failed, 20 ignored, 7 todos` (exit 127 = the known graceful
pre-existing Dict-NRE crash; summary still emitted). NeoStep ~401/0. Legacy
(plain Debug) build VERIFIED clean (Legacy-neutral).

## The CURRENT 28, clustered by PINNED root (fresh run)

Each row = exception type + the Neo.cs frame + the test-internal throwing
site. "grab-bag" = the test's OWN assertion fired (reaches the CIL Throw
handler); each is a DISTINCT value-corruption root, NOT a single fix.

### Cluster A -- throw@6988/7070 grab-bag (test's own assertion), 11 tests, DISTINCT roots
Each reaches `throw ex` because the test detected a wrong value. NOT one fix;
each needs its value-corruption root pinned individually.
- DelegateTest42 (DelegateTest.cs:662) -- `delegate.Target` wrong for an
  IL-instance-method delegate (Target != cls2).
- DelegateTest43 (DelegateTest.cs:684) -- `OnIntEvent != null` after `-=` (a
  static event field null-comparison OR Delegate.Remove not yielding null).
- ExpTest_20.UnitTest_TestInline01 (LightTester2.cs:143) -- D3 reference-arg
  aliasing: `Sub(object o){o=null;}` nullifies the caller's local (by-ref
  instead of by-value copy on a plain Call). [child-13 fixed newobj arg alias]
- ExpTest_20.UnitTest_TestFCP (LightTester2.cs:158) -- float/Convert.ToInt64
  ToColor arithmetic on TestVector3NoBinding.
- ExpTest_20.UnitTest_TestStackRegisterTransition3 (LightTester2.cs:339) --
  register-transition value corruption.
- ReflectionTest25 (ReflectionTest.cs:680) -- CLR attribute reflection
  (GetCustomAttribute wrong/null).
- StaticTest.UnitTest_StaticTest05 (StaticTest.cs:106) -- static field value.
- StructTests.StructTest12 (Structs.cs:396) -- generic struct constrained to
  ITestStruct: `new T(){i=10}` -> `ins.i` wrong (constrained iface property
  set on a generic struct param).
- Test05.TestForEach (Test05.cs:275) -- throws "error" (NotSupportedException);
  enumerator/ParseOne.
- TestValueTypeBinding.UnitTest_10046 (TestValueTypeBinding.cs:470) --
  TestVector3 via delegate: `a = a + One2; a.X != 2` (delegate VT-arg marshal
  OR the struct-newobj retDst gap, child-28 follow-up).
- TestValueTypeBinding.UnitTest_10051 (TestValueTypeBinding.cs:619) --
  Fixed64Vector2 `.x.RawValue` constrained-callvirt property read on a nested
  struct field (D4 residual, F-10 handoff noted).

### Cluster B -- autogen Neo binding stub / upstream arg marshalling, 6 tests
All route `InvokeNeoClrMethod -> autogen *_Neo stub`. The stubs themselves are
proper ReadNeoReference stubs (NOT stale `default(...)` TODOs -- verified for
List_1_ILTypeInstance_Binding.Add_0_Neo). The exception comes from UPSTREAM
arg marshalling feeding a bad index / a mis-typed `this`. Distinct per test.
- GenericMethodTest11 -- `CompareTo_0_Neo` casts ILTypeInstance->IComparable<int>
  (constrained. T dispatch on an IL type implementing a CLR interface). Neo.cs:7370.
- ReflectionTest06 -- `SetValue_1_Neo` -> ILRuntimeFieldInfo.SetValue NRE
  (ILRuntimeFieldInfo.cs:227). Neo.cs:4149.
- ReflectionTest10 -- `Invoke_1_Neo` -> ILRuntimeMethodInfo.Invoke ->
  CheckCLRTypes -> Enum.ToObject(ILRuntimeType) "Type must be a type provided
  by the runtime". Neo.cs:4149.
- StructTests.StructTest11 -- `List_1_ILTypeInstance_Binding.Add_0_Neo` ->
  List.get_Item OOB (upstream item/this marshalling feeds a bad mStack index).
  Neo.cs:4149.
- MyTest.Test -- `IEnumerator_1_KVP_2_Int32_Int32_Binding.get_Current_0_Neo`
  casts String->IEnumerator (enumerator `this` mis-marshalled). Neo.cs:4204.
- Test05.TestGenericMethod2 -- `Convert_Binding.ChangeType_1_Neo` ->
  Convert.DefaultToType "Invalid cast String->Int32". PINNED root =
  typeof(<genericparam>) under Neo returns an ILRuntimeType whose
  ILType.TypeForCLR mis-resolves BOTH A=int and B=double to int (remaining-34
  batch tried + reverted a ConvertChangeTypeNeo redirect; the real root is the
  generic-param TypeForCLR resolution). Neo.cs:3614.

### Cluster C -- raw ldfld.i4 / stfld.ref owner NRE/OOB (collection/generic), 4 tests
- Test01.UnitTest_Generics + Test01.UnitTest_Generics2 -- stfld.ref NRE @
  Neo.cs:5007 on `SingletonTest.Inst.Test = "bar"` (self-referential generic
  singleton `SingletonTest : Singleton<SingletonTest>`). IDENTICAL IL + site;
  ONE shared root (the Inst owner resolves null). [2 tests, same root]
- Test05.TestStructDictionary -- ldfld.i4 NRE @ Neo.cs:4722 on
  `TestStruct::id` from a Dictionary enumerator element.
- RegisterVMTest04 -- stfld.ref IndexOutOfRange @ Neo.cs:5028 on
  `ILScrollRect2::viewRectEvent`.

### Cluster D -- List.get_Item IndexOOB (ret/ret-area), 1 test
- DelegateTest19 -- List.get_Item OOB @ Neo.cs:4250 (ret area, TestCLREnum).

### Cluster E -- ldlen on null reflection array, 1 test
- ReflectionTest14 -- ldlen NRE @ Neo.cs:5917; `targetType.GetFields()` on an
  IL type returns null (the foreach lowers to ldlen). Reflection-GetFields
  under Neo returns null instead of an empty/field array for PlayerInfo.

### Cluster F -- ldsflda CLR static struct field address, 1 test  [FIXED THIS CHILD]
- ExpTest_10.UnitTest_Struct2 -- tagged NIE "Neo Ldsflda: CLR static field
  address deferred (follow-up)" @ Neo.cs:5428. `TestStruct.instance.value
  =222; +=111` on a CLR-static struct field. Explicitly deferred TODO.
  -> Fixed by neo-recluster-28 (NeoClrStaticFieldAddr descriptor + consumer
     guards). Test prints 222/222/333 (correct).

### Cluster G -- byref out-STRUCT write-back, 2 tests
- StructTests.StructTest6 -- `Dictionary.TryGetValue(strId, out StructTest
  cube)`; the out STRUCT (IL struct with a ref field) is NOT written back
  (cube.type stays "123", expected "111"). Distinct from recluster-38's
  byref REF-field fix (the out-STRUCT shape in NeoMarshalByrefFieldToSlot /
  CopyNeoCallThisBack). [StructTest6 also listed in A; the root is G]
- RefOutTest.UnitTest_NestedGenericRefOut -- nested-generic ref/out write-back
  (throw).

### Cluster H -- Hotfix Neo-bridge field-index (Legacy Execute path), 2 tests
Via the LEGACY `Execute` path (hotfix-patched IL) calling Neo ILTypeInstance
PushToStack/AssignFromStack:
- HotfixBasicTestCases.Test04 -- AssignFromStack field index 1 out of range
  for HotfixClass___Extra (TotalFieldCount=1). ILTypeInstance.cs:1177.
- HotfixBasicTestCases.Test05 -- PushToStack field index 0 out of range for
  <PrivateImplementationDetails> (TotalFieldCount=0). ILTypeInstance.cs:918.
(cluster-H commit fd66d81c did NOT clear these -- a residual hotfix-patched-
type vs Neo field-layout mismatch. ONE shared mechanism.)

## Largest sub-cluster + verdict
- By COUNT, Cluster A (throw grab-bag, 11) is largest, but it is 11 DISTINCT
  roots (each a test-internal assertion) -- NOT actionable as one fix.
- The largest SHARED-ROOT clusters are 2-test pairs: C-pair (Test01.Generics
  x2, identical IL/site), G (byref out-STRUCT), H (hotfix field-index).
- The 28 are predominantly DEEP singletons / distinct roots. Per the task
  mandate ("if singletons, pick the 2-3 most tractable"), this child fixed
  the single MOST TRACTABLE item: Cluster F (ldsflda CLR static, an
  explicitly-tagged deferred NIE with a bounded, isolated fix).

## Recommended next-batch priority (by coverage / confidence)
1. C-pair (Test01.UnitTest_Generics + Generics2, x2 same root) -- stfld.ref
   NRE on a self-referential generic singleton. Highest shared-root coverage.
2. H (hotfix Neo-bridge field-index, x2) -- niche but one shared mechanism.
3. G (byref out-STRUCT, x2) -- distinct from recluster-38's ref-field fix.
4. E (ReflectionTest14 ldlen-on-null) -- reflection bridge GetFields-null.
5. The Cluster B typeof(generic-param) root (TestGenericMethod2) -- likely
   broader than ChangeType.
6. The Cluster A grab-bag -- each needs its own JIT-dump triage child.
