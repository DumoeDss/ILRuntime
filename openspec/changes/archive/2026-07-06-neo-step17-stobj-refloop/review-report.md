# Review Report -- neo-step17-stobj-refloop

**Reviewer:** adversarial, non-author. **Date:** 2026-07-06.
**Scope reviewed:** `git diff HEAD` (base commit `4d9e26f1` -- Neo step13-area4-refandstind; NOT the
`7c53df83` shown in the stale gitStatus snapshot). Working tree UNCOMMITTED throughout.
**Diff size:** `ILIntepreter.Neo.cs` +176/-26 (medium tier); `NeoStep17Test.cs` +185; planning-context +240.
**Verdict:** **APPROVE.**

The change is correct, well-scoped, and adversarially verified. No Blockers, no Majors. Two Minors
(documentation/consistency nits, no behavior change required) and one Trivial noted below. The
load-bearing risks the prompt flagged -- the `ExecuteNeo` signature change and the mStack-clobber seed
placement -- are both verified sound. All 8 mandatory probes executed; the smoke (Neo 181/181, NeoStep17
Neo 41/41, NeoStep17 Legacy 41/41) reproduced independently.

---

## Mandatory probe results

| # | Probe | HEAD (reverted) | After (restored) | Stash-toggle |
|---|-------|-----------------|------------------|--------------|
| 1 | `NeoStep17_StobjVtWithRefField_OverwritesStaleDestRef` | FAIL (stale canary kept) | PASS | independently confirmed |
| 2 | `NeoStep17_LdobjVtWithRefField_ReadsSrcRefNotStaleNull` | FAIL (stale null kept) | PASS | independently confirmed |
| 3 | `NeoStep17_NestedVtWithRefField_Stobj` (TWO ref fields, multi-slot) | FAIL (`b` canary kept) | PASS | independently confirmed |
| 4 | `NeoStep17_ConstrainedIlVtWithRefFields_DirectCall` | FAIL (tagged NIE) | PASS | independently confirmed |
| 5 | `NeoStep17_ConstrainedIlVtWithRefFields_InheritedClrMethod` | FAIL (tagged NIE) | PASS | independently confirmed |
| 6 | `NeoStep17_StobjLdobjPrimitiveOnly_Regression` (guard) | PASS | PASS | byte-identical (correct) |

All 6 probes FAIL-on-HEAD -> PASS-after except probe 6 (the regression guard), which is correctly
PASS-on-both -- the `TotalReferenceCount == 0` gate skips the ref-loop, so primitive-only VTs are
byte-identical to pre-change.

**Smoke reproduction (independent):**
- Neo full `NeoStep` smoke: **181/181, 0 failed.**
- `NeoStep17_` filter on Neo (`Debug_Neo` + `useRegister=true`): **41/41, 0 failed.**
- `NeoStep17_` filter on Legacy (plain `Debug` + `useRegister=true`, ExecuteR): **41/41, 0 failed.**
- Builds: CLI `Debug_Neo` 0 errors; TestCases `Debug --no-incremental` 0 errors.

---

## Probe 1 -- `ExecuteNeo` signature change (HIGHEST blast radius): CLEAN

The signature gained 4 optional hook params (`constrainedSlot0SeedRefBase / RefOffset / SrcRefBase /
RefCount`, defaults `-1, -1, -1, 0`). Grep over the full `ILRuntime/Runtime/Intepreter/` tree finds
**5 call sites**; all verified:

| File:line | Caller | Hook params passed | Seed runs? |
|-----------|--------|--------------------|------------|
| `ILIntepreter.Neo.cs:504` | `InvokeNeoCallTarget` (ILMethod branch) | none (5-arg form) | No -- `RefCount==0` default |
| `ILIntepreter.Neo.cs:2206` | newobj VT-THIS-ADDR copy-back | 4 vt-newobj params; seed params defaulted | No -- `RefCount==0` default |
| `ILIntepreter.Neo.cs:3966` | Constrained IL-VT direct-call (NEW) | seed params set explicitly | **Yes (the only site)** |
| `DelegateAdapter.cs:1084` | delegate invoke | none (5-arg form) | No -- `RefCount==0` default |
| `ILIntepreter.cs:112` | top-level entry | none (5-arg form) | No -- `RefCount==0` default |

