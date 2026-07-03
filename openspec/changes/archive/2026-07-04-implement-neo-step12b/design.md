# Design — implement-neo-step12b (Move_Vt + LowerMove)

This design is grounded in the current code. All file:line references are to
the branch state at propose time (HEAD = Step 12 complete, `424b9730`).

## 0. The key finding that shapes the design

**The existing `Move` opcode ALMOST does Step 12b — it only mishandles multi-ref
value types.** Three facts:

1. The existing `Move` `ExecuteNeo` arm (ILIntepreter.Neo.cs ~440-458) already
   does `Unsafe.CopyBlock(frameBase + DstOffset, frameBase + SrcOffset,
   Operand2)` (copies the primitive bytes) AND, when `Operand == 1`, copies
   exactly ONE reference slot. The lowering pass (Optimizer.Neo.cs ~153-177)
   already stamps `Operand` = isRefMove(0/1), `Operand2` = byte size,
   `Operand3` = dst RefOffset.

2. The single-ref limitation is the bug. `Operand == 1` means "this is a
   reference-type move", set by `TypeSpecializeNeoOpcodes`
   (`op.Operand = IsNeoReferenceSlot(srcType) ? 1 : 0`, JITCompiler.cs ~545).
   A reference-type move copies one ref. But a value-type move with N ref
   fields needs N ref copies, and the current arm has no loop and no refCount.

3. So Step 12b is NOT "build copy from scratch". It is: (a) make the JIT
   detect Moves whose dest is a value type with refCount > 0 and rewrite them
   to a new `Move_Vt` opcode; (b) give `Move_Vt` a multi-ref-aware body; and
   (c) leave plain `Move` alone for everything it already handles (primitives,
   single references, and pure-primitive value-type copies — where refCount
   == 0 and the byte CopyBlock is already correct).

This is the minimal, lowest-risk cut.

## 1. The OpCodeR union pitfall (Step 12 round-2 lesson) — pass placement

`OpCodeR` is `[StructLayout(LayoutKind.Explicit)]` (OpCode.cs ~35-71):

```
offset 4: Register1 (short) / DstOffset (ushort)         // ALIASED
offset 6: Register2 (short) / SrcOffset (ushort)         // ALIASED
offset 8: Register3 (short) / OperandOffset (ushort) / Operand (int)  // ALIASED
offset 10: Register4 (short)                              // (part of Operand's int)
offset 12: Operand2 (int)                                 // STANDALONE
offset 16: Operand3 (int)                                 // STANDALONE
offset 20: Operand4 (int)                                 // STANDALONE
```

`LowerNeoOffsets` OVERWRITES `Register1/2/3` with byte offsets (it writes
`DstOffset`/`SrcOffset` which alias `Register1`/`Register2`). After lowering,
the original register indices are GONE.

`LowerMove` needs the DEST register's INDEX (to look up its value type and
derive primitiveSize + refCount). Therefore:

**DECISION: `LowerMove` runs INSIDE `TypeSpecializeNeoOpcodes`**
(JITCompiler.cs ~480-), BEFORE `LowerNeoOffsets` (line 466), operating on
`res` in register-index form. There it has both the register indices AND the
`registerTypes` array (already built at line 482 via
`BuildInitialRegisterTypes`). This is the same place the existing
`op.Operand = IsNeoReferenceSlot(srcType) ? 1 : 0` stamping happens (line 545),
so `LowerMove` slots in right next to it with identical visibility.

This completely sidesteps the round-2 trap: `LowerMove` decides opcode
selection from register indices + types BEFORE any offset is written. After
lowering, `Move_Vt` is just another opcode whose byte offsets the existing
`LowerNeoOffsets` Move-case extension stamps.

BCP/FCP non-interference (the planning-context concern): BCP
(Optimizer.BCP.cs ~48-) and FCP (Optimizer.FCP.cs ~47-) run at JITCompiler.cs
lines 310-312, well BEFORE `TypeSpecializeNeoOpcodes` (line 446). They operate
on register-index `Move`s and may elide them (self-moves, copy-propagation).
`LowerMove` runs strictly later and only ever sees the surviving `Move`s, so
it cannot perturb copy-elision and elision cannot starve it of information.
`Move_Vt` is never visible to BCP/FCP (they run first), so they need no
changes for it. The one registration touch-point (design 4.2) is defensive,
for the inliner and any future pass that walks the body.

