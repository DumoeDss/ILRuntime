# Full Neo Smoke Grounding -- 2026-07-16 FRESH re-cluster (17 failing, then 16 post-fix)

## STATUS: FRESH grounding + 1 tractable fix SHIPPED (ReflectionTest10).
Full smoke **17 -> 16** (verified by re-running the full smoke AFTER the fix).
NeoStep **404/0** (no regression). Legacy build VERIFIED clean (0 errors; the
fix is `#if ENABLE_NEO_MODE`-gated inside shared Extensions.cs, and Legacy never
reaches that branch for an IL enum -- empirically confirmed). The 16 survivors
are a STRICT SUBSET of the 17 (only ReflectionTest10 removed; no new failures).

## Fresh ground (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch)
PRE-fix: `Ran 938 tests, 17 failded, 20 ignored, 7 todos` (exit 0; no crash this
run -- the known graceful Dict-NRE crash did not surface; the summary is emitted
either way). NeoStep **404/0**.
POST-fix: `Ran 938 tests, 16 failded, 20 ignored, 7 todos` (exit 0).
This is the CURRENT frontier after 46+ wave-2 children drove 189 -> 17.

## Decisive diagnostic (ReflectionTest10 -- the ONLY shallow root)
Temporarily instrumented `Extensions.CheckCLRTypes` IsEnum branch (Extensions.cs:295):
- **NEO**: `pt=ILRuntime.Reflection.ILRuntimeType  pt.FullName=TestCases.EnumTest/TestEnum  obj=System.Int64`
  -> the IL enum getter's reflection-Invoke reaches CheckCLRTypes with pt = the
     IL enum's ILRuntimeType; framework `Enum.ToObject(ILRuntimeType, ...)` throws
     "Type must be a type provided by the runtime".
- **LEGACY**: `pt=System.RuntimeType  pt.FullName=System.Reflection.BindingFlags  obj=System.Int32`
  -> Legacy reaches this branch ONLY for a REAL CLR enum (BindingFlags, from
     `GetProperties(BindingFlags.Public)`), NEVER for the IL enum TestEnum.
     Legacy routes IL-enum reflection returns through a different path; the fix
     branch is unreachable for IL enums under Legacy => Legacy-neutral.
(`TestEnum : long` in EnumTest.cs:19, so obj=Int64 is CORRECT, not a sizing bug.)

## The CURRENT 17 (pre-fix), clustered by PINNED root (fresh run)

Each row = exception type + the throwing JIT opcode + the Neo.cs frame + the
test-internal throwing site. "throw@7115" = the test's OWN assertion fired
(reaches the CIL Throw handler @ Neo.cs:7115); each is a DISTINCT value-
corruption root, NOT a single fix.

### Cluster A -- throw@7115 grab-bag (test's own assertion), 10 tests, DISTINCT roots
- DelegateTest42 (DelegateTest.cs:662) -- delegate assertion (delegate.Target /
  dispatch wrong for an IL-instance-method delegate). Step-19.
- ExpTest_20.UnitTest_TestInline01 (LightTester2.cs:143) -- reference-arg
  aliasing: `Sub(object o){o=null;}` nullifies the caller's local. The callee is
  INLINED; JIT dump shows caller obj=r0, inlined arg=r2 (distinct registers), yet
  r0 ends up null at the check -> Neo FRAME LAYOUT for inlined calls aliases the
  caller's local and the inlined arg slot (inlined-call register coalescing for a
  reference). Same defect CLASS as child-13 (newobj arg alias) but on an inlined
  plain Call. [PINNED via JIT dump] DEEP (JIT inliner/allocator).
- ExpTest_20.UnitTest_TestFCP (LightTester2.cs:158) -- TestVector3NoBinding ctor
  (reflection fallback, NO redirect) yields (1,0,0) instead of (1,1,0): the 2nd
  ctor arg (y) is lost. Deep (`!isNewObj` ctor path).
- ExpTest_20.UnitTest_TestStackRegisterTransition3 (LightTester2.cs:339) --
  register-transition value corruption (needs JIT dump). Deep.
- ReflectionTest25 (ReflectionTest.cs:680) -- CLR attribute reflection
  (GetCustomAttribute wrong/null). Deep.
