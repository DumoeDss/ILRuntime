# Design — implement-neo-step12 (In-frame value types + Inline field access)

This design is grounded in the current code. All file:line references are to
the branch state at propose time.

## 0. The one big finding that shapes the design

**`ValueTypeObjectReference` / `AllocValueType` is Legacy-only.** A full-repo
grep shows every reference lives in `ILIntepreter.cs`, `ILIntepreter.Register.cs`,
`RuntimeStack.cs`, `StackObject.cs`, `ValueTypeBinder.cs`, `DebugService.cs`,
`CLRRedirections.cs`, `ValueTypeInitInfo.cs`, and one Legacy path in
`ILTypeInstance.cs`. The Neo files — `ILIntepreter.Neo.cs`, the Neo regions of
`JITCompiler.cs`, and `Optimizer.Neo.cs` — contain **zero** references.

Consequence: Step 12 is NOT "remove the descriptor from the Neo path". The
Neo path already stores VT locals as flat bytes. Concretely,
`JITCompiler.AllocateLocalStackSpaces` (JITCompiler.cs ~1129-1156) already
allocates a VT local a contiguous `slot.Offset .. +il.TotalPrimitiveSize` byte
range with `slot.RefOffset .. +il.TotalReferenceCount` ref slots. The
`StackRegisterCount`-temp loop (~1195-1205) already sizes each temp at the
max VT size across all value types in the method (`GatherValueTypes`,
~1181-1194).

So the **actual** Step 12 work is:
1. Add natural alignment (currently missing).
2. Add the `_Inline` field opcodes + their `ExecuteNeo` arms (do not exist).
3. Make the JIT pick the inline variant when the operand slot is an in-frame
   VT (currently the JIT always emits the heap `Ldfld_*`/`Stfld_*`, which is
   wrong for in-frame VT operands).
4. Resolve the `Initobj` ref-field sub-case (currently throws).

## 1. In-frame value-type storage layout

### 1.1 Slot already exists — only alignment is added

A value-type local/temp today occupies, in the frame byte region:

```
slot.Offset                              (start of the VT's primitive bytes)
  ... il.TotalPrimitiveSize bytes ...    (the VT's Primitives layout, verbatim)
slot.Offset + il.TotalPrimitiveSize      (end of primitive bytes)
```

and, in the frame's mStack ref region:

```
mStack[frameRefBase + slot.RefOffset]            (first ref field)
  ... il.TotalReferenceCount ref slots ...
mStack[frameRefBase + slot.RefOffset + refCount] (one past last ref field)
```

This is **identical in shape** to an `ILTypeInstance` (`byte[] Primitives` +
`AutoList ManagedObjects`), so a memcpy between the two is a pure byte copy +
ref-slot copy (this is what Step 12b `Move_Vt` and Step 13 Box/Unbox exploit).
Step 12 does not change this shape.

### 1.2 What Step 12 adds: natural alignment

Today `AllocateSlotForType` (JITCompiler.cs ~1227-1261) and the local/temp
loops advance `offset += size` with **no alignment**. A `long`/`double` field
or a VT containing one can land on a non-8-aligned offset, so the `*(long*)`
/ `*(double*)` casts in the `_Inline` arms would be misaligned. (On x64
misaligned access works but is slower and non-portable; the heap
`ILTypeInstance.Primitives` path sidesteps this with `Unsafe.ReadUnaligned`,
but the design doc mandates aligned flat bytes — §4.3 of
`object-model-neo-design.md`.)

Step 12 introduces an alignment helper used wherever a slot offset is
advanced:

```csharp
static int AlignUp(int offset, int alignment)
    => (offset + alignment - 1) & ~(alignment - 1);
```

The alignment of a slot is the **maximum natural alignment among its
primitive fields** (recursively, for nested VTs). To avoid a per-field
recursion at allocation time, `ILType` exposes a cached
`NaturalAlignment` property (int), computed once in `InitializeFields`:

- primitive T → `AppDomain.GetPrimitiveSize(T)` (1/2/4/8).
- IL value-type field → `it.NaturalAlignment` (recurse).
- enum → underlying primitive size.
- A VT's own `NaturalAlignment` = max over its fields (1 if no fields).

