# neo-stfld-value-inframe-owner (wave-2 child of neo-overhaul)

## Problem
At the 73-failure full Neo smoke baseline (2026-07-14), `Stfld_Value` on an
in-frame IL-struct owner NREs. `Stfld_Value` / `Ldfld_Value` are the Step-12b
"whole-IL-VT field store/load" opcodes (a struct/enum field that is ITSELF an
IL value type, e.g. `MyStruct { Vector3 v; }` or `MyStruct { SomeEnum e; }`).
They are emitted as the PLAIN heap-owner opcode for BOTH:
- a heap ILTypeInstance owner (`obj.v = val;` -> owner slot holds an mStack
  index), AND
- an in-frame IL-struct owner -- a value-type LOCAL (`s.e = ...;`) or a
  value-type `this` (`this.v = v;` in a struct ctor) -- whose flat bytes sit
  directly in the frame at DstOffset/SrcOffset (NOT an mStack index).

The runtime arm (`ILIntepreter.Neo.cs:4806` Stfld / `:4846` Ldfld) unconditionally
does `ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->DstOffset))`,
which reads the owner slot as an mStack index. For an in-frame owner that int is
the struct's first field bytes -> garbage index -> NRE.

Confirmed failing: `StructTests.StructTest3` (`s.e = EnumTest.TestEnum.Enum2` ->
`stfld.value r2,r3,..MyStruct(4,0)`) and `StructTests.StructTest14`
(`this.v = v` -> `stfld.value r0,r1,..NestedStruct`). Both NRE at
`ILIntepreter.Neo.cs:line 4806`. Both PASS under Legacy.

## Root cause (re-audit-confirmed, JIT-dump-pinned)
- The typed scalar arms (`Stfld_I4` / `Stfld_R4` / ...) get a DISTINCT `_Inline`
  opcode via the JIT rewrite `TryRewriteFieldAccessForInline` when the owner
  register holds an in-frame VT (`registerTypes[ownerReg] is ILType VT`).
- The Value variants are EXPLICITLY EXCLUDED from that rewrite
  (`TryRewriteFieldAccessForInline` comment @ `JITCompiler.cs:1517`:
  "Value variants are 12b whole-copy"); there is NO `Stfld_Value_Inline` /
  `Ldfld_Value_Inline` opcode. So the in-frame owner shape reaches the plain
  heap arm.
- The owner representation (heap-mStack-index vs in-frame-flat-bytes) is a
  JIT-time dataflow fact; the untyped Neo frame CANNOT distinguish them at
  runtime (a small in-frame struct's first int field can coincidentally be a
  valid mStack index pointing to an ILTypeInstance -- the same constructible
  collision documented for child-24/29 raw-Ldfld). So a runtime discriminator
  is unsafe; a JIT-time discriminator is required.

## Fix (Neo-gated -> Legacy-neutral; 2 files)
A JIT-time discriminator stamped into `Operand` (@8). For the Value variants
`Operand` is the declaring-type hash, which is DEAD -- neither `LowerNeoOffsets`
nor the ExecuteNeo arm reads it (verified). Repurpose it.

1. `JITCompiler.cs`: two `public const int` markers
   (`NeoValueFieldInFrameOwnerMarker = 1`, `NeoValueFieldHeapOwnerMarker = 0`).
   In `TypeSpecializeNeoOpcodes`, stamp Operand unconditionally for every
   `Stfld_Value`/`Ldfld_Value` from the owner register's dataflow type (the
   SAME `registerTypes[ownerReg]` signal the typed `_Inline` rewrite keys on).
   Both arms stamped (a heap type-hash could otherwise collide with the marker).
2. `ILIntepreter.Neo.cs`: branch the `Stfld_Value` / `Ldfld_Value` arms on the
   marker. The in-frame branch mirrors the typed `_Inline` arms: write/read the
   field's primitive region at `frameBase + ownerOff + fldPrimOff` (a flat
   CopyBlock of `ftFld.TotalPrimitiveSize` bytes) and copy the ref-region via
   the established `localInfos` ref-run scan (R2). The heap branch is unchanged.

No new opcode (avoids the ~8-location ripple of the typed-arm pattern: enum def,
ToString, JIT rewrite/seeding, Optimizer.Neo lowering+alias pass, 5 Optimizer.Utils
helpers). No optimizer change (the existing Stfld_Value/Ldfld_Value lowering at
`Optimizer.Neo.cs:987/957` already resolves DstOffset/SrcOffset to byte offsets
for BOTH owner shapes). No object-model change.

## Verify (truth = full-smoke count, REAL run) -- CONFIRMED
- Name-filter: `StructTest3` / `StructTest14` PASS after fix (both NRE'd at
  `ILIntepreter.Neo.cs:line 4806` on the 73-baseline; `.tmp-st3-baseline.log`
  / `.tmp-st14-baseline.log`).
- FULL SMOKE delta: **73 -> 71** (`Ran 925 tests, 71 failded`; the 2 flipped
  are exactly StructTest3 + StructTest14; `.tmp-stfldvalue-postfix.log`). No
  regression is possible from this change: an in-frame owner NRE'd at the old
  arm (already failing) so it can only flip RED->GREEN; a heap owner keeps the
  byte-identical `else` path (marker=0); and a heap reference typed as an
  ILType has `IsValueType=false` so it cannot false-positive as in-frame.
- NeoStep **391/0** (broad green, no regression; NeoStep12/13 VT intact;
  `.tmp-neostep-postfix.log`).
- Legacy-neutral: plain-Debug CLI build = 0 errors; Legacy NeoStep = 391 ran /
  18 failed (the documented pre-existing Legacy baseline, unchanged); Legacy
  StructTest3 = PASS. The change is `#if ENABLE_NEO_MODE`-gated (file-level for
  ILIntepreter.Neo.cs; the stamping is inside the Neo-gated
  TypeSpecializeNeoOpcodes; the consts are Neo-labeled but harmless if unused).

## Files
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` (+~30: 2 const
  markers + the stamping block in TypeSpecializeNeoOpcodes).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (the
  Stfld_Value + Ldfld_Value arms gain an in-frame branch on the marker).
