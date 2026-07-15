# Full Neo Smoke Grounding -- 2026-07-16 FRESH re-cluster (15 failing)

## STATUS: FRESH grounding (no fix shipped this child yet).
Full smoke **15 failed** (verified by a FRESH no-filter run this child):
`Ran 938 tests, 15 failded, 20 ignored, 7 todos` (exit 0; no crash this run).
NeoStep **404/0** (no regression). This is the CURRENT frontier after 48+
wave-2 children drove 189 -> 15.

The 15 are a STRICT SUBSET of ground-17's 17 (ReflectionTest10 fixed in
recluster-17; GenericMethodTest11 fixed in recluster-16). All 15 were already
documented in ground-16/17; this child RE-VERIFIED each against a fresh run +
captured clean per-test stack traces.

## Fresh ground (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch)
`Ran 938 tests, 15 failded, 20 ignored, 7 todos` (exit 0). NeoStep **404/0**.
Build: CLI Debug_Neo --no-incremental -p:UseSharedCompilation=false (0 errors);
TestCases Debug -p:UseSharedCompilation=false (0 errors).

## The CURRENT 15, clustered by PINNED root + per-test trace (fresh run)

Each row = exception type + the throwing site (clean stack from a filtered
single-test run). "throw@7115" = the test's OWN assertion fired (reaches the
CIL Throw handler); each is a DISTINCT value-corruption root, NOT one fix.

### Cluster A -- test-internal assertion (System.Exception throw@7115), 10 tests
Each needs its own JIT-dump triage; the surface is exhausted of shared roots.
- DelegateTest42 (DelegateTest.cs:662) -- delegate assertion (delegate.Target /
  dispatch wrong for an IL-instance-method delegate). Step-19. DEEP.
- ExpTest_20.UnitTest_TestInline01 (LightTester2.cs:143) -- reference-arg
  aliasing on an INLINED plain Call: `Sub(object o){o=null;}` nullifies the
  caller's local. PINNED (JIT dump, ground-17): caller obj=r0, inlined arg=r2
  (distinct regs) yet r0 ends up null -> Neo FRAME LAYOUT for inlined calls
  aliases the caller local + inlined arg slot. DEEP (JIT inliner/allocator).
- ExpTest_20.UnitTest_TestFCP (LightTester2.cs:158) -- `new
  TestVector3NoBinding(num1,num2,num3)` (CLR struct, NO binder, reflection-
  fallback ctor) yields (1,0,0) instead of (1,1,0): the 2nd/3rd ctor args are
  lost. child-28 deferred SYMPTOM 1 (CLR-struct newobj dest/byref-this
  convention). DEEP (struct-newobj dest convention).
- ExpTest_20.UnitTest_TestStackRegisterTransition3 (LightTester2.cs:339) --
  TransitionTest struct {int A; string B; float C; TransitionTestSub D} passed
  by value to TransitionTest2.Test: register-transition value corruption.
  DEEP (needs JIT dump).
- ReflectionTest25 (ReflectionTest.cs:680) -- CLR attribute reflection
  (GetCustomAttribute wrong/null). DEEP.
- StaticTest.UnitTest_StaticTest05 (StaticTest.cs:106) -- `ref Vector3
  staticField` write-back lost. MULTI-BUG (ldsflda offset + read-back). DEEP.
- StructTests.StructTest6 (Structs.cs:262) -- `Dictionary.TryGetValue(strId,
  out StructTest cube)`: the out STRUCT (IL struct with ref fields) is NOT
  written back. DEEP (byref out-STRUCT).
- StructTests.StructTest12 (Structs.cs:396) -- generic struct constrained to
  ITestStruct: `new T(){i=10}` -> `ins.i` == 0 (constrained-callvirt property
  set on a generic struct param). DEEP.
- TestValueTypeBinding.UnitTest_10046 (TestValueTypeBinding.cs:470) --
  TestVector3 via delegate: `a = a + One2; a.X != 2` (delegate VT-arg marshal
  OR struct-newobj retDst gap). DEEP.
- TestValueTypeBinding.UnitTest_10051 (TestValueTypeBinding.cs:619) --
  Fixed64Vector2 `.x.RawValue` constrained-callvirt property read on a nested
  struct field (distinct from the `+=` ldflda path neo-nested-ldflda-byref
  fixed). DEEP.