`AllocateSlotForType` and the VT-local branch (~1133-1146) then do
`offset = AlignUp(offset, t.NaturalAlignment)` before assigning
`slot.Offset`. The temp-register loop (~1195-1205) aligns each temp slot by
the max alignment across `GatherValueTypes` (tracked alongside `maxSize`).
`mStack` ref offsets need no alignment (they are indices, not byte offsets).

The frame's `TotalStructSize` may grow by a few padding bytes; the
`ushort.MaxValue` cap check in `Optimizer.Neo.cs:13-15` still holds (frames
are nowhere near 64KiB).

### 1.3 Ref fields inside an in-frame VT — where they live

A VT with reference fields stores those refs **inside the VT's own byte
range** as mStack indices (the Neo null convention: -1 = null), exactly like
an `ILTypeInstance` stores ref fields as `ManagedObjects` entries. But in the
**frame** the refs are not inside the byte region — they live in the frame's
mStack ref region under the slot's own `slot.RefOffset`.

This is the subtle point. There are two consistent representations and the
heap (`ILTypeInstance`) one packs refs into a separate `AutoList`. The frame
cannot do that inline (the byte region is flat bytes), so the frame uses the
**parallel ref-offset region**: the slot's `slot.RefCount` ref fields occupy
`mStack[frameRefBase + slot.RefOffset .. +slot.RefCount]`, and the per-field
index within the VT is `field.ReferenceOffset` (from `ILTypeFieldOffset`,
already computed in `ILType.InitializeFields` ~2084-2114).

Therefore:
- `Ldfld_Ref_Inline` / `Stfld_Ref_Inline` do NOT read from
  `frameBase + slot.Offset + field.PrimitiveOffset`. They read/write
  `mStack[frameRefBase + slot.RefOffset + field.ReferenceOffset]`. The
  instruction encodes `slot.RefOffset + field.ReferenceOffset` (an absolute
  frame-ref index) in one operand — see §2.2.
- Box/Unbox (Step 5/13) and `Move_Vt` (Step 12b) already bridge the two
  representations via `CopyFrameToIL` / a ref-slot copy loop; that machinery
  is unchanged by Step 12.

This matches the existing `Ldfld_Ref` heap arm (ILIntepreter.Neo.cs ~1640-
1646) which writes `mStack[frameRefBase + ip->Operand] = obj` — the same
"absolute frame-ref index" encoding, just sourced from a heap instance's
`ManagedObjects` instead of the frame ref region.

## 2. New opcodes and their ExecuteNeo arms

### 2.1 Opcode names (added to `OpCodeREnum.cs` after the existing set)

One per primitive kind, mirroring the existing Ldfld/Stfld family:

```
Ldfld_I1_Inline, Ldfld_I2_Inline, Ldfld_I4_Inline, Ldfld_I8_Inline,
Ldfld_U1_Inline, Ldfld_U2_Inline, Ldfld_U4_Inline, Ldfld_U8_Inline,
Ldfld_R4_Inline, Ldfld_R8_Inline, Ldfld_Ref_Inline,
Stfld_I1_Inline, Stfld_I2_Inline, Stfld_I4_Inline, Stfld_I8_Inline,
Stfld_U1_Inline, Stfld_U2_Inline, Stfld_U4_Inline, Stfld_U8_Inline,
Stfld_R4_Inline, Stfld_R8_Inline, Stfld_Ref_Inline,
```

(22 opcodes. There is no `Ldfld_Value_Inline`/`Stfld_Value_Inline` — loading
a whole VT field as a VT is a copy, which is Step 12b's `Move_Vt`. A nested
VT field is accessed field-by-field via its own primitive offsets; see §6.1.)

### 2.2 Operand encoding

