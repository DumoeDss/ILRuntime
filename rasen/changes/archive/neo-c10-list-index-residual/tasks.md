# neo-c10-list-index-residual -- tasks

## Phase 1 -- re-audit (DONE)
- [x] Build CLI (Debug_Neo) + TestCases (Debug).
- [x] Confirm StructTest8 + StructTest11 fail at the 70-baseline with the OOB
      (name-filter runs; full stack traces captured).
- [x] Pin root causes via JIT dump. RESULT: two DISTINCT bugs.
      - StructTest8: `ldfld vec2.x` not inlined because `registerTypes[r1]` (vec2)
        was clobbered to null by a Move chain from an unseeded `initobj`/`ldobj`
        temp -> Ldfld_R4 misreads flat bytes as mStack index.
      - StructTest11: IL-struct call arg to `List<ILTypeInstance>.Add` reference
        param not boxed at the call boundary.

## Phase 2 -- Bug 1 fix (DONE, VERIFIED)
- [x] Add `Initobj`/`Ldobj` producer-seeding case to `TypeSpecializeNeoOpcodes`
      (`JITCompiler.cs`, after `Ldc_R8`). Seeds `registerTypes[op.Register1]` from
      `appdomain.GetType(op.Operand)` (the type-token hash).
- [x] Stash-toggle: the fix is a single additive case-block; reverting it (via
      `git checkout`) reproduces the StructTest8 OOB; re-applying fixes it.
- [x] Name-filter: StructTest8 PASS (Neo). StructTest8 PASS (Legacy).
- [x] FULL SMOKE: 70 -> 69 (StructTest8 flipped; two identical smokes confirm no
      regression/flake).
- [x] NeoStep 394/0 (no regression).

## Phase 2 -- Bug 2 fix (NOT DONE; handed off)
- [x] Root cause + eliminated hypotheses + recommended fix path documented in
      `handoff/worker-1.md`.
- [ ] Implement the per-instruction box detection in `TypeSpecializeNeoOpcodes`
      (HYPOTHESIS D1/D2 in the handoff) -- the `frame.NeoRegisterTypes`-based
      detection (HYPOTHESIS A) is UNSOUND and must NOT be used.
- [ ] The runtime box in `CopyNeoCallArguments` (the version that was reverted) is
      CORRECT and reusable -- re-add it once the JIT-time detection is sound.
- [ ] Verify StructTest11 PASS; full smoke 69 -> lower.

## Notes
- The C11 report's guess ("autogen List binding, C10 territory") was WRONG for both
  tests. StructTest8 is a JIT type-seeding gap; StructTest11 is a missing box at the
  call boundary. Re-audit vindicated (12-for-12 lesson: always re-audit).
- Full smoke in THIS environment runs in ~3-4 minutes (928 tests), not 25.
