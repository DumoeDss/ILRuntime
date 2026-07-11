## ADDED Requirements

### Requirement: CLR-method ref/out parameter marshaling

The Neo VM SHALL marshal a `ref`/`out`-typed CLR-method parameter across the
IL-to-CLR boundary as a typed-ref bridge (deref-read before the call, write-back
after), in both the reflection fallback and the autogen redirect delegate. For
a CLR-method call (the reflection fallback `CLRMethod.Invoke(byte*)` and
the autogen `*_Neo` redirect delegate) with a `ref`/`out`-typed parameter,
the Neo VM SHALL marshal the byref param across the IL->CLR boundary as a
typed-ref bridge: BEFORE the call the reader SHALL read the 8-byte Ref Slot
`(objectIndex, offset)` from the callee param region, dereference it (a
frame-native slot `objectIndex == -1` reads `*(T*)(frameBase + offset)`; an
mStack-object slot `objectIndex >= 0` reads the mStack object's addressed
field), and pass the dereffed VALUE (boxed for a CLR value-type `ref`/`out`)
to the underlying CLR `MethodInfo.Invoke`/`ConstructorInfo.Invoke`. AFTER the
call, for a `ref` or `out` param (NOT a `in`-only/`readonly` param whose
contract forbids mutation), the reader SHALL WRITE BACK the (possibly-mutated)
CLR value through the SAME Ref Slot — a frame-native slot writes
`*(T*)(frameBase + offset)`; an mStack-object slot writes the mStack object's
field — so the caller's local or heap object observes the mutation. The
representation (byref vs by-value) SHALL be determined unconditionally by the
parameter's type token (`pt.IsByRef` on the `IType`, or
`ParameterType.IsByRef` on the CLR `ParameterInfo`), mirroring the per-arm
type-token discriminator used for value-type params (no per-slot runtime
flag). A `ref`/`out` parameter whose element type is a CLR value type WITH
reference fields and NO registered `ValueTypeBinder` SHALL throw a tagged
`NotImplementedException` (the GC refs are not materializable without a
binder — same constraint as the by-value CLR-struct-param path).

#### Scenario: CLR method ref int mutation propagates to the caller's local

- **WHEN** an IL method holds a local `int x = 5;`, calls a CLR method
  `void Bump(ref int v){ v += 10; }` on it, and reads `x` after the call
- **THEN** `x` SHALL equal `15` (the `ref` param deref-read `5`, passed it to
  the CLR method, and the write-back stored `15` through the frame-native Ref
  Slot to the caller's local).

#### Scenario: CLR method out int assigns the caller's local

- **WHEN** an IL method holds `int x;` and calls a CLR method
  `void Produce(out int v){ v = 42; }`, then reads `x`
- **THEN** `x` SHALL equal `42` (the `out` param's post-call write-back stores
  `42` through the Ref Slot; the pre-call deref of the uninitialized local is
  not relied upon).

#### Scenario: CLR method ref struct mutation propagates (pure-primitive binder struct)

- **WHEN** an IL method holds a local `TestVector3 v` (a binder-registered
  pure-primitive struct) and calls a CLR method `void Scale(ref TestVector3 v)`
  that mutates `v`, then reads `v.X`/`v.Y`/`v.Z`
- **THEN** the mutation SHALL be visible in the caller's local (the `ref`
  struct deref reads flat bytes, the CLR method mutates the boxed copy, and the
  write-back stores the mutated flat bytes through the frame-native Ref Slot).

#### Scenario: CLR method out reference type assigns a heap object

- **WHEN** an IL method calls a CLR method `void Make(out string s){ s = "ok"; }`
  and reads the `out` param
- **THEN** the caller SHALL observe `"ok"` (the `out` param's write-back stores
  the string's mStack index through the Ref Slot's offset into the caller's
  ref-typed local / mStack ref slot).

#### Scenario: CLR value type with reference fields via ref/out throws tagged NIE

- **WHEN** an IL method passes a CLR value type WITH reference fields and no
  `ValueTypeBinder` as a `ref`/`out` param to a CLR method (the reflection
  fallback path)
