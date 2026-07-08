# neo-byref

Unified 8-byte Ref Slot / byref model for the Neo register VM
(`ENABLE_NEO_MODE`): a runtime representation of a *managed address* and the
store/load-indirect dispatch over it, plus the `ldloca`/`ldflda`/`ldarga`/
`ldelema` address producers, the `stind_*`/`ldind_*`/`stobj`/`ldobj` consumers,
the `ref`/`out` IL-parameter call ABI, and the `constrained.`-on-value-type
runtime arm.

This capability is distinct from `neo-value-types` (which owns the in-frame-VT
storage and the `addrAlias` folding fast path for the pure
`ldloca;ldflda;stfld/ldfld` pattern). `neo-byref` owns the *genuine* byref
case (an address that escapes the folding window) and MUST preserve
`neo-value-types`'s folding fast path.

## Requirements

### Requirement: Unified 8-byte Ref Slot representation

The Neo VM SHALL represent a managed address as a Ref Slot occupying 8
contiguous bytes in the frame byte region, encoded as two `int`s: an
`objectIndex` followed by an `offset`. When `objectIndex == -1`, the slot is a
**frame-native** address and `offset` is an absolute frame byte offset (the
referent is `*(T*)(frameBase + offset)`). When `objectIndex >= 0`, the slot is
an **mStack-object** address: `objectIndex` is the mStack index of the object
and `offset` is a byte offset into that object's `Primitives` (for an IL heap
field or array element). A byref-typed slot SHALL have `RefCount == 0` (it owns
no independent mStack reference of its own).

#### Scenario: ldloca produces a frame-native Ref Slot
- WHEN an `ldloca`/`ldloca.s` of an in-frame local executes and its dest has
  NOT been resolved by the `addrAlias` folding (its consumers include a
  byref-escape opcode)
- THEN the runtime arm SHALL write `objectIndex = -1` and `offset = <the
  local's absolute frame byte offset>` into the 8-byte dest slot.

### Requirement: Ref Slot sizing for byref-typed slots

The Neo frame allocator (`AllocateSlotForType`) and the call-param layout
helper (`AllocateNeoCallParamSlot`) SHALL detect a byref-typed local,
parameter, or temp (declared type `IsByRef == true`) and allocate it exactly 8
bytes with 4-byte alignment and `RefCount == 0`, BEFORE any primitive/value-
type/reference branch. The call-param-layout helper SHALL keep its contiguous
`offset += size` contract (no per-param alignment) so autogen CLR binding
`ReadNeo*` readers continue to match the layout.

#### Scenario: byref IL parameter sized 8 bytes on both sides
- WHEN an IL method declares a `ref int`/`out int` parameter
- THEN the callee's param-info slot for that parameter SHALL be 8 bytes, and
  the caller-side source register that the `ldloca`/`ldflda` dest maps to SHALL
  also be an 8-byte slot, so the call's primitive-copy emits one 8-byte entry.

### Requirement: Genuine byref without regressing the addrAlias fast path

The optimizer SHALL keep the `addrAlias` folding (from `neo-value-types`) for
any `ldloca`/`ldflda` dest whose every consumer is a foldable opcode
(`_Inline` field opcodes, `Initobj`, or another foldable `ldflda`). The
optimizer SHALL NOT fold a dest (leaving it to the real Ref Slot arm) when any
consumer is a byref-escape opcode: `stind_*`, `ldind_*`, `stobj`, `ldobj`,
`ldelema`, a `Call`/`Newobj`/`Push` argument whose declared parameter
`IsByRef`, or the box path of a `constrained.` Removing a dest from folding
SHALL make its non-foldable consumers read the real Ref Slot; the previously-
foldable consumers SHALL remain folded (they never referenced the dest at
runtime).

#### Scenario: ldloca consumed only by inline field access stays folded
- WHEN `ldloca V` feeds only `Stfld_*_Inline`/`Ldfld_*_Inline`/`Initobj`
- THEN the dest SHALL remain in the `addrAlias` map, the inline opcodes SHALL
  use the folded absolute offset, and the `ldloca` arm SHALL be dead at
  runtime (the in-frame-VT field-access fast path is unchanged from Steps 12-16).

