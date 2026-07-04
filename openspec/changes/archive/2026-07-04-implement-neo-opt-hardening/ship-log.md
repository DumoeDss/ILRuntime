# Ship Log - implement-neo-opt-hardening

Change: `implement-neo-opt-hardening`
One-line: K1 FCP correctness fix - Forward Copy Propagation now kills a
propagation when a `Ldloca`/`Ldloca_S` takes the address of the propagation
source (`xSrc`) or dest (`xDst`); a whole value-type copy's field reads must
not survive an intervening field mutation of the source. Neo-only,
Legacy-neutral.

## Verdict: CLEAN

Review verdict: 0 Blocker / 0 Major / 0 Minor / 5 Info. Recommendation: SHIP.
See `review-report.md`.

## Verification evidence

- Builds (0 errors):
  - `dotnet build ILRuntimeTestCLI -c Debug_Neo` -> 0 errors.
  - `dotnet build TestCases -c Debug` -> 0 errors.
  - `dotnet build ILRuntimeTestCLI -c Debug` (Legacy) -> 0 errors (the kill is
    `#if ENABLE_NEO_MODE`, compiled out in plain `Debug`).
- NeoStep smoke (Debug_Neo): 72/72 existing cases green, ZERO regressions.
- K1 regression (Debug_Neo): 3/3 PASS
  (`NeoOptHardTest_K1_FcpVtPropagation`,
  `NeoOptHardTest_K1_MutateDestAfterCopy`,
  `NeoOptHardTest_K1_SourceAndDestAfterMutation`).
- Causation confirmed: with the fix stashed and rebuilt on HEAD, 2 of the 3
  K1 tests FAIL with `DivideByZeroException` (the `b.n` read returns the
  mutated 999). Re-applied -> 3/3 PASS.
- Combined gate: 72 NeoStep + 3 K1 = 75/75 green, 0 regression.
- Legacy-neutral (stash-verified): plain-`Debug` Legacy NeoStep smoke = 7
  failures, IDENTICAL with and without the fix. The 7 are pre-existing
  Neo-feature tests (Step 13 CLR-struct boxing, Steps 14/15 try/catch
  internals, Step 6 NaN compare) that do not run correctly on Legacy
  `ExecuteR`; none is a copy-prop issue. The kill compiles out in this build,
  so the 7 cannot be caused by it.
- `openspec validate implement-neo-opt-hardening` -> valid.

## Review summary (review-report.md)

- (a) ldloca-kill correctness: SOUND. Both FCP loops get the kill in the right
  place with the right abort variable (`postPropagation/ended/break` in-block;
  `cannotRemove/break` cross-block). Condition is exactly
  `Y.Code in {Ldloca, Ldloca_S}` AND `ySrc >= 0 && (ySrc == xSrc || ySrc == xDst)`,
  with the ldloca-opcode guard first so no spurious kill. `ySrc` =
  `op.Register2` is the addressed base local, definitely assigned and not
  stale; the kill is scoped to ACTIVE propagations only.
- (b) Legacy-neutrality: CLEAN. The kill is entirely inside
  `#if ENABLE_NEO_MODE ... #endif` in BOTH loops; confirmed via csproj
  (`Debug` does not define `ENABLE_NEO_MODE`).
- (c) Over-kill correctness-safety: SAFE. Killing a propagation only suppresses
  a register rewrite; the read keeps its original register and is always
  correct. At worst a missed optimization (perf), never a wrong value.
- (d) K1 regression test: VERIFIED. FAIL-on-HEAD (2/3 DivideByZero) ->
  PASS-after (3/3). Causative.
- (e) Design/spec/proposal accuracy: ACCURATE. Corrected root cause shipped;
  original mis-diagnosis explicitly flagged.
- Scope: TIGHT - only `Optimizer.FCP.cs` + test + openspec docs. Adversarial
  scope check confirms `ILIntepreter.Register.cs`, the heap
  `Stfld_*`/`Ldfld_*` families, `Optimizer.BCP.cs`, and the conv/compare
  lowering are all UNTOUCHED.

## Delivered scope

- K1 FIXED - FCP `ldloca-kill` (corrected re-attempt):
  - In-block propagation loop and cross-block pending-FCP loop both gain a
    Neo-only kill. When `Y.Code` is `Ldloca`/`Ldloca_S` and its source
    (`ySrc = op.Register2`, the addressed base local) equals the active
    propagation's `xSrc` or `xDst`, the propagation is killed.
  - Rationale: taking the address of a local means it can be mutated through
    that handle by a later `stfld`/`stind`, so any already-propagated field
    read involving that local is potentially stale. Conservative-correct.
  - Gated `#if ENABLE_NEO_MODE`; Legacy `ExecuteR` compiles it out.

### Corrected-root-cause note (important)

The planner's ORIGINAL K1 design (kill keyed on
`Stfld_*_Inline.Register1 == xSrc/xDst`) was a NO-OP and was reverted: the
field store reaches the base local INDIRECTLY through a `ldloca.s` address
handle, so `Stfld_*_Inline.Register1` is the address temp, NEVER the base
local - that key can never match `xSrc`/`xDst`. The corrected fix keys the
kill on the `ldloca` itself (the base local is the Ldloca source register
`op.Register2`, which FCP already enumerates as `ySrc`). The mis-diagnosis is
explicitly flagged in `design.md` and preserved as a postmortem in
`planning-context.md` sec.9; `proposal.md`/`spec.md` carry the corrected root
cause.

## Deferred scope (not shipped this change)

- Q-STRUCT (DEFERRED - not reproducible on current HEAD): the documented
  "struct-local + field-mutation + element/branch-read optimizer
  temp-renumber" quirk could not be reproduced (6 probes pass). Tracked as a
  spec requirement with a "confirm reproducibility first" gate. Suspect
  location pinned (BCP renumber). No fix shipped.
- Q-LONG (DEFERRED - not reproducible on current HEAD): the documented
  "long default-zero compare (conv.i8)" quirk could not be reproduced; the
  conv.i8 + Cgt_Un/Ceq_I8/Bne_Un_I8 arms all evaluate correctly. Tracked as a
  spec requirement. Suspect location pinned (conv.i8 / long-compare). No fix
  shipped.
- Both remain tracked, open requirements in the synced spec; a fix SHALL land
  only when a reproducing test case on current HEAD accompanies it.

## Files changed

- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.FCP.cs` - the K1 fix
  (two Neo-only `#if ENABLE_NEO_MODE` kill blocks: in-block loop ~:168,
  cross-block loop ~:323). +39 lines.
- `TestCases/NeoOptHardeningTest.cs` (new, untracked) - 3 K1 regression tests.
- openspec change docs under
  `openspec/changes/implement-neo-opt-hardening/` (this ship-log, proposal,
  design, planning-context, review-report, tasks, spec delta).

## Git note (for LEAD commit)

- All change files are UNCOMMITTED in the working tree. Per the brief, the
  LEAD commits and pushes; the SHIPPER did not edit source and did not run
  `openspec-gstack-ship` (Rails/JS-centric, does not apply; no PR).
- The LEAD's commit MUST stage ONLY these files:
  `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.FCP.cs`,
  `TestCases/NeoOptHardeningTest.cs`, and the
  `openspec/changes/implement-neo-opt-hardening/` tree (+ the synced
  `openspec/specs/neo-optimizer/spec.md` created at archive time, and the
  archived `openspec/changes/archive/2026-07-04-implement-neo-opt-hardening/`
  move).
- Do NOT stage the unrelated `.pdb` / `.gitignore` churn in `git status`
  (committed binary-dep noise per CLAUDE.md; INFO-4 in review-report).
