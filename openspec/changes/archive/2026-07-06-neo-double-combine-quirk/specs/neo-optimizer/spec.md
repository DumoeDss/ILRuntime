## ADDED Requirements

### Requirement: Combining 2+ double locals in one boolean expression computes the correct result (F-8)

The Neo register VM SHALL compute the correct result when a method reads 2+
`double` locals and combines them in a single boolean expression (e.g.
`if (a0 != expected0 || a1 != expected1)`); the comparison MUST NOT silently
misfire (trip the wrong branch, or read a `double` as 0). The combine MUST
produce the same truth value the equivalent isolated checks would produce.
(Status: ACTIVE -- the dump-confirmed fix has shipped.) The `double` operand's
immediate constant MUST be preserved end-to-end through the optimizer's
offset-lowering pass (`Optimizer.Neo.cs LowerNeoOffsets`): the I8/R4/R8
immediate-branch forms (`Bnei_Un_R8`, `Beqi_R8`, `Blti_R8`, etc.) carry their
8-byte/4-byte constant in `OperandLong`/`OperandDouble`/`OperandFloat`
(`OpCodeR` field offsets 12-19 / 8-11); the lowering pass MUST NOT write any
field that overlaps those bytes for the wide-immediate forms.

The defect is `double`-SPECIFIC: 3 `long` locals combined in the same shape
compute the correct result; only `double` triggers the silent wrong result.
Therefore the F-8 defect is in the R8 COMPUTATION path (the dump confirmed it
is in `LowerNeoOffsets`, NOT the type-spec / copy-prop / runtime), NOT in
8-byte-primitive frame-slot sizing or alignment:
`AllocateLocalStackSpaces` (`JITCompiler.cs:1561-1574`) sizes BOTH `double`
and `long` to 8 bytes (`GetPrimitiveSize`, `AppDomain.cs:1898-1921`) and
aligns BOTH to 8 (`AlignUp(offset, size)`, `size=8` for both) -- they receive
byte-identical frame slots. A future change that proposes to "fix F-8 via the
8-byte-primitive slot allocator / alignment" MUST be rejected on these grounds
(the slot sizing is provably not the discriminator). This record-keeping
mirrors the F-MAJ-1 `AllocateLocalStackSpaces`-monotonic-allocation finding:
prevent mis-attribution of the fix to logic that does not exist or is not the
defect.

The dump-confirmed root cause (apply, 2026-07-06, HEAD `fe13c25e`): in
`Optimizer.Neo.cs LowerNeoOffsets`, the immediate-branch case stamped
`op.Operand3 = localInfos[r1].RefOffset` for EVERY immediate branch (I4 / I8
/ R4 / R8). `Operand3` (`OpCodeR` field offset 16) overlaps the HIGH 4 bytes
of `OperandLong`/`OperandDouble` (offset 12-19), and is NEVER READ by any
immediate-branch runtime arm. The write is DEAD -- but DESTRUCTIVE for the
I8/R4/R8 forms: it clobbers the high 4 bytes of the 8-byte immediate
constant. The `long`-works / `double`-fails split arises because the
optimizer's constant-folding folds a `double` `Ldc_R8` constant INTO the
immediate form (`Bnei_Un_R8`) but leaves a `long` `Ldc_I8` in a register
(register-register `Bne_Un_I8`), so the I8 immediate-branch corruption is
unreachable for the long combine while the R8 form is reachable for the
double combine. The fix SKIPS the dead `Operand3` write for the I8/R4/R8
immediate-branch forms (their immediate lives at offset 12-19 / 8-11); the
I4 forms keep it byte-identical (their immediate `Operand` @8 does not
collide). A fix to the shared type-spec / copy-prop path MUST NOT ship (the
dump refuted those candidates: the R8 type-spec seeds are correct, the
emitted opcodes are `*_R8`, and copy-prop is width-correct).

#### Scenario: Two double locals combined in one boolean expression compute the correct result
- **WHEN** a method reads two `double` locals `a0`, `a1` (e.g. from a
  `double[]` via `Ldelem_R8`) and evaluates a combined
  `if (a0 != expected0 || a1 != expected1)` check that SHOULD be false (both
  locals hold their expected values)
- **THEN** the combined check MUST NOT raise a wrong-value fault (e.g.
  DivideByZero assertion trip)
- **AND** the JIT dump of the combine's compare/branch opcodes MUST show
  `*_R8` opcodes (e.g. `Ceq_R8` / `Bne_Un_R8`) -- NOT `*_I4` opcodes reading
  4 bytes of an 8-byte slot
- **AND** the dump of `registerTypes[]` for the two `double` locals' dest
  registers at the combine's type-specialization point MUST show `DoubleType`
  (NOT null / `IntType`)

#### Scenario: Each double local is individually correct in isolation
- **WHEN** the same method is reduced to a single `if (a0 != expected0)` check
  alone, and separately to a single `if (a1 != expected1)` check alone
- **THEN** each isolated check MUST pass after the fix
- **AND** (regression characterisation) on HEAD BEFORE the fix, the combined
  check FAILS while each isolated check PASSES -- proving the defect is in the
  COMBINE path (the second `double` read interferes with the first), NOT in
  the isolated `double` read

