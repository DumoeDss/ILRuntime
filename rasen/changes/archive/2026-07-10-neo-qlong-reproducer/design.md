# Design: neo-qlong-reproducer (portfolio child 19)

## What this is
A REPRODUCER child (Q-* discipline). Q-LONG is the long-suspected JIT quirk:
"long default-zero compare / conv.i8 quirk", suspect site the JIT branch
type-specialization + `AllocateLocalStackSpaces` 4-vs-8-byte overlap (a `long`
local is 8 bytes; a mis-sized slot could overlap its neighbour, and a
`conv.i8` / compare could read stale upper bytes). It has been
NON-reproducible on HEAD for multiple sessions (3 prior OPT-HARDEN probes
pass). The only legitimate close is a guard that PROVES the shape stays
correct.

## The adversarial shape
The suspected failure modes:
1. A default-zero `long` that reads as non-zero (upper 4 bytes stale from a
   prior 4-byte write to the same slot region).
2. A `conv.i8` of an int to long whose dest is only 4 bytes, so the upper half
   is garbage -> a `== 0L` / `!= someLong` compare mis-fires.
3. A branch type-spec mismatch where the compare/branch arm reads the wrong
   width.

The reproducer forces all three:
- `long L = default;` then `L == 0L` AND `L != 0L` (both directions).
- `conv.i8` of a small int, then `== 0L` (must be false) and `== 5L`.
- `conv.i8` of a NEGATIVE int (`-1`) -- sign-extension correctness -- compared
  against a zero long (must differ), plus `conv.i8` of `0x7FFFFFFF`.
- An array of longs + many live longs (highest 4-vs-8 overlap pressure).

Three guards:
1. `NeoStepQlong_DefaultZeroCompare` -- default-zero long both compare
   directions + conv.i8 of a small int.
2. `NeoStepQlong_ConvI8SignExtend` -- conv.i8 of `-1` and `0x7FFFFFFF`
   (sign/zero extension correctness, the strongest 4-byte-slot detector).
3. `NeoStepQlong_ArrayScalarPressure` -- long array + 3 zero longs + a
   conv.i8'd scalar, all live at once.

## Outcome on HEAD
All three guards PASS on HEAD (no fault). The conv.i8 readers and I8
compare/branch arms read `*(long*)` correctly; `InferPrimTag`->`_I8` widening
is correct; the long-slot sizing has no 4-vs-8 overlap for this shape.
Confirmed-closed, doc-only -- the guards are the legitimate close and stay as
permanent regression guards. No engine change shipped.

## Legacy-neutral
TEST-ONLY (no engine change). Runs under both engines; only asserted under the
Neo smoke.
