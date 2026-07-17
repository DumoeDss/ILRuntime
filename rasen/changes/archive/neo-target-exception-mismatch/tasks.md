# tasks -- neo-target-exception-mismatch (Wave-2 C14)

## Phase 1 -- RE-AUDIT (DONE)
- [x] Build CLI (Debug_Neo) + TestCases (Debug).
- [x] Confirm the 4 C14 tests' CURRENT failure modes in the 104-baseline
      (TargetException is GONE -- C2 fixed it; 05 passes, 07=`0!=11`,
      15=generic Exception, 18=InvalidCast).
- [x] Confirm all 4 PASS on Legacy (Neo-specific).
- [x] Pin root cause for 07/15: missing CopyNeoCallThisBack in Callvirt_CLR +
      stale VMethod3_1_Neo autogen stub (no byref write-back).
- [x] C2-overlap verdict: DISTINCT site (C2 = forward projection; C14 = byref
      write-back; same opcode, different mechanism).

## Phase 2 -- IMPLEMENT (DONE)
- [x] Fix 1: ILIntepreter.Neo.cs Callvirt_CLR -- add snapshot + CopyNeoCallThisBack
      after InvokeNeoClrMethod (mirror IL Call path; ~:3864).
- [x] Fix 2: ILRuntimeTest_TestFramework_TestClass2_Binding.cs VMethod3_1_Neo --
      hand-port stale stub to emit the Step-13-Area-4c write-back epilogue (:111).
- [x] Both edits Neo-gated (`#if ENABLE_NEO_MODE`) -> Legacy-neutral by construction.

## Phase 3 -- VERIFY (DONE; truth = full-smoke number)
- [x] Name-filter: InheritanceTest15 PASS after fix.
- [x] FULL SMOKE delta: **104 -> 103** (`.tmp-c14-postfix.log`, Ran 916/103 fail).
- [x] Failure-set diff vs 104-baseline: 1 flipped GREEN (IT15), 0 regressions.
- [x] Stash-toggle: both edits stashed -> IT15 FAILS on HEAD (generic Exception)
      -> pop -> IT15 PASSES (airtight).
- [x] NeoStep smoke: 382/0 (no regression).
- [x] Legacy-neutral: IT15 PASS on Legacy; Legacy NeoStep 382/18 == documented
      pre-existing baseline.

## Surfaced follow-ups (distinct clusters; out of scope, reported honestly)
- [ ] IT07: adaptor invokes IL override via Legacy ExecuteR -> `Addi_R4`
      (NotImplementedException in ExecuteR). Cluster: adaptor -> Legacy executor
      in Neo mode. Pre-existing (masked by the ref failure pre-fix).
- [ ] IT18: reverse-projection -- `unbox.any`/field-access on a CLR adaptor for
      an IL-type target. Neo unbox.any THROWS where Legacy keeps + unwraps.
      Multi-site Neo adapter-unwrap gap (reverse of C2).