#### Scenario: Long locals combined work fine (the double-vs-long boundary)
- **WHEN** a method reads 3 `long` locals and combines them in one boolean
  expression (the documented control)
- **THEN** the check MUST pass on HEAD and after the fix (the I8 type-spec /
  I8 compare / I8 copy-prop are correct; the fix MUST NOT regress the I8 path)
- **AND** the fix MUST be scoped to the R8 path the dump confirms (NOT a
  blanket 8-byte-primitive change)

#### Scenario: 3+ double locals combined
- **WHEN** a method holds 3+ simultaneously-live `double` locals and combines
  all their derived checks in one boolean expression
- **THEN** all MUST be correct after the fix (stress on the combine path with
  multiple R8 operands)

#### Scenario: Double + long mix in one boolean expression
- **WHEN** a method combines a `double` local check and a `long` local check
  in one boolean expression
- **THEN** the check MUST compute correctly (the fix MUST NOT regress the long
  operand; the R8 and I8 widths MUST coexist correctly in one combine)

#### Scenario: Double + int mix in one boolean expression
- **WHEN** a method combines a `double` local check and an `int` local check
  in one boolean expression
- **THEN** the check MUST compute correctly (the R8 and I4 widths MUST coexist
  correctly in one combine)

#### Scenario: Double locals NOT combined (each isolated) still work
- **WHEN** a method reads two `double` locals but checks each in its OWN `if`
  (no combine)
- **THEN** each check MUST pass (the isolated `double`-read path is correct on
  HEAD and after the fix; the fix MUST NOT break it)

#### Scenario: Double local live range overlaps across an intervening method call
- **WHEN** a `double` local `a0` is read, an intervening method call touches
  the frame, then a second `double` local `a1` is read, and finally they are
  combined in one boolean expression
- **THEN** the combined check MUST compute correctly (the fix MUST NOT be
  order-dependent or rely on no-intervening-call)

#### Scenario: Single double local regression guard
- **WHEN** a method holds a SINGLE `double` local (read + check, no combine)
- **THEN** the existing single-`double`-read tests MUST still pass (the fix
  MUST NOT break the working single-local path)

#### Scenario: F-MAJ-1 probes and full NeoStep smoke stay green
- **WHEN** the fix is applied
- **THEN** the F-MAJ-1 regression probes (`NeoOptHardTest_Fmaj1_*`) MUST still
  pass
- **AND** the full `NeoStep` smoke MUST stay 161/161 (ZERO regressions; the
  fix is scoped to the R8 combine path)

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and runs the Legacy
  register VM
- **THEN** the Legacy NeoStep-filter smoke MUST show the SAME pre-existing
  failures with and without the fix (stash-toggle proof), because either (a)
  the fix is Neo-only (the runtime arm is in `ILIntepreter.Neo.cs`, which
  Legacy compiles out), or (b) the type-spec / copy-prop fix is gated
  `#if ENABLE_NEO_MODE` OR is byte-identical for every existing I4/I8 operand
  (only ADDING a correct R8 seed/fold that Legacy's `registerTypes` already
  has via its mature `ExecuteR`)

#### Scenario: Regression probes fail on HEAD without the fix, pass with it
- **WHEN** the F-8 regression tests (`NeoOptHardTest_Dbl_TwoDoubleCombine` and
  the 3-double variant) are run on HEAD with the fix stashed
- **THEN** at least the two-double-combine test and the three-double test MUST
  fail with a wrong-value result (DivideByZero)
- **AND WHEN** the fix is applied
- **THEN** all F-8 regression tests MUST pass

#### Scenario: Dump refutes the candidates -- requirement becomes DEFERRED
- **WHEN** an apply-time JIT dump shows the combine's compare/branch opcodes
  are already `*_R8` (refuting the type-spec-mis-types candidate), AND the R8
  copy-prop fold is width-correct (refuting the copy-prop candidate), AND
  `LowerNeoOffsets` does not overlap the R8 operands (refuting the lowering
  candidate), yet the symptom persists
- **THEN** this requirement becomes DEFERRED -- no fix SHALL ship
- **AND** the dump artifacts + the reproducer SHALL be pinned in the change so
  a future reproducing case has a ready home
- **AND** the `AllocateLocalStackSpaces`-slot-sizing-is-NOT-the-defect finding
  (double and long get byte-identical 8-byte slots) SHALL remain on record so
  an 8-byte-slot-allocator change is NOT mis-attributed as the fix

#### Scenario: 8-byte-primitive slot allocator is NOT the fix site (sizing hypothesis disproven)
- **WHEN** a future change is proposed to "fix F-8 via the 8-byte-primitive
  slot allocator / alignment in `AllocateLocalStackSpaces`"
- **THEN** that proposal MUST be rejected: `AllocateLocalStackSpaces`
  (`JITCompiler.cs:1561-1574`) sizes `double` and `long` BOTH to 8 bytes
  (`GetPrimitiveSize:1898-1921`) and aligns BOTH to 8 (`AlignUp(offset, size)`,
  `size=8` for both) -- they receive byte-identical frame slots. The F-8
  symptom (`long` works, `double` fails) CANNOT be a slot-sizing / alignment
  defect. The record-keeping in this requirement exists precisely to prevent
  that mis-attribution.
