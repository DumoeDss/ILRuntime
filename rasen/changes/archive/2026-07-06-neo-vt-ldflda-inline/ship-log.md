# Ship Log — neo-vt-ldflda-inline (F-6, ldflda on in-frame VT)

**Date:** 2026-07-06. **Branch:** `features/object-model-overhaul`.
**Review:** APPROVED (0 open Blocker/Major; 2 Minor/Trivial doc-coverage
findings F-R1/F-R2 — both accepted-known, no rework required).
**Working tree:** UNCOMMITTED (LEAD commits after ship).

---

## What shipped — F-6 / NEO-VT-FLDADDR closed

ldflda on an in-frame IL value type whose operand slot holds FLAT BYTES (the
constrained-boxed-`this` shape produced inside an IL-struct method override
invoked via `constrained.callvirt`) was broken: the `Ldflda` arm read the
operand slot as an mStack `objectIndex`, but an in-frame VT slot holds flat
primitive bytes, so the leading int (the first field's VALUE) was read as an
mStack index -> garbage Ref Slot -> wrong result / OOB crash. There is NO
`Ldflda_Inline`. The defect was surfaced by the `neo-step17-completion` apply
phase (the IL-struct `ToString()` override probe) and recorded in
`.trae/documents/neo-deferred-items.md` (F-6 / NEO-VT-FLDADDR).

The fix is a marker-stamp + a 3-way runtime dispatch (all Neo-only; Legacy is
the REFERENCE, untouched).

### Delivered machinery

- **D1 (JIT, marker stamp).** `TypeSpecializeNeoOpcodes` `case OpCodeREnum.Ldflda:`
  stamps `op.Operand4 |= NeoLdfldaInlineMarker (0x1)` when the source `Register2`
  is an in-frame IL value type (`GetRegisterType(...) is ILType srcIl &&
  srcIl.IsValueType && !srcIl.IsEnum`) — the SAME condition that already seeds
  the dest type, so the stamp is inside the existing `if` block. The pass runs
  PRE-lowering (`Register2` is still a register index there); the marker lives
  in the standalone `Operand4` (offset 20, not aliased with any register/byte-
  offset union field). A named const `NeoLdfldaInlineMarker = 0x1` sits next to
  `CallRegisterParamCount`. `TypeSpecializeNeoOpcodes` is called under
  `#if ENABLE_NEO_MODE`, so the stamp is Neo-only.
- **D2 (runtime, 3-way dispatch).** The `case OpCodeREnum.Ldflda:` arm
  (`ILIntepreter.Neo.cs`) now branches on the marker + the leading int of the
  operand slot:
  - marker + leading-int `== -1` -> shape 1/2 frame-native Ref Slot (resolve
    `vtBase` from the Ref Slot's offset half; produce `(-1, vtBase + fieldPrimOff)`).
    Byte-identical to the existing frame-native branch.
  - marker + leading-int `!= -1` -> **shape 3 flat-bytes (the F-6 fix):** the
    operand slot holds the struct's flat primitive bytes, so the slot's own
    frame byte offset IS the struct base; produce `(-1, operandSlotOff + fieldPrimOff)`.
  - marker absent -> the existing heap-IL / CLR-object dispatch byte-identical.
- **D3 (addrAlias folding).** UNCHANGED. The marker is on `Operand4`, which the
  `addrAlias` / `liveAliasMap` machinery never reads for `Ldflda` (the
  `liveAliasMap` handler reads only `Register1`/`Register2`/`Operand2`). So the
  marker is invisible to the COEXIST gate; folding + escape behavior are
  byte-identical.

### Verification

- **NeoStep smoke:** 154/154 green (146 baseline + 8 new
  `NeoStep17_LdfldaInline_*` probes), reproduced twice.
- **Load-bearing stash-toggle (F-6 shape 3):** probe
  `NeoStep17_LdfldaInline_StructMethodFlatBytes` (an IL struct `ToString()`
  override dispatched via a generic constrained caller; body takes `ref id` via
  `ldflda` into an IL byref helper). FAILS on HEAD with `Index was out of range`
  (`ldflda` reads `id=42` as an mStack index -> `mStack[42]` OOB). PASSES with
  the fix (marker branch -> `(-1, 0)` -> reads 42 back).
- **Blast-radius sweep:** all 4 `Ldflda` operand kinds confirmed safe — heap-IL,
  CLR-object/byref-of-primitive, frame-native Ref-Slot (shape 1/2), flat-bytes
  (shape 3). The 7 regression-guard probes PASS on BOTH HEAD and HEAD+fix (the
  marker, when present for a Ref-Slot operand, reads `leadingInt == -1` and
  resolves through the offset half — byte-identical; the new `else if` fires
  ONLY for shape 3).
- **addrAlias COEXIST:** independent reuse-reconstruction (probe 4.6,
  `LdfldaReuseRead(ref x)` then re-read the field-address byrefs) GREEN — no
  silent corruption; the per-instruction `liveAliasMap` snapshot (Step-17-B1
  fix) handles the reuse correctly and the F-6 marker does not perturb it.
- **Legacy-neutral:** plain `Debug` CLI build is 0 errors (all changes Neo-only —
  the marker stamp is inside `TypeSpecializeNeoOpcodes` which is gated, the
  runtime change is in the Neo-gated `ILIntepreter.Neo.cs`, the named const is a
  harmless `public const int` never referenced in Legacy).

### Files touched (working tree UNCOMMITTED)

- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` — named const
  `NeoLdfldaInlineMarker = 0x1`; `TypeSpecializeNeoOpcodes case Ldflda:` stamp.
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` — `case Ldflda:`
  arm 3-way marker+leading-int dispatch.
- `TestCases/NeoStep17Test.cs` — 8 adversarial keeper probes
  (`NeoStep17_LdfldaInline_*`).

No shared-pass (FCP/BCP/copy-prop/RegisterCleanup) change. The marker is on the
standalone `Operand4`, which those passes do not read or clobber for `Ldflda`.

---

## Accept review findings (both accepted-known; no rework)

### F-R1 (Minor) — F-3 "key deviation" rationale does not reproduce

The implementer's apply-phase narrative claimed the design's literal reproducer
body `return "Named:" + id.ToString();` FAILS both on HEAD and with the F-6 fix
because the downstream `Int32.ToString()` (a CLR method receiving the frame-
native byref as its `this`) reads 0 — a "separate F-3 / NEO-BYREF-THIS callvirt-
on-CLR-struct deferred gap." The reviewer's independent reconstruction of the
shape-3 literal body shows the body **PASSES in BOTH configurations** (with the
fix AND with the fix stashed): for `id.ToString()` where `id` is an `int` field,
the C# compiler emits a `ldfld` (by-value load into a temp) + a value-`this`
`call Int32.ToString()`, NOT a `ldflda` + byref-`this` call — so no frame-native
byref is ever passed to the CLR method and the F-3 gap is never engaged.

**F-6 correctness is unaffected.** The shipped `ReadViaRef(ref id)` probe is a
valid reproducer that isolates the `ldflda` correctness cleanly. The deviation
note (this change's `design.md` apply-phase findings + the apply-findings block
in `planning-context.md`) is **softened** to reflect that the F-3 interaction is
NOT reproducible and the literal body was avoided out of caution / probe-
isolation preference, not because of a real F-3 gap. (No code change; this is a
documentation/narrative correction so a future Step-17 D-CONSTRAINED follow-up
does not chase a non-existent gap.)

### F-R2 (Trivial) — Probe 4.8 is a byref-of-primitive, not a CLR-object-field ldflda

Probe `NeoStep17_LdfldaInline_ClrObjectRegression` (the `ReadPointX(ref n)`
variant) actually exercises a byref of a CLR-primitive LOCAL (`n` is an `int`
local), NOT an `ldflda` on a CLR object's field. The probe comment is honest
about scoping out the Step-17 CLR-field-hash stind/ldind deferral. So the
genuine **CLR-object-field `ldflda` operand kind remains UNCOVERED** by this
change (a real CLR-object `ldflda` would carry no marker anyway and hit the
existing `else` branch, so F-6 correctness is unaffected — but the probe set
does not independently cover it).

**Deferred to follow-up:** fold into the Step-17 stind/ldind follow-up
`neo-step17-stobj-refloop` (task #18), which already owns the CLR-object field-
hash stind/ldind territory. Recorded as a small follow-up in the deferred-items
doc + planning-context.

---

## Status

**DONE.** `ship-log.md` written. F-6 -> RESOLVED; F-R1 softened (the F-3
interaction is not reproducible; F-6 correctness unaffected); F-R2 recorded
(CLR-object-field ldflda coverage gap -> `neo-step17-stobj-refloop`). Working
tree UNCOMMITTED (LEAD commits after archive).