The seed loop is gated on `constrainedSlot0SeedRefCount > 0 && RefBase >= 0 && SrcRefBase >= 0 &&
RefOffset >= 0` (`ILIntepreter.Neo.cs:745-746`). With defaults (`-1, -1, -1, 0`) the guard is false and
the loop is skipped -- **all 4 pre-existing callers are byte-identical**. The seed runs ONLY at the one
new constrained direct-call site. No existing call site breaks.

**Bonus check (direct `ExecuteNeo` call vs `InvokeNeoCallTarget`):** the constrained direct-call path
(line 3966) now calls `ExecuteNeo` directly instead of via `InvokeNeoCallTarget`. Verified
`InvokeNeoCallTarget`'s ILMethod branch (`:502-506`) is a thin wrapper (just `ExecuteNeo` + bool
invert, NO StackFrame push/pop, NO return-value plumbing) -- the direct call is functionally equivalent
for ILMethods plus passes the hook. No regression.

---

## Probe 2 -- mStack-reservation-clobber gotcha: seed placement CORRECT

The seed must run AFTER the callee's `mStack.Add(null)` reservation (which zeroes slots) and BEFORE the
body. Verified at `ILIntepreter.Neo.cs`:

- Lines 733-735: `frameRefBase = mStack.Count; for (...) mStack.Add(null);` -- the reservation that
  zeroes the callee's ref region.
- Lines 737-751: the seed loop, immediately AFTER the reservation, BEFORE the `StackFrame` plumbing
  (line 756) and the body dispatch.

The comment at 737-744 explicitly documents the ordering invariant. Placement is correct: a pre-call
write to `mStack[Count + ...]` would indeed be clobbered by the reservation's `Add(null)` zeroing, so
deferring the seed to inside ExecuteNeo (post-reservation) is the right fix and mirrors the VT-THIS-ADDR
copy-back precedent. The dump in the apply resolution (`slot0.RefOffset=0, mStack.Count=5,
callerFrameRefBase=0`) confirms the callee's `frameRefBase` (= `mStack.Count` at entry) is distinct from
the caller's, so the seed correctly targets `mStack[frameRefBase + constrainedSlot0SeedRefOffset + i]`
(the callee's own region).

---

## Probe 3 -- Stobj/Ldobj ref-region copy (R2): two-ref-field + stale-overwrite + NIE-clean CONFIRMED

- **Two-ref-field multi-slot (probe 3):** the `for (int i = 0; i < refCount; i++)` loop
  (`ILIntepreter.Neo.cs:3568-3569` Stobj, `:3647-3648` Ldobj) iterates the FULL `refCount`.
  `NeoStep17VtWithTwoRefs { int n; string a; string b; }` has `refCount==2`. Stash-toggle: on reverted
  HEAD the dest's `b` keeps its `"CAN_B"` canary (FAIL); after restore both `a` and `b` reflect src
  (PASS). The off-by-one guard holds -- both ref slots are copied.
- **Stale-overwrite (probes 1, 2):** the dest's stale non-null ref slot (`"CANARY"` for Stobj, stale
  null for Ldobj) is overwritten with the src's value. Verified via the FAIL-on-HEAD state (silent stale
  kept) -> PASS-after. No ref-slot leak.
- **NIE-clean (exotic shape):** when the `localInfos` scan fails to resolve the byref to a direct local
  (`dstRefBase < 0 || srcRefBase < 0`), the frame-native branch throws a Step-17-tagged
  `NotImplementedException` (`:3565-3566` Stobj, `:3644-3645` Ldobj) -- NOT silent corruption. See
  Minor M1 below for an asymmetry in the IL-instance branch.

---