- **THEN** the reader SHALL throw a Step-13b/13-Area-4-tagged
  `NotImplementedException` naming the type (the GC refs are not materializable
  without a binder), rather than silently mis-handle the param.

#### Scenario: No regression on non-byref CLR-method parameters

- **WHEN** the existing NeoStep smoke suite is run after this change
- **THEN** every previously-green CLR-method call (by-value params, return
  values, value-type `this` direct-call from `neo-step13-area4`) remains green
  (the `pt.IsByRef` discriminator fires ONLY for a byref-typed param; every
  by-value path is byte-identical).

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
cached `FieldInfo`) on the mStack object. `stobj`/`ldobj` SHALL copy
`TotalPrimitiveSize` bytes sized by the type token in `Operand`. **The
`TotalReferenceCount` ref-slot portion of a value-type copy through
`stobj`/`ldobj` is PARTIAL this step (primitives only): the ref-slot loop is
deferred to a follow-up, so a value type with reference fields is not yet
correctly copied through `stobj`/`ldobj`. Primitive-field value types (the
green target) are fully supported.**

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

#### Scenario: No regression on the frame-native, ILTypeInstance, and CLR-array paths

- **WHEN** the existing NeoStep smoke suite is run after this change
- **THEN** every previously-green `stind_*`/`ldind_*`/`stobj`/`ldobj` case
  (frame-native, heap-IL field, CLR-array element) remains green (the new
  CLR-object-field branch fires ONLY when `mStack[objIdx]` is neither an
  `ILTypeInstance` nor an `Array`; the discriminator is additive).

### Requirement: ldflda / ldarga address producers

The Neo VM SHALL implement runtime arms for `ldarga`/`ldarga.s` (producing
`(-1, paramFrameOffset)`) and a real `ldflda` arm that dispatches on its
operand. The JIT type-specialization pass SHALL stamp a marker (a flag bit in
the standalone `Operand4` field) on a real `ldflda` whose source operand is an
in-frame IL value type, so the runtime arm distinguishes the in-frame-VT
operand case from the heap-IL / CLR-object operand case. The marker SHALL be
stamped in the pre-lowering type-specialization pass (where the source register
index is still available for type lookup), and SHALL survive `LowerNeoOffsets`
intact (it lives in a standalone, non-union-aliased operand field).

For a CLR-OBJECT operand (the marker is absent and `mStack[objIdx]` is neither
an `ILTypeInstance` nor an `Array`), the arm SHALL stamp the CLR FIELD
IDENTITY into the produced Ref Slot's offset half (a `FieldInfo` handle, a
stable field hash, or a domain-cached field token) so the `stind`/`ldind`/
`stobj`/`ldobj` consumers can resolve it via the field's reflection accessor.
A CLR object field does NOT have a `Primitives[]` byte offset, so the IL
`field.PrimitiveOffset` MUST NOT be used for a CLR operand.