## 2. The LowerMove pass

### 2.1 Where

Inside `TypeSpecializeNeoOpcodes`, immediately after the existing `Move` type
case (JITCompiler.cs ~542-548), in the same loop iteration that processes each
opcode. The pass is a per-instruction rewrite; no separate scan needed.

### 2.2 The discriminator

For a `Move` instruction `op`:

```csharp
case OpCodeREnum.Move:
{
    IType srcType = GetRegisterType(registerTypes, op.Register2);
    op.Operand = IsNeoReferenceSlot(srcType) ? 1 : 0;
    SetRegisterType(registerTypes, op.Register1, srcType);

    // ---- Step 12b: LowerMove ----
    IType dstType = GetRegisterType(registerTypes, op.Register1);
    if (dstType is ILType dstIl && dstIl.IsValueType && !dstIl.IsEnum)
    {
        int refCount = dstIl.TotalReferenceCount;
        if (refCount > 0)
        {
            op.Code = OpCodeREnum.Move_Vt;
            // primitiveSize/refCount are stamped at lowering time from the
            // dst slot; carry them now only as a hint is unnecessary --
            // LowerNeoOffsets re-derives from localInfos[dstReg], which is
            // authoritative (it has the slot's actual Size/RefOffset/RefCount).
        }
        // else: pure-primitive VT. Keep Move. Its byte CopyBlock
        // (Operand2 = size, isRefMove=0) already copies the primitives
        // correctly with zero refs to worry about. No rewrite needed.
    }
    break;
}
```

Notes:
- `IsNeoReferenceSlot(srcType)` is the EXISTING discriminator (true when
  !IsPrimitive && !IsValueType). For a reference-type Move the dest is also a
  reference slot (single ref) and the existing `Move` arm handles it —
  `LowerMove` does NOT touch reference-type Moves (`dstType.IsValueType` is
  false). So the single-ref path is unchanged.
- `dstIl.IsEnum` excludes enums (they are their underlying primitive; copying
  an enum is a primitive copy handled by plain `Move`).
- The rewrite only fires when refCount > 0. A pure-primitive VT
  (`Vector3`, three floats, refCount == 0) keeps `Move` — its byte CopyBlock is
  already correct (verified: the existing arm copies `Operand2` bytes with no
  ref work when `Operand == 0`). This MINIMIZES the regression surface: the
  only opcodes that change behavior are VT copies with reference fields,
  which are exactly the ones currently broken.

### 2.3 BCP/FCP visibility

BCP/FCP run before `TypeSpecializeNeoOpcodes`, so they only ever see `Move`.
No change to BCP/FCP. The defensive registration of `Move_Vt` in
`GetOpcodeDestRegister`/`GetOpcodeSourceRegister` (Optimizer.Utils.cs, the
helpers `Translate` uses) is only so that the inliner and any later pass that
re-walks the body treat `Move_Vt` like `Move` (Register1 = dest, Register2 =
src). This mirrors how `Move` is already registered there.

## 3. Move_Vt encoding (Operand fields)

