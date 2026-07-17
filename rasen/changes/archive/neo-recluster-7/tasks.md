# Tasks -- neo-recluster-7

- [x] Build CLI (Debug_Neo --no-incremental) + TestCases (Debug) -- 0 errors each.
- [x] FRESH full smoke (no filter) -> extract the CURRENT 7 failures + classify.
- [x] Re-cluster the 7 -> write `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-07.md`.
- [x] Deep-diagnose the most tractable singleton (UnitTest_StaticTest05) to byte/IL
      root (3 interacting defects: ldsfelda offset, bad inline fold, ldfld base offset).
- [x] Implement the 3-part Neo-gated fix:
   - [x] `ILIntepreter.Neo.cs` Ldsfelda IL-static arm: PrimitiveOffset for IL-struct fields.
   - [x] `JITCompiler.cs` TypeSpecialize: `case Ldsfelda:` clears registerTypes[dest].
   - [x] `ILIntepreter.Neo.cs` `NeoLdfldStaticByrefOff` helper + 10 scalar Ldfld arms.
- [x] Verify UnitTest_StaticTest05 filtered -> 1/0 PASS.
- [x] Verify NeoStep 0-failures (414/0, no regression).
- [x] Verify FULL SMOKE delta 7 -> 6 (FRESH no-filter re-run; truth).
- [x] Confirm Legacy-neutral (all changes under `#if ENABLE_NEO_MODE`).
- [x] Report the remaining 6 honestly (each a distinct deep singleton).
