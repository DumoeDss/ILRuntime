# Review Report — `neo-step13-area4-refandstind` (Step 13 Area 4 4c + 4d)

**Reviewer:** adversarial, non-author. **Date:** 2026-07-06. **Base:** `git diff HEAD`
(working tree UNCOMMITTED). **Verdict: APPROVE.**

## Scope check

- **Intent:** Close D-13B Area 4 remainder — 4c (CLR-method `ref`/`out` typed-ref
  bridge) + 4d (`stind`/`ldind`/`stobj`/`ldobj` on a byref to a CLR OBJECT field via
  field identity). F-7 (delegate `ref`/`out`) explicitly DEFERRED.
- **Delivered:** Exactly that. 6 library files touched (all Neo-only; Legacy
  `ExecuteR` / `AppendArgumentCode` untouched), 2 test files, 1 host-helper file.
  No scope creep. F-7 correctly deferred (different site + direction — IL→CLR vs
  CLR→IL).

## Verification performed (NOT trusting green smoke)

### Build + smoke (independently reproduced)

- `dotnet build ILRuntimeTestCLI -c Debug_Neo` → **0 errors** (only pre-existing
  version-conflict warnings).
- `dotnet build TestCases -c Debug` → **0 errors**.
- **Neo `NeoStep` smoke: 175/175, 0 failed** (reproduced).
- **Neo `NeoOptHard`: 24/24, 0 failed** (reproduced).
- **Legacy-neutral:** `dotnet build ILRuntimeTestCLI -c Debug` → **0 errors** (the
  `*Neo` variants compile out under plain Debug; verified by clean build).

### Stash-toggle (≥3 probes independently confirmed FAIL-on-HEAD → PASS-after)

Stashed the 6 library files only (kept tests + host helpers), rebuilt CLI + TestCases
clean at HEAD library, ran:

| Probe | HEAD library | Fixed tree |
|---|---|---|
| `NeoStep13_RefIntMutation` (4c ref-int) | **FAIL** (silent-wrong: reads Ref-Slot `objectIndex` half, no write-back) | **PASS** |
| `NeoStep13_OutIntAssign` (4c out) | **FAIL** (no write-back of assigned 4242) | **PASS** |
| `NeoStep17_StindClrObjectIntField` (4d CLR-object-field stind) | **FAIL** — `NotImplementedException: Step 17/13b: field/element access on a CLR object via the IL-instance path is deferred (CLR field-hash plumbing lands in Step 13b)` (the documented clean NIE) | **PASS** |

The 4d NIE message is byte-identical to the documented HEAD failure mode — confirms
the gap is genuinely pre-existing (not introduced, not masked).

## Mandatory adversarial probes — findings

### 1. 4c by-value-param regression — **CLEAN (byte-identical confirmed)**

- **Reflection path (`CLRMethod.Invoke`):** the byref detection
  (`if (ptRaw.IsByRef)`) fires ONLY for a byref-typed param; for a by-value param
  `ptRaw.IsByRef == false`, the block is skipped entirely, and the existing
  primitive/struct/ref dispatch runs unchanged (`CLRMethod.cs:441-463`). The only
  always-executed addition is the 3 scratch-array allocations + the
  init loop (`byRefSlotOff[i] = -1; byRefWriteBack[i] = false;`) — these do NOT
  touch `curPrim` or `param[i]` for by-value params.
- **Autogen path (`AppendArgumentCodeNeo`):** the ONLY addition to the by-value
  read path is `int __off_{idx} = __curPrim;` (`BindingGeneratorExtensions.cs:156-
  159`) — a capture that does NOT alter `__curPrim`. The dead `pt.IsByRef` arm was
  REMOVED; only the genuine `TypedReference` shape keeps `default+TODO`. The
  write-back epilogue (`AppendNeoWriteBackCode`) is gated on
  `p.ParameterType.IsByRef` (`:272`) — a by-value param emits zero write-back code.
