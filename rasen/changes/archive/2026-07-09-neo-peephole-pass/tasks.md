## 1. Dump-gate evidence (PLANNER -- complete in propose)

- [x] 1.1 Read prior D-PEEP scoped-deferral (`archive/2026-07-08-neo-peephole-isinst/`)
  to load the three load-bearing findings (PatchKind wrong shape; no fusion pass;
  box;isinst emitted adjacently + correct un-fused).
- [x] 1.2 Evaluate the Step-17 `addrAlias`/`liveAliases`/`liveAliasMap` machinery
  (`Optimizer.Neo.cs:35-428`) as the live-range def-use framework a fusion would
  reuse; record the tractability verdict (D1) with file:line evidence.
- [x] 1.3 Determine the fused-opcode encoding for the 2 type tokens (T+U) from the
  `OpCodeR` field layout (`OpCodes/OpCode.cs:36-71`); confirm F-8 disjointness
  (`Operand` @8 + `Operand2` @12 standalone; `Operand3` @16 avoided) -- (D2).
- [x] 1.4 Run a Cecil-based adjacency scan over TestCases.dll + ILRuntime.dll +
  ILRuntimeTestBase.dll counting boxes / isinsts / adjacent box;isinst pairs;
  record the zero-payoff finding (D3).
- [x] 1.5 Record the fused-check semantic hazard (boxed-int-vs-long identity) for
  the future child (D4).

## 2. Scope verdict + artifacts (PLANNER -- complete in propose)

- [x] 2.1 Decide SHIP-the-pass vs CLOSE-as-optimization-deferral on the combined
  D1+D2+D3+D4 evidence; record the verdict (D5: CLOSE on zero-payoff, not on
  missing-infra) in `design.md`.
- [x] 2.2 Write `proposal.md` (WHY: re-gate refutes the prior needs-infra premise;
  WHAT: doc-only CLOSE; capability: `neo-optimizer` MODIFIED).
- [x] 2.3 Write `design.md` (Context, Goals/Non-Goals, D1-D5 with file:line +
  empirical evidence, Risks, Open Questions).
- [x] 2.4 Write `specs/neo-optimizer/spec.md` (MODIFIED delta: the DEFERRED
  requirement re-grounded on zero-payoff; encoding = `Operand`+`Operand2`;
  Step-17 framework acknowledged; full future-child contract preserved; PURE
  ASCII; SHALL-first; Neo-only/Legacy-neutral).
- [x] 2.5 Write `tasks.md` (this file; doc-only, no code/test tasks).

## 3. Apply (NO source/test change -- doc-only change)

- [ ] 3.1 Run `openspec validate neo-peephole-pass` and resolve any delta-format
  issues (the MODIFIED requirement header MUST match the canonical
  `### Requirement: The box T; isinst U peephole fusion is deferred...` exactly;
  scenarios MUST use exactly 4 hashtags).
- [ ] 3.2 Confirm NO `ILRuntime/` source change, NO test change, NO
  `ExecuteNeo`/JIT/optimizer/opcode change shipped (this is a CLOSE: the current
  2-arm `box;isinst` path stays the only path).
- [ ] 3.3 Confirm `neo-peephole-isinst` (the prior child's capability delta) is
  not double-applied -- this change MODIFIES the SAME canonical requirement the
  prior child ADDED; the archive step merges this delta over the already-merged
  prior text (the prior child's findings 1/3 + future-child contract are
  preserved verbatim inside this MODIFIED block; only the Status line + finding 2
  + the encoding answer are corrected).

## 4. Verification (doc-only -- confirmation gates, not regression gates)

- [ ] 4.1 Re-read the canonical `openspec/specs/neo-optimizer/spec.md` D-PEEP
  requirement at archive time to confirm: (a) the Status line no longer says
  "prerequisite infrastructure does not exist" (it now says deferred on
  zero-payoff); (b) the Step-17 framework + `Operand`/`Operand2` encoding are
  recorded; (c) the prior child's PatchKind-wrong-shape finding + future-child
  contract are intact.
- [ ] 4.2 Smoke-unchanged confirmation (optional, doc-only change): run the
  `NeoStep` filter under `Debug_Neo` + `useRegister=true` and confirm the pass
  count matches HEAD `162ce992` (no code shipped -> byte-identical behavior).
