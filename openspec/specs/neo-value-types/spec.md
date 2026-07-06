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

### Requirement: Whole value-type copy and assignment

The Neo VM SHALL correctly copy an entire in-frame value type on assignment
(`T a = b;`), local initialization, `starg` of a value type, and any other IL
site that lowers to a register `Move` whose destination is an in-frame value
type. The copy SHALL preserve C# value semantics: every primitive/value field
is copied independently so that subsequent mutation of the source does not
affect the destination, while reference fields share object identity (shallow
copy, matching CLR struct copy semantics).

A value-type copy with one or more reference fields (`TotalReferenceCount > 0`)
SHALL be lowered to a dedicated `Move_Vt` opcode whose `ExecuteNeo` arm copies
the destination's full primitive byte range with `Unsafe.CopyBlock` and then
copies each of the destination's reference-field mStack slots individually
(preserving the Neo null convention on both source and destination). A
pure-primitive value type (`TotalReferenceCount == 0`) MAY keep the existing
`Move` opcode, since its byte `CopyBlock` is already correct with no reference
slots to copy.

The JIT SHALL make the copy-vs-no-copy decision at compile time from the
destination register's static type, before Neo offset-lowering overwrites the
register index with a byte offset. The destination slot's primitive size and
reference count SHALL be encoded into the opcode's standalone (non-union-aliased)
Operand fields so they survive offset-lowering intact.

#### Scenario: Pure-primitive value-type copy

- **WHEN** an IL method declares `Vector3 a; Vector3 b; ... b = a;` (a value
  type with only primitive fields, `TotalReferenceCount == 0`)
- **THEN** the assignment executes correctly (all three fields copied), the
  JIT emits a plain `Move` (its byte CopyBlock suffices), and mutating `a.x`
  after the copy does not change `b.x`.

#### Scenario: Value type with a single reference field

- **WHEN** an IL method declares `struct S { int a; string b; }`, assigns
  `S x; ... S y = x;`, and the source has a non-null `b`
- **THEN** after the copy `y.a == x.a`, `y.b` refers to the same string
  instance as `x.b` (shared identity), and mutating `x.a` does not change
  `y.a`. No `NotImplementedException` is thrown and no reference is truncated.

#### Scenario: Value type with multiple reference fields

- **WHEN** an IL method declares `struct S { string p; string q; string r; }`
  with `TotalReferenceCount == 3` and copies `S a = b;` where `b` has distinct
  non-null values for all three reference fields
- **THEN** all three reference fields are copied independently (none truncated,
  none dropped), `a.p == b.p`, `a.q == b.q`, `a.r == b.r` (identity shared per
  shallow-copy semantics), and the JIT emits `Move_Vt` (not the single-ref
  `Move`).

#### Scenario: Nested value type copy

- **WHEN** an IL method declares `struct Inner { int x; string s; } struct
  Outer { Inner i; int y; }`, populates an `Outer` source, and copies
  `Outer o2 = o1;`
- **THEN** the copy reproduces every primitive and reference field of the
  nested value type in one operation (no recursion), the nested `Inner`'s
  primitive and reference fields are correctly placed in the destination, and
  mutating a source value field after the copy does not affect the destination.

#### Scenario: Copy-aliasing independence

- **WHEN** an IL method copies `S b = a;` and then writes a different value to
  a primitive field of `a`
- **THEN** the corresponding field of `b` retains its pre-mutation value (the
  primitive CopyBlock is a value copy, not an alias).

### Requirement: LowerMove JIT pass placement and union safety

The JIT pass that selects `Move_Vt` over `Move` (LowerMove) SHALL run on the
register-index form of the method body, before Neo offset-lowering, and SHALL
derive the destination's value type from the per-register static type map that
the type-specialization pass already maintains. This is required because Neo
offset-lowering overwrites register indices with byte offsets (the `OpCodeR`
explicit-layout union aliases `Register1/2/3` with `DstOffset/SrcOffset/
OperandOffset`), so a register index needed for type lookup is unavailable
after lowering.

The value type's primitive size and reference count SHALL be encoded into the
`Move_Vt` opcode's standalone Operand fields (offsets that do not alias any
register or byte-offset field), not into the aliased register/offset union
fields. This guarantees the encoded data survives offset-lowering and is
readable by the `ExecuteNeo` arm.

