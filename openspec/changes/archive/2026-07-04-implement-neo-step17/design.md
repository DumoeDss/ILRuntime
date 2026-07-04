# Design -- implement-neo-step17 (Ref/Out + ldloca/ldflda + stind/ldind)

Grounded in the current code. All file:line references are to the branch state
at propose time (HEAD = `e3fa8ef2`, Steps 1-16 + OPT-HARDEN landed).

## 0. The shape of the problem

Neo currently has **two halves** of the byref story that do not meet:

1. **Step 12 in-frame-VT fast path (exists, green).** For the C#-idiomatic
   `ldloca V; ldflda f; stfld/ldfld/initobj` pattern on an in-frame value type,
   `Optimizer.Neo.cs:LowerNeoOffsets` builds an `addrAlias` map (struct
   `NeoAddressAlias { Reg; Offset }`, helpers `ResolveAddressAlias`,
   `Optimizer.Neo.cs:32-78, 882-893`) that folds the address chain to a
   compile-time absolute frame offset. The leaf `_Inline` field opcodes and
   `Initobj` consume that folded offset. Because of this, the `ExecuteNeo` arms
   for `Ldloca`/`Ldloca_S` and `Ldflda` are **runtime no-ops** today
   (`ILIntepreter.Neo.cs:503-524`). This fast path covers the vast majority of
   real `ldloca`/`ldflda` emissions and is zero-overhead. It MUST NOT regress.

2. **Genuine byref (missing).** When the address *escapes* the folding window --
   passed to a `ref`/`out` param, stored through `stind`/`ldind`, captured by
   `fixed`, or read by `stobj`/`ldobj` -- there is no runtime value. The arm is
   a no-op that leaves the dest temp uninitialized, so the consumer reads junk.

Step 17's job is to add the genuine-byref half as a **real runtime value** (the
8-byte Ref Slot) WITHOUT disturbing half 1. The central design decision is
sec "addrAlias reconciliation" below.

The Legacy reference (`ILIntepreter.Register.cs`) carries the *semantics* --
what to dispatch on (heap VT ref vs in-frame address vs CLR field vs array
element) -- via the `ObjectTypes` discriminator (`StackObjectReference`,
`FieldReference`, `ArrayReference`, `ValueTypeObjectReference`). Neo reshapes
that discriminator into the `(objectIndex, offset)` pair. Legacy is NOT
modified.

## 1. Ref Slot representation and frame storage

### 1.1 The 8-byte slot

A Ref Slot is two consecutive `int`s in the frame byte region:

```
*(int*)(frameBase + slot.Offset + 0) = objectIndex;   // -1 = frame native
*(int*)(frameBase + slot.Offset + 4) = offset;        // meaning depends on objectIndex
```

- `objectIndex == -1`: a **frame-native** address. `offset` is an absolute
  frame byte offset; `*(T*)(frameBase + offset)` is the referent. (e.g. an
  in-frame local, an in-frame VT field, an in-frame param.)
- `objectIndex >= 0`: an **mStack object** address. `offset` is a byte offset
  **into the object's `Primitives`** (for an IL heap field or array element) or
  a CLR field hash (deferred -- see sec 6). `mStack[frameRefBase + ...]` indexing
  does NOT apply here: `objectIndex` is the mStack index itself as stored by
  the field/array opcodes (the Neo "ref slot holds an mStack index" convention,
  identical to how `Ldfld_Ref` writes `*(int*)(frameBase + slot.Offset) =
  mStackIdx`). The Ref Slot therefore reuses the *same* index value the heap
  field opcodes already produce; it just generalizes "4-byte mStack index" into
  "8-byte (index, intra-object offset)".

### 1.2 Storage and sizing

A byref-typed local/param/temp is allocated **8 bytes** by `AllocateSlotForType`
(`JITCompiler.cs:1513`) and `AllocateNeoCallParamSlot`
(`Optimizer.Neo.cs:835`): `Size = 8`, `RefCount = 0`, aligned to 4 (the slot's
own alignment is 4; the referent's natural alignment is irrelevant to the slot
itself). Detection: the declared type's `IsByRef` is true
(`ILType.IsByRef` `ILType.cs:1111`, `CLRType.IsByRef` `CLRType.cs:255`).

