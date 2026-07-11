# Ship Log — neo-opportunistic-cleanup

**Date:** 2026-07-06
**Change type:** TRIVIAL — comment-only + test tighten (no behavior change).
**Shipper:** shipper role (post-review; LEAD non-author diff-read APPROVED).
**Working tree:** UNCOMMITTED (LEAD commits after the shipper finishes).

---

## 1. Outcome

Two micro-fixes, neither altering runtime behavior:

- **N-CGTUN (comment tighten).** The `Cgt_Un` arm divergence comment in
  `Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (~lines 1029-1032) now
  names BOTH symmetric sentinel-collision divergences from raw unsigned
  semantics: (a) the operand case `cgt.un x, (uint)0xFFFFFFFF` (the
  `cguB == -1` clause short-circuits the compare to `true`) AND (b) the
  symmetric source case `cgt.un (uint)0xFFFFFFFF, x` (the leading
  `cguA != -1` clause forces the result to `false`). Both are the same
  sentinel-collision class (`-1 == 0xFFFFFFFF`, the null sentinel colliding
  with the integer bit pattern); neither is exercised by the validated
  TestCases suite. **Comment-only — the runtime expression on ~line 1035
  (`cguRes = cguA != -1 && ((uint)cguA > (uint)cguB || cguB == -1);`) is
  byte-identical.**
- **N-TC2 (test tighten).** `NeoStep14_TC2_CatchObjectAccess` in
  `TestCases/NeoStep14Test.cs` had a weak `e != null` assertion because the
  type-check-in-catch (`isinst`) was Step-15 territory when TC2 was authored.
  Step 15 has shipped `isinst`, so the assertion was tightened to
  `e is DivideByZeroException && e.Message != null`. The `is` lowers to
  `isinst`, exercising the type-check-in-catch shape (now that Step 15
  `isinst` has landed). TC2 asserts strictly MORE than before — a tighten,
  not a loosening.

## 2. Verification

NeoStep smoke: **154/154 green** (the HEAD baseline). TC2 itself stays green,
now asserting more (type + non-null Message). No new failures. The `isinst`
arm is shared-engine (not Neo-gated), so the tightened assertion exercises the
same opcode on both engines. LEAD non-author diff-read APPROVED (comment-only
on the Cgt_Un expression is byte-identical; TC2 tightened, not loosened).

## 3. Closeout

- `N-CGTUN` and `N-TC2` rows in `.trae/documents/neo-deferred-items.md` §2 /
  §3 marked RESOLVED 2026-07-06 (neo-opportunistic-cleanup); N-CGTUN gets a §4
  Resolved bullet.
- `openspec/changes/neo-completion-portfolio/planning-context.md` note
  appended under "Follow-ups discovered" (resolved section).
- This change archived to
  `openspec/changes/archive/2026-07-06-neo-opportunistic-cleanup/`; the
  `neo-type-checks` delta (1 MODIFIED requirement + 1 added scenario) merged
  into the canonical spec (4 requirements before, 4 after; no unrelated
  requirement lost).