### Cluster B -- autogen Neo binding stub / upstream arg marshalling, 2 tests
Both route `Callvirt_CLR -> InvokeNeoClrMethod -> autogen *_Neo stub`.
- StructTests.StructTest11 (Structs.cs:365) -- `List<Anim>.Add(new Anim(...))`
  where Anim is an IL struct (boxed to ILTypeInstance). Trace:
  `ArgumentOutOfRangeException @ List`1.get_Item` from
  `System_Collections_Generic_List_1_ILTypeInstance_Binding.Add_0_Neo:98`
  (called via InvokeNeoClrMethod Neo.cs:1305 <- ExecuteNeo Neo.cs:4212). The
  autogen Add_0_Neo reads the item via ReadNeoReference; the IL-struct item is
  NOT boxed/placed on mStack as a reference at the call boundary ->
  ReadNeoReference reads a garbage mStack index -> AutoList (List-backed) OOB.
  Same deep class as TestStructDictionary (IL-struct-as-ILTypeInstance boxing
  at a List<ILTypeInstance> call boundary). DEEP.
- MyTest.Test (Test01.cs:618) -- boxed-CLR-struct enumerator interface
  dispatch. Trace: `InvalidCastException String->IEnumerator<KVP<int,int>>`
  from `System_Collections_Generic_IEnumerator_1_KeyValuePair_2_..._Binding:48`
  (InvokeNeoClrMethod Neo.cs:1305 <- InvokeNeoCallTarget Neo.cs:832 <-
  ExecuteNeo Neo.cs:4267). The Dictionary enumerator `this` is mis-marshalled
  (a String ends up in the `this` slot on a later loop iteration). DEEP
  (Step-19 / boxed-CLR-struct enumerator).

### Cluster C -- raw call / value materialization, 2 tests
- Test05.TestStructDictionary (Test05.cs:200) -- `var item = lists[i];
  item.id` where lists is `List<TestStruct>` (resolved to
  `List<ILTypeInstance>`; TestStruct is an IL struct, boxed). Trace: `NRE @
  Neo.cs:4817` (Ldfld_I4 heap arm: `GetNeoILInstance(mStack, *(int*)SrcOffset)`
  returns null). JIT dump (Final): `41:callvirt.clr r8, r1, r7,
  List<ILTypeInstance>::ILTypeInstance get_Item; 43:ldfld.i4 r11, r8,
  TestStruct::id`. get_Item returns ILTypeInstance (reference); the call writes
  `mStack[retRefBase]=result; *(int*)retDst=retRefBase`; ldfld.i4 reads r8=
  retRefBase and dereferences mStack[retRefBase]. Local dump shows
  `item={id=12,address=null}` = retRefBase(12) misread as the id int. The NRE
  = mStack[retRefBase] is null at the ldfld (the slot was not populated with
  the ILTypeInstance -- candidate: the binding Add-branch mis-index, OR the
  returned ILTypeInstance was null/freed, OR retRefBase is a non-pre-allocated
  temp beyond mStack.Count). The Call case in TypeSpecializeNeoOpcodes cannot
  seed r8 as an in-frame TestStruct VT (resolved ReturnType = ILTypeInstance, a
  REF type, not the CIL value type) -> ldfld stays heap-arm. DEEP
  (IL-struct-as-ILTypeInstance identity loss + call-return materialization).
- RegisterVMTest.RegisterVMTest04 (RegisterVMTest.cs:107) -- `stfld.ref
  IndexOOB @ Neo.cs:5140` (`mStack[srcIdx]` where srcIdx=65535 garbage). Trace:
  `ArgumentOutOfRangeException @ List`1.get_Item` (AutoList backed) <-
  ExecuteNeo Neo.cs:5140. The store is `viewRectEvent = action` inside
  `ILScrollRect2<T>.SetViewRect` (override in a GENERIC INSTANCE, called
  virtually with >3 args + default `action=null`). PINNED (ground-16, JIT
  dump): the generic instance field layout is CORRECT (ManagedObjects.Count=1);
  the bug is the CALLEE reads `action` (param r4) at frame SrcOffset=12 but the
  value there is 65535 (garbage), NOT the -1 (ldnull) the caller produced -> a
  >3-arg virtual-IL-call (through a generic instance) NeoCallParamMap / frame-
  offset bug (the C7 `isNeoNewobjShape` territory, but for a plain
  callvirt.il). DEEP (call marshalling / param map / calling convention).

### Cluster D -- ldlen on null reflection array, 1 test
- ReflectionTest14 (ReflectionTest.cs:465) -- `ldlen NRE @ Neo.cs:6044`
  (`((Array)mStack[srcIdx]).Length` where mStack[srcIdx] is null). The test
  calls `TestTypeAssignableFrom(typeof(PlayerInfo))` which iterates
  GetProperties/GetFields and does `typeof(ICollection).IsAssignableFrom(
  property.PropertyType)`. The null array is an internal framework reflection
  array (GetFields/GetProperties returned null for an IL array property
  `string[]`/`Detail[]`). NOT a clean ILRuntimeType fix. DEEP.

## Recommended next-batch priority (the 15, by coverage / confidence)
ALL 15 are DEEP singletons (the shallow surface is exhausted -- verified by 48
prior wave-2 children). Ordered by a mix of coverage + fix-confidence:
1. Cluster C TestStructDictionary + Cluster B StructTest11 -- SAME deep class
   (IL-struct boxing/materialization at a List<ILTypeInstance> boundary). A
   single fix to the call-boundary boxing could flip BOTH. Highest coverage.
   Candidate root: the value-type-declared result of a List<ILTypeInstance>
   indexer / the by-value IL-struct arg is not boxed to ILTypeInstance at the
   Callvirt_CLR boundary. DEEP but 2-for-1.
2. Cluster C RegisterVMTest04 (re-pinned ground-16: >3-arg virtual-IL-call
   param-map / frame-offset through a generic instance). The cleanest DEEP
   call-marshalling child.
3. Cluster B MyTest.Test (boxed-CLR-struct enumerator interface dispatch).
4. UnitTest_TestFCP (CLR-struct newobj retDst=-1 -> ctor arg write no-ops) --
   child-28 deferred SYMPTOM 1.
5. UnitTest_TestInline01 (inlined-call reference-arg aliasing) -- JIT-level
   Neo frame-layout fix for inlined calls.
6. ReflectionTest14 (null internal reflection array).
7. StaticTest05 / StructTest6 / StructTest12 -- each a DISTINCT deep root.
8. The Cluster A grab-bag (UnitTest_TestStackRegisterTransition3 /
   ReflectionTest25 / UnitTest_10046 / UnitTest_10051 / DelegateTest42) -- each
   needs its own JIT-dump triage child.

## Lesson reaffirmed (the 12-for-12 / 17-for-17 lineage)
Every prior "foundational gap" verdict was disproven on re-audit. The 15 here
are re-verified singletons; a future child MUST re-audit each before scoping
(e.g. TestStructDictionary's "ldfld heap-arm" is the SYMPTOM; the PRODUCER is
the callvirt.clr return materialization for an IL-struct-element collection --
trace the producer, not just the throwing reader).
