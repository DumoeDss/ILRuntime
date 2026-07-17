# Tasks: neo-lowerneoffsets-push-missing

- [x] 1. Build CLI (Debug_Neo) + TestCases (Debug). (DONE, 0 errors.)
- [x] 2. Reproduce on Neo: DelegateTest24/StructTest7/UnitTest_10027 -> confirm
      "could not find expected Push" from LowerNeoOffsets. (DONE.)
- [x] 3. Add diagnostic to LowerNeoOffsets pre-throw; dump failing call's
      resolved method + pCnt/pushCnt/foundPushes/Operand4. (DONE.) Result: all 3
      are `Call_Redirect` on `TestVector3::.ctor(Single,Single,Single)`,
      PC=3, HasThis=True, pCnt=4, pushCnt=1, foundPushes=0, op4=0x30002.
- [x] 4. Pin root cause: Newobj-originated Call_Redirect (Operand4 bit 0x2)
      wrongly takes the HasThis bump + wrong param-layout in LowerNeoOffsets.
      (DONE -- JIT dump + runtime-arm contract.)
- [x] 5. Implement fix: `isNeoNewobjShape` discriminator substituted at all 10
      param-layout sites in the LowerNeoOffsets Call/Newobj case.
      `Optimizer.Neo.cs` only. (DONE.)
- [x] 6. Revert diagnostic to a clean informative throw. (DONE.)
- [x] 7. Name-filter verify: 0 "Push" exceptions; UnitTest_10027 PASS;
      StructTest7 -> F-10 NIE (deferred sibling); DelegateTest24 -> res!=6
      (child-28 struct-newobj follow-up). (DONE.)
- [x] 8. NeoStep smoke (regression check; LowerNeoOffsets is core): 382/0. (DONE.)
- [x] 9. Full Neo smoke delta: 103 -> 101 (0 Push exceptions). (DONE.)
- [x] 10. Legacy-neutral: `Optimizer.Neo.cs` is `#if ENABLE_NEO_MODE`-gated. (YES.)

## Verification evidence
- Pre-fix: 3 tests throw `Neo lowering could not find expected Push instructions
  for Call/Newobj.` during JIT (`LowerNeoOffsets:1268`).
- Post-fix: 0 such exceptions in the full smoke. `UnitTest_10027` PASS.
- NeoStep: 382 ran / 0 failed (was 382/0 -- no regression).
- Full smoke: 916 ran / 101 failed (was 103 failed) -- delta -2.
- Files: `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` (1 file,
  LowerNeoOffsets Call/Newobj case only).
