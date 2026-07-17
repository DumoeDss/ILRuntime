## MODIFIED Requirements

### Requirement: ldflda / ldarga address producers

The Neo VM SHALL implement runtime arms for `ldarga`/`ldarga_s` (producing
`(-1, paramFrameOffset)`) and a real `ldflda` arm that dispatches on its
operand. The JIT type-specialization pass SHALL stamp a marker (a flag bit in
the standalone `Operand4` field) on a real `ldflda` whose source operand is an
in-frame IL value type, so the runtime arm distinguishes the in-frame-VT
operand case from the heap-IL / CLR-object operand case. The marker SHALL be
stamped in the pre-lowering type-specialization pass (where the source register
index is still available for type lookup), and SHALL survive `LowerNeoOffsets`
intact (it lives in a standalone, non-union-aliased operand field). The JIT
body emission SHALL additionally stamp a DISTINCT marker bit (in the same
standalone `Operand4` field, disjoint from the in-frame-VT / CLR-struct-field-
of-IL / heap-IL-ref-field marker bits) on a real `ldflda` whose field's
declaring type is a `CLRType`, so the runtime arm can discriminate the CLR-
struct-local operand case from the IL-struct-local case without a per-call type
resolution.

When the in-frame-VT marker is present, the runtime arm SHALL produce a frame-
native Ref Slot `(-1, vtBase + field.PrimitiveOffset)` for the in-frame-VT
operand, where `vtBase` is resolved by reading the operand slot's leading int:
when the leading int is `-1`, the operand slot holds a frame-native Ref Slot
produced by a real `ldloca`/`ldarga` (the struct base is the Ref Slot's offset
half); otherwise the operand slot holds the struct's flat primitive bytes (e.g.
a `this` seeded with flat bytes by the `constrained.callvirt` box-once path, or
any in-frame-VT operand whose slot is the struct's primitive region), and the
struct base IS the operand slot's own frame byte offset.

When the operand is a FRAME-NATIVE Ref Slot (`objectIndex == -1`) addressed to a
CLR value-type local AND the field's declaring type is a `CLRType` (the CLR-
struct-local marker is present), the runtime arm SHALL produce a frame-native
Ref Slot `(-1, vtBase + fieldManagedByteOffset)` where `fieldManagedByteOffset`
is the field's REAL managed byte offset within the struct's flat bytes -- NOT
`field.PrimitiveOffset` (which is the `FieldInfo` hash for a CLR declaring type
and MUST NOT be used as a byte offset). The real byte offset SHALL be resolved
via `Marshal.OffsetOf(clrStructType, fieldName)` and cached per
`(declaringTypeHash, fieldHash)` so subsequent executions are an O(1) lookup.
This is sound because the only CLR value types that reach the Neo flat-byte
local path are blittable (a CLR value type with reference fields throws the
Step-13b tagged `NotImplementedException` inside `ReadNeoValueType`/
`WriteNeoValueType` before materializing as a frame local), and for a blittable
value type the managed layout written by `Unsafe.WriteUnaligned` is identical to
the `Marshal.OffsetOf` offset. If `Marshal.OffsetOf` cannot resolve the offset
(an auto-layout struct -- an unreachable edge for the blittable-only path), the
arm SHALL throw a tagged `NotImplementedException` naming the struct rather than
silently mis-using the hash.

For the remaining cases (the in-frame-VT marker is absent and the operand slot
is a Ref Slot), the arm SHALL keep the existing dispatch: `objectIndex == -1`
with no CLR-struct-local marker is an IL-struct-local field (the offset half is
a real `Primitives` byte offset); `objectIndex >= 0` is heap-IL / CLR-object,
with the CLR-object sub-case stamping the field identity.

For a CLR-OBJECT operand (the operand's `mStack[objIdx]` is neither an
`ILTypeInstance` nor an `Array`), the arm SHALL stamp the CLR FIELD IDENTITY
into the produced Ref Slot's offset half (a `FieldInfo` handle, a stable field
hash, or a domain-cached field token) so the `stind`/`ldind`/`stobj`/`ldobj`
consumers can resolve it via the field's reflection accessor. A CLR object field
does NOT have a `Primitives[]` byte offset, so the IL
`field.PrimitiveOffset` MUST NOT be used as a byte offset for a CLR operand.

The `addrAlias` folding behavior for `ldflda` SHALL remain unchanged: a dest
whose every consumer is foldable stays folded (runtime arm dead); a dest whose
address escapes the folding window stays real (runtime arm fires, now correctly
for the in-frame-VT-flat-bytes operand via the in-frame-VT marker branch, for
the CLR-object-field operand via the field-identity stamp, AND for the CLR-
struct-local-field operand via the managed-byte-offset resolution).

#### Scenario: ldarga of a struct method's this

- WHEN a value-type instance method executes `ldarga.s 0` for `this`
- THEN the arm SHALL produce `(-1, thisParamFrameOffset)` consumable by
  `stind`/`ldind` and by a `constrained.` callvirt.

#### Scenario: ldflda on an in-frame VT whose operand is a Ref Slot (ldloca/ldarga dest)

- WHEN an IL method emits `ldloca V; ldflda f` (or `ldarga this; ldflda f` in a
  struct instance method called via a direct `call`) and the `ldflda` dest's
  address escapes the `addrAlias` folding window (consumed by `stind`/`ldind`/
  `stobj`/`ldobj`/a byref `Call` arg/a `constrained.` box path)
- THEN the runtime `ldflda` arm SHALL read the operand slot's leading int as
  `-1`, resolve the struct base from the Ref Slot's offset half, and produce a
  frame-native Ref Slot `(-1, vtBase + field.PrimitiveOffset)` consumable by
  the byref consumers.