After `LowerNeoOffsets`, `Move_Vt` carries (mirroring `Move`'s encoding):

```
DstOffset  (ushort, aliases Register1) = dst slot byte offset
SrcOffset  (ushort, aliases Register2) = src slot byte offset
Operand2   (int, offset 12, STANDALONE) = primitive byte size to CopyBlock
Operand3   (int, offset 16, STANDALONE) = dst slot RefOffset (base of dst ref run)
Operand4   (int, offset 20, STANDALONE) = refCount (number of ref slots to copy)
```

Why these fields:
- `Operand2`/`Operand3`/`Operand4` are the standalone (non-aliased) int fields
  of the union (offsets 12/16/20). They do NOT alias any register/offset, so
  writing them is stable regardless of `LowerNeoOffsets` clobbering
  Register1/2/3 and regardless of `Operand` (offset 8, aliased). This is the
  explicit answer to the union pitfall: the two compile-time-known sizes
  (primitiveSize, refCount) live in standalone fields and survive lowering
  intact.
- We do NOT reuse `Operand` (offset 8, aliased with Register3/OperandOffset)
  because after `LowerR1R2` only Register1/Register2 are touched, but reusing
  `Operand` would be fragile if any future lowering extends to Register3.
  `Operand4` (offset 20) for refCount keeps all four Move_Vt parameters in
  disjoint fields.
- The EXISTING `Move` stamps `Operand` (isRefMove flag), `Operand2` (size),
  `Operand3` (dstRefOffset). `Move_Vt` extends this with `Operand4` (refCount)
  and drops the `Operand` flag (always multi-ref-aware; the refCount itself
  encodes "how many refs").

`LowerNeoOffsets` Move-case extension (Optimizer.Neo.cs ~153-177, add a parallel
`case OpCodeREnum.Move_Vt:` fallthrough or sibling):

```csharp
case OpCodeREnum.Move_Vt:
{
    int srcReg = op.Register2;
    int dstReg = op.Register1;
    int srcSz = localInfos[srcReg].Size;
    int dstSz = localInfos[dstReg].Size;
    int dstRef = localInfos[dstReg].RefOffset;
    int dstRefCount = localInfos[dstReg].RefCount;
    int srcRef = localInfos[srcReg].RefOffset;
    // primitive size: min(src,dst) to avoid clobbering neighbours (same rule
    // as the existing Move case).
    int sz = srcSz < dstSz ? srcSz : dstSz;
    LowerR1R2(ref op, localInfos);
    op.Operand2 = sz;
    op.Operand3 = dstRef;       // dst ref-run base
    op.Operand4 = dstRefCount;  // number of ref slots
    // NOTE: src ref-run base is NOT encoded -- see Move_Vt body (4.1) for why.
}
break;
```

The src ref base is intentionally NOT encoded: at runtime the src/dst ref runs
have the SAME length (refCount) because src and dst are the same value type
(the JIT only emits Move_Vt for a same-type assignment; the C# compiler does
not emit a cross-type struct Move). So the body reads
`mStack[frameRefBase + dstRefBase + i] = mStack[<srcIdx for slot i>]`. The src
slot's index for ref-slot `i` is read from the src primitive bytes the same way
the existing `Move` arm reads the single src ref (design 4.1).

## 4. Move_Vt ExecuteNeo body

Placed right after the existing `Move` arm (ILIntepreter.Neo.cs ~458).

### 4.1 Body

```csharp
case OpCodeREnum.Move_Vt:
{
    int primSize = ip->Operand2;
    int dstRefBase = ip->Operand3;      // dst ref-run base (frame-ref offset)
    int refCount = ip->Operand4;
    // 1. Primitive bytes (flat copy; includes the in-VT ref-field index cells
    //    which are rewritten per-slot below, exactly like ILTypeInstance).
    if (primSize > 0)
        Unsafe.CopyBlock(frameBase + ip->DstOffset, frameBase + ip->SrcOffset, (uint)primSize);
    // 2. Per-reference-field copy (null convention -1, no aliasing).
    for (int i = 0; i < refCount; i++)
    {
        // The src ref index for field i is stored in the src primitive bytes
        // at the field's position within the VT (same layout the heap path
        // uses). We mirror the existing Move arm's single-ref read, generalized.
        int srcIdx = *(int*)(frameBase + ip->SrcOffset /* + field i's ref-cell offset */);
        int dstIdx = frameRefBase + dstRefBase + i;
        ... 
    }
}
break;
```

RESOLUTION of the src-ref-cell-offset subtlety (the `/* ... */` above): in the
Neo frame, an in-frame value type's reference fields live in the frame's mStack
ref region under the slot's `RefOffset`, NOT inside the byte region. This is
the Step 12 design (archive design.md 1.3): the byte region holds primitives;
refs are in `mStack[frameRefBase + slot.RefOffset + field.ReferenceOffset]`.
So both src and dst ref runs are in the mStack region:

```
src ref for field i = mStack[frameRefBase + srcSlotRefOffset + i]
dst ref for field i = mStack[frameRefBase + dstSlotRefOffset + i]
```

where `srcSlotRefOffset` is the src slot's `RefOffset` and `dstSlotRefOffset`
is `ip->Operand3`. The src slot's `RefOffset` is NOT in the opcode (only dst's
is). Two clean options for the implementer (pick ONE, document in tasks):

