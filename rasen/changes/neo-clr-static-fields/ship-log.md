# Ship Log — neo-clr-static-fields (neo-overhaul child 3)

**Date:** 2026-07-11  **Pipeline:** auto-decompose -> small-feature  **Tier:** A
**Delivery:** local commit + push. **Outcome:** SHIPPED. NeoStep **314/0**. Full smoke COMPLETES (848/249), CLR-static NIE 88->0. Legacy-neutral.

## What shipped
- `ILIntepreter.Neo.cs` (Neo-gated, +145) — the Stsfld (:3867) + Ldsfld (:3910) **CLR-static** branches (replacing the "IL-only capstone" throws): cast `declType`->`CLRType`; `sIdx=(int)ip->OperandLong`; `ct.GetField/GetFieldValue/SetStaticFieldValue` (mirror Legacy ExecuteR). Per-category read/write: primitive `NeoBoxPrimitiveByType`/`NeoWritePrimitiveToFrame`; VT `ReadNeoValueType`/`WriteNeoValueType` (guarded); ref mStack index / `mStack.Add` temp-ref (no dstRefOffset). CrossBindingAdaptorType unwrap on read.
- `NeoClrVtStaticFieldIsUnsafe` guard — a CLR VT static with a registered `ValueTypeBinder`, ref fields (Step-13b gap), or eval-slot overflow throws a tagged NIE (flat marshal would AV-exit). Simple blittable structs (IntPtr) pass.
- `TestClass3.cs` (+8) — `NeoClrStaticProbe` infra; `TestCLRBinding.HostReadNeoClrStaticProbe` host helper.
- `TestCases/NeoStepClrStaticFieldTest.cs` — 3 probes (primitive round-trip, `string.Empty` ref, `IntPtr.Zero` VT).

## Recovery (the first implementer died mid-work via socket-close)
The dead worker's partial code had TWO real bugs the recovery implementer found + fixed:
- **Bug-fix 1 (silent wrong-slot):** Stsfld/Ldsfld have NO `LowerNeoOffsets` case -> `ip->DstOffset` is the raw register INDEX, not a byte offset. The dead code read/wrote `frameBase + ip->DstOffset` directly. Fixed to resolve `localInfos[ip->DstOffset].Offset` at runtime (mirrors `LowerR1`). Reviewer empirically reproduced (TC1 -> DivideByZeroException with the fix off).
- **Bug-fix 2 (VT-binder AV crash):** the VT sub-branches would AccessViolation-exit for a CLR VT static with a registered binder -> the `NeoClrVtStaticFieldIsUnsafe` guard converts to a tagged NIE.
- **Scoping decision:** a GLOBAL Stsfld/Ldsfld lowering was NOT added — it would unmask the brtrue-on-reference gap (F4) and broke `NeoStep20_Tr2/Tr5`. CLR-static-execution-side only.

## Evidence
- **Stash-toggle:** branches reverted -> 3/3 probes FAIL (the "CLR static field not implemented" NIE); bug-fix-1-only-off -> TC1 DivideByZeroException (wrong-slot, reproduced); restored -> 3/3 PASS.
- **NeoStep smoke:** 314/0 (311 + 3); `NeoStep20_Tr2`/`Tr5` pass (brtrue-gap scoping didn't regress).
- **Full smoke:** COMPLETES (848 ran, 249 failed — the design's "Activator crash" premise was WRONG; HEAD completes too); CLR-static NIE 88->0; net failures 252->249 (the 3 probes flipping).
- **Legacy-neutral:** `ILIntepreter.Neo.cs` wholly `#if ENABLE_NEO_MODE`; `Optimizer.Neo.cs` unchanged; TestClass3 inert infra.

## Review verdict
APPROVE-WITH-FINDINGS (reviewer != implementer). 0 Blocker/Major. F1 (TC2/TC3 weak probes, TC1 covers solidly) accepted-known; F3 (IL-static Stsfld/Ldsfld ALSO read raw DstOffset — pre-existing, masked) + F4 (brtrue/brfalse-on-reference null misclassifies as truthy) -> paired follow-up child (they mask each other, fix together); F5/F6 trivial accepted-known.

## Deferred / surfaced
- **`neo-brtrue-on-reference` (F3+F4 paired):** `brtrue` tests low-int32 `!= 0` but a null reference is a non-zero mStack index (IL-static) / -1 (CLR-static Ldsfld) -> the Roslyn delegate-cache `ldsfld cache; brtrue` pattern misclassifies null as truthy. Currently masked by F3 (IL-static raw DstOffset) + `ceq` lowering. Fix F3 (IL-static runtime offset) + F4 (brtrue null-sentinel) together. CORRECTNESS, latent.
- Durable: Stsfld/Ldsfld are NOT lowered (raw DstOffset = register index); CLRType static API = `GetField/GetFieldValue/SetStaticFieldValue` keyed by FieldInfo hash.
