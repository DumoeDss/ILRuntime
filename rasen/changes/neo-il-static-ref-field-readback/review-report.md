# Review Report — neo-il-static-ref-field-readback (child 13)

**Reviewer:** verifier (author != verifier gate; autonomous LEAD run, Tier A)
**Branch:** `features/object-model-overhaul`
**Date:** 2026-07-12
**Mode:** dispatched (report-only) — no fixes applied, no subagents spawned

## Verdict: **APPROVE-WITH-FINDINGS**

The pivoted root cause (newobj dest/arg **reference** alias clobber) is
**independently confirmed**. The fix is correct, tight, Neo-gated, and does not
over-fire. All gates pass (NeoStep 339/0; `TestStaticFieldInstance` PASS; canaries
intact; stash-toggle proven both directions; Legacy 4/0). Findings below are all
**Minor / informational** — none block shipping. The single load-bearing nuance
worth surfacing is that the diff silently reorders `*(int*)retDstPtr = newobjDstIdx`
to *after* `CopyNeoCallArguments`; this is essential to the fix and correct, but
design.md does not call it out as a distinct decision.

---

## 1. New root cause — CONFIRMED

**The IL-static ref-field read-back is correct; the framed hypothesis is disproven.**

- **Stsfld IL-static ref arm** (`ILIntepreter.Neo.cs:4294-4300`):
  `int srcRefIdx = *(int*)srcSlot; sinst.ManagedObjects[off.ReferenceOffset] = srcRefIdx >= 0 ? mStack[srcRefIdx] : null;`
  — writes the source register's mStack object into the static field's ref region. Faithful.
- **Ldsfld IL-static ref arm** (`ILIntepreter.Neo.cs:4439-4446`):
  `object rv = sinst.ManagedObjects[off.ReferenceOffset]; mStack.Add(rv); *(int*)dstSlot = mStack.Count - 1;`
  — reads the stored object back and materializes a fresh ref slot. Symmetric with Stsfld. Faithful.

The planner's disproof holds: the round-trip is innocent.

**The real defect is in the IL reference-type newobj arm (`ILIntepreter.Neo.cs:3309-3349`).**
Independently confirmed the ordering and the alias mechanism:

- Decls at `ILIntepreter.Neo.cs:3116-3118` (Newobj case): `dstRefOffset = ip->Operand3;`
  `int newobjDstIdx = frameRefBase + dstRefOffset;` `byte* retDstPtr = frameBase + ip->DstOffset;`
  — so `retDstPtr` IS exactly `frameBase + ip->DstOffset` (load-bearing for the fix; see §2).
- The arm runs `ins = ilNewobjType.Instantiate(false);` → (re-base guard) →
  `mStack[newobjDstIdx] = ins;` (line 3345) → `*(int*)targetBase = newobjDstIdx;` →
  `CopyNeoCallArguments(...)` (line 3348). **The instance store runs BEFORE the arg copy.**
- For the canonical lowering `ldloc/ldstr refArg; newobj(refArg)`, the JIT
  (`Optimizer.Neo.cs:1451-1457`) sets the newobj dest from `localInfos[op.Register1]`
  and the eval-stack lowering reuses the arg's register as the dest. When that happens
  the dest RefOffset == the arg RefOffset, the arg object lives at `mStack[newobjDstIdx]`,
  and `mStack[newobjDstIdx] = ins` clobbers the arg before `CopyNeoCallArguments` copies
  it. The ctor then receives `this` as the aliased reference arg. (JIT dump in the
  smoke shows `newobj r2, r2, TestA..ctor` — dest r2 == arg r2 — exactly this alias.)
- `NeoCallParamMap.RefSrc` is populated with each reference arg's `srcInfo.RefOffset`
  (`Optimizer.Neo.cs:1424-1428`), and the dest register's RefOffset is `ip->Operand3`
  (`Optimizer.Neo.cs:1457`), so the alias is detectable as `dstRefOffset ∈ map.RefSrc`.

## 2. The fix — CORRECT (alias detection + re-base + primitive exclusion + no over-fire)

The added block (`ILIntepreter.Neo.cs:3330-3344`) and the reordered
`*(int*)retDstPtr = newobjDstIdx` (now at line 3349, after the copy):

- **Alias detection — sound & exact.** `map.RefSrc != null && map.PrimitiveSrc != null`,
  then scan `map.RefSrc` for any entry `== dstRefOffset`. Because each register owns a
  unique RefOffset, `map.RefSrc[ri] == dstRefOffset` iff a **reference** arg occupies the
  dest register. Confirmed against the optimizer builder.