`AllocateSlotForType` currently keys off `IsPrimitive` / `IsValueType` /
else-reference. Add a byref branch FIRST (before the primitive branch):

```csharp
if (t.IsByRef)
{
    offset = AlignUp(offset, 4);
    slot.Offset = offset; slot.RefOffset = refOffset;
    slot.Size = 8; slot.RefCount = 0;     // pure-bytes; no mStack ref of its own
    offset += 8;
    return slot;
}
```

`AllocateNeoCallParamSlot` (`Optimizer.Neo.cs:835-874`) mirrors this: a byref
param is `Size = 8`, contiguously (no alignment -- that helper's contract is
contiguous `offset += size` to match the autogen CLR binding `ReadNeo*`
readers, per its own NOTE).

### 1.3 A subtlety: ref fields of an in-frame VT

An in-frame VT does not store its ref fields inside its byte range -- they live
in the frame's parallel ref region under the slot's `RefOffset` (Step 12 design
sec 1.3). A Ref Slot addressing a **ref field** of an in-frame VT therefore needs
the frame-ref index, not a byte offset. Resolution: for the frame-native case
we reserve `objectIndex == -1, offset < 0` to mean "frame-ref index
`~offset`" (i.e. the ref-region slot). This keeps the 8-byte encoding uniform;
the `stind_ref`/`ldind_ref` arms check `objectIndex == -1 && offset < 0`. (This
sub-case is rare -- `ref` to a VT's reference field -- but the encoding must not
silently mis-handle it. Tests cover it if a smoke case naturally needs it;
otherwise it is a documented edge case, not a silent bug.)

## 2. Address-producing arms

### 2.1 `Ldloca` / `Ldloca_S` -- real arm, coexisting with folding

Today (`ILIntepreter.Neo.cs:503-514`) the arm is `break;` (no-op). Step 17
replaces the no-op with a real producer, but the **addrAlias folding still runs
in the optimizer and resolves the consumers of an ldloca that the folding
window covers** (see sec "addrAlias reconciliation"). Concretely:

- When the optimizer's folding resolves ALL consumers of an `ldloca` dest (the
  dest only feeds `_Inline` field ops / `Initobj`), the dest is never read at
  runtime -- the real arm is dead for that instruction but harmless to execute.
- When ANY consumer is not foldable (a `stind`/`ldind`, a byref `Call` param, a
  `stobj`/`ldobj`, a `fixed`), the optimizer must NOT fold that dest (leave it
  out of `addrAlias`), and the real arm produces the Ref Slot.

Real arm:

```csharp
case OpCodeREnum.Ldloca:
case OpCodeREnum.Ldloca_S:
    {
        // ip->SrcOffset = the addressed local's frame byte offset (lowered
        // from Register2). ip->DstOffset = dest temp byte offset (8 bytes).
        // For a value-type local the "address" is frame-native.
        // For a reference-typed local, ldloca is illegal in verifiable IL;
        // a `ref` to a byref-typed local is not produced by the C# compiler.
        int dst = ip->DstOffset;
        *(int*)(frameBase + dst + 0) = -1;            // frame-native
        *(int*)(frameBase + dst + 4) = ip->SrcOffset; // absolute frame offset
    }
    break;
```

The optimizer's lowering for `Ldloca` (`Optimizer.Neo.cs:459-472`) already sets
`DstOffset = localInfos[R1].Offset; SrcOffset = localInfos[R2].Offset;` -- that
is exactly what the real arm needs. No new lowering is required for the arm
itself; only the **folding-gate** logic in sec "addrAlias reconciliation" changes.

### 2.2 `Ldarga` / `Ldarga_S`

