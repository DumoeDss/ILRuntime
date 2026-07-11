# Design: neo-qstruct-reproducer (portfolio child 18)

## What this is
A REPRODUCER child (Q-* discipline). Q-STRUCT is the long-suspected optimizer
quirk: "struct-local + field-mutation + element-read temp-renumber", suspect
site `Optimizer.BCP.cs:97-141` (the `BackwardsCopyPropagation` rewrite loop,
specifically the `ReplaceOpcodeDest(ref Y, xDst)` + forward scan that rewrites
all uses of a renamed source register to the copy destination). It has been
NON-reproducible on HEAD for multiple sessions (6 prior OPT-HARDEN probes
pass). The only legitimate close of a non-reproducible suspected bug is a
guard that PROVES the shape stays correct.

## The adversarial shape
The BCP rewrite is dangerous when a copy's source register (`xSrc`) coincides
with a prior instruction `Y`'s destination (`yDst`): BCP rewrites `Y`'s dest to
the copy's dest (`xDst`) and rewrites all *forward* uses of `yDst` to `xDst`,
removing the copy. A bug here corrupts a value silently IF the renumber
collides with a still-live temp.

The reproducer forces the densest such interleaving C# can emit:
- A struct local `S { int a,b,c; }`, mutated via `stfld` (address+value pairs ->
  copy temps).
- An array element READ interleaved *between* field mutations, whose dest temp
  is the value BCP would renumber.
- Then reads of BOTH the mutated struct fields AND the array element, asserted.

If the renumber corrupts a live temp, one of the three assertions sees the
wrong value -> native DivideByZero fault.

Three guards:
1. `NeoStepQstruct_MutateReadRenum` -- the core shape (mutate, read element,
   mutate, read element, assert all).
2. `NeoStepQstruct_HighRegPressure` -- 3 struct locals + 6 array elements +
   6 scalar locals live simultaneously, maximizing temp-reuse density.
3. `NeoStepQstruct_MutateWriteRenum` -- stelem (element WRITE) interleaved with
   field mutation; the write direction may collide symmetrically.

## Outcome on HEAD
All three guards PASS on HEAD (no fault). The BCP renumber is correct for this
shape. Confirmed-closed, doc-only -- the guards are the legitimate close and
stay as permanent regression guards. No engine change shipped (a fix to the
shared BCP pass without a reproducing case would be worse than none).

## Legacy-neutral
TEST-ONLY (no engine change). Runs under both engines; only asserted under the
Neo smoke.