The pass SHALL run after the Backwards/Forward Copy-Propagation passes
(BCP/FCP), which operate on `Move` instructions in register-index form and may
elide them. The pass SHALL only ever observe the surviving `Move`s, so it does
not perturb and is not starved by copy-elision.

#### Scenario: BCP/FCP still elide redundant value-type Moves

- **WHEN** a method contains a redundant value-type `Move` that BCP or FCP can
  eliminate (e.g. a self-move `a = a`, or a copy whose result is overwritten
  before use)
- **THEN** BCP/FCP elide the `Move` before LowerMove runs, LowerMove never
  converts it to `Move_Vt`, and the resulting body contains no dead value-type
  copy. Previously-green NeoStep cases that rely on copy-elision of value-type
  Moves remain green.

#### Scenario: Reference-type Moves are unaffected

- **WHEN** a method performs a reference-type assignment (`object o = other;`)
  or a single-reference value-type-field load
- **THEN** the JIT emits a plain `Move` with the existing single-reference-copy
  semantics; LowerMove does NOT rewrite it to `Move_Vt`, and the existing
  reference-move behavior is unchanged.

### Requirement: addrAlias folding is conditional on consumer foldability

The optimizer's `addrAlias` folding (which lets the in-frame-VT field-access
fast path resolve `ldloca`/`ldflda` address chains to compile-time absolute
frame offsets) SHALL remain in effect for any address-producing dest whose
EVERY consumer is a foldable opcode (`_Inline` field opcodes, `Initobj`, or
another foldable `ldflda`). The folding SHALL NOT apply to a dest when any
consumer is a byref-escape opcode (`stind_*`, `ldind_*`, `stobj`, `ldobj`,
`ldelema`, a `Call`/`Newobj`/`Push` argument whose declared parameter
`IsByRef`, or a `constrained.` box path); such a dest is realized as a real
Ref Slot under the `neo-byref` capability. The `ldloca`/`ldflda` runtime arms
SHALL therefore be real Ref-Slot producers, and the no-op-at-runtime behavior
SHALL apply only to dests the folding resolved.

This MODIFIES the prior phrasing (Step 12) under which `ldloca`/`ldflda` were
unconditionally runtime no-ops because no genuine byref path existed. The
in-frame-VT field-access fast path itself (Steps 12-16) is unchanged for the
pure `ldloca;ldflda;stfld/ldfld/initobj` pattern.

#### Scenario: pure inline field access stays zero-overhead
- WHEN `ldloca V; stfld/ldfld _Inline` accesses an in-frame VT field and no
  byref-escape consumer exists
- THEN the dest stays in the `addrAlias` map, the inline opcode uses the folded
  absolute offset, and the `ldloca` arm is dead at runtime (unchanged from
  Steps 12-16).

#### Scenario: address escapes the folding window
- WHEN the same `ldloca V` also feeds a byref `Call` argument or a `stind`
- THEN the dest is removed from the `addrAlias` map and the real `ldloca` arm
  produces a Ref Slot consumable by `neo-byref` opcodes, while any inline field
  consumers that did NOT reference the dest at runtime continue to use their
  own folded offsets.

### Requirement: K1 ldloca-kill soundness preserved across the real ldloca arm

The OPT-HARDEN `ldloca-kill` in FCP (which kills a copy-propagation when its
source or dest is addressed by an `ldloca`) SHALL remain sound once `ldloca`
produces a real Ref Slot for the non-folded case. Taking an address is a
potential-mutation escape regardless of whether the address is later folded or
realized as a Ref Slot, so the kill SHALL continue to fire on the `Ldloca`
opcode. The new real address producers (`ldflda`, `ldarga`, `ldelema`) SHALL be
treated as escapes by the same kill when they address a propagation's source or
dest.

#### Scenario: copy-prop killed when dest is addressed by a real ldloca
- WHEN `b = a` is followed by `ldloca b` feeding a byref-escape consumer
- THEN FCP SHALL NOT rewrite a later `b.field` read to `a.field`, because the
  address-escape makes `b`'s value potentially mutated through the Ref Slot.

### Requirement: IL value-type construction via newobj