The existing heap arms encode `ip->DstOffset` (destination temp byte offset),
`ip->SrcOffset` (operand object slot byte offset, holds mStack index),
`ip->Operand2` (field PrimitiveOffset within the instance's Primitives),
`ip->Operand3` (field ReferenceOffset within the instance's ManagedObjects).
`Register1/2/3` are lowered to `DstOffset/SrcOffset` byte offsets by the
Neo offset-lowering pass (`LowerNeoOffsets` in Optimizer.Neo.cs).

The `_Inline` family reuses the same fields with shifted meaning:

- `DstOffset` = destination temp's byte offset in the frame (same as heap).
- `SrcOffset` (Ldfld) / `DstOffset` (Stfld) = the **owning VT slot's byte
  offset** in the frame (`slot.Offset`), NOT an mStack index.
- `Operand2` = the field's `PrimitiveOffset` within the VT (from
  `ILTypeFieldOffset.PrimitiveOffset`). The absolute field byte address is
  `frameBase + ip->SrcOffset + ip->Operand2` for Ldfld.
- For the `Ref` variant only: `Operand` (reused, as the heap Ref arm already
  does at ~1643) = absolute frame-ref index
  `slot.RefOffset + field.ReferenceOffset`; the object is at
  `mStack[frameRefBase + ip->Operand]`.

### 2.3 Exact case bodies

Primitive kinds — pure pointer arithmetic, zero branches (Ldfld_I4 shown; the
I1/I2/I8/U*/R4/R8 and Stfld mirrors follow the obvious typed-cast pattern):

```csharp
case OpCodeREnum.Ldfld_I4_Inline:
    // ip->SrcOffset = owning in-frame VT slot byte offset
    // ip->Operand2  = field PrimitiveOffset within the VT
    // ip->DstOffset = destination temp byte offset
    *(int*)(frameBase + ip->DstOffset) =
        *(int*)(frameBase + ip->SrcOffset + ip->Operand2);
    break;

case OpCodeREnum.Ldfld_I8_Inline:
    *(long*)(frameBase + ip->DstOffset) =
        *(long*)(frameBase + ip->SrcOffset + ip->Operand2);
    break;

case OpCodeREnum.Ldfld_R4_Inline:
    *(float*)(frameBase + ip->DstOffset) =
        *(float*)(frameBase + ip->SrcOffset + ip->Operand2);
    break;
// ... I1/I2/U1/U2/U4/U8/R8 analogous ...
```

Alignment (§1.2) guarantees these casts are naturally aligned, so no
`ReadUnaligned`. (If alignment is deferred as a risk-reducer, the
implementer MAY use `Unsafe.ReadUnaligned<T>`/`WriteUnaligned` initially and
drop it once alignment lands — but the spec target is aligned direct casts.)

Stfld mirrors — note operand-direction swap (Stfld writes *into* the VT field
from a source temp):

```csharp
case OpCodeREnum.Stfld_I4_Inline:
    *(int*)(frameBase + ip->DstOffset + ip->Operand2) =
        *(int*)(frameBase + ip->SrcOffset);
    break;
// ... I1/I2/I8/U*/R4/R8 analogous ...
```
(`DstOffset` = owning VT slot byte offset for Stfld; `SrcOffset` = value
temp byte offset. This mirrors the heap Stfld arm's use of `ip->DstOffset`
as the object slot at ~1649-1688.)

Reference kind — operates on the frame ref region:

```csharp
case OpCodeREnum.Ldfld_Ref_Inline:
    // ip->Operand = absolute frame-ref index (slot.RefOffset + field.ReferenceOffset)
    obj = mStack[frameRefBase + ip->Operand];
    dstIdx = frameRefBase + ip->Operand;        // reuse same ref slot for the dest temp
    // (dest temp's own RefOffset is encoded instead if different — see note)
    *(int*)(frameBase + ip->DstOffset) = obj != null ? dstIdx : -1;
    break;

case OpCodeREnum.Stfld_Ref_Inline:
    srcIdx = *(int*)(frameBase + ip->SrcOffset);
    mStack[frameRefBase + ip->Operand] = srcIdx >= 0 ? mStack[srcIdx] : null;
    break;
```

Note on the Ldfld_Ref_Inline destination: a reference value must land in a
destination temp that itself has a ref slot. The implementer encodes the
**destination temp's RefOffset** in a spare operand (e.g. `Operand3`) so the
loaded object is also installed in the dest temp's ref slot (otherwise the
mStack entry could be truncated on frame exit). The heap `Ldfld_Ref` arm
(~1640-1646) has the identical concern and uses `frameRefBase + ip->Operand`
as the dest ref slot — the `_Inline` variant follows the same convention,
encoding the dest RefOffset in `Operand` and the field's source ref index in
`Operand3`. (Exact operand split is an implementation detail; the contract is:
both the source field ref index and the destination temp ref index are
available to the arm.)

