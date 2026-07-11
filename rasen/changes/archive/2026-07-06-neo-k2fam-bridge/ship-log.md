# Ship Log — neo-k2fam-bridge

**Date:** 2026-07-06
**Change type:** TEST-ONLY (K2-FAM was probed on HEAD and found SUBSUMED — no
runtime fix shipped).
**Shipper:** shipper role (post-review; review APPROVED, no findings).
**Working tree:** UNCOMMITTED (LEAD commits after the shipper finishes).

---

## 1. Outcome

**K2-FAM is RESOLVED — SUBSUMED by recent work; this change ships test-only
regression guards.** No `ILRuntime/` runtime / JIT / optimizer / CLR-binding
file is touched. `git diff HEAD --stat -- ILRuntime/` is EMPTY (confirmed in
the review report, section 1).

The K2-FAM deferred item ("a CLR value-type LOCAL sourced from Box/Initobj,
passed by value to a CLR method, mis-reads an int as an mStack index" — a
silent-wrong-result defect from the old boxed-ref CLR-VT-local representation)
was probed on HEAD `f7539642` and is **no longer reproducible**: every
adversarial reproducer for the Box/Initobj/Unbox source shapes PASSES. The
defect was subsumed as a side effect of three changes whose combined effect was
re-assessed against K2-FAM by this change:

- **`neo-opt-harden-2` (F-MAJ-1)** declared a CLR-VT LOCAL as flat bytes
  (`Size = GetNeoValueTypeManagedSize, RefCount = 0, localIsRef = false` under
  `#if ENABLE_NEO_MODE`) in `AllocateLocalStackSpaces`. The OLD boxed-ref
  representation (`Size=4, RefCount=1`) that produced the
  "int-as-mStack-index" corruption **NO LONGER EXISTS** for a CLR-VT local.
- **`neo-opt-harden-2` review-fix (round 1)** rewrote the three runtime arms
  that still assumed the boxed-ref representation: `Initobj` (M1:
  `Unsafe.InitBlock` flat-bytes zero), `Box` (M2: `ReadNeoValueType` flat-bytes
  read), and `Unbox_Any` dest (the M2-twin: `WriteNeoValueType` flat-bytes
  write). So a local sourced from Box/Initobj/Unbox is flat bytes end-to-end.
- **`implement-neo-step13b`** unified the by-value-param read in
  `CopyNeoCallArguments` to byte-copy N flat bytes from the caller local's
  `Offset`.

The closure holds for ALL source shapes of a CLR-VT local (return / Box /
Initobj / Unbox). The Box/Initobj-source half that was previously DEFERRED is
now realized.

**This is a resolution-by-recent-work attribution, NOT a new engine fix.** The
closure was real but previously UNGUARDED — no smoke probe exercised a
Box/Initobj/Unbox-sourced CLR-VT local passed by value, so a future regression
that re-introduces a boxed-ref local representation (or breaks a flat-bytes
Box/Initobj/Unbox arm) would slip past the green smoke unchanged. This change
locks the closure in with adversarial regression guards and closes the deferred
item.

---

## 2. What shipped (TEST-ONLY)

Six adversarial regression guards added to `TestCases/NeoStep13bTest.cs`
(co-located with the existing return-source `NeoStep13_K2FamRegression` probe,
the existing K2-FAM home), each realizing a distinct Box/Initobj/Unbox-sourced
CLR-VT-local -> by-value-param shape:

1. `NeoStep13_K2Fam_BoxSourceByValue` — local via Box->Unbox (`object o = v;
   T t = (T)o`), by value (== 60).
2. `NeoStep13_K2Fam_InitobjSourceByValue` — local via `default(T)` (Initobj),
   by value (== 0).
3. `NeoStep13_K2Fam_BoxMoveByValue` — Box->Unbox->struct-copy (Move)->by value
   (== 66).
4. `NeoStep13_K2Fam_ReinitThenByValue` — local re-initobj'd via
   `= default(T)`, by value (== 0, the post-re-init value, NOT the prior
   assigned value — catches a stale-RefOffset clobber).
5. `NeoStep13_K2Fam_BoxUnboxByValueToHost` — Box->Unbox->by-value to a host
   static helper (== 6).
6. `NeoStep13_K2Fam_TwoBoxedStructLocalsByValue` — TWO Box->Unbox-sourced
   locals both by value (the F-MAJ-1 two-live-struct stress, Box-sourced;
   `r1==600 && r2==3`). **LOAD-BEARING** neighbour-corruption guard — if a
   future change re-introduced an under-sized `Size=4, RefCount=1` boxed-ref
   declaration, the Box/Unbox flat-bytes write would overflow into the
   neighbour's region, corrupting both Sum reads independently.

All field reads route through the host helper
`TestCLRBinding.SumTestVector3NoBindingFields(TestVector3NoBinding v)` (returns
`(int)(v.x + v.y + v.z)`, a representation-agnostic field sum). **None** perform
an IL-side `Ldfld` on a CLR struct field — so none trip the separate,
out-of-scope `[NEO-IL-VT-INSTANCE-COVERAGE]` Step-6 gap (the IL-side-`Ldfld`-on-
CLR-struct gap, also surfaced by the `neo-step13-area4` review and Step 19
TC10). `TestVector3NoBinding` (no binder, pure-primitive 3-float struct) is used
throughout, exercising the reflection-fallback `CLRMethod.Invoke(byte*)` path.
The DivideByZero-assertion pattern (`int _ = 1/0` on a wrong result) is used
because the harness is not xUnit (`[ExpectedException]` does not exist;
`throw new T()` is infeasible) and the K2-FAM defect is a SILENT wrong result.