#### Scenario: ldloca consumed by a byref call escapes folding
- WHEN `ldloca V` feeds a `Call` whose declared parameter `IsByRef`
- THEN the dest SHALL be removed from the `addrAlias` map and the real `ldloca`
  arm SHALL produce a Ref Slot at runtime.

### Requirement: ldflda / ldarga address producers

The Neo VM SHALL implement runtime arms for `ldarga`/`ldarga_s` (producing
`(-1, paramFrameOffset)`) and a real `ldflda` arm that dispatches on its
operand. The JIT type-specialization pass SHALL stamp a marker (a flag bit in
the standalone `Operand4` field) on a real `ldflda` whose source operand is an
in-frame IL value type, so the runtime arm distinguishes the in-frame-VT
operand case from the heap-IL / CLR-object operand case. The marker SHALL be
stamped in the pre-lowering type-specialization pass (where the source register
index is still available for type lookup), and SHALL survive `LowerNeoOffsets`
intact (it lives in a standalone, non-union-aliased operand field).

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

For a CLR-OBJECT operand (the marker is absent and `mStack[objIdx]` is neither
an `ILTypeInstance` nor an `Array`), the arm SHALL stamp the CLR FIELD
IDENTITY into the produced Ref Slot's offset half (a `FieldInfo` handle, a
stable field hash, or a domain-cached field token) so the `stind`/`ldind`/
`stobj`/`ldobj` consumers can resolve it via the field's reflection accessor.
A CLR object field does NOT have a `Primitives[]` byte offset, so the IL
`field.PrimitiveOffset` MUST NOT be used for a CLR operand.

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

#### Scenario: Register reuse / escape probe (the Step-17-B1 silent-corruption class)

- WHEN an IL method produces a byref via `ldflda` on an in-frame VT, the dest
  register is then reused by an intervening operation, and FINALLY the byref
  is read
- THEN the read SHALL observe the field's current value (no stale/clobbered
  value from the intervening reuse); the `liveAliasMap` per-instruction
  snapshot correctly tracks the `ldflda` dest's live range across the reuse,
  and the marker branch produces the correct Ref Slot for the dest's own live
  range.

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

### Requirement: CopyNeoCallArguments boxed-source branch (F-5 closure)

The Neo VM SHALL ensure that the boxed-source branch of `CopyNeoCallArguments`
(reached when a boxed struct `this` flows from the `constrained.callvirt`
box-once path) does not silently mis-copy the boxed receiver. The branch SHALL
either (a) correctly copy the boxed struct's flat bytes into the dest slot (via
the same `ReadNeoValueType`-style read the Box arm's inverse uses), or (b) store
the boxed object's mStack index into the dest slot when the dest is an
`object`-typed `this` (matching the reference-type `this` passing convention),
or (c) throw a `NotImplementedException` tagged `Step 17` naming the unhandled
boxed-source sub-case. The branch SHALL NOT perform a
`CopyBlock(frameBase + offset, ...)` that treats an mStack field offset as a
struct address. The `CopyNeoCallThisBack` documentation SHALL state that it
covers mutating INSTANCE METHODS, not constructors (the newobj path does not
invoke it).

#### Scenario: boxed this from constrained.callvirt is copied correctly

- **WHEN** the `constrained.callvirt` box-once path produces a boxed `this` that
  flows through `CopyNeoCallArguments` to a CLR redirect delegate
- **THEN** the boxed-source branch SHALL pass the boxed object correctly (a
  boxed reference for an `object`-typed `this`, or flat bytes for a value-typed
  dest) and SHALL NOT silently mis-copy an mStack field offset as a struct
  address.

### Requirement: ldelema array-address producer (D-LDELEMA)

The Neo VM SHALL implement a `ldelema` arm that produces a Ref Slot
`(arrayMStackIndex, elementByteOffset)` where `elementByteOffset` is computed
from the element index and the array's element layout, resolved at runtime
from the array's CLR type (mirroring the Step 16 `Ldelem`/`Stelem`
array-kind resolution). The intended consumers of an `ldelema` Ref Slot
SHALL be `stind_*`/`ldind_*`/`stobj`/`ldobj`.

