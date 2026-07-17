# Ship Log — neo-raw-stfld-clr-object-vt-field (child 27)

**Change:** raw `Stfld` on a CLR-struct field of a CLR reference object (`obj.Struct.value = 111`) — add a third
`objIdx >= 0` branch (box/mutate/unbox one level up via the Area-4d helpers). Triage batch-2 R3; COMPLETES the
batch-2 sweep (R1-B=child 26, R2=child 25, R3=this).
**Capability:** `neo-value-types` (ADDED requirement).
**Pipeline:** small-feature (triage-re-audit -> diagnose -> propose+apply -> verify -> review -> ship).
**Date:** 2026-07-13. **Branch:** `features/object-model-overhaul`. **Base HEAD:** `226ee5c4`.

## Diagnosis (the crux, pinned by JIT dump)
`obj.Struct.value = 111` (CIL `ldflda Struct(on obj); stfld value`) hit the raw-Stfld VT-owner `else` NIE
("unrecognized CLR value-type owner byref shape") at `ILIntepreter.Neo.cs:~4193`. The `ldflda Struct(on a CLR
object)` runtime `else` branch (`:1941-1956`, fires because `objIdx >= 0`) produces a byref
`(objIdx_of_containing_CLR_object, structFieldHash)` where the offset half is the **Struct field's
`FieldInfo.GetHashCode()`** (NOT a byte offset). The Area-4d accessors `NeoReadClrObjectField`/
`NeoWriteClrObjectField` resolve that hash to the struct FieldInfo via the containing CLRType's `fieldInfoCache`
(generate-side `AppDomain.GetFieldOffset:2277`→`CLRType.GetFieldIndex:661`→`fieldMapping:614` and resolve-side
`CLRType.InitializeFields:615` use the IDENTICAL key on the identical FieldInfo). So the hash-vs-offset distinction
is moot — resolve the hash to a FieldInfo.

## What shipped (runtime-only, ~15 lines, NO JIT marker, Neo-gated, Legacy-neutral)
- **`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`** (+34): a new `else if (objIdx >= 0)` branch in
  the raw-Stfld CLR-VT-owner arm — null→NRE, IL-instance→tagged deferred NIE (the F-10 ManagedObjects-storage
  sibling, honestly deferred), else box/mutate/unbox: `NeoReadClrObjectField(target, off=structFieldHash)` reads
  the WHOLE struct field → box → `f.SetValue(boxedStruct, value)` mutates the inner field →
  `NeoWriteClrObjectField(target, off, boxedStruct)` writes the whole mutated struct back. The SAME boxedStruct
  reference flows read→mutate→write-back. Runtime detection is SAFE: a VT-owner Stfld is ALWAYS a byref (the
  write/read asymmetry child-24/26 identified — only the raw-Ldfld READ side needs a marker).
- **`ILRuntimeTestBase/TestFramework/TestVector3.cs`** (+25): `NeoClrObjVtFieldProbe` (3 int fields) +
  `NeoClrObjVtFieldOwner` (CLR class with struct field `S`).
- **`ILRuntimeTestBase/TestFramework/TestClass3.cs`** (+11, in `TestCLRBinding`): `NeoClrObjVtFieldProbeSum` host helper.
- **`TestCases/NeoStepRawStfldClrObjVtFieldTest.cs`** (new): TC1 single-write (111), TC2 three-write-preservation (666).

## Verification
- **NeoStep smoke: 375/0** (373 baseline + 2 probes), no regressions. Sibling families green: child-4/9/19/24/25/26.
- **`UnitTest_Struct2` PROGRESSES** (was NIE at line 103 `obj.Struct.value=111`; now passes 103, fails at line 104
  `obj.Struct.value+=111` — the nested-ldflda `+=` gap, out of scope, distinct from raw Stfld).
- **Stash-toggle (airtight):** stash `ILIntepreter.Neo.cs` (probe + helpers kept) → 2/2 FAULT (exact R3 NIE
  "unrecognized CLR value-type owner byref shape (objIdx=3). Field a on NeoClrObjVtFieldProbe"); pop → 375/0.
- **Field-preservation PROVEN:** TC2 does 3 separate writes (111/222/333) and host read-back sum = 666 (a
  zeroing/recreate bug would yield 333, not 666 — proves each write preserves the others).
- **Legacy-neutral:** plain `Debug`+`useRegister=true`+NeoStep = 375 ran/18 failed (pre-existing Legacy set; both
  probes PASS under Legacy). 100% `#if ENABLE_NEO_MODE`.

## Review
**APPROVE** (reviewer != implementer; 0 Blocker/Major). Diagnosis-correctness CONFIRMED (hash genuinely resolvable;
box/mutate/unbox sound; same boxedStruct flows read→mutate→write-back; TC2=666 field-preservation) +
runtime-detection-safe (VT-owner Stfld always byref) + regression-clean all confirmed.
- Minor M1 (pre-existing, shared Area-4d foundation): `fieldInfoCache` keyed by `FieldInfo.GetHashCode()` — a
  theoretical collision would clobber resolution; `RuntimeFieldInfo.GetHashCode()` is handle-derived and unique per
  field within a type in practice. No action.
- Minor M2 (forward-looking, for the F-10 sibling): the branch assumes `off` is a field hash (true for a CLR owner;
  for an IL owner `off` would be a Primitives byte offset) — the deferred IL-instance NIE guard protects this today;
  when un-deferred, the Area-4d helpers can't be reused directly for the IL sub-case.
- Trivial T1: a cross-arm comment refers to a prior child.

## Delivery
**Mode:** local commit (portfolio per-child delivery; push per parent directive). Committed with the
portfolio-run.json + planning-context.md record updates. No PR.

## Durable findings (for future planning)
1. **The `ldflda` heap-CLR-object `else` branch produces a byref whose offset half is the struct field's
   `FieldInfo.GetHashCode()`** — resolvable via the Area-4d `NeoReadClrObjectField`/`NeoWriteClrObjectField`
   (same key on the same FieldInfo at generate and resolve time). For a CLR-object-field owner, the fix is a
   one-level-up box/mutate/unbox through reflection, NOT a byte-offset dereference.
2. **A VT-owner Stfld is ALWAYS a byref** → runtime content detection is unambiguous and safe — no JIT marker
   (the write/read asymmetry: only the raw-Ldfld READ side, where the owner can be flat bytes OR a byref, needs a
   marker).
3. **`obj.Struct.value op= k` (compound assignment) lowers to `ldflda Struct; ldflda value; ldind; op; stind`**
   (NOT raw Stfld) — a distinct nested-ldflda-on-byref gap, where `UnitTest_Struct2` now fails (line 104).

## Surfaced siblings (out of scope)
1. raw `Ldfld` READ sibling (`x = obj.Struct.value`) — silent flat-bytes-reinterpret corruption; needs a JIT marker
   (child-24 lineage).
2. `+=` nested-ldflda path (`obj.Struct.value += 111`) — the inner ldflda-on-byref gap.
3. IL-instance CLR-struct-field owner (F-10 ManagedObjects storage) — deferred tagged NIE.