The Neo VM SHALL support construction of an IL value type via the `newobj`
instruction, using the caller's frame byte region (the dest register's slot,
sized and aligned for the value type by the frame allocator) as the construction
site. The `newobj` SHALL zero-initialize the dest region before invoking the
ctor, and the ctor's `this` SHALL be a frame-native Ref Slot so that field
assignments inside the ctor propagate into the caller's frame slot. This is the
value-type analog of the Step 8b reference-type newobj and relies on the Step 17
byref/Ref-Slot model for the ctor `this`.

The detailed construction contract (frame zero-init, Ref-Slot `this`, ctor
writeback, base-ctor chain, default-ctor and ctor-with-args scenarios) is owned
by the `neo-newobj` capability; the byref-`this` call-ABI caller contract is
owned by the `neo-byref` capability. This capability owns the **end-to-end
in-frame-address consistency** that makes the two agree: a value-type `this`
(param slot 0 of a VT instance method) and a value-type `newobj` dest SHALL be
tracked as in-frame addresses for ALL field access (ctor `stfld` + caller
`ldfld`), so no method can lower a single logical VT field access to a mix of
in-frame `_Inline` and heap `GetNeoILInstance` arms.

The JIT type-specialization pass SHALL seed the dest register of a `Newobj` of
an IL value type with the constructed VT type (the same rule already applied
for `Ldloca` and `Ldflda` dests), so the field-access discriminator
(`TryRewriteFieldAccessForInline`) recognizes the dest as an in-frame value type
and rewrites the caller's subsequent `ldfld`/`stfld` to the `_Inline` variant.
The heap-object field-access opcodes and their `ExecuteNeo` arms SHALL remain
unchanged.

The `addrAlias`/`liveAliasMap` machinery SHALL additionally treat a value-type
`this` parameter (param slot 0 of a method whose declaring type is an IL value
type) as a pre-existing in-frame address root for the duration of the method
body, so the ctor's `this.field` chains fold to compile-time offsets exactly as
an `ldloca`-produced address would. (A value-type `newobj` dest needs no new
`addrAlias` entry on the caller side -- the dest register IS the owning slot,
resolved directly once typed.)

#### Scenario: IL value-type newobj with a multi-field ctor

- **WHEN** an IL method emits `new S(args)` for an IL value type `S` whose ctor
  sets multiple fields via `this.field = ...` (a mix of primitive and reference
  fields), and the caller reads those fields from the newobj result
- **THEN** the ctor's `this.field =` writes lower uniformly to `_Inline`
  opcodes, the caller's field reads lower uniformly to `_Inline` opcodes, both
  ends resolve against the SAME dest frame byte/ref region, and every field
  round-trips with the value the ctor assigned -- with no heap `ILTypeInstance`
  allocated for the value type and no post-ctor copy-back.

#### Scenario: IL value-type local-form ctor (`VT x = new VT(args)`)

- **WHEN** an IL method executes `S x = new S(args);` (which the C# compiler
  lowers to `ldloca x; call S::ctor`) and then reads `x.field`
- **THEN** the ctor's `this.field =` writes land in the caller's frame slot for
  `x`, the subsequent `x.field` read returns the assigned value, and no opaque
  `NullReferenceException` is thrown. (This is the common C# idiom; it shares
  the VT-`this` in-frame-address root with the newobj-instruction form.)

#### Scenario: VT newobj result read after intervening heap writes / register reuse

- **WHEN** an IL method constructs `new S(args)`, then performs one or more
  unrelated heap writes or call results that reuse eval-stack registers, and
  only THEN reads a field of the newobj result
- **THEN** the field read returns the value the ctor assigned (no stale/clobbered
  value from the intervening operations); the `liveAliasMap` per-instruction
  snapshot correctly tracks the newobj dest's live range across the reuse.

#### Scenario: Nested value type constructed via newobj

- **WHEN** an IL value type `Outer` contains a nested value-type field `Inner
  inner` and is constructed via `new Outer(args)`, where the Outer ctor sets
  `this.inner.x` and `this.inner.y`
- **THEN** the nested-field writes resolve to single `_Inline` accesses at the
  correct absolute offsets in the dest region, and reading `outer.inner.x` /
  `outer.inner.y` from the caller returns the assigned values.

#### Scenario: VT returned from a method then field-read

- **WHEN** an IL method calls a method `S Make()` returning an IL value type,
  stores the result, and reads a field of it
- **THEN** the return-value path (which lowers to a `Move_Vt` into the caller's
  dest slot) produces an in-frame value whose subsequent field read returns the
  value assigned inside `Make`.

