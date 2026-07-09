# Ship Log — neo-qstruct-reproducer (child 18)

**Date:** 2026-07-10  **Capability:** neo-optimizer  **Wave:** completion-3, child 18
**Status:** SHIPPED — doc-only CONFIRMED-CLOSED + 3 permanent regression guards. LEAD-verified (TEST-ONLY).

## Outcome: NON-REPRO on HEAD → confirmed-closed
The Q-STRUCT shape (struct-local + field-mutation + element-read temp-renumber; suspected
`Optimizer.BCP.cs:97-141`) does **NOT reproduce** on HEAD. 3 adversarial guards (each stronger than
the prior 6 OPT-HARDEN probes):
- `NeoStepQstruct_MutateReadRenum` — mutate/read-element interleaving (the exact BCP
  `ReplaceOpcodeDest` collision shape).
- `NeoStepQstruct_HighRegPressure` — 3 struct locals + 6 array elements + 6 scalars all live
  (densest temp-reuse pressure).
- `NeoStepQstruct_MutateWriteRenum` — `stelem` (element-write) interleaved with mutation (symmetric
  write-direction collision).

All PASS on HEAD. **Q-*/F-* discipline:** a fix to the shared BCP pass without a reproducing case
would be worse than none → TEST-ONLY guards, no engine change.

## Verification
NeoStep 253 → **259/0/0** (+6 guards incl. child 19's). All 6 guards run 0-20ms (no infinite loop);
each PASSES on HEAD (the non-repro evidence). TEST-ONLY → Legacy-neutral trivially.

## Durable finding
**Q-STRUCT non-repro on HEAD** — the BCP temp-renumber (`Optimizer.BCP.cs:97-141`) is correct for the
struct-mutation/element-read interleaving shape. Suspect site pinned in `neo-deferred-items.md` for
any future reproducing case.