- **Clobber predicate — tight.** `aIdx = *(int*)(frameBase + ip->DstOffset); aIdx >= 0 && aIdx == newobjDstIdx`.
  This is the *actual* clobber condition (the arg index IS the slot about to be overwritten).
  If the arg's primitive value is some other slot (e.g. a fresh `mStack.Add` temp), there is
  no clobber and the guard correctly does not fire.
- **Re-base — preserves the arg.** `mStack.Add(mStack[aIdx])` copies (not moves) the arg
  object to a fresh slot; `*(int*)(frameBase + ip->DstOffset) = mStack.Count - 1` rewrites
  the source the copy will read. Since the aliased arg's `PrimitiveSrc == ip->DstOffset`
  (same register), `CopyNeoCallArguments` (`ILIntepreter.Neo.cs:426`,
  `Unsafe.CopyBlock(targetBase + PrimitiveDst[i], frameBase + PrimitiveSrc[i], PrimitiveSize[i])`)
  now copies the *fresh* index → the ctor reads `mStack[fresh]` = the arg object, while
  `this` = `mStack[newobjDstIdx]` = `ins`. Correct end state.
- **Primitive-int exclusion — CONFIRMED.** `CopyNeoCallArguments` only iterates
  `PrimitiveSize` and never consults `RefSrc`/`RefDst` (verified at `ILIntepreter.Neo.cs:382-426`).
  A primitive int arg has `RefCount 0`, so it contributes nothing to `RefSrc`
  (`Optimizer.Neo.cs:1424`, the ref-loop body does not execute) → `map.RefSrc` can never
  contain its RefOffset → the discriminator cannot fire for an int arg even if its value
  coincidentally equals `newobjDstIdx`. Design D2 holds.
- **No over-fire.** The discriminator requires a reference arg to literally occupy the
  dest register (the alias itself); it cannot fire for a non-aliasing newobj. Even in a
  hypothetical false positive the re-base is benign (it copies the arg; the ctor still
  receives the correct object).
- **Load-bearing reorder (not a separate decision in design.md).** `retDstPtr ==
  frameBase + ip->DstOffset` (decl at line 3118) is the *same* location the re-base
  rewrites. In the pre-fix code `*(int*)retDstPtr = newobjDstIdx` ran **before** the copy
  and would have stomped the fresh index before `CopyNeoCallArguments` consumed it, so
  moving it to *after* the copy is **necessary** for the fix. It is also safe for the
  non-aliasing case: when no arg aliases the dest, `CopyNeoCallArguments` does not read
  `ip->DstOffset`, so the write's position is immaterial. (See Finding F1.)

## 3. Gates re-run (independent)

Build: CLI `Debug_Neo --no-incremental -f net8.0` → 0 errors; TestCases `Debug` → 0 errors.

| Gate | Result |
|---|---|
| NeoStep smoke (`Debug_Neo`, `true NeoStep`) | **339 / 0** (matches 335 baseline + 4 probe runs) |
| `TestStaticFieldInstance` (`Debug_Neo`, filtered) | **1 / 0 PASS** — prints `testerror,error test`; reached `String.Concat` with no `InvalidCastException` (was `InvalidCastException ILTypeInstance → String` on HEAD) |
| New probes `NeoStepNewobjArgAlias_TC1/TC2` | ran within the 339/0 (TC1 + TC2 + their two getters) |
| child-11/12 canaries | intact: `NeoStepOrChain_*`, `NeoStep16_TC10`, `NeoStep20_Tr2`, `NeoStep20_Tr5`, `NeoStepBrtrueRef`, `NeoStepCeqNullSentinel` all ran green in the 339/0 |
| Stash-toggle (buggy build) | **4 / 2 FAIL** — TC1 + TC2 trip the deliberate `DivideByZeroException: Attempted to divide by zero` at `NeoStepNewobjArgAliasTest.cs:57` / `:78` |
| Stash-toggle (restored build) | **4 / 0 PASS**, 0 `DivideByZeroException` |
| Legacy-neutral (plain `Debug`, `true NeoStepNewobjArgAlias`) | **4 / 0** under `ExecuteR`; edit is inside the Neo-only newobj arm (`ExecuteNeo`) |
| `RegisterVMTest04` (Neo, spot) | still FAIL — but now `NullReferenceException`, **not** the alias `InvalidCastException`. The alias fix did its part; the residual is a separate downstream gap (see F3) |

