# Tasks - neo-double-combine-quirk

One implementer pass. The fix is DUMP-GATED (the OPT-HARDEN K1 / F-MAJ-1
discipline): Block 0 stash-proves + constructs the reproducer + dumps the JIT
and LOCKS the root cause; Block 1 ships the adversarial probes; Block 2 ships
the dump-confirmed fix; Block 3 is the regression + Legacy-neutrality gate;
Block 4 ships + syncs; Block 5 is the defer fallback if all candidates are
refuted.

## Block 0 - Stash-prove + construct the reproducer + DUMP the JIT BEFORE designing the fix (MANDATORY)

> MANDATORY first step. The F-8 symptom (`long` works, `double` fails) already
> REFUTES the slot-sizing hypothesis at propose (`double` and `long` get
> byte-identical 8-byte slots). The dump confirms which COMPUTATION-path
> candidate (D2 type-spec / D3 copy-prop / D4 lowering) is the defect -- or
> refutes all three (-> Block 5 defer). DO NOT design the fix before the dump.

- [x] 0.1 Re-confirm the F-8 reproducer FAILS on current HEAD (`fe13c25e`):
  build CLI `Debug_Neo` + TestCases `Debug`; temporarily add the probe
  `NeoOptHardTest_Dbl_TwoDoubleCombine` (read `a0`, `a1` from a `double[]` via
  `Ldelem_R8`, then `if (a0 != expected0 || a1 != expected1) { 1/0 }` with
  both locals holding their expected values -> SHOULD be a no-op); run under
  the `NeoOptHardTest_` filter -> DivideByZero (CONFIRMED). Also confirm:
  (a) `NeoOptHardTest_Dbl_ThreeDoubleCombine` (3 `double` locals combined)
  FAILS; (b) `NeoOptHardTest_Dbl_ThreeLongCombine` (3 `long` locals combined)
  PASSES (the boundary control); (c) `NeoOptHardTest_Dbl_SingleDoubleEach`
  (each `double` in its own `if`, no combine) PASSES.
- [x] 0.2 STASH-PROOF it is pre-existing: the probes are test-only on HEAD
  (no engine files touched yet); the bug exists independent of this change.
  (The neo-array-completion reviewer already stash-reproduced this; do NOT
  re-litigate D-ARR reachability -- it is documented pre-existing in
  `neo-deferred-items.md` F-8.)
- [x] 0.3 JIT DUMP (D2 probe -- type-spec): enable `OUTPUT_JIT_RESULT`
  (Debug_Neo already does); add a temporary `Console.WriteLine` INSIDE the JIT
  type-spec pass for the combine method (or filter the dump by method token --
  the F-MAJ-1 review-fix earned gotcha: do NOT grep the full `OUTPUT_JIT_RESULT`
  dump, it floods). For the combine's compare/branch opcodes, record: the
  emitted `OpCodeREnum` (is it `Ceq_R8`/`Bne_Un_R8` -- CORRECT -- or
  `Ceq_I4`/`Bne_Un_I4` -- the D2 defect?). Also dump `registerTypes[]` for the
  two `double` locals' dest registers (the `Ldelem_R8` dests) at the point the
  combine is type-specialized: is the entry `DoubleType` (correct) or
  null/`IntType` (the defect)? CONFIRM D2 if the opcodes are `*_I4` OR the
  registerTypes entry is missing/IntType; REFUTE D2 if the opcodes are `*_R8`
  AND the registerTypes entry is `DoubleType`.
- [x] 0.4 IF 0.3 CONFIRMS D2 (R8 operand untyped at the combine): identify
  the missing producer dest-type seed. Leading suspect: the `Ldelem_R8` JIT
  case (`JITCompiler.cs Translate`, `:2107` Ldelem block) lacks the
  `SetRegisterType(registerTypes, op.Register1, appdomain.DoubleType)` that
  `Conv_R8` has (`:776`). Dump-confirm by inspecting the producer's case in
  the type-spec pass (`:537-847`). Lock the fix: add the missing dest-type
  seed (mirror the `Conv_R8` / `Ldloca` / `Ldflda` dest-typing rules; the
  `neo-vt-this-addr` "Newobj-dest typing" fix is the precedent for a missing
  producer case). Determine SHARED-vs-Neo-only (the type-spec pass is SHARED;
  confirm Legacy-neutral OR gate `#if ENABLE_NEO_MODE`).
