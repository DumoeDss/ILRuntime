# Capability: neo-optimizer

Correctness invariants of the Neo code-path through the shared optimizer passes
(Forward Copy Propagation / Backward Copy Propagation / copy-prop) and the
conv/compare type-specialization. These are hard invariants the optimizer MUST
preserve for the Neo register VM (`ExecuteNeo`) to produce correct results.

> Status legend:
> - **ACTIVE** - the requirement is implemented and enforced; a regression is a
>   real bug.
> - **DEFERRED** - the requirement is tracked but NOT YET APPLICABLE on current
>   HEAD because the documented quirk could not be reproduced. No fix is
>   shipped; the requirement SHALL activate when a reproducing test case on
>   current HEAD is provided. A guessed fix MUST NOT be shipped.
> - **PROVISIONAL** - the requirement's root cause and fix are dump-gated; the
>   fix lands only when an apply-time JIT dump confirms the candidate defect.
>   Until then no fix ships; the requirement tracks the reproducer + dump
>   artifacts so they are not lost.

## Purpose

The Neo register VM shares its optimizer passes (`Optimizer.FCP.cs`,
`Optimizer.BCP.cs`, copy-prop) with the Legacy `ExecuteR` VM. Because these
passes run on every method, a correctness bug in them is silent and
method-wide. This capability records the invariants those passes MUST preserve
for the Neo path, and honestly tracks the optimizer quirks that surfaced during
the Neo Step 12b / Step 16 work but could not be reproduced on the current
HEAD, so a future reproducing case has a ready home.

## Requirements

### Requirement: FCP respects value-type copy independence after a field mutation

Forward Copy Propagation (FCP) MUST NOT propagate a field read of a
value-type-copy destination across a write to a field of the propagation
source (Status: ACTIVE - implemented and enforced). A whole value-type `Move`
(`S b = a`) creates an independent snapshot in `b`; an intervening `stfld`
that writes a field of the source `a` MUST invalidate any propagation that
rewrote a later `b.field` read to `a.field`.

This kill MUST apply when a `Ldloca`/`Ldloca_S` takes the address of the
propagation source (`xSrc`) or destination (`xDst`), because the local can
then be mutated through that address by a later `stfld`/`stind`. The base
local whose address is taken is the Ldloca source register (`op.Register2`),
which FCP scans as `ySrc`; the kill MUST fire when `ySrc == xSrc` or
`ySrc == xDst`. (The field store reaches the base indirectly via the
`ldloca` address handle, so a kill keyed on the `Stfld_*_Inline` owning
slot `Register1` cannot fire - `Register1` is the address temp, not the
base local. This was the corrected root cause; the original
`Stfld_*_Inline.Register1`-keyed kill was a no-op and was reverted.)

This requirement is Neo-only: it MUST be a no-op for Legacy `ExecuteR`. The
kill MUST be gated to opcodes/code-paths that exist only under
`ENABLE_NEO_MODE`, so the Legacy FCP control flow is byte-identical to before
this change.

#### Scenario: Copy then mutate source primitive field, read dest field
- **WHEN** a value-type copy `b = a` is followed by `a.n = <new value>` and a
  later read of `b.n`
- **THEN** the read of `b.n` MUST return the value `b.n` held at copy time
  (the original), NOT the mutated source value
- **AND** FCP MUST have killed the `b.n -> a.n` propagation at the `ldloca`
  of `a` (the propagation source), because the `stfld a.n` reaches `a`
  indirectly through that address handle

#### Scenario: Copy then mutate source reference field, read dest field
- **WHEN** a value-type copy `b = a` is followed by `a.s = <new ref>` and a
  later read of `b.s`
- **THEN** the read of `b.s` MUST return the reference `b.s` held at copy time
  (shallow copy: the original reference), NOT the mutated source reference

#### Scenario: Field write to an unrelated register does not kill
- **WHEN** a propagation `b = a` is live and a `stfld` writes a field of a
  register that is neither `a` nor `b`
- **THEN** the propagation MUST remain live (no spurious kill)

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and runs the Legacy
  register VM
- **THEN** FCP MUST behave identically to before this change (no field-write
  kill executes), because the kill is gated to Neo-only code paths that Legacy
  never compiles in

#### Scenario: Regression guard fails on HEAD without the fix, passes with it
- **WHEN** the K1 regression tests
  (`NeoOptHardTest_K1_FcpVtPropagation`,
  `NeoOptHardTest_K1_SourceAndDestAfterMutation`) are run on HEAD with the
  fix stashed
- **THEN** at least one MUST fail with a wrong-value result (the dest field
  reads the mutated source value)
- **AND WHEN** the fix is applied
- **THEN** all three K1 regression tests MUST pass

### Requirement: Q-STRUCT struct-mutation-then-read temp-renumber quirk is tracked

This quirk MUST remain a tracked, open item until a reproducing test case on
current HEAD is provided. The Q-STRUCT optimizer temp-renumber quirk is a
struct local + later field mutation + element/branch read pattern surfaced by
Step 16. Status: DEFERRED - not yet applicable on current HEAD; no fix
shipped. It MUST NOT be fixed by a guessed change to the shared BCP / copy-prop
passes. A fix SHALL land only when a test case that reproduces the quirk on
current HEAD accompanies it.

Suspect location pinned for recovery: BCP temp renumber around struct-local +
field-mutation patterns.

#### Scenario: Deferred until reproduced
- **WHEN** no test case reproduces the Q-STRUCT quirk on current HEAD
- **THEN** no optimizer change for this quirk SHALL be shipped
- **AND** the documented probe patterns (`ProbeQStruct_ElementReadAfterMutation`,
  `ProbeQStruct_BranchAfterMutation`) SHALL be retained as non-asserting
  documentation cases so the patterns stay under observation

#### Scenario: Activates on a reproducing case
- **WHEN** a test case that reproduces the Q-STRUCT quirk on current HEAD is
  provided
- **THEN** this requirement becomes ACTIVE
- **AND** a fix SHALL be scoped to the BCP / copy-prop pass (no guessing;
  the reproducing case is the gate)

### Requirement: Q-LONG conv.i8 long-compare quirk is tracked

This quirk MUST remain a tracked, open item until a reproducing test case on
current HEAD is provided. The Q-LONG quirk is a long default-zero compare
involving `conv.i8`, surfaced by Step 16. Status: DEFERRED - not yet
applicable on current HEAD; no fix shipped. It MUST NOT be fixed by a guessed
change to the conv/compare lowering or the shared optimizer. A fix SHALL land
only when a test case that reproduces the quirk on current HEAD accompanies it.

Suspect locations pinned for recovery: `conv.i8` emission and the long-compare
lowering (`Cgt_Un` / `Ceq_I8` / `Bne_Un_I8` arms).

#### Scenario: Deferred until reproduced
- **WHEN** no test case reproduces the Q-LONG quirk on current HEAD
- **THEN** no conv/compare or optimizer change for this quirk SHALL be shipped
- **AND** the documented probe patterns (`ProbeQLong_ConvI8ZeroCompare`,
  `ProbeQLong_ScalarZeroCompare`, `ProbeQLong_DefaultFieldZeroCompare`)
  SHALL be retained as non-asserting documentation cases

#### Scenario: Activates on a reproducing case
- **WHEN** a test case that reproduces the Q-LONG quirk on current HEAD is
  provided
- **THEN** this requirement becomes ACTIVE
- **AND** a fix SHALL be scoped to the conv/compare lowering or shared
  optimizer (no guessing; the reproducing case is the gate)

### Requirement: CLR value-type local representation agrees with the CLR struct return-write (F-MAJ-1)

A method holding 2+ simultaneously-live CLR value-type locals MUST compute
correct results: the local's value MUST NOT be silently corrupted by a
neighbouring CLR struct's return write (Status: ACTIVE -- dump-confirmed root
cause, fixed via Option B in `AllocateLocalStackSpaces`, gated
`#if ENABLE_NEO_MODE`). The Step-13b D6 CLR-struct return-write path writes
the return's flat managed bytes (`retSz` bytes via `WriteNeoValueType`) into the
caller's dest frame slot; the local-slot declaration for a CLR value-type local
MUST agree with that representation (flat-bytes sized by
`GetNeoValueTypeManagedSize` in Neo mode), so a struct return of size > 4 does
NOT overflow a 4-byte dest slot and corrupt the neighbouring local.

The `AllocateLocalStackSpaces` method (`JITCompiler.cs`) allocates a STRICTLY
MONOTONIC, non-overlapping `Offset` / `RefOffset` per surviving local/temp
register (the cursors only advance; there is NO slot-reuse / liveness logic in
this method). Therefore a "liveness-aware slot allocator" is NOT the F-MAJ-1
fix, and a future change MUST NOT mis-attribute the fix to slot-reuse logic
that does not exist. The dump-confirmed defect is a representation-consistency
gap between the D6 return-write (which writes the struct's flat managed bytes
via `WriteNeoValueType`) and the local-slot declaration (which was boxed-ref
`Size=4, RefCount=1`); the fix declares a CLR value-type LOCAL as flat bytes
(`Size = GetNeoValueTypeManagedSize`, `RefCount = 0`, `localIsRef = false`)
so the declaration AGREES with the D6 write and the D2 by-value-param read
(which byte-copies the same flat bytes out). Candidate (2) (`CleanupRegister`
compaction) was REFUTED by the dump (the two struct locals received distinct,
non-overlapping regions).

This requirement is enforced via Option B (declare-side). The fix is in
`AllocateLocalStackSpaces`, a SHARED method, gated `#if ENABLE_NEO_MODE` so
Legacy keeps its `Size=4, RefCount=1` path (Legacy never reaches the Neo
D6/D2 flat-bytes arms). Legacy-neutrality was confirmed by a stash-toggle
plain-`Debug` + `useRegister=true` NeoStep-filter run: the SAME 7 pre-existing
Legacy failures appear with and without the fix (byte-identical failure set).
Legacy `ExecuteR` is the REFERENCE, not a target.

Because the declare-side change re-declares EVERY Neo CLR value-type local as
flat bytes, EVERY runtime consumer arm that reads or writes a CLR-VT local's
slot MUST also operate on the flat-bytes representation (`Size = clrVtSize`,
`RefCount = 0`, `isRef = false`). The D6 return-write (`InvokeNeoClrMethod`,
`WriteNeoValueType` of `retSz` bytes into the dest `Offset`) and the D2
by-value-param read (`CLRMethod.Invoke` via `ReadNeoValueType`, byte-copied by
the optimizer's `CopyNeoCallArguments`) are flat-bytes by construction. The
adversarial review-loop confirmed three additional consumer arms that MUST be
flat-bytes-consistent: (1) the `Initobj` CLR-struct arm MUST zero the flat-bytes
region via `Unsafe.InitBlock(frameBase+DstOffset, 0, clrVtSize)` (NOT install a
boxed default at a stale `RefOffset`); (2) the `Box` CLR-struct arm MUST read
the flat managed bytes via `ReadNeoValueType` (NOT read a 4-byte mStack index);
(3) the `Unbox_Any` CLR-struct dest MUST write the flat managed bytes via
`WriteNeoValueType` (NOT install a boxed clone + index write). The
`Isinst`/`Castclass` arms are UNREACHABLE for a flat-bytes CLR-VT local (C#
always emits a prior `box`, so their operand is always a Box-result `object`
local holding an mStack index) -- the genuine boxed-ref path for Isinst,
Castclass, and the Unbox source read (a CLR struct sourced from Box/heap, NOT a
local) is UNAFFECTED and remains correct. A future change that touches any
runtime consumer of a CLR-VT local's slot metadata MUST verify it does not
re-introduce a boxed-ref assumption; the completeness sweep (19 arms: 3 fixed,
2 correct-no-fix, 14 correct-by-construction) is the precedent.

#### Scenario: Two simultaneously-live CLR struct locals compute correct values
- **WHEN** a method holds two CLR value-type locals `v`, `w` (each from a CLR
  method return) and computes `r1 = Sum(v); r2 = Sum(w)` followed by a combined
  `r1 != expected1 || r2 != expected2` check
- **THEN** both `r1` and `r2` MUST hold their expected values
- **AND** the combined check MUST NOT raise a wrong-value fault (e.g.
  DivideByZero assertion)
- **AND** the JIT dump of `frame.LocalInfos` MUST show the two locals' dest
  slots and the D6 return-write representation AGREE (boxed-ref write into a
  boxed-ref slot, OR flat-bytes write into a flat-bytes-sized slot) -- NOT a
  flat-bytes write into a 4-byte boxed-ref slot

#### Scenario: Each struct local is individually correct in isolation
- **WHEN** the same method is reduced to a single `if (r1 != expected1)` check
  alone, and separately to a single `if (r2 != expected2)` check alone
- **THEN** each isolated check MUST pass after the fix
- **AND** (regression characterisation) on HEAD BEFORE the fix, each isolated
  check FAILS -- proving the corruption is NOT an r1<->r2 cross-clobber but a
  neighbour-slot corruption observed by both reads

#### Scenario: Primitive (int) CLR returns are unaffected
- **WHEN** a method holds two CLR int-return locals with a combined check (the
  documented control)
- **THEN** the check MUST pass on HEAD and after the fix (the primitive-return
  path writes 4 bytes into a 4-byte slot; no representation gap exists, and the
  fix MUST NOT regress it)

#### Scenario: 3+ simultaneous CLR struct locals
- **WHEN** a method holds 3+ simultaneously-live CLR struct locals and checks
  all their derived values
- **THEN** all MUST be correct after the fix (stress on the overflow direction
  and the neighbouring-slot corruption chain)

#### Scenario: Live range overlaps across an intervening method call
- **WHEN** a CLR struct local `v` is constructed, an intervening method call
  touches the frame, then a second CLR struct local `w` is constructed, and
  finally `Sum(v)` is read
- **THEN** `Sum(v)` MUST return `v`'s original value (the fix MUST NOT be
  order-dependent or rely on no-intervening-call)

#### Scenario: CLR struct local reused after its last use -- no frame-size regression
- **WHEN** a CLR struct local `v` goes out of scope (last use passed) and a
  later CLR struct local `w` is declared in a disjoint scope
