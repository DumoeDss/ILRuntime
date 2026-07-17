# Review Report — neo-raw-stfld-array-element

**Reviewer:** author != verifier gate (independent re-verification, dispatched report-only).
**Change:** child 19 of `neo-overhaul`; closes the child-4 deferred "array-element owner of raw `Stfld`" shape.
**Date:** 2026-07-12.
**Branch:** `features/object-model-overhaul`.

## Scope Check

**Scope Check: CLEAN.** Intent: replace the two tagged `NotImplementedException`s in the raw
`Stfld` handler's array-element-owner branches with a box/mutate/unbox, + 2 NeoStep probes.
Delivered: exactly that — 2 handler branches in `ILIntepreter.Neo.cs`, a host read-back helper
(`TestClass3.cs`), a host probe struct (`TestVector3.cs`), and a 2-probe test file
(`TestCases/NeoStepRawStfldArrElemTest.cs`). No JIT/optimizer/object-model/CLR-binding change, no
new opcode, no new helper (inline). `Ldfld` array-element is explicitly deferred with a sound
rationale (untyped Neo frame). No scope creep.

**Diff files (4):** `ILIntepreter.Neo.cs` (+36/-9), `TestClass3.cs` (+10), `TestVector3.cs` (+14),
`TestCases/NeoStepRawStfldArrElemTest.cs` (new, untracked, 66 lines).

## Findings

| # | Severity | Location | Finding |
|---|----------|----------|---------|
| F1 | **Trivial** | `ILIntepreter.Neo.cs:4082` (comment) | Stale line-number reference: comment cites `ILIntepreter.Neo.cs:5747-5748` for the `ldelema` CLR-array byref encoding, but the encoding actually lives at `:5774-5775` (`*(int*)(DstOffset+0)=arrIdx; *(int*)(DstOffset+4)=elementIdx`). The proposal/design/planner-findings also cite `:5729-5749` / `:5747-5748` for the same lines — all drifted ~25-45 lines as the file grew. Non-load-bearing (comment + doc accuracy only); the described semantics are correct. No action required to ship. |

No Blocker, Major, or Minor findings. The two-branch correctness, the write-back persistence, and
all gates are independently verified below.

## 2-branches correctness (independent re-verification)

**Branch 1 — CLR value-type declaring (`ILIntepreter.Neo.cs:4078-4091`, the reachable ~4 hits).**
Verified each piece against the surrounding handler and the sibling consumers:

- **`value` marshalling reuses child-4's pattern correctly.** `value` is boxed by field category at
  `:4044-4057` (`IsPrimitive` -> `NeoBoxPrimitiveByType`; `IsValueType` -> `ReadNeoValueType`; else
  `mStack[srcRefIdx]`/null) — the SAME `value` local child-4's frame-byref branch at `:4070-4077`
  consumes. No re-marshalling, no double-box. Correct.
- **`f` is the resolved FieldInfo** (`ct.GetField(fieldHash)` at `:4036`). Correct.
- **`ownerOff = ip->DstOffset`** (`:4040`); **`objIdx = *(int*)(ownerOff)`**, **`off = *(int*)(ownerOff+4)`**
  (`:4068-4069`). The decode matches the `stind_*`/`ldind_*` consumer convention exactly — e.g.
  `Stind_I1` at `:5199-5203` decodes `objIdx = *(int*)(DstOffset+0)`, `off = *(int*)(DstOffset+4)`,
  and routes `mStack[objIdx] is Array` -> `cArr.SetValue(v, off)`. Same `(objIdx, off)` decode; same
  `off`-is-element-index semantics.
- **`off` is the ELEMENT INDEX, not a byte offset.** Verified at the producer: `Ldelema` CLR-array
  arm (`:5756-5776`) stamps `*(int*)(DstOffset+0)=arrIdx`, `*(int*)(DstOffset+4)=elementIdx`, after
  validating the element is a value type (`:5771-5773`). So `cArr.GetValue(off)` / `cArr.SetValue(.., off)`
  index the correct element. The discriminator `objIdx >= 0 && mStack[objIdx] is Array cArr` is the
  same one `stind`/`ldind` use — and unlike `Ldfld`, it is UNAMBIGUOUS here because a `Stfld`
  value-type owner is always a byref (you cannot write a field without the address), so design D2/D3's
  false-positive concern does not apply.
- **Box/mutate/unbox ordering** — `cArr.GetValue(off)` -> `f.SetValue(boxedElem, value)` ->
  `cArr.SetValue(boxedElem, off)` — is the right order and mirrors child-4's frame-byref
  (`ReadNeoValueType` -> `f.SetValue` -> `WriteNeoValueType`) with `Array.GetValue`/`SetValue` in
  place of the frame byte read/write.

**Branch 2 — CLR ref-type declaring (`:4118-4130`, defensive/unreachable).** Re-decodes
`elementIdx2 = *(int*)(ownerOff+4)` (correct — `off` is not decoded in the ref-type arm, only
`objIdx` at `:4102`, so this local decode is necessary, not redundant) and runs the same
box/mutate/unbox. **Unreachability verified:** `Ldelema` on a ref-type-element array throws a
tagged NIE at `:5771-5773` (`if elemClrType == null || !elemClrType.IsValueType throw`), so the
owner of a ref-type-declaring `stfld` can never be an Array via `ldelema`; no other path produces
an array-element owner. The branch is fail-soft symmetry. IF it were ever hit, the code is still
correct (ref-type array element: `GetValue` returns the live object, `f.SetValue` mutates it,
`SetValue` writes the same ref back — harmless). Correct either way.

