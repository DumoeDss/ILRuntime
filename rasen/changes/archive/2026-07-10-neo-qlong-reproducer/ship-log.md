# Ship Log — neo-qlong-reproducer (child 19)

**Date:** 2026-07-10  **Capability:** neo-optimizer  **Wave:** completion-3, child 19
**Status:** SHIPPED — doc-only CONFIRMED-CLOSED + 3 permanent regression guards. LEAD-verified (TEST-ONLY).

## Outcome: NON-REPRO on HEAD → confirmed-closed
The Q-LONG shape (long default-zero compare / `conv.i8` quirk; suspected conv.i8 / JIT branch
type-spec / long-slot sizing) does **NOT reproduce** on HEAD. 3 adversarial guards:
- `NeoStepQlong_DefaultZeroCompare` — `long L = default` + `L==0L`/`L!=0L` both directions + conv.i8
  of 5.
- `NeoStepQlong_ConvI8SignExtend` — conv.i8 of `-1` (sign-extend) and `0x7FFFFFFF` (zero-extend) vs a
  zero long — the strongest 4-vs-8-byte-slot detector.
- `NeoStepQlong_ArrayScalarPressure` — long array + 3 zero longs + a conv.i8'd scalar all live.

All PASS on HEAD → conv.i8 readers / I8 compare-branch arms / `InferPrimTag`→`_I8` widening /
long-slot sizing are all correct for this shape. **Q-*/F-* discipline:** TEST-ONLY guards, no engine
change.

## Verification
NeoStep 253 → **259/0/0** (+6 guards incl. child 18's). All 6 guards PASS on HEAD (the non-repro
evidence). TEST-ONLY → Legacy-neutral trivially.

## Durable finding
**Q-LONG non-repro on HEAD** — conv.i8 + I8 compare/branch + long-slot sizing have no 4-vs-8-byte
overlap for this shape. Suspect sites pinned in `neo-deferred-items.md` (JIT branch type-spec /
`AllocateLocalStackSpaces`) for any future reproducing case.
