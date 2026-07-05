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
CLR-object).

The `addrAlias` folding behavior for `ldflda` SHALL remain unchanged: a dest
whose every consumer is foldable stays folded (runtime arm dead); a dest whose
address escapes the folding window stays real (runtime arm fires, now correctly
for the in-frame-VT-flat-bytes operand via the marker branch).

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
`stobj`/`ldobj` over a value type, the corresponding `ManagedObjects` entry).
`stobj`/`ldobj` SHALL copy `TotalPrimitiveSize` bytes sized by the type token
in `Operand`. **The `TotalReferenceCount` ref-slot portion of a value-type copy
through `stobj`/`ldobj` is PARTIAL this step (primitives only): the ref-slot
loop is deferred to a follow-up, so a value type with reference fields is not
yet correctly copied through `stobj`/`ldobj`. Primitive-field value types (the
green target) are fully supported.**

#### Scenario: stind_i4 through a frame-native ref mutates the caller's local
- WHEN `void Inc(ref int x){...}` writes via `stind_i4` on the byref param
- THEN the mutation SHALL be visible in the caller's frame local after return.

#### Scenario: ldind_i4 through a heap-IL field ref reads the field
- WHEN `ldind_i4` reads a Ref Slot produced by `ldflda` of a heap IL field
- THEN the arm SHALL read the field's current value from the ILTypeInstance's
  pinned `Primitives` at the field offset.

### Requirement: ref/out IL-parameter call ABI

For an IL-method call with a `ref`/`out` parameter, the caller SHALL copy the
8-byte Ref Slot value into the callee's param region (one primitive-copy entry
of size 8, no ref entry), and the callee SHALL treat the parameter as an 8-byte
byref slot. Mutations the callee makes through the slot (via `stind`/`stobj`)
SHALL propagate to the caller's frame local or heap object referenced by the
slot. The ABI SHALL be identical for `ref` and `out` (an `out` parameter's
referent is uninitialized-on-entry by contract; the runtime enforces no extra
check). Passing a byref onward (the callee re-passes the slot to a nested call)
SHALL preserve the `(objectIndex, offset)` pair through the 8-byte copy.

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
not supported by this step. The Neo VM is NOT required to support in this step:
(a) the `stobj`/`ldobj` ref-slot portion of a value-type copy through
`stobj`/`ldobj` (a value type WITH reference fields is not yet correctly copied
through `stobj`/`ldobj`; primitive-field value types are fully supported),
(b) CLR-object stind/ldind via field hash (`(objMStackIdx, fieldHash)`),
(c) CLR-method `ref`/`out` parameters (the IL-to-CLR byref crossing),
(d) generic-byref (`ref T`/`out T` with `T` a generic parameter),
(e) explicit-interface byref, (f) `fixed` unmanaged-pinning blocks,
(g) interface-on-VT-constrained beyond the common shape,
(h) the IL-value-type-with-reference-fields constrained sub-case (the byref
source does not carry the struct's ref-region mStack base). When a Ref Slot
targeting one of these deferred sub-cases is consumed, the relevant arm SHALL
throw a `NotImplementedException` tagged with `Step 17` (or `Step 13b` for the
CLR-binding-owned sub-cases) rather than silently mis-handle it.

#### Scenario: stobj on a VT with reference fields throws tagged NIE

- **WHEN** `stobj` copies a value type that has one or more reference fields
  through a Ref Slot
- **THEN** the arm SHALL throw a Step-17-tagged `NotImplementedException`
  (the ref-slot loop is deferred to a follow-up; only primitive-field value
  types are correctly copied through `stobj`/`ldobj` this step).

#### Scenario: stind on a CLR-object field hash ref throws tagged NIE
- WHEN `ldflda` of a CLR object field produces a Ref Slot and `stind_i4`
  consumes it
- THEN the arm SHALL throw a Step-17-tagged `NotImplementedException`
  (CLR-field-hash stind/ldind is deferred to a follow-up).

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