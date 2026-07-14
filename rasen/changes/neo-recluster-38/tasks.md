# Tasks -- neo-recluster-38

- [x] Phase 1: FRESH full smoke (no filter) on HEAD 50c98bed -> 38 failed. Raw log
      `.tmp-r38-ground.log`. Re-clustered by exception + top Neo.cs frame -> 8 clusters
      (table in `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-38.md`).
- [x] Phase 1: identify largest single-root sub-cluster = cluster C
      (`NeoMarshalByrefFieldToSlot` @ Neo.cs:519, 3 tests). Confirm Legacy PASS for
      InheritanceTest2* (5/0), GenericsRefOut (3/0), DelegateExtTest (4/0).
- [x] Phase 2: implement reference-field branch in `NeoMarshalByrefFieldToSlot`
      (ILIntepreter.Neo.cs, ILTypeInstance arm, after F-10 check). Mirrors the CLR-
      object reference-field branch. Discriminator: `elemType` reference type.
- [x] Phase 2: implement F-10 flag stamp in `ldsflda` for CLR-struct static fields of
      IL types (mirrors JIT `IsClrStructFieldOfIL`).
- [x] Verify name-filter: InheritanceTest2* 5/0 (was 2 fail), GenericsRefOut 3 ran/1
      fail (GenericsRefOut2 distinct root; was 2 fail).
- [x] Verify NeoStep smoke: 398/0 (no regression).
- [x] Verify FULL smoke: 38 -> 35 (delta -3; diff shows EXACTLY InheritanceTest21,
      InheritanceTest22, RefOutTest.UnitTest_GenericsRefOut fixed; ZERO new failures).
- [x] Legacy-neutral: both edits in ILIntepreter.Neo.cs (file-gated
      `#if ENABLE_NEO_MODE`).

## Files
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- 2 edits:
  (1) `NeoMarshalByrefFieldToSlot` reference-field branch (ILTypeInstance arm);
  (2) `ldsflda` F-10 flag for CLR-struct static fields.

## Deferred (documented in proposal.md / design.md D3)
- Delegate-extension-method arg-marshal cluster (DelegateExtTest01/02, DelegateTest01)
  -- delicate delegate Invoke arg-layout change; deferred to delegate-Step-19 work.
- Cluster A (13 throw-assertions) -- grab-bag, not single-fix.
- GenericsRefOut2 (constrained-callvirt JIT shape); StructTest6 (IL-struct-with-ref-
  fields local write-back).
