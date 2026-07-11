## 1. Diagnose-first (settle the open encoding questions per design D2/D3/D4)

Resolved STATICALLY from code-reading (the stash-toggle + per-opcode probes are
the empirical confirmation); no TEMP diagnostics were shipped.

- [x] 1.1 **Switch index field.** RESOLVED: `Switch` is NOT in the
      `LowerNeoOffsets` lowering dispatch (only its jump-table TARGETS are
      remapped by `FixBranchTargetsAfterRemove` at `Optimizer.Neo.cs:1728`), so
      `ip->DstOffset` is the raw `Register1` INDEX. The arm resolves the byte
      offset via the defensive `(idx < localInfos.Length) ? localInfos[idx].Offset
      : idx` pattern (same one the un-lowered Stsfld/Ldsfld arms use), which also
      tolerates the offset form. Empirically confirmed by the 3-case probe
      (distinct value per case + out-of-range default).
- [x] 1.2 **ILEnumTypeInstance value storage.** RESOLVED: the value lives in
      `Primitives`. `ILEnumTypeInstance`'s Neo ctor (`ILTypeInstance.cs:100-104`)
      allocates `fields = new byte[underlyingSize]` and the `Primitives` getter
      returns `fields`. Matches the existing ILType-enum Unbox arm (`Neo.cs` reads
      `ins.Primitives`). The Box arm writes `ins.Primitives`. No `fields`-byte
      fallback needed.
- [x] 1.3 **Ldsflda hit audit.** RESOLVED: the 5 full-smoke hits are addressed
      by the IL-static arm (materialize `StaticInstance` byref). CLR-static
      `Ldsflda` has no heap object to address -> deferred with a tagged NIE per
      D4 (2 such tagged-NIE occurrences remain in the full smoke, acceptable).
- [x] 1.4 **Ldsflda offset + consumer check.** RESOLVED: `Ldsflda` shares
      `Ldsfld`'s JIT case (`JITCompiler.cs:2506-2510`) and is NOT in the lowering
      list -> dest resolved via `localInfos` (same defensive pattern). The byref
      is consumed by the EXISTING `Stind_*`/`Ldind_*` arms AND by
      `CopyNeoCallArguments` -> `NeoMarshalByrefFieldToSlot`, which ALREADY
      dispatch `mStack[objIdx] is ILTypeInstance` -> `Primitives[off]` for read
      AND write-back (`CopyNeoCallWriteBack` -> `isWrite: true`). ZERO consumer
      change (task 5.3 not needed). `ILTypeStaticInstance : ILTypeInstance`, so
      `GetNeoILInstance`/`NeoMarshalByrefFieldToSlot` recognize it.

## 2. Conv_R_Un arm (design D1 -- fully determined)

- [x] 2.1 Added `case OpCodeREnum.Conv_R_Un:` to ExecuteNeo's conv cluster
      (beside `Conv_R4`/`Conv_R8`), under `#if ENABLE_NEO_MODE`. Dispatches on
      `(NeoPrimitiveTypeTag)ip->Operand2`: `I8`/`U8` -> `(double)ReadConvU8`;
      `R4` -> `(double)*(float*)`; `R8` -> `*(double*)`; else (`I4`/`U4`) ->
      `(double)ReadConvU4`. Writes `*(double*)(frameBase + ip->DstOffset)`.
      Width dispatch is required because `ReadConvU4` truncates 64-bit sources.
- [x] 2.2 Probe `NeoStepMiscOp_ConvRUn`. Uses `0x80000000` (2^31, float32-exact
      -> Legacy-neutral) for the U4 case and a runtime-built sign-bit-set ulong
      for the U8 case. FAULT criterion met (Step-6 NIE without the arm).

## 3. Switch arm (design D2)

- [x] 3.1 Added `case OpCodeREnum.Switch:`, under `#if ENABLE_NEO_MODE`. Reads
      the index via the localInfos-resolved offset; `var table =
      method.JumpTablesRegister[ip->Operand];` in-range -> `ip = ptr +
      table[idx]; continue;` else fall through. Identical to Legacy.
- [x] 3.2 Probe `NeoStepMiscOp_Switch`: 3-case `switch(int)` + default; asserts
      case-1 result (20) and out-of-range default (-1). FAULT criterion met.

