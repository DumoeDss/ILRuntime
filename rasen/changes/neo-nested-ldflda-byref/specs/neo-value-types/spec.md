# neo-value-types

In-frame value-type storage and inline field access for the Neo register VM
(`ENABLE_NEO_MODE`). Value-type locals and temps live as flat, naturally-
aligned bytes in the frame byte region (with their reference fields in the
frame's parallel mStack ref region), and field access on such an in-frame
value type is pure pointer arithmetic -- no `ILTypeInstance`, no descriptor,
no runtime branch.

This capability is distinct from `neo-dispatch` (virtual/interface dispatch),
which is unaffected by value-type storage.

## ADDED Requirements

### Requirement: Nested ldflda-on-byref for a CLR-struct field

The Neo `Ldflda` runtime arm SHALL handle the case where its operand is a byref
produced by a preceding address-of (`ldflda`/`ldsflda`) -- i.e. the ldflda
addresses an INNER field of a struct field (`outer.Struct.field` lowers to
`ldflda Struct; ldflda field`). For a CLR-struct inner field whose declaring
type is a `CLRType`, the JIT SHALL stamp a dedicated marker
(`NeoLdfldaNestedByRefMarker`, bit 0x10 of the Ldflda standalone `Operand4`)
when the CIL predecessor is `Ldflda` or `Ldsflda`. The runtime arm SHALL, when
that marker is set and the operand's objectIndex half is `>= 0`, materialize the
boxed struct field into a self-describing descriptor pushed onto mStack and
produce byref `(tempIdx, 0)`. The descriptor SHALL carry the containing object,
the struct-field offset, the inner field hash, the inner declaring-type hash, and
the materialized boxed struct. The `ldind`/`stind` primitive arms (I4/I8/R4/R8)
SHALL recognize the descriptor (`mStack[objIdx] is NeoNestedFieldAddr`) before
their existing branches and route to a reflection read (`f.GetValue`) or a
reflection write (`f.SetValue`) with explicit origin write-back (F-10 ->
`ManagedObjects[refOff]`; CLR object -> `NeoWriteClrObjectField`). The
frame-native nested chain (`ldloca; ldflda; ldflda`, objectIndex == -1) SHALL be
unaffected (it is handled by the existing `objectIndex == -1` branch). The type
check on the containing object SHALL precede any F-10 flag-bit test (a CLR-object
owner's struct-field offset is a `FieldInfo.GetHashCode()` whose high bit can
collide with the flag).

#### Scenario: nested struct-field read-modify-write on an IL instance (F-10)
- **WHEN** an IL method does `h.Struct.value = 100; h.Struct.value += 50;` on an
  IL-class field `h.Struct` of CLR-struct type (F-10: boxed at
  `ManagedObjects[ReferenceOffset]`)
- **THEN** the nested `+=` (CIL `ldflda Struct; ldflda value; ldind.i4; add;
  stind.i4`) reads 100, adds 50, and writes 150 back to the boxed struct at
  `ManagedObjects[ReferenceOffset]`, and a subsequent read returns 150.

#### Scenario: nested struct-field read-modify-write on a CLR object
- **WHEN** an IL method does `obj.Struct.value += 50` on a CLR-class field
  `obj.Struct` of CLR-struct type
- **THEN** the nested `+=` reads, adds, and writes the inner field back to the
  CLR object's struct field via `NeoWriteClrObjectField`, and a subsequent read
  returns the persisted value.

#### Scenario: frame-native nested ldflda chain is unaffected
- **WHEN** an IL method does `ldloca structLocal; ldflda fieldA; ldflda fieldB`
  (the operand is a frame-native byref with objectIndex == -1)
- **THEN** the existing `objectIndex == -1` branch resolves the chain via
  pointer arithmetic (`vtBase + fieldOffset`) and the nested-byref marker branch
  (gated on objectIndex >= 0) does not fire.
