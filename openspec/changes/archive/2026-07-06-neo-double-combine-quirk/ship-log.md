# Ship Log -- neo-double-combine-quirk (F-8 / [OPT-HARDEN-3])

**Date:** 2026-07-06
**Outcome:** F-8 FIXED (candidate D4 confirmed; D2 and D3 refuted by the dump).
**HEAD at apply:** `fe13c25e` (Neo array-completion).

## Dump-confirmed root cause (D4, NOT D2)

The propose-time LEADING candidate was D2 (R8 type-spec mis-types the combine:
an R8 operand, likely from `Ldelem_R8`, lacks the dest-type seed so the compare
emits `Ceq_I4`/`Bne_Un_I4` reading 4 bytes of an 8-byte slot). **The dump
REFUTED D2** and confirmed a D4 shape the propose had dismissed as a "long
shot, refuted in principle".

### D2 refuted

A temporary diagnostic in `TypeSpecializeNeoOpcodes` (filtered to
`TwoDoubleCombine`) showed the two `Ldelem_R8` dest registers ARE correctly
seeded `System.Double`, and the emitted compare/branch opcodes are the correct
R8 forms (`Bnei_Un_R8`, `Ceqi_R8`):

```
[F8DUMP] Ldelem_R8 dest r1 regType=System.Double
[F8DUMP] Ldelem_R8 dest r2 regType=System.Double
```

(The `Ldelem_R8` JIT case does NOT seed its dest in the type-spec switch, but
the C# compiler's `a0 != 1.5` lowering emits `Ldc_R8 1.5` into a register, and
the compare type-specializes off THAT register's seeded `System.Double` -- so
the R8 form is emitted regardless.)

### D4 confirmed (the actual defect)

A diagnostic in the `Bnei_Un_R8` runtime arm showed:

```
[F8RT] Bnei_Un_R8 DstOff=8 val=1.5 opd=2.121995791E-314 -> branch=True to 12
```

The compared VALUE (`val=1.5`) is correct; the IMMEDIATE CONSTANT `opd`
(should be `1.5`) is GARBAGE (`2.1e-314` = a small int's bit pattern as a
double). The branch tripped because `1.5 != 2.1e-314`.

The `OpCodeR` struct is `[StructLayout(LayoutKind.Explicit)]`:
- `Operand` (int) @ 8; `OperandFloat` (float) @ 8
- `Operand2` (int) @ 12; `OperandLong` (long) @ 12-19; `OperandDouble` (double) @ 12-19
- `Operand3` (int) @ 16  <-- overlaps the HIGH 4 bytes of `OperandLong`/`OperandDouble`

In `Optimizer.Neo.cs LowerNeoOffsets`, the immediate-branch case stamped
`op.Operand3 = localInfos[r1].RefOffset` for EVERY immediate branch (I4/I8/R4/
R8). `Operand3` is NEVER READ by any immediate-branch runtime arm (each reads
only `DstOffset` + the immediate field + `Operand4`). The write is DEAD -- but
DESTRUCTIVE for the I8/R4/R8 forms: it clobbers the high 4 bytes of the 8-byte
immediate constant at offset 16.

### The long-works / double-fails discriminator (RESOLVED)

- **double:** the optimizer's constant-folding folds a `double` `Ldc_R8`
  constant INTO the immediate form (`Bnei_Un_R8` -- the constant lives in the
  opcode). The `Operand3` write corrupts `OperandDouble` -> silent wrong branch.
- **long:** copy-prop does NOT fold `Ldc_I8` into the immediate form; it keeps
  the constant in a register and emits a REGISTER-REGISTER `Bne_Un_I8` (dump of
  `ThreeLongCombine` confirmed: `ldc.i4.s r9,10; bne.un.i8`). The I8 immediate
  branch is never produced for the long combine, so the corruption is
  unreachable. (The fix covers `Bnei_Un_I8` too -- if one is ever emitted, it
  would be corrupted the same way.)