- **Optimizer (`Optimizer.Neo.cs`):** the byref dest-sizing
  (`if (paramType.IsByRef) AllocateNeoCallParamSlot(elemType, ...)`) only changes
  sizing for a byref param; a by-value param hits the `else` (unchanged
  `AllocateNeoCallParamSlot(paramType, ...)`). `primByRef.Add(dstByRef)` where
  `dstByRef = dstIsVtThisSlot || dstIsByRefParam` — for a by-value param both are
  false, so `byRefSrc[i] == false` and `CopyNeoCallArguments` takes the unchanged
  `else` (verbatim byte copy). **Byte-identical.**
- Empirical: the full 175/175 NeoStep smoke (which exercises the high-CLR-call-
  density tests — Step 18 newobj, Step 19 delegate ctor, the 4b/4a direct-calls,
  all reflection + autogen CLR calls) is green.

### 2. 4c write-back semantics (ref / out / in) — **CORRECT**

- **Gate consistency:** the optimizer (`Optimizer.Neo.cs:1318`) and both readers
  use the SAME D5 gate: `byRefWriteBack = !p.IsIn || p.IsOut` (optimizer) /
  `byRefWriteBack[i] = !pinfos[i].IsIn || pinfos[i].IsOut` (reflection
  `CLRMethod.cs:447`) / `if (p.IsIn && !p.IsOut) continue` (autogen
  `AppendNeoWriteBackCode:275`). For a plain `ref` (neither flag), `!false ||
  false = true` → writes back. For `out` (`IsOut`), writes back. For `in`
  (`IsIn && !IsOut`), `!true || false = false` → NOT written back. **Correct.**
- **Probe 4c.6 (`NeoStep13_InOnlyNotWrittenBack`):** `in int x=7`, call returns
  `x+5=12`, asserts `r==12 && x==7` (local unchanged). PASS — confirms the gate.
- **Write-back source:** reads `param[i]` (reflection, the mutated box) /
  `{varName}` (autogen, the mutated local) — the RIGHT place (the CLR method's
  post-call value). Probe 4c.1 (`ref int` mutation 5→15) + 4c.3 (`ref struct`
  field deltas summing to 66) + 4c.5 (multi-byref `ref int` 1→101 + `out int`
  =999) all PASS — confirms ref AND out AND multi marshal correctly.

### 3. 4d stind/ldind blast-radius (5 operand kinds, I4 representative) — **CLEAN**

The `NeoIsClrObject` discriminator is `!(o is ILTypeInstance) && !(o is Array)`
(`ILIntepreter.Neo.cs:4107-4114`). It is inserted AFTER the `objIdx == -1`
(frame-native) check and AFTER the `mStack[objIdx] is Array` check, and BEFORE the
`GetNeoILInstance` fallback — in EVERY width arm + Stobj/Ldobj/Stind_Ref/Ldind_Ref.

| # | Operand kind | Path taken | Status |
|---|---|---|---|
| (a) | frame-native byref (`objIdx == -1`) | `*(T*)(frameBase + off)` — unchanged | **byte-identical** |
| (b) | ILTypeInstance field | `else { ins = GetNeoILInstance(...); ins.Primitives[off] }` — unchanged | **byte-identical** |
| (c) | CLR-array element (`is Array`) | `cArr.SetValue(v, off)` — unchanged | **byte-identical** |
| (d) | CLR-object field (NEW) | `NeoIsClrObject` → `NeoWriteClrObjectField` / `NeoReadClrObjectField` | **NEW, correct** |
| (e) | CLR-struct field (VT) | Stobj/Ldobj `NeoIsClrObject` branch — boxes src via `ReadNeoValueType`, writes via field accessor | **NEW, correct** |

The discriminator fires ONLY for (d)/(e) (a CLR object that is neither ILTypeInstance
nor Array). (a)/(b)/(c) hit earlier branches and are byte-identical. **Confirmed via
the additive-branch structure + the green step17-completion / array-completion / 4b
regression probes.**

### 4. 4d field-identity + width — **CORRECT**

