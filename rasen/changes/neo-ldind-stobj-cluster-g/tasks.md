# Tasks: neo-ldind-stobj-cluster-g

## Phase 1 -- RE-AUDIT (DONE)
- [x] Build CLI (Debug_Neo --no-incremental, UseSharedCompilation=false) + TestCases (Debug). 0 errors.
- [x] Full smoke baseline: 932 ran, 48 failed, 20 ignored, 7 todos. (Confirmed the 48 ground.)
- [x] Extract the 6 cluster-G tests at 48 + their current Neo.cs frames (line numbers shifted vs stale ground-60).
- [x] Sub-cluster: stobj-ref(3) / ldind.i4-nested-ldflda(2) / ldlen-upstream(1) / ldelema-null-element(1).
- [x] Confirm TestGenrRef + TestClass222 are IL CLASSES (reference types) -> stobj/ldobj of T is a reference store/load, not a value copy.
- [x] Diagnostic NIE at the top of the Stobj arm captured the exact dest byref (objIdx, off, flag, srcVal, mStack[objIdx]) for RefOutNull2 / GenericsRefOut / GenericsRefOut2.

## Phase 2 -- IMPLEMENT (DONE)
- [x] Stobj reference-type branch: `t != null && !t.IsValueType` -> mirror Stind_Ref dispatch (frame-native / F-7B flag / Array / CLR-object / IL-instance-ref-field). Value-type body under `else if`. (ILIntepreter.Neo.cs Stobj arm)
- [x] Ldobj reference-type branch: symmetric read, mirror Ldind_Ref, materialize referent into dest ref slot via `ip->Operand3` (stamped for Ldobj by LowerNeoOffsets). Value-type body under `else if`. (ILIntepreter.Neo.cs Ldobj arm)
- [x] Ldelema null-IL-class-element branch: emit `(arrIdx, elementIdx)` array-element byref (same as the CLR-array else branch) so the write-back Array arm SetValues the out-param target. Non-null path unchanged (materialize-for-read). (ILIntepreter.Neo.cs Ldelema arm)

## Phase 3 -- VERIFY (DONE)
- [x] Stash-toggle (diagnostic -> fix): RefOutNull2 FAIL-on-HEAD (NRE in value-copy path) -> PASS-after (stobj-ref branch 2 F-7B stores null then the new instance).
- [x] ArrayReferenceTest FAIL-on-HEAD (ldelema NRE on null arr[1]) -> PASS-after (null-element `(arrIdx, elementIdx)` byref -> TryGetValue write-back SetValues arr[1]).
- [x] NeoStep smoke 398/0 (no regression; stobj/ldobj/ldelema are additive `!IsValueType` / null-element branches).
- [x] Full smoke delta: 48 -> 46 (RefOutNull2 + ArrayReferenceTest deterministic flips; GenericsRefOut/GenericsRefOut2 progress past stobj/ldobj to a separate constrained. gap; 0 new regressions). RegisterVMTest04 is flaky/order-dependent (passes/fails with the SAME code across runs) -- NOT a deterministic effect of this change.
- [x] Legacy-neutral: change is 100% in ILIntepreter.Neo.cs (file-gated `#if ENABLE_NEO_MODE`); Legacy compiles none of it. (Structural; ILIntepreter.Neo.cs is a Neo-only file.)

## Phase 4 -- REPORT remaining (DONE)
- [x] ldind.i4 nested-ldflda (UnitTest_Struct/Struct2, 2 tests): child-27 deferred "inner ldflda-on-byref" sibling. Needs byref-aware ldflda (JIT marker, child-24/29 lineage).
- [x] ldlen / GetFields-returns-null (ReflectionTest14, 1 test): upstream reflection bug (GetFields() returns null). ldlen arm is correct.
- [x] constrained. on a reference-type T (GenericsRefOut/GenericsRefOut2): JIT lowers a non-virtual callvirt on a ref-type T to Call, leaving constrained. orphaned. Step 17 D-CONSTRAINED scope.
- [x] NeoMarshalByrefFieldToSlot NRE on a CLR-struct byref (GenericsRefOut -> DoTest(TestStruct&)). Separate byref-marshal gap.

## Notes
- Artifacts: proposal.md / design.md / tasks.md (this file) in `rasen/changes/neo-ldind-stobj-cluster-g/`.
- No probe TestCases added (the flips are existing TestCases: RefOutNull2, ArrayReferenceTest; the fix is exercised by the full smoke). The stobj/ldobj reference-type fix is verified by the dump + the existing tests.
- Do NOT commit (LEAD commits).