New arms (currently these opcodes have no ExecuteNeo case at all -> fall to the
default NIE). Identical to `Ldloca`: the param is an in-frame slot, so the arm
produces `(-1, paramFrameOffset)`. The JIT already emits `Register1/2`
(`JITCompiler.cs:2042-2050`); the optimizer's `Ldarga` case
(`Optimizer.Neo.cs:461-462`) already lowers R1/R2 the same way as Ldloca.

### 2.3 `Ldflda` -- per operand kind

`Ldflda`'s JIT emission (`JITCompiler.cs:2175-2193`) already stamps
`Operand = type.GetHashCode()`, `Operand2 = field.PrimitiveOffset`,
`Operand3 = field.ReferenceOffset`. The arm dispatches on the operand's value
category at runtime (the operand slot's content distinguishes in-frame-VT from
heap-IL):

```csharp
case OpCodeREnum.Ldflda:
    {
        int dst = ip->DstOffset;
        int operandSlotOff = ip->SrcOffset;   // R2 lowered
        // If the operand is an in-frame VT, the optimizer's folding either
        // resolved this ldflda's consumers (no-op, harmless) OR left it real.
        // For the real case the operand is frame-native ONLY when it was
        // produced by an ldloca/ldarga whose dest we did not fold. We encode
        // that by checking a marker the optimizer stamps: ip->Operand4 = 1
        // means "operand is an in-frame VT address (frame-native)".
        int fieldPrimOff = ip->Operand2;
        if (ip->Operand4 == 1)
        {
            // in-frame VT field: operand is a frame byte offset (the VT base)
            int vtBase = *(int*)(frameBase + operandSlotOff + 4); // from ldloca's offset half
            *(int*)(frameBase + dst + 0) = -1;
            *(int*)(frameBase + dst + 4) = vtBase + fieldPrimOff;
        }
        else
        {
            // heap IL field: operand slot holds an mStack index
            int objIdx = *(int*)(frameBase + operandSlotOff);
            *(int*)(frameBase + dst + 0) = objIdx;
            *(int*)(frameBase + dst + 4) = fieldPrimOff;
            // CLR field (objIdx is a CLR object) -> offset is a field hash;
            // DEFERRED: stind/ldind on this throws Step-17 NIE (see sec 6).
        }
    }
    break;
```

