# Ship Log -- neo-recluster-10 (2026-07-16)

## Deliverable
Re-cluster the CURRENT 10 full-Neo-smoke failures from a FRESH no-filter run, then
fix the most tractable singleton. Truth metric = the full-smoke failure count.

## Result: full smoke 10 -> 9 (SUCCESS, verified by re-running the full smoke)

### Pre-fix (FRESH no-filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch)
`Ran 944 tests, 10 failded, 20 ignored, 7 todos` (exit 127, known graceful Dict-NRE).
NeoStep 404/0.

### Post-fix (FRESH no-filter, same DLL + patch)
`Ran 948 tests, 9 failded, 20 ignored, 7 todos` (exit 127; +4 = this child's probes).
NeoStep 414/0 (no regression).

### The flip
UnitTest_TestInline01: FAIL -> PASS (the only test that flipped). The 9 survivors
are a strict subset of the pre-fix 10. No new regressions. UnitTest_1013 (unmasked
by fix #1, then fixed by fixes #2+#3) passes both pre- and post-fix.

## Root pinned (DEEP-diagnosed, isolation-probe-confirmed)
The recluster-34 "by-ref aliasing on a plain Call" framing was DISPROVEN.
`new object()` itself returned null: `AppDomain.IsInvalidMethodReference`
(AppDomain.cs:2156) caches null for `System.Object..ctor()`, and the Neo Newobj
arm silently skipped (`ip++; continue;`) when targetMethod==null. Legacy handles
this explicitly (Register.cs:3529-3536 -> `new object()`). Probes TC1/TC3/TC4
(IsNeoStepRecluster10Probe.cs) prove the call was irrelevant.

## Fix (3 parts, all `#if ENABLE_NEO_MODE`-gated -> Legacy-neutral)
1. `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- Newobj arm,
   targetMethod==null branch: construct `new object()` + write to dest as a
   reference-type newobj result (mirrors Legacy).
2. `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` -- LowerNeoOffsets
   Newobj case, targetMethod==null branch: stamp op.DstOffset/Operand3 from
   localInfos[op.Register1] (was skipped -> DstOffset stayed a register INDEX ->
   clobbered adjacent register; the UnitTest_1013 OOB unmasked by fix #1).
3. `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- extend
   `NeoInitobjByRefOperandMarker` to a `Code.Ldflda` predecessor (Roslyn lowers
   `refField = null/default(T)` to `ldflda; initobj T`; the UnitTest_1013 SetNull
   field-clear unmasked by fix #1).

## Verify evidence
- Full smoke 10 -> 9 (the load-bearing metric; re-run post-fix).
- NeoStep 414/0 (no regression).
- Legacy-neutral: plain Debug build 0 errors; all changes Neo-gated; Legacy NeoStep
  414 ran / 19 failed (pre-existing Legacy-specific set); all 4 probes PASS under
  Legacy.
- Isolation probes: TC1 (new object() alone) FAILS on HEAD, PASSES post-fix; TC3
  (no-nullify control) proves obj was null before any call; TC4 (string arg) passes
  -> bug specific to `new object()`.
- Final Debug_Neo rebuild (after a doc-comment edit): 0 errors; both UnitTest_1013
  and UnitTest_TestInline01 pass.

## Probe added (permanent)
`TestCases/NeoStepRecluster10Probe.cs` -- 4 TCs (TC1 new object() alone; TC2 the
inline-call shape; TC3 no-nullify control; TC4 string arg). TC1 is the load-bearing
regression probe for fix #1.

## Remaining (9, all DEEP singletons -- reported honestly, not addressed)
RegisterVMTest04, MyTest.Test, StructTest6, UnitTest_10051,
UnitTest_TestStackRegisterTransition3, ReflectionTest25, ReflectionTest14,
StructTest12, UnitTest_StaticTest05. Cluster table + per-test traces in
`rasen/changes/neo-overhaul/handoff/fullsmoke-ground-10.md`.

## Delivery mode
NOT committed (LEAD commits). Files left in the working tree:
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs`
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`
- `TestCases/NeoStepRecluster10Probe.cs` (NEW)
- `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-10.md` (NEW)
- `rasen/changes/neo-recluster-10/{proposal,ship-log}.md` (NEW)