- **THEN** `w` MUST compute correctly (no corruption)
- **AND** the fix MUST NOT over-conservatively balloon the frame size: the
  monotonic `AllocateLocalStackSpaces` allocator already gives `w` a fresh slot
  (it never reused `v`'s), and this change MUST NOT disable that -- the frame
  size for this method MUST be unchanged by the fix

#### Scenario: Single CLR struct local regression guard
- **WHEN** a method holds a SINGLE CLR struct local (the Step-13b test shape:
  `Make -> local -> Sum`)
- **THEN** the existing Step-13b tests (`NeoStep13bClrStructByValueParamNoBinding`,
  `NeoStep13bClrStructReturnValueRoundTrip`, `NeoStep13bClrStructParamDistinctValue`)
  MUST still pass (the fix MUST NOT break the working single-local path)

#### Scenario: All consumer arms of a Neo CLR-struct local use the flat-bytes representation
- **WHEN** a Neo CLR value-type local (declared flat bytes: `Size = clrVtSize`,
  `RefCount = 0`, `isRef = false`) is consumed by a runtime arm that reads or
  writes its slot metadata
- **THEN** every such arm MUST operate on the flat-bytes representation: the
  `Initobj` arm zeroes `clrVtSize` flat bytes (`Unsafe.InitBlock`, no mStack /
  `RefOffset` touch); the `Box` arm reads flat managed bytes
  (`ReadNeoValueType`, no 4-byte mStack-index read); the `Unbox_Any` dest write
  writes flat managed bytes (`WriteNeoValueType`, no boxed-clone install)
- **AND** the `Isinst` / `Castclass` arms MUST be unreachable for a flat-bytes
  CLR-VT local (C# always emits a prior `box`; their operand is a Box-result
  `object` local) -- the genuine boxed-ref path for Isinst / Castclass / the
  Unbox source read (a CLR struct sourced from Box/heap, NOT a local) MUST
  remain unaffected and correct
- **AND** the regression probes (`NeoOptHardTest_Fmaj1_InitobjClrStruct`,
  `NeoOptHardTest_Fmaj1_BoxClrStructLocal`,
  `NeoOptHardTest_Fmaj1_IsinstClrStructLocal` which exercises `(T)obj` ->
  `unbox.any`, `NeoOptHardTest_Fmaj1_MixedFrameNoCrossCorruption`) MUST pass

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and runs the Legacy
  register VM
- **THEN** the Legacy NeoStep-filter smoke MUST show the SAME pre-existing
  failures with and without the fix (stash-toggle proof), because either (a)
  the fix is Neo-only (Option A: the D6 arm is in `ILIntepreter.Neo.cs`, which
  Legacy compiles out), or (b) the declare-side fix is gated
  `#if ENABLE_NEO_MODE` so the Legacy `AllocateLocalStackSpaces` control flow
  is byte-identical

#### Scenario: Regression guard fails on HEAD without the fix, passes with it
- **WHEN** the F-MAJ-1 regression tests (`NeoOptHardTest_Fmaj1_TwoClrStructLocals`
  and the isolation controls) are run on HEAD with the fix stashed
- **THEN** at least the combined-check test and the isolation controls MUST
  fail with a wrong-value result (DivideByZero)
- **AND WHEN** the fix is applied
- **THEN** all F-MAJ-1 regression tests MUST pass

#### Scenario: Dump refutes the candidates -- requirement becomes DEFERRED
- **WHEN** an apply-time JIT dump of `frame.LocalInfos` shows the two CLR struct
  locals' dest slots are DISTINCT and non-overlapping AND the D6 write size fits
  the declared slot (refuting candidate 1), AND `CleanupRegister` is confirmed
  not to renumber the live struct locals (refuting candidate 2), yet the symptom
  persists
- **THEN** this requirement becomes DEFERRED -- no fix SHALL ship
- **AND** the dump artifacts + the reproducer SHALL be pinned in the change so
  a future reproducing case has a ready home
- **AND** the `AllocateLocalStackSpaces` monotonic-allocation finding (no reuse
  logic exists) SHALL remain on record so a liveness-allocator is NOT
  mis-attributed as the fix

#### Scenario: AllocateLocalStackSpaces is NOT the fix site (liveness hypothesis disproven)
- **WHEN** a future change is proposed to "fix F-MAJ-1 via a liveness-aware
  slot allocator in `AllocateLocalStackSpaces`"
- **THEN** that proposal MUST be rejected: `AllocateLocalStackSpaces`
  (`JITCompiler.cs:1394-1587`) allocates a strictly monotonic, non-overlapping
  `Offset` / `RefOffset` per surviving local/temp register (cursors only
  advance); there is no slot-reuse logic in this method to "fix". The
  record-keeping in this requirement exists precisely to prevent that
  mis-attribution.

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
emitted opcodes are `*_R8`, and copy-prop is width-correct). Accepted-known:
the `immLarge` set includes R4 unnecessarily -- R4's `OperandFloat` (@8-11)
is disjoint from `Operand3` (@16), so the dead write was harmless for R4;
the broader set is safe (uniform "I4 keeps the legacy write, everything else
skips it"), just not minimal. The `OpCodeR` `[StructLayout(Explicit)]` union
overlap is a recurring sharp edge (3rd concrete instance after Step 12
frame-VT offsets and OPT-HARDEN K1 Move/Move_Vt): any `LowerNeoOffsets` case
that stamps an `OpCodeR` field MUST verify it does not alias a wide-immediate
field used by another consumer.

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

### Requirement: PatchEntry captures only T-identity operand sites, never byte offsets

The generic-method template mechanism SHALL record T-dependent opcode sites in
a `PatchEntry` table. A `PatchEntry` SHALL capture only T-IDENTITY-dependent
opcode FIELDS (the concrete type's type-token hash on `Initobj`/`Box`/`Unbox`/
`Unbox_Any`/`Isinst`/`Castclass`/`Newarr`/`Stobj`/`Ldobj`/`Constrained`/
`Ldelem_Any`/`Stelem_Any`; the resolved method-token hash on T-qualified
`Call`/`Callvirt`/`Call_Redirect`; the `Move` is-ref flag) at a fixed
instruction index. A `PatchEntry` MUST NOT capture a byte offset
(`DstOffset`/`SrcOffset`/`OperandOffset`/`Register1`/`Register2`/`Register3`),
because those are T-SIZE-dependent and CUMULATIVE (a change in a T-typed
slot's size shifts the offset of every subsequent slot), and MUST be re-derived
by re-running `AllocateLocalStackSpaces` + `LowerNeoOffsets` rather than
patched. A `PatchEntry.Field` SHALL be one of the STANDALONE `OpCodeR` fields
(`Operand`/`Operand2`/`Operand4`); it MUST NOT alias a wide-immediate field a
runtime consumer reads (the F-8 OpCodeR-union discipline). This split is grounded
in the per-occurrence JIT dump diff, which shows `Register1/2/3` are T-INVARIANT
across instantiations of the same generic definition while only `Operand`-level
fields and the lowered byte offsets vary.

#### Scenario: T=int vs T=long register body is identical
- **WHEN** the per-occurrence JIT compiles `T Fill<T>(T v, int n)` instantiated
  at `T=int` and at `T=long`
- **THEN** the two register-index `CodeBody` arrays MUST be identical (same
  length, same opcodes, same `Register1/2/3`, same `Operand`-level fields)
- **AND** the ONLY difference MUST be in `localInfos[0].Size` (the T-typed
  parameter: 4 vs 8 bytes) and the cumulative byte offsets derived from it
- **AND** therefore the PatchEntry tables for the two instantiations MUST
  contain NO byte-offset entries (the size difference is handled by re-running
  Allocate+Lower, not by a patch)

#### Scenario: T=object vs T=string lowered body is byte-identical (ref-share)
- **WHEN** the per-occurrence JIT compiles `T Fill<T>(T v, int n)` instantiated
  at `T=object` and at `T=string` (a body with NO T-identity type-token operand
  such as `Box T`/`Isinst T`)
- **THEN** the two `NeoExecuteBody` arrays MUST be byte-for-byte identical (same
  length, same opcodes, same operands INCLUDING the `Move` is-ref flag, same
  lowered offsets)
- **AND** therefore the two instantiations MUST share ONE cached body verbatim
  (no clone, no patch)

#### Scenario: T=struct adds Initobj and a type-token operand
- **WHEN** the per-occurrence JIT compiles `T Fill<T>(T v, int n)` instantiated
  at `T = an IL value type` (e.g. a 12-byte struct)
- **THEN** the register-index body MUST contain `Initobj` ops NOT present for
  `T=int` (the instruction COUNT differs)
- **AND** those `Initobj` ops MUST carry the concrete struct's type-token hash
  in `Operand` (a Kind-A `PatchEntry` site: `Field=Operand, Kind=TypeToken`)
- **AND** the PatchEntry extractor MUST record those sites so a re-instantiation
  at a DIFFERENT value type re-derives the correct struct token

#### Scenario: PatchEntry field never aliases a wide immediate
- **WHEN** the PatchEntry extractor records a site on an opcode whose
  `OpCodeREnum` is an immediate-branch or wide-immediate form (e.g. `Bnei_Un_R8`,
  `Ldc_I8`)
- **THEN** the recorded `PatchField` MUST be a standalone field whose byte range
  does not overlap a wide-immediate field the runtime reads
- **AND** the extractor MUST verify disjointness per opcode kind (the F-8
  `LowerNeoOffsets` discipline), never defaulting to `Register1/2/3` (which
  alias the byte offsets post-lowering)

#### Scenario: Constrained type-token is captured from the constrained. prefix's own TypeReference
- **WHEN** a generic method body contains a `constrained.callvirt T.M` pair
  (e.g. `T.GetHashCode()`/`T.Equals(...)`/`IComparable<T>::CompareTo(...)`,
  emitted as a `Constrained` op immediately followed by the callvirt)
- **THEN** the `Constrained` PatchEntry's `CecilToken` MUST be the `constrained.`
  CIL prefix's OWN `TypeReference` (the concrete-T type), NOT the trailing
  callvirt's `MethodReference`
- **AND** a re-instantiation at a DIFFERENT T MUST re-resolve the Constrained
  type-token to the new concrete-T hash (via `GetTypeTokenHashCode`), so the
  runtime Constrained arm (`AppDomain.GetType(ip->Operand)`) dispatches on the
  CORRECT type -- NOT a stale capture-instance hash
- **AND** where the paired callvirt's method token is itself T-qualified (e.g.
  `IComparable<T>::CompareTo`), the trailing callvirt's method-token MUST ALSO
  be recorded as a `MethodToken` PatchEntry (on the callvirt's
  `Operand2`, the field the runtime reads at `cv->Operand2`) and re-resolved per
  concrete T via `GetMethodTokenHash`
- **AND** a NON-constrained generic-method call whose resolved `IMethod` hash
  does NOT vary with T (the call resolves to one `ILMethod` regardless of T)
  MUST be T-invariant and require NO patch (the default `continue` arm of the
  extractor is correct for this case)

### Requirement: CloneAndPatch output is structurally equivalent to the per-occurrence JIT body

`CloneAndPatch(template, concreteTypeArgs)` SHALL produce a `NeoExecuteBody`
+ frame layout structurally equivalent, for a generic method definition with a
cached template, to the body the per-occurrence JIT (`JITCompiler.Compile`
via `MakeGenericMethod`) produces for the same instantiation: identical length,
identical `OpCodeREnum` per index, and identical operands modulo the expected
T-derived values. The template body SHALL be the register-index `OpCodeR[]`
captured AFTER `CleanupRegister` and BEFORE `TypeSpecializeNeoOpcodes` (the
latest T-invariant artifact in the Compile pipeline). `CloneAndPatch` SHALL
re-run the T-dependent back-half (`TypeSpecializeNeoOpcodes` +
`AllocateLocalStackSpaces` + `LowerNeoOffsets`) on a clone of the template body
with the concrete type args. For a struct-T instantiation, `CloneAndPatch` SHALL
rebuild the auto-`Initobj` prefix (a front-half step that prefix-initialises each
struct-T local/param) and SHALL shift EVERY resolved body-index target that lands
after the prefix by the prefix `delta` -- branch `Operand`s, `SwitchTargets`, the
`addr[]` exception-handler map, AND the EH `Leave`/`Leave_S` targets. The EH
`Leave`/`Leave_S` opcodes are NOT classified as branching by
`Optimizer.IsBranching` / `IsIntermediateBranching` (EH control-flow is resolved
separately via `addr[]`), but their `Operand` IS a resolved body index that
shifts with the prefix; a `Leave`/`Leave_S` target MUST NOT be left unshifted or
the struct-T EH body diverges from the per-occurrence JIT body. This is the V1
equivalence anchor and the load-bearing correctness gate for Step 22 (there is
no FAIL-on-HEAD functional probe because the per-occurrence JIT already runs
every generic method correctly).

#### Scenario: CloneAndPatch body equals per-occurrence JIT body
- **WHEN** a generic method is instantiated at a concrete T (a primitive, an
  8-byte primitive, an IL struct, a reference type) and BOTH paths produce a
  body -- `CloneAndPatch(def.Template, T)` and `Compile(MakeGenericMethod(T))`
- **THEN** the two `NeoExecuteBody` arrays MUST be structurally equal (same
  length, same `Code`/`Register1/2/3`/`Operand`/`Operand2/3/4` per index -- all
  24 bytes of `OpCodeR`), as asserted by a host-side comparator
- **AND** the two frame layouts (`TotalStructSize`, `TotalRefSize`,
  `localInfos[].Offset/RefOffset/Size/RefCount`) MUST be identical

#### Scenario: Equivalence holds across the T-profile matrix
- **WHEN** CloneAndPatch is exercised over a matrix of generic-method shapes
  (a T-typed local/param/return; a `T[]` + `ref T` variant; a nested generic;
  a generic method on a generic type; a `constrained.callvirt T.M` such as
  `GetHashCode`/`Equals`; a T-qualified `IComparable<T>::CompareTo` callvirt;
  a struct-T combined with `if`/`switch`/exception-handler control-flow) and
  concrete T's spanning primitive / 8-byte-primitive / IL-struct / reference-type
- **THEN** the V1 structural equivalence MUST hold for every cell of the matrix
  (the round-1 matrix is 11 methods x 5 T = 55 cells, including `HashIt`/
  `EqualsIt` for the Constrained type-token, `CompareThem` for the T-qualified
  method-token, and `BranchIt`/`SwitchIt`/`TryCatch` for struct-T + control-flow
  / EH `Leave` delta-shift)
- **AND** any cell that FAILS equivalence MUST fall through to the per-occurrence
  JIT path (correctness preserved; only the optimization is lost), not produce a
  wrong result

#### Scenario: struct-T exception-handler Leave targets shift with the Initobj prefix
- **WHEN** a generic method with an exception handler (try/catch) is instantiated
  at `T = an IL value type` (Initobj prefix growth, `delta > 0`)
- **THEN** the CloneAndPatch delta-shift MUST shift the EH `Leave`/`Leave_S`
  `Operand` targets by the prefix `delta` (alongside the branch `Operand`s,
  `SwitchTargets`, and `addr[]` map)
- **AND** the resulting body MUST be byte-identical to the per-occurrence JIT
  body (the `TryCatch<Struct>` V1 cell), NOT leave any `Leave`/`Leave_S` target
  unshifted

#### Scenario: Functional roundtrip via both paths yields identical results
- **WHEN** a generic method is invoked via the template path and via the
  per-occurrence path with identical inputs
- **THEN** the observable results MUST be identical (the V2 sanity gate)

### Requirement: ref-type-share and value-type-CloneAndPatch discrimination at instantiation

The instantiation-time discrimination SHALL be: if EVERY concrete generic
argument is a reference type AND the cached template's patch table contains no
T-identity `TypeToken`/`MethodToken` site whose value would differ across
reference types, the instantiation SHALL share ONE cached "ref body" verbatim
(no clone); otherwise (any value-type arg, or a token-bearing ref body) the
instantiation SHALL `CloneAndPatch`. The discriminator SHALL read the concrete
`IType[]` (available at instantiation from the `BodyRegister` getter of a
generic-instance `ILMethod` / `ILMethod.GenericArugmentsArray`).

#### Scenario: All reference-type args share one body
- **WHEN** a generic method is instantiated at `T=object`, `T=string`,
  `T=SomeILClass` (all reference types) and the body has no T-identity token
- **THEN** all three instantiations MUST reference the SAME cached `NeoExecuteBody`
  array instance (no per-instance clone)

#### Scenario: Any value-type arg forces CloneAndPatch
- **WHEN** a generic method is instantiated at `T=int` (or any value type)
- **THEN** the instantiation MUST NOT share the ref body; it MUST produce its
  own `CloneAndPatch`-derived body (the frame byte size differs)

#### Scenario: Token-bearing ref body is cloned-and-patched, not shared
- **WHEN** a generic method body contains a T-identity token (e.g. `Box T`, or a
  `constrained.callvirt T.M` whose Constrained type-token / paired method-token
  varies across reference types) and is instantiated at `T=object` vs `T=string`
- **THEN** the two instantiations MUST NOT share a body (the type-token /
  method-token operand differs); each MUST `CloneAndPatch` so the concrete token
  is patched

### Requirement: Template cache is additive, Neo-only, and Legacy-neutral

The template mechanism SHALL be an ADDITIVE cache layer: the per-occurrence JIT
instantiation path (`MakeGenericMethod` -> `JITCompiler.Compile`) SHALL remain
as the reference and the fallback when no template is cached for a definition.
The template field, the `BodyRegister`-getter hook, the `PatchEntry` struct, and
`CloneAndPatch` SHALL all be gated `#if ENABLE_NEO_MODE` (Neo-only). Legacy
`ExecuteR` (`ILIntepreter.Register.cs`) MUST be byte-identical to before this
change: the template code SHALL compile out under plain `Debug`, and a stash-
toggle plain-`Debug` + `useRegister=true` NeoStep-filter run MUST show the SAME
pre-existing Legacy failure set with and without the change. The full NeoStep
smoke (205/205, including the Step-22 V2 functional test) + NeoOptHardening
(24/24) + NeoStep20 (9/9) MUST stay green (template path must not change any
existing JIT behavior).

#### Scenario: Per-occurrence JIT path stays as the fallback
- **WHEN** a generic-instance `ILMethod` has no cached template for its
  definition (e.g. template building was skipped or failed)
- **THEN** the `BodyRegister` getter MUST fall through to the per-occurrence
  `JITCompiler.Compile` path, producing the same body as before this change

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and runs the Legacy
  register VM
- **THEN** the Legacy NeoStep-filter smoke MUST show the SAME pre-existing
  failures with and without this change (stash-toggle proof), because the
  template field, the `BodyRegister` hook, and `CloneAndPatch` are all gated
  `#if ENABLE_NEO_MODE` and compile out

#### Scenario: NeoStep regression smoke stays green
- **WHEN** the template mechanism is enabled (`Debug_Neo`) and the full NeoStep
  smoke is run
- **THEN** NeoStep MUST stay 205/205, NeoOptHardening 24/24, NeoStep20 9/9
  (ZERO regressions; the template path is additive and does not change existing
  JIT behavior for non-generic or already-cached methods)

### Requirement: NeoAssembly .neo format serializes OpCodeR[] as raw 24-byte little-endian records under a versioned header

The `.neo` serializer SHALL persist each `OpCodeR` (`ILRuntime/Runtime/
Intepreter/OpCodes/OpCode.cs`, `[StructLayout(LayoutKind.Explicit)]`, 24 bytes)
as its raw 24 bytes via `MemoryMarshal.AsBytes<OpCodeR>` on write and
`MemoryMarshal.Cast<byte, OpCodeR>` on read, little-endian. The serializer
SHALL NOT use field-by-field encoding for the body, because the explicit-layout
aliases (`Register1`/`DstOffset` @4, `Register2`/`SrcOffset` @6, `Register3`/
`OperandOffset`/`Register4`/`Operand`/`OperandFloat` @8-11, `Operand2`/
`OperandLong`/`OperandDouble` @12-19, `Operand3` @16, `Operand4` @20) mean a
raw dump captures every variant without per-opcode canonical-field selection,
and field-by-field would reintroduce the OpCodeR-union aliasing defects
documented under F-8 / OPT-HARDEN-K1. The file SHALL begin with a header
`{ Magic "ILRN" (0x494C524E), Version, Endianness, TableOffsets[] }`; the
deserializer SHALL reject a file whose Magic is not "ILRN" or whose Version
exceeds the reader's supported maximum. The raw encoding is version-fragile by
construction -- the header `Version` field is the sole guard, and a future
`OpCodeR` layout change (24 -> N bytes) MUST bump `Version`.

#### Scenario: Raw 24-byte OpCodeR roundtrips byte-for-byte
- **WHEN** a compiled method's `NeoExecuteBody` (`OpCodeR[]`) is serialized to a
  `.neo` stream and deserialized back
- **THEN** the deserialized `OpCodeR[]` MUST be byte-for-byte identical to the
  original (`MemoryMarshal.AsBytes` equality over all 24 bytes per record,
  length included)
- **AND** the equality MUST hold for opcode kinds that use the aliasing fields
  (an `Ldc_I8` carrying `OperandLong` @12-19, an `Ldc_R8` carrying
  `OperandDouble` @12-19, a `Bnei_Un_R8` carrying its 8-byte immediate, a
  `Move_Vt` carrying `Register1`/`Register2` byte offsets @4/@6, a `Callvirt`
  carrying `Operand4` packed flags @20)

#### Scenario: Wrong magic / version rejected
- **WHEN** a reader is given a stream whose first 4 bytes are not "ILRN" (e.g. a
  HybridPatch stream with Magic 0x58883551)
- **THEN** the reader MUST throw a clear "not a .neo file" error without reading
  further
- **AND WHEN** a reader is given a `.neo` stream whose `Version` exceeds the
  reader's supported maximum
- **THEN** the reader MUST throw a "newer .neo version" error

### Requirement: NeoAssembly reuses HybridPatch reference tables for type/method/field/string indices

The serializer SHALL reuse HybridPatch's `TypeReferencePatchInfo`,
`MethodReferencePatchInfo`, and `FieldReferencePatchInfo` record types and their
`WriteToStream`/`FromStream` implementations from `ILRuntime/HybridPatch/PatchInfo/
AssemblyInfo.cs` for the `.neo` StringTable, TypeRefTable, MethodRefTable, and
FieldRefTable (design doc section 8.1: "Neo extends HybridPatch"). The serializer
SHALL NOT reinvent these tables. Each `TypeReferencePatchInfo`-derived record in the `.neo` file SHALL
carry an IL-vs-CLR discriminator so the Step-25 loader can resolve IL types via
`AppDomain.LoadedTypes[fullName]` and CLR types via assembly-qualified name. The
token-bearing `OpCodeR` operands (type-token hash on `Box`/`Isinst`/`Castclass`/
`Initobj`/`Newarr`/`Constrained` in `Operand`; method-token hash on `Call`/
`Callvirt`/`Ldftn` in `Operand2`; string token on `Ldstr` in `OperandLong`) are
serialized verbatim as part of the raw `OpCodeR` record (they are runtime
hashes); the reference tables exist so a future Step-25 loader can rebuild the
`hash -> IType/IMethod/string` maps. Cross-AppDomain hash re-resolution is
explicitly DEFERRED to Step 25.

#### Scenario: Reference tables reuse HybridPatch record types
- **WHEN** the `.neo` writer builds the TypeRefTable / MethodRefTable /
  FieldRefTable
- **THEN** it SHALL route Cecil `TypeReference`/`MethodReference`/`FieldReference`
  objects through the SAME `TypeReferencePatchInfo.Create` /
  `MethodReferencePatchInfo.Create` / `FieldReferencePatchInfo.Create` factories
  HybridPatch uses
- **AND** the serialized records SHALL be readable by the corresponding
  `FromStream` methods (structural fidelity)

#### Scenario: Reference tables roundtrip structurally
- **WHEN** a `.neo` stream's reference tables are written and read back
- **THEN** the deserialized `TypeReferencePatchInfo[]` / `MethodReferencePatchInfo[]`
  / `FieldReferencePatchInfo[]` / `string[]` MUST equal the originals
  field-for-field (including arrays, byrefs, generic-instances, generic-parameter
  decomposition)

### Requirement: CompiledFrame serialization persists every load-bearing field for a runnable frame

The `.neo` MethodDefTable SHALL serialize, per IL method, the load-bearing
fields of `CompiledFrame` (`JITCompiler.cs:74-107`): `NeoExecuteBody` (`OpCodeR[]`,
the lowered body `ExecuteNeo` runs), `LocalInfos` and `ParamInfos`
(`StackSlotInfo[]` = `{Offset, RefOffset, Size, RefCount}`), `TotalStructSize`,
`TotalRefSize`, `ParamPrimitiveSize`, `ParamReferenceCount`,
`LocalsPrimitiveSize`, `LocalsReferenceCount`, `ReturnPrimitiveSize`,
`ReturnRefCount`, `StackRegisterCount`, `LocalIsReference[]`,
`NeoCatchException{RegIndex,ByteOffset,RefOffset}`, `SwitchTargets`
(`Dictionary<int,int[]>`), `NeoCallParamMap[]` (ushort arrays + `bool[]` flags
+ CLR `System.Type[]` element types serialized by assembly-qualified name), and a
structured `ExceptionHandler[]` table with try/handler ranges as BODY INDICES
plus a catch-type TypeRef index. The serializer SHALL NOT serialize `CodeBody`
(the register-index body; only consumed by the inliner/debugger, out of AOT
scope) NOR `Symbols` (keyed by Cecil `Instruction`, not serializable; consumed
only by the Step-22 extractor at serialize-time and the debugger). The EH table
SHALL re-represent the Cecil-keyed `addr[]` map as body-index ranges, because
`addr[]` is not serializable and Step 25's loader must rebuild the runtime EH
lookup from indices without Cecil.

#### Scenario: CompiledFrame roundtrips field-for-field
- **WHEN** a compiled method's `CompiledFrame` is serialized and deserialized
- **THEN** every load-bearing field listed above MUST equal the original
  (`StackSlotInfo[]` field-for-field on `{Offset, RefOffset, Size, RefCount}`;
  scalar sizes/counts equal; `LocalIsReference[]` equal; `NeoCatchException*`
  equal; `SwitchTargets` dictionary equal key-for-key and value-for-value;
  `NeoCallParamMap[]` equal including the `PrimitiveByRefElemType` CLR types
  resolved back from assembly-qualified names)
- **AND** `NeoExecuteBody` MUST be byte-for-byte identical (per the raw-OpCodeR
  requirement)

#### Scenario: Exception handler table roundtrips as body indices
- **WHEN** a method with a try/catch handler is serialized
- **THEN** the EH record SHALL carry `TryStartIdx`/`TryEndIdx`/
  `HandlerStartIdx`/`HandlerEndIdx`/`FilterIdx` as BODY INDICES (resolved via
  the JIT-time `addr[]` map at serialize time), `HandlerType` as the Cecil
  `ExceptionHandlerType` int, and `CatchTypeRefIdx` as a TypeRefTable index
  (-1 for finally/fault)
- **AND** the deserialized EH table MUST equal the original structurally
  (ranges, type, catch-type ref)

#### Scenario: Methods with diverse frame shapes roundtrip
- **WHEN** the roundtrip matrix covers a non-generic method, a generic method
  (with a template), a method with an exception handler, and methods with
  various locals/params/refs (primitive locals, IL value-type locals, CLR
  value-type locals, byref params, ref-returning methods)
- **THEN** every cell MUST roundtrip field-for-field (the matrix exercises the
  `StackSlotInfo` Size/RefCount variation, the `LocalIsReference` flag, the
  `NeoCallParamMap` byref paths, and the EH table)

### Requirement: GenericMethodTemplate serialization is faithful (every T-identity site, not just the Initobj prefix)

The `.neo` TemplateTable SHALL serialize each cached `GenericMethodTemplate`
(`GenericMethodTemplate.cs:83`) faithfully: `TemplateBody` (raw `OpCodeR[]`),
`Patches[]` (every `PatchEntry` the Step-22 extractor recorded, with the
`CecilToken` `object` re-resolved to a TypeRef/MethodRef table index by
`PatchKind`), the front-half scalars (`LocVarRegStart`, `TotalRegCnt`,
`NeoCatchExRegFinal`, `StackRegisterCount`, `VarCnt`), the `SwitchTargets`, the
`InitObjPrefixLength` + `InitObjPrefixRegisters[]` (the prefix, rebuilt from
these at instantiate time), `VariableTypes` (as TypeRef indices),
`ConstrainedTypeTokens` (as TypeRef indices), and `ConstrainedMethodTokens` (as
MethodRef indices). The serializer MUST capture EVERY T-identity site the
extractor records -- every IL-source `Initobj` (`Operand2==0`), every `Box`/
`Unbox`/`Unbox_Any`/`Isinst`/`Castclass`/`Newarr`/`Stobj`/`Ldobj` T-token, and
every `Constrained` T-type-token + its trailing T-qualified callvirt
method-token. The serializer MUST NOT assume the auto-`Initobj` prefix is the
only `Initobj` site (the Step-22 `CallIt<Struct>` follow-up: the inliner can
insert an `Initobj` for struct temps that the prefix-rebuild does not
reproduce; any such non-prefix `Initobj` T-token the extractor records as a
patch MUST be serialized as a patch). `Symbols`, `Addr`, `RefBody`, and
`RefBodyAddr` SHALL NOT be serialized (Cecil-keyed or runtime cache; the
ref-share is rebuilt by Step 25's loader via the same all-ref-and-no-token
discrimination).

#### Scenario: GenericMethodTemplate roundtrips structurally
- **WHEN** a cached template (built via the Step-22 capture path) is serialized
  and deserialized
- **THEN** `TemplateBody` MUST be byte-for-byte identical (raw OpCodeR)
- **AND** the `Patches[]` array MUST equal the original element-for-element on
  `{InstrIdx, Field, Kind, GenericParamIdx}` and each `TokenRefIdx` MUST resolve
  to the same TypeRef/MethodRef the original `CecilToken` routes to (by `Kind`)
- **AND** the front-half scalars, `SwitchTargets`, `InitObjPrefixRegisters`,
  `VariableTypeRefIdxs`, `ConstrainedTypeRefIdxs`, `ConstrainedMethodRefIdxs`
  MUST equal the originals

#### Scenario: Every T-identity patch site is captured
- **WHEN** a generic method body contains T-identity sites beyond the Initobj
  prefix (e.g. an IL-source `Initobj T` not in the prefix, a `Box T`, a
  `constrained.callvirt T.GetHashCode`, a `constrained.callvirt
  IComparable<T>::CompareTo` with a T-qualified method token)
- **THEN** the serialized `Patches[]` MUST contain a `PatchEntry` for EACH such
  site (the extractor's full output), with the correct `Field`/`Kind`/
  `GenericParamIdx`/`TokenRefIdx`
- **AND** the prefix-only `InitObjPrefixRegisters` MUST be disjoint from the
  IL-source `Initobj` patch sites (they are different mechanisms -- prefix vs
  patch)

### Requirement: NeoAssembly serialization is additive, Neo-only, and Legacy-neutral

The `.neo` serializer/deserializer SHALL be entirely additive new code under a
new `ILRuntime/Runtime/NeoAOT/` namespace, gated `#if ENABLE_NEO_MODE`. It
SHALL NOT modify the JIT compiler, `ExecuteNeo`, the optimizer, the Step-22
template mechanism, or any `ILType` field-layout logic; it only READS the
public/internal fields these already expose. Any new accessor needed on
`ILType`/`CompiledFrame`/`GenericMethodTemplate` for the serializer SHALL be
gated `#if ENABLE_NEO_MODE` (additive, no behavior change). Legacy `ExecuteR`
(`ILIntepreter.Register.cs`) SHALL be byte-identical to before this change: the
whole `NeoAOT/` namespace SHALL compile out under plain `Debug`, and a
stash-toggle plain-`Debug` + `useRegister=true` NeoStep-filter run MUST show the
SAME pre-existing Legacy failure set with and without the change. The full
NeoStep smoke (205/205) + NeoOptHardening (24/24) + NeoStep20 (9/9) MUST stay
green (the serializer does not change any existing JIT/runtime behavior).

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and runs the Legacy
  register VM
- **THEN** the Legacy NeoStep-filter smoke MUST show the SAME pre-existing
  failures with and without this change (stash-toggle proof), because the entire
  `NeoAOT/` namespace and any new accessors are gated `#if ENABLE_NEO_MODE` and
  compile out

#### Scenario: NeoStep regression smoke stays green
- **WHEN** the serializer is enabled (`Debug_Neo`) and the full NeoStep smoke is
  run
- **THEN** NeoStep MUST stay 205/205, NeoOptHardening 24/24, NeoStep20 9/9
  (ZERO regressions; the serializer is additive and reads existing structures)

#### Scenario: V1 roundtrip self-check passes for the full matrix
- **WHEN** the host-side V1 roundtrip self-check (DEBUG+Neo, mirroring
  `NeoStep22SelfCheck`) compiles each matrix method, serializes to an in-memory
  stream, deserializes, and compares
- **THEN** every matrix cell MUST pass: `OpCodeR[]` byte-for-byte, `CompiledFrame`
  field-for-field, type layout identical, `GenericMethodTemplate` faithful
- **AND** any cell that fails MUST be reported with a structural diff (per-index
  opcode / per-field mismatch), not a silent skip

### Requirement: ilrt_neoc is a standalone precompile CLI that bulk-compiles an IL assembly to a .neo via the Step-22 template + Step-23 serializer layers

The `ilrt_neoc` tool SHALL be a standalone console-app project (`ILRuntimeNeoCompiler/`)
SEPARATE from `PatchTool` and `ILRuntimeTestCLI` (design doc section 8.3: "ilrt_neoc
is independent from PatchTool"). The tool SHALL build standalone via `dotnet build
ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj -c Debug_Neo` and SHALL NOT be
added to `ILRuntime.sln` (the sln cannot build whole -- the VS2022 debugger VSIX
NU1201). The tool's argument surface SHALL be positional:
`ilrt_neoc <input.dll> <output.neo> [reference-assembly paths...]`. The tool SHALL
be a thin wrapper: it SHALL parse args, open streams, invoke a single public
`NeoCompiler.Compile(...)` entry, print the compile report, and map the outcome to
an exit code (`0` = every method + template compiled; `2` = one or more methods
skipped, `.neo` still written; `1` = fatal, no `.neo`). The tool project SHALL
contain NO runtime / JIT / serializer / template logic -- all compile logic SHALL
live in the `public NeoCompiler` driver class inside the ILRuntime assembly
(`ILRuntime/Runtime/NeoAOT/NeoCompiler.cs`, gated `#if ENABLE_NEO_MODE`), which is
the single public seam that reaches the internal NeoAOT + JIT + template
machinery. This whole requirement is Neo-only: the `NeoCompiler` class + the tool
SHALL compile out of plain `Debug`, so Legacy `ExecuteR` is byte-identical.

#### Scenario: Standalone tool builds and runs without the sln
- **WHEN** `dotnet build ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj -c Debug_Neo`
  is run on a clean tree
- **THEN** the build SHALL succeed with zero errors (transitively building
  `ILRuntime.csproj`)
- **AND** running `ILRuntimeNeoCompiler.exe <input.dll> <out.neo>` on a small IL
  assembly SHALL produce a `out.neo` whose first 4 bytes are the `.neo` Magic
  `0x494C524E` ("ILRN") and whose header `Version` is the Step-23 version
- **AND** the tool SHALL NOT require the `ILRuntime.sln` to build (it builds by
  project file alone)

#### Scenario: Arg-parse + exit-code contract
- **WHEN** the tool is invoked with fewer than 2 positional args, or a non-existent
  input path
- **THEN** it SHALL print a usage message and return exit code `1` without creating
  the output file
- **WHEN** the tool is invoked on a valid input where every IL method compiles
- **THEN** it SHALL return exit code `0` and the output `.neo` SHALL contain every
  non-generic IL method in the `MethodDefTable` and every generic-method definition
  in the `TemplateTable`
- **WHEN** the tool is invoked on an input where some methods throw during compile
  (e.g. a `NotImplementedException` for an unimplemented op)
- **THEN** it SHALL NOT abort; it SHALL skip those methods, emit the `.neo` with the
  compiled subset, report each skipped method (declaring-type-full-name.method-name +
  exception type + message) to stderr, and return exit code `2`

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and the Legacy register VM
  runs the NeoStep-filter smoke
- **THEN** the smoke SHALL show the SAME pre-existing Legacy failure set with and
  without this change (stash-toggle proof), because the `NeoCompiler` class is gated
  `#if ENABLE_NEO_MODE` and the `ILRuntimeNeoCompiler/` project is a separate tool
  the runtime never references

### Requirement: The NeoCompiler driver enumerates every IL method, force-compiles via the Step-23 writer, and captures every generic-method template via synthesized instantiation -- additive and Legacy-neutral

The `NeoCompiler` driver SHALL enumerate every IL type in the INPUT module only
(filtering `appdomain.LoadedTypes` by `TypeDefinition.Module == inputModule`, so
reference-assembly types are compiled-against but NOT emitted), and for each type
SHALL enumerate both `GetMethods()` and `GetConstructors()`. The driver SHALL
partition methods into non-generic (emitted to the `MethodDefTable` via
`NeoAssemblyWriter.Write`, which `CompileFresh`-compiles each) and generic-method
DEFINITIONS (`GenericParameterCount > 0 && !IsGenericInstance`). A generic-method
definition SHALL be emitted to the `TemplateTable` by SYNTHESIZING a capture-
eligible instantiation -- one primitive (`AppDomain.IntType`) per generic parameter
via `definition.MakeGenericMethod(IType[])`, then triggering the existing
`InitCodeBody` capture hook (reading the instance's `BodyRegister`) so
`StoreGenericTemplate` caches the template on
`definition.GenericMethodTemplateCache`. The driver SHALL NOT compile the open
generic definition directly (Step 22 REJECTED this -- it corrupts shared AppDomain
caches); synthesis is the only path. The driver SHALL NOT pass a generic definition
to `NeoAssemblyWriter.Write`'s `methods[]` (that would compile the open definition).
The driver SHALL force-compile each non-generic method inside a per-method try/catch
BEFORE handing the survivor set to `Write`, so a method that throws during compile
is recorded as a skip (method display name + exception type + message) and omitted,
NOT propagated. The driver SHALL NOT modify `NeoAssemblyWriter`, the JIT, the
optimizer, the Step-22 template mechanism, or the runtime; it is a new caller of
existing, unchanged internals.

#### Scenario: Every IL method and every generic definition in the input is emitted
- **WHEN** `NeoCompiler.Compile` is run on an input assembly containing a mix of
  non-generic methods (instance + static + instance ctor + static `.cctor`) and
  generic-method definitions, across top-level and nested types
- **THEN** the resulting `.neo` `MethodDefTable` SHALL contain EVERY non-generic IL
  method (including constructors and static constructors, including methods on
  nested types)
- **AND** the `TemplateTable` SHALL contain a template for EVERY generic-method
  definition in the input module
- **AND** the `MethodDefTable` SHALL contain NO generic-method definition (the
  generic/non-generic split is enforced)
- **AND** the `TypeDefTable` SHALL contain every IL type in the input module and NO
  type from a reference assembly (the input-module filter holds)

#### Scenario: Template capture via synthesized instantiation, no call site needed
- **WHEN** the input contains a generic method `T M<T>(T v, int n)` with NO call site
  in the assembly (no concrete instantiation)
- **THEN** the driver SHALL synthesize `M<int>` (one `int` per generic parameter),
  trigger the capture, and emit a template whose `TemplateBody` is the T-invariant
  register-index body
- **AND** the template's `Patches[]` SHALL equal what the in-process Step-22
  `ExtractPatches` produces for that definition (the synthesized-`int` capture path
  is the same `InitCodeBody` hook a real `M<int>` call site would take)
- **AND** the driver SHALL NOT compile the open `M<T>` definition directly (no
  cache-corrupting open-definition compile occurs)

#### Scenario: A method that throws during compile is skipped, not fatal
- **WHEN** a non-generic method's compile throws (e.g. `NotImplementedException` for
  an unimplemented op) and other methods compile cleanly
- **THEN** the driver SHALL record the throwing method in `NeoCompilerResult.Skipped`
  (method display name + exception type + message)
- **AND** the `.neo` SHALL still be written, containing every method that DID compile
  and every template that DID capture
- **AND** `NeoCompilerResult.IsComplete` SHALL be `false` (driving exit code `2`)
- **AND** a generic definition whose template CAPTURE throws SHALL likewise be
  recorded as a skip and omitted from the `TemplateTable` (the Step-25 runtime falls
  back to per-occurrence JIT for it -- the additive contract holds)

### Requirement: The V1 CLI roundtrip self-check proves the driver wiring end-to-end, reusing the Step-23 comparators

The V1 load-bearing gate for Step 24 SHALL be a host-side self-check
(`NeoStep24CliRoundtripCheck.Run(appdomain)`) driven via the existing
`ILRuntimeTestCLI` special-mode hook (`if (nameFilter == "NeoStep24CliRoundtrip")`),
mirroring the Step-22 / Step-23 self-check pattern. The self-check SHALL build a
small dedicated probe set (a handful of non-generic methods + a generic method + a
try/catch method + a struct-local method -- NOT the large `TestCases.dll` method
set, to keep the compile sub-second), invoke the SAME `NeoCompiler` driver the CLI
uses to produce a `.neo` in a `MemoryStream`, READ it back with
`NeoAssemblyReader.Read`, and assert the deserialized `NeoAssemblyModel` EQUALS the
in-memory compile. The equality assertions SHALL REUSE the Step-23 comparators:
`OpCodeRsEqual` (raw-24-byte `OpCodeR[]`), `MethodDefsEqual` (frame field-for-field),
`TemplatesEqual` (template faithful, every patch), `TypeDefsEqual` (type layout +
VTable + interfaces). The self-check SHALL additionally assert the driver's
generic/non-generic split: the deserialized `MethodDefTable` contains no generic
definition and the `TemplateTable` contains every generic definition in the probe.
V2 functional (deserialize -> `ExecuteNeo`) is explicitly Step 25 and is NOT
exercised here. The self-check + the probe type SHALL be Neo-only (compile out of
plain `Debug`); the NeoStep + NeoStep23Roundtrip + NeoStep22SelfCheck regression
smoke SHALL stay green.

#### Scenario: V1 CLI roundtrip passes for the probe matrix
- **WHEN** `NeoStep24CliRoundtripCheck.Run(appdomain)` is invoked host-side (DEBUG+Neo)
- **THEN** every probe method's deserialized `NeoMethodDefRecord` SHALL equal the
  in-memory compile (`OpCodeRsEqual` on `NeoExecuteBody`; `MethodDefsEqual` on the
  full frame: `LocalInfos`/`ParamInfos`/`TotalStructSize`/`TotalRefSize`/all scalars/
  `LocalIsReference`/`NeoCatchException*`/`SwitchTargets`/`NeoCallParams`/
  `ExceptionHandlers`)
- **AND** the probe's generic-method template SHALL roundtrip faithfully
  (`TemplatesEqual`: `TemplateBody` byte-for-byte, every `PatchEntry`, the front-half
  scalars, `InitObjPrefixRegisters`, `VariableTypeRefIdxs`, `ConstrainedTypeRefIdxs`,
  `ConstrainedMethodRefIdxs`)
- **AND** the probe type's `NeoTypeDefRecord` SHALL roundtrip (`TypeDefsEqual`:
  field layout, VTable method-refs, interface entries, static-ctor ref)
- **AND** the header (`Magic`/`Version`) + the table counts (`TypeDefs`/`MethodDefs`/
  `Templates`) SHALL match the driver's emitted set

#### Scenario: Generic/non-generic split is asserted by the self-check
- **WHEN** the self-check deserializes the probe's `.neo`
- **THEN** the `MethodDefTable` SHALL contain NO generic-method definition
  (every entry is a non-generic method, incl. ctors)
- **AND** the `TemplateTable` SHALL contain a template for EVERY generic-method
  definition present in the probe type

#### Scenario: Regression smoke stays green (additive)
- **WHEN** the driver + the self-check + the probe type are added (`Debug_Neo`)
- **THEN** the NeoStep smoke SHALL stay 205/205, NeoStep23Roundtrip 15/15,
  NeoStep22SelfCheck 55/55, NeoOptHardening 24/24, NeoStep20 9/9 (ZERO regressions;
  the driver is a new caller of unchanged internals, and the self-check is additive)
- **AND** a plain-`Debug` + `useRegister=true` NeoStep-filter run SHALL show the SAME
  pre-existing Legacy failure set with and without the change (Legacy-neutral;
  everything new is gated `#if ENABLE_NEO_MODE`)

### Requirement: The NeoAssemblyLoader binds a deserialized .neo to live ILMethods via an AOT-init dual-path (S1: non-generic, same-AppDomain)

The Step-25 runtime loader SHALL be a new Neo-only static class
`NeoAssemblyLoader` (`ILRuntime/Runtime/NeoAOT/NeoAssemblyLoader.cs`, gated
`#if ENABLE_NEO_MODE`) that consumes a deserialized `NeoAssemblyModel` (Step 23)
and attaches each `NeoMethodDefRecord` to a LIVE `ILMethod` in the SAME
`AppDomain` that compiled the `.neo`. For each record, the loader SHALL resolve
the record's `MethodRefIdx` to its `MethodReferencePatchInfo`, resolve the
declaring-type full name to an `ILType` via `appdomain.LoadedTypes[fullName]`,
match the method by name + parameter count to a non-generic `ILMethod` on that
type, and invoke an `InitCodeBodyFromNeo(record)` AOT-init on the matched
`ILMethod`. The loader SHALL operate on NON-GENERIC methods only for V1 (a
`NeoMethodDefRecord` is always non-generic by the Step-24 partition; generic
definitions live in the `TemplateTable` and are NOT consumed by the S1 loader).
The loader SHALL run in the SAME AppDomain as the compile, so the runtime token
hash maps (`mapTypeToken` / `mapMethod`) populated at Cecil-load + JIT-compile
time resolve the deserialized bodies' token operands NATURALLY -- the S1 loader
SHALL NOT perform cross-AppDomain hash re-resolution (deferred to S3). A type
that is not loaded, or a method that does not match, SHALL be recorded in a
skip report and omitted (the method KEEPS its JIT path -- the additive
contract); the loader SHALL NOT abort on a miss.

#### Scenario: Each NeoMethodDefRecord attaches to a live non-generic ILMethod
- **WHEN** `NeoAssemblyLoader.Attach(appdomain, model)` is invoked on a
  `NeoAssemblyModel` whose probe types are Cecil-loaded in `appdomain`
- **THEN** for every `NeoMethodDefRecord`, the loader SHALL resolve the
  declaring type via `LoadedTypes[fullName]`, match the method by name +
  parameter count, and call `ilm.InitCodeBodyFromNeo(record)`
- **AND** the matched `ILMethod`'s `isNeoAotBody` flag SHALL be `true` after the
  attach
- **AND** the loader SHALL NOT attempt to attach a generic-method definition
  (the `TemplateTable` is not consumed by S1) nor a generic-method instance (a
  runtime artifact, absent from `MethodDefs`)

#### Scenario: A miss is skipped, not fatal
- **WHEN** a `NeoMethodDefRecord`'s declaring type is not in `LoadedTypes`, or
  no method on the type matches the name + parameter count
- **THEN** the loader SHALL record the miss in the skip report (type-or-method
  identifier + reason) and continue with the remaining records
- **AND** the unmatched method SHALL keep its JIT path (no `isNeoAotBody` set;
  `BodyRegister` behaves as before)
- **AND** the loader SHALL return a report enumerating the attached and skipped
  methods

#### Scenario: Same-AppDomain token operands resolve naturally (no hash re-resolution)
- **WHEN** an attached AOT body executes (via `ExecuteNeo`) and a token operand
  (`Call`/`Callvirt` `Operand2`, `Box`/`Isinst`/`Castclass`/`Initobj` `Operand`,
  a `Ldstr` token) is resolved
- **THEN** the operand hash SHALL resolve via the EXISTING `mapTypeToken` /
  `mapMethod` / string-interner maps (populated at Cecil-load + JIT-compile time
  in the same AppDomain), WITHOUT any cross-AppDomain hash re-registration
- **AND** the S1 loader SHALL NOT record or rewrite compile-time hashes (that
  is the deferred S3 mechanism)

### Requirement: The ILMethod AOT-init populates CompiledFrame from a .neo NeoMethodDefRecord, bypassing JIT (the Cecil path stays as the reference + fallback)

`ILMethod` (`ILRuntime/CLR/Method/ILMethod.cs`) SHALL gain a Neo-only internal
`bool isNeoAotBody` flag (default `false`) + an internal
`InitCodeBodyFromNeo(NeoMethodDefRecord)` that populates the `compiledFrame`
struct field (`:58`) directly from the Step-23 record: `NeoExecuteBody`,
`LocalInfos`, `ParamInfos`, `TotalStructSize`, `TotalRefSize`,
`ParamPrimitiveSize`, `ParamReferenceCount`, `LocalsPrimitiveSize`,
`LocalsReferenceCount`, `ReturnPrimitiveSize`, `ReturnRefCount`,
`StackRegisterCount`, `LocalIsReference`, `NeoCatchException*`, `SwitchTargets`
(rebuilt from the serialized `KeyValuePair<int,int[]>[]`), and `NeoCallParams`
(rebuilt from `NeoCallParamMapRecord[]`, with the `PrimitiveByRefElemType` CLR
`System.Type[]` resolved back from assembly-qualified names). The AOT-init SHALL
additionally rebuild the runtime `Method.ExceptionHandler[]` EH structures
DIRECTLY from the `NeoExceptionHandlerRecord[]` body-index table (no Cecil, no
`addr[]` map) and set the ILMethod-level mirrors (`bodyRegister`,
`stackRegisterCnt`, `jumptablesR`) + the `isNeoAotBody` flag. The `BodyRegister`
getter (`:389`) SHALL short-circuit on the flag (return the AOT body without
calling `InitCodeBody`) when `isNeoAotBody` is `true`. The `ExecuteNeo` runtime
SHALL be UNCHANGED (it already reads only `method.CompiledFrame` + the AppDomain
hash maps -- no Cecil at execution time). The Cecil / JIT path
(`InitCodeBody(true)` -> `JITCompiler.Compile`) SHALL remain byte-identical when
`isNeoAotBody` is `false` (the reference + the fallback). All AOT-init additions
SHALL be gated `#if ENABLE_NEO_MODE`.

#### Scenario: InitCodeBodyFromNeo populates every load-bearing frame field
- **WHEN** `ilm.InitCodeBodyFromNeo(record)` is invoked on a matched ILMethod
- **THEN** `ilm.CompiledFrame.NeoExecuteBody` SHALL be the record's
  `NeoExecuteBody` (byte-for-byte the deserialized raw 24-byte `OpCodeR[]`)
- **AND** `ilm.CompiledFrame.LocalInfos` / `ParamInfos` / every scalar size +
  count / `LocalIsReference` / `NeoCatchException*` SHALL equal the record's
  fields
- **AND** `ilm.bodyRegister` SHALL equal the record's `NeoExecuteBody`,
  `ilm.stackRegisterCnt` SHALL equal the record's `StackRegisterCount`, and
  `ilm.isNeoAotBody` SHALL be `true`

#### Scenario: EH structures rebuild from body-index records without Cecil
- **WHEN** a method with a try/catch handler is AOT-attached
- **THEN** the rebuilt `Method.ExceptionHandler[]` SHALL carry
  `TryStart`/`TryEnd`/`HandlerStart`/`HandlerEnd`/`FilterStart` as the record's
  BODY INDICES (mapping 1:1 to `NeoExecuteBody` positions), `HandlerType` as the
  Cecil `ExceptionHandlerType`, and `CatchType` resolved to the runtime `IType`
  via the declaring AppDomain
- **AND** `ExecuteNeo`'s EH dispatch SHALL match a thrown exception to the
  correct catch clause using the rebuilt structures (no Cecil `addr[]` map)

#### Scenario: The BodyRegister getter short-circuits on the AOT flag
- **WHEN** `ilm.BodyRegister` is read on an AOT-attached ILMethod
  (`isNeoAotBody == true`)
- **THEN** the getter SHALL return the pre-populated `bodyRegister` WITHOUT
  calling `InitCodeBody` (no JIT, no Cecil `def.Body` read)
- **AND** `ExecuteNeo` SHALL run the method directly against the AOT body

#### Scenario: The JIT path is byte-identical when the AOT flag is false
- **WHEN** an ILMethod has `isNeoAotBody == false` (the default; every method
  not AOT-attached)
- **THEN** the `BodyRegister` getter SHALL behave exactly as before this change
  (lazily call `InitCodeBody(true)` -> `JITCompiler.Compile` when
  `bodyRegister == null`)
- **AND** the per-occurrence JIT instantiation path for generic methods SHALL be
  unchanged (the AOT-init does not touch the `BodyRegister` getter's
  generic-template hook)

### Requirement: The V2 functional self-check proves deserialize + ExecuteNeo == JIT for a non-generic probe matrix (the capstone gate)

The V2 load-bearing gate for Step 25 SHALL be a host-side self-check
(`NeoStep25LoadExecCheck.Run(appdomain)`, `#if ENABLE_NEO_MODE && DEBUG`) driven
via the existing `ILRuntimeTestCLI` special-mode hook (`if (nameFilter ==
"NeoStep25LoadExec")`), mirroring the Step-22 / Step-23 / Step-24 self-check
pattern. The self-check SHALL: (1) select a small dedicated probe ILType
(non-generic methods only) Cecil-loaded in the test AppDomain; (2) compile a
`.neo` for the probe via the SAME `NeoCompiler.Compile(IReadOnlyList<ILType>,
Stream)` driver the CLI uses into an in-memory `MemoryStream`; (3) read it back
via `NeoAssemblyReader.Read` -> `NeoAssemblyModel`; (4) run each probe method
via the JIT path and capture the result BEFORE attach; (5)
`NeoAssemblyLoader.Attach(appdomain, model)` to attach the deserialized AOT
bodies; (6) run the SAME methods again via the AOT body (`ExecuteNeo`) and
capture the result; (7) assert the JIT result EQUALS the AOT result via the
value/divide path (`if (!Equal(jit, aot)) int x = 1/0;`). The probe matrix SHALL
include an arithmetic method, a method with an exception handler, and a method
with various locals / a byref parameter (non-generic only). The probe type
SHALL be a dedicated `TestCases/NeoStep25LoadProbe.cs` (NOT the full
`TestCases.dll`, to stay sub-second + within the BCL-refs-only boundary). This
is the FIRST end-to-end "deserialize + execute" proof.

#### Scenario: The V2 self-check passes for the non-generic probe matrix
- **WHEN** `NeoStep25LoadExecCheck.Run(appdomain)` is invoked host-side
  (DEBUG+Neo)
- **THEN** every probe method's JIT-run result SHALL equal its AOT-run result
  (the divide-assert does NOT trip)
- **AND** the self-check SHALL compile the `.neo`, read it back, attach, and
  re-run via the SAME `NeoCompiler` / `NeoAssemblyReader` / `NeoAssemblyLoader`
  seams the runtime exposes (no test-only compile path)

#### Scenario: The capstone covers EH + locals/byref shapes
- **WHEN** the probe matrix includes a try/catch method and a method with mixed
  locals + a byref parameter
- **THEN** the AOT execution SHALL produce the same result as the JIT execution
  for both shapes
- **AND** the EH rebuild (`InitCodeBodyFromNeo` -> `RebuildEHFromNeo`) SHALL be
  exercised by the try/catch cell (a mis-dispatch trips the divide-assert)

#### Scenario: Generic methods are out of scope for the V2 gate
- **WHEN** the probe type contains a generic method
- **THEN** the self-check SHALL NOT assert on the generic method's AOT execution
  (generic-instantiation-at-load is S2, deferred)
- **AND** the generic method's template SHALL roundtrip through the `.neo`
  (Step 23) but the S1 loader SHALL NOT instantiate it (it is not consumed)

### Requirement: The Step-25 loader + ILMethod AOT-init are additive, Neo-only, and Legacy-neutral

The `NeoAssemblyLoader`, the `NeoStep25LoadExecCheck` self-check, the
`NeoStep25LoadProbe` type, and the `ILMethod` AOT-init (`isNeoAotBody` +
`InitCodeBodyFromNeo` + the `BodyRegister` getter short-circuit) SHALL all be
gated `#if ENABLE_NEO_MODE` (the self-check file additionally `&& DEBUG`). The
`.neo` serializer, the JIT compiler, `ExecuteNeo`, the optimizer, the Step-22
template mechanism, and the Step-23/Step-24 artifacts SHALL NOT be modified.
The Cecil / JIT path SHALL remain as the reference + the fallback (when
`isNeoAotBody` is `false`, `BodyRegister` behaves exactly as before). Legacy
`ExecuteR` (`ILIntepreter.Register.cs`) SHALL be byte-identical to before this
change: the whole `NeoAOT/` addition + the AOT-init flag + the getter
short-circuit SHALL compile out under plain `Debug`, and a stash-toggle plain-
`Debug` + `useRegister=true` NeoStep-filter run SHALL show the SAME pre-existing
Legacy failure set with and without the change. The full NeoStep smoke
(205/205) + NeoStep23Roundtrip (15/15) + NeoStep22SelfCheck (55/55) +
NeoStep24CliRoundtrip (5/5) + the new NeoStep25LoadExec SHALL stay green (ZERO
regressions; the loader is a new path and the AOT-init defaults off).

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and the Legacy
  register VM runs the NeoStep-filter smoke
- **THEN** the smoke SHALL show the SAME pre-existing Legacy failure set with
  and without this change (stash-toggle proof), because the `NeoAssemblyLoader`,
  the self-check, the probe type, the `isNeoAotBody` flag, `InitCodeBodyFromNeo`,
  and the `BodyRegister` getter short-circuit are all gated `#if ENABLE_NEO_MODE`
  and compile out

#### Scenario: Regression smoke stays green (additive)
- **WHEN** the loader + the AOT-init + the self-check are added (`Debug_Neo`)
- **THEN** the NeoStep smoke SHALL stay 205/205, NeoStep23Roundtrip 15/15,
  NeoStep22SelfCheck 55/55, NeoStep24CliRoundtrip 5/5 (ZERO regressions; the
  loader is a new path and the AOT-init defaults off)
- **AND** the new `NeoStep25LoadExec` self-check SHALL pass (the V2 capstone)

#### Scenario: The JIT path is the reference + the fallback
- **WHEN** an ILMethod is NOT AOT-attached (`isNeoAotBody == false`, the default
  for every method the loader did not bind)
- **THEN** the method SHALL JIT-compile + run exactly as before this change
- **AND** an AOT-attached method whose AOT execution is found incorrect SHALL
  be recoverable by clearing `isNeoAotBody` (falling back to the JIT path) --
  the design does NOT remove the JIT/Cecil path

### Requirement: The NeoAssemblyLoader consumes the .neo TemplateTable and binds reconstructed templates to live generic-method definitions (S2: generic-instantiation-at-load)

The Step-25 runtime loader (`NeoAssemblyLoader.Attach`) SHALL additionally
consume the deserialized `NeoAssemblyModel.Templates` table (Step 23) and, for
each `NeoTemplateRecord`, bind a reconstructed `GenericMethodTemplate` to the
matching live generic-method DEFINITION in the SAME AppDomain. The loader SHALL
resolve each record's `DefinitionMethodRefIdx` to its `MethodReferencePatchInfo`,
resolve the declaring-type full name to an `ILType` via
`appdomain.LoadedTypes[fullName]`, and match the open generic definition by
name + parameter count + `GenericParameterCount > 0 && !IsGenericInstance`. The
loader SHALL reconstruct the template via a new
`GenericMethodTemplateOps.BuildFromNeoRecord(definition, appdomain, model,
record, resolveVariableType)` helper (mirroring `StoreFromCapture` but taking
the `.neo` record + a loader-provided `VariableTypes` re-resolution closure),
then install it on the definition via a new Neo-only
`ILMethod.InitTemplateFromNeo(template)` setter that OVERWRITES the
`genericMethodTemplate` field. The execution path SHALL be UNCHANGED: the
existing Step-22 hook in `ILMethod.InitCodeBody` (`ILMethod.cs:720-762`) already
routes a generic-instance `ILMethod` through
`GenericMethodTemplateOps.TryInstantiate` (`CloneAndPatch`) whenever
`genericDefinition.GenericMethodTemplateCache != null`, and `ExecuteNeo` runs the
resulting body unmodified. A template whose reconstruction hits a deferred case
(a T-identity-token patch, or a `VariableTypes` re-resolution miss) SHALL be
SKIPPED -- the generic method keeps its per-occurrence JIT path (the additive
contract) -- and the miss SHALL be recorded in the load report. The loader
SHALL NOT abort on a miss.

#### Scenario: Each NeoTemplateRecord binds to a live generic-method definition
- **WHEN** `NeoAssemblyLoader.Attach(appdomain, model)` is invoked on a
  `NeoAssemblyModel` whose probe types are Cecil-loaded in `appdomain` and
  whose `TemplateTable` carries a template for a generic method on a probe type
- **THEN** for every `NeoTemplateRecord`, the loader SHALL resolve the
  declaring type via `LoadedTypes[fullName]`, match the open generic definition,
  reconstruct the `GenericMethodTemplate`, and call
  `def.InitTemplateFromNeo(template)`
- **AND** the matched definition's `GenericMethodTemplateCache` SHALL be the
  reconstructed template after the attach
- **AND** a subsequent generic-instance call on that definition SHALL route
  through `CloneAndPatch` (the Step-22 hook), NOT the per-occurrence JIT

#### Scenario: The parameterless Run entry exercises a generic call via a wrapper
- **WHEN** a non-generic parameterless wrapper method (e.g.
  `static int Wrap() { return Echo<int>(42); }`) is invoked via the Step-6
  parameterless `ILIntepreter.Run` entry shim (`ILIntepreter.cs:87-120`) and the
  wrapper's body contains a `Call` to a generic method
- **THEN** `ExecuteNeo` SHALL resolve the generic-instance target via
  `MakeGenericMethod` and the callee's `BodyRegister` getter SHALL hit the
  Step-22 hook, which SHALL route through `CloneAndPatch` when the definition's
  cache is set
- **AND** a parametrized probe entry (P2) SHALL NOT be required -- the generic
  args are baked into the wrapper's `Call` token at JIT-compile time and never
  cross the Run boundary (a generic call is the same `Call`-opcode shape as the
  S1 `MixedLocalsProbe` internal byref call)

#### Scenario: A miss is skipped, not fatal
- **WHEN** a `NeoTemplateRecord`'s declaring type is not in `LoadedTypes`, or no
  open generic definition matches, or the template reconstruction returns null
  (a deferred T-identity-token case, or a `VariableTypes` re-resolution miss)
- **THEN** the loader SHALL record the miss in the skip report (target +
  reason) and continue with the remaining records
- **AND** the unmatched generic definition SHALL keep its JIT path (the cache
  stays null; the Step-22 hook falls through to per-occurrence JIT)
- **AND** the loader SHALL return a report enumerating the attached and skipped
  templates alongside the attached and skipped method defs

#### Scenario: Same-AppDomain token operands resolve naturally (no hash re-resolution)
- **WHEN** an AOT-instantiated generic body executes (via `CloneAndPatch` +
  `ExecuteNeo`) and a token operand is resolved
- **THEN** the operand hash SHALL resolve via the EXISTING `mapTypeToken` /
  `mapMethod` maps (populated at Cecil-load + JIT-compile time in the same
  AppDomain), WITHOUT any cross-AppDomain hash re-registration
- **AND** the S2 loader SHALL NOT record or rewrite compile-time hashes (that
  is the deferred S3 mechanism)

### Requirement: The ILMethod AOT template setter overwrites the JIT-captured template from a .neo-reconstructed GenericMethodTemplate (S2)

`ILMethod` (`ILRuntime/CLR/Method/ILMethod.cs`) SHALL gain a Neo-only internal
`InitTemplateFromNeo(GenericMethodTemplate template)` setter that sets the
`genericMethodTemplate` field DIRECTLY (overwriting any previously-cached
template), distinct from `StoreGenericTemplate` (`ILMethod.cs:1392-1404`) whose
`if (genericMethodTemplate != null) return;` guard PREVENTS overwrite. The
overwrite is REQUIRED because the `NeoCompiler.CaptureTemplate` step
(`NeoCompiler.cs:277-310`) -- which runs during the V2 capstone's compile --
ALREADY caches a JIT-captured template on the definition (it synthesizes a
capture-eligible instance and reads `capInstance.BodyRegister`, firing the
`InitCodeBody` capture hook); the loader's AOT template MUST replace it so a
subsequent generic call routes through `CloneAndPatch` against the AOT template
body, not the JIT-captured one. The setter SHALL be Neo-only (`#if
ENABLE_NEO_MODE`); `StoreGenericTemplate`'s guard SHALL remain intact for the
JIT capture path. The `GenericMethodTemplateCache` getter (`ILMethod.cs:1387-
1390`) SHALL continue to return null for a generic INSTANCE (only the definition
caches).

#### Scenario: The setter overwrites a JIT-cached template
- **WHEN** a generic definition's `GenericMethodTemplateCache` is already
  non-null (a JIT-captured template from `StoreGenericTemplate`) and the loader
  calls `def.InitTemplateFromNeo(aotTemplate)`
- **THEN** the definition's `GenericMethodTemplateCache` SHALL become the AOT
  template (the JIT-captured one is replaced)
- **AND** a subsequent generic-instance call SHALL route through `CloneAndPatch`
  against the AOT template body

#### Scenario: StoreGenericTemplate's guard is unchanged for the JIT path
- **WHEN** a capture-eligible generic instantiation runs via the normal JIT path
  (no AOT loader involved) and the definition's cache is already set
- **THEN** `StoreGenericTemplate` SHALL still return early (no overwrite) -- the
  JIT capture path is byte-identical to before this change

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and runs the Legacy
  register VM
- **THEN** `InitTemplateFromNeo` SHALL compile out (it is gated `#if
  ENABLE_NEO_MODE`), and Legacy `ExecuteR` SHALL be byte-identical to before
  this change

### Requirement: The S2 generic-template slice covers no-T-identity-token generic methods; T-identity-token + cross-AppDomain + EH-bearing cases stay JIT-fallback (deferred to S3)

The S2 `BuildFromNeoRecord` reconstruction SHALL cover generic methods whose
template has NO T-identity token `PatchEntry` (no `Box T` / `Unbox T` /
`Unbox_Any T` / `Isinst T` / `Castclass T` / `Newarr T[]` / `Stobj T` /
`Ldobj T` / `Constrained T` type-token, and no T-qualified `IComparable<T>::
CompareTo`-style method-token). For such a template the patch table is
`IsRefMoveFlag`-only or empty; `BuildFromNeoRecord` SHALL set each patch's
`CecilToken` to null (a null `CecilToken` makes `DoCloneAndPatch` SKIP the patch
at `GenericMethodTemplate.cs:485`, and the back-half's
`TypeSpecializeNeoOpcodes` re-derives the is-ref flag). `BuildFromNeoRecord`
SHALL re-resolve `VariableTypes` from the record's `VariableTypeRefIdxs` via the
loader-provided closure (TypeRef idx -> Cecil `TypeReference`, same-AppDomain),
so `BuildInitObjPrefix` (`GenericMethodTemplate.cs:430-460`) works for generic-
param-typed locals. `BuildFromNeoRecord` SHALL set `Addr` and `Symbols` to null
(the S2 probe's generic methods carry no exception handler; `Addr` is needed
only for EH rebuild and `Symbols` is not read at `CloneAndPatch` /
`ExecuteNeo`). A template whose `Patches` contain a `TypeToken` or
`MethodToken` with a non-`none` `CecilTokenKind` (a T-identity-token site that
needs a Cecil `TypeReference` / `MethodReference` to re-resolve, or a Cecil-free
patch applier keyed on `GenericParamIdx`) SHALL be REJECTED by
`BuildFromNeoRecord` (return null -> the loader skips the bind -> the generic
method keeps JIT). This rejection is the honest S2 boundary: T-identity-token
re-resolution, generic methods WITH try/catch (Cecil `Addr`), cross-AppDomain
(a Cecil-FREE AppDomain), full `ILType` decoupling, and Approach-1 token-hash
re-resolution are DEFERRED to S3.

#### Scenario: A no-T-identity-token generic method binds and runs via CloneAndPatch
- **WHEN** a generic method `T Echo<T>(T v) { return v; }` (a pure-dataflow
  body with NO `Box T` / `Constrained T` / T-qualified callvirt) is in the
  probe and the loader binds its reconstructed template
- **THEN** a concrete-T call (e.g. `Echo<int>(42)`) SHALL route through
  `CloneAndPatch` from the AOT template and return the correct result
- **AND** the structural-equivalence check SHALL confirm the `CloneAndPatch`
  body EQUALS the per-occurrence JIT body for that concrete T

#### Scenario: A T-identity-token template is rejected (S3)
- **WHEN** a generic method body contains a T-identity token site (e.g.
  `Box T`, `constrained.callvirt T.GetHashCode`, `IComparable<T>::CompareTo`)
  and the loader processes its template
- **THEN** `BuildFromNeoRecord` SHALL return null (the template is rejected)
- **AND** the loader SHALL skip the bind and record it in the skip report
- **AND** the generic method SHALL keep its per-occurrence JIT path (correctness
  preserved; only the AOT optimization is lost for this case)

#### Scenario: A VariableTypes re-resolution miss is skipped (S3)
- **WHEN** the loader cannot re-resolve some `VariableTypeRefIdx` to a Cecil
  `TypeReference` (e.g. a CLR type from an unloaded assembly)
- **THEN** `BuildFromNeoRecord` SHALL return null (skip)
- **AND** the generic method SHALL keep its JIT path
- **AND** the miss SHALL be reported (the S3 cross-AppDomain + Cecil-free
  re-resolution concern)

#### Scenario: A generic method WITH try/catch is deferred (S3)
- **WHEN** a generic method body contains an exception handler (needs Cecil
  `Addr` for the EH rebuild)
- **THEN** S2 SHALL NOT cover it (the probe's generic methods carry no EH); it
  SHALL keep JIT, deferred to the S3 per-instruction Cecil-handle recovery

### Requirement: The V2 generic self-check proves deserialize + CloneAndPatch-from-AOT-template == JIT for a generic probe matrix (the S2 capstone gate)

The S2 load-bearing gate SHALL extend the existing host-side self-check
`NeoStep25LoadExecCheck.Run(appdomain)` (`#if ENABLE_NEO_MODE && DEBUG`, driven
via the existing `ILRuntimeTestCLI` special-mode hook `if (nameFilter ==
"NeoStep25LoadExec")`) with GENERIC cells. The probe type
(`TestCases/NeoStep25LoadProbe.cs`) SHALL gain a generic method + non-generic
parameterless wrappers that call it at concrete T's (a primitive, an 8-byte
primitive, an IL struct, a reference type). The capstone SHALL: (1) compile a
`.neo` for the probe via the SAME `NeoCompiler.Compile` driver the CLI uses
(which captures the generic template into the `TemplateTable`); (2) read it
back via `NeoAssemblyReader.Read`; (3) `NeoAssemblyLoader.Attach` (which now
ALSO binds the reconstructed templates to the generic definitions, OVERWRITING
the JIT-captured template from the compile step); (4) invoke parameterless
wrappers calling FRESH generic instances (concrete T's not instantiated before
attach) via `ExecuteNeo`; (5) assert each result EQUALS its known-expected
value. A green JIT==AOT comparison is INSUFFICIENT (Step 23 already proved the
AOT template body == JIT body byte-for-byte), so the capstone SHALL include an
ADVERSARIAL template body-mutation cell: mutate a deserialized `TemplateBody`
constant BEFORE attach, invoke a wrapper calling a fresh generic instance, and
assert the MUTATED value -- observing the MUTATED value PROVES `CloneAndPatch`
ran the genuine AOT template body (not the JIT-captured one), the binding "a
green smoke does not prove a gate correct" lesson. The probe type SHALL stay
NON-NESTED + within the BCL-refs-only boundary (the S1 constraint). The
capstone reuses the SAME `NeoCompiler` / `NeoAssemblyReader` /
`NeoAssemblyLoader` seams (no test-only compile path).

#### Scenario: The generic cells pass via the AOT template path
- **WHEN** `NeoStep25LoadExecCheck.Run(appdomain)` is invoked host-side
  (DEBUG+Neo) with the extended generic cells
- **THEN** every generic wrapper's AOT-run result SHALL equal its known-expected
  value (the divide-assert does NOT trip) for each concrete T (primitive /
  8-byte-primitive / IL-struct / reference-type)
- **AND** each generic call SHALL route through `CloneAndPatch` from the AOT
  template (the definition's `GenericMethodTemplateCache` is the reconstructed
  template, overwriting the JIT-captured one)

#### Scenario: The template body-mutation cell proves the AOT template ran
- **WHEN** a deserialized `TemplateBody` constant is MUTATED before attach and a
  wrapper calling a FRESH generic instance (not instantiated before attach) is
  invoked post-attach
- **THEN** the observed result SHALL be the MUTATED value (NOT the unmutated
  JIT value)
- **AND** this SHALL prove `CloneAndPatch` ran the genuine AOT template body,
  not the JIT-captured template (the load-binding property of the S2 slice)

#### Scenario: The fresh-instance cell isolates the AOT-template path
- **WHEN** the after-attach run invokes a concrete-T wrapper NEVER instantiated
  before attach (e.g. before-run used `Echo<int>`, after-run uses `Echo<long>`)
- **THEN** the `Echo<long>` call SHALL route through `CloneAndPatch` via the AOT
  template (it cannot reuse a JIT-cached instance body -- none exists)
- **AND** the result SHALL be correct (isolating the AOT-template path from any
  JIT-cached instance state)

#### Scenario: Structural equivalence holds for the AOT-reconstructed template
- **WHEN** a DEBUG host-side comparator asserts the AOT-reconstructed template's
  `CloneAndPatch` body EQUALS the per-occurrence JIT body (reusing the Step-22
  V1 `BodiesEqual` comparator shape) for each concrete T
- **THEN** the two bodies SHALL be structurally equal (same length, same
  `Code`/`Register1/2/3`/`Operand`/`Operand2/3/4` per index)
- **AND** a divergence SHALL trip the check (NOT be silently accepted)

#### Scenario: Regression smoke stays green (additive)
- **WHEN** the loader's template-consumption extension + `BuildFromNeoRecord` +
  the `InitTemplateFromNeo` setter + the capstone's generic cells are added
  (`Debug_Neo`)
- **THEN** the NeoStep smoke SHALL stay 210/210, NeoStep22SelfCheck 55/55,
  NeoStep23Roundtrip 15/15, NeoStep24CliRoundtrip 5/5, and the extended
  NeoStep25LoadExec SHALL pass (ZERO regressions; the loader extension is a new
  consumer of the unchanged Step-22 hook, and the setter defaults the cache to
  null for every non-AOT scenario)
- **AND** a plain-`Debug` + `useRegister=true` NeoStep-filter run SHALL show the
  SAME pre-existing Legacy failure set with and without the change
  (Legacy-neutral; everything new is gated `#if ENABLE_NEO_MODE`)


### Requirement: A host-side benchmark self-check measures Neo interpreter throughput on a fixed workload via appdomain.Invoke

The Neo code-path SHALL ship a host-side benchmark self-check
(`NeoStep26BenchCheck.Run(appdomain)`, gated `#if ENABLE_NEO_MODE && DEBUG`,
driven via the existing `ILRuntimeTestCLI` special-mode hook
`if (nameFilter == "NeoStep26Bench")`) that invokes a fixed set of
parameterless static bench methods on a dedicated probe type
(`TestCases/NeoStep26BenchProbe.cs`) through `appdomain.Invoke` and times each
invocation with a HOST-side `System.Diagnostics.Stopwatch` (NOT an interpreted
`Stopwatch`, because the generic test harness marks
`[ILRuntimeTest(IsPerformanceTest)]` methods IGNORED and emits no clean
timing line). The bench set SHALL cover five workload shapes: instance field
access + write (`BenchFieldAccess`), a static IL method call
(`BenchMethodCall`), a value-type compute (`BenchValueType`), a virtual or
interface dispatch (`BenchVirtualDispatch`), and a rank-1 array element
read + write (`BenchArray`). Each bench method SHALL be parameterless and
SHALL return a primitive (`int` or `long`) carrying the accumulated work, so
the self-check can assert the bench performed the correct computation (the
Step-6 parameterless-Run shim constraint and the F-12 reference-return
limitation are both avoided by the parameterless + primitive-return shape).
For each bench, the self-check SHALL assert the returned value EQUALS a
known-expected value via the divide-assert pattern
(`if (!Equals(result, expected)) int x = 1 / 0;`) -- this is the
correctness-of-measurement gate (the bench did the RIGHT WORK, not just some
work). The self-check SHALL emit one structured line per bench of the form
`BENCH:<benchName>:<iterations>:<elapsedTicks>` to stdout. The self-check
SHALL NOT assert a Neo-vs-Legacy ratio threshold, because the development
host is not the performance-baseline host and absolute timings are noise
across machines and load; the ratio is computed by a SEPARATE runner script
(`scripts/run-neo-bench.{ps1,sh}`) across two CLI invocations, not by the
self-check. A green self-check proves measurement works AND each bench
computed its expected value; it does NOT prove Neo is fast.

The bench probe type + the self-check SHALL be additive and Neo-only: the
self-check file SHALL be gated `#if ENABLE_NEO_MODE && DEBUG`, and the CLI
special-mode hook SHALL be gated `#if ENABLE_NEO_MODE`. The probe methods
SHALL be plain parameterless static methods with NO Neo dependency (they are
runnable by BOTH `ExecuteNeo` and `ExecuteR`, which is what makes the
Neo-vs-Legacy comparison apples-to-apples on the SAME `TestCases.dll`).

#### Scenario: The bench self-check runs the five workloads and emits BENCH lines
- **WHEN** `NeoStep26BenchCheck.Run(appdomain)` is invoked host-side
  (DEBUG+Neo) via the `NeoStep26Bench` CLI hook
- **THEN** it SHALL invoke each of `BenchFieldAccess`, `BenchMethodCall`,
  `BenchValueType`, `BenchVirtualDispatch`, and `BenchArray` via
  `appdomain.Invoke`, time each with a host `Stopwatch`, and emit a
  `BENCH:<name>:<iterations>:<ticks>` line per bench
- **AND** each bench SHALL return its known-expected primitive value (the
  divide-assert does NOT trip), proving the bench computed the correct result

#### Scenario: The self-check gates on measurement correctness, not on a ratio
- **WHEN** the self-check runs on the development host (which is NOT the
  performance-baseline host)
- **THEN** it SHALL NOT fail on the absolute timing or on any Neo-vs-Legacy
  ratio (no ratio threshold is asserted)
- **AND** it SHALL fail ONLY if a bench throws, returns a wrong value (the
  divide-assert trips), or produces a non-positive timing
- **AND** the Neo-vs-Legacy ratio SHALL be reported by the separate runner
  script, not asserted by the self-check

#### Scenario: The runner script compares Neo and Legacy on the same workload
- **WHEN** `scripts/run-neo-bench.{ps1,sh}` runs the CLI under `Debug_Neo`
  and under plain `Debug` with `useRegister=true` on the SAME
  `TestCases.dll`
- **THEN** it SHALL parse the `BENCH:` lines from both configs and print a
  per-bench `name | neo_ms | legacy_ms | ratio` table
- **AND** the bench probe methods SHALL be identical across both configs (the
  comparison is the SAME workload interpreted by `ExecuteNeo` vs `ExecuteR`)

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and runs the Legacy
  register VM
- **THEN** the `NeoStep26BenchCheck` self-check and the CLI special-mode hook
  SHALL compile out (they are gated `#if ENABLE_NEO_MODE`)
- **AND** the bench probe methods SHALL remain compilable plain static methods
  (they have no Neo dependency); the Legacy NeoStep-filter smoke SHALL show
  the SAME pre-existing failure set with and without this change

#### Scenario: NeoStep regression smoke stays green
- **WHEN** the benchmark self-check + the probe type + the CLI hook are added
  (`Debug_Neo`)
- **THEN** the full `NeoStep` smoke SHALL stay 215/215 (ZERO regressions),
  because the bench self-check runs under a SEPARATE filter (`NeoStep26Bench`)
  and does NOT run under the `NeoStep` regression filter
- **AND** the `NeoStep` filter SHALL NOT match the bench probe methods (the
  probe methods' names do not contain the substring `NeoStep`)

### Requirement: A single AppDomain and its interpreter pool are not safe for concurrent multi-threaded access (single-threaded cooperative contract)

The ILRuntime `AppDomain` + its `ILIntepreter` pool SHALL be documented as
NOT safe for concurrent multi-threaded access. The engine is single-threaded
cooperative: the `Thread.CurrentThread.ManagedThreadId ==
AppDomain.UnityMainThreadID` checks in the interpreter
(`ILIntepreter.cs:56,151,2164,4723`, `ILIntepreter.Neo.cs:829,4451`,
`ILIntepreter.Register.cs:91,3044,5362`) drive a cooperative coroutine pump
(the `Thread.Sleep(10)` yield at `ILIntepreter.cs:62`), NOT thread-safety.
The delegate and async paths (`DelegateAdapter.NeoInvokeSub`,
`ILAsyncContext` resumption) allocate a FRESH pooled interpreter per callback
or resumption to ISOLATE frame state across sequential callbacks; they do NOT
enable concurrent execution against a single AppDomain's token maps
(`mapTypeToken`, `mapMethod`, `LoadedTypes`, the string interner), which have
NO synchronization. A host that needs multi-threaded execution SHALL use one
AppDomain per thread. This requirement is DOCUMENTATION-only: the change
SHALL add a comment block near the `UnityMainThreadID` check in
`ILIntepreter.cs` stating the contract; it SHALL NOT add synchronization
(the contract is single-threaded, not lock-based) and SHALL NOT alter any
runtime behavior.

#### Scenario: The contract is documented at the thread-check site
- **WHEN** a maintainer reads the `UnityMainThreadID` check in
  `ILIntepreter.cs` (around line 56)
- **THEN** a comment block SHALL state that a single AppDomain is not safe
  for concurrent multi-threaded access, that the pool isolates per-callback
  frame state (it does not enable concurrency), and that a host needing
  multi-threaded execution SHALL use one AppDomain per thread
- **AND** no runtime behavior SHALL change (no lock added, no branch altered)

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE`
- **THEN** the comment SHALL remain (it is in shared `ILIntepreter.cs`), and
  the Legacy register VM SHALL be byte-identical to before this change

### Requirement: Neo debugger variable inspection and reflection-on-Neo field reads are deferred follow-ups (NOT Step 26 scope)

Step 26 SHALL NOT deliver Neo variable inspection in the debugger NOR the
reflection-on-Neo field-read fix (the F-4 / NEO-IL-EX-FIELDACCESS family).
These are explicitly DEFERRED follow-ups, recorded here so a future change
finds them. (1) The debugger variable-inspection paths in
`DebugService.cs` (`GetThisInfo` at :203-261, `GetLocalVariableInfo` at
:263-300, and the frame-variable readers at :442-731 and :678-740) are built
around the Legacy `StackObject*` + `BasePointer` frame; under
`ENABLE_NEO_MODE` they ALREADY degrade gracefully -- `GetThisInfo` returns
"Neo this inspection is not supported yet." (`DebugService.cs:207-209`) and
`GetLocalVariableInfo` returns "Neo local variable inspection is not
supported yet." (`DebugService.cs:268-271`). A real fix SHALL read the Neo
frame (`byte* frameBase` + `AutoList mStack` + `CompiledFrame.LocalInfos`)
and SHALL touch the variable-reading methods; this is substantial deep
debugger-protocol work routed to a dedicated `neo-debugger-neo-frame`
follow-up. The stacktrace instruction dump is ALREADY Neo-adapted (it reads
`CompiledFrame.NeoExecuteBody` at `DebugService.cs:143-148`) and is NOT
deferred. (2) The F-4 reflection-on-Neo fix (four independent broken read
paths: callvirt-on-CLR-interface for the `ILInstance` bridge; `callvirt.clr`
on `Object.GetType`; the `appdomain.Invoke` instance-method re-entry path
whose Step-6 shim handles only parameterless static methods; and the
`ILTypeInstance` Neo indexer which returns null because the indexer +
accessors are `#if !ENABLE_NEO_MODE` at `ILTypeInstance.cs:27,86,94,379,...`)
SHALL be routed to a cross-binding-adaptor follow-up; Step 26 does NOT
subsume F-4. The working reflection coverage (catch matching via
`CheckExceptionType` + `isinst`) is already guarded by the `NeoStep14_ILEx_*`
probes shipped by `neo-il-exception-throw` and SHALL NOT be re-tested here.
Step 26 SHALL NOT ship a fix for either deferred surface; a future change
SHALL own them.

#### Scenario: Step 26 does not touch the debugger variable-inspection paths
- **WHEN** Step 26 ships
- **THEN** `DebugService.cs` SHALL be unmodified (the existing "not supported
  yet" graceful-degradation guards SHALL remain verbatim)
- **AND** no Neo variable-inspection code SHALL be added by this change

#### Scenario: Step 26 does not subsume F-4
- **WHEN** Step 26 ships
- **THEN** no fix for the four F-4 reflection-on-Neo read paths SHALL be
  shipped (the `ILTypeInstance` Neo indexer stays absent; the callvirt-on-
  CLR-interface + `appdomain.Invoke` instance-reentry + `callvirt.clr GetType`
  paths stay unfixed)
- **AND** the F-4 deferred item SHALL remain open in
  `.trae/documents/neo-deferred-items.md`, routed to a cross-binding-adaptor
  follow-up (NOT marked resolved by Step 26)

#### Scenario: The deferrals are cross-referenced in the deferred-items tracker
- **WHEN** Step 26 archives
- **THEN** `neo-deferred-items.md` SHALL record the `neo-debugger-neo-frame`
  follow-up (the D debugger deferral) with the suspect sites pinned
  (`DebugService.cs:203-261, 263-300, 442-731, 678-740`)
- **AND** the F-4 row SHALL remain intact and SHALL NOT be marked resolved
  by Step 26


### Requirement: The NeoTypeDefRecord carries enough to rebuild an ILType field layout + Neo VTable without Cecil, proven by a host-side structural-equivalence self-check + an adversarial mutation cell (S3 partial: ILType AOT-init, same-AppDomain)

The Step-25 runtime AOT path SHALL additionally decouple the `ILType` side: a
Neo-only builder (`#if ENABLE_NEO_MODE`) SHALL reconstruct an `ILType`'s
instance field layout and Neo VTable as PURE DATA from a deserialized
`NeoTypeDefRecord` (`NeoAssembly.cs:162-179`), WITHOUT reading the Cecil
`TypeDefinition`. The builder SHALL reconstruct the instance field layout
(`TotalPrimitiveSize`, `TotalReferenceCount`, and the per-field
`ILTypeFieldOffset{PrimitiveOffset, ReferenceOffset}` from each
`NeoFieldLayoutRecord`) and the Neo VTable (the `IMethod[]` slot array, by
resolving each `VTableMethodRefIdxs[i]` to the live `IMethod` via the
same-AppDomain maps, plus the slot-key map re-derived from each slot method's
`SignatureString`, plus the interface offset map from each
`NeoInterfaceEntryRecord`). The builder SHALL NOT install the rebuilt layout or
VTable on a live `ILType` in this slice (the Cecil init path stays as the only
init path; the rebuild is consumed by the self-check). The Cecil / JIT init
path (`ILType.InitializeFields` / `BuildNeoVTable`) SHALL stay byte-identical
when the builder is not exercised (Legacy-neutral). The two honest record gaps
(`naturalAlignment`, not carried but re-derived from the resolved field types;
per-static-field offsets, not carried -- only the static totals are) SHALL be
documented; `naturalAlignment` SHALL be re-derived and compared, and the
per-static-field offset gap SHALL be deferred with sub-surface 4.

#### Scenario: The rebuilt instance layout equals the Cecil-computed layout
- **WHEN** the host-side self-check deserializes a `.neo` for a probe `ILType`
  that declares 2+ instance fields of differing primitive widths (and the type
  is Cecil-loaded in the SAME AppDomain)
- **THEN** the builder SHALL rebuild `TotalPrimitiveSize`,
  `TotalReferenceCount`, and each per-field `PrimitiveOffset` /
  `ReferenceOffset` from the `NeoTypeDefRecord`
- **AND** each rebuilt value SHALL EQUAL the Cecil-computed value on that
  `ILType` (`iltype.TotalPrimitiveSize`, `iltype.TotalReferenceCount`,
  `iltype.fieldOffsets`)
- **AND** the re-derived `naturalAlignment` SHALL EQUAL the Cecil-computed
  `iltype.NaturalAlignment`

#### Scenario: The rebuilt Neo VTable equals the Cecil-computed VTable
- **WHEN** the probe `ILType` declares a base-class virtual method and/or
  implements an interface (so the VTable + interface map are non-trivial)
- **THEN** the builder SHALL resolve each `VTableMethodRefIdxs[i]` to the live
  `IMethod` and rebuild the `IMethod[]` slot array
- **AND** the rebuilt slot array SHALL EQUAL `iltype.NeoVTable` slot-by-slot
- **AND** the re-derived slot-key map SHALL EQUAL `iltype`'s slot-key map
- **AND** each rebuilt interface offset SHALL EQUAL
  `iltype.GetInterfaceVTableOffset(interfaceType)`

#### Scenario: A mutated record produces a divergent rebuild (the load-bearing mutation cell)
- **WHEN** the self-check mutates a field `PrimitiveOffset` (or swaps two
  `VTableMethodRefIdxs` entries) in an INDEPENDENT deserialized
  `NeoTypeDefRecord` BEFORE rebuild
- **THEN** the rebuilt layout (or VTable) SHALL DIVERGE from the Cecil-computed
  value exactly where mutated
- **AND** a rebuild that ignored the record would still equal Cecil and this
  cell SHALL FAIL for such a rebuild -- so a PASS proves the rebuild genuinely
  reads the `NeoTypeDefRecord`

#### Scenario: The self-check reuses the existing capstone hook
- **WHEN** the `NeoStep25LoadExec` CLI special-mode hook runs
- **THEN** the existing `NeoStep25LoadExecCheck.Run(appdomain)` self-check
  (`#if ENABLE_NEO_MODE && DEBUG`) SHALL additionally run the
  structural-equivalence cells and the mutation cell
- **AND** the loader/helper additions SHALL be additive and Neo-only (no new
  CLI hook; the S1/S2 attach flow is unchanged)

### Requirement: The Cecil-free AppDomain load, cross-AppDomain token-hash re-resolution, static .cctor seeding, and full CLR-assembly registration remain DEFERRED from S3 partial (honest deferral -- not promoted to met)

The S3 partial slice SHALL NOT deliver the Cecil-free AppDomain load, the
cross-AppDomain token-hash re-resolution, the static `.cctor` seeding via
`.neo`, or the full CLR aqname / host-CLR-assembly registration. Each SHALL
remain at its current state: `AppDomain.LoadAssembly` SHALL still require a
Cecil module (sub-surface 2); the identity-based token hashes
(`ILType.GetHashCode` / `ILMethod.GetHashCode`) SHALL remain un-recorded in the
`.neo` (sub-surface 3, APPROACH 1 -- the loader re-registers resolved refs
under a recorded compile-time hash; Approaches 2/3 name-based-hash /
body-rewrite stay REJECTED); the static `.cctor` SHALL remain suppressed under
`ENABLE_NEO_MODE` (`ILType.cs:186-200`) (sub-surface 4); and the standalone
CLI SHALL keep its current reference-assembly handling (sub-surface 5, the
Step-24 `TestCLREnum` gap). The deferral SHALL be recorded in
`.trae/documents/neo-deferred-items.md` under the STEP-25-PARTIAL row. Nothing
in this slice SHALL be promoted to "met" in the spec without a functional gate.

#### Scenario: Cross-AppDomain token re-resolution stays APPROACH 1 (deferred)
- **WHEN** a `.neo` compiled in one AppDomain is loaded into a FRESH
  AppDomain (a Cecil-free load)
- **THEN** S3 partial SHALL NOT support it (the identity-based token hashes do
  not survive the fresh AppDomain)
- **AND** the APPROACH 1 design (record the compile-time `GetHashCode()` per
  ref entry under a `.neo` Version bump; the loader re-registers resolved refs
  under the recorded hash) SHALL be the recorded follow-up
- **AND** Approaches 2/3 (name-based hash / body rewrite) SHALL remain REJECTED

#### Scenario: Static .cctor seeding stays deferred
- **WHEN** an AOT-loaded type declares a static constructor (`.cctor`)
- **THEN** S3 partial SHALL NOT seed it (the `.cctor` stays suppressed under
  Neo; the per-static-field offsets are not in the `NeoTypeDefRecord`)
- **AND** seeding it SHALL remain a follow-up folded with the Cecil-free load
  (sub-surface 2) + a `.neo` format extension for the per-static-field offsets

#### Scenario: The ILType layout + VTable rebuild is the ONLY ILType-decoupling delivered
- **WHEN** the S3 partial slice is reviewed
- **THEN** the rebuild builder SHALL be consumed ONLY by the DEBUG self-check
  (it SHALL NOT replace the Cecil init on a live type in this slice)
- **AND** the Cecil-free functional load (sub-surface 2) + the cross-AppDomain
  re-resolution (sub-surface 3) SHALL remain deferred to a follow-up child


### Requirement: The box T; isinst U peephole fusion is deferred behind a dedicated peephole-pass optimizer child (D-PEEP)

The Neo optimizer SHALL NOT fuse the CIL pair `box T; isinst U` (box a value
type then immediately type-check it) into a single direct check until a
dedicated additive peephole-pass child lands the prerequisite infrastructure
(an adjacent-opcode fusion pass with def-use/liveness analysis of the `box`
dest register + a fused replacement opcode on a standalone `OpCodeR` field).
(Status: DEFERRED - the prerequisite infrastructure does not exist on HEAD; the
Step-22 `PatchKind`/`PatchEntry` mechanism is the wrong shape to host it; no
fix ships. The fusion is a pure optimization: the current two-arm `box;isinst`
path is functionally correct, so this deferral has no correctness cost.)

The dump-gate on HEAD `70505eba`
(`openspec/changes/neo-peephole-isinst/design.md` decisions D1-D3) established
three load-bearing findings a future peephole-pass planner MUST NOT re-derive
the hard way. (1) The Step-22 `PatchKind` enum
(`GenericMethodTemplate.cs:54`: `TypeToken`, `MethodToken`, `IsRefMoveFlag`) and
`PatchEntry` struct (`:74-81`, keyed by `GenericParamIdx` + `CecilToken`) are a
generic-method-template T-IDENTITY VALUE-SUBSTITUTION mechanism -- they record
"substitute the concrete-T-derived value at a FIXED instruction index." A
peephole fusion is an OPCODE-STREAM REWRITE (pattern-match the pair, prove the
`box` dest is dead after the `isinst`, replace the pair with one fused op,
remove the `box`); the patch table has no field for "the second opcode of the
pair" / "the fused replacement" / "the dead-register predicate" and its applier
(`GenericMethodTemplate.cs:596-604`) has no rewrite logic. (2) NO peephole/
fusion pass exists in the optimizer (the `RegisterVM/` tree has zero matches
for `peephole|fuse|fusion|IsinstResult`; the existing passes -- FCP, BCP, ELDC,
InlineMethod, RegisterCleanup, the Neo back-half `TypeSpecializeNeoOpcodes`/
`AllocateLocalStackSpaces`/`LowerNeoOffsets` -- do not pattern-match adjacent
opcodes into a fused form). (3) `box T; isinst U` IS emitted as two adjacent
register opcodes from the same CIL-translate arm (`JITCompiler.cs:2637-2645`)
and runs correctly today via two independent `ExecuteNeo` arms (`Box`
`ILIntepreter.Neo.cs:2660`, `Isinst` `:3072-3096`); copy-propagation can move
the `box` away from the `isinst`, so a correct fusion pass needs real def-use/
liveness, not a trivial adjacency peephole.

A future peephole-pass child that lands this fusion SHALL satisfy ALL of: (a)
it is a NEW additive optimizer pass with adjacent-pattern-match + def-use/
liveness analysis of the `box` dest register (the fusion is legal ONLY when the
boxed object is unobserved after the `isinst`); (b) the fused replacement
opcode's payload lives on a STANDALONE `OpCodeR` field whose byte range does NOT
alias a wide-immediate field a runtime consumer reads (`OperandLong`/
`OperandDouble` @12-19, `OperandFloat` @8-11) -- the F-8 / OPT-HARDEN-K1 /
double-combine OpCodeR-union-aliasing discipline; (c) it MUST NOT be implemented
as a `PatchKind.IsinstResult` extension to the Step-22 patch table (wrong
abstraction; see finding 1); (d) it is gated `#if ENABLE_NEO_MODE` (Neo-only)
and a stash-toggle plain-`Debug` + `useRegister=true` NeoStep-filter run shows
the SAME pre-existing Legacy failure set; (e) an adversarial equivalence probe
proves the fused form produces the SAME result as the un-fused `box;isinst` for
the null case, the wrong-type case, the subclass case, AND the case where the
boxed object IS observed after the check (fusion correctly declined). The
canonical `neo-optimizer` "PatchEntry captures only T-identity operand sites"
requirement is UNCHANGED and still governs the Step-22 patch table; this
requirement only records that D-PEEP is not a PatchEntry.

#### Scenario: No box;isinst fusion ships without a peephole-pass child
- **WHEN** the optimizer runs on HEAD `70505eba` (no peephole-pass child has
  landed)
- **THEN** a CIL `box T; isinst U` sequence MUST execute as two opcodes (a `Box`
  followed by an `Isinst`) with NO fused form emitted, because no fusion pass
  exists in the optimizer
- **AND** the result MUST be correct (the two-arm path is the only path), so the
  absence of fusion is a perf cost only, not a correctness gap

#### Scenario: The Step-22 PatchKind is not extended with IsinstResult
- **WHEN** a future change proposes to fuse `box T; isinst U` by adding an
  `IsinstResult` value to the Step-22 `PatchKind` enum
- **THEN** that proposal MUST be rejected: `PatchEntry`
  (`GenericMethodTemplate.cs:74-81`) is a value-substitution at a fixed
  instruction index (keyed by `GenericParamIdx` + `CecilToken`); a peephole
  fusion is an opcode-stream rewrite (delete the `box`, merge into the `isinst`)
  that the patch table's fields and applier cannot express
- **AND** the future fusion MUST instead be a new additive optimizer pass +
  a fused opcode on a standalone `OpCodeR` field

#### Scenario: A correct fusion requires liveness, not adjacency
- **WHEN** a future fusion pass considers fusing a `Box` into a following
  `Isinst`
- **THEN** the pass MUST prove the `Box` dest register is dead after the
  `Isinst` (the boxed object is not observed by any later instruction), because
  copy-propagation can move the `Box` away from the immediately-adjacent
  `Isinst` and an observed box makes the fusion illegal
- **AND** a fusion that fires only on trivial adjacency (no liveness) MUST be
  rejected as incorrect

#### Scenario: A future fused opcode obeys the OpCodeR-union discipline
- **WHEN** a future fusion lands a fused replacement opcode carrying both the
  `box` type token T and the `isinst` type token U
- **THEN** the opcode's payload MUST live on standalone `OpCodeR` fields whose
  byte ranges do NOT alias `OperandLong`/`OperandDouble` (@12-19) or
  `OperandFloat` (@8-11)
- **AND** the pass MUST verify per-opcode-kind disjointness (the F-8
  `LowerNeoOffsets` `Operand3` @16-clobbers-`OperandDouble` discipline), so the
  fusion does not reintroduce the OpCodeR-union-aliasing defect class

#### Scenario: The current box;isinst path is correct without fusion
- **WHEN** an IL method under `ENABLE_NEO_MODE` evaluates `boxedLocal is U` or
  `( boxedLocal as U )` on a value-type-typed local (the C# compiler emits
  `box T; isinst U`)
- **THEN** the result MUST be correct on HEAD without any fusion (null source
  -> null; matching type -> the reference; non-matching type -> null), proving
  the fusion is a pure optimization with no correctness gap
- **AND** the Step-15 `NeoStep15_*` smoke exercising `isinst` MUST stay green
  (it already covers this shape through the un-fused path)

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and runs the Legacy
  register VM
- **THEN** the Legacy NeoStep-filter smoke MUST show the SAME pre-existing
  failures with and without any future fusion change, because the fusion pass +
  the fused opcode SHALL be gated `#if ENABLE_NEO_MODE` and compile out of
  plain `Debug`

#### Scenario: Activation requires the prerequisite child
- **WHEN** no dedicated peephole-pass optimizer child has landed (the
  prerequisite -- a new fusion pass + liveness + a standalone-field fused
  opcode -- does not exist)
- **THEN** this requirement stays DEFERRED and no fusion code SHALL ship
- **AND WHEN** a peephole-pass child lands that satisfies conditions (a)-(e)
  above
- **THEN** this requirement becomes ACTIVE for the fused shapes that child
  covers, and the child's adversarial equivalence probe is the gate


### Requirement: Standalone AOT CLI registers host CLR reference assemblies with the host CLR

The standalone `ilrt_neoc` precompile CLI SHALL register every reference
assembly passed on the command line with the host CLR via
`System.Reflection.Assembly.LoadFrom` (in the file-path `NeoCompiler.Compile`
overload), so the compile AppDomain's CLR-type fallback
(`AppDomain.GetType(string)`, the live
`System.AppDomain.CurrentDomain.GetAssemblies()` scan) resolves host CLR types
as `CLRType`. This mirrors the in-process runtime model, where a host CLR
assembly defining a type referenced by the IL (e.g. a CLR enum such as
`TestCLREnum`) is resident in the host `System.AppDomain` and is found by the
same fallback without any explicit registration call.

The registration SHALL be best-effort: a reference that cannot be CLR-loaded
(BCL assembly already loaded, native/ref-only metadata, missing file) SHALL be
caught and skipped, and resolution SHALL fall back to the existing CLR/BCL
scan. A failed `LoadFrom` SHALL NOT fatal-abort the compile.

#### Scenario: Input referencing a host CLR enum compiles without a CLR-resolution fatal

- **WHEN** `ilrt_neoc` is run on an input IL assembly whose methods reference a
  host CLR enum defined in a non-BCL host CLR assembly, AND that host assembly
  is passed as a reference path
- **THEN** the compile SHALL resolve the enum's Cecil `TypeReference` token to
  a `CLRType` (NOT an `ILType`) and SHALL write a valid `.neo` (magic
  `0x494C524E`) with exit code 0 (clean) or 2 (partial, for unrelated
  unimplemented-op skips), and SHALL NOT emit `Cannot find Type` on stderr.

#### Scenario: Host CLR ref that cannot be CLR-loaded is skipped, not fatal

- **WHEN** a reference path passed to the CLI is not CLR-loadable (already
  loaded, ref-only metadata, or missing) AND `Assembly.LoadFrom` throws
- **THEN** the CLI SHALL catch the exception, skip that reference, and continue
  compiling without fatal-aborting (exit 1 is reserved for input-load /
  serializer fatals, not for a skipped reference).

#### Scenario: Host CLR type resolves at .neo load time via the existing CLR path

- **WHEN** a `.neo` produced from a host-CLR-type-referencing input is loaded
  in an AppDomain where the host CLR assembly is registered
- **THEN** the `.neo` loader SHALL resolve the host CLR type via
  `NeoAssemblyLoader.ResolveTypeRefToIType` -> `appdomain.GetType(fullName)`
  (the same CLR fallback), producing the same `CLRType` the compile recorded,
  with NO `.neo` format extension required.

### Requirement: Host CLR reference assemblies MUST NOT be registered via the IL LoadAssembly path

The standalone CLI SHALL NOT register a host CLR reference assembly via
`AppDomain.LoadAssembly` (the IL hotfix load path). `LoadAssembly`-ing a host
CLR assembly wraps its types as `ILType` in `mapType`, which `AppDomain.GetType
(string)` returns BEFORE reaching the CLR fallback, shadowing the real CLR type
with an `ILType`. For a CLR enum this shadow both mis-resolves the type and
produces a downstream failure during compile.

#### Scenario: Host CLR enum is not shadowed by an ILType wrap

- **WHEN** a host CLR assembly defining a CLR enum is passed as a reference to
  the CLI
- **THEN** the CLI SHALL NOT call `AppDomain.LoadAssembly` on it, and the
  enum's Cecil `TypeReference` SHALL resolve to a `CLRType` (the real CLR
  enum), so that a method reading the enum after `.neo` load + attach executes
  the enum equality correctly (the enum value round-trips).

#### Scenario: Removed LoadAssembly-the-ref path regresses no verified scenario

- **WHEN** the prior `LoadAssembly(refStream)` reference loop is replaced by
  the `Assembly.LoadFrom` registration
- **THEN** no previously-green self-check or smoke (NeoStep, NeoStep22/23/24/25
  self-checks) SHALL regress, because the replaced path had no verified
  coverage (Step-24 V1-B was BCL-refs-only; the ref-`LoadAssembly` path was
  V1-A-UNVERIFIED).


### Requirement: Standalone AOT CLI gracefully skips IL types whose CLR base or interface needs an unregistered CrossBindingAdaptor

The standalone `ilrt_neoc` precompile CLI SHALL NOT fatal-abort when an input
IL type's CLR-class base or CLR interface needs a `CrossBindingAdaptor` that
is not registered in the compile AppDomain. The skip SHALL be implemented in
`NeoCompiler.CompileCore` (the shared core both the file-path
`Compile(string, IReadOnlyList<string>, Stream)` overload and the host-side
`Compile(IReadOnlyList<ILType>, Stream)` overload call). The trigger is the
lazy CLR-base or CLR-interface adaptor resolution that throws
`TypeLoadException("Cannot find Adaptor for:...")` (the throw sites at
`ILType.cs:1505` / `:1568` / `:1593`, keyed on `appdomain.CrossBindingAdaptors`).
Such a type -- whose CLR base or
CLR interface needs a `CrossBindingAdaptor` that is NOT registered in the
compile AppDomain (the typical case for a test-harness-specific or
application-specific adaptor the generic CLI does not carry) -- SHALL be
SKIPPED at the TYPE level: every method on the type SHALL be omitted from the
`.neo`, the type SHALL be recorded in the `NeoCompilerResult.Skipped` report
with a clear type-level display marker, and the compile SHALL continue with
the survivor subset. The `.neo` SHALL still be written (a valid file with the
survivor methods, templates, and type defs), and the CLI SHALL return exit
code `2` (partial) -- NOT exit code `1` (fatal). This is the SAME additive-skip
contract as the existing per-method try/catch in `CompileCore` (a method that
throws during compile is recorded as a skip and omitted), lifted from METHOD
granularity to TYPE granularity.

The skip SHALL be implemented as a per-type PRE-FILTER at the top of
`CompileCore`: each input type's lazy adaptor resolution SHALL be eagerly
triggered (by touching the type's `FirstCLRBaseType` and `FirstCLRInterface`
getters, which drive `InitializeBaseType` / `InitializeInterfaces`) inside a
per-type try/catch that catches `TypeLoadException`; a type whose init throws
SHALL be excluded from the survivor list, and ONLY the survivor list SHALL be
passed to the per-method compile loop AND to `NeoAssemblyWriter.Write`.
Because the ILType init is memoized (`baseTypeInitialized` /
`interfaceInitialized`), a survivor's later `BaseType` access -- including
inside `NeoAssemblyWriter.BuildTypeDefRecord` (`NeoAssemblyWriter.cs:755` /
`:762`) -- SHALL NOT re-throw. The skip SHALL catch `TypeLoadException` (the
adaptor-absence exception) specifically; any OTHER exception during type init
SHALL propagate unchanged (a genuine non-`TypeLoadException` type-init failure
-- e.g. a real `NullReferenceException` from a logic bug -- remains a loud
fatal, exit 1, not a silent skip).

The pre-filter SHALL ALSO eagerly trigger the type's FIELD init (by touching
`type.TotalPrimitiveSize`, whose getter calls `ILType.InitializeFields` when
`fieldMapping == null`) inside the SAME per-type try/catch. An input IL type
with a field whose type fails to resolve -- e.g. a compiler-generated
anonymous OPEN generic type definition (`<>f__AnonymousType0`2<j,k>`) whose
generic-parameter field yields a null field type via `FindGenericArgument`, or
any type with an otherwise-unresolvable field type -- SHALL be SKIPPED at the
type level by the SAME mechanism: `ILType.InitializeFields` SHALL throw
`TypeLoadException("Cannot resolve field type: ...")` (mirroring the adaptor-
lookup throw sites at `ILType.cs:1505` / `:1568` / `:1593`) when a field's
resolved type is null, INSTEAD of null-dereferencing; the pre-filter's
`TypeLoadException` catch then records the skip and the `.neo` is still
written for the survivor subset (exit 2). An unresolvable field type is a
LOAD failure (a `TypeLoadException`), in the same category as the adaptor
absence -- it is NOT a "non-adaptor type-init failure" that must stay fatal.
The `InitializeFields` TLE throw SHALL be `#if ENABLE_NEO_MODE`-gated (it
sits inside the same Neo block that already gated the null-deref), so Legacy
is byte-identical.

A method with no Cecil body -- a delegate's `Invoke` / `BeginInvoke` /
`EndInvoke` (runtime-implemented), or any abstract / extern / PInvoke method
(`MethodDefinition.HasBody == false`) -- has no IL to AOT-compile. The
CompileCore per-method loop SHALL silently omit such methods (mirroring the
`IsGenericInstance` silent-skip), so they never reach
`NeoAssemblyWriter.CompileFresh` (whose JIT would null-deref the absent body).
This is a PRE-COMPILE filter, not a broadened catch: a body-bearing method
whose JIT throws is still recorded as a per-method skip, and a genuine
`CompileFresh` bug on a body-bearing method still stays a loud fatal.

The CLI SHALL NOT couple, reference, or register any test-harness-specific
adaptor (the adaptors `ILRuntimeHelper.Init` registers in
`ILRuntimeTestBase/Adapters/helper.cs:22-29`, or any application-specific
adaptor). The generic compile tool SHALL be robust to the ABSENCE of any
adaptor it does not ship: a type needing such an adaptor is SKIPPED, never
resolved, by this mechanism.

#### Scenario: Full TestCases.dll compiles to a valid .neo with no adaptor fatal

- **WHEN** `ilrt_neoc TestCases.dll out.neo <refs>` is run on the full
  `TestCases.dll` (which contains IL types inheriting harness-adaptor CLR
  classes such as `TestClass2`, `TestClass3`, `TestClass4`,
  `ClassInheritanceTest`, `ClassInheritanceTest2<T>`, and `IDisposable`) and
  the host CLR assembly is passed as a reference
- **THEN** the CLI SHALL write a valid `out.neo` (magic `0x494C524E`) and
  return exit code `0` (clean) or `2` (partial)
- **AND** the CLI SHALL NOT emit `Cannot find Adaptor` as a FATAL on stderr
  (the `ilrt_neoc: FATAL: serializer failure: Cannot find Adaptor for:...`
  line that occurs on HEAD before this change SHALL NOT appear)
- **AND** every IL type whose CLR base/interface needs an unregistered
  adaptor SHALL appear in the skip report (a `SKIP (type) <FullName>:
  TypeLoadException: Cannot find Adaptor for:...` line per skipped type)

#### Scenario: An adaptor-requiring IL type is skipped at the type level, not fatal

- **WHEN** the input contains an IL type `X` whose non-generic CLR base is a
  class `B` for which NO `CrossBindingAdaptor` is registered in the compile
  AppDomain (the `TestClass2` shape), alongside other IL types that have no CLR
  base (or a built-in-adaptor CLR base)
- **THEN** the compile SHALL skip `X` entirely: NO method of `X` SHALL appear
  in the `.neo` `MethodDefTable`, NO template of `X` SHALL appear in the
  `TemplateTable`, and NO `NeoTypeDefRecord` for `X` SHALL be emitted
- **AND** `X` SHALL be recorded in `NeoCompilerResult.Skipped` with a type-
  level display marker (e.g. `(type) <X.FullName>`) and the
  `TypeLoadException` message
- **AND** the other (resolvable) IL types SHALL compile and be emitted
  normally (the skip is scoped to the adaptor-requiring type, not contagious)
- **AND** `NeoCompilerResult.IsComplete` SHALL be `false`, driving exit code
  `2` (the `.neo` is written; the run is partial)

#### Scenario: The generic-instance CLR-base adaptor case is also skipped

- **WHEN** the input contains an IL type whose base is a GENERIC-INSTANCE CLR
  class needing an adaptor (e.g. `class X : ClassInheritanceTest2<X>`, the
  `ILType.cs:1568` throw site) and no matching adaptor construction is
  registered
- **THEN** the compile SHALL skip the type (record it in `Skipped`, omit its
  methods/templates/type-def, continue with survivors, exit `2`) -- NOT fatal

#### Scenario: The CLR-interface adaptor case is also skipped

- **WHEN** the input contains an IL type that IMPLEMENTS a CLR interface for
  which no adaptor is registered (the `ILType.cs:1505` throw site)
- **THEN** the compile SHALL skip the type (record it in `Skipped`, omit its
  methods/templates/type-def, continue with survivors, exit `2`) -- NOT fatal

#### Scenario: A type whose CLR base resolves via a built-in adaptor is NOT skipped

- **WHEN** the input contains an IL type `class X : System.Exception` (or
  `class Y : System.Attribute`) and the compile AppDomain is constructed via
  `new AppDomain()` (whose ctor registers `Adapters.ExceptionAdaptor` at
  `AppDomain.cs:244` and `Adapters.AttributeAdapter` at `AppDomain.cs:237`)
- **THEN** the pre-filter's eager `FirstCLRBaseType` access SHALL find the
  built-in adaptor in `appdomain.CrossBindingAdaptors` and SUCCEED
- **AND** the type SHALL NOT be skipped -- its methods SHALL be compiled and
  emitted (a built-in-adaptor type resolves TODAY; this change preserves that)
- **AND** no additional built-in adaptor registration SHALL be added by this
  change (the ctor's registration is the single source)

#### Scenario: A type with an unresolvable field type is skipped, not fatal (A2 closure)

- **WHEN** the input contains an IL type with a field whose type fails to
  resolve in the compile AppDomain -- e.g. a compiler-generated anonymous OPEN
  generic type definition (`<>f__AnonymousType0`2<j,k>`) whose generic-
  parameter field yields a null field type via `FindGenericArgument`, or any
  type whose `appdomain.GetType(field.FieldType)` returns null
- **THEN** `ILType.InitializeFields` SHALL throw `TypeLoadException("Cannot
  resolve field type: ...")` (mirroring the adaptor-lookup throw sites)
  INSTEAD of null-dereferencing the field type
- **AND** the pre-filter (which eagerly touches `type.TotalPrimitiveSize`,
  triggering `InitializeFields`) SHALL catch that `TypeLoadException` and
  record the type as a type-level skip
- **AND** the compile SHALL write the `.neo` for the survivor subset and
  return exit `2` (partial) -- NOT exit `1` (fatal) on a `NullReferenceException`
- **AND** the `InitializeFields` TLE throw SHALL be `#if ENABLE_NEO_MODE`-gated
  (Legacy byte-identical under plain `Debug`)

#### Scenario: A method with no Cecil body is silently omitted, not fatal (A3 extension)

- **WHEN** the input contains a method with `MethodDefinition.HasBody == false`
  -- a delegate's `Invoke` / `BeginInvoke` / `EndInvoke` (runtime-implemented),
  or an abstract / extern / PInvoke method
- **THEN** the CompileCore per-method loop SHALL silently omit it (it has no
  IL to AOT-compile), so it never reaches `NeoAssemblyWriter.CompileFresh`
  (whose JIT would null-deref the absent body)
- **AND** the `.neo` loader's existing additive JIT fallback SHALL handle it
  at load time
- **AND** this SHALL be a pre-compile filter (a body-bearing method whose JIT
  throws is still recorded as a per-method skip; a genuine `CompileFresh` bug
  on a body-bearing method stays a loud fatal)

#### Scenario: A genuine non-TypeLoadException type-init failure stays a loud fatal, not a silent skip

- **WHEN** a type's lazy init throws an exception that is NEITHER an adaptor-
  absence `TypeLoadException` NOR a field-type-resolution `TypeLoadException`
  -- i.e. a genuine non-`TypeLoadException` failure (e.g. a real
  `NullReferenceException` from a compile-tool LOGIC bug, not an
  unresolvable-type load failure)
- **THEN** the pre-filter SHALL NOT catch it (the catch is scoped to
  `TypeLoadException`); it SHALL propagate to `CompileCore`'s outer wrapper
  and surface as `ilrt_neoc: FATAL: ...` (exit `1`)
- **AND** the skip report SHALL NOT contain a spurious entry for it (a real
  bug is not masked as a graceful skip)
- **NOTE** an unresolvable field type (an open-generic definition's generic-
  parameter field, or any field whose `appdomain.GetType` returns null) is a
  LOAD failure: `ILType.InitializeFields` throws `TypeLoadException` for it
  (mirroring the adaptor-lookup sites), so the pre-filter DOES skip it (exit
  2). It is NOT an example of this "stays fatal" scenario -- only a non-TLE
  failure stays fatal.

#### Scenario: No test-harness-specific adaptor is coupled into the generic CLI

- **WHEN** the change is built and the `ILRuntimeNeoCompiler` project's
  references are inspected
- **THEN** the project SHALL NOT reference `ILRuntimeTestBase` or any
  test-framework assembly
- **AND** no `RegisterCrossBindingAdaptor` call for a test-harness-specific
  adaptor (`TestClass2Adapter`, `TestClass3Adaptor`, `TestClass4Adaptor`,
  `ClassInheritanceTestAdaptor`, `ClassInheritanceTest2Adaptor`,
  `InterfaceTestAdaptor`, `IDisposableAdapter`,
  `IAsyncStateMachineClassInheritanceAdaptor`) SHALL be added to `NeoCompiler`
  or the CLI
- **AND** the robustness SHALL come entirely from the graceful-SKIP mechanism
  (a type needing such an adaptor is skipped), NOT from coupling the adaptor

#### Scenario: The skip is purely compile-side; the .neo loader is unchanged

- **WHEN** a `.neo` produced by this change (a partial `.neo` missing the
  adaptor-requiring types) is loaded by `NeoAssemblyLoader.Attach`
- **THEN** the loader SHALL behave exactly as before this change: it SHALL
  attach every method def it can bind and SKIP every method/type it cannot
  bind (the existing additive load contract)
- **AND** no `.neo` format change, no `NeoAssemblyLoader` change, no
  `NeoAssemblyWriter` change, and no `AppDomain` change SHALL be introduced by
  this requirement. (The ONE `ILType` change in scope is the Neo-gated
  `InitializeFields` field-type `TypeLoadException` throw described above; no
  other `ILType` change. The loader is unchanged -- the throw is a compile-time
  load failure that drives a type-skip, not a loader behavior.)

#### Scenario: Legacy ExecuteR is unaffected

- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and the Legacy
  register VM runs the NeoStep-filter smoke
- **THEN** the smoke SHALL show the SAME pre-existing Legacy failure set with
  and without this change (stash-toggle proof), because the pre-filter and the
  `MakeTypeSkip` helper are inside `NeoCompiler.cs` (gated `#if
  ENABLE_NEO_MODE`, compiles out under plain `Debug`), AND the
  `ILType.InitializeFields` field-type `TypeLoadException` throw is inside the
  SAME `#if ENABLE_NEO_MODE` block that already gated the null-deref -- so
  plain `Debug` is byte-identical (the plain-`Debug` ILRuntime build is 0
  errors; the null-fieldType path was already latent in Legacy and is not
  reached by the Neo-only AOT CLI)

#### Scenario: The host-side Compile overload gets the skip for free

- **WHEN** the host-side `NeoCompiler.Compile(IReadOnlyList<ILType>, Stream)`
  overload is invoked (the V1-A self-check path)
- **THEN** it SHALL route through the SAME `CompileCore` pre-filter, so an
  adaptor-requiring type in the explicit type set is skipped (recorded in
  `Skipped`, omitted, exit-2-equivalent) with NO separate edit


*Step-25 / Step-26 partial-ship scope note.* The S1 requirements (non-generic methods, same-AppDomain), the S2 requirements (generic-instantiation-at-load for the no-T-identity-token slice), the Step-26 requirements (benchmark self-check + single-threaded contract), the S3-partial requirement (ILType layout + Neo VTable rebuild from a `.neo` `NeoTypeDefRecord`), the S3-5 requirement (standalone AOT CLI registers host CLR reference assemblies via `Assembly.LoadFrom`), and the STEP-25-CLR-ADAPTOR requirement above (standalone CLI gracefully skips IL types needing an unregistered CrossBindingAdaptor + types with unresolvable field types + bodyless methods; full TestCases.dll compiles to a valid `.neo`, exit 2, no fatal) are SHIPPED. DEFERRED follow-up scopes (the canonical spec does NOT describe these as met): the S3 remainder -- Cecil-free AppDomain load (sub-surface 2), cross-AppDomain token-hash re-resolution APPROACH 1 (sub-surface 3), static `.cctor` seeding (sub-surface 4); the D-PEEP `box;isinst` peephole fusion (needs a dedicated peephole-pass + liveness child); the Neo debugger variable inspection (`neo-debugger-neo-frame`); and the F-4 reflection-on-Neo field-read family. The S1 loader consumes `NeoAssemblyModel.MethodDefs`; the S2 loader additionally consumes the `TemplateTable`; the S3-partial additionally rebuilds ILType layout + VTable from the `TypeDefTable` as a DEBUG completeness proof; S3-5 additionally registers host CLR refs with the host CLR; STEP-25-CLR-ADAPTOR makes the standalone CLI robust on arbitrary assemblies (no fatal; graceful type/method skips; exit 0/2). None performs cross-AppDomain hash re-resolution (S3 sub-surface 3). The Step-26 benchmark self-check measures interpreter throughput on a fixed workload and asserts measurement correctness; it does NOT assert a Neo-vs-Legacy ratio threshold.