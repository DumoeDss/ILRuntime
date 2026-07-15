# Full Neo Smoke Grounding -- 2026-07-16 FRESH re-cluster (16 failing -> 15 post-fix)

## STATUS: FRESH grounding + 1 tractable fix SHIPPED (GenericMethodTest11).
Full smoke **16 -> 15** (verified by re-running the full smoke AFTER the fix).
NeoStep **404/0** (no regression). Legacy-neutral (the fix is inside the
`#if ENABLE_NEO_MODE` file-gated `ILIntepreter.Neo.cs` -> structural). The 15
survivors are a STRICT SUBSET of the 16 (only GenericMethodTest11 removed; no
new failures).

## Fresh ground (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch)
PRE-fix: `Ran 938 tests, 16 failded, 20 ignored, 7 todos` (exit 127 = known
graceful Dict-NRE crash; summary emitted either way). NeoStep **404/0**.
POST-fix: `Ran 938 tests, 15 failded, 20 ignored, 7 todos` (exit 127).
This is the CURRENT frontier (the prior ground-17 was STALE: its RegisterVMTest04
PIN was WRONG -- see Cluster C).

## IMPORTANT CORRECTION to ground-17 (the 12-for-12 re-audit lesson holds)
ground-17's RegisterVMTest04 pin ("generic-instance field-layout bug ->
ManagedObjects has 0 ref slots") is DISPROVEN. Empirically (instrumented
`ILType.InitializeFields` finalizer): the generic instance
`ILScrollRect2<ScrollItem2>` has `TotalReferenceCount = 1` (CORRECT -- it
flattens the non-generic base `ILScrollRect2`'s `viewRectEvent` ref field),
and the instance's `ManagedObjects.Count = 1` at the throwing site. The real
root is a >3-arg virtual-IL-call PARAM-MARSHALLING bug (see Cluster C). ALWAYS
re-audit a pinned root before scoping a child.

## The CURRENT 16 (pre-fix), clustered by PINNED root (fresh run, this child)

Each row = exception type + the throwing site. "throw@7115" = the test's OWN
assertion fired (reaches the CIL Throw handler @ Neo.cs:7115); each is a
DISTINCT value-corruption root, NOT a single fix.

### Cluster A -- throw@7115 grab-bag (test's own assertion), 9 tests, DISTINCT roots
- DelegateTest42 (DelegateTest.cs:662) -- delegate assertion (delegate.Target /
  dispatch wrong for an IL-instance-method delegate). Step-19.
- ExpTest_20.UnitTest_TestInline01 (LightTester2.cs:143) -- reference-arg
  aliasing on an INLINED plain Call: `Sub(object o){o=null;}` nullifies the
  caller's local `obj`. PINNED (JIT dump): the callee is inlined; the inlined
  arg slot aliases the caller's local -> by-ref instead of by-value. Same
  defect CLASS as child-13 (newobj arg alias) but on an inlined plain Call.
  DEEP (JIT inliner/allocator frame layout).
- ExpTest_20.UnitTest_TestFCP (LightTester2.cs:158) -- `new TestVector3NoBinding
  (num1,num2,num3)` (CLR struct, NO binder, reflection-fallback ctor) yields
  (1,0,0) instead of (1,1,0): the 2nd ctor arg (y/num2) is lost. This is
  child-28's explicitly-deferred SYMPTOM 1 (CLR-struct newobj: retDst=-1/null
  -> the ctor's write-to-retDst no-ops). DEEP (struct-newobj dest convention).
- ExpTest_20.UnitTest_TestStackRegisterTransition3 (LightTester2.cs:339) --
  register-transition value corruption (needs JIT dump). DEEP.
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
  struct field (distinct from the `+=` ldflda path child neo-nested-ldflda-byref
  fixed). DEEP.

### Cluster B -- autogen Neo binding stub / upstream arg marshalling, 2 tests
All route `InvokeNeoClrMethod -> autogen *_Neo stub`. Distinct per test.
- StructTests.StructTest11 (Structs.cs:365) -- autogen `Add_0_Neo`
  (List<Anim>.Add; Anim is an IL struct {string name; float duration}) -> the
  stub reads this+item; the OOB is INSIDE List.Add from upstream IL-struct-boxing
  marshalling feeding a bad mStack index. Neo.cs:4212. (IL-struct boxing at the
  call boundary.) DEEP.
- MyTest.Test (Test01.cs:618) -- autogen `get_Current_0_Neo` casts
  String->IEnumerator (Dictionary enumerator `this` mis-marshalled; a String
  ends up in the `this` slot on a later loop iteration). Neo.cs:4267. DEEP
  (Step-19 / boxed-CLR-struct enumerator interface dispatch).

### Cluster C -- raw call/param marshalling, 2 tests
- RegisterVMTest04 (RegisterVMTest.cs:107) -- IndexOOB @ Neo.cs:5140
  (`mStack[srcIdx]` where srcIdx=65535). The store is `viewRectEvent = action`
  inside `ILScrollRect2<T>.SetViewRect` (override in a GENERIC INSTANCE, called
  virtually with >3 args + default-value `action=null`). PINNED this child
  (DISPROVES ground-17's field-layout pin): the generic instance's field layout
  is CORRECT (ManagedObjects.Count=1, field ref offset=0). The bug is the CALLEE
  reads `action` (param r4) at frame SrcOffset=12 but the value there is 65535
  (garbage), NOT the -1 (ldnull) the caller produced. The caller JIT is
  `ldflda.skillView; ldc.0(type); ldc.0(isAnim); ldc.1(isJudgeEmpty); ldnull r7
  (action); push r3(this); push r4(type); callvirt.il r5,r6,r7 thisArg=0`. The
  callee action slot receives a misaligned/wrong caller source -> a >3-arg
  virtual-IL-call (through a generic instance) NeoCallParamMap / frame-offset
  bug (the C7 `isNeoNewobjShape` fix territory, but for a plain callvirt.il).
  DEEP (call marshalling / param map / calling convention).
- Test05.TestStructDictionary (Test05.cs:200) -- NRE @ Neo.cs:4817 (ldfld.i4) on
  `TestStruct::id` from a `List<TestStruct>[i]` element (`item = lists[i];
  item.id`). `List<TestStruct>.this[i]` returns the struct by value (internally
  a CLR-struct-array `ldelem.any`), and the field read on that result
  mis-resolves the owner. This is the ldelem.any/stelem.any CLR-struct-array
  path child-26 explicitly noted as separately broken. DEEP.

### Cluster D -- ldlen on null reflection array, 1 test
- ReflectionTest14 (ReflectionTest.cs:465) -- ldlen NRE @ Neo.cs:6044 on a null
  `FieldInfo[]` (`v5 = null`). The framework
  `typeof(ICollection).IsAssignableFrom(property.PropertyType)` path operates on
  a null internal reflection array (GetFields/GetProperties returned null for an
  IL array property). NOT a clean ILRuntimeType fix. DEEP.

### Cluster E -- constrained-callvirt `this` marshal (primitive field of IL instance), 1 test  *** FIXED this child ***
- GenericMethodTest11 (GenericMethodTest.cs:302) -- `data.CompareTo(value)` on
  `TestBind<int>.data` (an int field of the IL instance SubBind). CIL:
  `ldflda data; push; ...; constrained Int32; callvirt IComparable<int>::
  CompareTo`. The constrained-callvirt box-once arm (`ILIntepreter.Neo.cs` ~7380
  `if (thisObjIdx >= 0) boxedReceiver = mStack[thisObjIdx]`) grabbed the
  ILTypeInstance (the field's OWNER) instead of reading the primitive int VALUE
  at `Primitives[thisByteOff]`. ROOT: the byref from `ldflda <primField>` is
  `(objIdx=IL owner, thisByteOff=Primitives offset)`; for a PRIMITIVE
  constrained type the receiver is the field VALUE, not the owner. The autogen
  `CompareTo_0_Neo` then InvalidCast ILTypeInstance->IComparable<int>. FIX =
  in the `thisObjIdx >= 0` branch, when `mStack[thisObjIdx]` is an ILTypeInstance
  (or CrossBindingAdaptorType) and `constrainedType.IsPrimitive`, read+box the
  value from `conIli.Primitives[thisByteOff]` via `NeoBoxReturnValue` (mirror
  the frame-native IsPrimitive branch, sourced from the instance's Primitives
  via a `fixed` pin). A boxed primitive on mStack is a System.Int32, NOT an
  ILTypeInstance, so the discriminator never intercepts a genuine already-boxed
  receiver -> no regression on the existing box-once semantics. Primitive-only
  (a CLR-VT field of an IL instance stays the existing behavior). See "Fix
  shipped" below.

## Fix shipped this child (Neo-gated -> Legacy-neutral)
1. **GenericMethodTest11 / constrained-callvirt primitive-field-of-IL-instance
   `this` marshal** -- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`
   Constrained arm, the `if (thisObjIdx >= 0)` box-once sub-branch: before
   `boxedReceiver = mStack[thisObjIdx]`, unwrap to ILTypeInstance (plain or via
   CrossBindingAdaptorType) and, when `constrainedType.IsPrimitive` and the
   instance has a non-null `Primitives`, read+box the value from
   `Primitives + thisByteOff` via `NeoBoxReturnValue(constrainedType, ...,
   GetPrimitiveSize(constrainedType))` (Primitives pinned with `fixed`). Else
   fall back to the existing `boxedReceiver = mStack[thisObjIdx]` (genuine
   already-boxed receiver / ref-type). The whole arm is in the file-gated Neo
   file -> Legacy-neutral by construction; NeoStep 404/0; full smoke 16->15
   (strict subset, no new failures).

## Recommended next-batch priority (the 15 survivors, by coverage / confidence)
ALL 15 remaining are DEEP singletons (the shallow surface is exhausted):
1. Cluster C RegisterVMTest04 (re-pinned this child: >3-arg virtual-IL-call
   param-map / frame-offset bug through a generic instance). The cleanest next
   deep child: dump the NeoCallParamMap (PrimitiveSrc/Dst) for the callvirt.il
   and find why the callee's action (param 4) reads SrcOffset=12 with garbage
   instead of the caller's r7 (ldnull=-1). Likely the same defect class as C7
   (`isNeoNewobjShape` in LowerNeoOffsets) but for a plain callvirt.il.
2. Cluster B StructTest11 (IL-struct boxing at List.Add call boundary) +
   MyTest.Test (boxed-CLR-struct enumerator interface dispatch) -- Step-19.
3. Cluster C TestStructDictionary (ldelem.any CLR-struct-array field read) --
   the ldelem.any/stelem.any path child-26 deferred.
4. UnitTest_TestFCP (CLR-struct newobj retDst=-1 -> ctor arg write no-ops) --
   child-28's deferred SYMPTOM 1 (struct-newobj dest convention).
5. UnitTest_TestInline01 (inlined-call reference-arg aliasing) -- JIT-level Neo
   frame-layout fix for inlined calls.
6. ReflectionTest14 (null internal reflection array).
7. StructTest6 / StructTest12 / StaticTest05 -- each a DISTINCT deep root.
8. The Cluster A grab-bag (UnitTest_TestStackRegisterTransition3 /
   ReflectionTest25 / UnitTest_10046 / UnitTest_10051 / DelegateTest42) -- each
   needs its own JIT-dump triage child.
