# Review Report — neo-array-completion (D-ARR rank-1 array gaps)

**Reviewer:** adversarial non-author (review-skill, no subagents).
**Date:** 2026-07-06.
**Base:** `master` · **Branch:** `features/object-model-overhaul`.
**Diff scope:** `git diff HEAD` (working tree UNCOMMITTED — 4 engine files + 1 test file + 1 doc; binary `.pdb` churn ignored).
**Verdict: APPROVE.** No Blockers, no Majors introduced by this change. One Major PRE-EXISTING follow-up surfaced (the 8-byte-double-local optimizer quirk — real, silent, but upstream of D-ARR and correctly worked around). One Minor honesty gap (TC8 provenance). All MANDATORY probes passed.

---

## 0. Scope check

**Intent (proposal):** close 5 "rare in C# output" rank-1 array gaps — `Stelem_I` runtime arm; generic-token `Code.Ldelem`/`Code.Stelem` + native `Code.Ldelem_I`/`Code.Ldelem_U8` JIT enumeration; F-4 other-width Stind/Ldind CLR-array branches.
**Delivered (apply resolutions collapsed the 5 → 2):** the Mono.Cecil fork has no `Code.Ldelem`/`Code.Stelem`/`Code.Ldelem_U8` (generic form IS `Ldelem_Any`/`Stelem_Any`, already handled; `Ldelem_U8` is not a real ECMA opcode). So the real work = (a) `Stelem_I` + `Ldelem_I` dedicated runtime arms (Option B, dispatching on int[]/uint[]/IntPtr[]/UIntPtr[]); (b) `Ldelem_I` JIT case + 5-site optimizer cascade; (c) F-4 other-width `is Array` Stind/Ldind branches. Scope: CLEAN — the collapse is documented honestly in `design.md` "Apply resolutions" and the deviations land in `tasks.md`.

---

## 1. MANDATORY Probe #1 — optimizer cascade COMPLETENESS (HIGHEST PRIORITY)

**PASS.** `Ldelem_I` is present in EVERY enumeration where its `Ldelem_I4`/`Ldelem_I8`/`Ldelem_U4` siblings appear.

Grepped all `OpCodeREnum.Ldelem_I4` occurrences across `RegisterVM/` (sibling-canary). 6 enumerations total (excluding runtime arms + a comment):

| File:line (sibling anchor) | `Ldelem_I` added? |
|---|---|
| `Optimizer.Utils.cs:660` (`GetOpcodeSourceRegister`) | YES (line 663) |
| `Optimizer.Utils.cs:847` (`GetOpcodeDestRegister`) | YES (line 850) |
| `Optimizer.Utils.cs:1207` (`ReplaceOpcodeSource`) | YES (line 1210) |
| `Optimizer.Utils.cs:1487` (`ReplaceOpcodeDest`) | YES (line 1490) |
| `Optimizer.Neo.cs:992` (`LowerNeoOffsets` Ldelem block) | YES (line 995) |
| `JITCompiler.cs:2103` (`Translate`, CIL `Code.Ldelem_I4`) | YES — `case Code.Ldelem_I:` at 2125 (3-reg shape) |

**No MISS.** Critically, `FCP.cs` / `BCP.cs` / `RegisterCleanup.cs` do NOT enumerate the `Ldelem_*` family at all (grep for `OpCodeREnum.Ldelem_` returns only the 5 Utils/Neo sites + the 2 runtime arms), so there is nothing to add there. `Stelem_I` was ALREADY in all 5 optimizer enumerations pre-change (`692/901/1228` in Utils, `1011` in Neo, `2245` in JIT) — only its runtime arm was missing, confirmed.

