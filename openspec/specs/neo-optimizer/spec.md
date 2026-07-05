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