- **single double (no combine):** does not produce `Bnei_Un_R8` either (C#
  `if (a0 != 1.5)` lowers to `ceqi.r8; brfalse`; `Ceqi_R8` uses `LowerR1R2`,
  which does NOT write `Operand3`). Only the combined `||` form -- where
  copy-prop fuses a constant-folded double into a `Bnei_Un_R8` -- trips the bug.

So the discriminator is NOT 8-byte-slot sizing (refuted at propose; re-
confirmed). It is: **does the optimizer's constant-folding produce a wide-
immediate branch form?** For double yes; for long no. The `double`-vs-`long`
split is a property of the constant-folding, not the frame layout.

## The fix (Neo-only, 1 conditional)

`Optimizer.Neo.cs LowerNeoOffsets`, the immediate-branch case: resolve
`DstOffset` (always needed) but SKIP the dead `Operand3 = RefOffset` write for
the I8/R4/R8 immediate-branch forms. A single `immLarge` boolean gates the
existing `op.Operand3 = localInfos[r1].RefOffset` line. The I4 forms keep the
write byte-identical (their immediate `Operand` @8 does not collide; preserved
for safety since the original case grouped them).

- NO type-spec change (D2 refuted).
- NO copy-prop change (D3 not reached).
- NO runtime change (the arms already do not read `Operand3`).
- NO `AllocateLocalStackSpaces` change (the 8-byte-slot sizing is NOT the
  defect -- `double` and `long` get byte-identical 8-byte slots; recorded so a
  future "fix the 8-byte allocator" change is NOT mis-attributed as the F-8
  fix).

The whole file is `#if ENABLE_NEO_MODE`; the change is Neo-only by
construction. Legacy's `ExecuteR` never executes `LowerNeoOffsets`.

## Stash-toggle proof (pre-existing + load-bearing)

- The fix is the ONLY engine change. Stashing JUST `Optimizer.Neo.cs` and
  rebuilding the CLI reproduces the F-8 failure on stashed HEAD
  (`TwoDoubleCombine` -> 1 failed). Restoring it turns it green
  (`TwoDoubleCombine` -> 0 failed).
- This proves (a) the bug is PRE-EXISTING on HEAD `fe13c25e` (not a regression
  from any in-flight change), and (b) the fix is LOAD-BEARING.
- (The neo-array-completion reviewer had already stash-reproduced F-8 with
  temporary probes; this confirms it independently on the current HEAD.)

## Adversarial probe results (Block 1.3 / 3.3)

8 probes in `TestCases/NeoOptHardeningTest.cs` under `NeoOptHardTest_Dbl_*`:

| Probe | HEAD (pre-fix) | After fix |
|---|---|---|
| TwoDoubleCombine (exact F-8) | FAIL (DivByZero) | PASS |
| ThreeDoubleCombine | FAIL | PASS |
| ThreeLongCombine (control) | PASS | PASS |
| DoubleLongMix | FAIL | PASS |
| DoubleIntMix | FAIL | PASS |
| SingleDoubleEach (no combine) | PASS | PASS |
| LiveRangeAcrossCall | FAIL | PASS |
| SingleDoubleRegression | PASS | PASS |

The 5 FAIL-on-HEAD probes all combine a `Ldelem_R8`-sourced double; the 3
PASS-on-HEAD probes are the controls (long combine / isolated doubles). After
the fix all 8 PASS. Each probe is FAIL-on-HEAD-stash-toggle -> PASS-after-fix
(load-bearing).

## Regression + Legacy-neutrality gate

- **NeoStep smoke: 161/161 all-green** (ZERO regressions; the fix is scoped to
  the wide-immediate-branch lowering).
- **NeoOptHard: 24/24** (16 existing K1/F-MAJ-1 + 8 new Dbl).
- **Legacy-neutral:** plain `Debug` CLI builds clean (Neo-only file compiled
  out). All 8 `Dbl_*` PASS on Legacy too (the bug was Neo-only). Legacy NeoStep
  = 161 ran / 8 pre-existing Neo-feature failures unchanged (the standing set,
  identical with and without the fix -- stash-toggle belt-and-braces).