**Empirical confirmation:** TC8's method JIT-compiles `Ldelem_I` through the full optimizer pipeline (FCP + BCP + copy-prop + RegisterCleanup all run during JIT) and runs clean at 161/161. If any pass lacked `Ldelem_I` it would NIE at JIT time — it does not. I also added a temporary high-register-pressure probe (8 live int locals + 4 `Ldelem_I` reads kept live across them) — no optimizer NIE fired (failures observed were the pre-existing by-value-CLR-struct-param gap on `(int)IntPtr` and the pre-existing `Stsfld` Step-6 NIE, neither related to `Ldelem_I`). Probe reverted; test file restored byte-for-byte to the implementer's.

---

## 2. MANDATORY Probe #2 — Stelem_I / Ldelem_I dispatch correctness

**PASS.** Verified by code inspection + TC8's clean run.

- **Routing (each kind reads back correct value):** the dedicated `Stelem_I` arm (`ILIntepreter.Neo.cs:3028-3049`) reads `val4 = *(int*)(frameBase + ip->Operand4)` and dispatches `int[] sia[si]=val4` / `uint[] sua[si]=(uint)val4` / `IntPtr[] ipa[si]=(IntPtr)val4` / `((UIntPtr[])sa)[si]=(UIntPtr)val4`. The symmetric `Ldelem_I` arm (`:2928-2946`) reads `v` (int) and writes `*(int*)(frameBase + ip->DstOffset) = v`. Native-int is I4-width on this VM (4-byte frame slot; matches the pre-existing `Stind_I`/`Ldind_I` `goto I4` idiom). The implementer's dump-confirm (`Stelem_I` writes 100/-7/4660; `Ldelem_I` reads 100/-7/4660) corroborates the round-trip including negative-value sign-extension (`-7`, `0x1234`).
- **Why Option A (`goto Stelem_I4`) was correctly REJECTED:** I confirmed `Stelem_I4` arm (`:3051-3061`) only handles `int[]`/`uint[]` — an `IntPtr[]` would hit the `((uint[])sa)` fallback and throw `InvalidCastException`. Option B is the right call.
- **Wrong-type / type-mismatch behavior:** the dedicated arm's final `else ((UIntPtr[])sa)[si] = (UIntPtr)val4;` cast is the catch-all — a WRONG kind (e.g. an `long[]` reaching `Stelem_I`) would throw `InvalidCastException` (clean), matching the precedent set by `Stelem_I4`/`Stelem_I8` (same `else ((T[])sa)` fallback shape). No silent corruption path.
- **Out-of-bounds:** `if (srcIdx < 0) throw new NullReferenceException();` guards null-array; the CLR array indexer throws `IndexOutOfRangeException` on a bad element index. Clean.
- **4-byte negative-value round-trip:** `-7` (`0xFFFFFFF9` as int → `(IntPtr)(-7)` → `(int)ipa[li]` = -7) round-trips; dump-confirmed.

---

## 3. MANDATORY Probe #3 — F-4 other-width Stind/Ldind Array branches

**PASS.** Verified each width's `is Array` branch is byte-identical-additive to the I4 precedent (`Stind_I4:3123` / `Ldind_I4:3198`, step17-completion).

- **I4 precedent unchanged (additive):** every new line is an `else if (mStack[objIdx] is Array cArr) <typed-op>;` inserted BETWEEN the `objIdx == -1` (frame-native) branch and the `GetNeoILInstance` branch. The frame-native and IL-instance branches are byte-for-byte unchanged (confirmed in the diff hunks). TC16 guards the I4 path stays green.
- **Width matrix round-trip (TC12 I8 / TC13 R4 / TC14 R8 all PASS after fix):**
  - `Stind_I8`: `cArr.SetValue(v, off)` where `v` is `long`. `Ldind_I8`: `*(long*)(...DstOffset) = (long)cArr.GetValue(off)`. TC12 (`long[]`, sets `arr[1]=9999999999L` via `ref`, reads back all 3) PASS — load-bearing (FAIL-on-HEAD `InvalidCastException`, confirmed below).
  - `Stind_R4`: `cArr.SetValue(v, off)` (`float`). `Ldind_R4`: `(float)cArr.GetValue(off)`. TC13 PASS.
  - `Stind_R8`: `cArr.SetValue(v, off)` (`double`). `Ldind_R8`: `(double)cArr.GetValue(off)`. TC14 PASS.
  - `Stind_I1/I2`, `Ldind_I1/U1/I2/U2/U4/R4/R8`: same one-line `SetValue`/typed-cast-`GetValue` shape (no direct probes for every width, but TC12/13/14 cover the I8/R4/R8 widths end-to-end and the I1/I2/U1/U2/U4 branches are mechanical mirrors — the `Stind_I4`/`Ldind_I4` precedent has been green since step17-completion and TC16 guards it).
