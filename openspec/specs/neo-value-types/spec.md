# neo-value-types

In-frame value-type storage and inline field access for the Neo register VM
(`ENABLE_NEO_MODE`). Value-type locals and temps live as flat, naturally-
aligned bytes in the frame byte region (with their reference fields in the
frame's parallel mStack ref region), and field access on such an in-frame
value type is pure pointer arithmetic -- no `ILTypeInstance`, no descriptor,
no runtime branch.

This capability is distinct from `neo-dispatch` (virtual/interface dispatch),
which is unaffected by value-type storage.

## Requirements

### Requirement: In-frame value-type storage layout

The Neo frame allocator SHALL reserve, for every value-type local, temp, and
parameter of an IL value type, a contiguous byte range in the frame byte
region sized to the type's `TotalPrimitiveSize`, and a contiguous run of
`TotalReferenceCount` slots in the frame's mStack reference region. The
byte range SHALL start at an offset that is naturally aligned to the type's
largest primitive field (recursively, for nested value types), so that
typed pointer casts in field-access opcodes are aligned. This layout SHALL
be identical in shape to an `ILTypeInstance`'s `Primitives` + `ManagedObjects`
so that a later whole-struct copy (Step 12b) or box/unbox (Step 13) is a pure
byte + ref-slot copy with no format conversion.

#### Scenario: Vector3 local field access compiles and runs
- **WHEN** an IL method declares a value-type local `Vector3 v` and performs
  `v.x = 1f; v.y = 2f; float r = v.x + v.y;`
- **THEN** the method compiles under `ENABLE_NEO_MODE` without throwing, the
  frame for that method contains a naturally-aligned byte slot for `v`, and
  executing the method yields `r == 3f`.

#### Scenario: No regression on existing in-frame value-type usage
- **WHEN** the existing NeoStep smoke suite (NeoStep6 through NeoStep11) is
  run after this change
- **THEN** every previously-green case remains green (value-type locals,
  params, and fields already exercised by earlier steps are not corrupted by
  the storage-layout / alignment change).

### Requirement: Inline field-access opcodes

The opcode set SHALL include `Ldfld_<K>_Inline` and `Stfld_<K>_Inline` for
each primitive kind K in {I1, I2, I4, I8, U1, U2, U4, U8, R4, R8, Ref}. The
`ExecuteNeo` arm of each primitive-kind inline opcode SHALL perform a single
typed pointer read/write at `frameBase + owningSlotOffset + fieldOffset` with
no `mStack` lookup, no `ILTypeInstance` resolution, and no conditional
branch. The `Ref` inline opcode SHALL read/write the object via the frame's
mStack reference region at the field's absolute frame-ref index
(`owningSlotRefOffset + fieldReferenceOffset`). These opcodes SHALL be the
only field-access path used when the field's owning operand is an in-frame
value type.

#### Scenario: Primitive field read and write on an in-frame value type
- **WHEN** an IL method reads and writes primitive fields of a stack-resident
  value type (e.g. `v.x = 1; v.y = 2; return v.x + v.y;`)
- **THEN** the JIT emits `_Inline` opcodes for those accesses and the result
  is correct, with the field addresses resolved as `frameBase + slotOffset +
  fieldPrimitiveOffset`.

#### Scenario: Reference field on an in-frame value type
- **WHEN** an IL method declares `struct S { int a; string b; }` and reads
  and writes both `s.a` (primitive) and `s.b` (reference)
- **THEN** the primitive field access uses a primitive `_Inline` opcode on
  the byte region, the reference field access uses `Ldfld_Ref_Inline`/
  `Stfld_Ref_Inline` on the frame mStack ref region, and both values round-
  trip correctly including the null case.

### Requirement: Initobj memset for in-frame value types

The `Initobj` instruction, when applied to an in-frame IL value type, SHALL
zero the type's primitive byte region (`Unsafe.InitBlock(..., 0,
TotalPrimitiveSize)`) and, if the type has reference fields, SHALL also null
every one of the slot's reference-field mStack slots. The CLR value-type
`Initobj` path SHALL remain unimplemented (deferred to Step 13) and continue
to throw a Step-13-tagged `NotImplementedException`.

#### Scenario: Initobj zeroes a value-type local with reference fields
- **WHEN** an IL method declares `S s` (with a reference field), executes
  `Initobj s`, and then reads `s.a` and `s.b`
- **THEN** `s.a` reads as the primitive zero, `s.b` reads as null, and no
  `NotImplementedException` is thrown for the IL value-type path.

### Requirement: JIT type-based lowering selects the inline variant

The JIT compiler SHALL emit the `_Inline` field-access opcode if and only if
the field-access operand register is an in-frame value type (a value-type
local, temp, or parameter that is not boxed and not a reference slot). When
the operand is a reference slot (heap `ILTypeInstance`, CLR object, or boxed
value type), the JIT SHALL emit the existing heap-object `Ldfld_*`/`Stfld_*`
opcode. The discriminator SHALL be the operand register's value-category at
the IL site, not the field's declaring type (which is identical in both
cases). The heap-object field-access opcodes and their `ExecuteNeo` arms
SHALL remain unchanged.

#### Scenario: Heap object field access still uses the heap path
- **WHEN** an IL method reads a field of a heap-allocated ILTypeInstance
  (`class C { int x; }`, `C c = new C(); c.x = 5; return c.x;`)
- **THEN** the JIT emits the existing heap `Ldfld_I4`/`Stfld_I4` opcodes
  (operand is a reference slot), the heap `ExecuteNeo` arms handle it, and
  the result is correct -- no `_Inline` opcode is emitted and no regression
  vs the pre-Step-12 behavior.

### Requirement: Nested value-type field access

Field access into a nested value type (a value-type field of a value type,
e.g. `outer.inner.x`) SHALL resolve to a single `_Inline` access at
`frameBase + outerSlotOffset + absoluteNestedFieldOffset`, using the
per-field offset machinery already computed by `ILType.InitializeFields`
(which accumulates nested value-type primitive/reference offsets into the
outer type's field offsets). Loading the entire nested value type as a value
(whole-struct copy) is NOT required by this capability -- it is deferred to
Step 12b.

#### Scenario: Nested value type field access
- **WHEN** an IL method declares `struct Inner { int x; } struct Outer {
  Inner i; int y; }`, creates an `Outer` local, and reads/writes `o.i.x`
  and `o.y`
- **THEN** each field access resolves to a single `_Inline` opcode at the
  correct absolute offset and the values round-trip correctly, with no
  whole-struct copy performed.
