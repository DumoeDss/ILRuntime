# Design — Neo Step 16: Array element access

Concrete, code-grounded plan. The reference is Legacy `ExecuteR`
(`ILIntepreter.Register.cs:4879-5304`); Neo ports the *semantics* onto the `byte*`
frame + mStack model. Do NOT modify Legacy.

## 0. Current state (verified)

- `ExecuteNeo` (`ILIntepreter.Neo.cs`) has **no** `Newarr`/`Ldelem_*`/`Stelem_*`/
  `Ldelema` arms. All fall through to the generic
  `NotImplementedException("...not yet implemented (Step 6)")` at line 2183.
- All array opcodes exist as distinct `OpCodeREnum` values
  (`OpCodeREnum.cs:568-660`: `Newarr`, `Ldelema`, `Ldelem_I1`…`Ldelem_Ref`,
  `Stelem_I`…`Stelem_Ref`, `Ldelem_Any`, `Stelem_Any`).
- JIT `Translate` (`JITCompiler.cs`) lowers register usage:
  - `Newarr` (@2061): `Register1` = dest(array), `Register2` = count, `Operand` =
    element-type-token hash (`method.GetTypeTokenHashCode(token)`).
  - `Ldelem_*` (@1943-1959, the binary-op group): `Register1` = dest,
    `Register2` = array, `Register3` = index. Same shape as `Add`/`Sub`.
  - `Stelem_*` (@2071-2084): `Register1` = array, `Register2` = index,
    `Register3` = value. `baseRegIdx -= 3`.
  - **NOT enumerated in JIT:** `Code.Ldelem_I`, `Code.Ldelem` (generic w/ token),
    `Code.Ldelem_U8`, `Code.Stelem` (generic w/ token) → these hit the JIT default
    arm `throw new NotImplementedException("Unknown Opcode...")` (@2266). They stay
    NIE (rare in C# output); out of scope.
- `LowerNeoOffsets` (`Optimizer.Neo.cs`) does **not** yet handle the array opcodes.
  They must be added so `Register1/2/3` become byte offsets (and dest ref offsets are
  carried), exactly like the `Box`/`Unbox`/`Isinst` arm (@430-447) carries
  `Operand3`/`Operand4` = dest/src ref offsets.

## 1. The three array representations in Neo (the core finding)

An array reference is always a CLR object on the mStack (the frame byte slot holds the
mStack index, as for every reference local/field). What differs is the CLR object's
type and what an "element" is:

| Kind | CLR object held on mStack | Element | Element access |
|------|---------------------------|---------|----------------|
| (a) CLR primitive array | `int[]`/`float[]`/`byte[]`/… (a typed CLR `Array`) | the primitive value | typed CLR indexer `((int[])arr)[i]` |
| (b) IL **reference**-type array | `ILTypeInstance[]` (Legacy uses `new ILTypeInstance[n]`, ref-type elements left null) **or** a CLR `Array` of objects (when element is a CLR ref type / `object`) | an mStack-resident object | `ILTypeInstance[]` indexer, or `Array.GetValue` |
| (c) IL **value**-type array | `ILTypeInstance[]` whose every slot is a heap `ILTypeInstance` (pre-instantiated at `Newarr`) | a heap `ILTypeInstance` with `Primitives` + `ManagedObjects` | `CopyBlock` element.Primitives ↔ frame byte region + copy ref slots |

This matches Legacy exactly:
- `Newarr` (@4900-4911): for `ILTypeInstance` element type, `arr = new ILTypeInstance[n]`,
  and **if `type.IsValueType`**, pre-instantiate every slot
  `ilArr[i] = ((ILType)type).Instantiate(true)`. (Ref-type IL arrays are left null —
  C# `new` semantics, elements default to null.)
- For non-IL element types: `((CLRType)type).CreateArrayInstance(n)` if CLRType,
  else `Array.CreateInstance(type.TypeForCLR, n)` (@4888-4895).
- `Ldelem_Ref`/`Ldelem_Any` (@5133-5159): `arr as ILTypeInstance[]` first; if it is one
  and the element is an unboxed VT `ILTypeInstance`, do `CopyValueTypeToStack`
  (the VT path). Else `AssignToRegister` the object (ref path). This is the
  kind-detection branch Neo must replicate.

**Neo translation of (a)/(b)/(c):** Neo has no `StackObject`/`AssignToRegister`. The
dest slot is a frame byte region. For a primitive result we write the value into
`frameBase + DstOffset`. For a reference result we store the object on the mStack at
`frameRefBase + dstRefOffset` and write that index into `frameBase + DstOffset` (the
same pattern as `Isinst` @2067-2072). For a VT result we `CopyBlock`
`element.Primitives` → `frameBase + DstOffset` and copy the element's ref slots into
`frameRefBase + dstRefOffsetBase + i` (the Step 12/12b pattern, reusing
`CopyILToFrame` which already does primitive+ref copy from an `ILTypeInstance` to a
frame region).

## 2. Lowering (`LowerNeoOffsets`, `Optimizer.Neo.cs`)

Add the array opcodes to the lowering switch. They carry 2-3 source/dest registers;
follow the `Box`/`Isinst` shape (DstOffset/SrcOffset byte offsets + ref offsets in
`Operand3`/`Operand4`):

- `Newarr`: `Register1`=dest(array ref), `Register2`=count(primitive, int).
  - `DstOffset = localInfos[R1].Offset`; `Operand3 = localInfos[R1].RefOffset` (dest
    array ref slot). `SrcOffset = localInfos[R2].Offset` (count byte offset).
- `Ldlen`: `Register1`=dest(int), `Register2`=array(ref).
  - `DstOffset = localInfos[R1].Offset`; `SrcOffset = localInfos[R2].Offset`.
- `Ldelem_*` (all primitive + Ref + Any): `Register1`=dest, `Register2`=array(ref),
  `Register3`=index(int).
  - `DstOffset = localInfos[R1].Offset`; `Operand3 = localInfos[R1].RefOffset`
    (needed for Ref/Any/VT element dest); `SrcOffset = localInfos[R2].Offset`
    (array mStack index); the index byte offset is folded into a third field. There is
    no third byte-offset field on `OpCodeR` beyond Dst/Src — so carry the **index**
    offset in `Operand4` (high) — OR, simpler: read the index at JIT time is not
    possible (runtime value). Resolution: the existing binary-op group encodes three
    registers as R1/R2/R3 pre-lowering; LowerNeoOffsets must lower all three. Use
    `DstOffset`=R1, `SrcOffset`=R2(array), and store R3(index) byte offset in
    `Operand4` (cast). Document that array `Ldelem`/`Stelem` overload `Operand4` as the
    third (index/value) byte offset, and `Operand3` as the dest/src ref offset where a
    ref is involved. (See `OpCodeR` field inventory: `DstOffset`,`SrcOffset`,
    `Operand`,`Operand2`,`Operand3`,`Operand4`,`OperandLong`,… — enough room.)
- `Stelem_*`: `Register1`=array(ref), `Register2`=index(int), `Register3`=value.
  - `DstOffset = localInfos[R1].Offset` (array mStack index); index offset in
    `SrcOffset` (R2); value offset in `Operand4` (R3 byte offset); value ref offset
    (for Ref/Any/VT) in `Operand3` (R3 ref offset).

> NOTE for the implementer: confirm the exact `OpCodeR` scratch fields available and
> pick a consistent encoding; the `Box`/`Isinst` arm proves `Operand3`/`Operand4` are
> reusable scratch for ref offsets post-lowering. Do not collide with `Operand` (the
> element-type token for Newarr/Ldelem_Any/Stelem_Any).

## 3. Newarr — interpreter arm

Read count `n = *(int*)(frameBase + SrcOffset)`. Resolve element type
`IType et = AppDomain.GetType(ip->Operand)`. Allocate per §1:

```
object arr;
if (et != null) {
    if (et.TypeForCLR != typeof(ILTypeInstance)) {
        arr = (et is CLRType ct) ? ct.CreateArrayInstance(n)
                                 : Array.CreateInstance(et.TypeForCLR, n);
        AppDomain.GetType(arr.GetType());   // register, as Legacy does
    } else {
        var ilArr = new ILTypeInstance[n];
        if (et.IsValueType)
            for (int i = 0; i < n; i++) ilArr[i] = ((ILType)et).Instantiate(true);
        arr = ilArr;
    }
}
dstIdx = frameRefBase + ip->Operand3;
mStack[dstIdx] = arr;
*(int*)(frameBase + ip->DstOffset) = dstIdx;
```

`null` element type (shouldn't happen) → write -1 into the dest slot (null array).

## 4. Ldelem — interpreter arm (per array kind)

Read array mStack index `arrIdx = *(int*)(frameBase + SrcOffset)`; null →
`NullReferenceException`. Read index `idx = *(int*)(frameBase + <index offset>)`.

Dispatch by opcode + array kind. Mirror Legacy's per-type arms but write to the frame:

- **Primitive typed arms** (`Ldelem_I4`/`I8`/`R4`/`R8`/`I1`/`U1`/`I2`/`U2`/`U4`):
  `Array arr = (Array)mStack[arrIdx];` then a typed cast + indexer, e.g.
  `*(int*)(frameBase + DstOffset) = ((int[])arr)[idx];`. Match Legacy's signed/unsigned
  char/bool/short disambiguation for the I1/I2/U1/U2 arms (Legacy tries `bool[]` then
  `sbyte[]`, etc.) — though for Neo a single canonical cast per opcode suffices when
  the JIT emits the opcode matching the static element type; keep the Legacy-style
  fallback casts to be safe. The CLR indexer throws `IndexOutOfRangeException` for OOB.
- **`Ldelem_Ref` / `Ldelem_Any`** (object / IL element): detect kind:
  - If `arr is ILTypeInstance[] ilArr`:
    - `var ins = ilArr[idx];` If `ins != null && ins.Type.IsValueType && !ins.Boxed`
      → VT path: `CopyILToFrame(ins, frameBase, DstOffset, dstRefOffset,
      ins.Type.TotalPrimitiveSize, ins.Type.TotalReferenceCount, mStack, frameRefBase)`
      (reuse the existing Step 13 helper; writes element Primitives + ref slots into the
      dest frame region).
    - Else → ref path: store `ins` on mStack at `frameRefBase + dstRefOffset`, write
      that index into `frameBase + DstOffset`.
  - Else (`Array.GetValue` for a CLR element array): same VT-vs-ref decision via the
    value's runtime type (a boxed IL VT → CopyBlock its Primitives; else store ref).

## 5. Stelem — interpreter arm (per array kind)

Read array mStack index, index, and value (from their byte offsets). Dispatch:

- **Primitive typed arms**: typed cast + indexer write, e.g.
  `((int[])arr)[idx] = *(int*)(frameBase + <value offset>);`. OOB → CLR throws
  `IndexOutOfRangeException`.
- **`Stelem_Ref` / `Stelem_Any`**: detect kind:
  - `arr is ILTypeInstance[] ilArr`:
    - If `ilArr`'s element type is a VT: read the value from the frame (the value is
      an in-frame VT) → `CopyFrameToIL(ilArr[idx], frameBase, <value offset>,
      <value ref offset>, size, refCount, mStack, frameRefBase)` (Step 12/12b helper,
      symmetric to ldelem's CopyILToFrame).
    - Else (ref element): read the value's mStack index from its byte slot; store
      `ilArr[idx] = thatObject`.
  - Else (`Array` of CLR objects): `Array.SetValue` / typed cast of the value.

For VT `Stelem`, the element `ILTypeInstance` must already exist (it does — `Newarr`
pre-instantiates VT array slots, §3). There is no per-stelem allocation.

## 6. Ldlen — interpreter arm

`Array arr = (Array)mStack[arrIdx]; *(int*)(frameBase + DstOffset) = arr.Length;`
(null array → `NullReferenceException`.) Ported verbatim from Legacy @5108-5116.

## 7. Bounds check

No explicit check. The CLR typed array indexers (`((int[])arr)[idx]`, `ilArr[idx]`,
`Array.SetValue`) throw `IndexOutOfRangeException` natively on OOB, which Neo's outer
try/catch (`HandleException`) routes to a matching catch handler exactly as in Step 14.
This matches Legacy (which also has no explicit check). A throw-asserting smoke test is
NOT green-expressible (harness treats an uncaught throw as failure), so bounds are
verified only through the success path (read/write valid indices) and the throw
contract is documented as enforced-by-code.

## 8. `Ldelema` — DEFERRED to Step 17

Decision: **defer.** `ldelema` produces a Ref Slot `(arrayMStackIndex, elementIndex)`.
Its only consumers are `stind`/`ldind`, `fixed`, and `ref`/`out` parameters — all of
which are Step 17 (the unified 8-byte `(objIdx, offset)` Ref Slot model, `stind`/`ldind`
dispatch). Without those consumers there is no green-expressible test for `ldelema`, so
implementing it now ships dead, unexercisable plumbing. `ldelema` lands in Step 17
alongside its consumers. It remains a Step-tagged NIE in `ExecuteNeo` for now.

This is the conservative, correct-scope choice: ship Newarr + ldelem/stelem/Ldlen for
the testable array kinds; defer `ldelema` to where it has a purpose.

## 9. Edge cases / non-goals

- **Null array:** every arm throws `NullReferenceException` (read arrIdx → resolve →
  null check) — matches Legacy semantics.
- **Multi-dimensional arrays:** out of scope (Step 16 is rank-1 `*` only). mdim uses a
  different element-access ABI and is not emitted by the test cases here.
- **`Code.Ldelem`/`Code.Stelem` (generic, with type token), `Code.Ldelem_I`,
  `Code.Ldelem_U8`:** out of scope — not enumerated by JIT `Translate` (would NIE at
  JIT time). Address when a real test needs them.
- **`fixed`/`stind`/`ldind`/`ref`/`out`:** Step 17.
- **Boxed-element `call`/`callvirt`:** reachable via ldelem-ref then call; covered by
  the ref path in §4.
- **VT array element with reference fields:** handled by the primitive+ref copy
  (`CopyILToFrame`/`CopyFrameToIL` copy both regions), reusing the Step 12/12b/13
  pattern — no new copy code.
