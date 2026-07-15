# Tasks: neo-recluster-19

## Phase 1 -- FRESH grounding (DONE)
- [x] Build CLI (Debug_Neo --no-incremental, UseSharedCompilation=false) + TestCases (Debug). 0 errors.
- [x] Run full smoke (no filter): `Ran 937 tests, 19 failded, 20 ignored, 7 todos`.
- [x] Extract + classify all 19 failures (exception / JIT opcode / Neo.cs frame / test site).
- [x] Re-cluster -> write `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-19.md`.

## Phase 2 -- batch the tractable singletons + verify (DONE)
The 19 are deeply fragmented. Two tractable singletons identified + fixed.

### Fix 1 -- CLR-enum return sizing (DelegateTest19) [DONE]
- [x] Root-cause: `JITCompiler.AllocateSlotForType` `else` branch sized a CLR enum as
      a boxed reference (RefCount=1) while the JIT emits it as a flat int ->
      Ret handler vt-with-ref-fields branch OOB.
- [x] Fix: add `else if (t.IsValueType && !(t is ILType) && t.TypeForCLR.IsEnum)`
      branch (RefCount=0, underlying-primitive size) before the boxed-`else`.
- [x] Verify: DelegateTest19 PASS in isolation; NeoStep 404/0 (EnumTest green);
      full smoke 19 -> 17.
- [x] Stash-toggle: probe FAULTS with branch disabled (1 failed) -> PASSES restored.
- [x] NeoStep probe `NeoStepRecluster19_TC1_ClrEnumReturn` (force non-inline via
      try/catch) -- guards the fix in the fast smoke.

### Fix 2 -- ExpectException double-wrap (Test05.TestForEach) [DONE]
- [x] Root-cause: `ILIntepreter.Neo.cs:7598` bottom-of-method re-throw ALWAYS
      re-wrapped in a fresh ILRuntimeException, double-wrapping one already wrapped
      (foreach-finally re-throw path). Host ExpectException check mismatched.
- [x] Fix: `pendingThrow = ex is ILRuntimeException ? ex : new ILRuntimeException(...)`
      (mirror ILIntepreter.cs:4889 finally-branch guard).
- [x] Verify: TestForEach + TestForEachTry PASS in isolation; NeoStep 404/0
      (NeoStep14 EH + finally/catch unregressed); full smoke 19 -> 17.
- [x] (No NeoStep probe -- the defect only manifests at the host boundary; the
      TestForEach full-smoke test is the durable guard.)

## Verify (truth = full-smoke number) [DONE]
- [x] FULL SMOKE: **19 -> 17** (`Ran 937 tests, 17 failded`; strict subset, no new failures).
- [x] NeoStep: **404/0** (403 baseline + TC1 probe; 0 regression).
- [x] Legacy-neutral: plain `Debug` CLI build = 0 errors (both fixes Neo-gated/file-gated).

## Remaining (reported, NOT in scope -- distinct deep roots)
17 survivors, each a distinct root, prioritized in `fullsmoke-ground-19.md`:
ILRuntimeType-vs-framework reflection (ReflectionTest10), constrained dispatch
(GenericMethodTest11), enumerator marshal (MyTest.Test), inlined-call reference
aliasing (UnitTest_TestInline01), generic-instance field layout (RegisterVMTest04),
struct-from-collection ldfld (TestStructDictionary), IL-struct boxing (StructTest11),
+ the throw@7115 grab-bag (UnitTest_TestFCP / TestStackRegisterTransition3 /
ReflectionTest25 / UnitTest_StaticTest05 / StructTest6 / StructTest12 /
UnitTest_10046 / UnitTest_10051 / DelegateTest42) and null-reflection-array
(ReflectionTest14).
