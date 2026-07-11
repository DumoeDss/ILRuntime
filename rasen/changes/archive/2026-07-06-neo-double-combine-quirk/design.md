## Context

**F-8 / NEO-DOUBLE-COMBINE** is a silent-wrong-result quirk in the Neo
register VM (`ExecuteNeo`) optimizer/JIT path, surfaced by the
neo-array-completion review (Finding F-1) and tracked in
`.trae/documents/neo-deferred-items.md` §3 (F-8). Refined characterization
(from the review's independent reproduction):

- A method reads **2+ `double` locals** (e.g. from a `double[]` via
  `Ldelem_R8`) and combines them in one boolean expression
  (`if (a0 != x || a1 != y)`) -> the comparison **silently misfires**
  (DivideByZero on the assertion trip; the `||` evaluates the wrong branch /
  a `double` reads as 0).
- **`double`-specific**: 3 `long` locals combined in the same shape WORK; only
  `double` triggers it. This is the key discriminator.
- A single `double` read (each in its own `if`) is correct.
- Pre-existing (stash-proven): the failing comparison uses plain `Ldelem_R8`
  reads (the pre-existing fast typed-indexer path, NOT the new `is Array`
  branches) + a pure `double`-local combine. Upstream of, and independent
  from, the D-ARR array work.
- F-MAJ-1 class (silent wrong result), but on an 8-byte PRIMITIVE (`double`),
  not a CLR struct. So the F-MAJ-1 representation-mismatch mechanism (boxed-ref
  vs flat-bytes) does NOT apply -- a `double` local is a primitive, sized 8 and
  stored flat in the frame by `AllocateLocalStackSpaces:1561-1574`.

**Baseline.** HEAD = `fe13c25e` (neo-array-completion). NeoStep smoke = 161/161
green (154 + 7 array probes). Legacy 518/519 (~7 pre-existing Neo-feature
failures, the regression reference for shared-engine changes). The F-8
reproducer FAILS on HEAD (the array-completion reviewer reproduced it with
temporary `NeoStep16_REV_TwoDoubleDirectIf` / `ThreeDoubleDirectIf` /
`SingleDoubleEach` probes, since reverted).

**Established pattern to mirror.** `[OPT-HARDEN-2]` / F-MAJ-1
(`openspec/changes/archive/2026-07-05-neo-opt-harden-2/`) is the sibling:
stash-prove pre-existing, dump-gate the root cause, minimal Neo-only /
Legacy-neutral fix, `NeoOptHardTest_*` regression probes (FAIL-on-HEAD ->
PASS-after). F-MAJ-1's propose-time "leading candidate" (write-side boxed-ref,
Option A) was INVERTED by the apply-time dump (declare-side flat-bytes, Option
B). The discipline: probe BEFORE designing the fix; STOP if the designed fix is
wrong; never force a fix that the dump does not confirm. This change applies
that discipline to F-8.

## Goals / Non-Goals

**Goals:**
- **Dump-gate the F-8 root cause on current HEAD.** Construct the F-8
  reproducer, dump the JIT body + `localInfos` + the post-lowering opcode
  stream for the combine method, and pinpoint the defect (which candidate
  below).
- **Ship a minimal fix** whose root cause is dump-confirmed. Neo-only by
  preference; if the fix site is a SHARED pass (FCP/BCP/copy-prop), gate
  `#if ENABLE_NEO_MODE` OR confirm Legacy-neutral (plain `Debug` +
  `useRegister=true` smoke unchanged).
- **Adversarial regression probes** (`NeoOptHardTest_Dbl_*`) covering the F-8
  signature + the `double`-vs-`long` boundary + isolation + live-range +
  single-local regression. Green smoke MISSES this class (Step 17 B1 /
  OPT-HARDEN K1 / F-MAJ-1 lessons); the probes are the load-bearing guard.
- **Record the discriminator finding** (frame-slot sizing/alignment is NOT the
  defect; `double` and `long` get byte-identical 8-byte slots) so a future
  "fix the 8-byte allocator" change is not mis-attributed.

**Non-Goals:**
- NOT a rewrite of the type-specialization pass or copy-prop. Minimal fix only.
- NOT touching `AllocateLocalStackSpaces` 8-byte-slot sizing (proven
  byte-identical for `double`/`long` at propose; recorded).
- NOT touching Legacy `ExecuteR` (the REFERENCE, not a target).
- NOT a perf optimization. Correctness only.
- NOT fixing every 8-byte primitive -- only `double` reproduces; the fix is
  scoped to the R8 path the dump confirms.

## Decisions

### D1 -- The fix is DUMP-GATED; the slot-sizing hypothesis is REFUTED at propose

