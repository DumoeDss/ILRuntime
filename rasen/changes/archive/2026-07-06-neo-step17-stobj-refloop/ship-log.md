# Ship Log — neo-step17-stobj-refloop (Step 17 (b) Stobj/Ldobj ref-region copy + IL-VT-with-ref-fields constrained)

**Date:** 2026-07-06. **Branch:** `features/object-model-overhaul`.
**Review:** round 0 APPROVED (0 Blocker/Major); round 1 fixed M1 (the IL-instance
branches now throw a tagged NIE on a `localInfos` scan-miss — loud, not silent
corruption) + M2 (dropped the dead `constrainedSlot0SeedRefBase` param). LEAD
non-author diff-read confirmed.
**Working tree:** UNCOMMITTED (LEAD commits after ship).

---

## What shipped — Step 17 (b) Stobj/Ldobj ref-region copy + IL-VT-with-ref-fields constrained

Two byref correctness gaps that share a single root mechanism — *recover the
byref source's ref-region mStack base* — are now closed:

- **(b) `Stobj`/`Ldobj` ref-region copy.** `Stobj`/`Ldobj` previously copied only
  `TotalPrimitiveSize` bytes, so a value type WITH reference fields copied through
  a byref (`*(S*)ptr = local;` or a `cpobj`/`ldobj` round-trip) SILENTLY
  TRUNCATED the ref half (dest kept its stale canary / stale null). The arms now
  gate the ref-region copy on `ilType.TotalReferenceCount > 0`; primitive-only VTs
  (`TotalReferenceCount == 0`) are byte-identical to the prior primitives-only
  behavior. This is pre-existing deferral from `neo-step17-completion`, NOT a
  regression.
- **IL-VT-with-ref-fields constrained.** The two Step-17-tagged NIEs that blocked
  this sub-case (the Constrained arm's IL-VT-direct-call path + the inherited-
  CLRMethod box path) are removed; the callee slot-0 ref region is now seeded
  correctly for a struct WITH reference fields.

### Delivered machinery

- **(b) Stobj/Ldobj ref-region copy (`ILIntepreter.Neo.cs`).** Gated on
  `ilType.TotalReferenceCount > 0` (primitive-only VTs byte-identical). The
  byref's source/dest ref-region mStack base is recovered via **R2 (runtime
  `localInfos` scan)** — dump-confirmed, NO JIT change. The scan locates the
  local whose frame byte `Offset == thisByteOff` (the byref carries the primitive
  byte offset but NOT the ref base). Stobj: `DstOffset`=byref address,
  `SrcOffset`=value local; Ldobj is the mirror. Two operand shapes:
  - **frame-native-direct-local:** a byte `CopyBlock` of `primSize` PLUS an
    mStack-to-mStack copy of `TotalReferenceCount` ref slots (mirrors `Move_Vt`'s
    byte + ref copy).
  - **IL-instance (`objectIndex >= 0`):** the ref half is routed through the
    ILTypeInstance's `ManagedObjects` (reusing `CopyFrameToIL`/`CopyILToFrame`,
    which already iterate `ManagedObjects`).
  - **nested-field-byref (scan miss):** a Step-17-tagged `NotImplementedException`
    is thrown (the rare edge; the `localInfos` recovery does not resolve a nested-
    field offset). After the M1 fix, the IL-instance branches ALSO throw a tagged
    NIE on a scan-miss rather than silently skipping the ref copy — loud, not
    silent corruption (consistent with the frame-native branches).
