# Review Report -- neo-double-combine-quirk (F-8 / [OPT-HARDEN-3])

**Reviewer:** adversarial, NON-AUTHOR
**Date:** 2026-07-06
**Working tree:** UNCOMMITTED (HEAD `fe13c25e`, base branch `features/object-model-overhaul`)
**Verdict: APPROVE**

The load-bearing OpCodeR union-overlap reasoning is independently confirmed. The
`immLarge` gate is correct, scoped, and minimal. The fix is Neo-only and
Legacy-neutral. Smoke (Neo 161/161 + NeoOptHard 24/24) and the stash-toggle proof
(2 of 5 probes FAIL-on-HEAD -> PASS-after) are independently reproduced. One Minor
documentation inconsistency (a stale test-file header comment) and one Trivial nit
(the gate is slightly over-broad on R4) -- neither blocks.

---

## Mandatory probe results

### Probe 1 -- OpCodeR union-overlap reasoning (HIGHEST PRIORITY): CONFIRMED

Independently read `ILRuntime/Runtime/Intepreter/OpCodes/OpCode.cs:35-71`. The
`[StructLayout(LayoutKind.Explicit)]` field offsets are:

| field            | type   | offset | spans   |
|------------------|--------|--------|---------|
| `Operand`        | int    | 8      | 8-11    |
| `OperandFloat`   | float  | 8      | 8-11    |
| `Operand2`       | int    | 12     | 12-15   |
| `OperandLong`    | long   | 12     | 12-19   |
| `OperandDouble`  | double | 12     | 12-19   |
| `Operand3`       | int    | 16     | 16-19   |
| `Operand4`       | int    | 20     | 20-23   |

Confirmed:
- `Operand3` (@16) **overlaps the HIGH 4 bytes** of `OperandLong`/`OperandDouble`
  (@12-19, high half = 16-19). A 4-byte int write at @16 clobbers bits 32-63 of the
  8-byte immediate.
- `Operand2` (@12) overlaps the LOW 4 bytes -- irrelevant here (the fix does not
  touch `Operand2`).
- `Operand` (@8, the I4 immediate) is disjoint from @16 -- the dead `Operand3`
  write is harmless for I4 immediate branches.
- `OperandFloat` (@8-11, the R4 immediate) is disjoint from @16 -- the dead write
  is harmless for R4 too (see Trivial nit below).

Confirmed NO immediate-branch runtime arm reads `Operand3`. Read
`ILIntepreter.Neo.cs:1462-1730` -- every immediate-branch arm reads only:
`DstOffset` (the operand slot value), the width-typed immediate
(`Operand`/`OperandLong`/`OperandFloat`/`OperandDouble`), and `Operand4` (branch
target). Specifically:
- `Ceqi_I8`/`Bnei_Un_I8`/... read `OperandLong` (1462-1637).
- `Ceqi_R4`/`Bnei_Un_R4`/... read `OperandFloat` (1477-1683).
- `Ceqi_R8`/`Bnei_Un_R8`/... read `OperandDouble` (1488-1730).
- I4 `Beqi`/`Bnei_Un`/... read `Operand` (1499-1567).

None references `ip->Operand3`. The "dead write" claim is correct: the
`op.Operand3 = localInfos[r1].RefOffset` write was pure dead store for every
immediate-branch opcode, destructive only where @16 collides with a wide immediate
(I8/R8). The runtime-trace evidence in design.md (`val=1.5, opd=2.121995791E-314`)
is consistent with a small-int RefOffset bit pattern reinterpreted as the high
half of a double.

### Probe 2 -- `immLarge` gate correctness: CONFIRMED

The gate (`Optimizer.Neo.cs:785-802`):
- (a) Discriminator lists exactly the 10 I8 + 10 R4 + 10 R8 immediate-branch
  opcodes (`B*{i,eqi,nei,lti,gti,lei,gei}{,_Un}_{I8,R4,R8}`). Cross-checked against
  the case-list at `:719-748` -- the `immLarge` set is byte-for-byte the wide-
  immediate subset. Not too narrow (no wide-immediate opcode is missed), not too
  broad (no I4 opcode is gated; the I4 `Beqi`/`Bnei_Un`/`Blti`/... at `:709-718`
  keep `Operand3 = RefOffset` byte-identical to the pre-fix behavior).
- (b) I4 byte-identical confirmed by reading the diff: for `immLarge == false`,
  `op.Operand3 = localInfos[r1].RefOffset` still executes -- unchanged.
- (c) Scoped to the immediate-branch case only. The gate lives inside the
  `case OpCodeREnum.Beqi: ... Bgei_Un_R8:` block (`:709-804`); no non-immediate-
  branch opcode enters it. `Ceqi/Cgti/Clti` (the int-producing immediate compares)
  are in a SEPARATE case (`:608-650`) that calls `LowerR1R2` -- which does NOT
  write `Operand3` at all -- so they are unaffected either way.

