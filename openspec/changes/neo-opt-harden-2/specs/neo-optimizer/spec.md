# Capability: neo-optimizer

Correctness invariants of the Neo code-path through the shared optimizer passes
(Forward Copy Propagation / Backward Copy Propagation / copy-prop), the
conv/compare type-specialization, and the CLR value-type local / return-write
representation. These are hard invariants the optimizer + frame-layout +
return-write paths MUST preserve for the Neo register VM (`ExecuteNeo`) to
produce correct results.

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

## MODIFIED Requirements

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

## UNMODIFIED Requirements

### Requirement: FCP respects value-type copy independence after a field mutation
> UNCHANGED by this change. See the ACTIVE requirement in the parent spec; the
> K1 ldloca-kill (`Optimizer.FCP.cs`, Neo-only, Legacy-neutral) is implemented
> and enforced. This change does not modify FCP.

### Requirement: Q-STRUCT struct-mutation-then-read temp-renumber quirk is tracked
> UNCHANGED. Still DEFERRED (not reproducible on current HEAD). This change
> does not modify BCP / copy-prop.

### Requirement: Q-LONG conv.i8 long-compare quirk is tracked
> UNCHANGED. Still DEFERRED (not reproducible on current HEAD). This change
> does not modify the conv/compare lowering.