## Files edited

- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` -- `LowerNeoOffsets`
  immediate-branch case: gate the dead `Operand3` write off for the wide-immediate
  (I8/R4/R8) forms. The load-bearing fix. Neo-only. (+39/-1)
- `TestCases/NeoOptHardeningTest.cs` -- 8 `NeoOptHardTest_Dbl_*` adversarial
  probes + the `MakeDoubles` helper. Test-only.
- `openspec/changes/neo-double-combine-quirk/{proposal,design,tasks}.md` + `specs/
  neo-optimizer/spec.md` -- the change artifacts (design.md appended with the
  dump-confirmed root cause; spec delta updated to ACTIVE + the D4 fix).

## Did NOT

- Did NOT git commit/push (per process discipline; the LEAD commits after
  review).
- Did NOT update `.trae/documents/neo-deferred-items.md` F-8 row (the shipper
  does at archive, per implementer process discipline).
- Did NOT touch `ILIntepreter.Neo.cs` or `JITCompiler.cs` (the temporary
  diagnostics were added during the dump-gate and fully removed; `git diff`
  confirms both files are clean).

## Accepted-known (no fix needed)

- **F2 (Trivial) -- `immLarge` includes R4 unnecessarily (over-broad, harmless).**
  The `immLarge` set gates the dead `Operand3` write OFF for I8/R4/R8 immediate-
  branch forms. R4's immediate lives in `OperandFloat` (@8-11), which is DISJOINT
  from `Operand3` (@16), so the dead write was already harmless for R4 -- gating
  R4 off changes nothing for correctness. The destructive set is strictly I8 + R8;
  R4 is included for uniform "I4 keeps the legacy write, everything else skips it"
  (easier to audit than a precisely-minimized I8+R8 list). Recorded, no fix needed.
  (Review verdict F2 = Trivial; the broader set is safe.)

## The OpCodeR `[StructLayout(Explicit)]` union gotcha (recurring class)

The defect is the 3rd concrete instance of the OpCodeR explicit-layout union
overlap class -- the same gotcha that bit Step 12 (frame value-type offsets) and
OPT-HARDEN (K1's `Move`/`Move_Vt` `Operand3` overlap reasoning), now via a DEAD
field write colliding through the union. The handoff `neo-handoff.md` §4 already
warns about it; this change re-inforces the rule for future optimizer work:
**when a `LowerNeoOffsets` case stamps ANY `OpCodeR` field, enumerate EVERY field
that overlaps it via the explicit layout, and confirm none of those overlapping
fields is the live payload for that opcode's runtime arm.** A write that looks
dead (no arm reads the stamped field) can still be destructive if it clobbers a
different field's bytes through the union. The recurring instances:
1. Step 12 -- frame value-type `DstOffset`/`SrcOffset` byte-offset pair overlap.
2. OPT-HARDEN (K1) -- `Move`/`Move_Vt` `Operand3` overlap reasoning.
3. F-8 (this change) -- immediate-branch `Operand3` clobbering `OperandDouble`/
   `OperandLong` high half.

## Lesson re-affirmed

The propose ranked D2 (type-spec) LEADING and D4 (lowering) "long shot, refuted
in principle". The dump REFUTED D2 and CONFIRMED D4 -- but a D4 shape the
propose did NOT enumerate (a dead field write colliding via the explicit-layout
union, NOT an offset-overlap between siblings). The propose's D4 refutation
("lowering is width-agnostic") was correct for the field it considered
(`DstOffset`/`SrcOffset`) and simply did NOT consider the dead `Operand3`
write. **"Refuted in principle" must enumerate EVERY field the case writes,
not just the ones the design focused on.** Re-affirms the K1 / F-MAJ-1 /
Q-NEWOBJ / F-6 family: the dump is the arbiter; the propose-time ranking is
provisional; STOP if the designed fix is wrong, don't force it.