Probed each width:
- `Bnei_Un_R8` (the F-8) -- gated OFF, immediate preserved. PASS (smoke + stash).
- `Bne_Un_I8` immediate -- would be gated OFF; in practice never emitted for the
  long combine (see probe 4), but the gate covers it defensively.
- `Ceqi_R8` -- in the int-producing case, uses `LowerR1R2`, never wrote
  `Operand3`; unaffected.
- I4 immediate branch (`Bnei_Un`) -- `immLarge == false`, dead write still
  happens, harmless. NeoStep 161/161 confirms no I4 regression.

### Probe 3 -- Blast radius: CONFIRMED no regression

The dead `Operand3 = RefOffset` write was happening for ALL immediate branches
before; the fix gates it OFF only for I8/R4/R8. Confirmed via full read of
`Optimizer.Neo.cs` that NO other code path relied on the `Operand3` stamp for an
immediate-branch opcode:
- All other `Operand3` writes in `LowerNeoOffsets` are for DIFFERENT, non-branch
  opcodes: `Move` (`:535`), `Move_Vt` (`:578`), `Initobj` (`:817`),
  `Box/Unbox/Isinst/Castclass` (`:872`), `Newarr` (`:1014`), `Ldelem` (`:1046`),
  `Stelem` (`:1069`), `Stind/Stobj` (`:1094`), `Ldind/Ldobj` (`:1119`),
  `Call/Newobj` (`:1314,1321`). None is an immediate-branch opcode, and none
  overlaps the gated case.
- The runtime immediate-branch arms (probe 1) read `DstOffset` for the operand
  slot, NOT `Operand3` -- so the dest slot's `RefOffset` was never needed by these
  arms anyway. The dest `RefOffset` is irrelevant for a primitive-value compare
  (the operand is a flat primitive, no ref slot involved).

The fix is strictly additive (it only REMOVES a dead, destructive write for the
wide-immediate forms). No opcode that legitimately needs `Operand3` stamped loses
it.

### Probe 4 -- Copy-prop long-vs-double discriminator: CONFIRMED via dump

Independent JIT-dump inspection:
- **TwoDoubleCombine (double):** emits `bnei.un.r8` and `ceqi.r8` -- the
  IMMEDIATE form. The `Ldc_R8 1.5` constant is folded INTO the opcode
  (`OperandDouble`). The dead `Operand3` write corrupts it -> silent wrong branch.
- **ThreeLongCombine (long):** emits `bne.un.i8` -- the REGISTER-REGISTER form
  (NOT `bnei.un.i8`). The `Ldc_I8` constant stays in a register; the I8 immediate
  branch is never produced, so the corruption is structurally unreachable.

This exactly matches design.md's claimed discriminator: copy-prop folds a `double`
constant into the immediate form but keeps a `long` constant in a register. The
"long works" claim is re-examined and holds: long is immune because its immediate
form is never emitted, NOT because the I8 immediate form would be safe (it would
NOT be -- the fix covers it defensively).

### Probe 5 -- Stash-toggle (>=2 of 5): CONFIRMED FAIL-on-HEAD -> PASS-after

Stashed ONLY `Optimizer.Neo.cs` (the fix), rebuilt CLI `Debug_Neo`
`--no-incremental`, ran probes against stashed HEAD (`fe13c25e`):

| probe                          | HEAD (stashed) | after fix |
|--------------------------------|----------------|-----------|
| `Dbl_TwoDoubleCombine`         | FAIL (1/1)     | PASS      |
| `Dbl_DoubleLongMix`            | FAIL (1/1)     | PASS      |
| `Dbl_ThreeLongCombine` (ctrl)  | PASS           | PASS      |

Controls genuinely pass (ThreeLongCombine passes on HEAD AND after -- not
trivially skipped). The fix is the ONLY engine change; the bug is pre-existing on
HEAD and the fix is load-bearing. (The other 3 of the 5 FAIL-on-HEAD probes --
ThreeDoubleCombine, DoubleIntMix (the double half), LiveRangeAcrossCall -- were
not individually toggled but all 8 Dbl probes are green in the post-fix NeoOptHard
24/24 run, confirming the same fix resolves them.)

### Probe 6 -- Smoke + Legacy-neutral: CONFIRMED

- **Neo smoke:** NeoStep 161/161 PASS (`Ran 161 tests, 0 failded`).
- **NeoOptHard:** 24/24 PASS (`Ran 24 tests, 0 failded`) -- 16 K1/F-MAJ-1 + 8 Dbl.
- **Legacy-neutral:** plain `Debug` CLI + `useRegister=true`, `NeoOptHardTest_Dbl_`
  filter -> 8/8 PASS. Legacy `ExecuteR` never executes `LowerNeoOffsets` (the whole
  file is `#if ENABLE_NEO_MODE`), so the bug is Neo-only and the fix is Neo-only.

### Probe 7 -- AllocateLocalStackSpaces untouched: CONFIRMED

`git diff HEAD --name-only -- .../JITCompiler.cs` -> empty. `Optimizer.FCP.cs` /
`Optimizer.BCP.cs` -> empty. The ONLY engine file changed is `Optimizer.Neo.cs`
(Neo-only). The propose's refuted slot-sizing hypothesis (D1) was correctly left
alone.

