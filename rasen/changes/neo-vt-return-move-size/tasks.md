# neo-vt-return-move-size -- tasks

## Phase 1 -- re-audit (VERIFY at 12)
- [x] Build CLI (Debug_Neo --no-incremental) + TestCases (Debug), 0 errors.
- [x] Confirm UnitTest_TestFCP fails at 12 (color = (1,0,0), expected (1,1,0)).
- [x] Dump JIT for ToColor; examine `move r13, r12` + `ret r13`.
- [x] Pin root: NOT the Move size (Move copies 12 bytes correctly). The RETURN
      slot is sized 4/1 (boxed ref) by AllocateSlotForType -> Ret copies only
      4 bytes.

## Phase 2 -- implement + verify
- [x] Implement fix: AllocateSlotForType CLR-value-type branch (flat bytes,
      RefCount=0), mirroring the F-MAJ-1 local declaration. Neo-gated. Single
      file JITCompiler.cs (+27/-1).
- [x] Name-filter UnitTest_TestFCP: PASS (stash-toggle airtight).
- [x] FULL SMOKE: 12 -> 10 (strict subset; UnitTest_TestFCP + UnitTest_10046
      flipped; no new failures).
- [x] NeoStep 410/0 (no regression).
- [x] Legacy-neutral: plain Debug Legacy NeoStep = 19 both with and without
      the fix; AllocateSlotForType is Neo-gated (depth 1).

## DONE
