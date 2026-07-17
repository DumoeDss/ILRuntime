## ADDED Requirements

### Requirement: The Neo-lowered body is dispatch-safe (no garbage opcode, no ip overrun)

The Neo register VM SHALL execute a method body (`method.CompiledFrame.NeoExecuteBody`,
the `OpCodeR[]` produced by `TypeSpecializeNeoOpcodes` + `AllocateLocalStackSpaces` +
`Optimizer.LowerNeoOffsets`) in which **every** instruction's `Code` field is a named
`OpCodeREnum` member (implicit range `0..<max>`), and in which **no** resolved control-flow
target -- a branch `Operand`, an intermediate-branch `Operand4`, a `Switch` jump-table
entry, or an EH `Leave`/`Leave_S` target -- lands outside the body's index range. The
`ExecuteNeo` dispatch loop SHALL detect and throw a **precise, locatable** exception when
either invariant is violated, instead of silently reading a garbage `Code` that could alias
a real opcode and corrupt execution.

Status: ACTIVE. The runtime currently reads `ip->Code = 0x23F70C` (garbage) at ~8
pre-crash sites on the full Neo smoke and throws a misleading
`"Neo: opcode 2359324 not yet implemented (Step 6)"` in its `default:` arm
(`ILIntepreter.Neo.cs:5357`). The defect is Neo-only (Legacy `ExecuteR` is green on the
same test DLL) and lives in the Neo-only lowering pipeline. This requirement is dump-gated:
the fix SHALL land only when an instrumented full-smoke run confirms the candidate defect
(the specific mis-remapped target / un-terminated body / corrupt slot). A guessed,
un-dumped fix MUST NOT ship.

Defect localisation (investigated, not to be re-litigated): `new OpCodeR()`
zero-initialises and every `Translate` arm sets `op.Code = (OpCodeREnum)code.Code`, so a
purely uninitialised `Code` would read as `Nop` (0), not `0x23F70C`. Therefore the garbage
is an **overread past `body.Length`** (the `ExecuteNeo` loop is `while(!returned)` with an
unconditional `ip++` and NO bounds check) or a **mis-targeted `ip`** reading operand bytes
as `Code`. `Optimizer.LowerNeoOffsets` is the SOLE pass that mutates body length: it
deletes synthetic `Push` instructions (in-place shift + `Array.Resize(ref body, ...)`,
`Optimizer.Neo.cs` ~1210-1215) and re-maps targets via `FixBranchTargetsAfterRemove`
(~1662). The resized body IS written back to the frame (`frame.NeoExecuteBody = body;`,
line 1481), so a "stale frame array" cause is REFUTED. The `Push`-deletion path runs only
for calls / `newobj` with more than 3 register parameters, so the trigger involves a
multi-argument call followed by control flow that resolves across the deleted `Push`.

This requirement is Neo-only: the dispatch guard and any lowering fix SHALL be gated
`#if ENABLE_NEO_MODE` (the lowering passes and `ExecuteNeo` are already Neo-only), so
Legacy `ExecuteR` is byte-identical and a stash-toggle plain-`Debug` + `useRegister=true`
`NeoStep`-filter run shows the SAME pre-existing Legacy failure set with and without the
change. The full `NeoStep` smoke SHALL stay 301/301 (ZERO regressions; the fix is scoped to
the confirmed defect, NOT a blanket rewrite of `LowerNeoOffsets`).

#### Scenario: Instrumented default case captures the trigger before the crash
- **WHEN** the `ExecuteNeo` `default:` arm (and loop head) is temporarily instrumented to
  log the declaring method, the body index `(int)(ip - ptr)`, `body.Length`, the raw
  `Code`, and the full 24-byte `OpCodeR` field dump (Register1/2/3/4, Operand /
  OperandLong / Operand2 / Operand3 / Operand4), and the full Neo smoke is run WITHOUT the
  `NeoStep` filter
- **THEN** the instrumentation SHALL fire before the pre-crash `NullReferenceException`
  and capture at least one minimal triggering method + body index + raw-bytes dump
- **AND** the dump SHALL distinguish an **overrun** (`body index >= body.Length`) from a
  **mis-targeted in-range slot** (index valid, `Code` garbage, neighbours valid) from a
  **genuinely corrupt slot** (index valid, neighbours also corrupt)