## Probe 4 -- primitive-only byte-identical: CONFIRMED

The ref-loop in all four arms (Stobj frame-native `:3551`, Stobj IL-instance `:3588`, Ldobj
frame-native `:3630`, Ldobj IL-instance `:3664`) is gated on `refCount > 0`. For a primitive-only VT
(`refCount == 0`, e.g. `NeoStep17Point` of 2 ints) the gate is false and ONLY the existing
`Unsafe.CopyBlock` of `primSize` runs -- byte-identical to pre-change. Probe 6
(`NeoStep17_StobjLdobjPrimitiveOnly_Regression`) PASS on both reverted-HEAD and restored confirms this.

---

## Probe 5 -- cross-frame byref limitation: accepted-known, fails clean, documented

R2's `localInfos` scan resolves the byref to a direct local ONLY in the same frame. A byref PARAMETER
(a `ref` param to a non-inlined helper) points at the caller's frame; the helper's `localInfos` scan
cannot recover that ref base. The probe set is constructed to stay within the same-frame shape (the C#
trivial inliner folds the small byref helpers -- `static void M(ref S dst, S src)`, `out`-param helpers
-- into the caller where R2 resolves). The non-inlined cross-frame shape is documented as
accepted-known/deferred in `design.md` (D2 Rationale + Apply-phase resolution, "EARNED CONSTRAINT") and
in `planning-context.md` ("Implication: the genuine cross-frame byref-of-VT-with-refs shape ... stays
deferred"). If such a byref reached the arm, it would hit the `dstRefBase < 0` / `srcRefBase < 0` NIE
(clean throw), not silent corruption. Correctly scoped, correctly documented.

---

## Probe 6 -- nested-VT-field-byref upstream gap: pre-existing, OUT-OF-SCOPE

The implementer's claim: the genuine nested-VT-field-byref shape (`ref outer.inner` produced by
`ldflda` of a nested struct field) is blocked upstream by the pre-existing Step-6 `Ldfld_Value` NIE and
the F-6 `Ldflda_Inline` paths -- it never reaches the Stobj/Ldobj arm. This is consistent with the
known-defect register (the Step 6 / F-6 gaps are independent pre-existing items, not introduced by this
change). Probe 3 was repurposed from the nested-field shape (unreachable) to the two-ref-field
multi-slot shape (reachable, load-bearing for the off-by-one guard). The arm's internal NIE
(`dstRefBase < 0` / `srcRefBase < 0`) covers the rare shape that does reach it. Correctly out-of-scope.

---

## Findings

### M1 (Minor) -- IL-instance branch silently skips on scan-miss (asymmetric with frame-native)

**File:line:** `ILIntepreter.Neo.cs:3603` (Stobj IL-instance) and `:3672` (Ldobj IL-instance, by
inspection of the symmetric branch).

**Problem:** The frame-native branches throw a tagged NIE when the `localInfos` scan misses
(`dstRefBase < 0 || srcRefBase < 0` -> clean `NotImplementedException`). The IL-instance branches guard
with `if (srcRefBase >= 0) { ... copy ... }` and SILENTLY SKIP the ref copy when the scan misses. If the
src/dst value local does not resolve (an exotic shape -- e.g. a temp register), the ILTypeInstance's
`ManagedObjects` keeps its stale/null ref slots -- silent corruption rather than a clean NIE.

**Severity rationale (Minor, not Major):** the IL-instance branch is the `objectIndex >= 0` shape where
the byref target is the IL instance and the value operand is a frame-local register. The scan-miss case
is exotic (the green-target probes all resolve). The asymmetry is a consistency/correctness-polish
issue, not a reachable bug on the green target. Recommend mirroring the frame-native NIE throw for
consistency (silent-skip is the silent-corruption class the change otherwise avoids).

**Probe:** not directly reproducible from the green-target probes (they all resolve). A probe with a
non-local src in the IL-instance path would exercise it -- deferred since it is exotic.

