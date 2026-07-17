# Design: neo-raw-ldfld-array-element

## 1. The defect, precisely

CIL `x = clrStructArray[i].field;` (CLR-struct array, struct-typed field) lowers to
`ldelema <StructType>; ldfld <field>`. The field's declaring type is a CLRType, so
the Neo typed field-splitter (`JITCompiler.cs` `case Code.Ldfld`, `if (type is
ILType)` at `:3081`) leaves the raw `OpCodeREnum.Ldfld`. The raw-Ldfld runtime
handler (`ILIntepreter.Neo.cs:3876-3948`) decodes `typeHash`/`fieldHash` from
`OperandLong`, resolves `ct = CLRType`, `f = ct.GetField(fieldHash)`.

The handler's value-type-owner branch (`ct.TypeForCLR.IsValueType`, `:3890-3902`)
was written by child-4 for the FLAT-BYTES owner (`ldloc`/`ldsfld` of a CLR-struct
local by value): it does
`object boxedOwner = ReadNeoValueType(ct.TypeForCLR, frameBase, ref cur, ownerSz)`
starting at `ownerOff = ip->SrcOffset`, then `fldVal = f.GetValue(boxedOwner)`.

For an ARRAY ELEMENT owner, `ownerOff` holds the `ldelema`-produced 8-byte byref
`(arrIdx, elementIdx)` (`ILIntepreter.Neo.cs:5774-5775`), NOT flat managed bytes.
`ReadNeoValueType` reads `ownerSz` bytes there and reinterprets them as the struct
(`NeoArrElemIntProbe{int A; int B}` is 8 bytes -- exactly the two byref ints), so
`A = arrIdx`, `B = elementIdx`. `f.GetValue` then returns the arrIdx/elementIdx as
the field value. Observed on HEAD: after `arr[1].A = 4242; arr[1].B = 17;`, the
reads give `a = 3` (arrIdx), `b = 1` (elementIdx), sum 4 != 4259 -> `1/0`.

No crash, no NIE. The "array-element field read is deferred" NIE at `:3928-3929`
is in the REF-type declaring branch (`else` at `:3903`), which a struct array
element never reaches (the declaring type is the struct CLRType -> value-type
branch).

## 2. Why runtime `mStack[objIdx] is Array` alone is NOT safe (asymmetry with Stfld)

Child-19's Stfld array fix is runtime-only and safe because a value-type-owner
Stfld is ALWAYS a byref (you write through an address). So `objIdx` (byref first
int) is always -1 (local) or a valid arrIdx, and `mStack[objIdx] is Array` is
unambiguous.

A value-type-owner Ldfld is NOT always a byref: a READ can take the struct by VALUE
(`ldloc`/`ldsfld` -> owner slot = flat managed bytes; confirmed by child-21 TC3's
JIT dump `1:ldsfld r0; 3:ldfld r7,r0` where r0 is the struct value) OR by address
(`ldelema` of a CLR-struct array element -> owner slot = byref). For the flat-bytes
shape, `objIdx = *(int*)(ownerOff)` is the struct's FIRST FIELD VALUE. For most
field values (large ints, float bit-patterns like 0x3F800000) this is out of
mStack range -> a bounds check (`arrIdx >= 0 && arrIdx < mStack.Count`) safely
routes it to flat bytes. But for SMALL-NON-NEGATIVE int fields (0, 1, 2, ...) it
lands IN range, and if `mStack[arrIdx]` is an Array the check false-positives.

Element-type-match guard (`ca.GetType().GetElementType() == ct.TypeForCLR`) narrows
the false-positive to same-typed arrays but does NOT eliminate it: a default-
initialized struct (first int field = 0) plus a same-typed array at `mStack[0]` in
the same frame still collides. CONSTRUCTIBLE => not acceptable for a correctness
fix whose explicit goal is removing the deferred silent-corruption.

(Refined framing reply: the claim "a flat-bytes-local is NEVER an Array" is true of
the SLOT CONTENTS but NOT of `mStack[objIdx]` after dereferencing the first int as
an index. The dereference is the ambiguity.)

