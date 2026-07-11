## Why

The neo-array-completion review (Finding F-1) surfaced **F-8 / NEO-DOUBLE-COMBINE**:
a method that reads **2+ `double` locals** and combines them in a single boolean
expression (e.g. `if (a0 != expected0 || a1 != expected1)`) computes a
**silently wrong result** (the `||` trips the wrong branch / a `double` reads
as 0 -> DivideByZero on the assertion). The reviewer refined the
characterization beyond the implementer's note: the quirk is NOT "3+ locals" and
NOT all 8-byte primitives -- **3 `long` locals combined work fine; only
`double` triggers it**. A single `double` read is correct; combine two in one
`if` and the comparison misfires. This is the **F-MAJ-1 class** (silent wrong
result) and it is **double-specific**, which narrows the suspect surface from
"any 8-byte primitive" to "the R8 type-specialization / R8 compare / R8
copy-prop path."

This is the **`[OPT-HARDEN-3]`** follow-up. It delivers a fix whose root cause
is **PROVEN by a JIT dump on current HEAD** (`fe13c25e`) -- not assumed --
mirroring the discipline established by OPT-HARDEN (K1), OPT-HARDEN-2 (F-MAJ-1),
and the Q-STRUCT / Q-LONG / Q-NEWOBJ closures. A fix is shipped ONLY if a
reproducing case on current HEAD plus a JIT/optimizer dump pinpoints the defect;
a guessed fix to the shared type-specialization / copy-prop / compare path is
NOT shipped.

## What Changes

- **F-8 (FIXED -- root cause to be PROVEN by JIT dump at apply): combining 2+
  `double` locals in one boolean expression no longer silently misfires.** The
  leading candidates, ranked by code-grounded likelihood (each confirmed or
  refuted by a dump at apply):

  1. **(LEADING) R8 compare type-specialization mis-types the combine.** A C#
     `d1 != x || d2 != y` lowers through `Ceq`/`Cgt` (typed to `Ceq_R8`/
     `Cgt_R8` via `GetTypedCompareOpcode`, `JITCompiler.cs:1182-1218`) producing
     `int` results, then an integer `Or`/`Bne_Un` combines them; OR it lowers
     to direct `Bne_Un` branches typed by the operand. `GetTypedCompareOpcode`
     and `GetTypedBranchOpcode` both HAVE an `R8` case, but the `||`/`&&`
     short-circuit combine may flow through a path where the R8 operand's
     `registerType` is NOT yet seeded (e.g. the operand comes from a `Ldelem_R8`
     whose dest is typed only later, or from a `Conv_R8` whose dest type is
     seeded but the immediately-following compare reads the SOURCE before the
     seed propagates) -> `InferPrimTag` falls back to `I4` -> the compare emits
     the WRONG width (`Ceq_I4` reading 4 bytes of an 8-byte `double` slot) ->
     silent wrong result. This fits the `double`-specific signature exactly: the
     I8 (long) path is exercised by the working 3-`long` case, so the I8
     type-spec is correct; the divergence is in the R8 type-spec / seed
     ordering. The `long`-works / `double`-fails boundary is the discriminator.

  2. **R8 copy-prop fold.** FCP/BCP/copy-prop (SHARED passes) may fold a `double`
     local's value through a 4-byte-wide intermediate (the copy-prop value
     width assumes I4 for an untyped operand), so a `double` local combined in
     an expression is re-read at the wrong width. The I8 path is again the
     control: if I8 also flows through the same fold and works, copy-prop is
     exonerated; if the fold width-dispatches and the R8 arm is wrong, this is
     the defect.

  3. **(REFUTED AT PROPOSE -- recorded so it is not mis-attributed)
     `AllocateLocalStackSpaces` 8-byte-primitive slot sizing / alignment.**
     `double` and `long` BOTH size to 8 (`GetPrimitiveSize:1918-1921` for
     `double`, `:1898-1901` for `long`) and BOTH align to 8 (`AlignUp(offset,
     size)` at `:1568`). They get byte-identical frame layout. So an 8-byte-slot
     / alignment defect CANNOT explain `long`-works / `double`-fails -- the
     frame slots are indistinguishable. The defect is in the COMPUTATION path
     (type-spec / compare / copy-prop), NOT the slot sizing. A "fix the 8-byte
     slot allocator" change is NOT this fix (mirrors the F-MAJ-1 lesson:
     `AllocateLocalStackSpaces` monotonic allocation was NOT the F-MAJ-1 fix
     site).

  4. **`LowerNeoOffsets` R8 operand overlap.** A long shot: post-lowering, an
     R8 operand's `DstOffset`/`SrcOffset` byte-offset pair could overlap a
     sibling for the combine shape but not the isolated shape. Refuted-at-propose
     IN PRINCIPLE (lowering is width-agnostic -- it advances by the slot's
     declared `Size`, which is 8 for both `double` and `long`), but the dump
     confirms.

  The fix is PROVISIONAL pending the dump. For candidate (1) the fix is to
  ensure the R8 operand's `registerType` is seeded BEFORE the compare/branch
  type-specialization reads it (mirror the `Ldelem_R8` / `Conv_R8` dest-typing
  rules -- the missing case is whatever producer feeds the combine). For
  candidate (2) the fix is in the copy-prop value-width dispatch (SHARED -- gate
  `#if ENABLE_NEO_MODE` or confirm Legacy-neutral). The fix site is LOCKED at
  apply from the dump, not here.

