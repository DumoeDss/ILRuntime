# Review Report — implement-neo-opt-hardening (K1 FCP fix)

Reviewer: VERIFY stage (independent; author != verifier). Branch
`features/object-model-overhaul`. Reviewed the diff vs spec/design/planning-context,
ran builds + K1 regression + full NeoStep smoke + Legacy smoke.

## Executive verdict: CLEAN

K1 ldloca-kill is correct, sound, correctly scoped, and Legacy-neutral.
Empirically verified end-to-end. Zero Blockers, zero Majors, zero Minors.
A few observations (all INFO / non-blocking) recorded below.

Severity counts: Blocker 0 | Major 0 | Minor 0 | Info 5

## What shipped (scope confirmed)

Only ONE shipped source file changed: `Optimizer.FCP.cs` (+39, in two gated
blocks). Confirmed untouched (adversarial scope check):

- `ILIntepreter.Register.cs` (Legacy `ExecuteR`) — NOT touched.
- Heap `Stfld_*` / `Ldfld_*` families — NOT touched.
- `Optimizer.BCP.cs` (Q-STRUCT path) — NOT touched.
- Conv/compare lowering / JIT (Q-LONG path) — NOT touched.

Plus untracked `TestCases/NeoOptHardeningTest.cs` (3 K1 regression tests) and
the openspec change docs. `openspec validate implement-neo-opt-hardening` →
"Change is valid".

## (a) ldloca-kill correctness — VERIFIED SOUND

Both FCP loops get the kill, in the right place with the right abort variable:

- **In-block loop** (`Optimizer.FCP.cs:168-189`): after the existing
  whole-register kill (`xDst == yDst`, :159-167). On match sets
  `postPropagation = false; ended = true; break;` — the exact trio the
  surrounding code uses to abort an in-block propagation.
- **Cross-block pending-FCP loop** (`Optimizer.FCP.cs:323-339`): after the
  existing dest-kill (`yDst == xDst`, :317-321). On match sets
  `cannotRemove = true; break;` — the correct cross-block abort variable
  (matches its siblings at :261, :271, :279, :289, :297, :307).

Condition is exactly as specified: `Y.Code == Ldloca || Y.Code == Ldloca_S`
AND `ySrc >= 0 && (ySrc == xSrc || ySrc == xDst)`. The ldloca-opcode guard
comes FIRST, so the `ySrc` compare only runs for ldloca — no spurious kill on
other opcodes that happen to share a source register.

**`ySrc` is genuinely the addressed base local.** `GetOpcodeSourceRegister`
maps `Ldloca`/`Ldloca_S` to `r1 = op.Register2` (Optimizer.Utils.cs:463-464,
:500), and FCP reads that into `ySrc`. `op.Register2` is the base local being
addressed (NOT the address-temp dest, which lives in `Register1`/dest). So
`ySrc == xSrc` correctly detects "ldloca of the propagation source".

**`ySrc` is definitely assigned and not stale.** `GetOpcodeSourceRegister`
initializes all three outs to `-1` at its top (Utils.cs:402-404) before the
switch, so even for opcodes outside the switch `ySrc` is -1 (the `ySrc >= 0`
guard then skips). In both loops `ySrc` is declared at the `for`-body scope
(in-block :72; cross-block :253 — note `yDst` on the same line is used at :315
*outside* the source-`if`, proving line 253 is loop-body scope, not nested).
The kill reads the per-iteration `ySrc` for the current opcode Y — no
cross-iteration staleness. (Build is 0 errors, confirming definite-assignment.)