**Legacy parity:** Legacy's raw `Stfld` writeback for `ObjectTypes.ArrayReference`
(`ILIntepreter.Register.cs:3154-3158`) is `var arr = mStack[objRef->Value] as Array; int idx =
objRef->ValueLow; arr.SetValue(obj, idx)` — box/mutate/writeback BY ELEMENT INDEX. The Neo final
writeback `cArr.SetValue(boxedElem, off)` is byte-identical in addressing (`off` == Legacy's
`idx` = `ValueLow` = element index; `cArr` == Legacy's `arr`). Confirmed.

## Write-back persistence (adversarial)

The load-bearing concern: for a CLR **struct** element, `Array.GetValue(elementIdx)` returns a
**boxed copy**; the mutation must be written back or it is lost.

- `f.SetValue(boxedElem, value)` where `boxedElem` is a boxed value type **does mutate the boxed
  instance in place** (documented .NET reflection semantics — `FieldInfo.SetValue` unboxes-to-
  location, writes the field into the boxed storage; the box IS the mutated object). This is the
  SAME mechanism child-4 relies on at `:4070-4077` (`object boxedOwner = ReadNeoValueType(...)`;
  `f.SetValue(boxedOwner, value)`; `WriteNeoValueType(boxedOwner, ...)`) — an established, passing
  pattern.
- `cArr.SetValue(boxedElem, off)` then writes the mutated boxed struct back into the array slot
  (`Array.SetValue` unboxes the value type into the array backing store). The mutation persists.
- **Empirically confirmed by the probes:** TC1 (`arr[1].A=4242; arr[1].B=17;` -> host read-back sum
  == 4259) and TC2 (two distinct indices, four-way sum == 100) both PASS (see Stash-toggle + smoke
  below). The read-back is host-side (`TestCLRBinding.NeoArrElemFieldSum` reads `arr[i].A + arr[i].B`
  in CLR), so a non-persisting write-back would fail the sum assertion -> `1/0`. They pass, so the
  round-trip persists. Confirmed.

## Gate evidence (all re-run this review)

- **Build CLI `Debug_Neo --no-incremental`:** 0 errors (270 warnings, all pre-existing).
- **Build `TestCases -c Debug`:** 0 errors (87 warnings, all pre-existing).
- **NeoStep smoke** (`... true NeoStep`): **`Ran 354 tests, 0 failded`** (352 baseline + 2 new
  probes). Zero failures.
- **Targeted probes** (`... true NeoStepRawStfldArrElem`): **`Ran 2 tests, 0 failded`** — both
  `NeoStepRawStfldArrElem_TC1` and `_TC2` PASS with the fix.
- **Stash-toggle (FAIL-on-HEAD):** backed up the fixed file, reverted
  `ILIntepreter.Neo.cs` to HEAD (`git checkout HEAD -- <file>`; confirmed 2 NIE strings restored),
  rebuilt CLI (`Debug_Neo --no-incremental`, 0 errors), ran the targeted filter: **`Ran 2 tests, 2
  failded`** — BOTH TCs faulted on the tagged NIE:
  `Neo raw Stfld: array-element field write is deferred (stfld on a CLR array element; follow-up).
  Field A on ILRuntimeTest.TestFramework.NeoArrElemIntProbe`. Restored the fix from backup (verified
  0 NIEs + both `GetValue` lines present + diff back to +36/-9), removed the backup. Sound fault
  probe confirmed.
- **Full-smoke spot check** (no filter, captured to file, `grep -a`): "array-element field write is
  deferred" count = **0** (was ~4). Run produced a final tally (`Ran 888 tests, 201 failded` — the
  201 are the broader Step-19+ unimplemented-opcode tagged NIEs, unrelated to this change).
- **Legacy-neutral** (plain-`Debug` CLI + `useRegister=true`): NeoStep filter = **`Ran 354 tests, 18
  failded`** (matches the documented baseline 17 + child-18's Neo-scoped TC4); the 2 new probes =
  **`Ran 2 tests, 0 failded`** under Legacy. The Neo edits are all under `#if ENABLE_NEO_MODE`
  (excluded from the plain-Debug build); the host struct/helper/test are additive. No NEW Legacy
  failure.

## Spec axis

The `neo-value-types` ADDED requirement (4 scenarios) is satisfied:
1. "Write a CLR-struct field through an array-element owner" -> TC1 (host read-back 4259).
2. "Element index decoded correctly across multiple indices" -> TC2 (indices 0 and 5, sum 100).
3. "Regression probe faults without the fix" -> stash-toggle (2/2 FAIL on HEAD NIE).
4. "No regression to typed arms / sibling raw-owner arms" -> NeoStep 354/0.

## Verdict

**APPROVE-WITH-FINDINGS** — ship-able as-is.

The fix is correct, minimal (~10-15 LOC across 2 sites), byte-identical in semantics to Legacy's
`ObjectTypes.ArrayReference` writeback, and a faithful mirror of child-4's established
box/mutate/unbox pattern (with `Array.GetValue`/`SetValue` substituted for the frame byte
read/write). Both branches are independently verified correct; the write-back persists
(verified both by the .NET reflection-mutates-boxed-VT semantics and empirically by the host
read-back probes). All gates pass: NeoStep 354/0, targeted 2/0, stash-toggle 2/2 FAIL-on-HEAD ->
PASS, full-smoke array-element NIE 4->0, Legacy-neutral (18-failure baseline unchanged, new probes
pass). The single finding (F1) is a **Trivial** stale comment line-number reference and requires no
action to ship; the line refs in the proposal/design/planner-findings have the same drift.
