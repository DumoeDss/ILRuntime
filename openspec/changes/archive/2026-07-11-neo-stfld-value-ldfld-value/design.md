# neo-stfld-value-ldfld-value — design

## Why

A whole IL value-type **field** store/load (`stfld.value` / `ldfld.value`) —
e.g. `struct Outer { Inner inner; }` with `o.inner = new Inner(1,2);` (whole-
Inner store) and `Inner i = o.inner;` (whole-Inner load) — was a long-deferred
Step 12b gap. The opcodes `Stfld_Value` / `Ldfld_Value` EXISTED in
`OpCodeREnum` (953/965) and the JIT ALREADY EMITTED them
(`JITCompiler.GetLdfldCodeForType`/`GetStfldCodeForType`: `fieldType is ILType
&& fieldType.IsValueType`), but `ExecuteNeo` had **NO `case` for either** — they
fell through to the default `"Neo: opcode {0} not yet implemented (Step 6)"`
NIE (`ILIntepreter.Neo.cs:5234`). This re-surfaced as
`KeyValuePair<int,int>`-style generic-VT-field failures during the generic-types
work.

The 10-for-10 lesson applied: this was a **focused opcode-arm gap**, NOT a deep
rework. The whole-VT field copy is a `Move_Vt`-style byte CopyBlock + ref-slot
loop between the field's storage region and an in-frame VT — the exact pattern
the `Stobj`/`Ldobj` Step-17(b) arms and the `Stsfld` IL-VT-static-field arm
already implement.

## Root cause (the specific NIE site + storage shapes)

1. **Runtime NIE site.** `ExecuteNeo` (`ILIntepreter.Neo.cs`) had no
   `case OpCodeREnum.Stfld_Value:` / `case OpCodeREnum.Ldfld_Value:`. Default
   fall-through → `"Neo: opcode Stfld_Value not yet implemented (Step 6)"`.
