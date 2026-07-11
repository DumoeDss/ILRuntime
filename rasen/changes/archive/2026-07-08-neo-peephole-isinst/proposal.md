## Why

D-PEEP is a Step-15 deferral (`.trae/documents/neo-deferred-items.md` row/section
3 `D-PEEP`): fuse the CIL pair `box T; isinst U` (box a value type then
immediately type-check it) into a single direct check that avoids the box heap
allocation, expressed at the time as a `PatchKind.IsinstResult` patch-table
entry. The portfolio handoff (`neo-completion-portfolio/handoff/lead-1.md`)
tagged it BLOCKED because "neither the fusion pass nor `PatchKind` exists," and
LOWEST priority (pure optimization, non-functional). Since that handoff was
written, `neo-step22-generic-template` introduced a `PatchEntry` struct + a
`PatchKind` enum, so this change re-ran the dump-gate on HEAD `70505eba` to
decide SHIP-the-fusion vs STOP-at-blocker. The verdict is STOP: the Step-22
`PatchKind` is structurally a generic-method-template T-identity VALUE-
SUBSTITUTION mechanism (not an opcode-stream REWRITE), and NO peephole/fusion
pass exists in the optimizer. Hosting the fusion would require substantial NEW
infrastructure (a peephole-pass framework with def-use analysis + a new fused
opcode on a standalone `OpCodeR` field). Building that for a non-functional gain
on the lowest-priority item is the "force a fix past the dump-gate" anti-pattern
documented in the portfolio handoff. This change records the dump-gate verdict
durably and defers D-PEEP behind a dedicated future patch-infra / peephole-pass
child (mirroring the `neo-async-controlflow-iscompleted` scoped-deferral
precedent). The portfolio is COMPLETE either way; this is the last child.

## What Changes

- **No source change.** No `ILRuntime/` edit. No JIT, optimizer, runtime, or
  `ExecuteNeo` change. No new opcode, no new `PatchKind` value, no new
  `PatchEntry` field. The current `box;isinst` execution path (two runtime arms)
  is correct and stays as the only path.
- **Dump-gate verdict recorded in `design.md`** with file:line evidence that
  (a) `PatchKind` exists post-Step-22 but is generic-T-identity-specific and the
  wrong shape for a peephole fusion; (b) no peephole/fusion pass exists in the
  optimizer; (c) the current 2-arm `box;isinst` path is functionally correct
  (pure optimization, no correctness gap).
- **Add ONE DEFERRED requirement to `neo-optimizer`** recording: the
  `box T; isinst U` peephole fusion is deferred behind a dedicated
  peephole-pass child; the Step-22 `PatchKind`/`PatchEntry` mechanism (a
  value-substitution at a fixed instruction index, keyed by `GenericParamIdx` +
  `CecilToken`) MUST NOT be extended with an `IsinstResult` kind because a
  peephole fusion is an opcode-stream rewrite (pattern-match the pair, replace
  with a single fused op, remove the `box`), which the patch table cannot
  express; a future fusion SHALL be a new additive optimizer pass + a fused
  opcode on a standalone `OpCodeR` field (the F-8 / OPT-HARDEN-K1 union-
  aliasing discipline). This mirrors the existing DEFERRED `Q-STRUCT` / `Q-LONG`
  requirements in the same capability.
- **Update `.trae/documents/neo-deferred-items.md`** D-PEEP row (section 2) and
  the section-3 detail: replace the stale "neither the fusion pass nor PatchKind
  exists" rationale with the post-Step-22 finding (PatchKind now exists but is
  the wrong shape; the fusion-pass prerequisite remains), and point the
  "Resolution" at a future dedicated peephole-pass child.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `neo-optimizer`: add a DEFERRED requirement recording the `box T; isinst U`
  peephole-fusion deferral, the dump-gate finding that the Step-22 `PatchKind`/
  `PatchEntry` mechanism is the wrong shape to host it, and the contract a
  future peephole-pass child MUST satisfy (a new additive optimizer pass with
  def-use analysis of the `box` dest register + a fused opcode on a standalone
  `OpCodeR` field, never a `PatchKind.IsinstResult` extension). Chosen over
  `neo-type-checks` because the deferral is fundamentally a statement about the
  OPTIMIZER layer (no fusion pass exists) + the patch-infra discipline that
  already lives in this capability, and `neo-optimizer` is the one capability
  that validates cleanly under the repo's flaky validator.

## Impact

- **`openspec/changes/neo-peephole-isinst/specs/neo-optimizer/spec.md`** -- one
  ADDED DEFERRED requirement (delta; merged into `openspec/specs/neo-optimizer/
  spec.md` at archive).
- **`.trae/documents/neo-deferred-items.md`** -- D-PEEP section-2 row + section-3
  detail updated with the post-Step-22 dump-gate finding (CJK-light, small
  targeted edit to avoid the Write-tool CJK-corruption gotcha).
- **No `ILRuntime/` source change.** No runtime, JIT, optimizer, opcode, or
  public-API change. No test change (the `box;isinst` shape is already covered
  by the Step-15 `NeoStep15_*` smoke; the fusion is a non-functional speedup, so
  no regression guard is added -- there is no FAIL-on-HEAD probe because the
  current path is already correct).
- **Regression risk: NONE.** No executable code changes. The full `NeoStep`
  smoke (218/0/1 at HEAD) is unchanged. Legacy (`ExecuteR`) is the reference and
  is untouched. This is a documentation + spec-delta-only change.
