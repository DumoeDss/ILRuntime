## MODIFIED Requirements

### Requirement: stind / ldind / stobj / ldobj dispatch

The Neo VM SHALL implement `stind_*`, `ldind_*`, `stobj`, and `ldobj` arms
that read the address Ref Slot and dispatch on `objectIndex`: for
`objectIndex == -1` the arm SHALL read/write the frame byte region at the
offset (or the frame mStack ref region when the slot encodes a ref-region
index); for `objectIndex >= 0` with an `ILTypeInstance` target the arm SHALL
read/write the object's pinned `Primitives` (and, for the `Ref` variants or for
`stobj`/`ldobj` over a value type, the corresponding `ManagedObjects` entry);
for `objectIndex >= 0` with a CLR `Array` target the arm SHALL route to
`Array.GetValue`/`Array.SetValue` (the element-index encoding); for
`objectIndex >= 0` with a CLR OBJECT target (neither an `ILTypeInstance` nor an
`Array`) the arm SHALL resolve the Ref Slot's offset half as a CLR FIELD
IDENTITY stamped by `ldflda` for a CLR field, and route the read/write to that
field via the field's reflection accessor (`GetFieldValue`/`SetFieldValue` or a
cached `FieldInfo`) on the mStack object.

`stobj`/`ldobj` SHALL copy the FULL value type: `TotalPrimitiveSize` bytes
sized by the type token in `Operand` for the primitive half, PLUS the
`TotalReferenceCount` ref-slot portion for the reference-region half. For a
frame-native byref (`objectIndex == -1`) addressing a DIRECT local, the arm
SHALL recover the source/dest local's ref-region mStack base (the byref carries
the primitive byte offset but not the ref base) by locating the local whose
frame byte `Offset` equals the byref's offset half, and SHALL copy
`TotalReferenceCount` ref slots between the source and dest ref regions
(mirroring `Move_Vt`'s byte + ref copy). For an mStack-object byref
(`objectIndex >= 0`, an `ILTypeInstance` target), the arm SHALL copy the ref
half via the ILTypeInstance's `ManagedObjects` (reusing the
`CopyFrameToIL`/`CopyILToFrame` helpers, which iterate `ManagedObjects`). The
copy SHALL be a shallow ref-slot copy (both the source and dest reference the
same object), matching C# value-type copy semantics. A primitive-field value
type (`TotalReferenceCount == 0`) SHALL copy only the primitive bytes
(byte-identical to the prior primitives-only behavior). A frame-native byref
that does NOT resolve to a direct local (a nested-field address produced by
`ldloca outer; ldflda innerField` where the leaf is a value type with reference
fields) SHALL throw a Step-17-tagged `NotImplementedException` (the rare edge;
the `localInfos` recovery does not resolve a nested-field offset). A CLR value
type WITH reference fields and no registered `ValueTypeBinder` SHALL throw a
Step-13b-tagged `NotImplementedException` on the reflection path (the GC refs
are not materializable without a binder -- same constraint as the by-value
CLR-struct-param path).

#### Scenario: stind_i4 through a frame-native ref mutates the caller's local
- WHEN `void Inc(ref int x){...}` writes via `stind_i4` on the byref param
- THEN the mutation SHALL be visible in the caller's frame local after return.

#### Scenario: ldind_i4 through a heap-IL field ref reads the field
- WHEN `ldind_i4` reads a Ref Slot produced by `ldflda` of a heap IL field
- THEN the arm SHALL read the field's current value from the ILTypeInstance's
  pinned `Primitives` at the field offset.

#### Scenario: stind_i4 through a CLR-object-field ref writes the field

- **WHEN** an IL method emits `ldflda clrObj.intField` (a CLR object field
  address) and `stind_i4` consumes it to write an `int`
- **THEN** the arm SHALL resolve the Ref Slot's offset half as the CLR field
  identity, route the write via the field's reflection accessor on the mStack
  object, and the mutation SHALL be visible in subsequent reads of
  `clrObj.intField` (no `NotImplementedException`, no `InvalidCastException`).

#### Scenario: ldind_i4 through a CLR-object-field ref reads the field

- **WHEN** an IL method emits `ldflda clrObj.intField` and `ldind_i4` reads it
- **THEN** the arm SHALL resolve the CLR field identity and return the field's
  current value via the field's reflection accessor.

#### Scenario: stobj of a value type WITH reference fields copies the ref half

- **WHEN** an IL method emits `ldloca dst; ldloc src; stobj S` where `S` is a
  value type with one or more reference fields (e.g. `struct S { int x; string
  s; }`), the dest local's ref slot holds a non-null stale canary object, and
  the source's `s` field is a different object (or null)