## 3. Initobj memset for in-frame VTs

`Initobj` at ILIntepreter.Neo.cs ~1509 already does `Unsafe.InitBlock(
frameBase + ip->DstOffset, 0, sz)` for the primitive region of an in-frame IL
value type when `refCnt == 0`. Step 12 completes it:

Replace the `throw` at ~1533:

```csharp
if (refCnt > 0)
{
    // Zero the VT's ref-offset slots to null. ip->DstOffset is the VT slot
    // byte offset; the slot's RefOffset must be available here.
    int slotRefOffset = /* resolved from ip (see below) */;
    for (int i = 0; i < refCnt; i++)
        mStack[frameRefBase + slotRefOffset + i] = null;
}
```

The slot's `RefOffset` is not currently encoded on the `Initobj` instruction.
Two options for the implementer (pick one, document in tasks):
- **(A)** Have the optimizer's offset-lowering pass stamp the slot's
  `RefOffset` into a spare `Initobj` operand (e.g. `Operand3`) at lowering
  time, parallel to how it lowers `Register1` → `DstOffset`. This is the
  clean option and matches the existing lowering pattern.
- **(B)** Resolve `RefOffset` at runtime via `DstOffset` → slot lookup. Slower
  and against the Neo "compile-time-known offsets" principle; not preferred.

The implementer should use (A). The Initobj JIT emission must therefore also
record the target slot so the lowering pass can fill `Operand3`.

The CLR value-type `Initobj` branch (~1539, `throw ... Step 13`) is
**unchanged** — CLR VT Initobj is Step 13.

The reference-type `Initobj` branch (~1526-1528, writes -1) already handles
the "Initobj on a reference-typed local" case and needs no change beyond
ensuring its ref slot (if any) is nulled — but a reference local has
`RefCount == 1` and is handled by the existing frame-entry ref-slot init, so
no `Initobj` work is needed there.

## 4. JIT rule: emit `_Inline` when the operand slot is an in-frame VT

### 4.1 Where the decision is made

`Code.Ldfld` / `Code.Stfld` in `JITCompiler.Translate` (JITCompiler.cs
~1853-1896). Today both cases unconditionally call
`GetLdfldCodeForType(fieldType)` / `GetStfldCodeForType(fieldType)` when
`type is ILType`. That emits the heap variant — wrong when the **operand
register** is an in-frame VT slot.

### 4.2 The discriminator

`GetFieldOffset(token, declaringType, method, out type, out fieldType)`
returns `type` = the field's **declaring ILType** and `fieldType` = the field
type. `type` is the same ILType whether the field access is on a heap
instance or an in-frame VT — it is NOT the discriminator.

The correct discriminator is the **operand register's value-category** at
this IL site:
- If `Register2` (Ldfld operand) / `Register1` (Stfld operand) is a slot
  whose value-category is "in-frame value type" (a VT local, a VT temp that
  holds a VT, a VT parameter) → emit `_Inline`.
- If it is a reference slot (an mStack index — heap ILTypeInstance, CLR
  object, boxed VT) → emit the existing heap `Ldfld_*`/`Stfld_*`.

Value-category is known to the JIT during `Translate` because the stack-
simulation register allocation tracks each register's static type (the same
information `AllocateLocalStackSpaces` later consumes). The implementer
threads the operand register's resolved `IType` into the Ldfld/Stfld case and
tests:

```csharp
bool operandIsInFrameVt =
    operandType is ILType ot && ot.IsValueType && !ot.IsEnum;
```

(an enum operand is its underlying primitive and is not addressed as a VT;
boxing/`Ldfld_Value` paths stay heap.)

