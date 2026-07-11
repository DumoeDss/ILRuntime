## Context

The Neo object model stores a CLR value-type local (e.g. `TestVector3`, a blittable
`struct { float X, Y, Z; }`) as inline **flat managed bytes** in the frame byte
region (written by `WriteNeoValueType` = `Unsafe.WriteUnaligned<T>`). The byref
machinery is the 8-byte Ref Slot `(objectIndex, offset)`:

- `ldloca <CLR-struct local>` produces `(-1, structLocalFrameOff)` (frame-native).
- `ldflda <field>` on that operand reads the operand's leading int (`-1`) and enters
  the frame-native branch, producing `(-1, vtBase + fieldPrimOff)` where
  `fieldPrimOff = ip->Operand2`.
- `ldind.r4`/`stind.r4` consume the byref; the frame-native arm (`objectIndex == -1`)
  reads/writes `*(T*)(frameBase + off)`.

The bug: `ip->Operand2` for `ldflda` is `offset.PrimitiveOffset` from
`AppDomain.GetFieldOffset` (`JITCompiler.cs:2951`). For a non-IL declaring type,
`GetFieldOffset` returns `PrimitiveOffset = type.GetFieldIndex(token)`
(`AppDomain.cs:2257`), and `CLRType.GetFieldIndex` returns `fieldMapping[name]` =
`FieldInfo.GetHashCode()` (`CLRType.cs:610/614/661-676`) -- a large, arbitrary 32-bit
hash. So the produced byref is `(-1, vtBase + <huge hash>)`, and the `ldind`/`stind`
frame-native deref of `frameBase + <huge hash>` is an out-of-frame access -> AV.

This is correct for an IL-struct-local field, where `offset.PrimitiveOffset` is the
real `Primitives` byte offset. The gap is specifically the **CLR-struct-local**
operand: the existing `ldflda` requirement covers the in-frame-IL-VT operand (marker
0x1) and the heap CLR-object operand (field-identity stamp), but NOT the frame-native
CLR-struct-local operand, which silently mis-uses the hash as a byte offset.

This is LATENT: it was unmasked by child 8 (`neo-clr-static-vt-field`), which removed
the over-conservative binder guard that blocked `TestVector3.One` ldsfld, making the
`var a = TestVector3.One; a.X += 100;` shape reachable.

## Goals / Non-Goals

**Goals:**
- Eliminate the AV: `ldind`/`stind` (and `stobj`/`ldobj`/`initobj`) through a byref
  produced by `ldflda` on a CLR-struct-local field read/write the field correctly.
- A `NeoStep` probe that FAULTs (AV) on HEAD and PASSES after the fix.
- Neo-gated, Legacy-neutral.

**Non-Goals:**
- The `addrAlias`-foldable path (`ldloca <CLR struct>; ldflda <field>; ldfld/stfld`
  whose leaf is a foldable inline field access). That path folds `op.Operand2` (the
  hash) as a byte offset at `Optimizer.Neo.cs:1510` and is a SEPARATE defect on the
  raw `Ldfld`/`Stfld` surface (child-4's box-roundtrip handlers own CLR-VT-owner
  raw Ldfld/Stfld; whether the folding fast-path also mis-fires for a CLR struct is
  out of scope and not reached by the current smoke). This change does NOT touch the
  folding and does NOT regress it.
- CLR structs WITH reference fields (they throw the Step-13b tagged NIE inside
  `ReadNeoValueType`/`WriteNeoValueType` before reaching the flat-byte local path).
- A CLR struct field accessed through a HEAP boxed struct operand (`objectIndex >= 0`)
  -- that already works via the existing heap branch + `NeoReadClrObjectField`/
  `NeoWriteClrObjectField` (field hash), unchanged.

## Decisions

### D1: Fix in the `ldflda` runtime arm (not in `ldind`/`stind`)

The fix computes the field's REAL managed byte offset and stamps it into the
frame-native byref's offset half, so the byref is a valid frame-native address and
**every consumer** (`ldind_*`, `stind_*`, `stobj`, `ldobj`, `initobj`, and a byref
call arg) works unchanged via the existing `objectIndex == -1` arm.

**Alternatives rejected:**
- *New `ldind`/`stind` decode arm with a box-roundtrip (child-4 style).* Rejected: the
  `ldind`/`stind` opcodes are typed primitive loads/stores and carry NO field
  metadata; a box-roundtrip needs the struct's CLR Type, which is not recoverable from
  the 8-byte byref alone (the byref can carry at most `(structFrameOff, fieldHash)`,
  not the struct type). Routing the struct type to the consumer would require JIT
  type-dataflow through the eval-stack register -- a far larger change. Fixing the
  producer (`ldflda`) sidesteps this entirely.
- *Re-encode the byref to route to the existing CLR-object heap arm.* Rejected: the
  heap arm dereferences `mStack[objIdx]` (a boxed CLR object). A CLR struct LOCAL is
  flat bytes in the frame, not a boxed mStack object; boxing it into a scratch mStack
  slot would break `stind` write-back (the write lands in the box copy, not the frame
  local).

### D2: Resolve the byte offset via `Marshal.OffsetOf`, runtime-cached

`Marshal.OffsetOf(clrStructType, fieldName)` returns the field's offset in the
struct's layout. This is sound here because the ONLY CLR structs that reach the Neo
flat-byte local path are blittable (a CLR VT with reference fields throws the
Step-13b NIE before materializing), and for a blittable struct the managed layout
(what `Unsafe.WriteUnaligned` writes into the frame) is identical to the
`Marshal.OffsetOf` offset.