When the marker is present, the runtime arm SHALL produce a frame-native Ref
Slot `(-1, vtBase + field.PrimitiveOffset)` for the in-frame-VT operand,
where `vtBase` is resolved by reading the operand slot's leading int: when the
leading int is `-1`, the operand slot holds a frame-native Ref Slot produced
by a real `ldloca`/`ldarga` (the struct base is the Ref Slot's offset half);
otherwise the operand slot holds the struct's flat primitive bytes (e.g. a
`this` seeded with flat bytes by the `constrained.callvirt` box-once path, or
any in-frame-VT operand whose slot is the struct's primitive region), and the
struct base IS the operand slot's own frame byte offset. When the marker is
absent, the arm SHALL keep the existing dispatch (read the operand slot as a
Ref Slot: `objectIndex == -1` → frame-native; `objectIndex >= 0` → heap-IL /
CLR-object, with the CLR-object sub-case stamping the field identity).

The `addrAlias` folding behavior for `ldflda` SHALL remain unchanged: a dest
whose every consumer is foldable stays folded (runtime arm dead); a dest whose
address escapes the folding window stays real (runtime arm fires, now correctly
for the in-frame-VT-flat-bytes operand via the marker branch AND for the
CLR-object-field operand via the field-identity stamp).

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

#### Scenario: No regression on heap-IL and CLR-object ldflda

- WHEN the existing NeoStep smoke suite is run after this change
- THEN every previously-green heap-IL `ldflda` and CLR-object `ldflda` case
  remains green (the marker is stamped ONLY for an in-frame IL value-type
  operand; a heap-IL or CLR-object operand never carries the marker, so the
  existing heap/CLR branches fire byte-identically).

### Requirement: ref/out IL-parameter call ABI

For an IL-method call with a `ref`/`out` parameter, the caller SHALL copy the
8-byte Ref Slot value into the callee's param region (one primitive-copy entry
of size 8, no ref entry), and the callee SHALL treat the parameter as an 8-byte
byref slot. Mutations the callee makes through the slot (via `stind`/`stobj`)
SHALL propagate to the caller's frame local or heap object referenced by the
slot. The ABI SHALL be identical for `ref` and `out` (an `out` parameter's
referent is uninitialized-on-entry by contract; the runtime enforces no extra
check). Passing a byref onward (the callee re-passes the slot to a nested call)
SHALL preserve the `(objectIndex, offset)` pair through the 8-byte copy. **For
a CLR-method call with a `ref`/`out` parameter, the typed-ref bridge
(deref-read before the call, write-back after) is owned by the
`CLR-method ref/out parameter marshaling` requirement — the call-region copy
itself is identical (8-byte Ref Slot, no deref).**

#### Scenario: out parameter writeback
- WHEN a callee writes through an `out int` param via `stind_i4` and returns
- THEN the caller SHALL observe the written value in the referent local/field.

### Requirement: Deferred byref sub-cases throw tagged NIE

The Neo VM SHALL throw a tagged `NotImplementedException` for byref sub-cases
not supported by this step. The Neo VM is NOT required to support in this step:
(a) the `stobj`/`ldobj` ref-slot portion of a value-type copy through
`stobj`/`ldobj` (a value type WITH reference fields is not yet correctly copied
through `stobj`/`ldobj`; primitive-field value types are fully supported),
(b) ~~CLR-object stind/ldind via field hash~~ **RESOLVED** (the
CLR-object-field `stind`/`ldind`/`stobj`/`ldobj` consumer branch + the
`ldflda` CLR-field-identity stamp shipped in this change),
(c) ~~CLR-method `ref`/`out` parameters~~ **RESOLVED** (the IL-to-CLR byref
typed-ref bridge shipped in this change; a CLR value type WITH reference fields
and no binder still throws a Step-13b-tagged NIE on the reflection path),
(d) generic-byref (`ref T`/`out T` with `T` a generic parameter),
(e) explicit-interface byref, (f) `fixed` unmanaged-pinning blocks,
(g) interface-on-VT-constrained beyond the common shape,
(h) the IL-value-type-with-reference-fields constrained sub-case (the byref
source does not carry the struct's ref-region mStack base). When a Ref Slot
targeting one of the remaining deferred sub-cases is consumed, the relevant arm
SHALL throw a `NotImplementedException` tagged with `Step 17` (or `Step 13b`
for the CLR-binding-owned sub-cases) rather than silently mis-handle it.

#### Scenario: stobj on a VT with reference fields throws tagged NIE

- **WHEN** `stobj` copies a value type that has one or more reference fields
  through a Ref Slot
- **THEN** the arm SHALL throw a Step-17-tagged `NotImplementedException`
  (the ref-slot loop is deferred to a follow-up; only primitive-field value
  types are correctly copied through `stobj`/`ldobj` this step).

#### Scenario: generic-byref throws tagged NIE

- **WHEN** an IL method passes a `ref T`/`out T` (with `T` a generic
  parameter) through a byref consumer
- **THEN** the arm SHALL throw a Step-17-tagged `NotImplementedException`
  (generic-byref remains deferred).
