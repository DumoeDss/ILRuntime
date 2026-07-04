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
