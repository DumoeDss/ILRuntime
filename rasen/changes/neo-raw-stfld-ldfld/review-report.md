# Review Report — `neo-raw-stfld-ldfld`

**Reviewer:** author != verifier gate (dispatched leaf reviewer, `rasen:review`).
**Mode:** report-only (no auto-fix, no commit, no subagents).
**Date:** 2026-07-11. **Branch:** `features/object-model-overhaul`.
**Diff:** `Optimizer.Neo.cs` (+11), `ILIntepreter.Neo.cs` (+154), `TestClass3.cs` (+4),
new `TestCases/NeoStepRawFieldTest.cs` (2 probes).

---

## Scope Check

- **Stated intent:** Execute raw `Stfld`/`Ldfld` whose field is declared on a CLR type in
  `ExecuteNeo` (the typed-splitter leaves these raw for CLR declaring types), by adding a
  runtime handler + the offset-lowering entry, plus regression probes.
- **Delivered:** exactly that. No JIT splitter change, no Legacy change, both source files
  wholly `#if ENABLE_NEO_MODE`-gated (line 1 of each). Two probes; one host CLR instance
  field added. No scope creep; no missing requirements.

**Scope: CLEAN.**

---

## Spec axis (proposal.md / tasks.md / specs/neo-value-types/spec.md)

All 6 spec scenarios verified implemented and exercised:

| Spec scenario | Covered | Evidence |
|---|---|---|
| Read CLR ref-type field (Ldfld) | yes | TC1 `NeoStepRawFld_TC1_ClrRefPrimitiveRoundTrip` (Ldfld path) |
| Write CLR ref-type field (Stfld) | yes | TC1 (Stfld path) |
| Read CLR VT struct field by value (Ldfld) | yes | TC2 `NeoStepRawFld_TC2_ClrVtStructFields` (3× Ldfld flat-bytes) |
| Write CLR VT struct field via address (Stfld) | yes | TC2 (3× Stfld frame-native byref) |
| Regression probe faults without the fix | yes | stash-toggle: 2/2 FAIL on HEAD (`Step 6` NIE) |
| No regression to typed arms | yes | NeoStep 316/0; NeoStep12 12/0, NeoStep13 36/0, NeoStep17 54/0 |

**Spec axis: PASS (0 issues). Worst issue: none.**

---

## Standards axis

### Regression-risk site: Optimizer offset-lowering edit — ADDITIVE (confirmed)

`Optimizer.Neo.cs` adds `case OpCodeREnum.Ldfld:` to the Ldfld typed-arm block
(lines 940-966) and `case OpCodeREnum.Stfld:` to the Stfld typed-arm block (970-989).

Both blocks stamp only `DstOffset`/`SrcOffset` from `Register1`/`Register2`. The Ldfld
block has exactly one conditional body statement:

```
if (op.Code == OpCodeREnum.Ldfld_Ref)
    op.Operand = localInfos[r1].RefOffset;
```

For the raw `Ldfld` opcode, `op.Code != Ldfld_Ref`, so this `Operand` stamp is skipped
(field identity lives in `OperandLong` and is correctly untouched). The Stfld block has
**no** conditionals at all. Therefore:

- Adding a `case` label into a fall-through switch block does not alter the body executed
  for the pre-existing typed arms — they run the identical lowering.
- No `Operand`/`Operand4` is stamped for the raw opcodes (field identity stays in
  `OperandLong`), so no shared stamp can mis-fire.
- No duplicate case label in the switch (build is 0-error), so the raw opcodes were
  previously un-lowered (DstOffset/SrcOffset stayed 0) and are now correctly stamped;
  no opcode is removed from any other block.

The implementer's "purely additive" claim is correct.

### ExecuteNeo handler — 3 owner-representation cases (verified)

Field identity decode `typeHash=(int)((ulong)OperandLong>>32)`,
`fieldHash=(int)OperandLong`; `ct = AppDomain.GetType(typeHash) as CLRType`;
`f = ct.GetField(fieldHash)` returns a `System.Reflection.FieldInfo` (CLRType.cs:529,
recurses into BaseType). Null-guards on both `ct` and `f` throw tagged NIEs. Owner
discrimination is `ct.TypeForCLR.IsValueType` + opcode:

1. **CLR ref-type owner (Ldfld/Stfld):** owner slot first int = `mStack` index;
   routes through Step 13 Area 4d `NeoReadClrObjectField`/`NeoWriteClrObjectField`
   (ILIntepreter.Neo.cs:5927/5939, which call `CLRType.GetFieldValue`/`SetFieldValue`).
   Null owner → `NullReferenceException`. Verified by TC1.
2. **CLR value-type Ldfld (inline flat bytes):** owner slot holds the struct's flat
   managed bytes; `ReadNeoValueType` boxes the **whole struct**, then `f.GetValue`.
   No `FieldInfo.GetFieldOffset()` used (the box-roundtrip approach, not the design's
   D3/D4 offset approach — explicitly accepted by design Non-Goals: "correctness first").
   Verified by TC2 read.
3. **CLR value-type Stfld (frame-native byref):** decodes `(objIdx, off)` from
   `(*(int*)(frameBase+ownerOff), *(int*)(frameBase+ownerOff+4))`. For `objIdx == -1`
   (frame-native): box from `frameBase+off` via `ReadNeoValueType`, `f.SetValue`,
   `WriteNeoValueType` back to `frameBase+off` (box/mutate/unbox). Verified by TC2 write
   — **the Stfld-then-Ldfld round-trip passing proves `FieldInfo.SetValue` correctly
   mutates the boxed value-type instance** (the struct field write persists).

Dest/source marshaling by `fldClrType` category (Primitive → `NeoWritePrimitiveToFrame` /
`NeoBoxPrimitiveByType`; ValueType → `WriteNeoValueType`/`ReadNeoValueType`; ref →
`mStack.Add` + index) mirrors the Ldsfld/Stsfld CLR-static arms. All helper signatures
verified present and matching.

**Deferred shapes are fail-loud tagged NIEs (not silent):** IL-instance-with-CLR-base-field
(`target is ILTypeInstance || CrossBindingAdaptorType`), array-element (`target is Array`
and the VT `mStack[objIdx] is Array` branch), and an "unrecognized CLR value-type owner
byref shape" final `else`. These throw `NotImplementedException` with distinct messages
naming the field and shape — distinct from the Step-6 default. Confirmed in the full smoke
(see below). No silent wrong behavior observed.

**Standards axis: PASS. Findings below are Minor/Trivial (accepted-known).**

---

## Independent re-verification (re-ran every gate myself)

Build: CLI `Debug_Neo --no-incremental` = 0 errors; TestCases `Debug` = 0 errors.

| Gate | Expected | Observed | Verdict |
|---|---|---|---|
| NeoStep smoke (`NeoStep` filter) | 316/0 | `Ran 316 tests, 0 failded` (both `NeoStepRawFld_TC1`/`TC2` invoked, PASS) | PASS |
| Typed arms — NeoStep12 (inline VT fields) | green | `12 tests, 0 failded` | PASS |
| Typed arms — NeoStep13 (Box/Area 4d) | green | `36 tests, 0 failded` | PASS |
| Typed arms — NeoStep17 (byref/Ref Slot) | green | `54 tests, 0 failded` | PASS |
| Stash-toggle — HEAD (2 fixes reverted, infra kept) | 2/2 FAIL (Step-6 NIE) | `Ran 2 tests, 2 failded`; both `Neo: opcode Stfld not yet implemented (Step 6)` (TC1@line37, TC2@line55) | PASS |
| Stash-toggle — restore + rebuild | 2/0 PASS | `Ran 2 tests, 0 failded` | PASS |
| Full smoke — raw Stfld Step-6 NIE | 0 | `0` (was 36) | PASS |
| Full smoke — raw Ldfld Step-6 NIE | 0 | `0` (was 80) | PASS |
| Full smoke — handler tagged-NIE deferrals | fail-loud | 10 IL-instance-CLR-base + 2 array-element + 2 unrecognized-VT-byref = **14** (matches claim) | PASS |
| Legacy-neutral — plain `Debug` NeoStep | 316 ran/17 failed; new probes pass | `Ran 316 tests, 17 failded`; 2 `NeoStepRawFld` probes NOT in failed set (17 are pre-existing NeoStep13/14 Neo-specific failures) | PASS |

Full-smoke remaining Step-6 NIEs are unrelated unimplemented opcodes (`Ldsflda` 6,
`Conv_R_Un` 4, `Switch` 2) — Step 19+ TODOs, not Stfld/Ldfld. The full run segfaulted
mid-way (the known unrelated pre-crash); counts above are from the captured 128k-line
pre-crash output, consistent with the planner's pre-crash methodology.

