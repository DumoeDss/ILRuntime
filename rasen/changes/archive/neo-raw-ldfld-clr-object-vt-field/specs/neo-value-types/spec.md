# spec.md delta — neo-value-types

## ADDED

### Requirement: Raw Ldfld READ of a field of a CLR-struct field of a CLR object

A raw `Ldfld` whose declaring type is a CLR value type (the raw opcode survives
the typed-splitter because the field's declaring type is a CLRType, not an
ILType) reads a leaf field of a CLR-struct field that itself lives on a CLR
REFERENCE object (`x = obj.Struct.field`). The CIL lowering
`ldflda Struct(on obj); ldfld field(on the struct address)` leaves the raw
`ldfld` owner register holding an 8-byte byref `(objIdx, structFieldHash)`
produced by the ldflda heap-CLR-object branch, where `objIdx` parks the
containing CLR object and `structFieldHash` is the Struct field's
`FieldInfo.GetHashCode()`.

Because the raw-Ldfld value-type-owner branch also handles a flat-bytes owner
(`ldloc structByValue; ldfld field`) and a flat-bytes struct's first int field
can coincidentally index a real object in mStack, the runtime CANNOT
distinguish flat-bytes from a byref safely. The shape SHALL be marked at JIT
time.

The JIT SHALL stamp bit `0x2` of the raw `Ldfld` `Operand4`
(`NeoRawLdfldClrObjectFieldByRefMarker`) when the raw `Ldfld`'s immediate CIL
predecessor is `Code.Ldflda`. This is mutually exclusive with the `0x1` raw-
Ldfld array-element-byref marker (stamped when the predecessor is `Ldelema`):
a CIL instruction has exactly one immediate predecessor. The `0x2` bit lives in
the raw-`Ldfld` Operand4 namespace, which is disjoint from the Ldflda-opcode
markers (0x1/0x2/0x4/0x8) and from the child-23 `Ldfld_Ref` `Operand4 == 0`
guard (which operates on the typed `Ldfld_Ref` opcode, not raw `Ldfld`).

At runtime, when the marker is set, the raw-Ldfld value-type-owner branch
SHALL decode `(objIdx, structFieldHash)`, read the WHOLE struct field via the
containing object's CLRType (`NeoReadClrObjectField(target, structFieldHash)`
-> boxed struct), and reflection-read the leaf field
(`FieldInfo.GetValue(boxedStruct)`). The existing dest marshalling (primitive /
VT / ref) SHALL handle the result unchanged. A frame-native byref
(`objIdx == -1`, from a nested ldflda-on-frame-local) SHALL read the struct's
flat bytes at the byref offset. An IL-instance owner (a CLR-struct field of an
IL instance, the F-10 ManagedObjects-storage sibling) SHALL fail with a tagged
NotImplementedException (deferred, fail-loud), NOT silent corruption.

The flat-bytes owner path (no marker) SHALL remain the final `else`, so the
existing `ldloc structByValue; ldfld field` and raw-Ldfld-CLR-struct-primitive
producers (child-21 seeding) are unregressed.

#### Scenario: read a primitive field of a CLR-struct field of a CLR object
- **WHEN** an IL method executes `int x = obj.Struct.field` where `obj` is a CLR
  reference object, `Struct` is a CLR-struct instance field of `obj`, and
  `field` is a primitive (int) field of `Struct` whose value was set on the CLR
  host side to a known constant
- **THEN** under `ENABLE_NEO_MODE` the raw `Ldfld` is marked with
  `NeoRawLdfldClrObjectFieldByRefMarker`, the runtime decodes the byref, reads
  the whole struct field via `NeoReadClrObjectField`, reflection-reads the leaf
  field, and `x` equals the known constant exactly (no silent corruption).

#### Scenario: read multiple distinct fields preserves per-field resolution
- **WHEN** an IL method reads three distinct primitive fields `a`, `b`, `c` of
  the same CLR-struct field of a CLR object (each via `ldflda Struct; ldfld X`)
  and sums them in IL integer arithmetic
- **THEN** the sum equals the sum of the three host-set values exactly,
  confirming each field's byref offset resolves to the correct FieldInfo (a hash
  collision or a wrong-field read would yield a different sum).

#### Scenario: no regression on the raw-Ldfld value-type-owner surface
- **WHEN** the NeoStep smoke suite is run after this change
- **THEN** the raw-Ldfld flat-bytes path (child-4 CLR-struct-by-value owner,
  child-21 raw-Ldfld-CLR-struct-primitive seeding), the raw-Ldfld array-element
  marker branch (child-24), the raw-Stfld value-type-owner branches (child-4/
  19/27), and the IL-instance CLR-base field path (child-9) all remain green.