When `operandIsInFrameVt`, the emitted opcode is
`GetLdfldCodeForType(fieldType, inline: true)` (overload adds a bool, or a
sibling `GetLdfldInlineCodeForType`). The field offset operands are the same
(`Operand2 = field.PrimitiveOffset`, `Operand3 = field.ReferenceOffset`);
the meaning of `Register2`/`Register1` shifts from "mStack index slot" to
"VT slot byte offset", which the existing `LowerNeoOffsets` pass already
handles because it lowers every `Register*` to its slot's `Offset`
uniformly — the `_Inline` arm simply consumes `SrcOffset`/`DstOffset` as the
VT base instead of dereferencing it as an mStack index.

For `Ldfld_Ref_Inline`/`Stfld_Ref_Inline` the JIT must additionally stamp
the source field's absolute frame-ref index
(`slot.RefOffset + field.ReferenceOffset`) and, for Ldfld, the dest temp's
`RefOffset`. These ref indices are only known after
`AllocateLocalStackSpaces`, so the JIT emits placeholder operands here and
the optimizer's Neo ref-lowering (already responsible for stamping ref
offsets onto heap `Ldfld_Ref`/`Stfld_Ref`, Optimizer.Neo.cs ~305-348) fills
them — extended to recognize the `_Inline` opcodes.

### 4.3 What stays unchanged

Heap-object `Ldfld_*`/`Stfld_*` (operand is a reference slot): opcodes,
encoding, and `ExecuteNeo` arms (~1600-1688) are untouched. Step 12 only
adds the `_Inline` siblings and the branch that selects them.

## 5. Optimizer frame-allocation changes

Three concrete edits in `Optimizer.Neo.cs` (+ the shared
`AllocateLocalStackSpaces` / `AllocateSlotForType` in `JITCompiler.cs`):

1. **Alignment** (§1.2): every `offset += size` that assigns a slot offset
   is preceded by `offset = AlignUp(offset, t.NaturalAlignment)`. Touch
   points: `AllocateSlotForType` (~1230-1259), the VT-local branch (~1133-
   1146), the temp-register loop (~1195-1205, align by max alignment), and
   `AllocateNeoCallParamSlot` (~563-). `NaturalAlignment` is added to `ILType`.

2. **Register value-category propagation**: the optimizer already lowers
   `Register1/2/3` to byte offsets via `LowerNeoOffsets` / `LowerR1`,
   `LowerR1R2`, `LowerR1R2R3` (~556-686). It does not currently need to know
   whether a register is a VT slot or a ref slot because the heap arms always
   treat the operand as an mStack index. Once `_Inline` opcodes exist, the
   **JIT** has already chosen the opcode (§4), so the optimizer does not need
   to re-derive value-category — it only needs to keep lowering offsets
   uniformly, which it already does. The one new responsibility is stamping
   the absolute frame-ref indices for the `_Inline` Ref variants (§4.2),
   extending the existing ref-offset stamping at ~305-348.

3. **BCP/FCP safety**: these passes operate on the `CodeBody` register-index
   form (before `LowerNeoOffsets`) and are agnostic to operand kind. Adding
   new opcodes does not perturb them as long as the new opcodes' source/dest
   registers participate in the same liveness/copy-elision analysis. The
   implementer must register the new opcodes in
   `Optimizer.GetOpcodeDestRegister` / `GetOpcodeSourceRegister` (the helpers
   `Translate` uses at JITCompiler.cs ~99-101) so BCP/FCP see them. This is
   the single most important optimizer touch-point and the most likely
   regression source — the NeoStep smoke (not just NeoStep12) is the guard.

## 6. Edge cases

### 6.1 Nested value type (VT field of VT)
`struct Inner { int x; } struct Outer { Inner i; int y; }` — accessing
`outer.i.x`. `ILType.InitializeFields` (~2096-2104) already accumulates
nested-VT primitive/ref offsets into the outer VT's `ILTypeFieldOffset`, so
`field.PrimitiveOffset` of `x` is its absolute offset within `Outer`. The
`_Inline` arm computes `frameBase + outerSlot.Offset + x.PrimitiveOffset` —
no recursion, no intermediate. Validated by a NeoStep12 nested-VT test.
(Whole-`Inner` copy `Inner i = outer.i;` is Step 12b.)

