# Tasks: neo-callvirt-gettype-vtable

- [x] Phase 1 -- RE-AUDIT (verified against real runs)
  - [x] Build Neo CLI (Debug_Neo) + TestCases (Debug) -- 0 errors.
  - [x] Confirm 4a + 4b fail on Neo (name-filter): StaticTest01 `VTable slot for GetType`; SimpleTest.StaticTest `this is null`.
  - [x] Confirm PASS on Legacy (plain Debug+useRegister=true): StaticTest 10/0.
  - [x] Pin 4a root: GetType non-virtual -> not in Neo VTable -> ResolveNeoCallvirtILTarget throws; ObjectGetType not on RedirectMapNeo.
  - [x] Pin 4b root: stale Step-7 .cctor suppression under ENABLE_NEO_MODE (C4DBG diagnostic: getter Invoke never reached; InitializeMethods sets staticConstructorCalled=true without invoking).

- [x] Phase 2 -- implement (Neo-gated / Legacy-neutral)
  - [x] 4a routing: `ResolveNeoCallvirtILTarget` CLRMethod fallback (ILIntepreter.Neo.cs:1210-1233).
  - [x] 4a redirect: `ObjectGetTypeNeo` (CLRRedirections.cs:764) + register on RedirectMapNeo (AppDomain.cs:288).
  - [x] 4b .cctor restore site 1: StaticInstance getter Invoke (ILType.cs:214-230).
  - [x] 4b .cctor defer site 2: InitializeMethods eager block `#if !ENABLE_NEO_MODE` (ILType.cs:2424-2444).

- [x] Verify (truth = full-smoke number)
  - [x] Name-filter: StaticTest01 PASS, EnumTest15 PASS, ReflectionTest03 PASS, TestMethodParametersInfo PASS, SimpleTest.StaticTest PASS. VTable-slot-for-GetType message GONE everywhere.
  - [x] FULL SMOKE delta: **153 -> 140** (13 flipped, 0 regressions vs 189-grounding). C4 flips: DelegateTest38/39/40, EnumTest15, ReflectionTest03, TestMethodParametersInfo, StaticTest01 (4a, 7) + SimpleTest.StaticTest, GenericMethodTest18, MyTest.UnitTest_Test1, UnitTest_10030 (4b, 4) = 11 C4 + 2 incidental (C16 NRE whose `this` was a static field).
  - [x] C4 progressed (4a bug fixed, downstream = other clusters): DelegateTest36 (`Derived classes must provide an implementation`), DelegateTest37/ReflectionTest10 (`runtime Type` C12), ReflectionTest11 (`Add_I8`), EnumTest30/32 (enum Equals), InheritanceTest19 (InvalidCast).
  - [x] NeoStep smoke (Neo build): **380/0** (no regression).
  - [x] Stash-toggle (4 engine files): WITHOUT fix StaticTest01 FAILS (`VTable slot for GetType`); WITH fix StaticTest01 + SimpleTest.StaticTest PASS (1/0 each).
  - [x] Legacy-neutral: plain Debug+useRegister=true StaticTest 10/0, ReflectionTest 31/0, Legacy NeoStep 380/18 (documented baseline).

- [x] Artifacts: proposal.md, design.md, tasks.md (this file).

## Follow-ups (distinct roots, out of scope)
- neo-typename-gettype-string: `Type.GetType(string)` returns null under Neo (ReflectionTest04/19).
- neo-byref-out-writeback: StaticTest03 line 69 `ls.Add` after `dict.TryGetValue(1, out ls)`.
- neo-il-enum-Equals-vtable: IL enums have empty VTables (value-type skip); 4a fallback routes Equals to CLR Object.Equals (reference equality). Needs enum-aware Equals redirect.
- neo-add-i8-opcode: `Not supported opcode Add_I8` (ReflectionTest11).
- neo-dispose-interface-callvirt: InheritanceTest19 Dispose explicit-interface dispatch.
