> **⚠️ CORRECTION (post-implementation, re-review MAJOR-1 — supersedes Decision 2 + all
>> "spare Operand3" claims below):** `Operand3 [FieldOffset(16)]` **ALIASES the high dword of
>> `OperandLong [FieldOffset(12)]`** in the 24-byte `[StructLayout(LayoutKind.Explicit)]`
>> `OpCodeR` union — it is NOT spare. Stamping it (as this design originally specified)
>> clobbers the field path's declaring type (`(int)(OperandLong >> 32)`) -> NRE at
>> `ILType.GetStaticFieldOffset` (~20 NREs, reached by array initializers). The **shipped code
>> uses `Operand4 [FieldOffset(20)]`** — the only genuinely-disjoint int spare. See
>> `review-report.md` "MAJOR-1" + "Re-review round 1". **Do NOT re-use Operand3 for ldtoken.**

## Context

`ldtoken` is the CIL opcode that loads a metadata-token handle onto the eval stack.
C# uses it almost exclusively as the producer half of `typeof(T)`:

```
ldtoken   T              // push RuntimeTypeHandle
call      Type.GetTypeFromHandle(valuetype RuntimeTypeHandle) // -> System.Type
```

and, far less often, for method/field reflection handles. Under the Neo register VM
(`ENABLE_NEO_MODE`) the opcode is currently unimplemented: the JIT emits it, but
neither `LowerNeoOffsets` nor `ExecuteNeo` has a case for it, so it falls through to
the `default:` `"Neo: opcode Ldtoken not yet implemented (Step 6)"`. After child 1
(`neo-jit-bogus-opcode`) removed every garbage-opcode hit, `Ldtoken` is the sole
remaining `default:`-arm entry on the full Neo smoke (~60 pre-crash hits). Legacy
`ExecuteR` handles it and is green.

### How the JIT encodes `ldtoken` today (already correct -- no JIT change)

`JITCompiler.cs` `case Code.Ldtoken` (~line 2902):

```csharp
op.Register1 = baseRegIdx++;                 // destination register
if (token is FieldReference)      { op.Operand = 0; op.OperandLong = appdomain.GetStaticFieldIndex(token, declaringType, method); }
else if (token is TypeReference)  { op.Operand = 1; op.OperandLong = method.GetTypeTokenHashCode(token); }
else throw new NotImplementedException();    // NOTE: no method-reference branch exists
```

So the operand encoding is:

| `ip->Operand` | meaning        | `ip->OperandLong`                                       |
|---------------|----------------|---------------------------------------------------------|
| `1`           | TypeReference  | type-token hash (resolved via `AppDomain.GetType((int)OperandLong)`) |
| `0`           | FieldReference | packed static-field index (decl-type token in high 32 bits, field index in low 32 bits -- identical to `Ldsfld`) |

There is **no `MethodReference` branch** -- `ldtoken <method>` already throws at JIT
time, so `RuntimeMethodHandle` is out of scope by construction.

### Legacy reference (`ILIntepreter.Register.cs` ~line 1987)

```csharp
case OpCodeREnum.Ldtoken:
    switch (ip->Operand) {
        case 0: // field
            type = AppDomain.GetType((int)(ip->OperandLong >> 32));
            if (type is ILType t) t.StaticInstance.CopyToRegister((int)ip->OperandLong, ref info, ip->Register1);
            else throw new NotImplementedException();
            break;
        case 1: // type
            type = AppDomain.GetType((int)ip->OperandLong);
            if (type != null) AssignToRegister(ref info, ip->Register1, type.ReflectionType);
            else throw new TypeLoadException();
            break;
    }
```

Two things to note from Legacy:
1. The **type path pushes `type.ReflectionType` directly** -- a `System.Type` object,
   NOT a `RuntimeTypeHandle` struct. The consumer `Type.GetTypeFromHandle` is a
   registered no-op redirect (`CLRRedirections.GetTypeFromHandle` returns `esp`
   unchanged, "Nothing to do"), so the Type passes straight through. No handle struct
   is ever materialised.
2. The **field path reads the static field's VALUE** (`CopyToRegister`), not a handle.
   This is a Legacy quirk (semantically `ldtoken <field>` should yield a
   `RuntimeFieldHandle`), but it is the established behaviour and there is no
   `GetFieldFromHandle` redirect that would consume a real handle. We mirror it.

### Neo object-push idiom (the constraint that shapes the lowering decision)

Object-producing opcodes in `ExecuteNeo` push a reference in two parts: store the
object at a ref-slot index `dstIdx`, then write `dstIdx` into the destination
primitive slot (`*(int*)(frameBase + ip->DstOffset) = dstIdx`). `Ldstr` is the
canonical example:

```csharp
case OpCodeREnum.Ldstr:
    dstIdx = frameRefBase + ip->Operand;            // <- ref-slot index lives in Operand
    mStack[dstIdx] = AppDomain.GetString(ip->OperandLong);
    *(int*)(frameBase + ip->DstOffset) = dstIdx;
    break;
```

`LowerNeoOffsets` therefore stamps the dest ref-slot index into `op.Operand` for
`Ldstr`/`Ldftn`/`Ldvirtftn` (`op.Operand = localInfos[op.Register1].RefOffset; LowerR1(...)`).
**But `ldtoken`'s `Operand` is already the 0/1 discriminator** -- it cannot host the
ref slot. This is the crux the design has to resolve.

## Goals / Non-Goals

**Goals:**
- Implement `ldtoken` in `ExecuteNeo` such that `typeof(T)` (the dominant ~60-hit
  case) and the IL-static-field path execute correctly, mirroring Legacy semantics.
