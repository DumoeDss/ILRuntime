## ADDED Requirements

### Requirement: CLR-struct field of an IL instance addressed via ldflda

The Neo VM SHALL make an `ldflda` of a CLR-struct-typed field of an IL
reference type (a heap `ILTypeInstance`) produce a byref whose runtime
consumers recover the field's actual storage (`ManagedObjects[ReferenceOffset]`),
so that reading/writing through that byref is correct (no `Primitives` OOB, no
silent-zero `default`). When an IL reference type (a heap `ILTypeInstance`) declares an instance field
whose type is a CLR value type (a "CLR-struct field of an IL instance" — e.g.
an async state machine's `<>t__builder` = `AsyncTaskMethodBuilder`, `<>u__1` =
`TaskAwaiter`, or any IL class with a CLR-struct-typed field), the field SHALL
be laid out as a reference slot (the existing `ILType.InitializeFields`
behavior: `referenceOffset++`, NO `primitiveOffset` advance; the boxed struct
SHALL live at `ManagedObjects[ReferenceOffset]`). This layout is UNCHANGED by
this requirement.

The Neo JIT `ldflda` of such a field SHALL produce a byref whose runtime
consumers can recover the field's actual storage (`ManagedObjects[ReferenceOffset]`),
NOT a stale `Primitives` byte offset. The byref encoding SHALL distinguish
"the target is an ILTypeInstance and the offset is a `ManagedObjects` ref-slot
index for a boxed CLR-struct field" from the existing Ref-Slot meaning-classes
(`objectIndex == -1` = frame-native absolute byte offset; `objectIndex >= 0`
with the target an ILTypeInstance = `Primitives` byte offset; `objectIndex >= 0`
with the target a CLR object = field-hash). The JIT SHALL stamp this
discriminator only for the CLR-struct-field-of-IL-instance shape (source
operand is a heap IL reference type AND the addressed field is a CLR value
type); every other `ldflda` shape (heap-IL-primitive-field, heap-IL-ref-field,
CLR-object-field, in-frame IL value type, frame-native) SHALL remain byte-
identical.

The runtime byref consumers (`NeoMarshalByrefFieldToSlot`, used by
`CopyNeoCallArguments` for a byref `Call`/`Newobj` argument; the
`stind_*`/`ldind_*`/`Stobj`/`Ldobj` arms that read a byref) SHALL recognize
the discriminator and route the access to the boxed CLR struct at
`ManagedObjects[ReferenceOffset]`, marshaling it to/from the dest slot via the
existing `ReadNeoValueType`/`WriteNeoValueType` helpers. The existing
ILTypeInstance-`Primitives` and CLR-object-fieldHash consumer branches SHALL
remain byte-identical for non-discriminator byrefs.

The existing `Stfld_Ref`/`Ldfld_Ref` heap arms (which use
`Operand3 = ReferenceOffset` and read/write `ManagedObjects[ReferenceOffset]`)
already handle a CLR-struct field correctly (they treat the boxed struct as a
reference); this requirement does NOT change them. The capability this
requirement adds is the `ldflda`-produced-byref recoverability for the same
storage region.

This is a Neo-only change (`#if ENABLE_NEO_MODE`); the Legacy `Ldflda` arm
(`GetObjectAndResolveReference` + the tagged `StackObject`
`ValueTypeObjectReference` model) is the semantic reference and is NOT
modified. The shared-engine field-layout pass (`ILType.cs`) is NOT modified
(option A: encoding-only fix; no layout change).

#### Scenario: ldflda of a CLR-struct field of an IL instance, read through the byref

- **WHEN** an IL method declares `class C { ClrStruct f; }`, instantiates
  `C c`, sets `c.f = new ClrStruct(...)`, then takes `ref c.f` via `ldflda`
  and reads the struct through the byref (e.g. passes it by value to a CLR
  method that sums its fields, where the byref deref flows through
  `CopyNeoCallArguments` → `NeoMarshalByrefFieldToSlot`)
