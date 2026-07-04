## ADDED Requirements

### Requirement: FCP respects value-type copy independence after a field mutation

Forward Copy Propagation (FCP) MUST NOT propagate a field read of a
value-type-copy destination across a write to a field of the propagation
source. A whole value-type `Move` (`S b = a`) creates an independent snapshot
in `b`; an intervening `stfld` that writes a field of the source `a` MUST
invalidate any propagation that rewrote a later `b.field` read to `a.field`.

This kill MUST apply when a `Ldloca`/`Ldloca_S` takes the address of the
propagation source (`xSrc`) or destination (`xDst`), because the local can
then be mutated through that address by a later `stfld`/`stind`. The base
local whose address is taken is the Ldloca source register (`op.Register2`),
which FCP scans as `ySrc`; the kill MUST fire when `ySrc == xSrc` or
`ySrc == xDst`. (The field store reaches the base indirectly via the
`ldloca` address handle, so a kill keyed on the `Stfld_*_Inline` owning
slot `Register1` cannot fire — `Register1` is the address temp, not the
base local.)

This requirement is Neo-only: it MUST be a no-op for Legacy `ExecuteR`. The
kill MUST be gated to opcodes that exist only under `ENABLE_NEO_MODE`, so the
Legacy FCP control flow is byte-identical to before this change.

#### Scenario: Copy then mutate source primitive field, read dest field
- **WHEN** a value-type copy `b = a` is followed by `a.n = <new value>` and a
  later read of `b.n`
- **THEN** the read of `b.n` MUST return the value `b.n` held at copy time
  (the original), NOT the mutated source value
- **AND** FCP MUST have killed the `b.n -> a.n` propagation at the `stfld a.n`

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
  kill executes), because the kill is gated to Neo-only `_Inline` opcodes that
  Legacy never emits

### Requirement: Q-STRUCT struct-mutation-then-read temp-renumber quirk is tracked

The Q-STRUCT optimizer temp-renumber quirk (struct local + later field mutation + element/branch read, surfaced by Step 16) MUST remain a tracked, open item until a reproducing test case on current HEAD is provided.
It MUST NOT be fixed by a guessed change to the shared BCP / copy-prop passes.
A fix SHALL land only when a test case that reproduces the quirk on current
HEAD accompanies it.

#### Scenario: Deferred until reproduced
- **WHEN** no test case reproduces the Q-STRUCT quirk on current HEAD
- **THEN** no optimizer change for this quirk SHALL be shipped
- **AND** the documented probe patterns (`ProbeQStruct_ElementReadAfterMutation`,
  `ProbeQStruct_BranchAfterMutation`) SHALL be retained as non-asserting
  documentation cases so the patterns stay under observation

### Requirement: Q-LONG conv.i8 long-compare quirk is tracked

The Q-LONG quirk (long default-zero compare involving `conv.i8`, surfaced by Step 16) MUST remain a tracked, open item until a reproducing test case on current HEAD is provided.
It MUST NOT be fixed by a guessed change to the conv/compare lowering or the
shared optimizer. A fix SHALL land only when a test case that reproduces the
quirk on current HEAD accompanies it.

#### Scenario: Deferred until reproduced
- **WHEN** no test case reproduces the Q-LONG quirk on current HEAD
- **THEN** no conv/compare or optimizer change for this quirk SHALL be shipped
- **AND** the documented probe patterns (`ProbeQLong_ConvI8ZeroCompare`,
  `ProbeQLong_ScalarZeroCompare`, `ProbeQLong_DefaultFieldZeroCompare`)
  SHALL be retained as non-asserting documentation cases
