# Ship Log — neo-delegate-adapter-clr-cast (Wave-2 child C1)

**Change:** IL delegate (`MethodDelegateAdapter`/`FunctionDelegateAdapter`) crossing to CLR must convert to a
real CLR `Action`/`Func` via `Type.CheckCLRTypes`; Neo omitted it at 3 sites → ~32 `InvalidCastException`s.
**Capability:** `neo-dispatch` (ADDED requirement).
**Pipeline:** small-feature (grounding -> re-audit -> propose+apply -> verify -> review -> ship).
**Date:** 2026-07-13. **Branch:** `features/object-model-overhaul`. **Base HEAD:** `57d94231`.

## Root cause (PINNED via Neo-vs-Legacy code comparison + real run)
An IL delegate is a `MethodDelegateAdapter`/`FunctionDelegateAdapter` (derives `ILTypeInstance`). At every IL→CLR
boundary it must be converted to the real CLR delegate via `Type.CheckCLRTypes(value)` (routes through
`IDelegateAdapter.GetConvertor` → `DelegateManager.ConvertToDelegate`). Legacy applies this at every boundary;
Neo omitted it at three sites, so the raw adapter hit a CLR `(ConcreteDelegate)v` cast and threw
`InvalidCastException`. Neo's reflection fallback (`CLRMethod.Invoke:519-524`) already converts -- only these
sites leaked:
- **Site A (Stsfld, CLR static ref field):** `ILIntepreter.Neo.cs:~4606` → `CLRType.SetStaticFieldValue` → autogen `set_*` `(Delegate)v`. Legacy mirrors at `Register.cs:3308`.
- **Site C (instance CLR-object field):** `NeoWriteClrObjectField:~6621` → `SetFieldValue` → setter `(delegate)v`.
- **Site B (autogen CLR bindings):** committed Neo bindings did `(T)ReadNeoReference(...)`. The GENERATOR is already correct (`BindingGeneratorExtensions.cs:244-246`, Step-19 fix, emits `(T)typeof(T).CheckCLRTypes(v, IsDelegate)`) -- the committed bindings are STALE (predate the fix; regen is GUI-bound). Exactly the child-28 stale-binding class.

## What shipped (Neo-gated; Legacy-neutral)
- **Fix A** — `ILIntepreter.Neo.cs` Stsfld CLR-static ref branch (~:4606): `value = ft.CheckCLRTypes(value);` before `SetStaticFieldValue`.
- **Fix C** — `ILIntepreter.Neo.cs` `NeoWriteClrObjectField` (~:6621): resolve field via `ct.GetField(fieldHash)`, `value = f.FieldType.CheckCLRTypes(value);` (null-guarded).
- **Fix B** — hand-ported **41 delegate-param sites across 10 autogen bindings** to `(T)typeof(T).CheckCLRTypes(ReadNeoReference(...), IsDelegate)` -- byte-identical to the current generator output (reviewer spot-confirmed: 0 cast-type/typeof-type mismatches, 0 sites deviating from `(TypeFlags)8`, non-delegate params + Legacy `#else` halves untouched). Files: StaticGenericMethods, GenericExtensions, DelegateTest, IntDelegate, IntDelegate2, List_1_Action_1_Int32, List_1_Func_2_Int32_Int32, List_1_ILTypeInstance, Enumerable, Interlocked.

## Verification (the truth = the full-smoke number)
- **FULL SMOKE: 189 → 153 = −36 failures.** The C1 delegate-cast cluster (~32 `InvalidCastException` "cast
  MethodDelegateAdapter/FunctionDelegateAdapter to Action/Func") ELIMINATED (→0). `InvalidCastException` total
  56 → 40. Confirmed on a second full smoke after a BOM strip (153 both times).
- C1 tests confirmed green under Neo: DelegateTest01/03/06/07/11/18/20/21/41/45, DelegateInnerTest.TestRun,
  GenericStaticMethodTest1-8, GenericExtensionMethod1Test1/2 + GenericExtensionMethod2Test1-8, GenericMethodTest14,
  Test03.Test05, InheritanceTest10, CLRBindingTest07 (bonus). A few C1-listed tests PROGRESSED (not regressed) to a
  later different-cluster error (the delegate cast was their first error): DelegateTest19→C10, DelegateTest43→C4,
  DelegateTest01→Step-17/13b deferred NIE, UnitTest_10046→generic Exception.
- **Stash-toggle airtight:** stash (Neo.cs + 10 bindings) → DelegateTest01/GenericStaticMethodTest1/DelegateTest41/InheritanceTest10 all FAIL with the delegate-cast error; pop → PASS.
- **NeoStep 380/0 unchanged** (no regression in the green baseline).
- **Legacy-neutral:** DelegateTest01/GenericStaticMethodTest1/GenericExtensionMethod1Test1/DelegateTest41 PASS under plain Debug+useRegister=true. Fixes A/C in the file-gated Neo interpreter; Fix B edits only `#if ENABLE_NEO_MODE` regions.

## Review
**APPROVE-WITH-FINDINGS** (reviewer != implementer; 0 Blocker/Major). Fix-B byte-fidelity CONFIRMED (41 sites
match generator template; 0 deviations; non-delegate + Legacy untouched). C1-tests-green + NeoStep-380/0 +
Legacy-neutral all re-confirmed by reviewer's own run.
- Minor M1 (perf, non-blocking): Fix C's `ct.GetField(fieldHash)` re-resolves a field that `CLRType.SetFieldValue` also resolves internally -- a per-write dict cost on the hot Stfld path; suggest a follow-up to share the resolved FieldInfo.
- Trivial T1: the 3 Interlocked CompareExchange sites are correct but slightly out-of-cluster (no C1 test exercises them). T2 (pre-existing): the 10 touched binding files may harbor other stale non-delegate sites; full GUI regen is the follow-up.

## Delivery
**Mode:** local commit + push (portfolio per-child delivery per parent directive). No PR.

## Durable findings (for future planning)
1. **The IL→CLR delegate conversion (`CheckCLRTypes`) belongs at EVERY marshalling boundary.** Neo's reflection
   fallback centralizes it (`CLRMethod.Invoke:519-524`), but the RedirectionNeo (autogen) path + the field-store
   arms (`SetStaticFieldValue`/`SetFieldValue`) each need it explicitly -- there is no single chokepoint (the
   binding owns arg reading; `ReadNeoReference` can't convert without the target type).
2. **The stale-autogen-binding surface is recurring (child-28 class).** The generator is correct; committed
   bindings predate generator fixes; regen is GUI-bound. Any "autogen Neo binding casts an IL object wrongly"
   should first check whether the generator already emits the fix and hand-port the committed file to match.
3. **Wave-2 verification discipline WORKS:** the full-smoke failure count (189→153) is the truth metric; a child
   that doesn't move it is not done. This child's −36 is the proof the grounding-driven approach pays off.
