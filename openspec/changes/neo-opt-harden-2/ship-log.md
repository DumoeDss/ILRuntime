# Ship Log -- neo-opt-harden-2

**Date:** 2026-07-05
**Change:** neo-opt-harden-2 (F-MAJ-1 fix)
**Outcome:** F-MAJ-1 FIXED -- dump-confirmed root cause, Option B applied.

## Root cause (dump-confirmed)

Candidate (1) CONFIRMED; candidate (2) REFUTED.

A focused `frame.LocalInfos` dump for `NeoOptHardTest_Fmaj1_TwoClrStructLocals`
on HEAD (pre-fix) showed:

- `slot[0]` (CLR struct local `v`): `Offset=0, Size=4, RefCount=1, isRef=True`
  -- a 4-byte boxed-ref slot.
- `slot[1]` (CLR struct local `w`): `Offset=4, Size=4, RefCount=1, isRef=True`
  -- a 4-byte boxed-ref slot.
- The two locals received DISTINCT, non-overlapping regions -> candidate (2)
  (`CleanupRegister` compaction) is REFUTED.
- The D6 return-write wrote `retSz=12` flat bytes into each 4-byte slot -> an
  8-byte overflow into the neighbouring local. Candidate (1) is CONCLUSIVELY
  CONFIRMED.

The prior review's "AllocateLocalStackSpaces slot-reuse / liveness" hypothesis
remains DISPROVEN: the method has NO reuse logic (cursors only advance). The
spec records this so a future liveness-allocator is NOT mis-attributed.

## Chosen fix: Option B (declare-side flat-bytes), gated `#if ENABLE_NEO_MODE`

The D2/Move_Vt consistency probe INVERTED the propose-time ranking (Option A
was "PREFERRED"). The D2 by-value-param read (`CLRMethod.Invoke`) byte-copies
N flat bytes from the caller local's frame `Offset` (the optimizer's
`CopyNeoCallArguments` uses `primSize = dstInfo.Size`). So D2 ALREADY treats
the local as flat bytes at runtime. The D6 return-write ALREADY writes flat
bytes. Both ends are flat-bytes; only the SLOT DECLARATION lied (boxed-ref).

Option A (boxed-ref write into the 4-byte slot) would break D2: the
caller-local -> callee-param byte-copy would copy the 4-byte mStack index + 4
bytes of garbage. Making Option A work would require changing the shared Call
lowering to dereference a boxed ref during param-copy -- a much larger change.

Option B is the minimal representation-consistency fix: declare a CLR value-
type LOCAL as flat bytes (`Size = GetNeoValueTypeManagedSize`, `RefCount = 0`,
`localIsRef = false`), mirroring the callee param layout
(`AllocateNeoCallParamSlot`).

## Edit sites

- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` --
  `AllocateLocalStackSpaces` CLR-VT-local branch: split into a Neo
  `#if ENABLE_NEO_MODE` branch (flat bytes) and a Legacy `#else` branch
  (boxed-ref `Size=4, RefCount=1`). ONE branch changed.
- `ILRuntimeTestBase/TestFramework/TestClass3.cs` -- F-MAJ-1 host helpers
  (`MakeTestStruct4`/`SumTestStruct4`, `MakeTestStruct8`/`SumTestStruct8`,
  `MakeIntA`/`MakeIntB`, `TouchFrame`) + 2 boundary structs (`TestStruct4`,
  `TestStruct8`).
- `TestCases/NeoOptHardeningTest.cs` -- 9 `NeoOptHardTest_Fmaj1_*` probes
  (separate filter, NOT `NeoStep`).
- `TestCases/NeoStep13bTest.cs` -- promoted the exact reproducer as
  `NeoStep13bTwoClrStructLocalsRegression`.

Post-fix dump: `slot[0]` and `slot[1]` are now `Size=12, RefCount=0, isRef=False`
(distinct `[0,12)` / `[12,24)` regions); D6 `retSz=12` fits exactly.

## Legacy-neutrality proof (stash-toggle)

The fix is in a SHARED method (`AllocateLocalStackSpaces`), gated
`#if ENABLE_NEO_MODE`. Plain-`Debug` CLI + `useRegister=true` NeoStep-filter
smoke, WITH and WITHOUT the fix (stash-toggle):

- WITH fix:    100 ran, 7 failed (NeoTestClrStructNoBindingBoxRoundTrip,
  NeoTestClrStructWithBinderBoxRoundTrip, NeoStep14 TC1/TC5/TC8,
  NeoStep15_TC6, NeoNaNR8).
- WITHOUT fix: 100 ran, 7 failed (SAME 7 tests, byte-identical messages).

The failure set is IDENTICAL -> the `#if` gate works; Legacy is unaffected.

## Test results

- F-MAJ-1 probes on HEAD (pre-fix): 7/9 FAIL (reproducer, IsolationR1/R2,
  ThreeStructLocals, LiveRangeOverlapAcrossCall, ScopedReuseNoFrameBloat,
  StructSize8); 2/9 PASS controls (StructSize4, TwoClrIntReturns). Matches the
  candidate-(1) signature (overflow only past size 4).
- After fix: 9/9 F-MAJ-1 PASS; 3/3 K1 still PASS (12/12 under `NeoOptHardTest_`).
- Full `NeoStep` smoke: 100/100 PASS (99 baseline + 1 promoted reproducer).

## Adversarial-probe sweep (Block 3.3)

All 8 F-MAJ-1 probe categories pass after the fix:

1. Exact reproducer (2 CLR struct locals, combined check) -- PASS.
2. Isolation controls (R1 alone, R2 alone) -- PASS (proves not a cross-clobber).
3. 3+ simultaneous CLR struct locals -- PASS.
4. Live-range OVERLAP across a method call (`Make -> TouchFrame -> Make ->
   Sum`) -- PASS (fix not order-dependent).
5. Scoped reuse (`{ v }` then `{ w }`) -- PASS. NOTE: this was designed as a
   "PASS throughout" regression guard but FAILS on HEAD because the C#
   compiler does NOT narrow struct-local register liveness for block scope
   (both remain simultaneously-live method locals); after the fix it passes.
   No frame-size regression: the monotonic allocator never reused slots
   (propose-time finding holds); the frame grows correctly (two 4 -> 12-byte
   slots) but no slots are added.
6. Boundary: StructSize4 (4-byte struct) PASS (no overflow; fix does not
   over-correct); StructSize8 (8-byte struct) PASS.
7. Single CLR struct local regression (Step-13b tests) -- still PASS (100/100
   NeoStep smoke).
8. Two CLR int returns control -- PASS (the primitive path is unaffected).

## Out of scope (noted, NOT fixed)

- `GatherValueTypes` + the temp-register sizer only handle `ILType`, not CLR
  structs. A temp register holding a CLR struct > 8 bytes would be under-sized.
  The F-MAJ-1 reproducer does NOT exercise this (the Make() return dest is the
  LOCAL, not a temp). Separate pre-existing gap.
- A CLR struct local WITH a registered ValueTypeBinder (managedCount > 0) is
  declared `RefCount=0` here (the reflection-fallback D6 return path NIEs
  ref-field structs upstream). The binder path is owned by autogen redirects.
