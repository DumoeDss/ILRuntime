## Why

Two opportunistic cleanup items have been deferred since Steps 14-15 and are now
trivially resolvable because their blockers landed long ago. (1) The `Cgt_Un`
`ExecuteNeo` arm's divergence comment names only the operand-sentinel divergence
case, but the symmetric source-sentinel case (the `cguA != -1` clause in the same
expression) also diverges from raw unsigned semantics — the comment is
incomplete/misleading. (2) Step 14 TC2 (`NeoStep14_TC2_CatchObjectAccess`)
asserts only `e != null` because the type-check-in-catch (`isinst`) was Step 15
and had not landed at Step 14; Step 15 has since shipped `isinst`, so TC2 can be
tightened to assert the caught exception's type/identity. Both are zero-behavior-
change tidy-ups surfaced in `.trae/documents/neo-deferred-items.md` (N-CGTUN,
N-TC2).

## What Changes

- **N-CGTUN (comment accuracy):** tighten the `Cgt_Un` arm's divergence comment
  in `ILIntepreter.Neo.cs` to name BOTH sentinel-divergence cases — the operand
  case (`cgt.un x, (uint)0xFFFFFFFF`) AND the symmetric source case
  (`cgt.un (uint)0xFFFFFFFF, x`, i.e. the `cguA != -1` early-false clause). The
  runtime expression is unchanged; only the comment is made accurate.
- **N-TC2 (test tighten):** tighten `NeoStep14_TC2_CatchObjectAccess` in
  `TestCases/NeoStep14Test.cs` to assert the caught exception's type/identity
  (via `e is DivideByZeroException`, the `isinst` opcode that landed in Step 15)
  instead of the weak `e != null`. The catch clause already binds
  `DivideByZeroException e`, so the tighten exercises the now-supported type-
  check-in-catch shape.

No runtime behavior changes; no new opcodes; no spec requirement changes.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

(none — this is a comment + test-only change. The `Cgt_Un` opcode is not a
requirement of `neo-type-checks` (which covers `isinst`/`castclass` only); the
TC2 test tighten exercises an existing `neo-type-checks` requirement (`isinst`)
and an existing `neo-exceptions` shape (catch-slot stores the caught object),
without altering either spec's requirements. The `neo-type-checks` and
`neo-exceptions` specs are UNCHANGED. Recorded explicitly so a future reader
does not expect a delta file.)

## Impact

- **`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`** — the
  `Cgt_Un` arm divergence comment (~lines 1018-1032). Comment-only; the
  comparison expression on line 1035 is untouched.
- **`TestCases/NeoStep14Test.cs`** — `NeoStep14_TC2_CatchObjectAccess` body
  (~lines 40-52). Test-only; tightens the catch-body assertion.
- No change to any runtime arm, JIT, optimizer pass, spec requirement, or public
  API. Legacy (`ExecuteR`) is the reference and is untouched.
- **Regression risk: NONE.** Both edits are non-functional. Gate: full
  `NeoStep` smoke stays green (154/154 baseline at HEAD); TC2 itself remains
  green (it tightens, not loosens — it asserts strictly more).