Stash-toggle was performed by `git stash push -- ILIntepreter.Neo.cs` (tracked file
only; the untracked test file remained), rebuild, run, then `git stash pop`. The stash
was dropped; the working tree is restored to the pre-review state (Neo.cs modified
+36/-2, test untracked).

## 4. Findings

### F1 — Minor (documentation precision): the `retDstPtr` reorder is load-bearing but undocumented as a decision
design.md D3 describes `*(int*)retDstPtr = newobjDstIdx` as the "later" write that
"overwrites the rewritten source (fine — `CopyNeoCallArguments` has already consumed
it)" — accurate for the *post-fix* ordering, but it does not flag that the diff
**moves** this write from before to after the copy, and that without the move the
re-base rewrite would be immediately clobbered (since `retDstPtr == frameBase +
ip->DstOffset`, the rewritten location). The code is correct; this is a rationale-
precision nit only. Suggest a one-line note in design.md that the reorder is part of
the fix (not cosmetic).

### F2 — Minor (documentation staleness): README still states the disproven hypothesis
`rasen/changes/neo-il-static-ref-field-readback/README.md` line 3 still reads "Fix the
static-ref-field read-back" — the disproven framed hypothesis. `proposal.md` / `design.md`
/ `tasks.md` / the spec delta correctly pivot to the newobj arg-clobber. Cosmetic; the
README is the boilerplate one-liner. Optional: update it to point at the newoj root cause
for future readers.

### F3 — Minor (observed, out of scope): `RegisterVMTest04` hits a further downstream Neo gap
Under `Debug_Neo`, `RegisterVMTest04` still fails, now with `NullReferenceException`
(not the alias `InvalidCastException`, which this child eliminates). This is a separate,
broader-Neo-overhaul gap unrelated to the newoj arg alias; the child's stated scope
(newoj arm + `TestStaticFieldInstance` + the two alias probes) is met. Recording for the
LEAD's roadmap awareness; no action required of this child.

### F4 — Trivial: stash-toggle also dropped a stray `RUN_EXIT=127`
Harmless — the CLI `dotnet run --no-build` returns 127 in some paths of the run while
still printing a correct summary (`Ran 4 tests, 2 failded`); the test outcome was read
from the captured summary, not the exit code. No issue with the change; noted only for
reproducibility.

## 5. Spec axis (neo-newobj Q-NEWOBJ delta)
The `specs/neo-newobj/spec.md` MODIFIED delta extends the Q-NEWOBJ aliasing contract to
the reference-arg case (requirement + 3 scenarios: newarr-preceded int-arg, isolated
int-arg, lazy-init ref-arg alias) and mandates the re-base with the ref-map
discriminator. The diff faithfully implements the mandated detection (`dstRefOffset ∈
map.RefSrc && argIdx == newobjDstIdx`) and primitive exclusion. No spec requirement is
missing or partial; the lazy-init-ref-arg scenario is pinned by TC1+TC2.

## 6. Scope check
**CLEAN.** Stated intent (per proposal/design): fix the newoj reference-arg alias
clobber; add 2 fault-on-HEAD probes; extend the Q-NEWOBJ spec. Delivered: exactly that
— 1 runtime arm edited (Neo-gated, +34 logic lines incl. comments), 1 new test file
(2 probes + holder), 1 spec delta. No scope creep. No touching of the (correct) static
arms or the JIT/allocator.

---

## Summary
- **New root cause confirmed?** YES — newoj dest/arg reference-alias clobber; static
  read-back verified correct.
- **Fix correct (alias detection + re-base + primitive exclusion + no over-fire)?**
  YES on all four.
- **Stash-toggle?** PROVEN — buggy: TC1+TC2 `DivideByZeroException`; restored: 4/0.
- **NeoStep?** 339/0; `TestStaticFieldInstance` PASS; child-11/12 canaries intact.
- **Legacy-neutral?** YES — plain-Debug probes 4/0; Neo-gated edit.
- **Findings:** F1 (doc precision, Minor), F2 (stale README, Minor), F3 (RVMT04
  downstream NRE, Minor/out-of-scope), F4 (Trivial). No Blockers, no Majors.

**APPROVE-WITH-FINDINGS** — ship-able as-is; the findings are informational and do not
require code changes before landing.