- THEN the arm SHALL copy BOTH the primitive bytes AND the reference-region
  slots, so the dest's ref slot SHALL hold the source's `s` value (NOT the
  stale canary); the dest and source SHALL reference the same object (shallow
  copy, matching C# struct-copy semantics).

#### Scenario: ldobj of a value type WITH reference fields reads the ref half

- **WHEN** an IL method emits `ldloca src; ldobj S; stloc dst` where `S` is a
  value type with reference fields, the dest local's ref slot is null, and the
  source's ref field is a non-null object
- THEN the arm SHALL copy BOTH the primitive bytes AND the reference-region
  slots, so the dest's ref slot SHALL hold the source's non-null object (NOT
  the dest's stale null).

#### Scenario: stobj/ldobj of a primitive-field value type is byte-identical

- **WHEN** an IL method emits `stobj`/`ldobj` of a value type with
  `TotalReferenceCount == 0` (a primitive-only struct)
- THEN the arm SHALL copy only the primitive bytes (the ref-loop is skipped;
  byte-identical to the prior primitives-only behavior).

#### Scenario: stobj/ldobj on a nested-field byref throws tagged NIE

- **WHEN** an IL method emits `ldloca outer; ldflda innerField; stobj S` (or
  `ldobj S`) where the leaf is a value type with reference fields and the byref
  is a nested-field address that does NOT resolve to a direct local
- THEN the arm SHALL throw a Step-17-tagged `NotImplementedException` naming
  the nested-field shape (the `localInfos` recovery does not resolve a nested-
  field offset; the rare edge).

#### Scenario: No regression on the frame-native, ILTypeInstance, and CLR-array paths

- **WHEN** the existing NeoStep smoke suite is run after this change
- **THEN** every previously-green `stind_*`/`ldind_*`/`stobj`/`ldobj` case
  (frame-native, heap-IL field, CLR-array element, primitive-field value type)
  remains green (the ref-region copy fires ONLY for a value type with
  `TotalReferenceCount > 0`; the discriminator is additive).

### Requirement: Deferred byref sub-cases throw tagged NIE

The Neo VM SHALL throw a tagged `NotImplementedException` for byref sub-cases
not supported by this step. The Neo VM is NOT required to support in this step:
(a) ~~the `stobj`/`ldobj` ref-slot portion of a value-type copy through
`stobj`/`ldobj`~~ **RESOLVED** (a value type WITH reference fields is now
correctly copied through `stobj`/`ldobj` for the direct-local and IL-instance
shapes; the nested-field-via-`ldflda` shape throws a tagged NIE),
(b) ~~CLR-object stind/ldind via field hash~~ **RESOLVED** (the
CLR-object-field `stind`/`ldind`/`stobj`/`ldobj` consumer branch + the
`ldflda` CLR-field-identity stamp shipped in `neo-step17-completion`),
(c) ~~CLR-method `ref`/`out` parameters~~ **RESOLVED** (the IL-to-CLR byref
typed-ref bridge shipped in `neo-step13-area4-refandstind`; a CLR value type
WITH reference fields and no binder still throws a Step-13b-tagged NIE on the
reflection path),
(d) generic-byref (`ref T`/`out T` with `T` a generic parameter),
(e) explicit-interface byref, (f) `fixed` unmanaged-pinning blocks,
(g) interface-on-VT-constrained beyond the common shape,
(h) ~~the IL-value-type-with-reference-fields constrained sub-case~~
**RESOLVED** (the Constrained arm seeds the callee slot-0 ref region /
`CopyFrameToIL` with the real ref base + `TotalReferenceCount`, shipped in this
change). When a Ref Slot targeting one of the remaining deferred sub-cases is
consumed, the relevant arm SHALL throw a `NotImplementedException` tagged with
`Step 17` (or `Step 13b` for the CLR-binding-owned sub-cases) rather than
silently mis-handle it.

#### Scenario: stobj on a nested-field byref of a VT with reference fields throws tagged NIE

- **WHEN** `stobj`/`ldobj` copies a value type that has one or more reference
  fields through a nested-field byref (produced by `ldflda` of a struct field,
  not a direct local)
- THEN the arm SHALL throw a Step-17-tagged `NotImplementedException` (the
  nested-field ref-region recovery is deferred; only the direct-local and
  IL-instance shapes are correctly copied this step).

#### Scenario: generic-byref throws tagged NIE

- **WHEN** an IL method passes a `ref T`/`out T` (with `T` a generic
  parameter) through a byref consumer
- **THEN** the arm SHALL throw a Step-17-tagged `NotImplementedException`
  (generic-byref remains deferred).

#### Scenario: fixed pinning block throws tagged NIE (or is accepted-known)

- **WHEN** an IL method uses a C# `fixed` statement (a pinned byref)
- **THEN** the arm SHALL either throw a Step-17-tagged `NotImplementedException`
  OR, if the address works without GC pinning for the common `fixed`-over-a-
  primitive-array shape, the address SHALL be usable and the lack of GC pinning
  SHALL be an accepted-known limitation (documented in the ship log).
