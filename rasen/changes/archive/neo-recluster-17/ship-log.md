# Ship Log -- neo-recluster-17

## Outcome
Full Neo smoke **17 -> 16** (verified by re-running the full smoke after the fix).
NeoStep **404/0** (no regression). Legacy build clean (0 errors); fix is
`#if ENABLE_NEO_MODE`-gated; Legacy-neutral (empirically: plain-Debug
ReflectionTest1* = 10/0; Legacy never reaches the touched branch for an IL enum).

## What was done
1. FRESH grounding (full smoke, no filter): `Ran 938 tests, 17 failded`.
   Confirmed the 17 == ground-19's survivors (the 2 fixed in the prior child --
   DelegateTest19, TestForEach -- are gone). Full per-test cluster table +
   pinned roots written to
   `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-17.md`.
2. Re-clustered the 17 by root. Found exactly ONE shallow root: ReflectionTest10
   (ILRuntimeType fed to framework Enum.ToObject on the reflection-Invoke path).
   The other 16 are deep singletons (Step-19 delegate/interface dispatch, JIT
   inliner aliasing, generic-instance field layout, IL-struct boxing, reflection
   array population, test-internal value-corruption assertions).
3. Fixed ReflectionTest10 (Neo-gated, Legacy-neutral).

## Fix shipped: ReflectionTest10 -- ILRuntimeType Enum.ToObject reflection bridge
- **Root:** Reflecting an IL-enum getter (`EnumField`, `TestEnum : long`) via
  `ILRuntimeMethodInfo.Invoke` (ILRuntimeMethodInfo.cs:186
  `ReturnType.CheckCLRTypes(res)`) reached the `CheckCLRTypes` IsEnum branch
  (Extensions.cs:295) with `pt = ILRuntimeType`. The framework
  `Enum.ToObject(ILRuntimeType, obj)` rejects ILRuntimeType ("Type must be a type
  provided by the runtime"). The existing `EnumToObjectNeo` redirect
  (CLRRedirections.cs:1048) covers only the DIRECT IL call path, not the NATIVE
  reflection-Invoke path.
- **Decisive diagnostic:** instrumented Extensions.cs:295.
  - NEO: `pt=ILRuntimeType(TestEnum) obj=Int64` -> reaches branch, throws.
  - LEGACY: `pt=RuntimeType(BindingFlags) obj=Int32` -> reaches branch ONLY for a
    real CLR enum, NEVER for the IL enum. Legacy routes IL-enum reflection returns
    a different way; the branch is unreachable for IL enums under Legacy.
- **Fix:** in `Extensions.CheckCLRTypes` IsEnum branch, an `#if ENABLE_NEO_MODE`
  block: if `obj is ILEnumTypeInstance` return it; else if `pt is ILRuntimeType`
  and `it.IsEnum`, build an `ILEnumTypeInstance`, write the underlying value
  bytes (`BitConverter.GetBytes(Convert.ToInt64(obj))`, truncated to
  `ins.Primitives.Length`), return it. Mirrors EnumToObjectNeo. Neo-gated because
  `ILEnumTypeInstance.Primitives` (Neo byte[] fields) exists only under
  ENABLE_NEO_MODE; fall-through to `Enum.ToObject` preserved for real CLR enums.
- **Files:**
  - `ILRuntime/CLR/Utils/Extensions.cs` (CheckCLRTypes IsEnum branch, +24 / -0,
    Neo-gated).

## Verification
- **FULL SMOKE (truth):** PRE `Ran 938, 17 failded` -> POST `Ran 938, 16 failded`
  (ReflectionTest10 removed; the 16 survivors are a STRICT SUBSET, no new
  failures). exit 0 both runs (no crash).
- **NeoStep smoke:** 404/0 (no regression).
- **Legacy-neutral:** plain `Debug` build = 0 errors; plain-Debug + useRegister
  ReflectionTest1* = 10/0 (ReflectionTest10 still passes under Legacy).

## Remaining (16, all DEEP singletons -- the shallow surface is exhausted)
ReflectionTest14, ReflectionTest25, DelegateTest42, GenericMethodTest11,
MyTest.Test, ExpTest_20.UnitTest_TestInline01, ExpTest_20.UnitTest_TestFCP,
ExpTest_20.UnitTest_TestStackRegisterTransition3, RegisterVMTest04,
StaticTest.UnitTest_StaticTest05, StructTests.StructTest6, StructTests.StructTest11,
StructTests.StructTest12, Test05.TestStructDictionary,
TestValueTypeBinding.UnitTest_10046, TestValueTypeBinding.UnitTest_10051.
Clustered + pinned in handoff/fullsmoke-ground-17.md. Next highest-value:
GenericMethodTest11 (constrained-callvirt `this` marshal for autogen CLR-
interface bindings) and RegisterVMTest04 (generic-instance inherited ref-field
layout).
