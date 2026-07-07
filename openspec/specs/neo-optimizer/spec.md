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
