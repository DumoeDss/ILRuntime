# Full Neo Smoke Grounding -- 2026-07-15 FRESH re-cluster (24 failing)

## POST-FIX STATUS (this child shipped a fix)
- **DELTA: 24 -> 23** (Cgt_Un null-check fix; DelegateTest43 flipped). Verified by a
  fresh full smoke: `Ran 935 tests, 23 failded, 20 ignored, 7 todos`. The 23 are a
  STRICT SUBSET of the ground 24 (diff = only DelegateTest43 removed; zero new
  failures). DelegateTest43 alone (name-filter) -> `Ran 1 tests, 0 failded`. NeoStep
  **401/0** (no regression). Legacy-neutral (the change is entirely inside
  `ILIntepreter.Neo.cs`, file-gated under `ENABLE_NEO_MODE`; plain Debug build clean).
- Fix = the `Cgt_Un` runtime arm (`ILIntepreter.Neo.cs`) reference "!= null" branch.
  The CIL `x != null` idiom is `ldnull; cgt.un` (ldnull -> the -1 sentinel -> cguB ==
  -1). Under the Neo object model, an IL-STATIC reference field holding null is encoded
  as a NON-ZERO mStack index whose entry IS null (the `mStack.Add(null)+index` path),
  NOT the -1 sentinel. The prior arm `cguRes = cguA != -1 && (...)` read that non-zero
  index as "not null" -> a null static event tested non-null -> the wrong branch fired
  (DelegateTest43: `if (OnIntEvent != null) throw` after `-=`). The fix mirrors the
  sibling `Ceq_Ref` arm: when `cguB == -1` (the null-compare idiom), resolve the
  referenced object and test `mStack[cguA] == null`. See
  `rasen/changes/neo-recluster-24/ship-log.md`.
- NeoStep **401/0** (no regression). Legacy-neutral (the change is entirely inside
  `ILIntepreter.Neo.cs`, file-gated under `ENABLE_NEO_MODE`).

## Fresh ground (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch)
`Ran 935 tests, 24 failded, 20 ignored, 7 todos` (exit 0 in this run; the prior-known
graceful Dict-NRE crash did not surface this run, summary emitted). NeoStep 401/0.
Legacy (plain Debug) build VERIFIED clean earlier this session (0 errors).

## The CURRENT 24, clustered by PINNED root (fresh run)

Each row = exception type + the Neo.cs frame + the test-internal throwing site.
"grab-bag" = the test's OWN assertion fired (reaches the CIL Throw handler @
Neo.cs:7070); each is a DISTINCT value-corruption root, NOT a single fix.

### Cluster A -- throw@7070 grab-bag (test's own assertion), 10 tests, DISTINCT roots
Each reaches `throw` because the test detected a wrong value. NOT one fix; each needs
its value-corruption root pinned individually.
- DelegateTest42 (DelegateTest.cs:662) -- `delegate.Target` wrong for an
  IL-instance-method delegate (Target != cls2). Step-19 delegate dispatch.
- ExpTest_20.UnitTest_TestInline01 (LightTester2.cs:143) -- D3 reference-arg aliasing:
  `Sub(object o){o=null;}` nullifies the caller's local (by-ref instead of by-value
  copy on a plain Call). [child-13 fixed newobj arg alias; this is the plain-Call form]
- ExpTest_20.UnitTest_TestFCP (LightTester2.cs:158) -- TestVector3NoBinding ctor
  (reflection fallback, NO redirect) yields (1,0,0) instead of (1,1,0): the 2nd ctor
  arg (y) is lost. JIT confirms the ctor call args are correct (`call -, r5, r7, r11`)
  and the float div is `divi.r4` (correct), so the bug is in the struct-ctor
  reflection-fallback arg read / box-mutate-unbox (deep; `!isNewObj` ctor path).
- ExpTest_20.UnitTest_TestStackRegisterTransition3 (LightTester2.cs:339) -- register-
  transition value corruption.
- ReflectionTest25 (ReflectionTest.cs:680) -- CLR attribute reflection
  (GetCustomAttribute wrong/null).
- StaticTest.UnitTest_StaticTest05 (StaticTest.cs:106) -- static field value.
- StructTests.StructTest12 (Structs.cs:396) -- generic struct constrained to
  ITestStruct: `new T(){i=10}` -> `ins.i` wrong (constrained iface property set on a
  generic struct param).
- Test05.TestForEach (Test05.cs:275) -- `[ILRuntimeTest(ExpectException=typeof(
  NotSupportedException))]`; ParseOne throws NSE("error") which the framework SHOULD
  honor as Pass, but the test is reported Failed (ExpectException mechanism mis-matches
  under Neo for this case -- subtle framework-level issue).
- TestValueTypeBinding.UnitTest_10046 (TestValueTypeBinding.cs:470) -- TestVector3 via
  delegate: `a = a + One2; a.X != 2` (delegate VT-arg marshal OR struct-newobj retDst
  gap; child-28 follow-up).
- TestValueTypeBinding.UnitTest_10051 (TestValueTypeBinding.cs:619) -- Fixed64Vector2
  `.x.RawValue` constrained-callvirt property read on a nested struct field (F-10
  handoff noted this; distinct from the `+=` ldflda path).

### Cluster B -- autogen Neo binding stub / upstream arg marshalling, 5 tests
All route `InvokeNeoClrMethod -> autogen *_Neo stub` (or reflection fallback). The
exception comes from UPSTREAM arg marshalling feeding a bad index / a mis-typed `this`.
Distinct per test.
- GenericMethodTest11 -- `CompareTo_0_Neo` casts ILTypeInstance->IComparable<int>
  (constrained. T dispatch on an IL type implementing a CLR interface). Neo.cs:7452.
