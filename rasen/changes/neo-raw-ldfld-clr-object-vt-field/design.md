# Design: neo-raw-ldfld-clr-object-vt-field

## The diagnosis (pinned by JIT dump + local-variable dump on HEAD)

C# `int x = obj.Struct.field;` (obj = CLR reference object, Struct = CLR-struct
field, field = primitive field of Struct) lowers to CIL:
```
ldloc obj          ; push the owner reference
ldflda Struct      ; byref to the struct FIELD on the heap object
ldfld field        ; read the leaf field off the struct address
```
The Neo JIT typed-splitter rewrites `ldfld` into a typed arm (`Ldfld_I4` etc.)
ONLY for an ILType declaring type. The leaf field's declaring type is the STRUCT
(a CLRType) -> the raw `OpCodeREnum.Ldfld` survives with
`OperandLong = (structTypeHash << 32) | leafFieldHash`.

JIT dump for the probe (`NeoStepRawLdfldClrObjVtField_TC1`, `o.S.a`):
```
4:ldflda r5, r0, 0x8B6F43AC                 ; r5 = &(r0->S); 0x8B6F43AC = S hash
5:ldfld  r1, r5, 0x200000CFC3532101         ; raw ldfld a; owner r5 = ldflda dest
6:ceqi   r5, r1, 111
...
```
Local Variables on HEAD: `Int32 x = 4`. The owner `o` was parked at mStack index
4; the raw-Ldfld IsValueType branch took the flat-bytes `else` and reinterpreted
the byref `(objIdx=4, structFieldHash)` as the struct's flat bytes, so field `a`
read `objIdx` = 4 (NOT 111) -> ceqi false -> deliberate 1/0 DivideByZero.

So: SILENT CORRUPTION (flat-bytes reinterpret of a byref), not a NIE. Identical
mechanism to child-24's array-element read.

## The marker-vs-runtime decision (CONFIRMED: a marker is required)

The raw-Ldfld IsValueType branch (`ILIntepreter.Neo.cs:3981`) handles TWO owner
representations that are AMBIGUOUS at runtime:
1. **Flat bytes** -- `ldloc structByValue; ldfld field`. The owner register
   holds the struct's flat managed bytes; objIdx-half = the struct's FIRST FIELD
   VALUE.
2. **Byref** -- `ldflda Struct; ldfld field` (this child) or `ldelema; ldfld`
   (child-24). The owner register holds an 8-byte byref `(objIdx, off)`.