- StaticTest.UnitTest_StaticTest05 (StaticTest.cs:106) -- `ref Vector3
  staticField` write-back lost. MULTI-BUG (ldsflda offset + read-back). Deep.
- StructTests.StructTest6 (Structs.cs:262) -- `Dictionary.TryGetValue(strId,
  out StructTest cube)`: the out STRUCT (IL struct with ref fields) is NOT
  written back. Distinct from recluster-38's byref REF-field fix. Deep.
- StructTests.StructTest12 (Structs.cs:396) -- generic struct constrained to
  ITestStruct: `new T(){i=10}` -> `ins.i` == 0 (constrained-callvirt property
  set on a generic struct param). Deep.
- TestValueTypeBinding.UnitTest_10046 (TestValueTypeBinding.cs:470) --
  TestVector3 via delegate: `a = a + One2; a.X != 2` (delegate VT-arg marshal
  OR struct-newobj retDst gap). Deep.
- TestValueTypeBinding.UnitTest_10051 (TestValueTypeBinding.cs:619) --
  Fixed64Vector2 `.x.RawValue` constrained-callvirt property read on a nested
  struct field (distinct from the `+=` ldflda path child neo-nested-ldflda-byref
  fixed). Deep.

### Cluster B -- autogen Neo binding stub / upstream arg marshalling, 3 tests (was 4)
All route `InvokeNeoClrMethod -> autogen *_Neo stub`. The exception comes from
UPSTREAM arg marshalling feeding a bad index / a mis-typed `this`. Distinct per
test. (ReflectionTest10 was the 4th -- FIXED this child.)
- GenericMethodTest11 (GenericMethodTest.cs:302) -- `constrained Int32; callvirt
  IComparable<int>::CompareTo` on `ldflda data` (int field of SubBind). Autogen
  `CompareTo_0_Neo` reads `this` via ReadNeoReference and gets the ILTypeInstance
  (the containing SubBind) instead of the int field value -> InvalidCast
  ILTypeInstance->IComparable<int>. Neo.cs:7497. Step-19 / constrained-callvirt
  `this` marshalling for autogen CLR-interface bindings. Deep.
- StructTests.StructTest11 (Structs.cs:365) -- autogen `Add_0_Neo`
  (List<Anim>.Add; Anim is an IL struct {string name; float duration}) -> the
  stub reads this+item; the OOB is INSIDE List.Add from upstream IL-struct-boxing
  marshalling feeding a bad mStack index. Neo.cs:4212. (IL-struct boxing at the
  call boundary.) Deep.
- MyTest.Test (Test01.cs:618) -- autogen `get_Current_0_Neo` casts
  String->IEnumerator (Dictionary enumerator `this` mis-marshalled; a String
  ends up in the `this` slot on a later loop iteration). Neo.cs:4267. Deep
  (Step-19 / boxed-CLR-struct enumerator interface dispatch).

### Cluster C -- raw ldfld.i4 / stfld.ref owner NRE/OOB (collection/generic), 2 tests
- Test05.TestStructDictionary (Test05.cs:200) -- ldfld.i4 NRE @ Neo.cs:4817 on
  `TestStruct::id` from a List<TestStruct>[i] element (the owner register holds
  flat bytes or a mis-resolved index; Ldfld_I4 calls GetNeoILInstance on it).
  Deep (struct-from-collection field access).
