# Proposal: neo-autogen-binding-invoke (Wave-2 child of neo-overhaul)

## Context
Fresh-60 cluster D = 8 autogen-binding-invoke failures @ `ILIntepreter.Neo.cs`
3960/4015/3425 (callvirt.clr / Callvirt / Call paths into `InvokeNeoClrMethod`).
Baseline full Neo smoke: 931 ran, 55 failed.

## Re-audit finding: cluster D is MULTI-ROOTED (no single-fix cluster)
The 8 tests were re-verified at the 55 baseline (all PASS on Legacy plain
Debug+useRegister=true, confirming Neo-specificity). Grouped by real root:

| Sub-cluster | Tests | Root | Tractable here? |
|---|---|---|---|
| byref-enum marshal | UnitTest_RefCLREnum | reflection fallback boxes byref enum as Int32 -> CheckValue rejects `Int32 -> EnumType&` | YES (this child) |
| generic-method redirect | CLRBindingTest07/08 | Neo resolves `LoadAsset<String>`/`<Int32>` to the `<TestCLRBinding>` stub (`LoadAsset_1_Neo`); JIT dump shows `LoadAsset[TestCLRBinding]` for a `<String>` caller body | NO (deep JIT generic-arg specialization; high regression risk) |
| IL-struct in CLR generic collection | StructTest11 | `List<Anim>` (Anim = IL struct) maps to `List<ILTypeInstance>.Add`; caller passes flat struct bytes but the stub reads an mStack index -> OOB | NO (implicit IL-struct boxing on CLR-generic crossing) |
| boxed-struct interface dispatch | MyTest.Test | `Dictionary.Enumerator` (CLR struct) boxed to `IEnumerator`; get_Current `this` is a String not the boxed enumerator | NO (boxed-struct-to-interface marshal) |
| reflection SetValue | ReflectionTest06 | `FieldInfo.SetValue(Inst, 1.2f)`; obj arrives non-ILTypeInstance -> NRE at ILRuntimeFieldInfo.cs:227 (suspected box-float arg-register aliasing, child-13 lineage) | NO (needs instrumentation to pin) |
| ILRuntimeType vs CLR framework | ReflectionTest10, TestGenericMethod2 | `Enum.ToObject`/`Convert.ChangeType` receive an ILRuntimeType (or a value routed through CheckCLRTypes:295) the framework rejects ("Type must be runtime" / "Invalid cast") | NO (reflection/type-representation; shared CheckCLRTypes is Legacy-touching) |

## Scope of THIS child
Fix the LARGEST *cleanly-pinnable, low-risk* sub-cluster = **byref-enum marshal
(UnitTest_RefCLREnum)**. The other 7 are distinct deep roots, reported here for
follow-up children (each its own child; do NOT bundle).

## The fix (Neo-only, Legacy-neutral)
The Neo `CLRMethod.Invoke(byte* targetBase, ...)` reflection fallback (the path
that handles any byref-param CLR method, since `InvokeNeoClrMethod` bypasses the
autogen redirect for byref methods, `ILIntepreter.Neo.cs:1264`) read a CLR enum
param's underlying Int32 flat bytes and boxed them as a raw `Int32`. For a byref
enum param, `MethodBase.Invoke` -> `System.RuntimeType.CheckValue` requires the
boxed value to BE the enum type against the `EnumType&` parameter, so it threw
`ArgumentException: Object of type 'System.Int32' cannot be converted to type
'EnumType&'`.

Fix at `CLRMethod.cs:594`: split the `if (t == typeof(int) || t.IsEnum)` arm. For
an enum param, read the underlying Int32 then box as the enum via
`Enum.ToObject(t, ...)` WHEN the param is byref (`byRefSlotOff[i] >= 0`); by-value
enums keep the Int32 box (byte-identical to HEAD; the autogen redirect path owns
by-value enums). The post-call write-back (`:675` `et.IsEnum` arm) already
flattens the (possibly-mutated) boxed enum back to its Int32 bytes via
`WriteNeoValueType`, so the round-trip is correct.

`CLRMethod.Invoke(byte*)` is the NEO reflection overload; Legacy uses the
`Invoke(StackObject*)` overload. The change is structurally Neo-only.

## Why "largest" is 1 test here
The task hints (missing-Neo-redirect / stale-autogen-stub / projection) did NOT
materialize: the autogen `*_Neo` stubs are NOT stale (`ReadNeoReference` matches
the current Neo layout; verified), the redirects ARE registered, and the
projection (`ProjectNeoClrCallRefArgs`) does not corrupt these calls. Each
surviving D test is a distinct deep root (JIT generic specialization, IL-struct
boxing, boxed-struct interface, reflection, ILRuntimeType). The byref-enum gap
is the one clean, isolated, low-risk fix; the rest are reported honestly as
follow-up candidates.

## Verify (truth = full-smoke number)
- Name-filter: `UnitTest_RefCLREnum` + `NeoStep_RefClrEnum_TC1` PASS after fix.
- Stash-toggle: revert ONLY `CLRMethod.cs` -> probe FAILS (`ArgumentException`
  Int32 -> TestCLREnum&) -> restore -> PASS (airtight).
- FULL SMOKE delta: `55 -> N` (recorded in handoff).
- NeoStep 0-failures (no regression).
- Legacy-neutral (Neo `byte*` overload only).
