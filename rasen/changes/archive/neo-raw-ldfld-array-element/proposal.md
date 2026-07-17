# Proposal: neo-raw-ldfld-array-element

## Why

A raw `Ldfld` whose owner is a **CLR-struct array element** -- the CIL shape
`x = clrStructArray[i].field;` (lowered to `ldelema <StructType>; ldfld <field>`)
-- SILENTLY CORRUPTS under Neo. Child-19 (`neo-raw-stfld-array-element`) shipped
the symmetric WRITE (`clrStructArray[i].field = x`) and DEFERRED the read with the
note: "Ldfld array-element DEFERRED (not symmetric; untyped frame can't safely
distinguish flat-bytes-local vs array-element-byref without a JIT marker -- latent
silent-corruption if exercised)."

This child is the re-audit + fix of that deferred read.

## Re-audit verdict (REPRODUCES -- not disproven)

A minimal probe (`TestCases/NeoStepRawLdfldArrayElementTest.cs`, 3 TCs) FAULTS on
HEAD `7689c27c` with `DivideByZeroException` (the deliberate `1/0` on value
mismatch) for ALL three TCs. The interpreter "Local Variables" dump is the smoking
gun. After the (green, child-19) writes `arr[1].A = 4242; arr[1].B = 17;`, the
reads return:

```
Int32 a = 3, Int32 b = 1, Int32 s = 4
```

i.e. `a = arrIdx` (the array's mStack index, 3) and `b = elementIdx` (1). The raw
`Ldfld` value-type-owner branch (`ILIntepreter.Neo.cs:3890-3902`) reads the owner
slot as FLAT MANAGED BYTES via `ReadNeoValueType`. For an array element the owner
slot holds the `ldelema`-produced 8-byte byref `(arrIdx, elementIdx)`
(`ILIntepreter.Neo.cs:5774-5775`) -- so the two byref ints are reinterpreted as the
struct's first two fields. No crash, no NIE: pure silent corruption. (The tagged
"array-element field read is deferred" NIE at `:3928-3929` lives in the REF-type
declaring branch, which is UNREACHABLE for a struct array element -- `ldelema` on a
ref-type-element array throws at the ldelema guard.)

## Central question: runtime detection vs JIT marker

Child-19's Stfld fix detected the array owner at RUNTIME via
`mStack[objIdx] is Array` (NO JIT marker). The framing asks whether the Ldfld READ
is symmetrically tractable the same way, and claims the check is "unambiguous
(an array owner is ALWAYS `mStack[objIdx] is Array`; a flat-bytes-local is NEVER an
Array)."

**The re-audit REFUTES the "unambiguous" claim for Ldfld.** The asymmetry is real
and is the crux:

- **Stfld value-type owner is ALWAYS a byref** (`(-1, off)` from `ldloca`, or
  `(arrIdx, elementIdx)` from `ldelema`). A value-type WRITE always goes through the
  struct's address; there is no flat-bytes owner. So `objIdx` (the byref's first
  int) is always a well-formed mStack index (or -1), and `mStack[objIdx] is Array`
  is unambiguous. This is why child-19 needed no marker.
- **Ldfld value-type owner is EITHER flat bytes OR a byref.** A READ can take the
  struct by value (`ldloc`/`ldsfld` of a CLR struct local -> the owner slot holds
  the struct's flat managed bytes, confirmed by child-21 TC3's JIT dump:
  `ldsfld r0; ldfld r7,r0` where r0 is the struct VALUE) OR by address (`ldelema`
  of a CLR-struct array element -> the owner slot holds the byref). The raw `Ldfld`
  handler receives BOTH shapes in the SAME `SrcOffset` slot.

For a flat-bytes owner, `objIdx = *(int*)(ownerOff)` is the struct's FIRST FIELD
VALUE (e.g. an int 0, 1, 2, ...), NOT an mStack index. `mStack[objIdx]` therefore
dereferences a garbage index. It is out of range for most field values (large ints,
float bit-patterns) -- safe -- but for SMALL-NON-NEGATIVE int fields it lands IN
range, and if `mStack[objIdx]` happens to be an `Array` the check FALSE-POSITIVES
-> silent corruption. This is CONSTRUCTIBLE (e.g. a default-initialized struct
whose first int field is 0, with a same-typed array at `mStack[0]` in the frame).
An element-type-match guard (`cArr.GetType().GetElementType() == ct.TypeForCLR`)
narrows the window to same-typed arrays but does NOT eliminate it (the default-
struct + same-typed-array collision survives).

So: **a plain runtime `mStack[objIdx] is Array` check is NOT safe for Ldfld the
way it is for Stfld.** This is exactly the ambiguity child-19's deferral note
flagged, and the re-audit confirms it is real, not theoretical.

## The fix (recommended: JIT marker -- provable, Neo-consistent)

