## Why

A `ldloca <CLR-struct local>; ldflda <field>; ldind.r4/stind.r4` sequence (e.g.
`TestValueTypeBinding.Test00`'s `var a = TestVector3.One; a.X += 100;`) AV-crashes
the Neo interpreter. The byref that `ldflda` produces for a field of a **CLR value
type local** (inline flat bytes in the frame) carries the field's
`FieldInfo.GetHashCode()` -- which `AppDomain.GetFieldOffset` returns as
`PrimitiveOffset` for any non-IL declaring type -- where the consumer arms expect a
byte offset, so `ldind`/`stind` dereference `frameBase + (vtBase + <huge hash>)` and
segfault. This is a LATENT access violation unmasked by child 8
(`neo-clr-static-vt-field`, which made `TestVector3.One` ldsfld reachable); it is
reproducible post-child-8 and blocks the value-type-binding smoke.

## What Changes

- Fix the Neo `ldflda` runtime arm so that, for a **CLR-struct-local** operand (a
  frame-native Ref Slot, `objectIndex == -1`) whose field's declaring type is a
  `CLRType`, the produced frame-native Ref Slot's offset half is the field's REAL
  managed byte offset within the struct's flat bytes -- NOT `field.PrimitiveOffset`
  (which is the `FieldInfo` hash for a CLR type).
- The real byte offset is resolved via `Marshal.OffsetOf(clrStructType, fieldName)`
  (cached per `(typeHash, fieldHash)`). This is sound because the only CLR structs
  that reach the Neo flat-byte local path are blittable (a CLR VT with reference
  fields throws the Step-13b tagged NIE inside `ReadNeoValueType`/`WriteNeoValueType`
  before ever materializing as a frame local), and for a blittable struct the managed
  layout (what `Unsafe.WriteUnaligned` writes into the frame) is identical to the
  `Marshal.OffsetOf` offset.
- The JIT stamps a new `Operand4` marker bit (`NeoLdfldaClrStructLocalFieldMarker`)
  on `ldflda` when the field's declaring type is a `CLRType`, so the runtime arm
  discriminates the CLR-struct-local case with zero cost on the IL-struct path.
- `stind_*`/`ldind_*` (and `stobj`/`ldobj`/`initobj`) are **unchanged**: once the
  `ldflda`-produced byref carries a valid frame byte offset, the existing frame-
  native arm (`objectIndex == -1` -> `*(T*)(frameBase + off)`) reads/writes the field
  correctly.
- Add a `NeoStep` probe (a CLR struct local, `ldflda` a field, `stind`/`ldind`
  through it, assert the value) that FAULTs (the AV) without the fix.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `neo-byref`: The `ldflda / ldarga address producers` requirement is modified so the
  runtime `ldflda` arm, for a frame-native operand (`objectIndex == -1`) whose field's
  declaring type is a `CLRType`, produces a frame-native Ref Slot whose offset half is
  the field's real managed byte offset (resolving it via a cached
  `Marshal.OffsetOf` lookup) rather than the `field.PrimitiveOffset` FieldInfo hash.
  This closes the gap in the existing requirement (which only specifies the in-frame
  IL-VT operand case and the heap CLR-object operand case, leaving the frame-native
  CLR-struct-local operand case to mis-use the hash as a byte offset).

## Impact

- **Affected code (all Neo-gated, Legacy-neutral by construction):**
  - `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- add the
    `NeoLdfldaClrStructLocalFieldMarker = 0x8` constant; stamp it in `case Code.Ldflda`
    when the resolved declaring `type is CLRType`.
  - `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- in the `Ldflda`
    runtime arm's `objectIndex == -1` branch, consult the new marker; when set, resolve
    the real byte offset (cached `Marshal.OffsetOf`) and use it in place of
    `fieldPrimOff`. Add the cache (a static `Dictionary<long,int>` keyed by
    `((long)typeHash << 32) | (uint)fieldHash`).
- **No JIT/optimizer lowering change** (the marker lives in the standalone `Operand4`
  field, which survives `LowerNeoOffsets`; the F-6 type-spec gate clears only bits
  0x2/0x4 and is mutually exclusive with the new bit 0x8). The `addrAlias` folding is
  untouched (the byref escapes to `ldind`/`stind`, so the runtime `ldflda` arm fires).
- **No object-model / CLR-binding change.**
- **Probe:** new `TestCases/NeoStepLdindStindByrefClrStructTest.cs`.
- **Baseline:** NeoStep smoke 341/0 (after child 14) -> 343/0 (341 + 2 probe filter
  matches). Full smoke: the `TestValueTypeBinding.Test00`-shaped AV is eliminated.