- ReflectionTest10 -- `Invoke_1_Neo` -> ILRuntimeMethodInfo.Invoke ->
  CheckCLRTypes -> Enum.ToObject(ILRuntimeType) "Type must be a type provided by the
  runtime". Neo.cs:4184.
- StructTests.StructTest11 -- `List_1_ILTypeInstance_Binding.Add_0_Neo` ->
  List.get_Item OOB (upstream item/this marshalling feeds a bad mStack index; Anim is a
  struct with a ref field). Neo.cs:4184.
- MyTest.Test -- `IEnumerator_1_KVP_2_Int32_Int32_Binding.get_Current_0_Neo` casts
  String->IEnumerator (enumerator `this` mis-marshalled; struct `this` to autogen
  binding). Neo.cs:4239.
- Test05.TestGenericMethod2 -- `Convert_Binding.ChangeType_1_Neo` ->
  Convert.DefaultToType "Invalid cast String->Int32". PINNED root = typeof(<generic
  param>) under Neo returns an ILRuntimeType whose ILType.TypeForCLR mis-resolves BOTH
  A=int and B=double to int. Neo.cs:3649.

### Cluster C -- raw ldfld.i4 / stfld.ref owner NRE/OOB (collection/generic), 2 tests
- Test05.TestStructDictionary -- ldfld.i4 NRE @ Neo.cs:4772 on `TestStruct::id` from a
  Dictionary enumerator element / `List<TestStruct>[i]` (the owner register holds flat
  bytes or a mis-resolved index; Ldfld_I4 calls GetNeoILInstance on it).
- RegisterVMTest04 -- stfld.ref IndexOOB @ Neo.cs:5095 on
  `ILScrollRect2<T>::viewRectEvent` (generic IL type, Action ref field).

### Cluster D -- List.get_Item IndexOOB (ret/ret-area), 1 test
- DelegateTest19 -- List.get_Item OOB @ Neo.cs:4285 (ret-vt-with-ref-fields branch,
  TestCLREnum return; returnRefCount/retRefBase mis-computed for a CLR enum return).

### Cluster E -- ldlen on null reflection array, 1 test
- ReflectionTest14 -- ldlen NRE @ Neo.cs:5999; `targetType.GetFields()` on an IL type
  (`PlayerInfo`) returns null (the foreach lowers to ldlen). NOTE: the `field` local is
  shown non-null in the trace, so the exact ldlen source is ambiguous (may be a nested
  property.PropertyType.GetFields or an IsAssignableFrom internal ldlen); NOT a clean
  GetFields-null fix.

### Cluster G -- byref out-STRUCT / nested-generic ref-out write-back, 2 tests
- StructTests.StructTest6 -- `Dictionary.TryGetValue(strId, out StructTest cube)`;
  the out STRUCT (IL struct with a ref field) is NOT written back (cube.type stays
  "123", expected "111"). Distinct from recluster-38's byref REF-field fix.
- RefOutTest.UnitTest_NestedGenericRefOut -- nested-generic ref/out write-back (throw).

### Cluster H -- Hotfix Neo-bridge field-index (Legacy Execute path), 2 tests
Via the LEGACY `Execute` path (hotfix-patched IL) calling Neo ILTypeInstance
PushToStack/AssignFromStack:
- HotfixBasicTestCases.Test04 -- AssignFromStack field index 1 out of range for
  HotfixClass___Extra (TotalFieldCount=1). ILTypeInstance.cs:1177.
- HotfixBasicTestCases.Test05 -- PushToStack field index 0 out of range for
  <PrivateImplementationDetails> (TotalFieldCount=0). ILTypeInstance.cs:918.
(cluster-H commit fd66d81c did NOT clear these -- a residual hotfix-patched-type vs
Neo field-layout mismatch. ONE shared mechanism.)

## Largest sub-cluster + verdict
- By COUNT, Cluster A (throw grab-bag, 10) is largest, but it is 10 DISTINCT roots
  (each a test-internal assertion) -- NOT actionable as one fix.
- The largest SHARED-ROOT pairs are 2-test sets: C (collection-struct field access), G
  (byref out-STRUCT), H (hotfix field-index). Each is a distinct mechanism.
- The 24 are predominantly DEEP singletons / distinct roots. Per the task mandate ("if
  singletons, pick the 2-3 most tractable"), this child fixed the MOST TRACTABLE pin-able
  item found on fresh re-audit: the `Cgt_Un` reference "!= null" null-check (Cluster D
  delegate-adjacent -- DelegateTest43's `OnIntEvent != null`). It is a clean, low-risk,
  JIT-dump-pinned runtime fix that mirrors the existing `Ceq_Ref` sibling.

## Recommended next-batch priority (by coverage / confidence)
1. Cluster H (hotfix Neo-bridge field-index, x2) -- niche but ONE shared mechanism.
2. Cluster G (byref out-STRUCT, x2) -- distinct from recluster-38's ref-field fix.
3. Cluster C (TestStructDictionary + RegisterVMTest04) -- collection/generic struct
   field access; 2 distinct roots.
4. UnitTest_TestFCP (struct-ctor reflection-fallback arg read) -- JIT-pinned but the
   exact reflection-path line is elusive; needs a byte-dump diagnostic.
5. The Cluster B typeof(generic-param) root (TestGenericMethod2) -- likely broader.
6. The Cluster A grab-bag -- each needs its own JIT-dump triage child.