The optimizer stamps `ip->Operand4 = 1` for the in-frame-VT case when it
decides NOT to fold a particular `ldflda` (because a downstream consumer
escaped). For the folded case the optimizer leaves the dest out of `addrAlias`
consumption and the arm is dead. (The exact operand choice is an implementation
detail; the contract is "the arm knows whether its operand is a frame-native
address or an mStack index.")

### 2.4 `ldelema` (D-LDELEMA)

`Ldelema` (`JITCompiler.cs:1943` register allocation: R1 = dest, R2 = array,
R3 = index) produces `(arrayMStackIdx, elementByteOffset)`. The element byte
offset is `index * elementSize` (for a contiguous IL-VT array stored as an
`ILTypeInstance[]` with per-element `Primitives`) -- or, for a CLR primitive
array, the runtime computes the offset via the CLR array's element layout. New
arm:

```csharp
case OpCodeREnum.Ldelema:
    {
        int dst = ip->DstOffset;
        int arrIdx = *(int*)(frameBase + ip->SrcOffset);    // R2 = array mStack idx
        int elementIdx = *(int*)(frameBase + ip->OperandOffset); // R3 = index
        // elementSize + array-kind resolved at runtime from the array's CLR
        // type (mirrors Step 16 Ldelem/Stelem which already do this).
        int elemByteOff = ResolveElementByteOffset(arrIdx, elementIdx, /*out kind*/);
        *(int*)(frameBase + dst + 0) = arrIdx;
        *(int*)(frameBase + dst + 4) = elemByteOff;
    }
    break;
```

Its only consumers are `stind_*`/`ldind_*` (D-LDELEMA's whole rationale, per
`neo-deferred-items.md` sec D-LDELEMA). `fixed` and `ref`/`out` array-element are
deferred non-goals.

## 3. stind_* / ldind_* / stobj / ldobj dispatch

### 3.1 The dispatch shape

Each `stind_*`/`ldind_*` arm reads the Ref Slot (the address operand) and
dispatches on `objectIndex`:

```csharp
// Ldind_I4 shown; the I1/I2/I8/U1/U2/U4/U8/R4/R8/Ref mirrors follow.
case OpCodeREnum.Ldind_I4:
    {
        int addrSlot = ip->SrcOffset;          // R2 = address temp
        int objIdx  = *(int*)(frameBase + addrSlot + 0);
        int off     = *(int*)(frameBase + addrSlot + 4);
        int value;
        if (objIdx == -1)
        {
            if (off < 0) value = /* ref-region: read mStack index at ~off */ -1;
            else value = *(int*)(frameBase + off);
        }
        else
        {
            var o = mStack[objIdx];
            if (o is ILTypeInstance ili)
                value = *(int*)(ili.PrimitivesFixedPtr() + off);   // pinned
            else
                throw new NotImplementedException("Step 17: CLR-object ldind via field hash (deferred to Step 13b)");
        }
        *(int*)(frameBase + ip->DstOffset) = value;
    }
    break;
```

`Stind_*` mirrors (writes instead of reads, R1 = address, R2 = value).

### 3.2 ILTypeInstance pinned-Primitives access

`ILTypeInstance.Primitives` is a `byte[]`. Reading/writing through a managed
array pointer requires pinning. The heap `Ldfld_*` arms already access
`Primitives` indirectly via the field offset; Step 17 needs the *general*
"given (mStackIdx, intra-Primitives offset), read/write a typed value"
helper. Two viable implementations (pick one in tasks; the design target is
(a)):

- **(a) `fixed`-pinned helper** -- a private `unsafe` helper that does
  `fixed (byte* p = ili.Primitives)` and returns `p + off` for the duration of
  the single read/write. Safe because the pin spans only the one memory op.
- **(b) `Unsafe.ReadUnaligned`/`WriteUnaligned`** on `ili.Primitives` at `off`
  -- no explicit pin, framework handles it. Slightly slower but simpler and
  avoids a `fixed` per access. Acceptable for a first cut; the fast path
  (frame-native) is unaffected.

`stind_ref`/`ldind_ref` on an ILTypeInstance target additionally touch the
object's `ManagedObjects` (a ref field inside the object): when `off` addresses
a ref field, the arm uses the field's `ReferenceOffset` rather than the byte
offset. To keep the encoding uniform, a Ref Slot produced by `Ldflda` of an
**IL ref field** encodes the ref-field index in the negative-offset convention
(sec 1.3) -- but since `objectIndex >= 0` there, use a sentinel: `off ==
int.MinValue` flags "this is a ManagedObjects index = `~off`". (Documented
edge case; covered by a smoke test if natural, otherwise explicit.)

### 3.3 stobj / ldobj

`Stobj`/`Ldobj` (`JITCompiler.cs:2097-2102`, `2152-2156`) carry the type token
in `Operand`. They are the value-type-sized generalizations of stind/ldind:
they copy `type.TotalPrimitiveSize` bytes (+ `TotalReferenceCount` ref slots)
through the Ref Slot. Same dispatch as stind/ldind (frame-native memcpy vs
ILTypeInstance pinned copy), just sized by the token instead of a fixed 1/2/4/8.
Reuse `Move_Vt`'s CopyBlock+ref-loop shape (Step 12b).

## 4. Byref call-ABI

### 4.1 Caller side (call lowering)

`LowerNeoOffsets` builds `NeoCallParamMap` per call site
(`Optimizer.Neo.cs:750-792`) from `paramInfos` (callee signature). Today
`AllocateNeoCallParamSlot` does NOT branch on `IsByRef` -- a byref param is
sized by its `TypeForCLR` (which strips byref), so it is currently mis-sized
as the referent's size. Fix: add the byref branch (sec 1.2) so a byref param is
8 bytes in BOTH `paramInfos[dstIndex]` (callee layout) and the source register
(`localInfos[srcRegs[p]]` must also be an 8-byte byref slot -- the caller's
`ldloca`/`ldflda`/`ldelema` produced it).

The param-copy loop (`Optimizer.Neo.cs:758-776`) then naturally copies 8 bytes
(`dstInfo.Size == 8`, one `primSrc/primDst/primSize` entry of size 8, no ref
entries) -- i.e. it copies the **Ref Slot value**, not the referent. No new
copy code is needed for the IL-method-call case once sizing is correct; the
existing primitive-copy path handles the 8-byte slot as one opaque block.

### 4.2 Callee side

The callee's parameter slot is an 8-byte byref slot (sec 1.2). When the callee
does `ldarg.REF` (a `Ldarg` of a byref param) it gets a `Move` of the 8-byte
slot into a temp -- the temp is itself an 8-byte byref slot, holding the same
`(objectIndex, offset)`. Subsequent `stind`/`ldind`/`stobj`/`ldobj` on that
temp dispatch per sec 3. **Mutations propagate to the caller's frame/object** --
that is the `ref`/`out` contract. For `out`, the caller's referent is
uninitialized-on-entry and the callee must write before read; the ABI is
identical (the slot is just a pointer), the difference is a verification
concern, not a runtime one.

### 4.3 CLR-method byref params

A CLR `ref`/`out` parameter crosses the IL->CLR boundary: the autogen CLR
binding redirect reads the param via `ReadNeo*`. Today those readers assume a
contiguous by-value layout (Step 13b territory, `neo-deferred-items.md`
D-13B). Passing a byref to a CLR method correctly requires the binding to read
an 8-byte Ref Slot and resolve it to a real CLR `ref` -- this is **deferred to
Step 13b** (it is the same unified-param-layout work D-13B area 5 already
owns, and K2/K2-FAM live there). For Step 17, a CLR-method byref param throws
a Step-17/13b-tagged NIE; the IL-method byref path is the green target.

## 5. addrAlias reconciliation (the central decision)

**Decision: COEXIST, not replace.** The Step 12 `addrAlias` folding stays as
the fast path for the pure `ldloca;[ldflda;]stfld/ldfld/initobj` in-frame-VT
pattern; the real Ref Slot is the fallback for genuine byref (escaped address)
use.

### 5.1 Why coexist (and why NOT replace)

- **The folding is strictly better for the fast path.** It turns an address
  chain into a compile-time absolute frame offset consumed by a zero-branch
  `_Inline` arm. Replacing it with "always produce a real Ref Slot, always
  dispatch at runtime" would (a) add an 8-byte temp + a runtime
  `(objectIndex,offset)` decode per field access, and (b) force every
  `_Inline` field op to instead go through stind/ldind dispatch -- a regression
  for the single most common `ldloca` emission.
- **The folding is sound only within its window.** It tracks `ldloca`/`ldflda`
  dests and folds them when their consumers are also-foldable. It does NOT
  (and cannot, without a real value) handle a dest whose consumer is a byref
  `Call` param, `stind`/`ldind`, `stobj`/`ldobj`, `ldelema`, or `fixed`.
- **Coexistence is a single, well-localized change:** gate the folding on
  consumer foldability. Today `Optimizer.Neo.cs:32-78` adds *every* in-frame-VT
  `ldloca`/`ldflda` dest to `addrAlias` unconditionally. Step 17 changes that to
  add a dest to `addrAlias` ONLY IF every consumer of that dest is one of the
  foldable opcodes (`_Inline` field ops, `Initobj`, or another `ldflda` whose
  own dest is foldable). If any consumer is a byref-escape opcode, the dest is
  left real (the real arm produces the Ref Slot at runtime).

### 5.2 Why it does not regress Steps 12-16

- **Steps 12/12b/13 inline field access** depends on the folding ONLY for the
  pure in-frame-VT pattern, which remains fully foldable (its consumers are
  all `_Inline`/`Initobj`). The gate keeps folding exactly those cases. The
  K1 `ldloca-kill` in FCP (`Optimizer.FCP.cs`) keys off the `Ldloca` opcode
  itself (kills a copy-prop when its `xSrc`/`xDst` is addressed by an ldloca);
  that logic is unchanged -- taking an address is still a potential-mutation
  escape whether the address is later folded or real.
- **Step 16 arrays** -- `ldelema` is new; its Ref Slot is a real value by
  construction (no prior folding to disturb).
- **Step 14 exceptions** -- unaffected (no address machinery).
- **Step 15 type checks** -- unaffected.

The full 72-case NeoStep smoke is the regression gate. The risk concentrates in
`Optimizer.Neo.cs:32-78` (the folding-gate edit) and the new real arms; both
are additive -- the gate only ever *removes* a dest from folding (making it
real), never adds folding where there was none.

### 5.3 The foldability gate (concrete)

In `LowerNeoOffsets`, after the first alias-building pass (`Optimizer.Neo.cs:34-78`),
add a **consumer-scan** pass: for each candidate alias dest `d`, scan the body
for `Register1/2/3 == d` (and the Ldflda/Stfld operand conventions). If every
occurrence is a foldable opcode, keep the alias; if any occurrence is a
byref-escape opcode (`stind_*`, `ldind_*`, `stobj`, `ldobj`, `ldelema`, a
`Call`/`Newobj`/`Push` param whose declared param `IsByRef`, `Constrained`'s
box path), remove `d` from `addrAlias` (and any alias that inherits through
`d`). Removing an alias makes its consumers read the real Ref Slot via the
new arms -- but the consumers were emitting NIE/junk before, so they could not
have been green. (If a smoke case that was green turns red, that case was
silently relying on the no-op arm leaving a dest uninitialized -- itself a
latent bug surfaced by Step 17, to be triaged, not papered over.)

## 6. constrained. on value type (D-CONSTRAINED)

Today the JIT detects a preceding `Constrained` and **moves it to AFTER the
callvirt** (`JITCompiler.cs:1765-1829`), stamping `op.Operand4 = 1` on the call
and `old.Operand2 = op.Operand2` (the dispatch slot) on the Constrained opcode,
with the constrained type token in `Operand`. There is NO runtime `Constrained`
arm -- it is dead bytes today, and `needInline = canInline && !hasConstrained`
forces the callvirt to NOT inline.

Step 17 adds the runtime `Constrained` arm (executed after the callvirt has
produced its result). For `constrained.callvirt T.M` where the constrained
type `T` is a value type and `M` is an overrideable method:

- If `T` is a value type and `M` is NOT boxed-required (a struct method that
  does not require boxing): the callvirt already dispatched to the concrete
  method; the Constrained arm is a no-op.
- If `M` requires boxing (e.g. `T.ToString()` overriding `Object.ToString`):
  the callvirt's `this` was the VT's address (a Ref Slot the byref model now
  produces via `ldarga`/`ldloca`); the Constrained arm performs the box-once
  using the constrained type token and the address, so the dispatched method
  sees a boxed `this`.

This is the most subtle sub-part. The design target: implement the **common
VT-box case** (`struct.ToString()` / `IEquatable<T>`-style) and leave the rare
interface-on-VT-constrained cases to a Step-13b-or-later completion if they
need more than the byref model provides. State explicitly in tasks which
constrained sub-cases are green vs NIE.

## 7. Optimizer lowering summary (new work in `Optimizer.Neo.cs`)

1. **Byref sizing** in `AllocateNeoCallParamSlot` (sec 1.2) -- add the byref
   branch.
2. **`Ldloca`/`Ldarga`/`Ldflda`/`Ldelema` lowering** -- already lower R1/R2 to
   byte offsets (`Optimizer.Neo.cs:459-472`); for `Ldflda` add the
   `Operand4` frame-native marker when the dest is real-and-in-frame-VT; for
   `Ldelema` lower R3 (index) into `OperandOffset` (mirrors Step 16 Ldelem).
3. **Foldability gate** (sec 5.3) -- the consumer-scan that decides which
   `ldloca`/`ldflda` dests stay folded vs become real.
4. **Byref param-map entries** -- already handled by the existing
   primitive-copy loop once sizing is 8 bytes (sec 4.1); verify no ref-entry is
   emitted for a byref param (`RefCount == 0`).
5. **Register the new opcodes' source/dest registers** with BCP/FCP
   (`GetOpcodeDestRegister`/`GetOpcodeSourceRegister`) so the optimizer's
   liveness/copy-elysis sees them -- same requirement Step 12 called out. The
   `ldloca-kill` (K1) already treats `Ldloca` as an escape; verify `Ldflda`/
   `Ldelema` (new real producers) are also treated as escapes by the kill if
   they address a propagation source/dest.

## 8. Edge cases

- **Ref to an in-frame VT field** (`ref` to `outer.inner.x`): the address is
  frame-native `(-1, outerOffset + x.PrimitiveOffset)`; works via sec 2.1 + sec 5.3.
- **Ref to a heap-IL field** (`ref obj.field`): `(objIdx, fieldPrimOff)`; the
  callee's stind/ldind dispatches to the pinned-Primitives path.
- **`out` param**: same ABI as `ref`; the slot is just a pointer. The callee
  writes before read by contract; runtime enforces nothing extra.
- **Byref param passed onward** (`void Outer(ref int x) { Inner(ref x); }`):
  the callee's `ldarg` of `x` (8-byte slot) is `Move`d to a temp, then passed
  as Inner's byref param -- the 8-byte copy preserves `(objIdx, off)`.
- **`stobj`/`ldobj` on a boxed VT** (object on mStack): dispatches to the
  ILTypeInstance pinned path; matches Legacy `Ldobj`'s
  `StackObjectReference`/`FieldReference` cases.
- **Ref-field-of-in-frame-VT negative-offset sentinel** (sec 1.3, sec 3.2):
  documented encoding; smoke-covered if natural.

## 9. Explicit NON-GOALS

- **CLR-object stind/ldind via field hash** (`(objIdx, fieldHash)`): DEFERRED
  to Step 13b (the field-hash plumbing is revisited there with the unified
  param layout). Throws Step-17-tagged NIE.
- **CLR-method `ref`/`out` params** (IL->CLR byref crossing): DEFERRED to Step
  13b (D-13B area 5 owns this; K2/K2-FAM live there). NIE.
- **Generic-byref** (`ref T`, `T` generic param): DEFERRED. The closed-type
  byref ABI is the green target.
- **Explicit-interface byref**: DEFERRED.
- **`fixed`** (unmanaged pinning block): DEFERRED (the address model enables
  it; the `Pinned`-slot machinery is separate).
- **Whole value-type `newobj`** (constructor `ref this`): Step 18.
- **Legacy path**: untouched. All Step 17 code is `#if ENABLE_NEO_MODE`.

## 10. Symbols / files touched (summary)

- `ILIntepreter.Neo.cs` -- real `Ldloca`/`Ldflda` arms (replace no-ops); new
  `Ldarga`/`Ldarga_S`/`Ldelema`/`stind_*`/`ldind_*`/`stobj`/`ldobj`/
  `Constrained` arms; pinned-Primitives helper.
- `JITCompiler.cs` -- `AllocateSlotForType` byref branch (sec 1.2); `Ldflda`
  operand stamping already present; `Ldelema` lowering-to-offset (mirror
  Step 16).
- `Optimizer.Neo.cs` -- `AllocateNeoCallParamSlot` byref branch; foldability
  gate (sec 5.3); `Ldflda`/`Ldelema` lowering; opcode registration for BCP/FCP;
  verify `Ldflda`/`Ldelema` are escape-treating in the K1 ldloca-kill.
- `TestCases/NeoStep17Test.cs` -- new (ASCII): `ref int` frame, `ref` heap
  field, `out`, ref-to-in-frame-VT-field, `ldelema`+stind/ldind.