**Fix (suggested, optional):** replace `if (srcRefBase >= 0) { ... }` with `if (srcRefBase < 0) throw
new NotImplementedException("Step 17: stobj of an IL-instance VT field WITH reference fields ...");`
mirroring the frame-native branch.

### M2 (Minor) -- `constrainedSlot0SeedRefBase` param is dead (always passed `0`, never read meaningfully)

**File:line:** `ILIntepreter.Neo.cs:685` (signature), `:745` (guard), `:3967` (only caller).

**Problem:** The hook has 4 params but only 3 are load-bearing. `constrainedSlot0SeedRefBase` is passed
`0` with the comment `/*unused: offset form below*/` at the sole caller, and the seed loop at `:748-750`
uses `frameRefBase` (the callee's own reservation base, captured INSIDE ExecuteNeo) -- never
`constrainedSlot0SeedRefBase`. The guard at `:745` checks `constrainedSlot0SeedRefBase >= 0` but since
the caller always passes `0` this is always true. The param is effectively dead weight.

**Severity rationale (Minor):** purely cosmetic -- no behavior impact (the seed works correctly via
`frameRefBase + RefOffset`). But it is misleading: a future reader may think the caller controls the
dest base, when actually the callee's own `frameRefBase` is used. Recommend dropping the param (3 hooks
suffice) OR documenting why it exists (the design.md "Apply-phase resolution" lists it as "+ an unused
base" -- intentional but unexplained).

**Fix (suggested, optional):** drop `constrainedSlot0SeedRefBase` from the signature, guard, and call
site; or add a one-line comment explaining the placeholder.

### T1 (Trivial) -- diagnostic `Console.WriteLine`s removed?

The design/tasks reference temporary in-arm `Console.WriteLine` dumps (`[CONST-DIRECT-DUMP]` etc.) used
for apply-phase gating. The diff does NOT contain them (good -- they were cleaned up). No action;
confirming the cleanup happened.

---

## What was verified but NOT a finding (negative results, for the record)

- **No JIT change** -- confirmed; all edits are runtime-only in `ILIntepreter.Neo.cs`. R2 is a runtime
  `localInfos` scan. The 8-byte Ref Slot wire format is untouched (R1 not needed).
- **No Legacy change** -- `ILIntepreter.Register.cs` is NOT in the diff. Legacy NeoStep17 41/41 green.
- **No regression on the shared Stobj/Ldobj arms** -- the ref-loop is gated on `refCount > 0`;
  primitives-only byte-identical (probe 6); full NeoStep smoke 181/181.
- **`ManagedObjects[i]` indexing in the IL-instance branch matches `CopyFrameToIL`** -- the existing
  helper (`:4453-4456`) also writes `dstRefs[i]` from index 0 (the `refOffset` offsets the SOURCE
  frame mStack, not the dest ManagedObjects). The new code mirrors this exactly; not a new bug.
- **The `localInfos` scan is O(n)** per Stobj/Ldobj/Constrained-hit on a ref-VT -- byref-of-VT-with-refs
  is rare (per design.md), so the linear scan is acceptable. Not flagged.

---

## Summary

The change delivers Step 17 (b) -- the Stobj/Ldobj ref-region copy loop and the IL-VT-with-ref-fields
constrained sub-case -- correctly, with R2 (runtime `localInfos` scan) as the chosen low-blast-radius
mechanism. The two flagged risks (the `ExecuteNeo` signature change and the mStack-clobber seed
placement) are both verified sound: all 5 callers are inert by default, the seed runs only at the one
constrained direct-call site, and the seed placement (post-reservation, pre-body) is correct. All 6
probes FAIL-on-HEAD -> PASS-after (probe 6 correctly PASS-on-both as the byte-identical guard), the
smoke reproduces (Neo 181/181, Legacy NeoStep17 41/41), and the accepted-known edges (cross-frame
byref, nested-VT-field-byref) are correctly scoped and documented. The two Minors are
polish/consistency nits with no behavior impact on the green target; APPROVE.
