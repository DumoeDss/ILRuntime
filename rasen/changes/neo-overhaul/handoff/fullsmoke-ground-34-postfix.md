# Full Neo Smoke Grounding -- 2026-07-15 POST-neo-remaining-34-batch (33 remaining)

Worker: neo-remaining-34-batch (Wave-2 child of neo-overhaul). Branch
`features/object-model-overhaul`. HEAD after this child's uncommitted edit set:
`TypeMakeGenericTypeNeo` (CLRRedirections.cs) + its RedirectMapNeo registration
(AppDomain.cs), both `#if ENABLE_NEO_MODE`-gated.

- Build: `dotnet build ILRuntimeTestCLI -c Debug_Neo --no-incremental
  -p:UseSharedCompilation=false` (0 errors) + TestCases `-c Debug` (0 errors).
- Legacy-neutral VERIFIED: `dotnet build ILRuntimeTestCLI -c Debug` (plain, ENABLE_NEO_MODE
  off) = 0 errors (the new method is `#if`-gated; the registration is `#if`-gated).
- Run (no filter, Neo build): `dotnet run -c Debug_Neo -f net8.0 --project
  ILRuntimeTestCLI --no-build -- TestCases/.../TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true`
- **DELTA: 34 -> 33** (DelegateTest36 flipped green via TypeMakeGenericTypeNeo; zero
  regressions -- the 33 are a strict subset of the 34; exit 127 = known graceful
  pre-existing Dict-NRE crash, summary still emitted).
- NeoStep smoke (no-regression): see `.tmp-r34-neostep.log` (expected ~398/0).

## Section 1 -- The batch fix shipped this child

| file | change | gate |
|---|---|---|
| `ILRuntime/Runtime/Enviorment/CLRRedirections.cs` | ADDED `TypeMakeGenericTypeNeo` (mirrors Legacy `TypeMakeGenericType`: read Type `this` + Type[] args via `ReadNeoReference`; `ToIType` + `MakeGenericInstance` + `ReflectionType`; `WriteNeoObjectResult`). Placed after `ToIType`, wrapped in `#if ENABLE_NEO_MODE`. | Neo-only |
| `ILRuntime/Runtime/Enviorment/AppDomain.cs` | ADDED `RegisterCLRMethodRedirectionNeo(MakeGenericType, TypeMakeGenericTypeNeo)` next to the Legacy `TypeMakeGenericType` registration (line ~296), `#if ENABLE_NEO_MODE`. First-registered-wins preempts the broken autogen `MakeGenericType_4_Neo` stub. | Neo-only |

Why: DelegateTest36 `typeof(Action<>).MakeGenericType(ILMethodParamType)` hit the autogen
`MakeGenericType_4_Neo` stub which calls the framework `instance.MakeGenericType(typeArgs)`
directly. When `this` or a type arg is an `ILRuntimeType` (an IL type / an IL method's
parameter type reached via reflection), the base `System.Type.MakeGenericType` throws
NotImplementedException "Derived classes must provide an implementation" (the runtime-Type
override is absent on ILRuntimeType). The Legacy redirect routes through
`ToIType`+`MakeGenericInstance` (ILRuntime-internal generic construction) -- the Neo twin
mirrors it byte-for-byte. This is the recurring child-22/6/12 "missing-Neo-redirect" defect
class (same shape as EnumToObjectNeo / CreateInstanceNeo / InitializeArrayNeo). Verified:
DelegateTest36 PASSES in isolation post-fix (stash-equivalent: was the only MakeGenericType
NIE in the 34).

## Section 2 -- The 33 SURVIVING failures, clustered by PINNED root (deep singletons)

Each row = a candidate future child. "Quick-win?" = whether it is mechanical
(stale-stub / seeding / missing-redirect) vs deep (multi-step / architectural /
high-regression-risk). All 33 survivors are DEEP or uncertain-root (only DelegateTest36 was
a clean quick-win this pass). Pinning evidence is from the real run + (where noted) JIT dump
/ isolation run / Legacy comparison.