- Add the matching `LowerNeoOffsets` case so the ExecuteNeo arm receives a valid
  `DstOffset` and dest ref slot.
- Unblock downstream reflection / attribute / LINQ-expression patterns that consume
  a `System.Type`.
- Stay Legacy-neutral (Neo-gated only).

**Non-Goals:**
- Materialising a real `RuntimeTypeHandle`/`RuntimeFieldHandle` struct (Legacy never
  does; the no-op `GetTypeFromHandle` redirect makes it unnecessary). Out of scope.
- `RuntimeMethodHandle` -- the JIT has no `ldtoken <method>` branch and throws at
  JIT time, so there is nothing to implement.
- Fixing the Legacy field-path quirk (returning the value instead of a handle).
  Mirroring it is the mandate; a real handle representation would be a separate,
  consumer-driven change.
- CLR static fields on the field path (Legacy itself throws NIE there; child 4
  `neo-clr-static-fields` owns CLR static-field access).

## Decisions

### Decision 1 -- Push `type.ReflectionType` as a boxed object reference (not a handle struct)

The type-path arm resolves `IType type = AppDomain.GetType((int)ip->OperandLong)` and
pushes `type.ReflectionType` into a Neo ref slot, exactly as Legacy's `case 1` does
with `AssignToRegister`. `Type.GetTypeFromHandle` is already a no-op CLR redirect
shared by Legacy and Neo, so once the Type is on the stack the consumer receives it
unchanged.

**Alternatives considered:**
- *Construct a real `RuntimeTypeHandle` struct value and push it as a value-type
  slot.* Rejected: Legacy does not do this, the no-op redirect would then receive a
  struct it does not read, and it would diverge from the proven Legacy semantics.
  Matching Legacy is the mandate and the lowest-risk choice.

### Decision 2 -- Stamp the dest ref-slot index into the spare `Operand3` field

Because `ldtoken`'s `Operand` is the 0/1 discriminator, the lowering cannot reuse the
`Ldstr`/`Ldftn` convention (`op.Operand = RefOffset`). Instead it stamps the dest
ref-slot index into the spare `op.Operand3`, then calls `LowerR1` (which sets only
`DstOffset` and does not touch `Operand3` -- verified):

```csharp
case OpCodeREnum.Ldtoken:
    op.Operand3 = localInfos[op.Register1].RefOffset;   // dest ref slot (Operand is taken)
    LowerR1(ref op, localInfos);                        // DstOffset from Register1
    break;
```

This mirrors the existing `Isinst`/`Castclass`/`Initobj` convention of using
`Operand3` for a destination ref slot. (`OpCodeR` is a 24-byte `[StructLayout(
LayoutKind.Explicit)]` union; `Operand3` sits at byte offset 16 and is not aliased
by any field `ldtoken` reads at runtime -- the recurring explicit-layout sharp edge
noted in the portfolio context does not apply here because `Operand3` is otherwise
spare for this opcode.)

**Alternatives considered:**
- *Re-encode the discriminator into `OperandLong`'s high bit and free `Operand` for
  the ref slot.* Rejected: it changes the JIT emission and the encoding Legacy shares,
  raising the blast radius for no benefit.
- *Add a dedicated ref-slot field to `OpCodeR`.* Rejected: struct resize for one
  opcode; `Operand3` is already the right spare.

### Decision 3 -- Field path reuses the `Ldsfld` arm body (same encoding)

The field path's `OperandLong` is packed identically to `Ldsfld` (decl-type token in
the high 32 bits, static-field index in the low 32 bits). The arm therefore resolves
the declaring `ILType` from `(int)(ip->OperandLong >> 32)` and reads the static field
value per category -- the same primitive / inline-VT / reference branches the
`Ldsfld` arm (~line 3783) already implements. The apply worker SHOULD extract a
shared `ReadNeoILStaticField(ilt, sIdx, dstSlot, frameBase, mStack, AppDomain)` helper
to avoid duplicating that ~30-line body between `Ldsfld` and `Ldtoken`. A CLR
declaring type throws a tagged `NotImplementedException` (parity with Legacy's own
NIE on the CLR branch, and with `Ldsfld`).

### Decision 4 -- No JIT change

`JITCompiler.cs` already emits `Ldtoken` with the correct `Operand`/`OperandLong`/
`Register1`. Only the lowering pass and the ExecuteNeo arm are missing.

## Risks / Trade-offs

- **[Field path duplicates `Ldsfld` logic]** -> Mitigation: extract a shared static-
  field-read helper (Decision 3). If extraction is risky, an inline copy with a
  cross-reference comment is acceptable for this change; both arms share one encoding
  so divergence is unlikely.
- **[Field path is a Legacy quirk (value, not handle)]** -> Mitigation: the smoke's
  ~60 ldtoken hits are all `typeof()` (type path); the field path is rarely/if-ever
  hit. Mirroring Legacy is the mandate; a comment in the arm records the quirk. If a
  future consumer needs a real `RuntimeFieldHandle`, that is a separate change.
- **[Wrong ref-slot field would silently corrupt]** -> Mitigation: `Operand3` is
  verified spare for `ldtoken` and unaliased by any runtime-read field; the
  `NeoStep` probe asserts on the consumed value (not just "did not throw"), so a
  wrong-slot bug shows up as a wrong `typeof` result, not a silent pass. (Per the
  child-1 finding, a fault-only probe will NOT catch a wrong-value bug -- the probe
  must assert the value.)
- **[`OpCodeR` explicit-layout aliasing]** -> Mitigation: `LowerR1` touches only
  `DstOffset`; `Operand3` (@16) is disjoint from `Operand` (@4-7), `OperandLong`
  (@12-19 low 8), and the register aliases (@4-11). Confirmed by inspection.