- [x] 0.5 IF 0.3 REFUTES D2 (opcodes already `*_R8`): dump the optimizer IR
  BEFORE and AFTER copy-prop (D3 probe). Does a `double` local's value get
  re-materialized at a 4-byte width after the fold? Compare with the `long`
  control (does `long` flow through the same fold and stay 8-byte?). CONFIRM
  D3 if the R8 value is folded through a 4-byte intermediate (and I8 is not).
  Lock the fix in FCP/BCP/copy-prop (SHARED; gate `#if ENABLE_NEO_MODE` or
  confirm Legacy-neutral).
- [x] 0.6 IF 0.3 and 0.5 REFUTE D2 and D3: dump the post-`LowerNeoOffsets`
  opcode stream for the combine (D4 probe). Do the R8 operands' `DstOffset`/
  `SrcOffset` overlap a sibling where the I8 operands would not? (Extremely
  unlikely -- lowering is width-agnostic, advancing by the slot's declared
  `Size=8` for both `double` and `long` -- but the dump is cheap.) CONFIRM D4
  only if a concrete overlap is shown; otherwise -> Block 5 (defer).
- [x] 0.7 LOCK the fix option from the dump:
  - D2 confirmed -> fix = missing producer dest-type seed (the dump-named
    case). SHARED type-spec pass: confirm Legacy-neutral (the seed is
    byte-identical for every existing I4/I8 operand, only ADDING a correct
    R8 seed) OR gate `#if ENABLE_NEO_MODE`.
  - D3 confirmed -> fix = R8-aware copy-prop value-width dispatch. SHARED;
    gate `#if ENABLE_NEO_MODE` or confirm Legacy-neutral.
  - D4 confirmed -> fix in `LowerNeoOffsets` (Neo-only).
  - All refuted -> Block 5.

## Block 1 - Ship the F-8 reproducer + adversarial probes (test-only)

> Prefix `NeoOptHardTest_Dbl_` keeps these OUT of the `NeoStep` smoke filter
> (run under the `NeoOptHardTest_` filter, mirroring K1 / F-MAJ-1). Assertion =
> the DivideByZero-on-wrong-result pattern (the Neo VM cannot yet
> `new Exception`; established convention).

- [x] 1.1 Extend `TestCases/NeoOptHardeningTest.cs` with the F-8 probes under
  the `NeoOptHardTest_Dbl_*` prefix:
  - `NeoOptHardTest_Dbl_TwoDoubleCombine` -- the exact F-8 reproducer (FAILS
    on HEAD).
  - `NeoOptHardTest_Dbl_ThreeDoubleCombine` -- 3 `double` locals combined.
  - `NeoOptHardTest_Dbl_ThreeLongCombine` -- the boundary control (3 `long`
    locals combined; PASSES on HEAD -- proves the quirk is `double`-specific).
  - `NeoOptHardTest_Dbl_DoubleLongMix` -- `double` + `long` in one boolean
    expression (verify the long-still-works boundary).
  - `NeoOptHardTest_Dbl_DoubleIntMix` -- `double` + `int` in one boolean
    expression.
  - `NeoOptHardTest_Dbl_SingleDoubleEach` -- two `double` locals NOT combined
    (each in its own `if`; PASSES on HEAD -- verify still works after fix).
  - `NeoOptHardTest_Dbl_LiveRangeAcrossCall` -- `double` local live across an
    intervening method call, then combined.
  - `NeoOptHardTest_Dbl_SingleDoubleRegression` -- a SINGLE `double` local
    (read + check, no combine) regression guard.
- [x] 1.2 Do NOT promote any F-8 probe to `NeoStep*Test.cs` (unlike F-MAJ-1,
  the F-8 reproducer stays under `NeoOptHardTest_` only -- the array-completion
  TC11/TC12/TC14 already use the incremental `bad`-fold workaround in the
  smoke, so the smoke stays 161/161 green pre-fix). Rationale: the F-8 quirk
  is upstream of the array work and worked around in the smoke; promoting it
  would regress the smoke for an out-of-scope-at-the-time bug. (Revisit at
  ship: if the fix is clean, optionally promote the reproducer -- apply-phase
  decision.)