The existing return-source `NeoStep13_K2FamRegression` probe (Step 13b) stays
— this change adds the Box/Initobj/Unbox source shapes alongside it without
perturbing the return-source path.

---

## 3. Verification

| Run | Build | Filter | Result |
|-----|-------|--------|--------|
| Neo | `Debug_Neo` + `useRegister=true` | `NeoStep` | **146/146 PASS** (140 baseline + 6 new) |
| Neo | `Debug_Neo` + `useRegister=true` | `K2Fam` | **7/7 PASS** (6 new probes + existing `NeoStep13_K2FamRegression`) |
| Legacy-neutral | plain `Debug` + `useRegister=true` | `K2Fam` | **7/7 PASS** (representation-agnostic field-sum assertions hold on both engines) |

Builds clean (0 errors). No test exceeded ~10s (no interpreter infinite loop).
The broader Legacy `NeoStep13` filter has 2 PRE-EXISTING failures (the
`NeoStep13Test.NeoTestClrStructNoBindingBoxRoundTrip` family — different file /
class, already listed in the standing Legacy pre-existing failure set) — NOT
caused by this change and NOT in the K2-FAM family.

The `K2Fam` group filter confirms the probes collectively cover all K2-FAM
source shapes (return / Box / Initobj / Unbox / Move / re-init / two-live).

---

## 4. Review outcome

**APPROVED, no findings.** The adversarial non-author review confirmed:
- No runtime file touched (only `TestCases/NeoStep13bTest.cs`).
- All 6 probes are genuine guards (no tautology; each asserts a meaningful
  field sum; all route field reads through host helpers, avoiding the
  out-of-scope IL-`Ldfld`-on-CLR-struct gap).
- The load-bearing probe #6 holds 2 simultaneous Box-sourced locals and
  asserts both (`r1==600 && r2==3`) — the F-MAJ-1 two-live-struct
  neighbour-corruption signature, but Box-sourced.
- The subsumption reasoning is sound: opt-harden-2 declare-side + review-fix
  Initobj/Box/Unbox_Any arms + step13b by-value-param read collectively close
  the int-as-mStack-index corruption for all three source shapes.
- Smoke reproduced: NeoStep 146/146, K2Fam 7/7, Legacy-neutral.

Two non-blocking observations (NOT findings): probe #5 is source-shape-
redundant with #1 (both Box->Unbox->by-value, differing only in field values;
harmless extra coverage, explicitly enumerated in the proposal); and the
change is a faithful, low-risk closure (Step 17 B1 / F-MAJ-1 both taught that
a green smoke can hide a representation bug — these probes prevent that for
K2-FAM).

---

## 5. Spec + tracker closure

- **`openspec/specs/neo-boxing/spec.md`** — the K2-FAM closure requirement
  moves from PARTIAL/DEFERRED to DELIVERED; the Out-of-scope deferrals K2-FAM
  bullet is removed. Requirement count unchanged (12 before, 12 after — the
  change MODIFIES two existing requirements, adds/removes none). Merged at
  archive.
- **`.trae/documents/neo-deferred-items.md`** — K2-FAM row / entry / Resolved
  bullet updated to RESOLVED (subsumed, 2026-07-06). The F-2 /
  INLINER-REFONLY-VT cross-reference left intact (distinct inliner ref-fold
  defect class, still deferred).
- **`openspec/changes/neo-completion-portfolio/planning-context.md`** — K2-FAM
  resolution appended under "Follow-ups discovered -> Resolved follow-ups".

---

## 6. Lesson re-affirmed

The Q-NEWOBJ / Q-STRUCT / Q-LONG / F-5 family lesson holds: **a deferred item
that has not been re-probed on recent HEAD may already be SUBSUMED by later
work.** The K2-FAM partial-close note (Step 13b apply, 2026-07-04) said the
boxed-ref-source half "needs IL-side ldfld/stfld on CLR struct fields for a
clean reproducer" — but THREE subsequent changes (opt-harden-2 + its review-fix
+ the 13b by-value-param read) made the local flat bytes and the boxed-ref
representation ceased to exist. **Construct the reproducer FIRST, on current
HEAD, before designing a fix.** Here the reproducer PROVED the closure, turning
a would-be engine fix into a test-only lock-in. Probe-before-fixing; subsumed
defects ship test-only.

---

## 7. Did NOT

- **Did NOT git commit/push** (per process discipline; the LEAD commits after
  the shipper finishes).
- **Did NOT modify any `ILRuntime/` runtime / JIT / optimizer / CLR-binding
  file** (test-only).
- **Did NOT ship a failing probe** (the rejected 7th-probe shape — an
  IL-method-body `Ldfld` on a CLR struct param — NIEs on HEAD with the separate
  `[NEO-IL-VT-INSTANCE-COVERAGE]` Step-6 gap, hence was dropped).
