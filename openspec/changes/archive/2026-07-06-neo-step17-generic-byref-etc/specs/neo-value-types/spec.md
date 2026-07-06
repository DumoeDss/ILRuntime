# neo-value-types Delta -- neo-step17-generic-byref-etc

## ADDED Requirements

### Requirement: F-6 and F-10 ldflda markers are mutually exclusive at the JIT producer (F-10-R1)

The Neo JIT type-specialization pass SHALL guarantee that the F-6 in-frame-VT
`ldflda` marker (`NeoLdfldaInlineMarker`, `Operand4` bit `0x1`) and the F-10
CLR-struct-field-of-IL marker (`NeoLdfldaClrStructFieldMarker`, `Operand4` bit
`0x2`) are NEVER both stamped on the same `ldflda` opcode. The two stamping
conditions are orthogonal at the main-JIT body emission site (`IsClrStructFieldOfIL`
keys on the field's DECLARING type being an `ILType`, which is true for an IL
value type as well as an IL reference type; the F-6 condition keys on the
OPERAND's value-category being an in-frame IL value type), so without a gate an
IL value type with a CLR-struct field addressed via `ldflda` receives BOTH
markers (`Operand4 = 0x3`).

The mutual-exclusivity SHALL be enforced at the PRODUCER: in the
`TypeSpecializeNeoOpcodes case Ldflda:` pass (the F-6 stamping site, where the
source operand register's type is available), when the F-6 condition fires
(source is an in-frame IL value type), the pass SHALL CLEAR any F-10 marker the
main-JIT body emission set earlier (`op.Operand4 &= ~NeoLdfldaClrStructFieldMarker`).
The pass runs AFTER the body emission and BEFORE `LowerNeoOffsets`, so the body's
F-10 stamp is already on `Operand4` and the clear is effective; `Operand4` is a
standalone non-union-aliased field so the clear survives lowering intact.

The gate SHALL key on the OPERAND's value-category (in-frame VT vs heap/boxed),
NOT on the declaring type's `IsValueType`. A boxed IL value type (operand is a
heap mStack object from a `box` opcode) whose CLR-struct field is addressed via
`ldflda` is NOT an in-frame VT (the F-6 condition does not fire), so the F-10
marker stays stamped and the runtime F-10 branch (`ManagedObjects[ReferenceOffset]`)
fires correctly for the boxed-VT case. A declaring-type gate
(`!type.IsValueType`) is REJECTED -- it would silently break the boxed-IL-VT-
with-CLR-struct-field case (declaring type is a value type, but the operand is a
heap boxed object that correctly needs F-10).

With the gate, the runtime `Ldflda` arm's F-10-first check
(`clrStructFieldMarker && objIdx >= 0`) can NEVER mis-fire on an in-frame-VT
operand: for an in-frame-VT source, `clrStructFieldMarker` is false, so the arm
routes to the F-6 shape-1/2/3 branches. The runtime check order is UNCHANGED
(the reviewer's recommended runtime F-6-before-F-10 reorder was DISPROVEN -- it
broke 6 NeoStep17 F-6-only probes, 190->184, because shape 3 vs shape 1/2
produce different byrefs for the `objIdx == -1` case every reachable VT `this`/
arg uses today).

This is a Neo-only change (`#if ENABLE_NEO_MODE`, the type-spec pass); the
Legacy `Ldflda` arm is the semantic reference and is NOT modified.

#### Scenario: an IL value type with a CLR-struct field addressed via ldflda inside a constrained-VT direct-call (F-10-R1 reproducer)

- **WHEN** an IL value type `struct V { int prefix; ClrStruct field; }`
  implements an interface `IFace` with a method `M` whose body emits
  `ldflda this.field` (e.g. passes `ref this.field` to a byref helper), and `M`
  is invoked via `constrained.callvirt` on a generic caller
  `R F<T>(T v) where T : struct, IFace` (the constrained direct-call path seeds
  callee slot-0 with the struct's FLAT primitive bytes, so slot-0's leading int
  is the `prefix` field value, NOT `-1`)
- **THEN** the JIT SHALL stamp ONLY the F-6 marker (`Operand4 = 0x1`, NOT `0x3`),
  the runtime `Ldflda` arm SHALL route to the F-6 shape-3 branch (producing a
  frame-native Ref Slot to the field), and the byref SHALL be consumed correctly
  (no `NullReferenceException` from reading the `prefix` value as an mStack
  index, no `IndexOutOfRangeException`). The mutation/read round-trips through
  the field's actual storage.

#### Scenario: a boxed IL value type with a CLR-struct field keeps the F-10 marker

- **WHEN** an IL value type `struct V { ClrStruct field; }` is boxed (via `box`)
  and the boxed instance's CLR-struct field is addressed via `ldflda` (the
  operand is a heap mStack object, NOT an in-frame VT)
- **THEN** the JIT SHALL NOT clear the F-10 marker (the F-6 condition does not
  fire for a heap operand), the F-10 marker stays stamped, and the runtime
  `Ldflda` arm SHALL produce the F-10 byref `(objIdx, ReferenceOffset | flag)`
  so the consumer reads `ManagedObjects[ReferenceOffset]` (the boxed CLR struct).
  (This is the boxed-VT case the declaring-type gate would have broken.)

#### Scenario: a heap IL reference instance with a CLR-struct field keeps the F-10 marker (regression)

- **WHEN** an IL reference type (a `class`) declares a CLR-struct field and the
  field is addressed via `ldflda` (the existing F-10 shape)
- **THEN** the F-10 marker SHALL stay stamped (the operand is a heap IL reference
  instance, F-6 does not fire), the runtime F-10 branch SHALL produce
  `(objIdx, ReferenceOffset | flag)`, and the consumer SHALL read/write
  `ManagedObjects[ReferenceOffset]` -- byte-identical to the pre-gate behavior
  (the 8 `NeoClrStructField_*` probes + the Step-20 builder-byref hot path stay
  green).

#### Scenario: the 6 F-6-only probes are unaffected (the DISPROVEN reorder guard)

- **WHEN** the existing NeoStep17 F-6-only probes (`_TC6_RefInFrameVtField`,
  `_LdfldaInline_*`) are run after the gate
- **THEN** they SHALL stay green (the gate clears F-10 ONLY when F-6 is stamped;
  the F-6 stamping itself is unchanged, and for an in-frame VT WITHOUT a CLR-
  struct field, F-10 was never stamped -- `Operand4` is `0x1` before and after).
  The runtime check order is unchanged, so the `objIdx == -1` -> shape 1/2
  routing every reachable VT `this`/arg uses today is preserved.
