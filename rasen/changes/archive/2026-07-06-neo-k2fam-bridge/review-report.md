# Review Report — neo-k2fam-bridge

**Reviewer:** adversarial, non-author (review-loop)
**Date:** 2026-07-06
**Verdict:** **APPROVE**
**Working tree:** UNCOMMITTED (test-only; no runtime file touched)

---

## 1. Scope — no runtime file touched (CONFIRMED)

`git diff HEAD --stat -- ILRuntime/` is **EMPTY**. The only code file changed under
the build graph is `TestCases/NeoStep13bTest.cs` (132 lines added, purely additive —
6 new probe methods + a section banner comment). No `ILRuntime/`,
`ILRuntimeTestBase/`, or `ILRuntimeTestCLI/` file is modified.

The remaining diff entries (`.gitignore`, six `Dependencies/*.pdb`, and the
`neo-completion-portfolio/planning-context.md` seed) are pre-existing branch churn
and the planner's seed doc — not part of this change's deliverable. The change is
genuinely TEST-ONLY as the proposal claims. **No finding.**

---

## 2. Each probe is a GENUINE guard (not tautological)

The host helpers used by all 6 probes exist and assert a meaningful value:
`TestCLRBinding.SumTestVector3NoBindingFields(TestVector3NoBinding v)` returns
`(int)(v.x + v.y + v.z)` (`TestClass3.cs:115-118`), and
`MakeTestVector3NoBinding(x,y,z)` constructs `new TestVector3NoBinding(x,y,z)`
(`TestClass3.cs:131-134`). `TestVector3NoBinding` is a no-binder 3-float struct
(`TestVector3.cs:9-11`), so it exercises the reflection-fallback
`CLRMethod.Invoke(byte*)` path — the same path the existing 13b probes use.

The asserted values are field sums, which are representation-agnostic (a correct
flat-bytes read yields the same sum regardless of engine). The buggy
"int-as-mStack-index" corruption (the F-MAJ-1 / pre-fix-K2-FAM signature) would
read the first 4 flat bytes of the struct as an mStack index, then either OOB,
resolve a random object, or mis-copy — producing a field sum that is NOT the
expected int. The DivideByZero pattern (`int _ = z/d` on a wrong result) is the
test-harness idiom the codebase mandates (no xUnit; `[ExpectedException]` does not
exist; `throw new T()` is infeasible).

| # | Probe | Source shape | Expected | Genuine guard? |
|---|-------|--------------|----------|----------------|
| 1 | `BoxSourceByValue` | `object o = v; T t = (T)o; Sum(t)` | 60 (10+20+30) | **YES.** Box (M2 read) + Unbox_Any dest (twin write) + by-value param read. Distinct fields; a mis-copy yields != 60. |
| 2 | `InitobjSourceByValue` | `T t = default(T); Sum(t)` | 0 | **YES.** M1 Initobj path (the review-fix replaced a stale-RefOffset boxed-default write with `Unsafe.InitBlock` flat-bytes zero). A clobber regression yields non-zero garbage. |
| 3 | `BoxMoveByValue` | `o=v; t=(T)o; T t2 = t; Sum(t2)` | 66 (11+22+33) | **YES.** Adds the Move/struct-copy step on a Box-sourced local — the original K2-FAM "Move-path scalar->boxed-ref" signature. Distinct fields from #1. |
| 4 | `ReinitThenByValue` | assign, then `t = default(T); Sum(t)` | 0 (NOT 600) | **YES.** M1 reproduction subtlety: a re-initobj AFTER a real value must zero it. Asserting `0` (not the prior `600`) catches a stale-RefOffset clobber that leaves the prior value visible — exactly the opt-harden-2 review-fix M1 reproduction insight. |
| 5 | `BoxUnboxByValueToHost` | `o=v; t=(T)o; Sum(t)` to host static helper | 6 (1+2+3) | **YES.** Same source shape as #1 with distinct fields (1,2,3) to distinguish the byte path. Redundant with #1 in source shape but harmless additional coverage of the by-value-param read end-to-end into the host helper. |
| 6 | `TwoBoxedStructLocalsByValue` | TWO Box->Unbox locals, both by value | `r1==600 && r2==3` | **YES (LOAD-BEARING).** See §3. |

All 6 field reads route through the host helper `SumTestVector3NoBindingFields`.
**None** perform an IL-side `Ldfld` on a CLR struct field — so none trip the
out-of-scope `[NEO-IL-VT-INSTANCE-COVERAGE]` Step-6 gap. The probes deliberately
avoid the rejected 7th-probe shape (verified: the design.md §"Alternatives"
records that the 7th probe NIEs on HEAD with that gap, hence was dropped).

**No trivial/tautological probe found.** Minor note: probe #5 is
source-shape-redundant with #1 (both are Box->Unbox->by-value, differing only in
field values). It is harmless extra coverage and was explicitly enumerated in the
proposal, so it is not a finding — just an observation.

---

## 3. Load-bearing probe #6 (`TwoBoxedStructLocalsByValue`) — CONFIRMED

