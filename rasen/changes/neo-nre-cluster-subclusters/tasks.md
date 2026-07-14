# Tasks -- neo-nre-cluster-subclusters (Wave-2 C16)

## Phase 1 -- RE-AUDIT (DONE)
- [x] Build CLI (Debug_Neo) + TestCases (Debug). Baseline full smoke: `Ran 922, 95 failed`.
- [x] Extract the 44 NRE failures at 95 (Python correlator: Invoking-test -> Rethrown-NRE).
- [x] Sub-cluster by Neo.cs stack frame (table in design.md). Largest = enumerator (10).
- [x] Pin root: stale autogen `MoveNext_1_Neo`/`GetEnumerator_*_Neo`/`get_Current_0_Neo`
      `default(...)` TODO stubs (same defect class as child-28). JIT-dump + stack confirmed.

## Phase 2 -- implement (DONE; all Neo-gated, Legacy-neutral)
- [x] Hand-port enumerator `MoveNext_1_Neo` + `get_Current_0_Neo` (read VT this @0 +
      write-back + VT return) across 8 enumerator binding files.
- [x] Hand-port container `GetEnumerator_*_Neo` (write VT return) across the matching
      Dictionary/List main binding files (incl. ValueCollection.GetEnumerator).
- [x] Hand-port `KeyValuePair<,>` `get_Key_*_Neo` / `get_Value_*_Neo` (read VT this +
      write-back; return writes already generator-emitted) across 7 KeyValuePair files.
- [x] No engine / JIT / optimizer / object-model change.

## Verify (DONE; truth = full-smoke number)
- [x] Name-filter canary GCTest.TestDicEnumerator: PASS (1/0) after the first file pair.
- [x] **Full smoke: 95 -> 89 (delta -6, 0 regressions).** Flipped: TestDicEnumerator,
      GenericMethodTest.GenericTest, InheritanceTest24, Test05.TestForEachTry, Test05.TestReturn,
      UnitTest_10034.
- [x] Stash-toggle airtight: revert 2 canary files -> TestDicEnumerator FAILS (NRE) ->
      restore -> PASS.
- [x] NeoStep 388/0 (no regression).
- [x] Legacy-neutral: plain Debug + useRegister=true + NeoStep = 388/18 (documented
      pre-existing Legacy NeoStep set; edits are `#if ENABLE_NEO_MODE`).

## Reported (NOT fixed -- secondary roots, different sub-clusters)
- TestStructDictionary: IL-struct-in-CLR-Dictionary marshalling (NRE in ExecuteNeo).
- JsonTest9: IL-type GetType().Name reflection (sibling of child-18).
- MyTest.Test: non-generic/interface GetEnumerator InvalidCastException.
- TestForEach: reaches its designed NotSupportedException (harness ExpectException question).
- The other ~28 NRE tests in the bucket (ExecuteNeo:5007/5417, ResolveNeoCallvirtILTarget:1366,
  NeoMarshalByrefFieldToSlot:519, the ExecuteNeo singleton lines, the callvirt-this-null C4
  residual).
