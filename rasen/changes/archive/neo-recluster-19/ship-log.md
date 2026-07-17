# Ship Log: neo-recluster-19

## Result
- **Full smoke 19 -> 17** (DelegateTest19 + Test05.TestForEach flipped; 17 survivors
  are a strict subset, no new failures). Verified by re-running the full smoke.
- **NeoStep 404/0** (403 baseline + 1 new probe; 0 regression).
- **Legacy-neutral**: plain `Debug` build = 0 errors.

## Files (NOT committed -- LEAD commits)
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- `AllocateSlotForType`
  CLR-enum branch (Neo-gated `#if ENABLE_NEO_MODE`).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- Ret-unhandled
  re-throw no-double-wrap (file-gated).
- `TestCases/NeoStepRecluster19Test.cs` -- new (TC1 CLR-enum-return regression probe).
- `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-19.md` -- FRESH 19 cluster table.
- `rasen/changes/neo-recluster-19/{proposal,design,tasks,ship-log}.md`.

## Evidence
- Fresh full smoke (pre-fix): `Ran 937 tests, 19 failded, 20 ignored, 7 todos`.
- DelegateTest19 isolation: 1 fail (HEAD) -> 1/0 PASS (post-fix).
- TestForEach isolation: 1 fail (HEAD) -> 2/0 PASS (TestForEach + TestForEachTry).
- NeoStep: 403/0 (pre-probe) -> 404/0 (with probe); NeoStep14 EH + EnumTest green.
- Stash-toggle (AllocateSlotForType branch disabled): probe 1/1 FAULT ->
  restored: 1/0 PASS (airtight regression guard).
- Full smoke (post-fix): `Ran 937 tests, 17 failded, 20 ignored, 7 todos`.
- Legacy plain-Debug build: 0 errors.

## Commit trailer (when LEAD commits)
```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```