#### Scenario: ldflda on an in-frame VT whose operand holds flat bytes (the constrained-boxed-this shape)

- WHEN an IL struct override (e.g. `ToString()`) is invoked via
  `constrained.callvirt` (the box-once path seeds the override's `this` param
  slot with the struct's flat primitive bytes), and the override body emits
  `ldflda this.field` (e.g. `id.ToString()` lowering to
  `ldflda this.id; constrained.callvirt Int32.ToString`)
- THEN the runtime `ldflda` arm SHALL recognize (via the JIT-stamped marker)
  that the operand is an in-frame VT, read the operand slot's leading int as a
  field value (not `-1`, not an mStack index), and produce a frame-native Ref
  Slot `(-1, operandSlotFrameOffset + field.PrimitiveOffset)` whose offset half
  is the operand slot's own frame byte offset plus the field's primitive
  offset; the consumer (`ldind`/`stind`/`constrained.callvirt`) SHALL read/
  write the field correctly through that Ref Slot.

#### Scenario: IL struct ToString override whose body calls a field method (the Step-17 deferred positive test)

- WHEN an IL struct `S { int id; }` overrides `ToString()` with a body that
  calls `id.ToString()` (which the C# compiler lowers to `ldflda this.id;
  constrained.callvirt Int32.ToString`), and an IL method invokes `s.ToString()`
  on an in-frame `S` local
- THEN the override SHALL receive the correct boxed `this`, the `ldflda`
  SHALL produce a correct frame-native Ref Slot to `id`, the constrained
  `Int32.ToString` SHALL box the field value, and the result SHALL equal
  `"Named:" + id` (no garbage, no crash, no `NotImplementedException`).

#### Scenario: ldflda on a nested struct field

- WHEN an IL method emits `ldloca outer; ldflda inner; ldflda x` (a nested-
  field address chain) and the leaf address escapes the folding window
- THEN the leaf `ldflda` SHALL carry the marker (its operand is the inner
  `ldflda` dest, an in-frame VT) and produce a frame-native Ref Slot at the
  accumulated absolute offset, consumable by byref consumers (provided no
  intervening `Ldfld_Value` whole-VT load — that is the separate Step-12b
  deferral).

#### Scenario: ldflda on a struct's reference-type field (the ref region)

- WHEN an IL method emits `ldflda s.objField` on an in-frame struct `s` whose
  field `objField` is a reference type, and the address escapes the folding
  window
- THEN the produced frame-native Ref Slot's offset half SHALL point at the
  field's slot, and a `ldind.ref`/`stind.ref` consumer SHALL read/write the
  corresponding mStack ref slot (the ref-region sub-case).

#### Scenario: ldflda on a CLR-object field stamps the field identity

- **WHEN** an IL method emits `ldflda clrObj.field` on a CLR object (the
  operand's `mStack[objIdx]` is neither an `ILTypeInstance` nor an `Array`),
  and the dest's address escapes the folding window
- **THEN** the runtime `ldflda` arm SHALL produce a Ref Slot whose offset half
  encodes the CLR field identity (NOT `field.PrimitiveOffset`), so the
  `stind`/`ldind`/`stobj`/`ldobj` consumers can resolve the field via its
  reflection accessor on the mStack object.

#### Scenario: ldflda on a CLR-struct-local field resolves the real managed byte offset

- **WHEN** an IL method emits `ldloca v; ldflda f` (or `ldarga this; ldflda f`
  in a CLR-struct instance method called via a direct `call`) where `v` is a
  CLR value-type local (inline flat bytes in the frame) and `f` is a field of
  that CLR struct, and the `ldflda` dest's address escapes the `addrAlias`
  folding window (consumed by `stind_*`/`ldind_*`/`stobj`/`ldobj`)
- **THEN** the runtime `ldflda` arm SHALL read the operand slot's leading int
  as `-1`, recognize (via the JIT-stamped CLR-struct-local marker) that the
  field's declaring type is a `CLRType`, resolve the field's REAL managed byte
  offset within the struct's flat bytes (via cached `Marshal.OffsetOf`, NOT the
  `field.PrimitiveOffset` FieldInfo hash), and produce a frame-native Ref Slot
  `(-1, vtBase + fieldManagedByteOffset)`; a following `stind_*`/`ldind_*` SHALL
  read/write the field correctly through that Ref Slot (no access violation, no
  silent corruption).

#### Scenario: No regression on heap-IL and CLR-object ldflda

- WHEN the existing NeoStep smoke suite is run after this change
- THEN every previously-green heap-IL `ldflda` and CLR-object `ldflda` case
  remains green (the in-frame-VT marker is stamped ONLY for an in-frame IL
  value-type operand; the CLR-struct-local marker is stamped ONLY for a
  `CLRType` declaring type; a heap-IL or CLR-object operand carries neither
  in-frame-VT nor CLR-struct-local resolution, so the existing heap/CLR branches
  fire byte-identically).

#### Scenario: Register reuse / escape probe (the Step-17-B1 silent-corruption class)

- WHEN an IL method produces a byref via `ldflda` on an in-frame VT, the dest
  register is then reused by an intervening operation, and FINALLY the byref
  is read
- THEN the read SHALL observe the field's current value (no stale/clobbered
  value from the intervening reuse); the `liveAliasMap` per-instruction
  snapshot correctly tracks the `ldflda` dest's live range across the reuse,
  and the marker branch produces the correct Ref Slot for the dest's own live
  range.