The F-8 symptom (`long` works, `double` fails) CANNOT be a frame-slot sizing /
alignment defect: `AllocateLocalStackSpaces` sizes BOTH `double` and `long`
to 8 bytes (`GetPrimitiveSize:1898-1921`) and aligns BOTH to 8
(`AlignUp(offset, size)` at `:1568`, `size=8` for both). They get
byte-identical frame slots. So the divergence is in the COMPUTATION path
(type-spec / compare / copy-prop), NOT the slot sizing. This mirrors the
F-MAJ-1 lesson (`AllocateLocalStackSpaces` monotonic allocation was NOT the
F-MAJ-1 fix site). The spec records this so a future "fix the 8-byte slot
allocator" proposal is rejected on the same grounds.

### D2 -- Leading candidate: R8 compare type-specialization mis-types the combine

A C# `d1 != x || d2 != y` lowers through the typed-compare machinery
(`GetTypedCompareOpcode:1182-1218`, `GetTypedBranchOpcode:1221+`,
`GetTypedImmediateCompareOpcode:1321+`). All three HAVE an `R8` case. But the
combine may flow through a path where the R8 operand's `registerType` is NOT
yet seeded when the compare reads it:

- The operand comes from a `Ldelem_R8` whose DEST is typed
  (`SetRegisterType(..., appdomain.DoubleType)` -- but check the JIT case;
  `Ldelem_R8` may NOT seed the dest type, unlike `Ldloca`/`Ldflda`).
- OR from a `Conv_R8` whose dest IS seeded (`:776`) but the immediately-
  following compare reads a different register that aliases it pre-seed.
- OR the `||`/`&&` short-circuit lowering emits the compare against the
  SOURCE register before the producer's dest-type seed has propagated (the
  type-spec pass is single-forward-pass; order matters).

If `InferPrimTag` falls back to `I4` for an untyped R8 operand, the compare
emits `Ceq_I4`/`Bne_Un_I4` (reading 4 bytes of an 8-byte `double` slot) ->
silent wrong result. This fits the `double`-specific signature: the I8 path is
exercised by the working 3-`long` case (so the I8 type-spec + I8 compare are
correct); the divergence is R8-specific.

**Fix shape (if confirmed):** seed the R8 operand's `registerType` BEFORE the
combine reads it. The missing producer is whatever feeds the combine (the dump
identifies it -- likely `Ldelem_R8`'s missing dest-type seed, mirroring the
`neo-vt-this-addr` "Newobj-dest typing" fix). Additive + minimal; likely
SHARED (the type-spec pass runs for both engines), so confirm Legacy-neutral
or gate `#if ENABLE_NEO_MODE`.

**Probe-to-confirm at apply (Block 0):** dump the JIT body for the F-8
reproducer method. Inspect the combine's compare/branch opcodes: are they
`Ceq_R8`/`Bne_Un_R8` (correct) or `Ceq_I4`/`Bne_Un_I4` (the defect)? If the
latter, D2 is confirmed. Also dump `registerTypes[]` for the two `double`
locals' dest registers at the point the combine is type-specialized: is the
entry `DoubleType` (correct) or null/`IntType` (the defect)?

### D3 -- Fallback candidate: R8 copy-prop fold width-mishandles a `double`

If D2 is refuted (the type-spec seeds are correct AND the emitted opcodes are
`*_R8`), the defect is downstream: FCP/BCP/copy-prop (SHARED passes) may fold a
`double` local's value through a 4-byte-wide intermediate. The copy-prop value
width may assume I4 for an operand whose `registerType` was not seeded at the
copy-prop entry point (copy-prop runs AFTER type-spec; if it re-derives width
it could disagree).

