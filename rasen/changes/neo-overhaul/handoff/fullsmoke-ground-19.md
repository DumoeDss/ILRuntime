# Full Neo Smoke Grounding -- 2026-07-16 FRESH re-cluster (19 failing, then 17 post-fix)

## STATUS: FRESH grounding + 2 tractable fixes SHIPPED (DelegateTest19 + TestForEach).
Full smoke **19 -> 17** (verified by re-running the full smoke). NeoStep **403/0**
(no regression). Legacy build VERIFIED clean (0 errors; both fixes Neo-gated /
file-gated). The 17 survivors are a STRICT SUBSET of the 19 (no new failures).

## Fresh ground (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch)
`Ran 937 tests, 19 failded, 20 ignored, 7 todos` (exit 127 = the known graceful
Dict-NRE crash AFTER the summary; the summary is emitted). NeoStep **403/0**.
This is the CURRENT frontier after 45+ wave-2 children drove 189 -> 19.

## The CURRENT 19, clustered by PINNED root (fresh run)

Each row = exception type + the throwing JIT opcode + the Neo.cs frame + the
test-internal throwing site. "throw@7115" = the test's OWN assertion fired
(reaches the CIL Throw handler @ Neo.cs:7115); each is a DISTINCT value-
corruption root, NOT a single fix.

### Cluster A -- throw@7115 grab-bag (test's own assertion), 10 tests, DISTINCT roots
- DelegateTest42 (DelegateTest.cs:662) -- delegate assertion (delegate.Target /
  dispatch wrong for an IL-instance-method delegate). Step-19.
- ExpTest_20.UnitTest_TestInline01 (LightTester2.cs:143) -- reference-arg
  aliasing: `Sub(object o){o=null;}` nullifies the caller's local. The callee is
  INLINED; the optimizer shares the frame slot for the caller's `obj` local and
  the inlined param, so `ldnull` on the param clobbers the caller's obj. JIT-
  level (inlined-call register coalescing for a reference). Same defect CLASS as
  child-13 (newobj arg alias) but on an inlined plain Call. [PINNED via JIT dump]
- ExpTest_20.UnitTest_TestFCP (LightTester2.cs:158) -- TestVector3NoBinding ctor
  (reflection fallback, NO redirect) yields (1,0,0) instead of (1,1,0): the 2nd
  ctor arg (y) is lost. Deep (`!isNewObj` ctor path).
- ExpTest_20.UnitTest_TestStackRegisterTransition3 (LightTester2.cs:339) --
  register-transition value corruption (needs JIT dump).
- ReflectionTest25 (ReflectionTest.cs:680) -- CLR attribute reflection
  (GetCustomAttribute wrong/null).
- StaticTest.UnitTest_StaticTest05 (StaticTest.cs:106) -- `ref Vector3
  staticField` write-back lost. MULTI-BUG (ldsflda offset + read-back).
- StructTests.StructTest6 (Structs.cs:262) -- `Dictionary.TryGetValue(strId,
  out StructTest cube)`: the out STRUCT (IL struct with ref fields) is NOT
  written back. Distinct from recluster-38's byref REF-field fix.
- StructTests.StructTest12 (Structs.cs:396) -- generic struct constrained to
  ITestStruct: `new T(){i=10}` -> `ins.i` == 0 (constrained-callvirt property
  set on a generic struct param).
- TestValueTypeBinding.UnitTest_10046 (TestValueTypeBinding.cs:470) --
  TestVector3 via delegate: `a = a + One2; a.X != 2` (delegate VT-arg marshal
  OR struct-newobj retDst gap).
- TestValueTypeBinding.UnitTest_10051 (TestValueTypeBinding.cs:619) --
  Fixed64Vector2 `.x.RawValue` constrained-callvirt property read on a nested
  struct field (distinct from the `+=` ldflda path child neo-nested-ldflda-byref
  fixed).

### Cluster B -- autogen Neo binding stub / upstream arg marshalling, 4 tests
All route `InvokeNeoClrMethod -> autogen *_Neo stub` (or reflection fallback). The
exception comes from UPSTREAM arg marshalling feeding a bad index / a mis-typed
`this`. Distinct per test.
- GenericMethodTest11 (GenericMethodTest.cs:302) -- `constrained.T` -> autogen
  `CompareTo_0_Neo` casts ILTypeInstance->IComparable<int> (constrained dispatch
  on an IL type implementing a CLR interface; the int field value is marshalled
  as `this` instead of the boxed int). Neo.cs:7497. Step-19.
- ReflectionTest10 (ReflectionTest.cs:370) -- autogen `Invoke_1_Neo` ->
  ILRuntimeMethodInfo.Invoke -> framework Enum.ToObject(ILRuntimeType) "Type
  must be a type provided by the runtime". Neo.cs:4212. Deep (ILRuntimeType-vs-
  framework; the neo-ilruntimetype-runtime-bridge EnumToObjectNeo redirect does
  not cover the reflection-Invoke boxing path).
- StructTests.StructTest11 (Structs.cs:365) -- autogen `Add_0_Neo`
  (List<Anim>.Add; Anim is an IL struct {string name; float duration}) -> the
  stub itself is CORRECT (reads this+item via ReadNeoReference + Add); the OOB
  is INSIDE List.Add from upstream IL-struct-boxing marshalling feeding a bad
  mStack index. Neo.cs:4212. (IL-struct boxing at the call boundary.)
- MyTest.Test (Test01.cs:618) -- autogen `get_Current_0_Neo` casts
  String->IEnumerator (enumerator `this` mis-marshalled; a String ends up in the
  `this` slot). Neo.cs:4267.

