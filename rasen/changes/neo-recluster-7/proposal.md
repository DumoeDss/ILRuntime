# neo-recluster-7 -- Wave-2 child of neo-overhaul

## Why
Fresh-ground the CURRENT full-Neo smoke (7 failed) and batch-fix the most tractable
singleton. Success = the full-smoke count dropping (7 -> lower), verified by a
re-run of the full smoke (no "looks fixed").

## Fresh ground (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch)
- PRE-fix:  `Ran 948 tests, 7 failded`.
- POST-fix: `Ran 948 tests, 6 failded`.

The 7 (pre-fix): UnitTest_TestStackRegisterTransition3, RegisterVMTest04,
UnitTest_StaticTest05, StructTest6, StructTest12, MyTest.Test, UnitTest_10051.
See `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-07.md` for the full
cluster table + per-test pinned roots.

## What changed (batch = the most tractable singleton: UnitTest_StaticTest05)
A 3-part Neo-gated fix for the `ldsflda <IL-static struct field>; ldfld <sub-field>`
+ `ref <IL-static struct field>` interaction (pure-primitive struct case). Pinned
root + fix + verify in `fullsmoke-ground-07.md`. Files:
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`

## Impact
- FULL SMOKE: 7 -> 6 (UnitTest_StaticTest05 PASSES; the other 6 unchanged).
- NeoStep 414/0 (no regression).
- Legacy-neutral (all changes under `#if ENABLE_NEO_MODE`).