- **Byref to CLR ARRAY element vs byref to CLR OBJECT field:** the `is Array` discriminator correctly routes a `ldelema`-produced CLR-array address into the Array branch and leaves the IL-instance-byref path (heap IL ref field) to its existing `GetNeoILInstance`/NIE branch. The `Stind_Ref`/`Ldind_Ref` array branches (correct-by-construction, mirroring the frame-native ref materialization `dstIdx = frameRefBase + ip->Operand3`) are unreachable today (upstream ldelema ref-type NIE) — see Probe #7.
- **FAIL-on-HEAD confirmed for the F-4 arms:** with the 4 engine files stashed (HEAD engine), TC12/TC13/TC14 each FAIL with `"Step 17/13b: field/element access on a CLR object via the IL-instance path is deferred"` — exactly the `GetNeoILInstance` fallback throwing because the `is Array` branch was absent. LOAD-BEARING confirmed.

---

## 4. MANDATORY Probe #4 — 8-byte-locals optimizer quirk

**REAL BUG (Major, PRE-EXISTING, follow-up — NOT introduced by D-ARR).** Reproduced independently and refined beyond the implementer's report.

The implementer wrote "reading 3 long/double elements into separate locals combined in one `if` yields 0." My reproduction (temporary probes, since reverted):

| Probe (patched engine, direct `||` combine) | Result |
|---|---|
| 3 `long` locals, combined `if (a0!=..||a1!=..||a2!=..)` | **PASS** (longs round-trip fine) |
| 3 `double` locals, combined | **FAIL** (DivideByZero — assertion tripped) |
| **2** `double` locals, combined | **FAIL** (DivideByZero) |
| each `double` in its OWN `if` (no combine) | **PASS** |

**Refined characterization:** the quirk is NOT "3+ locals" and NOT "long" — it is **2+ `double` locals combined in one boolean expression** that yields a silent wrong result. A single `double` read is correct; combine two in one `if` and the comparison misfires. This is the F-MAJ-1 class (silent wrong result on 8-byte primitives) and broader than the implementer's note suggests (the implementer's `long` workaround in TC11/TC12 is conservative but not strictly necessary for `long`; the `double` workaround in TC14 IS necessary).

**Why it is NOT a D-ARR bug:** the failing comparison uses plain `Ldelem_R8` reads (the pre-existing fast typed-indexer path, NOT the new `is Array` branches) and a pure double-local combine. The corruption is in how the optimizer/runtime handles 2+ simultaneous `double` locals in a combined expression — upstream of, and independent from, the array work. The implementer correctly worked around it (incremental `bad`-fold pattern in TC11/TC12/TC14) and flagged it as a potential follow-up.

**Action:** record as a new follow-up (suggest name `neo-double-combine-quirk` or fold into the existing F-MAJ-1 / `[NEO-DBL-COMBINE]` tracker). Do NOT block this change on it. See Finding F-1.

---

## 5. MANDATORY Probe #5 — Legacy-neutral independent confirm

**PASS (byte-identical).** Built plain `Debug` CLI with the 4 engine files stashed (HEAD engine, TestCases still carrying the new probes), ran `NeoStep16` filter:

