# Ship Log — neo-misc-opcodes (neo-overhaul child 7)

**Date:** 2026-07-11  **Delivery:** local commit + push. **Outcome:** SHIPPED. NeoStep **324/0**; full-smoke per-opcode NIE all ->0. Legacy-neutral.

## What shipped (4 small opcodes, `ILIntepreter.Neo.cs`, Neo-gated)
1. **Conv_R_Un** (beside Conv_R8): width dispatch on `(NeoPrimitiveTypeTag)ip->Operand2` — I8/U8->`(double)ReadConvU8`, R4->`(double)*(float*)`, R8->`*(double*)`, else I4/U4->`(double)ReadConvU4`; writes `*(double*)DstOffset`.
2. **Switch** (after Brfalse): index via `(idx<localInfos.Length)?localInfos[idx].Offset:idx` (Switch NOT lowered -> raw Register1); `method.JumpTablesRegister[ip->Operand]`; in-range->`ip=ptr+table[idx];continue;` else fall through. Byte-for-byte Legacy.
3. **Unbox-of-enum** (Unbox CLR-primitive branch): `if (obj is ILEnumTypeInstance)` -> CopyBlock `Primitives` -> DstOffset. Value lives in Primitives.
4. **Ldsflda** (before Ldsfld): typeHash/fieldHash from OperandLong; IL-static -> `mStack.Add(ilt.StaticInstance)` + byref `(idx, PrimitiveOffset|ReferenceOffset)` (zero consumer change -- Stind/Ldind + CopyNeoCallArguments dispatch `mStack[objIdx] is ILTypeInstance`; `ILTypeStaticInstance : ILTypeInstance`); CLR-static -> tagged NIE deferred.
- `TestCases/NeoMiscOpTest.cs` — 4 probes (Conv_R_Un, Switch, Unbox-enum, Ldsflda).

## Diagnose-first resolutions
- Switch NOT in LowerNeoOffsets -> DstOffset raw Register1 -> resolve via localInfos (the defensive pattern).
- Unbox-enum value in Primitives (ILType-enum Unbox arm parity).
- Ldsflda IL/CLR split: IL -> materialize StaticInstance byref (zero consumer change confirmed); CLR -> deferred tagged NIE.

## Evidence
- **Stash-toggle:** 4 source arms stashed -> 4/4 FAIL (Conv_R_Un/Switch/Ldsflda Step-6 NIEs; Unbox-enum the ILEnumTypeInstance message); restored -> 4/0 PASS.
- **NeoStep smoke:** 324/0 (320 + 4).
- **Full smoke:** Conv_R_Un 4->0, Switch 2->0, Ldsflda-Step6 5->0, Unbox-enum 2->0 (2 Ldsflda-CLR-static intentionally deferred tagged).
- **Legacy-neutral:** plain Debug NeoStep 324 ran/17 failed == baseline; all arms `#if ENABLE_NEO_MODE`.

## Review verdict
APPROVE-WITH-FINDINGS (reviewer != implementer). 0 Blocker/Major. All 4 opcodes correct; Ldsflda zero-consumer-change confirmed. F1 (Conv_R_Un Neo writes exact double vs Legacy float32-round for >2^24 uints -- Neo MORE accurate, ECMA-compliant, double dest forced) accepted-known; F2 (Ldsflda ref-typed IL-static probe coverage) optional follow-up.

## Note (pre-existing gap reaffirmed)
IL-static `Ldsfld`/`Stsfld` arms read raw DstOffset as offset (same as child-3 F3 / the brtrue-on-reference paired follow-up). Ldsflda correctly avoids it (localInfos resolution); fixing the IL-static arms is the brtrue-on-reference child (they mask the brtrue null-sentinel gap).