- **IL-VT-with-ref-fields constrained.** The `TotalReferenceCount > 0` NIEs are
  removed at both the Constrained direct-call path and the inherited-CLRMethod
  box path. The callee slot-0 ref slots are seeded via the **VT-THIS-ADDR copy-back
  mechanism** — a new `ExecuteNeo` hook
  (`constrainedSlot0SeedRefOffset` / `constrainedSlot0SeedSrcRefBase` /
  `constrainedSlot0SeedRefCount`; the M2 fix dropped the dead
  `constrainedSlot0SeedRefBase` param — the seed uses the callee's own
  `frameRefBase` captured INSIDE `ExecuteNeo`). The hook is seeded INSIDE
  `ExecuteNeo` right after the mStack reservation (the mStack-reservation-clobber
  gotcha — a pre-call mStack write to `mStack[Count + ...]` would be zeroed by the
  reservation's `Add(null)`, so the seed MUST run post-reservation / pre-body).
  The inherited-CLRMethod box path extends its `CopyFrameToIL` call with the
  recovered `refOffset` (R2) and `refCount = ilBoxType.TotalReferenceCount` (was
  `refOffset=0, refCount=0`).

### Earned constraint

**R2 resolves the byref to a direct local ONLY in the same frame.** A byref
PARAMETER (a `ref` param to a non-inlined helper) points at the CALLER's frame;
the helper's `localInfos` scan cannot recover that ref base. The probe set is
constructed to stay within the same-frame shape (the C# trivial inliner folds the
small byref helpers — `static void M(ref S dst, S src)`, `out`-param helpers —
into the caller where R2 resolves). If such a cross-frame byref reached the arm,
it would hit the `dstRefBase < 0` / `srcRefBase < 0` NIE (clean throw), not
silent corruption. Documented as accepted-known / deferred to a follow-up.

### Verification

- **NeoStep smoke:** 181/181 green (175 baseline + 6 new probes), reproduced.
- **NeoStep17 Legacy-neutral:** 41/41 on plain `Debug` + `useRegister=true`
  (Legacy Stobj/Ldobj/Constrained is the REFERENCE; all Neo edits are Neo-only /
  `#if ENABLE_NEO_MODE`).
- **All probes FAIL-on-HEAD -> PASS-after** (the regression guard
  `NeoStep17_StobjLdobjPrimitiveOnly_Regression` is correctly PASS-on-both — the
  `TotalReferenceCount == 0` gate skips the ref-loop, so primitive-only VTs are
  byte-identical):

| # | Probe | HEAD | After |
|---|-------|------|-------|
| 1 | `NeoStep17_StobjVtWithRefField_OverwritesStaleDestRef` | FAIL (stale canary kept) | PASS |
| 2 | `NeoStep17_LdobjVtWithRefField_ReadsSrcRefNotStaleNull` | FAIL (stale null kept) | PASS |
| 3 | `NeoStep17_NestedVtWithRefField_Stobj` (TWO ref fields, multi-slot) | FAIL (`b` canary kept) | PASS |
| 4 | `NeoStep17_ConstrainedIlVtWithRefFields_DirectCall` | FAIL (tagged NIE) | PASS |
| 5 | `NeoStep17_ConstrainedIlVtWithRefFields_InheritedClrMethod` | FAIL (tagged NIE) | PASS |
| 6 | `NeoStep17_StobjLdobjPrimitiveOnly_Regression` (guard) | PASS | PASS |

- **Two highest-blast-radius risks, both verified sound** (round-0 review):
  - **The `ExecuteNeo` signature change** (4 hook params added, defaults
    `(-1, -1, -1, 0)`): all 5 call sites verified inert by default; the seed loop
    runs ONLY at the one new constrained direct-call site.
  - **The mStack-reservation-clobber seed placement:** the seed runs AFTER the
    callee's `mStack.Add(null)` reservation (which zeroes slots) and BEFORE the
    body dispatch — placement correct (a pre-call mStack write would indeed be
    clobbered; deferring the seed to inside `ExecuteNeo` post-reservation mirrors
    the VT-THIS-ADDR copy-back precedent).

### Files touched (working tree UNCOMMITTED)

- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` —
  `Stobj` (`:3514`) + `Ldobj` (`:3544`) arms: ref-region copy loop
  (frame-native-direct-local via `localInfos` recovery + IL-instance
  `ManagedObjects` half + nested-field NIE; IL-instance NIE on scan-miss after
  M1). `Constrained` arm IL-VT-direct-call + IL-VT-inherited-CLRMethod box paths:
  removed the ref-fields NIE; seed the callee slot-0 ref region / extend
  `CopyFrameToIL` with the real ref base + `TotalReferenceCount`. `ExecuteNeo`
  signature: 3 hook params after M2 (`constrainedSlot0SeedRefBase` dropped); the
  seed loop runs inside `ExecuteNeo` right after the mStack reservation.
- `TestCases/NeoStep17Test.cs` — 6 adversarial keeper probes
  (`NeoStep17_StobjVtWithRefField_*`, `NeoStep17_LdobjVtWithRefField_*`,
  `NeoStep17_NestedVtWithRefField_*`, the two constrained probes, the primitives-
  only regression guard).

NO JIT change for the green target (R2 is a runtime `localInfos` scan). NO Legacy
change (`ILIntepreter.Register.cs` is NOT in the diff).

---

## Accept review findings (round 0 APPROVED; round 1 fixed M1 + M2)

### M1 (Minor) — IL-instance branches now throw a tagged NIE on scan-miss

The frame-native branches threw a tagged NIE on a `localInfos` scan-miss
(`dstRefBase < 0 || srcRefBase < 0`), but the IL-instance branches guarded with
`if (srcRefBase >= 0) { ... copy ... }` and SILENTLY SKIPPED the ref copy on a
miss — leaving the ILTypeInstance's `ManagedObjects` with stale/null ref slots
(silent corruption rather than a clean NIE). **Round-1 fix:** the IL-instance
branches now mirror the frame-native NIE throw on a scan-miss — loud, not silent
corruption. The asymmetry is gone; the silent-skip-as-silent-corruption class the
change otherwise avoids is closed. (The scan-miss case is exotic — the green-
target probes all resolve — so this is a consistency/correctness-polish fix, not
a reachable bug on the green target.)

### M2 (Minor) — `constrainedSlot0SeedRefBase` param dropped

The hook originally had 4 params but only 3 were load-bearing.
`constrainedSlot0SeedRefBase` was passed `0` (with a `/*unused: offset form
below*/` comment) at the sole caller, and the seed loop used `frameRefBase` (the
callee's own reservation base, captured INSIDE `ExecuteNeo`) — never
`constrainedSlot0SeedRefBase`. The param was effectively dead weight and
misleading (a future reader might think the caller controls the dest base).
**Round-1 fix:** dropped `constrainedSlot0SeedRefBase` from the signature, guard,
and call site (3 hooks suffice). Purely cosmetic — no behavior impact (the seed
worked correctly via `frameRefBase + RefOffset` before and after).

---

## Accepted-known / follow-ups (recorded)

- **Cross-frame byref parameter limitation (R2 earned constraint).** R2 resolves
  the byref to a direct local ONLY in the same frame. A byref PARAMETER (cross-
  frame) cannot be recovered -> deferred to a follow-up. Fails clean (the
  `dstRefBase < 0` / `srcRefBase < 0` NIE), not silent corruption.
- **Nested-VT-field-byref.** A `ldflda` of a nested struct field where the leaf is
  a value type with reference fields does NOT resolve to a direct local -> tagged
  NIE. Blocked UPSTREAM by the pre-existing Step-6 `Ldfld_Value` NIE and the F-6
  `Ldflda_Inline` paths — the genuine nested-field-byref shape never reaches the
  Stobj/Ldobj arm. Out of scope (independent pre-existing items).
- **(c) generic-byref / `fixed` / interface-on-VT-constrained beyond the common
  shape -> `neo-step17-generic-byref-etc`** (separate child, task #22). These
  remain Step-17-tagged NIEs (or accept-known for `fixed` if a probe shows the
  address works without GC pinning). They are independent plumbing (a generic-
  param type-token discriminator; a pinned-local flag; an interface-dispatch
  branch) that does NOT fall out of (b) and is not exercised by the smoke.

---

## Status

**DONE.** `ship-log.md` written. Step 17 (b) + IL-VT-with-ref-fields constrained
shipped (NeoStep 181/181, NeoStep17 Legacy-neutral 41/41, all probes FAIL-on-HEAD
-> PASS). Round 0 APPROVED; round 1 fixed M1 (IL-instance NIE-on-scan-miss) + M2
(dropped the dead `constrainedSlot0SeedRefBase` param). D-CONSTRAINED (b) +
IL-VT-with-ref-fields constrained -> RESOLVED; (c) -> `neo-step17-generic-byref-etc`;
cross-frame byref + nested-VT-field-byref follow-ups recorded. Working tree
UNCOMMITTED (LEAD commits after archive).
