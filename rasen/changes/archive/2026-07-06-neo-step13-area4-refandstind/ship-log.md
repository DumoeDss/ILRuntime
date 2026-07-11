# Ship Log — neo-step13-area4-refandstind (Step 13 Area 4 4c + 4d)

**Date:** 2026-07-06
**Change type:** IMPLEMENTATION — the CLR-binding / byref completion of Area 4
(4c CLR-method ref/out typed-ref bridge + 4d CLR-object stind/ldind/stobj/ldobj
via field identity).
**Shipper:** shipper role (post-review; review APPROVED — 0 Blocker, 0 Major;
4 Minor/Trivial findings accepted-known).
**Working tree:** UNCOMMITTED (LEAD commits after the shipper finishes).

---

## 1. Outcome

**Step 13 Area 4 is now FULLY DONE.** Both deferred remainder pieces shipped:

- **4c — CLR-method `ref`/`out` typed-ref bridge (was SILENT-WRONG).** A CLR
  method taking a `ref`/`out` parameter from IL silently mis-handled it on
  HEAD: the reflection fallback `CLRMethod.Invoke(byte*)` had NO `IsByRef`
  check in its param loop — it stripped the byref modifier and read the param's
  flat bytes, so a `ref int` read the Ref Slot's `objectIndex` half as the int
  value and never wrote back; the autogen `AppendArgumentCodeNeo` emitted
  `default(...)` + a `// TODO: ByRef ... DEFERRED` comment. Both readers now
  marshal byref params via the area4b deref-at-copy-site mechanism:
  - The optimizer's `CopyNeoCallArguments` derefs byref params at the copy site
    (the `PrimitiveByRefSrc` machinery from `neo-step13-area4`) so the callee
    param region receives a flat-bytes view, AND `CopyNeoCallThisBack`
    propagates the post-call bytes back to the caller's frame.
  - The reflection reader (`CLRMethod.Invoke`) reads the 8-byte Ref Slot,
    dereferences it (frame-native `objectIndex == -1` -> `*(T*)(frameBase +
    offset)`; mStack-object `objectIndex >= 0` -> the mStack object's field),
    passes the dereffed VALUE (boxed for a CLR value-type `ref`/`out`) to the
    underlying `MethodInfo.Invoke`, and writes the (possibly-mutated) value
    back through the SAME Ref Slot — gated `!IsIn || IsOut` (a `ref`/non-`out`
    writes back; an `in`-only/`readonly` does not).
  - The autogen reader (`AppendArgumentCodeNeo` + a new `AppendNeoWriteBackCode`
    epilogue) emits the same deref + write-back via generated code; the
    `MethodBindingGenerator.GenerateMethodWraperCode_Neo` wires the epilogue
    after the CLR call.
  - **The D2 dead-discriminator fix:** the old `if (pt.IsByRef || pt ==
    typeof(TypedReference))` arm was DEAD — `pt` is de-byref'd earlier in the
    function (`:140`) so `pt.IsByRef` was ALWAYS false. The READ now dispatches
    on `pt` (the element type) exactly like a by-value param; the WRITE-BACK
    (`AppendNeoWriteBackCode`) keys on `p.IsByRef`/`ptRaw.IsByRef` — the LIVE
    raw parameter type. Only `TypedReference` retains a `default+TODO`
    (genuinely unsupported, not a byref marshal).
  - `NeoCallParamMap` gains `PrimitiveByRefWriteBack` + `PrimitiveByRefElemType`
    so the optimizer + the autogen/reflection readers share the gate.

- **4d — CLR-object `stind`/`ldind`/`stobj`/`ldobj` via field identity (was
  NIE).** `ref clrObj.field` consumed by `stind_*`/`ldind_*`/`stobj`/`ldobj`
  threw a Step-17/13b NIE on HEAD. Every width arm (I1/I2/I4/I8/R4/R8/Ref) +
  `Stobj`/`Ldobj` gained a `NeoIsClrObject` branch (`!(o is ILTypeInstance) &&
  !(o is Array)`) routing to the new `NeoReadClrObjectField` /
  `NeoWriteClrObjectField`, which resolve the field via the JIT-stamped
  `FieldInfo` hash (`CLRType.GetFieldValue`/`SetFieldValue`). A shared
  `NeoMarshalByrefFieldToSlot` covers ILTypeInstance + CLR-object + Array.

- **D4 (FieldInfo at JIT) dump-confirmed RESOLVED — no JIT change needed.**
  The `Ldflda` arm already stamps the field hash into the produced Ref Slot's
  offset half (the `Ldflda` arm's `else` branch — `field.PrimitiveOffset` is
  used for IL fields, but the CLR-field path produces the `type.GetFieldIndex
  (token)` hash). The consumers resolve it via the same hash.
- **D6 (reflection `ref int` boxing) N/A** — uses the deref-at-copy-site
  mechanism, not a separate boxing path.

- **F-7 (delegate `ref`/`out`) DEFERRED — stays OPEN.** `DelegateAdapter.
  NeoInvokeSub` is the CLR->IL callback direction; 4c is the IL->CLR direction.
  Different site, different direction — the byref helper here does not apply.
  F-7 remains a follow-up (route to a future byref child).

The "no double-write-back" concern (autogen epilogue + `CopyNeoCallThisBack`)
was cleared in review: the autogen wrapper's `__frameBase` IS `targetBase`
(the callee param region), NOT the caller's frame — the autogen epilogue
writes the mutated local into the callee param region; then
`CopyNeoCallThisBack` reverse-copies the callee region -> caller frame. Two
distinct steps, no double-write.

---

## 2. Verification

Independently reproduced by the adversarial non-author review (see
`review-report.md`).

| Run | Build | Filter | Result |
|-----|-------|--------|--------|
| Neo | `Debug_Neo` + `useRegister=true` | `NeoStep` | **175/175 PASS** (161 baseline + 14 new probes) |
| Neo | `Debug_Neo` + `useRegister=true` | `NeoOptHard` | **24/24 PASS** |
| Legacy-neutral | plain `Debug` CLI build | — | **0 errors** (the `*Neo` variants compile out under plain Debug) |

**Stash-toggle (3 of the 13 functional probes independently confirmed FAIL-on-HEAD -> PASS-after)** — the rest confirmed by the implementer:

| Probe | HEAD library | Fixed tree |
|---|---|---|
| `NeoStep13_RefIntMutation` (4c ref-int) | **FAIL** (silent-wrong: reads Ref-Slot `objectIndex` half, no write-back) | **PASS** |
| `NeoStep13_OutIntAssign` (4c out) | **FAIL** (no write-back of assigned 4242) | **PASS** |
| `NeoStep17_StindClrObjectIntField` (4d CLR-object-field stind) | **FAIL** — `NotImplementedException: Step 17/13b: field/element access on a CLR object via the IL-instance path is deferred` (the documented clean NIE) | **PASS** |

All 13 functional probes FAIL-on-HEAD -> PASS-after (the 4d NIE message is
byte-identical to the documented HEAD failure mode, confirming the gap is
genuinely pre-existing, not introduced or masked).

**Mandatory adversarial probes (9) — all CLEAN/CORRECT:**
1. 4c by-value-param regression — byte-identical (the `pt.IsByRef` discriminator
   fires ONLY for a byref param; by-value params take the unchanged path).
2. 4c write-back gate (`!IsIn || IsOut`) — consistent across optimizer +
   reflection + autogen; probe `NeoStep13_InOnlyNotWrittenBack` confirms `in`
   does NOT write back.
3. 4d blast-radius (5 operand kinds) — frame-native / ILTypeInstance / Array
   byte-identical; CLR-object + CLR-struct are the new additive branches.
4. 4d field-identity + width matrix — I1/I2/I4/I8/R4/R8/Ref + Stobj/Ldobj all
   have the `NeoIsClrObject` branch with the correct width cast; field
   isolation confirmed.
5. D2 dead-discriminator — FIXED (keyed on `p.IsByRef`/`ptRaw.IsByRef`).
6. Null-ref-param OOB fix — `object pval = idx < 0 ? null : mStack[idx];`
   (mStack[-1] would OOB); genuine, orthogonal to the byref write-back.
7. Inlined-IL-method-return-move — pre-existing inliner edge, flagged as a NEW
   follow-up (NOT introduced; the 4d.2 probe avoids it via `return v + 0`).
8. Smoke reproduced + Legacy-neutral — DONE.
9. Stash-toggle probes — DONE.

---

## 3. Review outcome

**APPROVED. 0 Blocker, 0 Major. 4 findings, all accepted-known:**

### Minor (accepted-known, recorded)

- **M-1 — `ref arr[i]` to a CLR method NIEs.** `NeoMarshalByrefFieldToSlot`'s
  Array branch throws NIE for a byref-param to an array element (the array
  case is owned by the stind/ldind array arm, not the field accessor). This is
  a documented limitation, scoped out (HEAD also could not do this — it threw
  a different NIE at `GetNeoILInstance`). Route to a future
  `ldelema`+byref-param follow-up.
- **M-2 — Reflection write-back `mStack.Add` churn for reference-type
  ref/out.** Each reference-type ref/out write-back parks a NEW mStack entry
  (`CLRMethod.cs:614-619` + autogen `:290-291`). Grows mStack monotonically
  per call. Acceptable (GC-collected; matches the existing reference-param
  pattern), not a correctness issue. Flag for a future de-dup pass.

### Trivial (cosmetic, accepted)

- **T-1 — `isOutOnly` reference-type skip condition is convoluted**
  (`CLRMethod.cs:457`). Works (probe `NeoStep13_OutRefTypeAssign` PASSes) but
  hard to read. Future cleanup.
- **T-2 — `CopyNeoCallThisBack` comment is stale.** Still describes the pre-4c
  "autogen does NOT write back" assumption, then corrects itself mid-comment.
  Cosmetic.

### NEW follow-up surfaced

- **Inlined-IL-method-return-move misclassification** (an int returned from an
  inlined IL method moved as a reference -> `mStack[intValue]` OOB). This is a
  PRE-EXISTING inliner edge, surfaced by the 4d.2 probe's need to avoid it.
  The 4d.2 probe (`LdindClrIntFieldPeek`) is deliberately structured as
  `int v = slot; return v + 0;` to defeat the trivial-inliner's
  return-move misclassification. Flagged separately as **F-9 /
  NEO-INLINED-RETURN-MOVE** (route to a future inliner/optimizer follow-up).

### F-7 (delegate ref/out) — still OPEN

4c's helper is the IL->CLR direction (a CLR method receiving a byref from IL).
F-7 (`DelegateAdapter.NeoInvokeSub`) is the CLR->IL callback direction (an IL
method receiving a byref from a delegate Invoke). Different site, different
direction. F-7 remains an OPEN follow-up.

---

## 4. Spec + tracker closure

- **`openspec/specs/neo-byref/spec.md`** — 1 ADDED requirement
  (`CLR-method ref/out parameter marshaling`) + 4 MODIFIED requirements (the
  `stind`/`ldind`/`stobj`/`ldobj` dispatch, the `ldflda`/`ldarga` producers,
  the `ref`/`out` IL-parameter call ABI, and the "Deferred byref sub-cases
  throw tagged NIE" requirement — the latter two RESOLVED-marking the 4c/4d
  deferrals). Merged at archive. Requirement count: **11 before, 12 after**
  (1 net ADD; no existing requirement removed).
- **`.trae/documents/neo-deferred-items.md`** — D-13B fully RESOLVED (4b/4a
  from area4 + 4c/4d from this child — all of Area 4 done); F-9 /
  NEO-INLINED-RETURN-MOVE recorded; F-7 (delegate ref/out) noted as
  different-direction + still OPEN; M-1 / M-2 accepted-known.
- **`openspec/changes/neo-completion-portfolio/planning-context.md`** —
  follow-ups appended under `## Follow-ups discovered`.

---

## 5. Did NOT

- **Did NOT git commit/push** (per process discipline; the LEAD commits after
  the shipper finishes).
- **Did NOT touch the Legacy engine** (`ExecuteR`, `AppendArgumentCode`,
  `GenerateMethodWraperCode_Legacy` byte-identical; all changes Neo-only /
  `#if ENABLE_NEO_MODE`-gated).
- **Did NOT bundle F-7 (delegate ref/out)** — different site + direction; the
  explicit Step-13b / area4 scoping lesson (don't bundle independent
  plumbing).
- **Did NOT ship a failing probe** for the inlined-return-move edge — it is
  pre-existing and unrelated to 4d; the 4d.2 probe avoids it, and the edge is
  flagged as F-9.