### Cluster C -- raw ldfld.i4 / stfld.ref owner NRE/OOB (collection/generic), 2 tests
- Test05.TestStructDictionary (Test05.cs:200) -- ldfld.i4 NRE @ Neo.cs:4817 on
  `TestStruct::id` from a List<TestStruct>[i] element (the owner register holds
  flat bytes or a mis-resolved index; Ldfld_I4 calls GetNeoILInstance on it).
- RegisterVMTest04 (RegisterVMTest.cs:107) -- stfld.ref IndexOOB @ Neo.cs:5140
  on `ILScrollRect2<T>::viewRectEvent` (a generic IL type's INHERITED Action ref
  field). PINNED via JIT dump: Operand3 == 0 (correct), so the instance's
  ManagedObjects itself has 0 ref slots -- a GENERIC-INSTANCE field-layout bug
  (the generic instance does not flatten the non-generic base's ref field). Deep
  type-system.

### Cluster D -- ldlen on null reflection array, 1 test
- ReflectionTest14 (ReflectionTest.cs:465) -- ldlen NRE @ Neo.cs:6044. The ldlen
  is likely INSIDE the framework
  `typeof(ICollection).IsAssignableFrom(property.PropertyType)` call operating on
  a null internal reflection array, NOT a clean ILRuntimeType fix. Deep.

### Cluster E -- CLR-enum return sizing, 1 test  *** FIXED this child ***
- DelegateTest19 (DelegateTest.cs:236) -- was List.get_Item OOB @ Neo.cs:4313
  (the Ret handler's vt-with-ref-fields branch). ROOT: `AllocateSlotForType`
  (JITCompiler.cs:2691 `else`) sized a CLR enum as a boxed reference (RefCount=1)
  while the JIT emits enum locals/returns as a FLAT int (`ldc.i4.1`/`initobj`,
  RefCount=0 behavior) -- so a CLR-enum RETURN had returnRefCount=1, entered the
  vt-with-ref-fields Ret branch, and OOB-read a non-existent ref slot. FIX =
  route CLR enums to a primitive-sized branch in AllocateSlotForType (RefCount=0,
  matching IL enums which the ILType branch already sizes TotalReferenceCount==0
  AND matching the JIT's flat-int emission). See "Fixes shipped" below.

### Cluster F -- ExpectException double-wrap, 1 test  *** FIXED this child ***
- Test05.TestForEach (Test05.cs:275) -- `[ILRuntimeTest(ExpectException=
  typeof(NotSupportedException))]`; ParseOne throws NSE("error") which the
  framework SHOULD honor as Pass (PASSES under Legacy). ROOT: the Neo bottom-of-
  method re-throw (ILIntepreter.Neo.cs:7598) ALWAYS wrapped the pending exception
  in a fresh `ILRuntimeException`, even when `ex` was ALREADY an
  ILRuntimeException (the foreach-finally re-throw path: HandleException sets
  `lastCaughtEx = ex` verbatim for a pre-wrapped exception at
  ILIntepreter.cs:4889). The DOUBLE-WRAP made the host's ExpectException check
  (`GetInnerException().GetType()` -- returns the DIRECT inner, another
  ILRuntimeException) mismatch. FIX = `ex is ILRuntimeException ? ex : new
  ILRuntimeException(...)` (mirror the finally-branch guard at ILIntepreter.cs:4889).

## Fixes shipped this child (Neo-gated -> Legacy-neutral)
1. **DelegateTest19 / CLR-enum return sizing** -- `JITCompiler.cs`
   `AllocateSlotForType`: added an `else if (t.IsValueType && !(t is ILType) &&
   t.TypeForCLR.IsEnum)` branch BEFORE the boxed-`else` branch, sizing a CLR enum
   as its underlying primitive (RefCount=0). Consistent with IL enums (already
   RefCount=0 via the ILType branch) and with the JIT's flat-int enum emission.
2. **TestForEach / ExpectException double-wrap** -- `ILIntepreter.Neo.cs` Ret-
   unhandled re-throw: `pendingThrow = ex is ILRuntimeException ? ex : new
   ILRuntimeException(...)` (was an unconditional `new ILRuntimeException`).
   Logically strictly-more-correct (no information lost; the inner's Data already
   carries the appended context from HandleException).

## Recommended next-batch priority (the 17 survivors, by coverage / confidence)
1. Cluster B typeof/ILRuntimeType reflection roots (ReflectionTest10) -- the
   reflection-Invoke boxing path needs the ILRuntimeType unwrapped before the
   framework Enum.ToObject; broader than the existing EnumToObjectNeo redirect.
2. Cluster B constrained dispatch (GenericMethodTest11) + delegate (MyTest.Test)
   -- Step-19 / constrained-callvirt on IL types implementing CLR interfaces.
3. UnitTest_TestInline01 (inlined-call reference-arg aliasing) -- JIT-level
   register-coalescing fix (mirror child-13's runtime rebase, but in the
   inliner/allocator). PINNED.
4. Cluster C RegisterVMTest04 (generic-instance field-layout for an inherited
   ref field) -- deep type-system.
5. Cluster C TestStructDictionary (ldfld.i4 on a List<struct>[i] element) --
   struct-from-collection field access.
6. StaticTest05 / StructTest6 / StructTest12 -- each a DISTINCT deep root.
7. ReflectionTest14 (null internal reflection array) -- deep.
8. The Cluster A grab-bag (UnitTest_TestFCP / TestStackRegisterTransition3 /
   ReflectionTest25 / UnitTest_10046 / UnitTest_10051 / DelegateTest42) -- each
   needs its own JIT-dump triage child.
