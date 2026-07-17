# Tasks: neo-recluster-16

- [x] 1. Build CLI (Debug_Neo --no-incremental) + TestCases (Debug). Fresh.
- [x] 2. Run the full smoke (no filter) -> extract the CURRENT 16 failures +
      classify each. (CONFIRMED 16 == ground-17's documented set.)
- [x] 3. Re-cluster. Write the table to
      `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-16.md`.
- [x] 4. Diagnose the tractable candidates. RegisterVMTest04 pin DISPROVEN
      (layout is correct; real root = >3-arg virtual-IL-call param marshalling,
      deep). GenericMethodTest11 pinned (constrained-callvirt primitive-field
      `this` marshal) -> TRACTABLE.
- [x] 5. Implement the GenericMethodTest11 fix (Constrained arm
      `thisObjIdx >= 0` sub-branch: intercept primitive-field-of-IL-instance,
      read+box from Primitives).
- [x] 6. Verify: GenericMethodTest11 standalone PASS; NeoStep 404/0; full smoke
      16 -> 15 (strict subset).
- [x] 7. Confirm Legacy-neutral (Neo-gated file).
- [x] 8. Write proposal/design/tasks/ship-log + the ground-16 handoff.