**Fix shape (if confirmed):** make copy-prop's value-width dispatch
R8-aware (or thread the type-spec'd `registerType` into copy-prop). SHARED --
gate `#if ENABLE_NEO_MODE` or confirm Legacy-neutral.

**Probe-to-confirm at apply:** dump the optimizer IR BEFORE and AFTER
copy-prop. Does a `double` local's value get re-materialized at a 4-byte width
after the fold? If yes, D3 is the defect. The `long`-works control: if `long`
flows through the same fold and stays 8-byte, the R8 arm of the width
dispatch is the defect.

### D4 -- Long-shot candidate: `LowerNeoOffsets` R8 operand overlap

`LowerNeoOffsets` (Neo-only, `Optimizer.Neo.cs`) advances operand byte-offsets
by the slot's declared `Size` (8 for both `double` and `long`). So an
overlap defect should affect BOTH equally -- refuted in principle by the
`long`-works signature. But the dump confirms: for the F-8 combine method,
inspect the post-lowering `DstOffset`/`SrcOffset` for the compare/branch
opcodes. Do the R8 operands overlap a sibling where the I8 operands would not?
(Extremely unlikely given the shared lowering, but the dump is cheap.)

### D5 -- Defer fallback (the Q-STRUCT / Q-LONG / Q-NEWOBJ outcome)

If the dump REFUTES D2, D3, AND D4 (the type-spec seeds are correct, the
emitted opcodes are `*_R8`, copy-prop is width-correct, lowering does not
overlap, yet the symptom persists), F-8 -> DEFERRED. Ship NO guessed fix.
Pin the dump + reproducer; mark the requirement DEFERRED in the spec delta;
record the finding so a future reproducing case has a ready home. This is the
honest outcome -- it matches Q-STRUCT / Q-LONG / Q-NEWOBJ (suspected quirk
already gone or non-reproducible on current HEAD) and the F-MAJ-1 "STOP if
the designed fix is wrong" discipline.

### D6 -- Test convention: mirror F-MAJ-1 / K1 (`NeoOptHardTest_Dbl_*`)

Probes go in `TestCases/NeoOptHardeningTest.cs` under the `NeoOptHardTest_Dbl_`
prefix (NOT promoted to `NeoStep`, so the 161/161 smoke stays green pre-fix).
Assertion = the DivideByZero-on-wrong-result pattern (the Neo VM cannot yet
`new Exception`; this is the established convention). After the fix, all
`Dbl_*` probes PASS; the smoke stays 161/161; Legacy-neutral confirmed via
stash-toggle plain-`Debug` + `useRegister=true` NeoStep-filter run.

## Risks / Trade-offs

- **[Risk] The dump refutes ALL candidates -> no fix ships (DEFERRED).**
  Mitigation: this is the honest, disciplined outcome (D5). The reproducer +
  dump are pinned so a future case has a home. NOT a failure -- matches
  Q-STRUCT / Q-LONG / Q-NEWOBJ. The proposal's "What Changes" states this
  explicitly.
- **[Risk] The fix touches a SHARED pass (type-spec, FCP/BCP/copy-prop) ->
  Legacy regression.** Mitigation: gate `#if ENABLE_NEO_MODE` OR confirm
  Legacy-neutral via the stash-toggle plain-`Debug` smoke (the F-MAJ-1
  precedent). Legacy `ExecuteR` is the REFERENCE.
- **[Risk] The propose-time leading candidate (D2) is wrong, like F-MAJ-1's
  was.** Mitigation: the fix is dump-locked at apply, not at propose. The dump
  is the arbiter. If D2 is refuted, fall to D3 / D4 / D5 in order.
- **[Risk] A green smoke hides the defect (the silent-corruption class).**
  Mitigation: MANDATORY adversarial probes (`NeoOptHardTest_Dbl_*`) including
  the exact F-8 signature + the `double`-vs-`long` boundary + isolation +
  live-range + single-local regression. The probes FAIL-on-HEAD (stash-toggle
  proof of load-bearing) and PASS-after.
- **[Risk] `Debug_Neo`'s `OUTPUT_JIT_RESULT` floods the dump.** Mitigation: use
  the F-MAJ-1 review-fix earned gotcha -- add a temporary `Console.WriteLine`
  INSIDE the runtime arm / JIT case (fires only for the probe method), run the
  single probe, then remove the diagnostic. Do NOT grep the full JIT dump
  (floods).
- **[Risk] Stale-DLL false-failure iteration (the F-MAJ-1 / IL-Ex earned
  gotcha).** Mitigation: ALWAYS rebuild TestCases `--no-incremental` after
  editing test source; verify DLL mtime > source mtime; rebuild CLI
  `--no-incremental` after adding a host type.

## Open Questions

- **OQ1 (resolve at apply via dump):** is the F-8 defect D2 (R8 type-spec
  mis-types the combine), D3 (R8 copy-prop fold), D4 (LowerNeoOffsets overlap),
  or D5 (defer -- all refuted)? The dump decides; the fix follows.
- **OQ2 (resolve at apply):** is `Ldelem_R8`'s JIT case missing a dest-type
  seed (the `SetRegisterType(..., appdomain.DoubleType)` that `Conv_R8` has at
  `:776` but `Ldelem_*` may lack)? This is the most likely concrete shape of
  D2. The dump of `registerTypes[]` for the `Ldelem_R8` dest register answers
  it.
- **OQ3 (resolve at apply):** if the fix is in a SHARED pass, is it
  Legacy-neutral (the type-spec change is byte-identical for every existing
  I4/I8 operand, only ADDING a correct R8 seed) or does it need an
  `#if ENABLE_NEO_MODE` gate? The stash-toggle plain-`Debug` smoke answers it.

## Apply (2026-07-06) -- dump-confirmed root cause + fix (D4, NOT D2)

**VERDICT: F-8 FIXED. Candidate = D4 (LowerNeoOffsets operand-union overlap), NOT D2 (refuted) and NOT D3 (not reached).** The propose-time leading candidate (D2 R8 type-spec mis-types the combine) was REFUTED by the dump; the actual defect is a D4 shape the propose dismissed as a long shot -- but the propose D4 reasoning (lowering is width-agnostic, advancing by the slot declared Size=8 for both double and long) was CORRECT for the offset advance and WRONG about the operand field. The defect is not in the offset advance; it is in a DEAD Operand3 field write that collides with the 8-byte immediate constant via the OpCodeR explicit-layout union.

### The dump evidence (Block 0)

**(a) D2 REFUTED -- Ldelem_R8 dest IS seeded System.Double.** A temporary diagnostic in TypeSpecializeNeoOpcodes (run only for TwoDoubleCombine) showed the two Ldelem_R8 dest registers are correctly seeded:

    [F8DUMP] Ldelem_R8 dest r1 regType=System.Double
    [F8DUMP] Ldelem_R8 dest r2 regType=System.Double

The emitted compare opcodes are also correct (Bnei_Un_R8, Ceqi_R8 -- the R8 forms, NOT *_I4). So the type-spec pass is innocent. (The propose hypothesis that Ldelem_R8 lacks a dest-type seed was wrong -- Ldc_R8 / Ldfld_R8 / Conv_R8 seed; Ldelem_R8 does NOT seed in the type-spec switch, BUT the C# compiler `a0 != 1.5` lowering emits Ldc_R8 1.5 into a register, and the compare type-specializes off THAT register seeded System.Double -- so the R8 form is emitted regardless. The dump proves it.)

**(b) The runtime trace pinned the defect.** A diagnostic in the Bnei_Un_R8 runtime arm showed:

    [F8RT] Bnei_Un_R8 DstOff=8 val=1.5 opd=2.121995791E-314 -> branch=True to 12

The compared VALUE (val=1.5, the actual a0) is correct. The IMMEDIATE CONSTANT (opd should be 1.5) is GARBAGE -- 2.121995791E-314 is the bit pattern of a small int reinterpreted as a double. The branch tripped because 1.5 != 2.1e-314 -> the || took the wrong path -> DivideByZero.

### Why long works and double fails (the discriminator, RESOLVED)

The Bnei_Un_R8 immediate constant lives in ip->OperandDouble. The OpCodeR struct is StructLayout(LayoutKind.Explicit):
- Operand (int) @ offset 8; OperandFloat (float) @ offset 8
- Operand2 (int) @ offset 12; OperandLong (long) @ 12-19; OperandDouble (double) @ 12-19
- Operand3 (int) @ offset 16   <-- overlaps the HIGH 4 bytes of OperandLong/OperandDouble (12-19)

In Optimizer.Neo.cs LowerNeoOffsets, the immediate-branch case stamped op.Operand3 = localInfos[r1].RefOffset for EVERY immediate branch (I4 / I8 / R4 / R8). Operand3 is NEVER READ by any immediate-branch runtime arm (Bnei_Un_R8 reads DstOffset, OperandDouble, Operand4; Bnei_Un_I8 reads DstOffset, OperandLong, Operand4; Bnei_Un I4 reads DstOffset, Operand, Operand4). The write is DEAD -- but DESTRUCTIVE for the I8/R4/R8 forms: it clobbers the high 4 bytes of the 8-byte immediate constant at offset 16.

Why the long-works / double-fails split:
- The C# compiler / copy-prop folds a double Ldc_R8 1.5 constant INTO the immediate form (Bnei_Un_R8 -- the constant lives in the opcode). -> the Operand3 write corrupts OperandDouble -> silent wrong branch.
- For long, copy-prop does NOT fold the Ldc_I8 into the immediate form; it keeps the constant in a register and emits a REGISTER-REGISTER Bne_Un_I8 (the dump of ThreeLongCombine confirmed: ldc.i4.s r9,10; bne.un.i8 -- a register-register compare, no immediate). The I8 immediate branch (Bnei_Un_I8) is simply never produced for the long combine, so the corruption is unreachable for long. (The corruption would ALSO bite an I8 immediate branch if one were emitted -- the fix covers it too.)
- A single double (no combine) does not produce a Bnei_Un_R8 either (the C# `if (a0 != 1.5)` lowers to ceqi.r8; brfalse -- the Ceqi_R8 arm uses LowerR1R2, which does NOT write Operand3, so the immediate is preserved). Only the combined || form -- where copy-prop fuses a constant-folded double into a Bnei_Un_R8 -- trips the bug.

So the double-vs-long discriminator is NOT 8-byte-slot sizing (refuted at propose, re-confirmed: the slots are byte-identical). It is: **does the optimizer constant-folding produce a wide-immediate branch form (Bnei_Un_R8 / Bnei_Un_I8)?** For double yes; for long no. And the wide-immediate branch form is the one whose constant at offset 12-19 is clobbered by the dead Operand3 write at offset 16.

### The fix (Block 2.3, D4 -- Neo-only)

Optimizer.Neo.cs LowerNeoOffsets, the immediate-branch case: resolve DstOffset (always needed) but SKIP the dead Operand3 = RefOffset write for the I8/R4/R8 immediate-branch forms (their 8-byte/4-byte immediate constant lives at offset 12-19 / 8-11, colliding with Operand3 @16 / Operand2 @12). The I4 forms keep the Operand3 write byte-identical (their immediate Operand @8 does not collide; preserved for safety since the original case grouped them).

The fix is a single conditional (immLarge boolean) gating the existing op.Operand3 = localInfos[r1].RefOffset line. Additive + minimal; Neo-only (the whole file is #if ENABLE_NEO_MODE). NO type-spec / copy-prop / runtime change. The immediate-branch runtime arms already do NOT read Operand3, so removing the write for the wide-immediate forms changes nothing except preserving the constant.

### Why this is NOT a regression risk for I4

The I4 immediate branches (Bnei_Un, Beqi, etc.) keep the Operand3 write byte-identical (gated off only for immLarge). Their immediate Operand lives at offset 8; Operand3 @16 never collided. Full NeoStep smoke 161/161 confirms no I4 regression.

### Adversarial probe results (Block 1.3 / 3.3)

HEAD (pre-fix) -- FAIL: TwoDoubleCombine, ThreeDoubleCombine, DoubleLongMix, DoubleIntMix, LiveRangeAcrossCall (the 5 that combine a Ldelem_R8-sourced double). HEAD (pre-fix) -- PASS: ThreeLongCombine (long), SingleDoubleEach (no combine), SingleDoubleRegression (no combine).

After fix -- ALL 8 Dbl_* PASS. Full NeoStep smoke 161/161 (no regression). Full NeoOptHard 24/24 (16 K1/F-MAJ-1 + 8 Dbl). Legacy (plain Debug + useRegister=true): all 8 Dbl_* PASS on Legacy too (the bug was Neo-only; Legacy ExecuteR never executes LowerNeoOffsets); Legacy NeoStep 161 ran / 8 pre-existing failures unchanged (the standing Neo-feature failure set).

### Stash-toggle proof (Block 0.1 / 0.2 + load-bearing)

The fix is the ONLY engine change; stashing JUST Optimizer.Neo.cs and rebuilding the CLI reproduces the F-8 failure on stashed HEAD (TwoDoubleCombine -> 1 failed), and restoring it turns it green (TwoDoubleCombine -> 0 failed). This proves (a) the bug is PRE-EXISTING on HEAD fe13c25e (not a regression from any in-flight change), and (b) the fix is LOAD-BEARING (the probe FAILS without it, PASSES with it). The stash-toggle is the F-MAJ-1 / VT-THIS-ADDR pattern.

### Deviation from the propose-time ranking

The propose ranked D2 (type-spec) LEADING, D3 (copy-prop) FALLBACK, D4 (lowering overlap) long shot -- refuted in principle. The dump REFUTED D2 (seed correct), did NOT need to reach D3, and CONFIRMED D4 -- but a D4 shape the propose did NOT enumerate (a dead field write colliding via the union, NOT an offset-overlap between siblings). This re-affirms the K1 / F-MAJ-1 / Q-NEWOBJ lesson: the dump is the arbiter; the propose-time ranking is provisional. The propose D4 refutation (lowering is width-agnostic) was technically correct for the field it considered (DstOffset/SrcOffset) and simply did not consider the dead Operand3 write -- a reminder that refuted-in-principle must enumerate EVERY field the case writes, not just the ones the design focused on.

### Spec-validation gotcha (re-affirms the area4 / K2-FAM finding)

The openspec validator requires the requirement DESCRIPTION (not just the title) to contain SHALL/MUST. The spec delta MODIFIED requirement was authored with an explicit SHALL sentence up front.