| Engine | NeoStep16 result |
|---|---|
| HEAD engine (stashed) | 14 tests, **1 failed** = TC8 (`NotImplementedException: Unknown Opcode:Ldelem_I` — Legacy runtime lacks the `Ldelem_I` arm, a pre-existing Legacy gap) |
| Patched engine | 14 tests, **1 failed** = TC8 (same `Ldelem_I` NIE — Legacy runtime still lacks the arm; the shared JIT/optimizer changes only add handling for a previously-NIE'd opcode, Legacy's own runtime arms are untouched) |

Identical failure set with/without the change. **Legacy-neutral confirmed.** (Note: the design.md says "8 failures" and CLAUDE.md says "9 pre-existing Legacy NeoStep failures" — those refer to the FULL `NeoStep` Legacy filter, not the `NeoStep16` sub-filter run here; the `NeoStep16` sub-filter has exactly 1 Legacy failure = TC8, identical both ways. Not a discrepancy in this change's claims, just a filter-scope detail.)

---

## 6. MANDATORY Probe #6 — NeoStep 161/161 reproduced independently

**PASS.** `dotnet run -c Debug_Neo -f net8.0 ... true NeoStep` → **`Ran 161 tests, 0 failed, 0 ignored, 0 todos`**. Reproduced twice (once after initial build, once after restoring the test file post-probe-revert). 154 baseline + 7 new keeper probes (TC8/TC10/TC11/TC12/TC13/TC14/TC16) = 161.

---

## 7. MANDATORY Probe #7 — omitted probes (TC9 UIntPtr[], TC15 ref-array) genuinely pre-existing

**CONFIRMED pre-existing upstream gaps, recorded as accepted-known (not silently dropped).**

- **TC9 UIntPtr[]:** the design (`design.md` "Apply resolutions") documents that `GetPrimitiveSize` (AppDomain.cs:1947) does not recognize `UIntPtr` (only `IntPtr`), so any `UIntPtr`-typed local throws at `AllocateLocalStackSpaces`. The `Stelem_I`/`Ldelem_I` arms DO dispatch on `UIntPtr[]` (the `else ((UIntPtr[])sa)[si]` arm) but no C# shape can reach it. This is a pre-existing unsupported-primitive gap, correctly omitted and documented in both the design and the in-test comment block at TC9's slot.
- **TC15 ref-array:** the Neo `ldelema` arm throws a Step-17 NIE for CLR arrays with a reference-type element ("...deferred (use direct indexing)"). That NIE sits UPSTREAM of the new `Stind_Ref`/`Ldind_Ref` `is Array` branch, so no C# shape can reach the Ref array branch. Correctly omitted; the new branches are correct-by-construction (mirror the I4 precedent TC16 guards). Documented in design + in-test comment.

Both omissions are honestly recorded with their root cause and an explicit "accepted-known / pre-existing" label. Not silently dropped.

---

## Findings

### F-1 — 8-byte-`double`-local combine optimizer quirk (Major, PRE-EXISTING follow-up)
- **Severity:** Major (silent wrong result) — but **PRE-EXISTING and out of scope for D-ARR**. Do NOT block.
- **File:** optimizer/runtime —suspect: `AllocateLocalStackSpaces` slot-reuse/liveness for 8-byte primitives (same family as F-MAJ-1 / OPT-HARDEN-2), or the `double`-local combine in copy-prop. Exact locus not pinned in this review.
- **Probe:** 2 `double` locals read from a `double[]` via `Ldelem_R8`, combined in one `if (a0 != x || a1 != y)` → assertion trips (silent wrong result). Single-`double`-per-`if` works. 3 `long` locals combined work fine (the quirk is `double`-specific, not all 8-byte primitives).
- **Evidence:** reproduced in this review (temporary `NeoStep16_REV_TwoDoubleDirectIf` / `ThreeDoubleDirectIf` / `SingleDoubleEach` probes; reverted). The implementer's TC13 (`float`, direct combine) PASSES; TC14 (`double`) needed the incremental `bad` workaround — consistent with the quirk being `double`-specific.
- **Fix:** out of scope. Record as a new follow-up child (suggest `[NEO-DBL-COMBINE]` or extend F-MAJ-1 / OPT-HARDEN-2 scope). The implementer's incremental-`bad` workaround in TC11/TC12/TC14 is the correct test-authoring response meanwhile.

### F-2 — TC8 provenance / naming honesty (Minor)
- **Severity:** Minor (documentation accuracy; no correctness impact).
- **File:** `TestCases/NeoStep16Test.cs:148-176` (`NeoStep16_TC8_StelemI_NIntArray`).
- **Issue:** TC8 is titled `StelemI` and its comment claims it exercises the `Stelem_I` runtime arm. But on HEAD its failure is `NotImplementedException: Unknown Opcode:Ldelem_I` — the JIT NIE on `Code.Ldelem_I` fires at method-JIT time (before any statement executes), so TC8 actually proves the **`Ldelem_I` JIT case + optimizer cascade** is load-bearing, NOT the `Stelem_I` runtime arm. The `Stelem_I` runtime arm's correctness is proven only by the apply-time runtime debug output (`design.md`), not by TC8's pass/fail.
- **Fix (optional):** rename to `NeoStep16_NativeIntArray_LdelemI_StelemI` and adjust the comment to say "proves the `Ldelem_I` JIT case + optimizer cascade load-bearing; the `Stelem_I` runtime arm is proven by apply-time debug." Or leave as-is — the probe IS load-bearing for the change overall, just not for the specific arm its name implies. Trivial.

### F-3 — `Stind_I4`/`Ldind_I4` `is Array` branch is byte-identical-additive (Trivial / positive confirmation)
- **Severity:** Trivial (verification note, not a defect).
- **File:** `ILIntepreter.Neo.cs:3145-3365` (F-4 additions) vs the I4 precedent.
- **Note:** the new `is Array` branches are mechanical one-line `else if` inserts between existing branches; the frame-native and IL-instance paths are byte-for-byte unchanged. TC16 guards the I4 path. No regression surface introduced. (Recorded as a positive finding for the archive step.)

---

## Summary

- **Optimizer cascade completeness (Probe #1):** `Ldelem_I` present in ALL 6 sibling enumerations; no MISS. Empirically confirmed (TC8 + a high-register-pressure stress probe, both clean). `Stelem_I` was already complete pre-change.
- **Dispatch correctness (Probe #2):** dedicated int[]/uint[]/IntPtr[]/UIntPtr[] dispatch is correct; Option A correctly rejected; wrong-type → clean `InvalidCastException`; OOB → clean; negative-value sign-extension round-trips (dump-confirmed).
- **F-4 width matrix (Probe #3):** I8/R4/R8 round-trip via `ref`+`ldelema` → `stind`/`ldind` proven (TC12/13/14); I4 precedent byte-identical-additive; FAIL-on-HEAD confirmed.
- **8-byte-locals quirk (Probe #4):** REAL, silent, `double`-specific (2+ combined), pre-existing, upstream of D-ARR. Correctly worked around + flagged. Recorded as F-1 follow-up.
- **Legacy-neutral (Probe #5):** byte-identical (NeoStep16: 1 failure = TC8 `Ldelem_I` NIE, identical with/without the change).
- **Smoke (Probe #6):** Neo 161/161 reproduced independently (twice).
- **Omitted probes (Probe #7):** TC9 UIntPtr-primitive gap + TC15 ldelema-ref-type NIE both genuinely pre-existing upstream, recorded as accepted-known.

**Verdict: APPROVE.** Ship after the (post-review, out-of-scope) archive steps: merge the `neo-arrays` spec delta, mark D-ARR rank-1 resolved in `neo-deferred-items.md`, and file F-1 (`neo-double-combine-quirk`) as a follow-up child.

Working tree UNCOMMITTED (no commits made by this review). Test file restored byte-for-byte to the implementer's version (all temporary review probes reverted; verified `0 REV_` lines, `7` keeper probes).