The Neo frame is untyped, so the byref-vs-flat-bytes owner representation -- a
JIT-time dataflow fact -- MUST be determined at JIT time, exactly as Neo already
does for the ref-vs-int distinction (child-11 Brtrue_Ref, child-15 ldflda offset
marker, child-16/21/23 producer seeding). This is the architecturally consistent
fix and it is PROVABLY correct (no collision window): when the marker is set the
owner is unambiguously an array-element byref; when clear, flat bytes.

- `Operand4` is FREE for the raw `Ldfld` (CLRType declaring owner): the JIT's
  `case Code.Ldfld` CLRType else-branch (`JITCompiler.cs:3104-3105`) sets only
  `OperandLong`; child-21's `TypeSpecializeNeoOpcodes` raw-Ldfld seeding case
  (`JITCompiler.cs:1089-1105`) reads only `OperandLong`/`Register1`. A new marker
  bit in `Operand4` (named const, e.g. `NeoRawLdfldArrayElementByRefMarker`)
  conflicts with nothing.
- The JIT signal is reliable for the canonical shape: in `case Code.Ldfld` CLRType
  branch, when `ins.Previous.OpCode.Code == Code.Ldelema` (the `ldelema; ldfld`
  pair of `arr[i].field` -- `ldelema`'s dest register IS `ldfld`'s owner register,
  verified via the Ldelema/Ldfld `baseRegIdx` emission), stamp the marker. The
  `readonly.`/`constrained.` prefixes do not intervene between `ldelema` and
  `ldfld`. The marker is a static bit on the `ldfld` op, so it survives the
  optimizer's `LowerNeoOffsets` (Operand4 is preserved/remapped per child-2).
- Runtime: in the raw `Ldfld` value-type-owner branch, if the marker is set, decode
  the byref `(arrIdx, elementIdx)` and do the symmetric READ of child-19's WRITE:
  `object boxedElem = cArr.GetValue(elementIdx); fldVal = f.GetValue(boxedElem);`
  then reuse the existing dest marshalling (`NeoWritePrimitiveToFrame` /
  `WriteNeoValueType` / mStack ref push by field category). If clear, the existing
  flat-bytes `ReadNeoValueType` + `f.GetValue` path runs unchanged.

**Scope / coverage gap (documented):** the `ins.Previous == Ldelema` signal covers
the direct `arr[i].field` shape (the reachable, common case). A ref-local
indirection (`ref var p = ref arr[i]; x = p.field;` -> `ldelema; stloc p(ref);
ldloc p; ldfld`) is NOT covered by the marker and falls to the flat-bytes path
(the SAME behavior as HEAD today -- no regression, just an unfixed rarer shape; a
follow-up could broaden the byref-producer tracking). This is strictly better than
HEAD (fixes the canonical shape, introduces NO new corruption vector).

## Rejected alternative (element-type-guarded runtime detection)

A runtime-only branch in the value-type-owner path: `if (arrIdx >= 0 && arrIdx <
mStack.Count && mStack[arrIdx] is Array ca && ca.GetType().GetElementType() ==
ct.TypeForCLR && elementIdx < ca.Length) { array read } else { flat bytes }`. This
needs NO JIT change (lower regression risk) and covers the ref-local shape too,
BUT it retains a CONSTRUCTIBLE silent-corruption window (default-initialized struct
whose first int field indexes a same-typed array in mStack). For a correctness fix
whose explicit goal is eliminating the deferred silent-corruption, introducing a
new (narrow) silent-corruption vector is the wrong trade. Kept as a FALLBACK only
if the JIT marker signal proves fragile under stash-toggle.

## What changes

- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- new marker const +
  stamp in `case Code.Ldfld` CLRType branch when `ins.Previous` is `Ldelema`
  (~3-5 lines, Neo-gated).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- raw `Ldfld`
  value-type-owner branch: marker check + array-element read mirroring child-19's
  Stfld array branch (~10-15 lines, Neo-gated).
- `TestCases/NeoStepRawLdfldArrayElementTest.cs` -- the 3 re-audit probes (TEMP ->
  ship as regression probes; names embed "NeoStep"). Plus the
  `BuildNeoArrElemProbeArray` host helper in `TestClass3.cs` (TC3 host-built-array
  isolation).

Neo-gated (`#if ENABLE_NEO_MODE`) => Legacy-neutral by construction.

## Capability

`neo-value-types` (owns in-frame value-type storage / CLR-struct field access; the
sibling `neo-raw-stfld-array-element` ADDED the Stfld requirement here). ADDED a
new requirement pinning the raw `Ldfld` array-element read.

## Verify

Stash-toggle: with the runtime branch removed/marker-unset, 3/3 probes FAULT
(DivideByZero on the corrupted sum); with the fix, 3/3 PASS. NeoStep smoke
baseline **365/0** on HEAD `7689c27c` -> **368/0** (365 + TC1/TC2/TC3). The typed
field arms, child-4's flat-bytes/CLR-ref raw-owner arms, child-9's IL-instance-
CLR-base arm, and child-19's Stfld array arm SHALL be unregressed. Legacy-neutral:
plain `Debug` + `useRegister=true` + `NeoStep` unchanged.
