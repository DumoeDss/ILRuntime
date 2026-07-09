# Tasks — neo-ret-vt-with-ref-fields

- [x] Read lead-6 handoff + planning-context + skill; confirm baseline NeoStep 241/0/0.
- [x] Locate the Ret arm in `ILIntepreter.Neo.cs` (~2923-2953); confirm the NIE at ~2940.
- [x] Find the return value's callee-side ref offset: confirmed it is NOT on `CompiledFrame` (AllocateLocalStackSpaces records only ReturnPrimitiveSize/ReturnRefCount via local dummy cursors); the return register's RefOffset lives in `localInfos[retReg].RefOffset` and is NOT stamped on the Ret opcode by `LowerNeoOffsets` (only `LowerR1` runs). Decision D1: stamp it into Ret's spare `Operand3`.
- [x] Write design.md (root cause, returnSlotRefOffset finding, fix, scope boundary, test design, verification, decisions, follow-ups).
- [x] JIT fix: `Optimizer.Neo.cs` `case Ret:` -- stamp `op.Operand3 = localInfos[r1].RefOffset` alongside `LowerR1`.
- [x] Runtime fix: `ILIntepreter.Neo.cs` Ret `else` branch -- add VT-with-ref-fields copy (byte CopyBlock + ref-slot loop mirroring Move_Vt); keep single-ref-return + pure-primitive paths unchanged.
- [x] Add adversarial probes `TestCases/NeoStepRetVtTest.cs` (class `NeoStepRetVtTest`): OneRef, ManyRefs, ReadAfterReturn, ShallowCopyIndep, NestedVtWithRef (scoped -- inner-field read hits pre-existing Ldfld_Value NIE; asserts r.top only), PurePrimitiveControl.
- [x] Build CLI (Debug_Neo) + TestCases (Debug) -- 0 errors.
- [x] NeoStep smoke green with new probes: 247/0/0 (241 baseline + 6 new).
- [x] FAIL-on-HEAD -> PASS-after stash-toggle: 5 of 6 probes FAIL on HEAD with the exact NIE (the 6th = PurePrimitiveControl passes on HEAD as expected); all 6 PASS after the fix.
- [x] Legacy-neutral: plain `Debug` builds 0 errors (Neo files compile out); Legacy NeoStep 247 ran / 10 pre-existing failures (NeoStep13/14/15/16/20/6 -- all documented, NOT introduced); all 6 new RetVt probes PASS on Legacy too.
- [x] No regression in broader NeoStep smoke (241 prior all still green).
- [x] Record durable findings + follow-up (async get_Task value-type return for child 4; the ReferenceEquals / Ldfld_Value test-harness edges).