---

## Scope check

**Scope: CLEAN.** Intent = fix F-8 (double-local-combine silent wrong result) with
a dump-confirmed minimal Neo-only fix + adversarial probes. Delivered = exactly
that. The change touches:
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` (+40 lines: 1 gate +
  explanatory comment).
- `TestCases/NeoOptHardeningTest.cs` (+145 lines: 8 `Dbl_*` probes + section
  header).
- openspec artifacts (proposal/design/tasks/ship-log/spec delta) +
  planning-context append.

No scope creep. No shared-engine edit (type-spec / FCP / BCP / copy-prop /
JITCompiler all untouched).

---

## Findings

### F1 -- Minor -- stale root-cause comment in the test file

**File:** `TestCases/NeoOptHardeningTest.cs:348-360`
**Probe:** read the F-8 section header comment.
**Problem:** the section comment describes the dump-REFUTED candidate D2 as the
root cause:

> "The dump-gated root cause (D2): `Ldelem_R8`'s dest has NO registerType seed in
> the type-spec pass, so the compare/branch type-specialization reads it as I4 ->
> emits Ceq_I4 / Bne_Un_I4 reading 4 bytes of an 8-byte double slot -> silent
> wrong result. ... The fix seeds the Ldelem_R8/Ldelem_R4 (and Ldind_R8/R4/I8)
> dest types."

The actual dump-confirmed root cause is D4 (the dead `Operand3` write colliding
with the wide immediate via the OpCodeR union), and the actual fix is in
`Optimizer.Neo.cs LowerNeoOffsets` -- NOT a type-spec seed (no `JITCompiler.cs`
change shipped). The comment is a leftover from the propose-time leading candidate
and contradicts `design.md`'s "VERDICT: D4, NOT D2" and the code. A future reader
debugging these probes would be misled about both the failure mechanism and where
the fix lives.
**Fix:** rewrite the section comment (lines ~351-357) to state the D4 mechanism:
the dead `Operand3 = RefOffset` write at offset 16 in `LowerNeoOffsets` clobbers
the high 4 bytes of `OperandDouble`/`OperandLong` (@12-19) for the I8/R8
immediate-branch forms; the fix gates that write off via `immLarge`. (Low-effort,
ASCII-safe; one Edit.)

### F2 -- Trivial -- `immLarge` includes R4 unnecessarily (over-broad, harmless)

**File:** `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs:791-795`
**Probe:** compared the R4 immediate's field offset to `Operand3`'s.
**Problem:** R4 immediate branches carry their constant in `OperandFloat` (@8-11),
which is DISJOINT from `Operand3` (@16). So the dead `Operand3` write is harmless
for R4 (just as it is for I4) -- gating R4 off changes nothing. The comment at
`:770-777` even implies the collision is I8/R8-only ("aliases the high half of
OperandLong/OperandDouble"), yet the gate lists the 10 R4 forms.
**Severity rationale:** this is NOT wrong -- "skip a dead write" is safe regardless
of whether the write was destructive. It is a minor consistency nit: the gate is
slightly broader than the destructive set (I8 + R8 only). Including R4 is arguably
defensible as "defensive uniformity" (treat all non-I4 wide immediate forms the
same), so this is Trivial.
**Fix (optional):** either (a) drop the 10 R4 entries from `immLarge` to match the
precisely-destructive set, OR (b) extend the comment to say the gate covers all
non-I4 wide immediate forms for uniformity (R4 included defensively). Recommend
(b) -- keeps the uniform "I4 keeps the legacy write, everything else skips it"
mental model, which is easier to audit than a precisely-minimized list.

---

## Verdict

**APPROVE.** The load-bearing reasoning (union overlap, dead-write, no arm reads
`Operand3`, long-vs-double discriminator) is independently confirmed by source
read + JIT dump + stash-toggle + smoke. The fix is minimal, Neo-only, scoped to
the immediate-branch case, and byte-identical for I4. The two findings are
documentation/consistency nits (Minor + Trivial) that do not affect correctness;
F1 should be fixed before ship (misleading comment), F2 is optional. Working tree
remains UNCOMMITTED.

- OpCodeR union-overlap independently confirmed: YES (Operand3 @16 overlaps
  OperandDouble/OperandLong high; Operand @8 / OperandFloat @8-11 disjoint; no
  immediate-branch arm reads Operand3).
- `immLarge` gate correct: YES (I8/R4/R8 gated, I4 byte-identical, scoped to
  immediate-branch).
- Copy-prop long-vs-double discriminator confirmed via dump: YES (double ->
  `bnei.un.r8` immediate; long -> `bne.un.i8` register-register).
- Stash-toggle (2 of 5) FAIL-on-HEAD -> PASS-after independently confirmed: YES.
- Smoke reproduced (Neo 161/161 + NeoOptHard 24/24) + Legacy-neutral (8/8 Dbl on
  plain Debug) + AllocateLocalStackSpaces untouched: YES.