- **(A) [PREFERRED] Encode the src ref base too.** Stamp `Operand3` = dst
  RefOffset and reuse `Operand` (the existing Move arm already uses `Operand`
  as a flag, but Move_Vt does not need the flag) to carry the src RefOffset.
  Then the body is a clean symmetric loop:
  ```csharp
  for (int i = 0; i < refCount; i++)
  {
      int srcObjIdx = frameRefBase + ip->Operand  + i; // src ref base
      int dstObjIdx = frameRefBase + ip->Operand3 + i; // dst ref base
      var srcVal = mStack[srcObjIdx];
      mStack[dstObjIdx] = srcVal;            // copy the reference (object identity preserved)
  }
  ```
  Wait — `mStack` holds objects (the Neo model), so `mStack[idx] = mStack[src]`
  copies the object reference. The byte region does NOT carry ref indices for
  in-frame VTs (refs are out-of-line in mStack). So the loop is just a direct
  mStack-to-mStack copy of `refCount` slots. The lowering stamps BOTH
  `Operand3` (dst RefOffset) and `Operand` (src RefOffset); the body uses both.
  This is the clean option and matches the existing `Move` arm's use of
  `Operand` (it currently holds the isRefMove flag; for Move_Vt we repurpose it
  as the src RefOffset since the flag is meaningless for a VT move).
- **(B)** Read the src RefOffset at runtime via a slot lookup on `SrcOffset`
  (slower; against the "compile-time-known offsets" principle; not preferred).

The implementer uses (A). Final encoding:

```
DstOffset = dst slot byte offset
SrcOffset = src slot byte offset
Operand2  = primitive byte size
Operand3  = dst slot RefOffset (dst ref-run base)
Operand   = src slot RefOffset (src ref-run base)   // repurposed flag field
Operand4  = refCount
```

(All five are populated by the `LowerNeoOffsets` Move_Vt extension; `Operand`
src-RefOffset stamping is added there.)

### 4.2 Edge cases

- **Zero-primitive VT** (`struct { string a; string b; }`, primitiveSize could
  be 0 if the type has no primitive fields): `primSize > 0` guard skips the
  CopyBlock; the ref loop still copies. (In practice a VT has at least the
  ref-index cells, but TotalPrimitiveSize may legitimately be 0 for a
  ref-only struct; the guard handles it.)
- **Ref-only VT** (`struct { string s; }`, refCount == 1, primitiveSize may be
  the size of one ref-index cell): handled by the general loop; refCount == 1
  is just one iteration. NOTE such a copy is currently routed to `Move` (not
  `Move_Vt`) only if the JIT's `IsNeoReferenceSlot` misclassifies it — but a
  value type is never a reference slot (`IsValueType` true), so the discriminator
  correctly sees it as a VT and `LowerMove` fires for refCount > 0. Verified:
  `IsNeoReferenceSlot` returns false for any `IsValueType` type.
- **Pure-primitive VT** (`Vector3`, refCount == 0): NOT rewritten — keeps
  `Move`. Its CopyBlock-only execution is correct. This is the design's
  deliberate minimization.
- **src == dst** (self-assignment `a = a`): BCP/FCP elide self-Moves before
  `LowerMove` runs (BCP ~53-56, FCP ~52-55 mark `xDst == xSrc` for removal),
  so `Move_Vt` never sees a degenerate self-copy. Even if it did, CopyBlock
  with overlapping identical ranges + a same-base ref loop is a no-op. Safe.
- **Nested VT** (`struct Outer { Inner i; }`): a nested-VT field's primitive
  bytes are part of the outer's byte region (InitializeFields accumulates the
  nested TotalPrimitiveSize), and its ref fields are part of the outer's ref
  run (accumulated TotalReferenceCount). So copying the whole outer VT copies
  the nested VT's bytes and refs in one shot — no recursion. Validated by a
  NeoStep12b nested-VT test.
- **Aliasing independence**: `Move_Vt` copies OBJECT REFERENCES (mStack
  entries), so after `S b = a;`, `b`'s ref fields point to the SAME objects as
  `a`'s — which is correct C# struct copy semantics (shallow copy: ref fields
  share identity, value fields are independent). Mutating `a.x` (a value
  field) after the copy must NOT change `b.x`. This holds because the primitive
  CopyBlock is a value copy, not an alias. The NeoStep12b aliasing test probes
  exactly this.

## 5. Value-type method-parameter passing — SCOPE FINDING

The planning-context flagged "value-type method-parameter passing" as a
Step-12b validation item to scope. Finding:

**VT-by-value parameter passing is ALREADY handled by the existing `Call`
lowering, NOT by `Move`.** The `LowerNeoOffsets` `Call`/`Newobj` case
(Optimizer.Neo.cs ~619-645) builds a `NeoCallParamMap` that, for each
parameter, records the src slot's `Offset`/`RefOffset`/`RefCount` and the dst
callee-param slot's `Offset`/`RefOffset`/`RefCount`, and the call's
`ExecuteNeo` arm (`CopyNeoCallArguments`, ILIntepreter.Neo.cs ~130-139 +
the ref-copy loop in the call arm ~1354+) copies BOTH primitive bytes
(`PrimitiveSrc`→`PrimitiveDst` of `PrimitiveSize`) AND ref slots
(`RefSrc`→`RefDst`). This is already a full value-type copy into the callee
frame, independent of `Move`/`Move_Vt`.

So:
- IL-to-IL calls passing a VT by value: already work via `NeoCallParamMap`
  (Step 8 + Step 12 alignment). NOT a `Move` path. Step 12b does not change
  this.
- VT return-by-value: the callee writes the return temp; the caller's `Ret`
  handling copies it. This goes through the call machinery, not `Move_Vt`
  directly. Step 12b does not change this either.
- The ONLY copy path Step 12b's `Move_Vt` covers is an explicit assignment /
  local-init copy (`T a = b;`, `Dup`-then-store, `starg` of a VT). These are
  the `Ldloc`/`Stloc`/`Ldarg`/`Starg`/`Dup` sites that JIT to `Move`
  (JITCompiler.cs ~1888-1989).

**CONCLUSION: "VT method-param passing" is effectively out of Move_Vt's
mechanical scope (it's already done by Call lowering) but IN scope as a
VALIDATION target** — a NeoStep12b test that passes a multi-ref struct by value
to a method and reads the fields inside the callee confirms the existing call
path still works alongside the new Move_Vt. If such a test reveals a bug, it's
in the Call lowering (Step 8 territory), not Step 12b — and is reported, not
fixed in this change unless trivially related.

## 6. Non-goals (fence the scope)

- **Box / Unbox / Unbox_Any of value types** = Step 13. The `Box`/`Unbox`
  lowering case (Optimizer.Neo.cs ~386-401) and ExecuteNeo arms are unchanged.
- **`byref` / `ref` / `out` parameters** = Step 17. A genuine byref VT (ldloca
  passed to a `ref` param) is a pointer model, not a copy. Step 12b only
  covers value copies. The `Ldloca`/`Ldflda` no-op arms (Step 12) are
  unchanged.
- **IL value-type `newobj`** (constructing a VT via `new`) = Step 18. Untouched.
- **`constrained.` callvirt on value types** = Step 13/18. Untouched.
- **`Ldfld_Value` / `Stfld_Value` inline** (loading/storing a WHOLE VT field
  as a value): a whole-VT field load is a copy. When the field's owning operand
  is an in-frame VT, loading the whole field into a temp is a `Move` of the
  field's byte range. The C# compiler currently emits this as
  `ldloca + ldfld_Value` patterns that may route to `Move`; Step 12b's
  `LowerMove` will rewrite such a Move to `Move_Vt` IF the dest is a multi-ref
  VT. The whole-field-load path is therefore partially covered as a side
  effect, but is NOT a primary Step 12b target and is not specifically tested
  (a dedicated `Ldfld_Value_Inline` opcode, if ever needed, is a future step).
- **Legacy path**: untouched. All Step 12b code is `#if ENABLE_NEO_MODE`.

## 7. Symbols / files touched (summary)

- `OpCodeREnum.cs` — +1 opcode `Move_Vt` (after `Move`).
- `JITCompiler.cs` — `LowerMove` inside `TypeSpecializeNeoOpcodes`
  (~542-548 Move case); the rewrite uses `registerTypes` + `dstType as ILType`.
- `Optimizer.Neo.cs` — extend `LowerNeoOffsets` Move case (~153-177) with a
  `Move_Vt` sibling: stamp `Operand2`(primSize) / `Operand3`(dstRefOffset) /
  `Operand`(srcRefOffset) / `Operand4`(refCount); `LowerR1R2` for byte offsets.
- `Optimizer.Utils.cs` — register `Move_Vt` in
  `GetOpcodeDestRegister`/`GetOpcodeSourceRegister` (defensive; for inliner).
- `ILIntepreter.Neo.cs` — `Move_Vt` arm (~after line 458): CopyBlock + ref loop.
- `TestCases/NeoStep12bTest.cs` — new.