### D1 -- Delegate dispatch arg-marshal (Step 19), single root x3 [DEEP]
`ldfld.i4` owner is a boxed `System.Int32` (the IL-class instance's mStack index was
replaced by its int field value) at `GetNeoILInstance` Neo.cs:7425 -> ExecuteNeo:4604.
Tests (identical msg+frame+IL):
- DelegateExtTest01, DelegateExtTest02, DelegateTest01.
Shape: delegate wraps `IntTest(this DelegateExtObj obj, int a)` (DelegateExtObj = IL class,
field `int Value`). On invoke, the delegate-Target/`this` for the bound extension method is
marshalled so the callee's `obj` slot holds an Int32 (field-0 value) instead of the
ILTypeInstance mStack index -> `obj.AddValue` -> `this.Value` ldfld.i4 -> GetNeoILInstance
sees `mStack[idx] is Int32`. Root is in the Step-19 delegate-invoke arg-marshalling for an
IL-class `this`. HIGH value (3 tests) but touches delegate dispatch (regression risk).

### D2 -- Long-literal corruption via widened temp (ldc.i4 + conv.i8/u8) [DEEP, PINNED]
- ExpTest_10.UnitTest_1020.
Isolation evidence: caller JIT = `0:ldc.i4 r2,20176515; 1:conv.i8 r2,r2; 2:ldc.i4
r3,-1894967296; 3:conv.u8 r3,r3; 4:call UnitTest_1020Sub(r2,r3)`. Legacy prints
`maxExp:2400000000,exp:20176515` and PASSES; Neo prints `maxExp:0,exp:0` and FAILS
(res=-0.010647422). The `2400000000` literal (>int32.max) is CIL `ldc.i4 0x8F0D1800; conv.u8`
(uint->ulong). Both long args arrive as 0 in the callee -> the IL-to-IL call reads the
HIGH dword (offset+4) of each 8-byte long arg, OR the conv-widened temp's frame slot is
sized 4 (ldc.i4) and the 8-byte conv write overflows/ mis-offsets. The conv.u8 unsigned
zero-extend is itself suspect (Neo `ReadConvU8` I4 case does `(ulong)*(int*)` = sign-extend;
ECMA conv.u8 zero-extends -> matches Legacy), but the dominant symptom (exp also =0, not
just maxExp) points to the call-marshalling/slot-sizing of widened long temps, not conv
alone. Needs deep call-convention investigation. NOTE: if long-literal/widened-temp
marshalling is broadly broken it could be higher-value than 1 test -- re-audit scope.

### D3 -- Reference-arg aliasing (object passed by-ref instead of by-value) [DEEP, PINNED]
- ExpTest_20.UnitTest_TestInline01.
Shape: `object obj = new object(); Sub(obj); if (obj==null) throw;` where `Sub(object
o){ o = null; }`. The callee nullifying its LOCAL `o` also nullifies the caller's `obj` ->
the reference arg's slot ALIASES the caller's local (by-ref) instead of by-value copy. Same
defect class as child-13 (newobj arg aliasing) but on a plain `Call`. Fix likely in the
call-arg copy / frame-slot aliasing for reference params (child-13 fixed it only for the
newobj dest/arg case).

