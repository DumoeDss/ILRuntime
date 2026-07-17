# Proposal: neo-callvirt-gettype-vtable

Wave-2 child C4 of the `neo-overhaul` portfolio. Branch `features/object-model-overhaul`.

## Problem
Full Neo smoke (grounding 2026-07-13) had a ~20-test cluster (C4) of two sub-classes:
- **4a** `MissingMethodException: Neo callvirt cannot resolve VTable slot for System.Type GetType()` (and `Equals`, `Dispose`) -- an IL instance calls an inherited CLR method; the Neo VTable has no slot for it.
- **4b** `NullReferenceException: Neo callvirt this is null` -- a callvirt's `this` resolves to null.

## Root causes (re-audited, pinned with Neo-vs-Legacy evidence)
- **4a**: `Object.GetType()` is NON-virtual, so `IsNeoVTableCandidate` (ILType.cs:703) rejects it and it is never in the Neo VTable. The JIT emits a generic `Callvirt` for it (`MayCallvirtTargetILObject` returns true for `typeof(object)`, JITCompiler.cs:3549). `ResolveNeoGenericCallvirtTarget` -> `ResolveNeoCallvirtILTarget` -> `TryGetNeoVTableSlot(GetType)` fails -> THROWS (ILIntepreter.Neo.cs:1213). Legacy instead consults the `ObjectGetType` redirect (CLRRedirections.cs:1250, registered on Legacy `RedirectMap` only). Two gaps: (1) Neo throws instead of falling back to CLR dispatch; (2) `ObjectGetType` is NOT registered on `RedirectMapNeo`, so even with the routing fix `GetType()` would return `typeof(ILTypeInstance)` (wrong).
- **4b (static-field sub-class)**: `ILType.StaticInstance` getter and `InitializeMethods` had a STALE Step-7 suppression of the `.cctor` invocation under `ENABLE_NEO_MODE` ("restore once Step 7 lands"). Step 7 landed long ago (we are past Step 25). So every IL static field with an inline initializer (`= new Dictionary(...)`) stayed at its default (null); a callvirt on it surfaced as "this is null". The remaining 4b tests (ReflectionTest04/19 `Type.GetType(string)` returning null; StaticTest03 `out`-param; etc.) are DISTINCT downstream roots, reported as follow-ups.

## Fix (Neo-gated / Legacy-neutral)
- **4a**: (1) `ResolveNeoCallvirtILTarget` -- when `TryGetNeoVTableSlot` fails AND `declaredMethod is CLRMethod`, return the CLRMethod (fall back to CLR dispatch) instead of throwing (virtual Object methods ToString/Equals/GetHashCode stay on the VTable path). (2) Add `ObjectGetTypeNeo` redirect (mirrors Legacy `ObjectGetType`) + register on `RedirectMapNeo`.
- **4b**: restore the `.cctor` invocation. The eager site in `InitializeMethods` is skipped under Neo (it re-enters init before `StaticInstance` storage is ready -- NRE in the Stsfld VT arm); the LAZY `StaticInstance` getter site is restored as the Neo trigger (fires after init completes).

## Verify (truth = full-smoke number)
- Name-filter: 4a tests flip (StaticTest01, EnumTest15, ReflectionTest03, TestMethodParametersInfo PASS; VTable-slot message gone everywhere). SimpleTest.StaticTest (4b) PASS.
- FULL SMOKE: delta `153 -> N` (recorded in tasks.md).
- NeoStep 380/0 (no regression).
- Legacy-neutral: re-audited tests still PASS under plain Debug + useRegister=true.

## Out of scope (follow-ups)
- 4b non-static-field roots: `Type.GetType(string)` returning null (ReflectionTest04/19), byref `out`-param write-back (StaticTest03 line 69), GenericMethodTest18 / MyTest.UnitTest_Test1 / UnitTest_10030 (need per-test triage).
- `Equals`/`ToString` Neo redirects for IL-enum receivers (EnumTest30/32 -- IL enums have empty VTables; value-type VTable gap).