### 6.2 VT with reference fields
`struct S { int a; string b; }` — `a` is at `PrimitiveOffset 0` in the byte
region; `b` is at `ReferenceOffset 0` in the slot's ref region. `s.a` uses
`Ldfld_I4_Inline` (byte region); `s.b` uses `Ldfld_Ref_Inline` (ref region).
`Initobj` on `s` zeroes the 4 bytes AND nulls the one ref slot (§3).

### 6.3 VT as a field of a heap object
`class C { Vector3 v; }` then `c.v.x`. Here the operand (`c`) IS a reference
slot → heap `Ldfld_Value`/the existing path applies for `c.v`, and `v` lives
inside `C`'s `Primitives`. This is unchanged by Step 12 — it already works via
the heap arms. Step 12 only changes the case where the operand itself is an
in-frame VT.

### 6.4 Alignment / padding
A VT containing a `long` after a `byte` gets 7 bytes of padding before the
`long` (matching CLR `StructLayout.Sequential`). The frame slot for such a
VT is aligned to 8. The `TotalPrimitiveSize` already includes this padding
(it mirrors CLR layout via `InitializeFields`), so `slot.Size` is correct;
only the **slot's own start offset** needs the new `AlignUp`.

### 6.5 Overlap with Step 7 ref-offset lowering
The existing heap `Ldfld_Ref`/`Stfld_Ref` arms (~1640-1646, 1684-1687) and
their optimizer stamping (~305-348) already implement "absolute frame-ref
index in `Operand`". The `_Inline` Ref variants reuse that exact convention
and the same stamping code path, so there is no new ref-offset *mechanism* —
only a new consumer of it. No conflict with Step 7's design.

### 6.6 VT passed/returned by value
Passing/returning a whole VT is a copy → Step 12b (`Move_Vt`). Step 12
deliberately does NOT make `Call`/`Ret` copy VTs by value. Any NeoStep12 test
that appears to need pass-by-value must instead test field-level access only
(and rely on Step 8's existing pointer-passing of VT *references* within a
frame, or be deferred to 12b). The implementer must not silently widen Call
/Ret — that is 12b scope.

## 7. Explicit NON-GOALS (do not bleed scope)

- **Whole value-type copy / assignment** (`Vector3 a = b;`, VT by-value
  param/return) = `Move_Vt` + JIT `LowerMove` pass = **Step 12b**. Step 12
  implements field access only.
- **CLR value-type Box / Unbox** and **CLR value-type `Initobj`** (with/without
  `ValueTypeBinder`) = **Step 13**. The `Initobj` CLR branch stays a throw.
- **`constrained.` callvirt** on value types = **Step 13/18**.
- **IL value-type `newobj`** (constructing a VT via `new`) = **Step 18**
  (needs Ref Slot for `this`). Step 12 does not add it.
- **`Ldfld_Value` / `Stfld_Value` inline** (loading a whole VT field as a VT)
  = part of 12b's copy story; out of scope.
- **Legacy path** (`ValueTypeObjectReference`/`AllocValueType`): untouched.
  All Step 12 code is `#if ENABLE_NEO_MODE`.

## 8. Symbols / files touched (summary)

- `OpCodeREnum.cs` — +22 opcodes (§2.1).
- `ILIntepreter.Neo.cs` — +22 case arms (~after line 1688); `Initobj` ref
  sub-case (~1532-1535).
- `JITCompiler.cs` — `Code.Ldfld`/`Code.Stfld` discriminator (~1853-1896);
  inline code selectors; `AllocateLocalStackSpaces`/`AllocateSlotForType`
  alignment (~1070-1261); register the new opcodes in
  `GetOpcodeDestRegister`/`GetOpcodeSourceRegister` usage.
- `Optimizer.Neo.cs` — ref-index stamping for `_Inline` Ref variants
  (~305-348); confirm `LowerNeoOffsets` handles the new opcodes (it lowers
  uniformly, so likely just registration).
- `ILType.cs` — `NaturalAlignment` property (computed in `InitializeFields`).
- `TestCases/NeoStep12Test.cs` — new.