## 4. Unbox-of-enum guard (design D3)

- [x] 4.1 In the Neo Unbox CLR-primitive branch, before
      `NeoWritePrimitiveToFrame`, added `if (obj is ILEnumTypeInstance
      enumUnboxObj)` -> copy `enumUnboxObj.Primitives` (underlying-primitive
      bytes) into `frameBase + ip->DstOffset` via `Unsafe.CopyBlock`, sized by
      `GetPrimitiveSize(enumIlType.FieldTypes[0])`. Under `#if ENABLE_NEO_MODE`.
- [x] 4.2 Probe `NeoStepMiscOp_UnboxEnum`: IL `enum E { A=1, B=2, C=7 }`; box
      `E.B`; `(int)(object)E.B`; assert `== 2`. FAULT criterion met (the
      "unsupported CLR primitive for Unbox: ILEnumTypeInstance" NIE without the
      guard).

## 5. Ldsflda arm (design D4)

- [x] 5.1 Added `case OpCodeREnum.Ldsflda:`, under `#if ENABLE_NEO_MODE`.
      Decodes `typeHash`/`fieldHash` from `ip->OperandLong`; for `ILType`:
      `mStack.Add(ilt.StaticInstance)`, emit byref `(mStack.Count-1,
      PrimitiveOffset|ReferenceOffset)`. Dest resolved via `localInfos` (not
      lowered). Does NOT stamp `Operand3`.
- [x] 5.2 Default branch: tagged NIE for `CLRType` -> "Neo Ldsflda: CLR static
      field address deferred (follow-up)" (the 2 full-smoke CLR-static hits).
- [x] 5.3 SKIPPED -- task 1.4 confirmed `CopyNeoCallArguments` already
      recognizes the materialized-StaticInstance byref (zero consumer change).
- [x] 5.4 Probe `NeoStepMiscOp_Ldsflda`: `SetRef(ref S_Sf, 7)` (optimizer
      inlines to `ldsflda; stind.i4`) + `GetRef(ref S_Sf)` (inlines to
      `ldsflda; ldind.i4`); assert read-back `== 7`. Read-back is via `ldind`
      (a lowered byref consumer), NOT `ldsfld` (whose IL-static arm has a
      pre-existing dest-resolution gap, unrelated to this change). FAULT
      criterion met (Step-6 NIE without the arm).

## 6. Verify

- [x] 6.1 **Stash-toggle.** Stashed ONLY the 4 `ILIntepreter.Neo.cs` arms
      (probes kept); all 4 probes FAILED on HEAD with the exact expected NIEs
      (`Conv_R_Un`/`Switch`/`Ldsflda` Step-6; Unbox-enum tagged-CLR-primitive).
      Restored -> all 4 PASS.
- [x] 6.2 **Build.** `dotnet build ILRuntimeTestCLI -c Debug_Neo
      --no-incremental` = 0 errors.
- [x] 6.3 **NeoStep smoke.** 324/0 (320 baseline + 4 new probes), ZERO
      regressions.
- [x] 6.4 **Full Neo smoke (no filter; pre-crash segfault).** Each gap message
      count drops: `Conv_R_Un` 4->0, `Switch` 2->0, `Ldsflda` Step-6 5->0,
      "unsupported CLR primitive for Unbox: ILEnumTypeInstance" 2->0. The 2
      remaining "Neo Ldsflda: CLR static field address deferred" are the
      intentionally-deferred CLR-static shape (tagged NIE, acceptable).
- [x] 6.5 **Legacy-neutral.** Plain `Debug` + `useRegister=true` + `NeoStep` =
      324 ran / 17 failed (the documented 17-failure Legacy baseline; my 4 new
      probes pass under Legacy too). Neo-gated => Legacy-neutral by
      construction.

## 7. Cleanup

- [x] 7.1 No TEMP diagnostics were shipped (resolved statically; the stash-toggle
      + probes are the empirical proof). Temp `.tmp-miscop-*.log` files removed.
- [x] 7.2 `git status` (source scope): only `ILIntepreter.Neo.cs` (4 arms +
      Unbox guard) + new `TestCases/NeoMiscOpTest.cs`. No `Optimizer.Neo.cs` /
      `CopyNeoCallArguments` change (not needed). All edits Neo-gated.