- **If the dump REFUTES candidates (1), (2), and (4)** (the type-spec seeds are
  correct, copy-prop is width-correct, lowering does not overlap, yet the
  symptom persists), the change falls back to tracking F-8 as a DEFERRED
  requirement (mirroring Q-STRUCT / Q-LONG / Q-NEWOBJ) with the dump artifacts
  and the reproducer pinned. **No guessed fix ships.**

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `neo-optimizer`: ADDS the F-8 invariant -- a method combining 2+ `double`
  locals in one boolean expression MUST compute the correct result (the R8
  compare type-specialization / R8 copy-prop fold MUST agree on the `double`
  operand's width, so a combined `d1 != x || d2 != y` check does not silently
  misfire). Records the `double`-vs-`long` discriminator finding (frame slot
  sizing/alignment is NOT the defect -- `double` and `long` get byte-identical
  8-byte slots; the defect is in the computation path) so a future "fix the
  8-byte slot allocator" change is NOT mis-attributed as the F-8 fix. The
  requirement is ACTIVE once a reproducing case + dump land a fix; DEFERRED
  (tracked, no fix) if the dump refutes the candidates.

## Impact

- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- the R8
  type-specialization cases (`GetTypedCompareOpcode:1182-1218`,
  `GetTypedBranchOpcode:1221+`, `GetTypedImmediateCompareOpcode:1321+`,
  `InferPrimTag:1052-1086`) and the producer dest-typing loop (`:537-847`) are
  the LIKELY fix site for candidate (1) (ensure the R8 operand is typed before
  the combine reads it). SHARED engine: a change here affects Legacy too, so it
  MUST be Legacy-neutral (gate `#if ENABLE_NEO_MODE` OR confirm plain-`Debug`
  smoke is unchanged).
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.FCP.cs` /
  `Optimizer.BCP.cs` / copy-prop -- the fix site ONLY IF candidate (2) is
  dump-confirmed (SHARED; gate `#if ENABLE_NEO_MODE`).
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` --
  `LowerNeoOffsets` is the fix site ONLY IF candidate (4) is dump-confirmed
  (Neo-only).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- the `Beq_R8`/
  `Bne_Un_R8`/`Ceq_R8`/`Cgt_R8` runtime arms (`:1068-1106`, `:1293-1320`) read
  `*(double*)` from `DstOffset`/`SrcOffset`. These are byte-identical-in-shape
  to the I8 arms; NOT expected to be the fix site, but the dump confirms the
  post-lowering offsets the R8 arms actually receive.
- `TestCases/NeoOptHardeningTest.cs` (extend) -- the F-8 regression tests under
  the `NeoOptHardTest_Dbl_*` prefix (run under the `NeoOptHardTest_` filter,
  mirroring K1 / F-MAJ-1). MANDATORY adversarial probes (the silent-corruption
  class -- green smoke MISSES it): (1) the exact F-8 reproducer (2 `double`
  locals combined); (2) 3 `double` locals combined; (3) `double` + `long` mix
  (verify the long-still-works boundary); (4) `double` + `int` mix; (5)
  `double` locals NOT combined (each isolated -- verify still works); (6)
  `double` locals across a method call (live range); (7) regression -- single
  `double` local + the F-MAJ-1 probes still green + full NeoStep smoke 161/161.
- No change to Legacy `ExecuteR`, `AllocateLocalStackSpaces` (the 8-byte-slot
  sizing is NOT the defect -- recorded), or `addrAlias` (not the fix site).
- Regression gate: full `NeoStep` smoke (161/161 baseline at HEAD after
  neo-array-completion) stays all-green; Legacy `Debug` smoke unchanged for any
  shared-engine edit.