2. **JIT already encoded the field offsets.** `case Code.Ldfld` / `case Code.Stfld`
   under `#if ENABLE_NEO_MODE` set `Operand` = declaring-type hash,
   `Operand2` = `field.PrimitiveOffset`, `Operand3` = `field.ReferenceOffset`,
   but did NOT stamp the field TYPE (the field's ILType), so the runtime could
   not learn `TotalPrimitiveSize` / `TotalReferenceCount`.
3. **Optimizer never lowered the registers.** `LowerNeoOffsets`
   (`Optimizer.Neo.cs`) did not list `Stfld_Value` / `Ldfld_Value` in its
   switch, so `handled = false` and `LowerR1R2` was never called →
   `DstOffset`/`SrcOffset` stayed at 0/0 (garbage). `WarnUnhandledNeoLoweringOpcode`
   is a no-op, so this was silent.

**Storage shapes** (the field is always an IL struct field on a HEAP
ILTypeInstance — `Stfld_Value`/`Ldfld_Value` have NO `_Inline` variant by
design; the in-frame-VT field path folds through the `_Inline` opcodes):

- Heap owner: `ILTypeInstance` resolved via
  `GetNeoILInstance(mStack, *(int*)(frameBase + ownerSlot))`.
- Field storage: `owner.Primitives[field.PrimitiveOffset .. +primSize]` +
  `owner.ManagedObjects[field.ReferenceOffset .. +refCount]`.
- Value register (in-frame flat-bytes VT): `frameBase + SrcOffset` (Stfld) /
  `frameBase + DstOffset` (Ldfld); its ref-run base is recovered via the
  **runtime `localInfos` scan** (the established R2 pattern shared with the
  Stobj/Ldobj Step-17(b) arms — `localInfos[li].Offset == valueOff` →
  `.RefOffset`).

## The fix (three Neo-gated edits, Legacy-neutral)

1. **JIT** (`JITCompiler.cs`, `case Code.Ldfld` / `case Code.Stfld` under
   `#if ENABLE_NEO_MODE`): when `op.Code == Ldfld_Value` / `Stfld_Value`,
   stamp `op.Operand4 = fieldType.GetHashCode()` (Operand4 is otherwise unused
   for these opcodes — F-10 only uses it for `Stfld_Ref`/`Ldfld_Ref`/`Ldflda`).
   The runtime resolves `AppDomain.GetType(ip->Operand4) as ILType` →
   `TotalPrimitiveSize` / `TotalReferenceCount`. No-op for the typed
   `Ldfld_*`/`Stfld_*` (Operand4 left 0).
2. **Optimizer** (`Optimizer.Neo.cs` `LowerNeoOffsets`): add
   `case OpCodeREnum.Stfld_Value:` to the Stfld group and
   `case OpCodeREnum.Ldfld_Value:` to the Ldfld group so `LowerR1R2` lowers
   R1/R2 → DstOffset/SrcOffset. (The `Ldfld_Ref` special case that overwrites
   `op.Operand` with the dest RefOffset is guarded by `== Ldfld_Ref`, so
   `Ldfld_Value` is unaffected.)
3. **Runtime** (`ILIntepreter.Neo.cs` `ExecuteNeo`): two new cases after
   `Stfld_Ref`. Each mirrors `Move_Vt`:
   - `Stfld_Value`: `CopyBlock(owner.Primitives[fldPrimOff], frameBase+SrcOffset,
     primSize)`; if `refCount > 0`, recover `srcRefBase` via the localInfos scan
     (scan-miss → tagged NIE, loud), then `owner.ManagedObjects[fldRefOff+i] =
     mStack[frameRefBase+srcRefBase+i]`.
   - `Ldfld_Value`: the reverse — `CopyBlock(frameBase+DstOffset,
     owner.Primitives[fldPrimOff], primSize)`; if `refCount > 0`, recover
     `dstRefBase` via the scan, then `mStack[frameRefBase+dstRefBase+i] =
     owner.ManagedObjects[fldRefOff+i]`.

Shallow copy semantics (C# struct-copy): references are shared, identity
preserved (same as `Move_Vt`).

## Scope

- IN: whole IL-struct field store/load on a heap ILTypeInstance. Nested VT
  WITH a reference field (`Inner { int a; string s; }`) is the load-bearing
  case — the ref copy. Pure-primitive nested VT (refCount 0) is the pure byte
  CopyBlock sub-case.
- OUT: CLR-struct field (a different opcode — `Stfld_Ref`/`Ldfld_Ref` F-10 arm,
  already shipped in `neo-clrstruct-field-of-il`); in-frame-VT field (the
  `_Inline` variants); nested-field-byref scan-miss shapes (fail loud with a
  tagged NIE, mirroring Stobj/Ldobj — a deferred follow-up, NOT silently
  wrong).

## Verification

- Reproducer: `NeoStep12b_StfldLdfldValue_Prim` (pure-primitive Inner) +
  `NeoStep12b_StfldLdfldValue_WithRef` (Inner WITH a ref field — load-bearing).
  Both FAIL-on-HEAD (`Stfld_Value not yet implemented (Step 6)`) → PASS-after.
- NeoStep12b gate: **6/6** (4 prior + 2 new).
- Full NeoStep smoke: **293/0/0** (291 baseline + 2 probes).
- Stash-toggle PROVEN: stash the 3 engine files → probes FAIL on HEAD; pop →
  PASS. Test file kept across the toggle.
- Legacy-neutral: `dotnet build ILRuntime/ILRuntime.csproj -c Debug` (Neo OFF)
  → 0 errors (all edits `#if ENABLE_NEO_MODE`).

## Durable findings

- `Stfld_Value`/`Ldfld_Value` are HEAP-only by design (no `_Inline` form) — the
  in-frame-VT field path folds through `_Inline`. So the owner is always a heap
  ILTypeInstance and the value is an in-frame flat-bytes VT.
- The in-frame VT's ref-run base is NOT in the opcode (operand pressure:
  Operand2/Operand3 hold the field offsets, Operand4 holds the field-type hash).
  Recover it via the runtime `localInfos` scan — the SAME R2 pattern
  `Stobj`/`Ldobj` use (Step 17(b), `neo-step17-stobj-refloop`). A scan-miss is
  an exotic shape (a non-direct-local value) → tagged NIE (loud), matching the
  established convention.
- `WarnUnhandledNeoLoweringOpcode` is a no-op by design (incremental steps +
  Prewarm compile the whole assembly). An unlisted opcode silently keeps
  0/0 DstOffset/SrcOffset — when adding a new opcode to `ExecuteNeo`, ALWAYS
  also add it to `LowerNeoOffsets` or it will read garbage offsets.