The probe genuinely holds **two** Box-sourced struct locals simultaneously (`ta`
and `tb`, each sourced from a Box->Unbox) and asserts **both**:
`if (r1 != 600 || r2 != 3)`. This is exactly the F-MAJ-1 two-live-struct
neighbour-corruption signature (`NeoStep13bTwoClrStructLocalsRegression`), but
with both locals sourced from Box->Unbox instead of method returns — so it guards
the boxed-ref-representation regression specifically for the K2-FAM source shape.

If a future change re-introduced an under-sized `Size=4, RefCount=1` boxed-ref
declaration for a CLR-VT local, the Box write / Unbox_Any write of 12 flat bytes
into a 4-byte slot would overflow into the neighbour's region, corrupting both
Sum reads independently — exactly the failure mode this probe exists to catch.
**The load-bearing guard holds.**

---

## 4. Subsumption reasoning — SOUND

The planner asserts K2-FAM is subsumed by three changes collectively closing the
"int-as-mStack-index" corruption for Box/Initobj/Unbox-sourced CLR-VT locals.
Independent reasoning confirms each link in the chain:

1. **`neo-opt-harden-2` (F-MAJ-1, Option B):** declared a CLR-VT LOCAL as flat
   bytes (`Size = GetNeoValueTypeManagedSize`, `RefCount=0`, `localIsRef=false`)
   in `AllocateLocalStackSpaces` under `#if ENABLE_NEO_MODE`. The OLD boxed-ref
   local declaration (`Size=4, RefCount=1`) — the root of the
   "int-as-mStack-index" mis-read — **no longer exists** for a CLR-VT local.
   Confirmed by the planning-context apply findings.
2. **`neo-opt-harden-2` review-fix (round 1):** rewrote the three runtime arms
   that still assumed the boxed-ref representation:
   - **Initobj** (M1): `Unsafe.InitBlock` flat-bytes zero (no mStack write).
   - **Box** (M2): `ReadNeoValueType` flat-bytes read into an independent boxed copy.
   - **Unbox_Any dest** (the dump-surprise twin of M2): `WriteNeoValueType` flat-bytes write.
   So a CLR-VT local sourced from Box/Initobj/Unbox is **flat bytes end-to-end**.
3. **`implement-neo-step13b`:** unified the by-value-param read
   (`CopyNeoCallArguments` byte-copies N flat bytes from the caller local's
   `Offset`). Combined with (1)+(2), a CLR-VT local — regardless of source — is
   flat bytes, and passing it by value reads flat bytes.

The conjunction closes the defect: the "int-as-mStack-index" mis-copy is no longer
reachable for any Box/Initobj/Unbox-sourced CLR-VT local. The 6 PASSING probes on
HEAD are independent empirical confirmation of the closure (a green probe is
necessary-and-sufficient evidence the defect is not currently reachable — and the
neighbour-corruption probe #6 is the adversarial check that the smoke originally
missed). **The test-only approach is correct.** No shape is missed by the
subsumption.

---

## 5. Smoke reproduced

| Run | Filter | Result |
|-----|--------|--------|
| Neo `Debug_Neo` + `useRegister=true` | `NeoStep` | **146/146 PASS** (140 baseline + 6 new) |
| Neo `Debug_Neo` + `useRegister=true` | `K2Fam` | **7/7 PASS** (6 new probes + existing `NeoStep13_K2FamRegression`) |
| Legacy plain `Debug` + `useRegister=true` | `K2Fam` | **7/7 PASS** (Legacy-neutral; assertions are representation-agnostic field sums) |

Builds clean (0 errors). No test exceeded ~10s (no interpreter infinite loop).

---

## Verdict: **APPROVE**

- No runtime file touched: **CONFIRMED** (only `TestCases/NeoStep13bTest.cs`).
- All 6 probes genuine guards: **CONFIRMED** (no tautology; each asserts a
  meaningful field sum; all route field reads through host helpers, avoiding the
  out-of-scope IL-Ldfld-on-CLR-struct gap).
- Load-bearing #6 holds 2 simultaneous Box-sourced locals and asserts both
  (`r1==600 && r2==3`): **CONFIRMED**.
- Subsumption reasoning sound: **CONFIRMED** (opt-harden-2 declare-side +
  review-fix Initobj/Box/Unbox_Any arms + step13b by-value-param read collectively
  close the int-as-mStack-index corruption for all three source shapes).
- Smoke reproduced: NeoStep 146/146, K2Fam 7/7, Legacy-neutral.

**Observations (non-blocking, NOT findings):**
- Probe #5 is source-shape-redundant with #1 (both Box->Unbox->by-value, differing
  only in field values). Harmless extra coverage; was explicitly enumerated in the
  proposal. No action needed.
- The change is a faithful, low-risk closure of the K2-FAM deferred item. The
  regression-guard value is real (Step 17 B1 / F-MAJ-1 both taught that a green
  smoke can hide a representation bug; these probes prevent that for K2-FAM).
