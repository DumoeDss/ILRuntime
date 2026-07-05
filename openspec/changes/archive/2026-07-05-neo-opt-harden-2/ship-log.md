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

## Review-loop round 1

The initial ship-log (above) documented the F-MAJ-1 declare-side fix only. The
adversarial review-loop ran two rounds and surfaced three consumer arms that
still assumed the OLD boxed-ref representation after the declare-side change.
This section records the FINAL delivered state.

### Round-0 review verdict: CHANGES-REQUESTED

The round-0 review (independent reproduction, dump probes, stash-toggle -- NOT
trusting the green smoke, since the implementer self-committed `fda264e8`
before the review ran, a process deviation) found the F-MAJ-1 root-cause fix
itself correct and the smoke numbers (100/100 NeoStep, 12/12 NeoOptHard,
identical 7 Legacy failures with/without fix) independently reproduced. But it
flagged a HIGH blast-radius gap: the declare-side change re-declares EVERY Neo
CLR struct local, and the implementer's blast-radius sweep MISSED three runtime
consumers. Three Major findings:

- **M1 `Initobj`** CLR-struct arm still wrote a stale `RefOffset` boxed default
  + a 4-byte index write into the now-flat-bytes region (`ILIntepreter.Neo.cs`).
- **M2 `Box`** CLR-struct arm still read a 4-byte mStack index from a local now
  holding flat bytes (`ILIntepreter.Neo.cs`).
- **M3 `Isinst`/`Castclass`** of a CLR struct local -- inferred Major (same
  shape as M2, not independently probed by round 0).

### Round-1 consumer-arm fixes (UNCOMMITTED; the LEAD commits)

All three fixes retarget the arm to operate on the flat-bytes representation
(`Size = TotalPrimitiveSize, RefCount = 0, isRef = False`), mirroring the
declare-side Option B layout:

- **M1 `Initobj`** CLR-struct arm -> `Unsafe.InitBlock(frameBase+DstOffset, 0,
  clrVtSize)` zero-init. Was writing a stale `RefOffset` boxed default into
  another local's ref slot + a 4-byte index into the flat region.
- **M2 `Box`** CLR-struct arm -> `ReadNeoValueType(clrBoxType.TypeForCLR,
  frameBase, ref boxOff=SrcOffset, bsz)`. Was reading a 4-byte mStack index
  from flat bytes (garbage index -> wrong/OOB read). The Box opcode source is
  ALWAYS a value-typed operand, so the flat-bytes read is unconditional.
- **`Unbox_Any`** CLR-struct dest -> `WriteNeoValueType(obj,
  frameBase+DstOffset, unbxSz)`. This is the 4TH arm that the round-0 sweep
  MISSED -- the round-1 fixer found it via dump-gating (the M3 probe
  `(TestVector3NoBinding)boxed` lowers to `unbox.any`, and the dump showed the
  Unbox_Any dest write firing). The dest of `unbox.any` is always a value-typed
  local, so unconditional.
- **M3 (isinst/castclass):** deemed UNREACHABLE for a flat-bytes CLR-VT local.
  C# `is`/`as`/`(T)obj` on a CLR struct ALWAYS emits a prior `box` (the operand
  of `isinst`/`castclass` is always an `object`-typed Box-result local holding
  an mStack index, never flat bytes). The fixer dump confirmed `box` fired but
  `isinst`/`castclass` did NOT for the M3 probe. NO dead code added -- the
  genuine boxed-ref path for Isinst/Castclass/Unbox-source-read (a CLR struct
  sourced from Box/heap) is UNAFFECTED and remains correct.

### Round-1 review verdict: APPROVE

The round-1 re-reviewer (a distinct worker from the round-0 reviewer, the
planner, the implementer, and the fixer -- author != verifier across all five
roles) ran a fresh COMPLETENESS sweep (the round-0 top mandate was "the sweep
missed Unbox_Any -- are there MORE?"). The sweep walked every
`*(int*)(frameBase + ip->SrcOffset)`/`DstOffset` site that indexes `mStack[...]`
plus the write-side `mStack[frameRefBase + <RefOffset>]` sites:

- **19 arms total:** 3 fixed (M1/M2/Unbox_Any) + 2 correct-no-fix-needed
  (M3 Isinst/Castclass unreachable) + 14 correct-by-construction + 1
  out-of-scope pre-existing gap (callvirt-on-a-CLR-struct byref-`this`).
- **0 remaining arms of the F-MAJ-1 boxed-ref-vs-flat-bytes defect class are
  broken.** The fixer's "3 reachable arms + M3 unreachable" claim is CONFIRMED.

The implementer self-committing early (`fda264e8` before round-0 review) was a
process deviation, but the two-round review-loop still gated correctness: every
consumer arm is now representation-consistent with the declare-side Option B
layout.

### Final smoke (round-1, independently reproduced)

- NeoStep: **100/100 PASS** (99 baseline + 1 promoted
  `NeoStep13bTwoClrStructLocalsRegression`).
- NeoOptHard: **16/16 PASS** (12 prior = 9 F-MAJ-1 + 3 K1, plus 4 new round-1
  probes: `InitobjClrStruct`, `BoxClrStructLocal`, `IsinstClrStructLocal`
  (exercises `(T)obj` -> `unbox.any`), `MixedFrameNoCrossCorruption`).
- K1 subset: **3/3 PASS**. F-MAJ-1 subset: **13/13 PASS**
  (9 declare-side + 4 round-1 consumer-arm).
- Legacy-neutral: plain `Debug` CLI builds clean; the `#if ENABLE_NEO_MODE`
  gate confirmed via stash-toggle (the declare-side fix + the three consumer-arm
  fixes are all Neo-gated; the round-1 arms live in `ILIntepreter.Neo.cs`, which
  Legacy compiles out).
- Stash-proven PRE-EXISTING: the direct-`new ClrStruct(...)` byref-`this` ctor
  gap (see Deferred below) fails IDENTICALLY on `f673b9c9` (pre-F-MAJ-1,
  pre-round-1) -- NOT a regression.

### Lesson reaffirmed (OPT-HARDEN K1)

The OPT-HARDEN K1 lesson held: **dump-gate the ACTUAL failing opcode**. The
round-0 review HYPOTHESISED the M3 cast was the fourth arm (it inferred M3 as a
Major by code-shape symmetry). The round-1 fixer instead dumped the M3 probe
and found the cast lowers to `unbox.any` (NOT `castclass`), so the real fourth
arm was `Unbox_Any`. Dump-gating beats code-shape inference.

### Deferred (out of scope, recorded for routing)

- **`[NEO-BYREF-THIS]`** -- a DIRECT `new ClrStruct(args)` in interpreted IL
  (CLR struct ctor via a byref `this`, which C# lowers to `initobj + ldloca +
  call ctor`, NOT to `InvokeNeoClrMethod(isNewobj:true)`) hits a pre-existing
  reflection gap at `CLRMethod.Invoke:353` (the ctor `this` is read as a 4-byte
  mStack index, but the byref `this` is an 8-byte Ref Slot from `ldloca`).
  Fails IDENTICALLY on `f673b9c9` -- NOT a regression from F-MAJ-1 or the
  round-1 fix; same defect class as the byref-`this`-via-callvirt-on-a-CLR-
  struct gap. Routed to Step 17 byref-completeness / a `[NEO-BYREF-THIS]`
  follow-up. Recorded in `.trae/documents/neo-deferred-items.md` (F-3) and
  `openspec/changes/neo-completion-portfolio/planning-context.md`.