#### Scenario: ip overrun past the body end is caught loudly, not silently aliased
- **WHEN** execution of a Neo body would advance `ip` to `ptr + body.Length` or beyond
  (the body is not terminated by a reachable `Ret`/throw, OR a branch/`Leave` target
  resolves to an out-of-range index)
- **THEN** `ExecuteNeo` SHALL throw an exception whose message names the declaring method
  and the offending body index and `body.Length`
- **AND** the exception SHALL NOT be the generic `"not yet implemented (Step 6)"` message
  and SHALL NOT silently read a garbage `Code` that could dispatch to a real arm

#### Scenario: A garbage in-range Code is caught with a field dump
- **WHEN** an in-range `ip->Code` is outside the named `OpCodeREnum` range (an operand
  byte pattern read as a `Code` via a mis-targeted `ip`, or a corrupt slot)
- **THEN** `ExecuteNeo` SHALL throw an exception whose message names the declaring method,
  the body index, the raw `Code` value, and the surrounding `OpCodeR` operand/register
  fields, so the offending instruction is identifiable in one read
- **AND** the throw SHALL occur in the `default:` arm (or an equivalent pre-dispatch
  range check) BEFORE the garbage value can match a real `case` label

#### Scenario: Branch / switch / Leave targets are valid indices after Push-deletion
- **WHEN** `LowerNeoOffsets` deletes one or more synthetic `Push` instructions around a
  call / `newobj` with more than 3 register parameters and re-maps control-flow targets via
  `FixBranchTargetsAfterRemove`
- **THEN** every remaining branch `Operand`, intermediate-branch `Operand4`, every entry of
  every affected `Switch` jump-table, AND every EH `Leave`/`Leave_S` target SHALL resolve to
  a valid in-range body index after the deletion(s)
- **AND** no target SHALL be left pointing past `body.Length` or at a shifted-but-un-remapped
  index
- **AND** the regression probe (a method exercising a multi-argument call followed by
  branch/switch/try control flow) SHALL produce the correct result

#### Scenario: Regression probe fails on HEAD without the fix, passes with it
- **WHEN** the new `NeoStep` probe(s) exercising the discovered trigger (multi-argument
  call -> `Push` deletion -> subsequent control flow) are run on HEAD with the fix stashed
- **THEN** at least one probe SHALL fail (wrong result, or the loud overrun/garbage throw)
- **AND WHEN** the fix is applied
- **THEN** all such probes SHALL pass

#### Scenario: Full NeoStep smoke and Legacy baseline are unchanged
- **WHEN** the fix + the dispatch guard are applied (`Debug_Neo`)
- **THEN** the full `NeoStep` smoke SHALL stay 301/301 (ZERO regressions; the fix is
  scoped to the confirmed defect)
- **AND WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` (plain `Debug`) and runs the
  Legacy register VM with `useRegister=true` and the `NeoStep` filter
- **THEN** the Legacy smoke SHALL show the SAME pre-existing failure set with and without
  this change (stash-toggle proof), because the lowering passes, `ExecuteNeo`, and the new
  guard are all gated `#if ENABLE_NEO_MODE` and compile out

#### Scenario: Dump refutes the candidates -- requirement becomes DEFERRED
- **WHEN** the instrumented full-smoke dump shows every suspect body's last instruction is
  a reachable terminator (refuting H2 un-terminated-body), AND every branch/switch/Leave
  target in the suspect bodies is a valid in-range index (refuting H1 mis-remap), AND every
  in-range slot's `Code` is a named member (refuting H3 corrupt slot), yet the garbage
  symptom persists on a path the dump did not capture
- **THEN** this requirement becomes DEFERRED -- no fix SHALL ship
- **AND** the dump artifacts + the instrumented reproducer SHALL be pinned in the change so
  a future reproducing case has a ready home
- **AND** the localisation findings (un-bounded `ExecuteNeo` loop; `LowerNeoOffsets` is the
  sole length-mutating pass; uninitialised-`Code` and stale-frame-array causes REFUTED)
  SHALL remain on record so a guessed fix is NOT mis-shipped

#### Scenario: The dispatch guard is a permanent Neo tripwire, not DEBUG-only
- **WHEN** the runtime is built in `Debug_Neo` (or any Neo release configuration)
- **THEN** the `ExecuteNeo` bounds check and the garbage-`Code` range check SHALL both be
  present (NOT compiled out under a DEBUG-only flag)
- **AND** their per-instruction cost SHALL be a single integer comparison plus, on the
  rare offending instruction, the field dump (negligible beside the dispatch switch)