### D4 -- Nested-ldflda-on-byref (`outer.Struct.field +=` / `.field`) [DEEP, deferred item]
`ldflda Struct(on outer); ldflda value; ldind.i4; add; stind.i4`. The inner
ldflda-on-a-byref gap. Explicitly deferred by child-27 (surfaced sibling #2) + child F-10.
Tests:
- ExpTest_10.UnitTest_Struct (ldind.i4 IOOB @ Neo.cs:6241), ExpTest_10.UnitTest_Struct2
  (ldind.i4 NRE @ Neo.cs:6240) -- `obj.Struct.value += 111`.
- TestValueTypeBinding.UnitTest_10051 (property-read form `.x.RawValue`, child F-10 noted).
Needs the ldflda-on-byref + ldind/stind nested-address path. Multi-step.

### D5 -- Autogen stub / IL-type-bridge (framework method on ILRuntimeType) [DEEP]
The autogen `*_Neo` stub calls the framework method, which rejects an ILRuntimeType /
mis-marshalled arg. NO Legacy hand-redirect to twin (unlike DelegateTest36) -> would need a
from-scratch hand-redirect or an ILRuntimeType-bridge fix. Per-test:
- GenericMethodTest11: autogen `CompareTo_0_Neo` casts ILTypeInstance->IComparable<int>
  (constrained. T dispatch on an IL type implementing a CLR interface).
- ReflectionTest06: autogen `SetValue_1_Neo` -> ILRuntimeFieldInfo.SetValue NRE.
- ReflectionTest10: autogen `Invoke_1_Neo` -> ILRuntimeMethodInfo.Invoke -> CheckCLRTypes ->
  framework Enum.ToObject(ILRuntimeType) "Type must be a type provided by the runtime".
  MethodInfoInvoke HAS a Legacy redirect (no Neo twin) but the IL-method execution path
  under Neo is non-trivial (byte* frame setup).
- StructTests.StructTest11: autogen `Add_0_Neo` (List<ILTypeInstance>.Add) -- the stub
  ITSELF is correct; the upstream `this`/item marshalling feeds a bad index -> List.get_Item
  OOB.
- MyTest.Test: autogen `get_Current_0_Neo` casts String->IEnumerator (enumerator
  mis-marshalled).
- Test05.TestGenericMethod2: autogen `ChangeType_1_Neo` -> Convert.DefaultToType "Invalid
  cast String->Int32". EXPERIMENT (tried + reverted, NOT shipped): a ConvertChangeTypeNeo
  redirect that unwraps `ILRuntimeType -> ILType.TypeForCLR` PARTIALLY worked -- it made
  `Convert.ChangeType("123", typeof(A))` (A=int) PASS, but `Convert.ChangeType("345.678",
  typeof(B))` (B=double) then threw FormatException "345.678 not in correct format" (parsed
  as Int32). PINNED: `typeof(<genericparam>)` under Neo returns an ILRuntimeType whose
  `ILType.TypeForCLR` resolves BOTH A and B to int (B=double is mis-resolved to int). So the
  REAL root is the `typeof(generic-param)` resolution (TypeForCLR of a generic-param
  ILRuntimeType), NOT a simple ChangeType unwrap. A ConvertChangeTypeNeo alone CANNOT fix it
  until typeof(generic-param) TypeForCLR is correct. Candidate child: typeof(generic-param)
  resolution (likely affects more than ChangeType).

### D6 -- raw ldfld.i4 / stfld.ref owner-mismatch on collection struct-by-value [DEEP]
- Test05.TestStructDictionary: `ldfld.i4 TestStruct::id` NRE @ Neo.cs:4604 (owner slot null;
  TestStruct from a Dictionary).
- Test01.UnitTest_Generics, Test01.UnitTest_Generics2: `stfld.ref` NRE @ Neo.cs:4889.
- RegisterVMTest04: `stfld.ref` IndexOutOfRange @ Neo.cs:4910.
The typed ldfld.i4/stfld.ref arms call `GetNeoILInstance(ownerSlot)` but the owner register
holds a struct-by-value / null / bad index (collection-element / generic-field path). Needs
per-test JIT-dump to pin whether it is a marker gap (child-24/29 lineage) or an upstream
materialization bug.

### D7 -- byref out-STRUCT write-back [DEEP, distinct from byref-ref-field]
- StructTests.StructTest6: `Dictionary.TryGetValue(strId, out StructTest cube)` -- the
  `out` STRUCT (IL struct) param is not written back (cube stays default -> `cube.type !=
  "111"` -> throw). recluster-38 fixed byref REF-field marshal; the out-STRUCT shape is a
  distinct sub-root in NeoMarshalByrefFieldToSlot / CopyNeoCallThisBack.
- RefOutTest.UnitTest_NestedGenericRefOut: nested-generic ref/out write-back (throw).

### D8 -- throw-cluster grab-bag (test's own assertion; each distinct root) [DEEP]
Each reaches `throw ex` @ Neo.cs:6862 (the CIL Throw handler) because the test's own assertion
fired. NOT single-fix; each needs its value-corruption root pinned:
- DelegateTest42, DelegateTest43 (delegate assertions).
- ExpTest_20.UnitTest_TestFCP (float/Convert.ToInt64 arithmetic in ToColor).
- ExpTest_20.UnitTest_TestStackRegisterTransition3 (needs JIT dump).
- ReflectionTest25 (reflection assertion).
- StaticTest.UnitTest_StaticTest05 (needs JIT dump).
- StructTests.StructTest12 (needs JIT dump).
- Test05.TestForEach ("error" -- foreach/dict enumerator).
- TestValueTypeBinding.UnitTest_10046 (VT-binding assertion).

### D9 -- ldlen on null (reflection) [DEEP]
- ReflectionTest14: `ldlen` NRE @ Neo.cs:5799 -- a reflection result array (GetProperties /
  GetMethods on PlayerInfo) is null -> ldlen derefs null. Reflection-GetProperties under Neo.

### D10 -- Delegate callvirt misc [DEEP]
- DelegateTest19: List.get_Item IndexOutOfRange @ Neo.cs:4132 (ret area).
- DelegateTest43: (D8 throw).

### D11 -- Hotfix Neo-bridge field-index (Legacy Execute path) [DEEP, cluster-H residual]
Via the LEGACY `Execute` path (hotfix patched IL) calling Neo ILTypeInstance
PushToStack/AssignFromStack:
- HotfixBasicTest04: AssignFromStack field index 1 out of range for HotfixClass___Extra
  (TotalFieldCount=1).
- HotfixBasicTest05: PushToStack field index 0 out of range for
  <PrivateImplementationDetails> (TotalFieldCount=0).
cluster-H commit fd66d81c ("PushToStack/AssignFromStack eval-stack bridge") did NOT clear
these -- a residual hotfix-patched-type vs Neo field-layout mismatch.

## Section 3 -- Recommended next-batch priority (by coverage / confidence)
1. D1 (delegate dispatch arg-marshal, x3) -- highest coverage single root; delegate Step-19.
2. D2 (long-literal/widened-temp, possibly broad) -- correctness; re-audit scope first.
3. D4 (nested-ldflda-on-byref, x3 incl UnitTest_10051) -- known deferred, multi-step.
4. D6 (raw ldfld.i4/stfld.ref collection-struct, x4) -- needs per-test JIT dumps.
5. D7 (byref out-STRUCT, x2) -- distinct from recluster-38.
6. D5 (autogen IL-type-bridge) -- from-scratch redirects; TestGenericMethod2 most tractable.
7. D11 (hotfix Neo-bridge, x2) -- niche.
8. D3, D8, D9, D10 -- individual deep roots.

## Section 4 -- Classification of the ORIGINAL 34 (quick-win vs deep, per test)
QW = quick-win (this child). D = deep (survives).
- DelegateExtTest01/02 -- D (D1)
- DelegateTest01 -- D (D1)
- DelegateTest19 -- D (D10)
- DelegateTest36 -- **QW (FIXED: TypeMakeGenericTypeNeo)**
- DelegateTest42, DelegateTest43 -- D (D8/D10)
- GenericMethodTest11 -- D (D5)
- ExpTest_10.UnitTest_Struct, UnitTest_Struct2 -- D (D4)
- ExpTest_10.UnitTest_1020 -- D (D2)
- ExpTest_20.UnitTest_TestInline01 -- D (D3)
- ExpTest_20.UnitTest_TestFCP, UnitTest_TestStackRegisterTransition3 -- D (D8)
- ReflectionTest06 -- D (D5)
- ReflectionTest10 -- D (D5)
- ReflectionTest14 -- D (D9)
- ReflectionTest25 -- D (D8)
- RefOutTest.UnitTest_NestedGenericRefOut -- D (D7)
- RegisterVMTest04 -- D (D6)
- StaticTest.UnitTest_StaticTest05 -- D (D8)
- StructTests.StructTest6 -- D (D7)
- StructTests.StructTest11 -- D (D5)
- StructTests.StructTest12 -- D (D8)
- Test01.UnitTest_Generics, UnitTest_Generics2 -- D (D6)
- MyTest.Test -- D (D5)
- Test05.TestGenericMethod2 -- D (D5, candidate ConvertChangeTypeNeo)
- Test05.TestStructDictionary -- D (D6)
- Test05.TestForEach -- D (D8)
- TestValueTypeBinding.UnitTest_10046 -- D (D8)
- TestValueTypeBinding.UnitTest_10051 -- D (D4)
- HotfixBasicTest04, HotfixBasicTest05 -- D (D11)