- **THEN** the read returns the field values the caller assigned (no
  `IndexOutOfRangeException`, no silent-zero `default` struct), proving the
  byref recovered the field's `ManagedObjects[ReferenceOffset]` storage.

#### Scenario: ldflda of a CLR-struct field of an IL instance, written through the byref

- **WHEN** an IL method declares `class C { ClrStruct f; }`, takes
  `ref c.f` via `ldflda`, and writes a new struct through the byref (e.g.
  a `stind`/`Stobj` or a CLR method invoked with the byref that mutates the
  field)
- **THEN** the subsequent `c.f` read returns the value written through the
  byref (the write propagated to `ManagedObjects[ReferenceOffset]`), proving
  the byref write-path also recovered the field's storage.

#### Scenario: an IL instance with multiple CLR-struct fields

- **WHEN** an IL method declares `class C { ClrStruct a; ClrStruct b; }`,
  sets both fields, and takes `ref c.a` and `ref c.b` via `ldflda` (distinct
  `ReferenceOffset`s), reading each through its byref
- **THEN** each byref recovers its OWN field's storage (no cross-clobber;
  the `ReferenceOffset` carried per-byref is correct), and both reads return
  the values assigned.

#### Scenario: a CLR-struct field that itself has a reference-type field (the TaskAwaiter shape)

- **WHEN** an IL method declares `class C { ClrStructWithRef f; }` where
  `ClrStructWithRef` has a reference-type field (e.g. `TaskAwaiter` wraps a
  `Task`), sets `c.f`, and reads `c.f` through the byref
- **THEN** the byref read returns the boxed struct with its reference field
  intact (the `ReadNeoValueType`/`WriteNeoValueType` marshaling preserves
  the boxed struct's reference field), proving the F-10 fix handles the
  async-blocker shape (the awaiter field `<>u__1`).

#### Scenario: stfld/ldfld of a CLR-struct field (regression — already correct)

- **WHEN** an IL method declares `class C { ClrStruct f; }`, sets `c.f = ...`
  via `stfld`, and reads it back via `ldfld` (NOT via `ldflda`/byref)
- **THEN** the read returns the assigned value (the existing `Stfld_Ref`/
  `Ldfld_Ref` heap arms at `Operand3 = ReferenceOffset` already handle this
  shape), proving the F-10 fix did NOT regress the non-`ldflda` field-access
  path.

#### Scenario: a CLR-struct field passed by value after ldflda+deref

- **WHEN** an IL method declares `class C { ClrStruct f; }`, sets `c.f`,
  takes `ref c.f` via `ldflda`, and passes the struct BY VALUE to a CLR
  method (the byref is dereferenced at the copy site into the callee param
  region, then the callee reads the flat bytes)
- **THEN** the callee observes the field values the caller assigned (no
  mStack-index mis-interpretation, no OOB), proving the byref-then-deref-
  to-by-value-param shape (the Step-20 builder-byref → redirect copy site).

#### Scenario: No regression on IL instances with other field types

- **WHEN** the existing NeoStep smoke suite is run after this change
- **THEN** every previously-green case remains green — IL instances with IL-
  primitive fields, IL value-type fields, CLR-reference fields, and CLR-object
  fields (ALL the existing field types) are byte-identical (the discriminator
  is stamped ONLY for the CLR-struct-field-of-IL-instance shape; every other
  `ldflda`/`stfld`/`ldfld`/byref path is unchanged).

#### Scenario: register-reuse across an escaped ldflda byref (Step-17-B1 class)

- **WHEN** an IL method takes `ref c.f` via `ldflda`, the byref escapes the
  `addrAlias` folding window (its dest register is reused by an intervening
  foldable), and the byref is then read
- **THEN** the read returns the field value (no stale/clobbered offset from
  the reuse), proving the F-10 discriminator does NOT perturb the `addrAlias`
  COEXIST gate (the discriminator is on a standalone operand, invisible to
  the COEXIST gate's `Register1`/`Register2`/`Operand2` reads).