**Soundness:** taking the address of a local means it can be mutated through
that address by any later `stfld`/`stind` through the handle, so any
already-propagated field read involving that local (`xSrc` or `xDst`) is
potentially stale. Killing is the conservative-correct choice. The kill is
scoped to ACTIVE propagations only (it runs inside the per-propagation scan,
comparing against that propagation's `xSrc`/`xDst`), so an unrelated ldloca
of some other register never trips it. Matches the design's K1 reasoning.

## (b) Legacy-neutrality — VERIFIED (top risk cleared)

The kill is entirely inside `#if ENABLE_NEO_MODE … #endif` in BOTH loops.
Confirmed by csproj: plain `Debug` (ILRuntime.csproj:14-18) does NOT define
`ENABLE_NEO_MODE`; only `Debug_Neo` (:26-30) and `Release_Neo` (:32-36) do.
No code leaks outside the gate.

Empirical three-way verification:

1. **Plain-`Debug` CLI builds clean (0 errors)** — the gate compiles the kill
   out entirely; Legacy FCP control flow is byte-identical to pre-change.
2. **Legacy NeoStep smoke = 7 failures**, IDENTICAL to the documented
   pre-existing baseline. The 7 are, by name and failure mode, all Neo-feature
   tests that don't run correctly on Legacy `ExecuteR` — NONE is a copy-prop
   issue:
   - `NeoTestClrStructNoBindingBoxRoundTrip`, `NeoTestClrStructWithBinderBoxRoundTrip`
     (Step 13 CLR-struct boxing — DivideByZero)
   - `NeoStep14_TC1_BasicTryCatch`, `NeoStep14_TC5_NestedInnermostWins`,
     `NeoStep14_TC8_NullRefCatch`, `NeoStep15_TC6_CastclassFailureCaught`
     (Steps 14/15 try/catch internals — ArgumentOutOfRangeException)
   - `NeoNaNR8` (Step 6 NaN compare)
3. Since the kill is compiled out in this build, the 7 cannot be caused by the
   fix — they are pre-existing. Confirmed.

**Note on the Neo-only gate for non-Neo-specific opcodes (INFO, intentional):**
`Ldloca`/`Ldloca_S` are emitted by Legacy too, so this kill COULD apply Legacy-
wide. The design explicitly scopes it Neo-only as a conservative choice (the
K1 silent-correctness path is only KNOWN to be reachable under Neo's inline-
field-store lowering; widening to Legacy would be a broader behavior change to
a shared pass and is out of scope). A Legacy K1-equivalent, if one exists, is
intentionally left for a separate change. This is a sound scope decision, not a
defect — flagging only because the brief asked.

## (c) Over-kill / correctness-safety — VERIFIED SAFE

Reasoning confirmed: killing a propagation only means "do not rewrite this
read's source register"; the read then keeps its original register and executes
against the value it would have used without FCP. That is always correct — at
worst it leaves a valid optimization on the table (the "address taken but only
read" case = missed optimization = perf only), NEVER a wrong result. The kill
cannot produce a wrong value; it can only fail to produce a faster one.

Empirically: full NeoStep smoke = **0 failures** (72 existing cases all green,
no case flipped). The 3 new K1 tests pass. No correctness regression observed.

## (d) K1 regression test — VERIFIED (FAIL-on-HEAD → PASS-after)

`NeoOptHardeningTest.cs` ships 3 tests:
`NeoOptHardTest_K1_FcpVtPropagation`, `NeoOptHardTest_K1_MutateDestAfterCopy`,
`NeoOptHardTest_K1_SourceAndDestAfterMutation`.

- **After fix (Debug_Neo):** 3/3 PASS ("0 tests failed").
- **HEAD (fix stashed, rebuilt):** 2 of 3 FAIL with
  `System.DivideByZeroException` (the `b.n` read returns the mutated 999);
  the third (`MutateDestAfterCopy`) passes trivially on HEAD too. Exactly
  matches the design/tasks claim. Fix is causative.
- The pattern is the real-world C# lowering (`OptHardK1Struct a; a.n=11;
  b = a; a.n=999; read b.n`), not a contrived path — the IR dump in
  planning-context §9.1 confirms it lowers through `ldloca.s; stfld.i4.inline`,
  which is exactly what the kill targets.

## (e) Design / spec / proposal accuracy — VERIFIED

`design.md` §K1 carries the CORRECTED root cause (ldloca address-handle
indirection; `Stfld_*_Inline.Register1` is the address temp, never the base)
with an explicit "NOTE: the original root cause was MIS-DIAGNOSED" flag and a
pointer to the §9 postmortem. `planning-context.md` §9 preserves the full
failed-attempt + re-attempt-success record (§9.6). `proposal.md` and `spec.md`
K1 requirement both reflect the ldloca kill (fires on ldloca source == xSrc/xDst,
not on stfld Register1). The archived design does NOT ship the wrong root cause.
Q-STRUCT / Q-LONG are tracked as deferred-until-reproduced spec requirements
with non-shipped probe patterns — honest and correctly scoped.

## Findings (all INFO / non-blocking)

- **INFO-1 — `MutateDestAfterCopy` is a weak regression guard.** It passes on
  HEAD too (the dest-mutation path is already correct without the fix), so it
  does not by itself pin the bug. The two FAIL-on-HEAD tests
  (`FcpVtPropagation`, `SourceAndDestAfterMutation`) are the real guards.
  Acceptable — the trio together covers the xSrc side, the xDst side, and the
  source-not-corrupted side. No action needed.
- **INFO-2 — Conservative kill may disable a rare valid optimization**
  (address taken but only read). This is a documented, accepted trade-off
  (perf, not correctness). The 72/72 smoke shows no real-world case regressed.
- **INFO-3 — Neo-only gate on non-Neo-specific opcodes.** Discussed in (b);
  intentional, documented in design.md. Out of scope for this change.
- **INFO-4 — `.gitignore` + LFS `.pdb` noise in the diff.** Unrelated binary/
  ignore churn per CLAUDE.md ("repo contains committed binary deps; git status
  noise is normal"). Not part of this change's intent; the LEAD's commit should
  stage only `Optimizer.FCP.cs` + the test + openspec docs, not the `.pdb`
  blobs.
- **INFO-5 — `tasks.md` Block 2/3 checkboxes unchecked.** Correct: Q-STRUCT/
  Q-LONG were confirmation-only (deferred); their probes were NOT shipped (per
  task 1.5 note), so the "leave probes as documentation cases" sub-tasks are
  N/A. The unchecked state honestly reflects "deferred, no fix". No action.

## Verification commands run (evidence)

- `dotnet build ILRuntimeTestCLI -c Debug_Neo` → 0 errors.
- `dotnet build TestCases -c Debug` → 0 errors.
- `dotnet build ILRuntimeTestCLI -c Debug` (Legacy) → 0 errors (gate compiles out).
- K1 after fix: `… true NeoOptHardTest` → 0 tests failed (3/3 PASS).
- K1 on HEAD (stash): → 2 tests failed (DivideByZero) — bug reproduced.
- NeoStep smoke after fix: `… true NeoStep` → 0 failed (72/72 existing green).
- Legacy NeoStep smoke: `… -c Debug …` → 7 failed (pre-existing, unrelated).
- `openspec validate implement-neo-opt-hardening` → valid.

## Verdict per priority

| Priority | Verdict |
|---|---|
| (a) ldloca-kill correctness | SOUND — both loops, right vars, right scope, `ySrc`=addressed base |
| (b) Legacy-neutrality | CLEAN — gate compiles out in `Debug`; 7 failures genuinely pre-existing |
| (c) over-kill correctness-safety | SAFE — kill only suppresses a rewrite; never yields a wrong value |
| (d) K1 regression test | VERIFIED — FAILs on HEAD (2/3 DivideByZero), PASSes after fix |
| (e) design/spec accuracy | ACCURATE — corrected root cause shipped, mis-diagnosis flagged |
| Scope | TIGHT — only `Optimizer.FCP.cs` + test + docs |

**Recommendation: SHIP.** The fix is minimal, correct, Legacy-neutral, and
fully verified. No blocking findings.