## 3. The fix: JIT marker (provable, Neo-consistent)

The owner representation (byref vs flat bytes) is a JIT-time dataflow fact. Neo's
untyped frame requires such facts be resolved at JIT time (the recurring pattern:
child-11 Brtrue_Ref, child-15 ldflda 0x8 offset marker, child-16/21/23 producer
seeding). A marker is the consistent, PROVABLY-correct tool here.

### 3a. JIT (`JITCompiler.cs`)

Add a const next to the existing markers (`:206`):

```
public const int NeoRawLdfldArrayElementByRefMarker = 0x1;
```

(`Operand4` of the raw `Ldfld` is otherwise entirely 0; bit 0x1 is free. The
existing `NeoLdfldaClrStructLocalFieldMarker = 0x8` lives on the Ldflda opcode, a
different opcode's Operand4 namespace -- no collision.)

In `case Code.Ldfld` (`:3075`), the CLRType declaring-type else-branch
(`:3104-3105`), stamp the marker when the owner was produced by `ldelema`. The
owner is `Register1 = baseRegIdx - 1`; the preceding CIL `ldelema` (whose dest is
that same register, per the Ldelema emission `Register1 = baseRegIdx - 2;
baseRegIdx--` at `:2854-2857`) is `ins.Previous`:

```
else
{
    op.OperandLong = ((long)type.GetHashCode() << 32) | (uint)offset.PrimitiveOffset;
    // neo-raw-ldfld-array-element: the owner is a CLR-struct ARRAY ELEMENT byref
    // (ldelema-produced) rather than a flat-bytes local value. The untyped Neo
    // frame cannot distinguish these at runtime (a flat-bytes struct's first int
    // can coincidentally index an Array in mStack), so mark the shape at JIT time.
    // The ldelema immediately precedes the ldfld in `arr[i].field` and its dest
    // register IS the ldfld owner register.
    if (ins.Previous != null && ins.Previous.OpCode.Code == Code.Ldelema)
        op.Operand4 |= NeoRawLdfldArrayElementByRefMarker;
}
```

Reliability of `ins.Previous == Ldelema`: for `arr[i].field` the IL is
`...; ldc.i4 i; ldelema T; ldfld field` -- `ldelema` is the immediate predecessor.
`readonly.`/`constrained.` are PREFIXES (they precede ldelema, not sit between
ldelema and ldfld). No intervening nop-emitting op. The marker is a static bit on
the ldfld op, so it survives `LowerNeoOffsets` (Operand4 is preserved/remapped per
child-2's finding) and `TypeSpecializeNeoOpcodes` (the raw-Ldfld seeding case reads
only OperandLong/Register1).

### 3b. Runtime (`ILIntepreter.Neo.cs`)

In the raw `Ldfld` value-type-owner branch (`:3890`), branch on the marker BEFORE
the flat-bytes `ReadNeoValueType`. Mirror child-19's Stfld array branch
(`:4078-4091`) but for a READ:

```
if (ct.TypeForCLR.IsValueType)
{
    int ownerOff = ip->SrcOffset;
    if ((ip->Operand4 & JITCompiler.NeoRawLdfldArrayElementByRefMarker) != 0)
    {
        // CLR-struct ARRAY ELEMENT owner: the owner slot holds the ldelema
        // byref (arrIdx, elementIdx). Box the element (Array.GetValue gives a
        // boxed copy of the struct), reflection-read the field, marshal to dest
        // by field category below. Symmetric READ of child-19's Stfld array WRITE.
        int arrIdx = *(int*)(frameBase + ownerOff);
        int elementIdx = *(int*)(frameBase + ownerOff + 4);
        Array cArr = (Array)mStack[arrIdx];
        object boxedElem = cArr.GetValue(elementIdx);
        fldVal = f.GetValue(boxedElem);
    }
    else
    {
        // existing flat-bytes local-value path (child-4): box the whole struct,
        // reflection-read the field.
        int ownerSz = Optimizer.GetNeoValueTypeManagedSize(ct.TypeForCLR);
        int cur = ownerOff;
        object boxedOwner = ILIntepreter.ReadNeoValueType(ct.TypeForCLR, frameBase, ref cur, ownerSz);
        fldVal = f.GetValue(boxedOwner);
    }
}
```

The existing dest marshalling (`:3937-3946`) then handles `fldVal` unchanged by
field category (`NeoWritePrimitiveToFrame` / `WriteNeoValueType` / mStack ref
push). `f.GetValue(boxedElem)` returns the field value already boxed by category
(boxed int/float for primitives, boxed struct for nested VT, the object for ref
fields), so the marshalling is identical to the flat-bytes path's.

The `CrossBindingAdaptorType` unwrap at `:3932` stays in the ref-type branch
(unreached for a struct-array element). No other raw-Ldfld path is touched.

## 4. Why the marker and not the guarded runtime (decision record)

- Marker: PROVABLY correct for the canonical `arr[i].field` shape (no false
  positive -- the marker is set IFF the owner was ldelema-produced). Strictly
  better than HEAD (fixes the canonical shape; introduces NO new corruption
  vector). Coverage gap = ref-local indirection (`ref var p = ref arr[i]; p.field`),
  which is the SAME (broken) behavior as HEAD today -> no regression; a documented
  rarer follow-up.
- Guarded runtime: NO JIT change, full coverage (incl. ref-local), but retains a
  CONSTRUCTIBLE silent-corruption window on flat-bytes owners (default-initialized
  struct + same-typed array). Trades one silent-corruption vector (the deferred
  read) for another (the collision). Rejected as primary; kept as fallback.

The portfolio's north star is eliminating silent corruption; a fix that introduces
a new (narrow) silent-corruption vector violates it. The marker upholds it.

## 5. Probe design (already written + verified to fault on HEAD)

`TestCases/NeoStepRawLdfldArrayElementTest.cs`:

- TC1 single-element round-trip: writes A/B to `arr[1]` via the (green) child-19
  Stfld array path, then READS `arr[1].A`/`arr[1].B` via raw Ldfld. Asserts IL-side
  int sum == 4259 (else `1/0`). On HEAD: a=3, b=1, sum=4 -> fault.
- TC2 multi-index decode: writes A/B at indices 0 and 5, reads all four, asserts
  sum == 100. Proves the element-index decode on the READ side.
- TC3 host-built-array isolation: the array is built+populated on the HOST
  (`TestCLRBinding.BuildNeoArrElemProbeArray`), so the probe depends ONLY on the
  raw Ldfld read (no Stfld dependency). Args (7,70,700,7000) -> asserts sum == 7777
  (the design draft's "1477" was a miscalculation; 7+70+700+7000 = 7777, confirmed
  by the runtime dump `s = 7777` after the fix).

All fields are INT (sidesteps the unrelated addi-on-float / conv.i4-float bugs).
Reads are IL-side int arithmetic (no float). The `1/0` fault discipline (child-1/2)
makes a wrong value observably fail. All 3 FAULT on HEAD (DivideByZero, confirmed);
all 3 PASS after the fix (stash-toggle to be run by the apply worker).

## 6. Out of scope

- Ref-local indirection of an array element (`ref var p = ref arr[i]; p.field`) --
  the `ins.Previous == Ldelema` signal does not cover it; HEAD behavior unchanged
  (still corrupts). A follow-up could extend byref-producer tracking in the JIT if a
  live hit surfaces.
- The ref-type declaring branch NIE at `:3928-3929` (unreachable via ldelema on a
  ref-type-element array, which throws at the ldelema guard) -- left as-is (fail-
  soft, symmetry with the Stfld ref-type branch note at `:4118-4130`).
- A CLR struct with reference fields still NIEs inside `ReadNeoValueType`/
  `WriteNeoValueType` (the Step-13b sibling) -- unaffected; `f.GetValue(boxedElem)`
  handles it via reflection if ever reached, but the array element is a blittable
  struct in practice.