The offset is resolved lazily and cached in a static
`Dictionary<long, int>` keyed by `((long)typeHash << 32) | (uint)fieldHash` (both
already on the `ldflda` instruction: `ip->Operand` = type hash, `ip->Operand2` =
field hash). First access pays one `Marshal.OffsetOf` + one `CLRType.GetField`;
subsequent accesses are an O(1) dict hit.

**Alternatives rejected:**
- *JIT-time `Marshal.OffsetOf` stamped into a spare `OpCodeR` field.* The only spare
  int for `ldflda` is `Operand3 (@16)`, which is the HIGH dword of `OperandLong`
  (child-2 sharp edge: "NEVER a safe scratch when `OperandLong` is in use"). Although
  no current pass reads `ip->OperandLong` for a `ldflda`, stamping `Operand3` is a
  latent aliasing hazard; the runtime cache avoids it entirely and keeps the `OpCodeR`
  stamping to a single standalone-`Operand4` marker bit.
- *Pack the offset into the high bits of `Operand4` alongside the marker.* Works
  (offsets are tiny) but couples the offset to the marker field and risks confusion
  with the F-6 type-spec gate's bit operations; the cache is clearer and the path is
  not hot.
- *Child-4 box-roundtrip (`ReadNeoValueType` whole-struct + `FieldInfo.GetValue`/
  `SetValue`).* That is the proven approach for the raw `Ldfld`/`Stfld` opcodes
  (which DO carry field metadata in `OperandLong`), but does not apply through the
  byref indirection (see D1).

### D3: JIT stamps a new marker bit to discriminate the CLR-struct-local case

Add `NeoLdfldaClrStructLocalFieldMarker = 0x8` (bit 0x8 of the standalone `Operand4`
field; existing `ldflda` markers are 0x1/0x2/0x4, so 0x8 is free). The JIT body's
`case Code.Ldflda` stamps it when the resolved declaring `type is CLRType`. The
runtime `ldflda` arm consults it ONLY in the `objectIndex == -1` branch (a CLR struct
LOCAL); the heap branch (`objectIndex >= 0`) ignores it and keeps using the hash via
`NeoReadClrObjectField`/`NeoWriteClrObjectField`.

This is mutually exclusive with the F-6 (0x1, in-frame IL VT) and F-10 (0x2,
CLR-struct-field-of-IL) markers: those require the declaring type to be an `ILType`,
whereas the new marker requires a `CLRType` declaring type. The F-6 type-spec gate
(`JITCompiler.cs:1081`) clears only bits 0x2/0x4 and fires only for an IL-VT source,
so it never touches bit 0x8 and never fires for a CLR-struct source. The marker lives
in standalone `Operand4`, so it survives `LowerNeoOffsets` intact (same property the
existing markers rely on).

**Alternative rejected:** *Runtime-only detection by resolving `ip->Operand`
(typeHash) -> CLRType on every frame-native `ldflda`.* Rejected: it adds a dict
lookup to every IL-struct `ldflda` (the common case). The marker makes the IL path a
single bit test.

## Risks / Trade-offs

- **[Marshal.OffsetOf correctness for non-blittable/auto-layout structs]** -> Only
  blittable CLR structs reach this path (ref-field structs NIE earlier in
  `ReadNeoValueType`/`WriteNeoValueType`); C# value types default to
  `LayoutKind.Sequential`, so `Marshal.OffsetOf` matches the managed layout. If a
  genuinely auto-layout blittable struct ever reaches the path,
  `Marshal.OffsetOf` throws -> wrap the resolution in try/catch and re-throw a tagged
  `NotImplementedException` naming the struct (fail-loud, no silent corruption). The
  cache stores the resolved int (or the resolution throws once and is re-attempted --
  acceptable for an unreachable edge).
- **[Per-`ldflda`-on-CLR-struct-field dict lookup]** -> The path is not hot (the AV
  currently aborts it); the dict hit is O(1). Acceptable for correctness.
- **[Probe must FAULT, not just produce a wrong value]** -> The HEAD behavior is an AV
  (segfault) -> the probe crashes the runner = a failed test. The fix makes the field
  read/write correct, so the probe asserts the round-tripped value. The apply worker
  MUST stash-toggle to confirm FAULT-on-HEAD -> PASS-after (child-1/child-2/child-14
  discipline).
- **[Probe shape must actually emit `ldflda` + `ldind`/`stind`, not `ldfld`/`stfld`]**
  -> The `a.X += v` compound-assignment pattern on a CLR struct local is the trigger
  (child-8 confirmed `Test00` reaches `ldflda;ldind.r4/stind.r4`). The probe mirrors
  `Test00` AND adds a `ref float` variant to robustly force the `ldind`/`stind`
  consumers; the apply worker confirms via the stash-toggle that the probe exercises
  the fixed arm (if Roslyn lowers to `ldfld`/`stfld` instead, the probe still passes
  but does not exercise the fix -- the worker adjusts the probe to force the byref
  escape, e.g. via a `ref` local or `Unsafe`).

## Open Questions

- Whether the `addrAlias`-foldable `ldfld`/`stfld` path on a CLR struct local
  (Optimizer.Neo.cs:1510 folds `op.Operand2` = the hash) is ALSO broken and needs a
  sibling fix. Out of scope here (not reached by the current smoke post-fix); recorded
  for a follow-up audit. The raw `Ldfld`/`Stfld` CLR-VT-owner handlers (child-4) cover
  the non-folded shape via box-roundtrip.