- `NeoReadClrObjectField` / `NeoWriteClrObjectField` resolve `((CLRType)appdomain.
  GetType(obj.GetType())).GetFieldValue(hash, obj)` / `SetFieldValue` — the SAME
  hash the JIT stamps (`type.GetFieldIndex(token)`, dump-confirmed D4). No JIT
  change needed (the hash was already the offset half).
- **Field isolation:** `Area4dHolder` has `int intField` + `string refField`.
  Probe 4d.4 roundtrips `intField` (500→750) without disturbing `refField`;
  probe 4d.3 roundtrips `refField` ("updated") via stind_ref/ldind_ref. Both
  PASS → field-A's byref does NOT touch field-B.
- **Width matrix:** I1/I2/I4/I8/R4/R8/Ref + Stobj/Ldobj all have the
  `NeoIsClrObject` branch with the correct width cast per arm (e.g. I1:
  `(sbyte)NeoReadClrObjectField(...)`; I8: `(long)...`; R4: `(float)...`).
  Probe 4d.1 (I4 stind `7173`) + 4d.2 (I4 ldind `99`) PASS → I4 width correct.

### 5. D2 dead-discriminator — **FIXED (keyed on `p.IsByRef`)**

- `BindingGeneratorExtensions.cs:183-194`: the old dead `if (pt.IsByRef || pt ==
  typeof(TypedReference))` arm is replaced. The comment explicitly notes `pt` is
  de-byref'd at `:140` so `pt.IsByRef` was always false. The READ now dispatches
  on `pt` (the element type) exactly like a by-value param. The write-back
  (`AppendNeoWriteBackCode:272`) keys on `p.ParameterType.IsByRef` (the LIVE raw
  parameter type). Only `TypedReference` retains a `default+TODO` (genuinely
  unsupported, not a byref marshal).
- **Grep confirmation:** no remaining `pt.IsByRef` check that should be `p.IsByRef`
  in `BindingGeneratorExtensions.cs`. **Clean.**

### 6. Null-ref-param OOB fix — **CORRECT + GENUINE**

- `CLRMethod.cs:499-504`: `object pval = idx < 0 ? null : mStack[idx];`. A null
  reference param is encoded as mStack index `-1` (the Neo null-ref sentinel);
  `mStack[-1]` would throw OOB. This materializes null directly — matches CLR
  semantics (a null `ref`/`out`/by-value reference arg is `null`).
- **Genuine fix (not masking):** the fix is in the read path, orthogonal to the
  byref write-back. It does not alter byref behavior; it merely prevents an OOB
  when the ref is null. Surfaced by 4d probes passing a null string to
  `MakeArea4dHolder`. Correct + minimal.

### 7. Inlined-IL-method-return-move follow-up — **PRE-EXISTING + FLAGGED**

- The 4d.2 probe (`LdindClrIntFieldPeek`) is deliberately structured as
  `int v = slot; return v + 0;` to defeat the trivial-inliner's return-move
  misclassification (an int returned from an inlined IL method moved as a
  reference → `mStack[intValue]` OOB). This avoidance confirms the edge is real
  but UNRELATED to 4d (it's the inliner's return-value classification).
- **Correctly flagged** in `design.md` ("Apply resolution") + planning-context as a
  separate follow-up — NOT silently dropped, NOT introduced here (it's a
  pre-existing inliner edge; 4d only needs to drive the ldflda;ldind path, which
  the probe does without triggering the inliner edge).

### 8. Smoke reproduced + Legacy-neutral — **DONE** (see Verification section above)

### 9. Stash-toggle probes — **DONE** (3 probes, see table above)

## Additional findings

### Minor