For an IL value-type array (the Step 16 `ILTypeInstance[]` representation), the
arm SHALL resolve the element `ILTypeInstance`, park it on mStack, and encode
`(elementMStackIdx, 0)` so the consumers hit the standard IL-instance
`Primitives` path on that element instance. For a CLR primitive array
(`int[]`, `float[]`, etc.) or a CLR struct array, the arm SHALL compute
`elementByteOffset = elementIdx * elementSize` from the array's CLR element
type and encode `(arrayMStackIdx, elementByteOffset)`; the consumer arms SHALL
read/write the CLR array element at that index.

#### Scenario: ldelema + stind round-trip on an int[]

- **WHEN** `ldelema arr, i` produces a Ref Slot consumed by `stind_i4`, then later
  by `ldind_i4`, on a CLR `int[]`
- **THEN** the stored value SHALL be observable through a subsequent element
  load (direct `Ldelem` or `ldind_i4` on the same Ref Slot).

#### Scenario: ldelema on an IL value-type array (unchanged)

- **WHEN** `ldelema arr, i` produces a Ref Slot on an IL value-type array
- **THEN** the arm SHALL park the element `ILTypeInstance` on mStack and encode
  `(elementMStackIdx, 0)` so stind/ldind (and the heap stfld/ldfld that the C#
  compiler emits for `arr[i].field = v`) hit the standard IL-instance
  `Primitives` path on that element instance (unchanged from Step 17).

### Requirement: constrained. runtime arm (D-CONSTRAINED)

The Neo VM SHALL dispatch `constrained.callvirt T.M` where the constrained type
`T` is a value type, regardless of whether the C# compiler emits the
`constrained.` prefix as `ldloca v; constrained T; callvirt M` (the struct
`this` arrives as an 8-byte frame-native Ref Slot from `ldloca`/`ldarga`). The
runtime `Constrained` arm SHALL own the dispatch: the JIT emits the order
`[Push..., Constrained T, Callvirt M]` (Constrained runs BEFORE the callvirt),
so the `Constrained` arm carries the type token, reads the trailing callvirt
for the method token / param map / return-slot info, dispatches, and skips the
trailing callvirt (`ip += 2`). No JIT fusion or operand stamping is required;
non-constrained callvirts are byte-identical.

For the **box-required** case (`M` is an override of `Object.ToString`/
`GetHashCode`/`Equals`, or any interface method, on the value type `T`), the
dispatch SHALL box the struct ONCE using the constrained type token and the
byref `this` address (reading the flat bytes from the frame-native address, the
same dereference area4's `CopyNeoCallArguments` performs) and SHALL dispatch
`M` on the boxed receiver so the override receives a boxed `this`. The box
SHALL happen exactly once (no per-virtual-dispatch re-boxing), matching Legacy's
`GetObjectAndResolveReference` box-once semantics.

For the **direct-call** case (`M` is a method declared on the value type itself
that is neither an override nor an interface implementation), the dispatch SHALL
reuse the byref-`this` direct-call path: the call-lowering marks the `this`
slot source as a byref (`PrimitiveByRefSrc`), `CopyNeoCallArguments` dereferences
the byref at the copy site so the callee `this` slot holds flat bytes, and the
callee reads the `this` via `ReadNeoValueType` -- exactly as a
`local.VTInstanceMethod()` direct call (no box).

The dispatch SHALL cover both an IL value type and a CLR value type as the
constrained type `T`. The constrained type's concrete override (not the static
call-site type's) SHALL be the dispatched method (resolved via
`constrainedType.GetVirtualMethod(targetMethod)`).

For the **IL value type + inherited CLRMethod** sub-case (an IL struct with NO
override calling `Object.ToString`/`GetHashCode`/`Equals`, the default for the
majority of IL structs), the dispatch SHALL box the IL struct into a real
`ILTypeInstance` (via `Instantiate(false)` + `CopyFrameToIL` of the flat
primitive bytes + `Boxed = true`, NOT `ReadNeoValueType` -- an IL value type's
`TypeForCLR` is `ILTypeInstance`, a reference type, so `ReadNeoValueType` is
unsound for it) and dispatch the inherited CLRMethod on the boxed
`ILTypeInstance`. The generic CLR-value-type box branch SHALL be guarded so an
IL value type cannot fall through to `ReadNeoValueType` (it MUST be handled by
the IL-VT box branch).

