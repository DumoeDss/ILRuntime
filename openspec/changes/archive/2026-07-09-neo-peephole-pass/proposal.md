## Why

D-PEEP is the `box T; isinst U` peephole fusion: a compile-time rewrite that
would fuse a value-type box immediately followed by a type-check into a single
direct check, avoiding the heap-box allocation. The prior D-PEEP child
(`archive/2026-07-08-neo-peephole-isinst`) closed it as scoped-deferral on the
premise that hosting the fusion "requires substantial NEW infrastructure (a
peephole-pass framework + liveness + a fused opcode)." This change re-runs the
dump-gate on HEAD `162ce992` and reaches a STRONGER close: the prior "needs a
new framework" premise is STALE (the Step-17 `liveAliases`/`liveAliasMap`
machinery in `Optimizer.Neo.cs` IS a live-range-aware def-use framework a fusion
pass could reuse), AND an empirical frequency probe finds the fused pattern is
NEVER emitted in practice. Building an additive pass for a pattern the C#
compiler does not emit would be premature optimization with zero measured payoff.
This change records the evidence-based close as an accepted-known optimization
deferral (the F-11 precedent -- closed as "not-a-bug, an optimization gap, no
reproducing hot path").

## What Changes

- **No source change.** No `ILRuntime/` edit. No JIT, optimizer, runtime, or
  `ExecuteNeo` change. No new opcode, no new pass. The current 2-arm
  `box;isinst` execution path is correct and stays as the only path.
- **Dump-gate verdict recorded in `design.md`** with file:line + empirical
  evidence: (a) the fusion pass IS tractable on HEAD (the Step-17
  `addrAlias`/`liveAliases`/`liveAliasMap` machinery is a reusable live-range
  def-use framework, refuting the prior child's "needs substantial new infra"
  premise); (b) the fused opcode encoding IS available (`Operand` T-token @8 +
  `Operand2` U-token @12, both standalone, F-8-disjoint from wide-immediates);
  (c) but the pattern is NEVER emitted: a Cecil-based scan of three real DLLs
  (TestCases + ILRuntime.dll + ILRuntimeTestBase.dll) found **0 adjacent
  `box;isinst` pairs across 1071 boxes and 1568 isinsts** -- the C# compiler
  elides the box when the source is already `object` and folds value-type
  checks via constrained-virtual / `Unbox_Any`, so the JIT-time hit rate is
  zero; the optimizer passes synthesize no boxes.
- **Update the one DEFERRED requirement in `neo-optimizer`** (the
  `box T; isinst U` peephole-fusion deferral the prior child added) with the
  post-dump-gate finding: the framework prerequisite is now MET (Step-17
  machinery), the encoding is available, but the fusion is deferred on
  PAYOFF grounds (zero observed adjacency), not infra grounds. The "must be a
  new additive pass + a standalone-field fused opcode, never a PatchKind
  extension" contract is preserved and strengthened with the encoding answer.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `neo-optimizer`: update the existing DEFERRED requirement for the
  `box T; isinst U` peephole fusion with the HEAD `162ce992` dump-gate finding
  (framework prerequisite MET via Step-17; encoding = `Operand`+`Operand2`;
  deferred on zero-payoff, not on missing-infra). Chosen over `neo-type-checks`
  because the deferral is a statement about the OPTIMIZER layer (no fusion pass
  fires; the patch-infra discipline lives here) and `neo-optimizer` is the
  capability that validates cleanly under the repo's flaky validator.

## Impact

- **`openspec/changes/neo-peephole-pass/specs/neo-optimizer/spec.md`** -- one
  MODIFIED requirement (delta; the existing DEFERRED requirement's rationale
  updated from "needs a new framework" to "framework exists, encoding
  available, deferred on zero observed payoff"). Merged into
  `openspec/specs/neo-optimizer/spec.md` at archive.
- **No `ILRuntime/` source change.** No runtime, JIT, optimizer, opcode, or
  public-API change. No test change (the `box;isinst` shape is already covered
  by the Step-15 `NeoStep15_TC4_BoxedValueTypeIs` smoke; the fusion is a
  non-functional speedup, so no regression guard is added -- the current path
  is already correct).
- **Regression risk: NONE.** No executable code changes. The full `NeoStep`
  smoke is unchanged. Legacy (`ExecuteR`) is the reference and is untouched.
  This is a documentation + spec-delta-only change.