Working tree restored after the stash-toggle (fix diffs back: ILIntepreter.Neo.cs +154,
Optimizer.Neo.cs +11; all other changes untouched).

---

## Findings

### Minor — M1: VT-Ldfld owner-shape assumption (theoretical coverage gap)
The CLR value-type Ldfld path unconditionally treats the owner slot as inline flat bytes
(`ReadNeoValueType` from `ownerOff`). Unlike the ref-type path, there is **no fail-loud
guard** for a malformed VT owner (e.g. a byref produced by a compiler variation such as
`ldflda`+`ldobj` feeding `ldfld`). The design's Risks section explicitly flags this
("discrimination relies on IsValueType + opcode, which is sound for the C#-emitted
ldloc/ldloca patterns but must be verified for compiler-variations"). Observed C# shapes
are correct; the gap is theoretical. Accepted-known. Suggest a follow-up: add a cheap
shape sanity-check on the VT-Ldfld owner if such a compiler variation is ever observed.

### Minor — M2: IL-instance-with-CLR-base-field deferred (Legacy parity gap)
`TestCls : ClassInheritanceTest` setting a CLR-base field (10 occurrences in the full
smoke) throws a tagged NIE; Legacy **does** handle this (`ILIntepreter.Register.cs:3113-
3118`). This is a real Legacy-parity gap, explicitly accepted by the design ("5 of 27
hits — all TestCls..ctor; acceptable to defer if isolated") and spec-compliant (spec
requires "at least" the 3 shapes). Fail-loud, not silent. Tracked as a follow-up.

### Minor — M3: "unrecognized CLR value-type owner byref shape" deferral (2 occurrences)
A value-type Stfld owner byref with `objIdx >= 0` that is not an Array (e.g. a byref into
a struct-typed field inside a heap CLR object) throws a tagged NIE. Fail-loud, minority
shape (2 occurrences). Reasonable deferral; noted for the follow-up backlog.

### Trivial — T1: box-roundtrip perf for VT field access
The VT paths box the whole struct + reflection GetValue/SetValue + unbox, rather than a
direct `FieldInfo.GetFieldOffset()` flat-bytes read/write (design D3/D4's original
sketch). Acceptable per design Non-Goals ("correctness first"). Perf optimization
deferred.

No Blockers. No Majors.

---

## Verification-of-claims audit

- "Optimizer edit is purely additive" — **confirmed** by reading the block bodies
  (Ldfld_Ref-only `Operand` stamp skipped for raw `Ldfld`; Stfld block has no
  conditionals) + empirical (typed-arm filters green).
- "3 owner cases correct" — **confirmed** (helper signatures verified; TC1/TC2 pass;
  SetValue-on-boxed-struct proven by the Stfld→Ldfld round-trip).
- "Tagged-NIE deferrals, not silent" — **confirmed** (14 distinct NIEs in full smoke,
  none silent; unhandled shapes throw).
- "NeoStep 316/0, typed arms unregressed" — **confirmed** (316/0; NeoStep12/13/17 green).
- "Full smoke raw Stfld 36→0, Ldfld 80→0, 14 tagged-NIE" — **confirmed** (0/0; 14 NIEs).
- "Legacy-neutral 316 ran/17 failed, identical set, both probes pass" — **confirmed**
  (316/17; probes absent from failed set; structurally guaranteed by line-1 Neo-gating).

---

## VERDICT: **APPROVE-WITH-FINDINGS**

**Rationale:** The change is correct on every implemented owner path, the optimizer edit
is genuinely additive (the typed arms are provably unregressed, both by code read and by
the NeoStep12/13/17 + full-NeoStep gates), every spec scenario is covered, the
stash-toggle proves the probes fault-without/pass-with the fix, the full smoke shows the
raw Stfld/Ldfld Step-6 NIEs eliminated (36→0, 80→0) with only fail-loud tagged-NIE
deferrals for the explicitly-deferred shapes, and Legacy is structurally + empirically
neutral. All findings are Minor/Trivial accepted-knowns or design-acknowledged deferrals
(fail-loud, tracked as follow-ups); none is a defect in what was delivered and none
blocks shipping.