- **M-1 — `NeoMarshalByrefFieldToSlot` Array branch throws NIE for a byref-param
  to an array element** (`ILIntepreter.Neo.cs`, the `target is Array` arm). This
  is the documented behavior (the array case is "owned by the stind/ldind array
  arm, not the field accessor"). A `ref arr[i]` passed to a CLR method would NIE
  here. This is a known limitation, not a regression (HEAD also cannot do this —
  it threw a different NIE at `GetNeoILInstance`). **Acceptable; flag for a
  future `ldelema`+byref-param follow-up.** No test covers it, but the design
  explicitly scopes it out.
- **M-2 — Reflection write-back `mStack.Add` churn for reference-type ref/out.**
  Each reference-type ref/out write-back parks a NEW mStack entry
  (`CLRMethod.cs:614-619` + autogen `:290-291`). This grows mStack monotonically
  per call. Acceptable (GC-collected; matches the existing reference-param
  pattern), but a high-density ref-out-reference loop could balloon mStack. Not a
  correctness issue; flag for a future de-dup pass.

### Trivial

- **T-1 — `isOutOnly` reference-type skip condition is convoluted**
  (`CLRMethod.cs:457`): `isOutOnly && !(pt is CLRType cct && cct.IsValueType) &&
  !(pt is ILType) && !t.IsPrimitive && !t.IsEnum`. This is "out-only AND not a
  value-type-ish thing" = "out-only reference type". It works (probe 4c.4
  `NeoStep13_OutRefTypeAssign` PASSes) but is hard to read. A future cleanup
  could simplify to `isOutOnly && !t.IsValueType` (the `t` de-byref'd CLR type
  already captures primitiveness/enum/struct). **Not blocking** — behavior is
  correct and tested.

- **T-2 — `CopyNeoCallThisBack` comment is stale.** The long comment block
  (`ILIntepreter.Neo.cs:~449-465`) still describes the pre-4c "autogen does NOT
  write back" assumption, then corrects itself mid-comment ("the autogen
  write-back for a byref param is owned by the wrapper's own epilogue"). The
  narrative is internally contradictory. **Cosmetic; not blocking.**

## Concerns explicitly checked and cleared

- **Double-write-back (autogen epilogue + `CopyNeoCallThisBack`)?** NO. The
  autogen wrapper's `__frameBase` IS `targetBase` (the callee param region), NOT
  the caller's frame — confirmed via the redirect delegate signature
  `redirectNeo(this, targetBase, mStack, ...)` (`ILIntepreter.Neo.cs:523`). The
  autogen epilogue writes the mutated local into the callee param region; then
  `CopyNeoCallThisBack` (`:1985`) reverse-copies the callee region → caller frame.
  Two distinct steps, no double-write. **Correct.**
- **Reflection write-back `slotOff` correctness?** `byRefSlotOff[i] = curPrim` is
  captured BEFORE the param read advances `curPrim` (`CLRMethod.cs:443`), so it
  points at the param's dest bytes. The write-back writes there. **Correct.**
- **`invocationParam` reuse across calls?** `Array.Clear(invocationParam, ...)`
  (`:619`) clears after each call; the per-method cached array is safe to reuse.
  The byref write-back reads `param[i]` post-`Invoke` (post-mutation). **Correct.**
- **D4 field-identity/JIT-resolvability dump gate?** RESOLVED at apply (no JIT
  change needed — the hash was already stamped). Confirmed via `CLRType.
  GetFieldValue(hash, ...)` resolution path + green 4d probes.

## Verdict: **APPROVE**

All 9 mandatory probes pass. The D2 dead-discriminator is fixed (keyed on
`p.IsByRef`). The 4c by-value path is byte-identical. The 4d blast-radius sweep
shows the `NeoIsClrObject` branch is correctly scoped (frame-native / Array /
ILTypeInstance byte-identical; CLR-object is the new additive path). Field
identity + width empirically correct. Null-ref-param fix is genuine. Inlined-
return-move follow-up is pre-existing + flagged. F-7 correctly deferred.

No Blockers. No Majors. 2 Minors (M-1, M-2 — both documented limitations / future
work, not regressions). 2 Trivials (T-1, T-2 — cosmetic).

Smoke: **Neo 175/175 + NeoOptHard 24/24**, Legacy-neutral build clean. Working
tree UNCOMMITTED.
