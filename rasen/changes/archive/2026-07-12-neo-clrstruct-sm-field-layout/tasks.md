# Tasks — neo-clrstruct-sm-field-layout (B1)

**Date:** 2026-07-10  **Outcome:** INVESTIGATED, DISPROVEN. No engine fix ships.

## 1. Reproducer + dump-gate (isolate B1 from the async machinery)

- [x] 1.1 Build CLI (`Debug_Neo`) + TestCases (`Debug`); confirm 0 errors. Run
      the NeoStep smoke; confirm the environment-healthy baseline (273/5; the 5
      = the parked child-4 VT cluster).
- [x] 1.2 Construct the minimal B1 reproducer: a plain IL class
      `{ TestClrStructWithRef builder; int v; }` (the
      `AsyncValueTaskMethodBuilder<T>` shape, NO async machinery). Set builder,
      set `v = 11`, read `v`, assert 11. **RESULT: PASSES on HEAD** — the alleged
      collision does NOT reproduce in isolation. (`TestCases/NeoClrStructFieldTest.cs`,
      `NeoClrStructField_ClrStructWithRefThenPrim_NoClobber` + 3 sibling guards.)
- [x] 1.3 Dump the ILType field-layout for the reproducer: confirm the CLR-
      struct-with-ref field and the sibling int DO share the same
      `PrimitiveOffset` (e.g. both primOff 0) — the cursor collision is real —
      BUT `v` reads back correctly (disjoint Primitives vs ManagedObjects
      storage). This is the benign-collision proof.
- [x] 1.4 Confirm the blast-radius: `Stfld_Ref` / `Ldfld_Ref` (F-10 arms) use
      `Operand3 = ReferenceOffset` → `ManagedObjects[]`; `Stfld_I4` / `Ldfld_I4`
      use `Operand2 = PrimitiveOffset` → `Primitives[]`. Disjoint arrays. A
      CLR-struct-with-ref field's `PrimitiveOffset` is DEAD (only its
      `ReferenceOffset` is used). So the shared offset value cannot corrupt.

## 2. Confirm the disproof on the REAL VT1 SM (not the minimal reproducer)

- [x] 2.1 Pop the child-4 stash (`child4-valuetask-blocked-partial`) to get the
      VT1-6 probes + fixer-1 partial work. Resolve the NeoStep20Test.cs merge
      conflict (both probe blocks kept).
- [x] 2.2 Add temporary diagnostics to `Stfld_I4` / `Ldfld_I4` gated on the VT1
      SM type name + primOff 4/8. Run VT1. **RESULT:** `stfld.i4 primOff=4
      val=11` then `ldfld.i4 primOff=4 val=11` — `v` stores 11 AND loads 11.
      The field-layout is NOT the corruption site. (Diagnostics removed.)
- [x] 2.3 Apply the proposed B1 fix (advance `primitiveOffset` by 4 in the
      branch-3 case of `ILType.cs:InitializeFields`) as a PROBE. Re-run VT1.
      **RESULT:** identical failure (`VTSetResult resultObj=4`,
      `frameBase[8]=4`). The layout change does NOT fix VT1. (Probe reverted;
      `ILType.cs` byte-identical to HEAD.)

## 3. Identify the REAL VT1 root cause (re-route to child-4)

- [x] 3.1 Widen the `VTDBG2 VTSetResult` frame dump to `b4/b8/b12/b16/b20/b24`.
      **RESULT:** `b16=14` — the `int result` (`v+3`=14) is at `frameBase[16]`,
      not `frameBase[8]`. The builder byref-`this` occupies 16 call-frame bytes
      (8-byte F-10 byref + 8-byte struct flat-bytes), but
      `AsyncValueTaskMethodBuilder_T_SetResult_Neo` skips only 8 (`curPrim += 8`)
      → `ReadResultParam` reads `frameBase[8]` (stale = 4) instead of
      `frameBase[16]` (= 14). (Diagnostic reverted to the stash's original
      b4/b8/b12.)
- [x] 3.2 Confirm the root cause is a call-argument-marshalling bug in the async
      redirect (the 8-vs-16-byte builder byref-`this` skip), NOT a field-layout
      bug. Re-route to child-4's async-redirect scope.

## 4. Ship the durable storage-disjointness guards

- [x] 4.1 Add 4 guards to `TestCases/NeoClrStructFieldTest.cs`:
      `NeoClrStructField_ClrStructWithRefThenPrim_NoClobber`,
      `_WriteOrder`, `_PrimThenClrStructWithRef_Regression`,
      `_ClrStructWithRefBetweenTwoPrims`. All PASS on HEAD (the benign
      invariant). Header documents the disproof (NOT FAIL-on-HEAD reproducers).
- [x] 4.2 Update the holder-class comments + probe comments to reflect the
      disproof accurately (the shared `PrimitiveOffset` is benign).

## 5. Verify + handoff

- [x] 5.1 NeoStep smoke: 273/5 (the 5 = VT1/VT2/VT3/VT4/VT6, the parked child-4
      cluster, unchanged from the lead-7 baseline). The 4 new guards pass. No
      regression.
- [x] 5.2 Legacy-neutral by construction: `ILType.cs` byte-identical to HEAD;
      no `ILIntepreter.Neo.cs` / `JITCompiler.cs` change by this change (the
      Neo/DebugService diffs in the working tree are the child-4 stash's
      fixer-1 work, NOT this change). `ENABLE_NEO_MODE` gates unaffected.
- [x] 5.3 `ILType.cs` confirmed clean vs HEAD (`git diff HEAD -- ILType.cs`
      empty).
- [x] 5.4 Write `design.md` (the disproof + the real root cause) + this
      `tasks.md` + `blocked.md` (the PARK record + the re-route).
- [ ] 5.5 *(LEAD's job)* Decide: archive this change (DISPROVEN, guards shipped)
      OR keep it open as the parked B1 record. The real VT1 fix is child-4's.