A plain runtime check (`mStack[objIdx] is <something>`) is UNSAFE: for the flat-
bytes shape, a small non-negative first-field value lands in mStack range and
can false-positive as a real object index -> a new silent-corruption vector
(child-24's verdict, restated). The owner representation is a JIT-time dataflow
fact; the untyped Neo frame requires it resolved at JIT time. So a JIT marker is
the provably-correct fix. (Decisive contrast with child-27 Stfld: a VT-owner
Stfld is ALWAYS a byref -> runtime detection sufficed there. The write/read
asymmetry child-24/26/27 identified holds.)

## The marker-bit decision (distinct bit 0x2; NOT a reuse of 0x1)

Child-24's `NeoRawLdfldArrayElementByRefMarker = 0x1` sits in bit 0x1 of the raw
`Ldfld` Operand4 (a spare int -- raw Ldfld sets only OperandLong, so Operand4 is
all-free). The raw-Ldfld Operand4 namespace is DISJOINT from the Ldflda-opcode
markers (0x1/0x2/0x4/0x8 live on a different opcode).

Two encoding options were considered:
- **Reuse 0x1 as a generic "byref-owner" marker** + runtime content dispatch
  (Array vs CLR-object). REJECTED: renames a shipped green const (child-24) for
  no benefit, and mixes two decode semantics in one branch.
- **Distinct bit 0x2** (`NeoRawLdfldClrObjectFieldByRefMarker`). CHOSEN. Lowest
  risk (child-24 untouched), explicit, each branch has clean single semantics,
  matches the Ldflda-opcode convention of one-bit-per-shape.

Mutual exclusivity is GUARANTEED at the CIL level: `ins.Previous` is a single
instruction (the CIL Previous link). `ldflda` and `ldelema` are distinct opcodes;
a raw-Ldfld's immediate predecessor is EITHER one OR neither (ldloc/ldsfld/etc.),
never both. So the two stamps never both fire. Even if they somehow did, the
runtime checks 0x1 first (child-24) -> 0x1 wins -> no conflict.

## The predecessor signal is reliable

`ins.Previous.OpCode.Code == Code.Ldflda` is the immediate CIL predecessor
(child-24 verified the same idiom for `Ldelema`). The CIL Previous/Next links are
stable IL order. Prefixes (`readonly.`, `constrained.`) PRECEDE ldflda (they are
prefixes TO ldflda), so they never sit between ldflda and the following ldfld.
The JIT dump above confirms it: instruction 4 (ldflda) is immediately followed by
instruction 5 (raw ldfld), owner register r5 == ldflda dest r5.

Known theoretical edge (documented, matches child-24 T1): a `volatile.` or
`unaligned.` prefix ON the ldfld itself would sit between ldflda and ldfld, so
`ins.Previous` would be the prefix, not ldflda, and the marker would not stamp.
This is rare (volatile struct-field reads) and fails SAFE (the flat-bytes path
still runs -> at worst continued silent corruption for that narrow shape, never a
crash). The probe does not use volatile fields.

## Operand4 0x2 survives all Neo passes

Child-24 proved this exhaustively for 0x1 in the SAME slot; 0x2 is identical:
- `LowerNeoOffsets` raw-Ldfld case (`Optimizer.Neo.cs:945-971`) does NOT touch
  Operand4 ("field identity lives in OperandLong").
- `TypeSpecializeNeoOpcodes` raw-Ldfld seeding (`JITCompiler.cs` child-21) reads
  only OperandLong/Register1.
- The Push-deletion remap only decrements Operand4 if `> removedIndex` (0x2 is
  never > a typical small removedIndex).
- `TypeSpecializeNeoOpcodes` `Ldfld_Ref` seeding (child-23) keys on
  `op.Operand4 == 0` but operates on the TYPED `Ldfld_Ref` opcode, not raw
  `Ldfld` -> no interaction.

## The runtime branch (mirrors child-27's Stfld WRITE for the READ direction)

Child-27's Stfld branch (`ILIntepreter.Neo.cs:4206-4239`) decodes
`(objIdx, structFieldHash)`, reads the whole struct via
`NeoReadClrObjectField(target, off)`, mutates, writes back. The READ direction
is the first half only:

```
else if ((ip->Operand4 & JITCompiler.NeoRawLdfldClrObjectFieldByRefMarker) != 0)
{
    int objIdx = *(int*)(frameBase + ownerOff);
    int off    = *(int*)(frameBase + ownerOff + 4);   // structFieldHash
    if (objIdx == -1)
    {
        // Defensive: a nested ldflda-on-frame-local could produce a frame-
        // native byref (-1, addr); the struct flat bytes ARE at frameBase+addr.
        int ownerSz = Optimizer.GetNeoValueTypeManagedSize(ct.TypeForCLR);
        int cur = off;
        object boxedOwner = ILIntepreter.ReadNeoValueType(
            ct.TypeForCLR, frameBase, ref cur, ownerSz);
        fldVal = f.GetValue(boxedOwner);
    }
    else
    {
        object target = mStack[objIdx];
        if (target == null) throw new NullReferenceException();
        if (target is ILTypeInstance || target is CrossBindingAdaptorType)
            throw new NotImplementedException(
                "Neo raw Ldfld: byref owner is an IL instance whose CLR-struct "
              + "field uses F-10 ManagedObjects storage (deferred). Field "
              + f.Name + " on " + ct.FullName);
        object boxedStruct = NeoReadClrObjectField(AppDomain, target, off);
        fldVal = f.GetValue(boxedStruct);
    }
}
```
The existing dest marshalling (primitive / VT / ref) below the branch handles
`fldVal` unchanged.

The byref encoding (`off` = the struct field's `FieldInfo.GetHashCode()`) is
resolved by `NeoReadClrObjectField` -> `CLRType.GetFieldValue(fieldHash, target)`
-> the struct FieldInfo via the `Fields`/`fieldInfoCache` dict keyed by the SAME
`FieldInfo.GetHashCode()` (child-27 pinned this; the hash-vs-offset distinction
is moot for a CLR-object-field owner). `f.GetValue(boxedStruct)` then reads the
leaf field off the boxed whole-struct copy.

## Tractability verdict: CLEAN (~20-30 lines)

- 1 const + 1 JIT stamp line (mutually exclusive with child-24).
- ~20 runtime lines (one `else if` with two sub-branches + comments).
- No optimizer / object-model / binding change. Neo-gated -> Legacy-neutral.
- Sibling families all green (child-4 flat-bytes path moves under a new `else`;
  child-24 array-element marker branch stays first; child-27 Stfld untouched).

## Probes (hand-checked constants)

Host helper `BuildNeoClrObjVtFieldOwner(a,b,c)` writes the struct field VALUES on
the CLR side, so the probe exercises ONLY the raw Ldfld READ (no dependency on
the sibling Stfld WRITE or Neo float arithmetic). Read-back + assert are IL int
arithmetic.
- **TC1** `o.S.a` host-set to 111 -> IL reads `o.S.a` -> must be 111. On HEAD
  reads objIdx (small int) -> != 111 -> 1/0.
- **TC2** reads `o.S.a`, `o.S.b`, `o.S.c` (three raw Ldfld ops), IL sum. 111+222+
  333 = 666. On HEAD sum is garbage -> 1/0. A wrong-field/hash-collision fix
  would also miss 666.