The IL-value-type-with-reference-fields constrained sub-case MAY remain a
tagged `NotImplementedException` this step (the byref source does not carry the
struct's ref-region mStack base). The previously-deferred sub-cases -- a
constrained call where the runtime `this` is already a boxed object (the
box-once no-op), and interface-on-VT-constrained beyond the common
`IEquatable<T>`/`IComparable<T>` shape -- MAY remain tagged
`NotImplementedException` if no smoke case exercises them; the box-once no-op
case SHALL be delivered IF reachable by the smoke (it falls out of the box-once
arm for free).

#### Scenario: constrained callvirt on a struct override dispatches with a boxed this

- **WHEN** an IL method emits `constrained.callvirt T.ToString` on a struct `T`
  that overrides `Object.ToString`, where the struct `this` is an in-frame local
  addressed by `ldloca`
- **THEN** the dispatch SHALL box the struct exactly once and the dispatched
  `T.ToString` override SHALL receive a boxed `this`; the call's result SHALL
  equal the struct's `ToString` output; the box SHALL NOT be repeated per
  virtual dispatch.

#### Scenario: constrained callvirt on a non-boxing struct method direct-calls

- **WHEN** an IL method emits `constrained.callvirt T.M` where `M` is a method
  declared on the struct `T` itself (not an override/interface)
- **THEN** the dispatch SHALL direct-call `M` on the byref `this` without
  boxing; the callee's `this` slot SHALL hold the struct's flat bytes (the
  byref dereferenced at the copy site); a mutating `M` SHALL propagate its
  mutation back to the caller's local via the post-call reverse copy.

#### Scenario: constrained callvirt on a CLR struct override

- **WHEN** an IL method emits `constrained.callvirt ClrStruct.ToString` on a CLR
  value type that overrides `Object.ToString`
- **THEN** the dispatch SHALL box the CLR struct once and dispatch the override
  on the boxed receiver, matching Legacy's box-once semantics.

#### Scenario: constrained callvirt on an IL struct calling an inherited Object/ValueType method

- **WHEN** an IL method emits `constrained.callvirt Object.ToString` (or
  `GetHashCode`/`Equals`) on an IL struct `T` that does NOT override the method,
  so the resolved method is the inherited CLRMethod
- **THEN** the dispatch SHALL box the IL struct into an `ILTypeInstance` (NOT
  via `ReadNeoValueType` on `ILTypeInstance`) and dispatch the inherited
  CLRMethod on the boxed receiver, returning a non-null/non-crashing result;
  it SHALL NOT read the struct's flat bytes as an `ILTypeInstance` shape.

#### Scenario: No regression on existing byref / addrAlias callers

- **WHEN** the existing NeoStep smoke suite is run after this change
- **THEN** every previously-green byref/ref/out/ldelema/addrAlias case remains
  green (the constrained dispatch is a new caller of the byref model; the
  addrAlias COEXIST gate and the call-lowering are unchanged for the non-
  constrained shapes).

### Requirement: Deferred byref sub-cases throw tagged NIE

The Neo VM SHALL throw a tagged `NotImplementedException` for byref sub-cases
not supported by the current step. The generic-parameter byref form (`ref T` /
`out T` with `T` a generic parameter) and the interface-on-VT-constrained
dispatch shape (beyond the common `IEquatable<T>` / `IComparable<T>` / box-once
shapes) are NOT deferred defects: they work via the existing type-agnostic byref
model (an 8-byte Ref Slot copied regardless of element type; the generic-param
token is resolved at the call site, not at the byref-marshal level) and the
existing `constrained.callvirt` dispatch arm (the {a,d,M2,b} cohorts already
cover the box-and-interface-dispatch + IL-VT-direct-call + CLR-VT-box-once +
IL-VT-inherited-CLRMethod paths). Regression guards for both SHALL be maintained
(the closure is locked).

The Neo VM is NOT required to support in this step:
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
(d) ~~generic-byref (`ref T`/`out T` with `T` a generic parameter)~~ **RESOLVED
(this change, NO-OP)** -- the byref model is type-agnostic; the generic-param
form works through the existing 8-byte Ref Slot call ABI + the typed-ref bridge
without a generic-param-specific discriminator. Regression guards
(`Swap<int>`/`Swap<IL-ref>`/`Swap<IL-VT>`) lock the closure.
(e) explicit-interface byref,
(f) `fixed` unmanaged-pinning blocks -- **DEFERRED (out of `neo-byref` scope).**
The `fixed` statement lowers to raw-pointer opcodes (`conv.u` / `Conv_U` to
convert the pinned array reference to a native `int*`, plus raw-pointer
indexing), which are unimplemented Step-6 opcodes outside this capability.
The array-element ADDRESS itself works via `ref arr[i]` (`ldelema` + `stind`/
`ldind`, the TC14 path); GC pinning is a CLR-host concern the interpreter does
not model. A `fixed` probe SHALL throw `NotImplementedException` tagged with
the unimplemented pointer opcode (`Conv_U` / `Conv_I`; `Ldtoken` when an array
initializer is used). Support is routed to a future pointer/`Conv_U` step.
(g) ~~interface-on-VT-constrained beyond the common shape~~ **RESOLVED (this
change, NO-OP)** -- the `constrained.callvirt` dispatch arm's direct-call path
(IL-VT + ILMethod override) and box-once path (CLR-VT + CLRMethod; IL-VT +
inherited CLRMethod) already cover the box-and-interface-dispatch shape. The
{a,d,M2} cohort (`neo-step17-completion`) + the (b) cohort (`neo-step17-stobj-
refloop`) delivered the coverage; regression guards (IL-VT + CLR-VT, both via a
generic constrained caller) lock the closure.
(h) ~~the IL-value-type-with-reference-fields constrained sub-case~~
**RESOLVED** (the Constrained arm seeds the callee slot-0 ref region /
`CopyFrameToIL` with the real ref base + `TotalReferenceCount`, shipped in
`neo-step17-stobj-refloop`).

When a RefSlot targeting one of the remaining deferred sub-cases (`fixed`, (e)
explicit-interface byref) is consumed, the relevant arm SHALL throw a
`NotImplementedException` tagged with `Step 17` (or the unimplemented-opcode tag
for `fixed`'s `Conv_U`/`Ldtoken`) rather than silently mis-handle it.

#### Scenario: generic-byref round-trips through the type-agnostic byref model

- **WHEN** an IL method declares `void Swap<T>(ref T a, ref T b)` and calls it
  with `T = int`, `T = an IL reference type`, and `T = an IL value type`
- **THEN** each call SHALL swap the two referents correctly (the 8-byte Ref Slot
  is copied via the standard byref call ABI; the body's `T tmp = a; a = b; b =
  tmp;` lowers to the correct Move/Move_Vt/ref-Move from the resolved `T`). No
  `NotImplementedException` is thrown and no generic-param-specific discriminator
  is consulted. (This locks the closure discovered by the dump-gate; the form
  was already green on HEAD.)

#### Scenario: interface-on-VT-constrained dispatches via the existing constrained arm

- **WHEN** an IL method calls `constrained.callvirt IFace.M` on a value type `T`
  that implements `IFace` (an IL struct with an ILMethod override, or a CLR
  struct with a CLRMethod), dispatched via a generic caller
  `R F<T>(T v) where T : IFace`
- **THEN** the dispatch SHALL resolve the constrained type's concrete override
  via `constrainedType.GetVirtualMethod(targetMethod)` and route to the direct-
  call path (IL-VT + ILMethod) or the box-once path (CLR-VT + CLRMethod),
  returning the correct result with no `NotImplementedException`. (This locks
  the {a,d,M2,b}-cohort closure; both shapes were already green on HEAD.)

#### Scenario: a fixed statement throws the unimplemented-pointer-opcode NIE

- **WHEN** an IL method uses a C# `fixed (T* p = arr) { ... }` statement (a
  pinned byref)
- **THEN** the JIT SHALL throw a `NotImplementedException` naming the
  unimplemented pointer opcode (`Conv_U` / `Conv_I`; or `Ldtoken` when an array
  initializer is involved), NOT a byref-machinery NIE. The array-element address
  via `ref arr[i]` (the `ldelema` + `stind`/`ldind` path) is unaffected and
  remains usable; the gap is specifically the `fixed` statement's pointer-
  conversion opcodes, which are outside the `neo-byref` capability.

#### Scenario: stobj on a nested-field byref of a VT with reference fields throws tagged NIE

- **WHEN** `stobj`/`ldobj` copies a value type that has one or more reference
  fields through a nested-field byref (produced by `ldflda` of a struct field,
  not a direct local)
- **THEN** the arm SHALL throw a Step-17-tagged `NotImplementedException` (the
  nested-field ref-region recovery is deferred; only the direct-local and
  IL-instance shapes are correctly copied this step).

### Requirement: Legacy reference preserved

All Step 17 machinery SHALL live behind `#if ENABLE_NEO_MODE`. The Legacy
interpreter (`ExecuteR`, `ILIntepreter.Register.cs`) is the SEMANTIC reference
for byref/ldloca/ldflda/stind/ldind dispatch (via the `ObjectTypes`
discriminator) and SHALL NOT be modified by this change.

### Requirement: Byref `this` for value-type constructor invocation

The byref call-ABI SHALL additionally serve as the `this` parameter of an IL
value-type constructor invoked via `newobj`. The `newobj` arm SHALL seed the
ctor's callee param region `this` slot (param slot 0) with a frame-native
address of the caller's dest region, so the ctor treats `this` as a managed
pointer to the caller's frame slot. Field assignments in the ctor
(`this.field = ...`) SHALL resolve through the existing `stind_*`/`stfld` /
`_Inline` machinery into the caller's dest region, exactly as an explicit
byref/out parameter would.

The seeding mechanism SHALL be consistent with the callee's `ParamInfos[0]`
layout, which for a value-type ctor is sized as the in-frame value
(`Size = TotalPrimitiveSize`, `RefCount = TotalReferenceCount`). The runtime
SHALL seed the callee's slot-0 bytes (and, for a value type with reference
fields, the callee's slot-0 ref slots) so that the ctor's `_Inline` field
accesses -- resolved through the `addrAlias`/`liveAliasMap` root at param slot
0 -- target the caller's dest region. (Equivalently, the runtime MAY byte-copy
the dest region into the callee's `this` slot before the call and back after
the call; the contract is that the ctor's `this`-relative writes are observable
in the caller's dest slot.)

This is a new caller of the byref call-ABI (a byref `this`), not a change to
the ABI itself. The detailed newobj-side construction contract is owned by the
`neo-newobj` capability; the end-to-end in-frame-address consistency (the
`addrAlias` root at param slot 0 and the typed newobj dest) is owned by the
`neo-value-types` capability.

#### Scenario: IL value-type newobj passes the dest as the ctor this

- **WHEN** an IL method emits `new S(args)` for an IL value type `S` and the
  ctor writes `this.field = value`
- **THEN** the ctor's `this` (param slot 0) resolves to the caller's dest frame
  region, the field write lands in that region, and after the ctor returns the
  caller's newobj result dest holds the assigned field value -- with no heap
  `ILTypeInstance` for the value type and no separate post-ctor copy that could
  diverge from the ctor's writes.

#### Scenario: Value-type ctor with reference fields

- **WHEN** an IL value type `S` has one or more reference fields and is
  constructed via `new S(args)` where the ctor assigns those reference fields
  via `this.refField = ...`
- **THEN** the reference-field assignments propagate to the caller's dest ref
  slots (the callee's slot-0 ref slots are seeded to address the caller's dest
  ref region), and reading those fields from the caller returns the assigned
  references (incl. the null case).

#### Scenario: No regression on existing byref callers

- **WHEN** the existing NeoStep smoke suite is run after this change
- **THEN** every previously-green byref/ref/out/ldelema case remains green
  (the byref call-ABI itself is unchanged; only a new `this` caller is added).

### Requirement: Cross-frame reference-byref write-back lifetime

A frame-native byref (`objectIndex == -1`) with a reference-type referent SHALL NOT leave a dangling callee-frame mStack index in an outer frame's cell when the byref crosses a frame boundary (the callee runs in a nested `ExecuteNeo` whose `frameBase`/`frameRefBase` differ from the byref's owning frame).
The written-back reference object SHALL be PROMOTED into an mStack slot owned by
the frame that observes the byref (the caller frame, i.e. a slot at index
`< caller frameRefBase`), and the caller's frame cell SHALL end up holding an
index into that caller-owned slot. The promotion SHALL run BEFORE the nested
frame's mStack pop (`ExecuteNeo` `Ret` arm truncation) deletes the callee's
frame ref region.

This requirement is the byref-channel analog of the single-reference RETURN
promotion (`ExecuteNeo` `Ret` arm copies `mStack[retSrcIdx]` into the
caller-supplied `retRefBase` slot before the pop). A reference-byref
write-back SHALL enjoy the same lifetime guarantee.

Rationale: the Neo per-frame mStack reservation + `Ret`-arm truncation
(`mStack.RemoveRange(frameRefBase, ...)`) is a load-bearing invariant for the
Neo frame model. Without promotion, a reference-byref write-back stores a
callee-frame index into the caller cell; the callee pop then leaves that index
dangling, and a subsequent dereference (e.g. a CLR binding reading
`mStack[<dead index>]`) throws `Index out of range`. Primitive-byref
write-backs are unaffected (the value is flat bytes, no mStack index).

#### Scenario: reference-byref write-back of a callee-created object

- WHEN an IL method obtains a frame-native byref to a reference-typed local
  (e.g. `string s = "abc"; ... ref s`), passes that byref across a frame
  boundary to a callee that REASSIGNS the referent (`s = s + "!"`), the callee
  returns, and the caller reads the local
- THEN the caller's local SHALL hold the callee-created object (e.g.
  `s == "abc!"`), the local's mStack index SHALL resolve to a slot below the
  callee's `frameRefBase` (i.e. the index survived the callee pop), and the
  read SHALL NOT throw `Index out of range`

#### Scenario: reference-byref READ then WRITE across a frame

- WHEN a callee first READS a reference-typed byref (e.g. returns
  `s.Length`) and a SEPARATE call WRITEs a new object through the byref
- THEN the read path SHALL observe the object present at call entry, and the
  write path SHALL promote the new object into the caller-owned slot; the two
  paths SHALL be consistent (the caller-owned slot is the single source of
  truth for the byref referent across the call)

#### Scenario: primitive-byref write-back is unaffected

- WHEN a callee writes a primitive value (`ref int`, `out int`, `ref long`,
  `ref float`, `ref double`) through a cross-frame frame-native byref
- THEN the write-back SHALL store flat bytes directly into the caller's frame
  cell (no mStack index, no promotion), matching the F-7 primitive-byref
  behavior byte-for-byte; the primitive value SHALL be observable after the
  call with NO dangling index

## MODIFIED Requirements

### Requirement: stind / ldind / stobj / ldobj dispatch

The `stind`/`ldind`/`stobj`/`ldobj` dispatch over a Ref Slot SHALL handle a
reference-typed frame-native byref that crosses a frame boundary by addressing
a CALLER-OWNED mStack slot for the object, so that a write stores the object
into a slot the callee pop does not delete and a read observes a stable index.
The dispatch SHALL distinguish this cross-frame reference-byref shape from the
existing mStack-object field-address shape (a byref whose `objectIndex >= 0`
and whose `offset` is a field hash or a Primitives byte offset) via an
encoding discriminator disjoint from real field hashes and Primitives offsets
(e.g. a high-bit flag on the `offset` half, mirroring the F-10
`NeoF10ByrefOffsetFlag` discriminator idiom). Primitive and value-type
frame-native byrefs SHALL keep their existing dispatch UNCHANGED.

#### Scenario: stind_ref write through a cross-frame reference byref

- WHEN a `stind.ref` executes on a reference-typed byref that was converted
  to the cross-frame caller-owned-slot form before a nested `ExecuteNeo`
- THEN the stored value's object SHALL be copied into the caller-owned mStack
  slot and the caller's frame cell SHALL hold the caller-owned slot index;
  the stored index SHALL be valid after the nested frame's `Ret` pop

#### Scenario: ldind_ref read through a cross-frame reference byref

- WHEN a `ldind.ref` executes on the same cross-frame reference-byref form
- THEN the read SHALL observe the object in the caller-owned mStack slot,
  materializing it into the callee's dest ref slot for the callee's use,
  WITHOUT introducing a dangling index into the caller cell
