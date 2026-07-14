# Tasks -- neo-nre-residual-sites

## Phase 1 -- RE-AUDIT (VERIFY the residual at 89)
- [x] Build CLI (Debug_Neo) + TestCases (Debug) -- 0 errors each.
- [x] Run the full Neo smoke; confirm baseline `Ran 922 tests, 89 failed`.
- [x] Extract the 35 distinct NRE tests; sub-cluster by top ILIntepreter frame.
- [x] Confirm the two largest sub-clusters (tied at 4): ResolveNeoCallvirtCLRTarget:1432
      (C4 residual) and ExecuteNeo:5007 (Ldsfeld IL-static VT).
- [x] Pick ExecuteNeo:5007/4852 (proven same root, spans 2 sub-clusters = 6 tests).

## Phase 2 -- pin root cause + implement + verify
- [x] Reproduce `Vector3.get_One` in isolation (name-filter: 1 test, 1 fail).
- [x] Instrumented the Stsfeld IL-static-VT site (ExecuteNeo:4852) with a diagnostic
      throw -> printed `pSize=-1 mCnt=0 sftLen=2` -> pinned root cause.
- [x] Pin root cause: self-referential static VT field (`struct Vector3 { static
      Vector3 one; }`) reads `sit.TotalPrimitiveSize` == -1 sentinel during
      InitializeFields (instance totals not finalized until after the loop) ->
      corrupts `staticPrimitiveOffset` -> `StaticTotalPrimitiveSize == -1` ->
      `ILTypeStaticInstance.Primitives == null` -> NRE in the .cctor Stsfld (and any
      Ldsfeld of an IL-VT static field).
- [x] Implement fix in `ILRuntime/CLR/TypeSystem/ILType.cs` `InitializeFields()`:
      move the Neo static-field offset computation to a post-loop pass (after instance
      totals finalized). Neo-gated; Legacy byte-identical. (+45/-43, 1 file.)
- [x] Revert both diagnostic edits (Neo.cs back to HEAD).

## Verify (truth = full-smoke number)
- [x] Name-filter: largest-sub-cluster tests PASS after fix (get_One + UnitTest_10025
      flip green; StaticTest05 + UnitTest_10023 progress to secondary roots).
- [x] FULL SMOKE: `89 -> 86` (delta -3, 0 regressions). Flipped: StructTest4,
      UnitTest_10025, Vector3.get_One. Newly failing: none.
- [x] Stash-toggle (airtight): ILType.cs revert-to-HEAD -> get_One FAILS (1/1);
      restore -> get_One PASSES (1/0).
- [x] NeoStep: 388/0 (no regression).
- [x] Legacy-neutral: plain Debug build 0 errors; Legacy NeoStep 388 ran / 18 failed
      (documented pre-existing baseline).

## Reported (NOT fixed -- secondary roots + other sub-clusters)
- [ ] ExecuteNeo:5007 progress-to-secondary: UnitTest_StaticTest05 (test assertion
      throw), UnitTest_10023 (NRE at 4720), UnitTest_10022.
- [ ] ResolveNeoCallvirtCLRTarget:1432 x4 (C4 residual this==null).
- [ ] ResolveNeoCallvirtILTarget:1366 x3; NeoMarshalByrefFieldToSlot:519 x2;
      ExecuteNeo:4672/4720/5182/5417/5419 x2 each; ~14 singletons.
