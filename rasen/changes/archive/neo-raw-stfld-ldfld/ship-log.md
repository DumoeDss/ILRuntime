# Ship Log — neo-raw-stfld-ldfld (neo-overhaul child 4)

**Date:** 2026-07-11  **Delivery:** local commit + push. **Outcome:** SHIPPED. NeoStep **316/0**; full-smoke raw Stfld 36->0, Ldfld 80->0. Legacy-neutral.

## What shipped
- `Optimizer.Neo.cs` (+11, additive) — added `case Ldfld:` + `case Stfld:` to the offset-lowering typed-arm blocks (fall through the existing DstOffset/SrcOffset assignment; no Operand/Operand4 touch; the Ldfld_Ref-only `Operand=RefOffset` stamp is skipped since raw `Ldfld != Ldfld_Ref`). Typed arms unaffected.
- `ILIntepreter.Neo.cs` (+154, Neo-gated) — raw `case Stfld:`/`case Ldfld:` handlers (before the typed arms). Decode `typeHash/fieldHash` from OperandLong, resolve CLRType+FieldInfo. 3 owner-representation cases (discriminate on `ct.TypeForCLR.IsValueType` + opcode):
  1. CLR ref-type owner -> mStack index -> `NeoReadClrObjectField`/`NeoWriteClrObjectField` (Area 4d).
  2. CLR value-type Ldfld -> inline flat bytes -> box whole struct (`ReadNeoValueType`) + `f.GetValue` -> marshal to dest.
  3. CLR value-type Stfld -> frame-native byref `(-1, structBaseOff)` -> box from byref target + `f.SetValue` + `WriteNeoValueType` back (box/mutate/unbox).
  - IL-instance-with-CLR-base-field + array-element owners -> tagged NIEs (deferred, fail-loud). Slot<->object marshalling reuses the child-3 Stsfld/Ldsfld pattern.
- `TestClass3.cs` (+NeoClrInstProbe) + `TestCases/NeoStepRawFieldTest.cs` (TC1 CLR ref owner, TC2 CLR VT owner).

## Root cause (planner, instrumented full-smoke)
Raw Stfld/Ldfld escape the typed-splitter when the field's DECLARING type is a CLRType (the JIT `else` branch leaves the raw opcode — CORRECT, identical to Legacy, no JIT change; CLR fields can't lower into ILType-field typed arms). The gaps were: (1) no ExecuteNeo case; (2) no offset-lowering entry.

## Evidence
- **Stash-toggle:** revert the 2 fixes -> 2/2 probes FAIL (`Neo: opcode Stfld not yet implemented (Step 6)`); restore -> PASS.
- **NeoStep smoke:** 316/0 (314 + 2). Typed arms UNREGRESSED — NeoStep12 12/0, NeoStep13 36/0, NeoStep17 54/0 (the optimizer-additive invariant holds).
- **Full smoke:** raw Stfld 36->0, Ldfld 80->0. 14 tagged-NIE deferrals (10 IL-instance-CLR-base + 2 array-element + 2 unrecognized-VT-byref). Run segfaults mid-way (pre-existing unrelated).
- **Legacy-neutral:** plain Debug NeoStep 316 ran/17 failed == baseline 314 ran/17 failed (identical set; new probes pass under Legacy).

## Review verdict
APPROVE-WITH-FINDINGS (reviewer != implementer). 0 Blocker/Major. Optimizer-additive + 3-owner-case + typed-arm-unregressed all independently confirmed. M1 (VT-Ldfld owner-shape theoretical gap) accepted-known; M2 (IL-instance-with-CLR-base-field, 10 hits, Legacy handles it) -> follow-up child; M3 (unrecognized-VT-byref, 2 hits) deferred fail-loud; T1 (box-roundtrip perf) accepted.

## Deferred / surfaced
- **`neo-il-instance-clr-base-field` (M2):** an IL instance accessing a field declared on a CLR base type (TestCls..ctor, 10 full-smoke hits). Legacy handles it (Register.cs:3113); Neo defers via tagged NIE. Real parity gap -> follow-up child.
- Durable: raw Stfld/Ldfld-on-CLR-declaring-type owner map (ref->Area 4d; VT-Ldfld->ReadNeoValueType+GetValue; VT-Stfld->frame-native-byref box/SetValue/WriteNeoValueType). VT-Stfld correctness hinges on FieldInfo.SetValue mutating the boxed VT in place (round-trip-proven).