- RegisterVMTest04 (RegisterVMTest.cs:107) -- stfld.ref IndexOOB @ Neo.cs:5140
  on `ILScrollRect2<T>::viewRectEvent` (a generic IL type's INHERITED Action ref
  field). PINNED via JIT dump: Operand3 == 0 (correct), so the instance's
  ManagedObjects itself has 0 ref slots -- a GENERIC-INSTANCE field-layout bug
  (the generic instance does not flatten the non-generic base's ref field). Deep
  type-system.

### Cluster D -- ldlen on null reflection array, 1 test
- ReflectionTest14 (ReflectionTest.cs:465) -- ldlen NRE @ Neo.cs:6044 on a null
  `FieldInfo[]` (`v5 = null`). The framework
  `typeof(ICollection).IsAssignableFrom(property.PropertyType)` path operates on
  a null internal reflection array (GetFields/GetProperties returned null for an
  IL array property). NOT a clean ILRuntimeType fix. Deep.

### Cluster E -- ILRuntimeType-vs-framework Enum.ToObject (reflection-Invoke), 1 test  *** FIXED this child ***
- ReflectionTest10 (ReflectionTest.cs:370) -- reflecting an IL-enum getter
  (`EnumField`, `TestEnum : long`) via `ILRuntimeMethodInfo.Invoke` ->
  `ReturnType.CheckCLRTypes` (Extensions.cs:295) reached the IsEnum branch with
  `pt = ILRuntimeType` -> framework `Enum.ToObject(ILRuntimeType, obj)` threw
  "Type must be a type provided by the runtime". ROOT: the existing
  `EnumToObjectNeo` redirect covers only the DIRECT IL call path
  (`Enum.ToObject(Type,int)` from interpreted code); the reflection-Invoke
  boxing path calls framework `Enum.ToObject` from NATIVE C# and is not
  intercepted. FIX = handle `pt is ILRuntimeType && it.IsEnum` in the
  CheckCLRTypes IsEnum branch (#if ENABLE_NEO_MODE): build an
  ILEnumTypeInstance carrying the underlying value (mirrors EnumToObjectNeo).
  Legacy never reaches this branch for an IL enum (empirically confirmed) =>
  Legacy-neutral. See "Fix shipped" below.

## Fix shipped this child (Neo-gated -> Legacy-neutral)
1. **ReflectionTest10 / ILRuntimeType Enum.ToObject reflection bridge** --
   `ILRuntime/CLR/Utils/Extensions.cs` `CheckCLRTypes` IsEnum branch: added an
   `#if ENABLE_NEO_MODE` block BEFORE the `Enum.ToObject(pt, obj)` call that, when
   `pt is ILRuntimeType` and `ILType.IsEnum`, constructs an `ILEnumTypeInstance`
   and writes the underlying value bytes (BitConverter of Convert.ToInt64(obj),
   truncated to the underlying-primitive size via `fields.Length`). Mirrors the
   EnumToObjectNeo redirect (CLRRedirections.cs:1048). Also short-circuits when
   `obj is ILEnumTypeInstance` (already boxed). The block is Neo-gated because
   `ILEnumTypeInstance.Primitives` (the Neo byte[] fields) only exists under
   ENABLE_NEO_MODE; Legacy compiles none of it and (empirically) never reaches
   the branch for an IL enum anyway.

## Recommended next-batch priority (the 16 survivors, by coverage / confidence)
ALL 16 remaining are DEEP singletons (the shallow surface is exhausted):
1. Cluster B constrained dispatch (GenericMethodTest11) + enumerator (MyTest.Test)
   -- Step-19 / constrained-callvirt `this` marshalling for autogen CLR-interface
   bindings. GenericMethodTest11 is the cleaner pin (constrained-callvirt on an
   IL instance's primitive field -> autogen binding receives ILTypeInstance
   instead of the field value). Highest Step-19 value.
2. Cluster C RegisterVMTest04 (generic-instance field-layout for an inherited
   ref field) -- deep type-system (generic instance does not flatten non-generic
   base's ref field -> ManagedObjects has 0 ref slots).
3. Cluster C TestStructDictionary (ldfld.i4 on a List<struct>[i] element) --
   struct-from-collection field access.
4. UnitTest_TestInline01 (inlined-call reference-arg aliasing) -- JIT-level Neo
   frame-layout fix for inlined calls (caller local + inlined arg slot alias).
   PINNED but delicate (inliner/allocator surface).
5. StructTest11 (IL-struct boxing at List.Add call boundary).
6. ReflectionTest14 (null internal reflection array -- GetFields/GetProperties
   returns null for an IL array property).
7. StaticTest05 / StructTest6 / StructTest12 -- each a DISTINCT deep root.
8. The Cluster A grab-bag (UnitTest_TestFCP / TestStackRegisterTransition3 /
   ReflectionTest25 / UnitTest_10046 / UnitTest_10051 / DelegateTest42) -- each
   needs its own JIT-dump triage child.