- [x] 1.3 Rebuild TestCases `Debug` `--no-incremental` -> 0 errors (the
  F-MAJ-1 / IL-Ex earned gotcha: small source edits sometimes do NOT re-emit
  the DLL on an incremental hash hit; verify DLL mtime > source mtime). Run
  the `NeoOptHardTest_` filter on HEAD (pre-fix) -> record which probes FAIL
  (expected: `TwoDoubleCombine`, `ThreeDoubleCombine`, `DoubleLongMix` (the
  double half), `LiveRangeAcrossCall`; expected PASS on HEAD:
  `ThreeLongCombine`, `DoubleIntMix` (if int-only), `SingleDoubleEach`,
  `SingleDoubleRegression`).

## Block 2 - Apply the dump-locked fix (root-cause fix)

> The fix site depends on Block 0's dump. The tasks below cover the leading
> candidate (D2); D3 / D4 variants are bracketed.

- [ ] 2.1 D2 (type-spec missing producer dest-type seed) -- IF Block 0.4
  LOCKED this: add the missing `SetRegisterType(registerTypes, op.Register1,
  appdomain.DoubleType)` for the dump-named producer case (likely
  `Ldelem_R8`'s JIT case in the type-spec pass, `JITCompiler.cs:537-847`).
  Mirror the `Conv_R8` (`:776`) / `Ldloca` (`:741-747`) / `Ldflda`
  (`:761-767`) dest-typing rules. Also add the symmetric `Ldelem_R4` ->
  `FloatType` seed if it is also missing (probe at apply). SHARED pass: gate
  `#if ENABLE_NEO_MODE` OR confirm byte-identical-for-existing-operands
  (the seed only ADDS a correct R8/R4 type; Legacy's `ExecuteR` has the
  mature path).
- [ ] 2.2 D3 (R8 copy-prop fold) -- IF Block 0.5 LOCKED this: make the
  copy-prop value-width dispatch R8-aware in FCP/BCP/copy-prop (SHARED; gate
  `#if ENABLE_NEO_MODE` or confirm Legacy-neutral). Thread the type-spec'd
  `registerType` into copy-prop's width derivation.
- [x] 2.3 D4 (LowerNeoOffsets overlap) -- IF Block 0.6 LOCKED this (Neo-only):
  fix the R8 operand offset advance in `Optimizer.Neo.cs LowerNeoOffsets` so
  the R8 operands do not overlap a sibling.
- [x] 2.4 Rebuild CLI `Debug_Neo` `--no-incremental` (the F-MAJ-1 earned
  gotcha: rebuild CLI `--no-incremental` after any host-type/dependency change)
  -> 0 errors. Re-run the `NeoOptHardTest_` filter -> all `Dbl_*` probes PASS
  (including the reproducer + the boundary controls).

## Block 3 - Regression + Legacy-neutrality gate (the second gate)

- [x] 3.1 Full `NeoStep` smoke: `dotnet run -c Debug_Neo -f net8.0 --project
  ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep` -> 161/161 all-green, ZERO
  regressions. `NeoOptHardTest_` filter -> all `Dbl_*` probes PASS; the
  pre-existing `Fmaj1_*` / `K1_*` probes still PASS.
- [x] 3.2 Legacy-neutrality (for any SHARED-engine edit -- D2 type-spec or
  D3 copy-prop): build a plain-`Debug` CLI + `useRegister=true`; run the
  NeoStep-filter smoke. Stash the fix and re-run -- the SAME pre-existing
  failures (the ~7 Neo-feature tests) appear with and without the fix. Record
  the failure-set is identical. (For a Neo-only fix -- D4 -- a code-read
  argument that the site is in a Neo-only file suffices; the stash-toggle is
  still run as belt-and-braces.)
- [x] 3.3 Adversarial-probe sweep: re-run the 8 `Dbl_*` probes from Block 1
  AFTER the fix. All PASS. Specifically verify: `ThreeLongCombine` PASSES
  (the I8 path is not regressed); `SingleDoubleEach` + `SingleDoubleRegression`
  PASS (the isolated `double`-read path is not broken); `DoubleLongMix` PASSes
  (R8 and I8 widths coexist in one combine).

## Block 4 - Ship-log + spec sync + planning-context append

- [x] 4.1 Write `ship-log.md`: record the dump-confirmed root cause (D2 / D3 /
  D4 / D5-defer), the chosen fix, the Legacy-neutrality proof (Neo-only OR
  stash-toggle failure-set identical), the `Dbl_*` probe results (FAIL-on-HEAD
  -> PASS-after for the reproducer + boundary controls), and the explicit note
  that `AllocateLocalStackSpaces` 8-byte-slot sizing is NOT the defect
  (`double` and `long` get byte-identical 8-byte slots) so a future 8-byte-
  allocator change is not mis-attributed.
- [x] 4.2 Sync the spec delta into `openspec/specs/neo-optimizer/spec.md`:
  the F-8 requirement goes ACTIVE (fix shipped) or stays PROVISIONAL ->
  DEFERRED (Block 5). The `AllocateLocalStackSpaces`-slot-sizing-is-NOT-the-
  defect record-keeping stays regardless.
- [x] 4.3 APPEND durable findings to
  `openspec/changes/neo-completion-portfolio/planning-context.md` under
  `## Findings -- neo-double-combine-quirk`: the dump-confirmed root cause,
  the fix (or the defer), the F-MAJ-1-vs-F-8 distinction (F-MAJ-1 = CLR struct
  representation mismatch boxed-ref-vs-flat-bytes; F-8 = R8 computation-path
  defect on an 8-byte PRIMITIVE, NOT a representation mismatch -- the frame
  slots are byte-identical to `long`), and the `double`-vs-`long` discriminator.
- [ ] 4.4 Update `.trae/documents/neo-deferred-items.md` F-8 row (§2 master
  table + §3 detail): move to Resolved (or mark DEFERRED with the dump pinned
  if Block 5).
- [ ] 4.5 Commit + push: stage the runtime fix file(s) + the extended
  `NeoOptHardeningTest.cs` + the openspec/ artifacts + the planning-context
  append + (if updated) `.trae/documents/neo-deferred-items.md`. Message:
  `Neo opt-harden-3: double-local-combine R8 type-spec/combine fix (F-8)`
  (or `Neo opt-harden-3: F-8 deferred (candidates refuted by dump)` for
  Block 5). End with the Co-Authored-By trailer. Push
  `origin features/object-model-overhaul`.

## Block 5 - Fallback: all candidates refuted (DEFER, ship NO guessed fix)

- [ ] 5.1 IF Block 0.3 REFUTES D2 AND Block 0.5 REFUTES D3 AND Block 0.6
  REFUTES D4 (opcodes already `*_R8`, copy-prop width-correct, lowering does
  not overlap, yet symptom persists): capture the full JIT dump (the combine
  method's opcode stream + `frame.LocalInfos` + `registerTypes[]`) + the
  reproducer source into the change directory.
- [ ] 5.2 Mark the F-8 requirement PROVISIONAL -> DEFERRED in the spec delta
  (the Q-STRUCT / Q-LONG / Q-NEWOBJ outcome). Do NOT ship a guessed fix to
  the shared type-spec / copy-prop / compare path.
- [ ] 5.3 Ship-log: record that the slot-sizing hypothesis was disproven at
  propose, all three computation-path candidates were refuted by the dump,
  and F-8 is DEFERRED with the dump + reproducer pinned for a future
  reproducing case. Update `.trae/documents/neo-deferred-items.md` F-8 row to
  reflect the new finding (no 8-byte-slot-allocator defect; candidates
  refuted; reproducer pinned). Append the planning-context finding.
- [ ] 5.4 Commit + push the test-only change (the reproducer under
  `NeoOptHardTest_Dbl_`, NOT promoted to `NeoStep` so the smoke stays green)
  + the spec delta + the dump artifacts. Message: `Neo opt-harden-3: F-8
  deferred (all candidates refuted by dump; reproducer pinned)`. Push.