#### Scenario: Base-ctor chain does not re-allocate the VT this

- **WHEN** an IL value-type ctor `S(int a) : base(a)` calls a base IL ctor that
  sets a field via `this.field =`
- **THEN** the chained base-ctor `Call` forwards the same in-frame `this`
  (param slot 0) without allocating a fresh frame region, and the field set by
  the base ctor is observable in the caller's dest slot after `new S(a)`.

#### Scenario: No regression on existing in-frame value-type usage

- **WHEN** the existing NeoStep smoke suite (NeoStep6 through NeoStep18) is run
  after this change
- **THEN** every previously-green case remains green (the additive dest-typing
  and the `this`-root extension do not perturb the existing `_Inline` /
  `addrAlias` fast paths for `ldloca`/`ldflda`-rooted accesses).

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

The Neo JIT SHALL stamp a CLR-struct-field-of-IL-instance discriminator ONLY
for this shape (source operand is a heap IL reference type AND the addressed
field is a CLR value type), so the three heap field-access arms — `Ldflda`
(produce a recoverable byref), `Stfld_Ref` (box the source flat bytes via
`ReadNeoValueType` and store at `ManagedObjects[ReferenceOffset]`), and
`Ldfld_Ref` (read the boxed struct at `ManagedObjects[ReferenceOffset]` and
flatten via `WriteNeoValueType`) — ALL address the field's actual storage
consistently. The byref encoding SHALL distinguish "the target is an
ILTypeInstance and the offset is a `ManagedObjects` ref-slot index for a boxed
CLR-struct field" from the existing Ref-Slot meaning-classes
(`objectIndex == -1` = frame-native absolute byte offset; `objectIndex >= 0`
with the target an ILTypeInstance = `Primitives` byte offset; `objectIndex >= 0`
with the target a CLR object = field-hash). Every other `ldflda`/`stfld`/
`ldfld` shape (heap-IL-primitive-field, heap-IL-ref-field, CLR-object-field,
in-frame IL value type, frame-native) SHALL remain byte-identical (the
discriminator fires ONLY for the CLR-struct-field-of-IL-instance shape).

The runtime byref consumers (`NeoMarshalByrefFieldToSlot`, used by
`CopyNeoCallArguments` for a byref `Call`/`Newobj` argument — the Step-20
builder-byref hot path; the `Ldobj`/`Stobj` arms that read a byref) SHALL
recognize the discriminator and route the access to the boxed CLR struct at
`ManagedObjects[ReferenceOffset]`, marshaling it to/from the dest slot via the
existing `ReadNeoValueType`/`WriteNeoValueType` helpers. The existing
ILTypeInstance-`Primitives` and CLR-object-fieldHash consumer branches SHALL
remain byte-identical for non-discriminator byrefs. The `NeoMarshalByrefFieldToSlot`
write-branch elemType-recovery fallback SHALL fail LOUD (a tagged
`NotImplementedException`) when the boxed slot is null/uninit AND the declared
element type is null — never silent corruption.

This is a Neo-only change (`#if ENABLE_NEO_MODE`); the Legacy `Ldflda` arm
(`GetObjectAndResolveReference` + the tagged `StackObject`
`ValueTypeObjectReference` model) is the semantic reference and is NOT
modified. The shared-engine field-layout pass (`ILType.cs`) is NOT modified
(option A: encoding-only fix; no layout change). The `addrAlias`/`liveAliasMap`
machinery is NOT perturbed (the discriminator is on a standalone operand,
invisible to the COEXIST gate).

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

#### Scenario: stfld/ldfld of a CLR-struct field via the boxed-struct ref slot

- **WHEN** an IL method declares `class C { ClrStruct f; }`, sets `c.f = ...`
  via `stfld`, and reads it back via `ldfld` (NOT via `ldflda`/byref)
- **THEN** the read returns the assigned value: the `Stfld_Ref`/`Ldfld_Ref`
  arms, when the F-10 discriminator fires, box/unbox the CLR struct at
  `ManagedObjects[ReferenceOffset]` via `ReadNeoValueType`/`WriteNeoValueType`
  (the F-10 fix made these arms consistent with `Ldflda`), proving the
  non-`ldflda` field-access path round-trips the boxed CLR struct correctly.

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
