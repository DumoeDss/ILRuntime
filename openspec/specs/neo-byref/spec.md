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
operand: an in-frame value-type operand produces a frame-native Ref Slot
`(-1, vtBase + field.PrimitiveOffset)`; a heap-IL operand produces
`(mStackIndex, field.PrimitiveOffset)`. The optimizer SHALL stamp a marker
(e.g. `Operand4`) on a real `ldflda` so the arm distinguishes the in-frame-VT
case from the heap-IL case.

#### Scenario: ldarga of a struct method's this
- WHEN a value-type instance method executes `ldarga.s 0` for `this`
- THEN the arm SHALL produce `(-1, thisParamFrameOffset)` consumable by
  `stind`/`ldind` and by a `constrained.` callvirt.

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

### Requirement: ldelema array-address producer (D-LDELEMA)

The Neo VM SHALL implement a `ldelema` arm that produces a Ref Slot
`(arrayMStackIndex, elementByteOffset)` where `elementByteOffset` is computed
from the element index and the array's element layout, resolved at runtime
from the array's CLR type (mirroring the Step 16 `Ldelem`/`Stelem`
array-kind resolution). The only intended consumers of an `ldelema` Ref Slot
SHALL be `stind_*`/`ldind_*`/`stobj`/`ldobj`.

#### Scenario: ldelema + stind round-trip on an int[]
- WHEN `ldelema arr, i` produces a Ref Slot consumed by `stind_i4`, then later
  by `ldind_i4`
- THEN the stored value SHALL be observable through a subsequent element load.

### Requirement: constrained. runtime arm (D-CONSTRAINED) -- PARTIAL / DEFERRED

The Neo VM SHALL provide a runtime `Constrained` arm (executed after the
preceding `callvirt`, which the JIT moves the `Constrained` opcode past and
flags with `Operand4`) for `constrained.callvirt T.M` where `T` is a value
type. **This step delivers the arm as a loud, Step-tagged
`NotImplementedException` only** (the arm exists and is reached, so the case is
surfaced rather than silently mis-handled). Full constrained.-on-value-type
dispatch -- box-once using the constrained type token and the value-type
address (a Ref Slot produced by `ldarga`/`ldloca`) so a boxing-required
override (e.g. an overridden `Object.ToString`) receives a boxed `this`, or a
no-op for a non-boxing override -- is **DEFERRED**: it requires the preceding
`callvirt` to accept a byref `this` (the struct's managed address) and dispatch
to the constrained type's concrete override, which the current callvirt does
not support (it reads `this` as an mStack object index). That callvirt-byref-
`this` work is deferred to Step 13b / a follow-up. No green test is added for
the dispatched case this step; the arm's contract for this step is solely to
throw the tagged NIE.

#### Scenario: constrained callvirt on a struct throws a tagged NIE (this step)
- WHEN a `constrained.callvirt T.M` executes with a struct `this`
- THEN the `Constrained` arm SHALL throw a `NotImplementedException` tagged
  `Step 17` / `Step 13b` (full VT dispatch -- callvirt byref-`this` -- is
  deferred); it SHALL NOT silently no-op or mis-handle the call.

### Requirement: Deferred byref sub-cases throw tagged NIE

The Neo VM is NOT required to support in this step: (a) CLR-object
stind/ldind via field hash (`(objMStackIdx, fieldHash)`), (b) CLR-method
`ref`/`out` parameters (the IL-to-CLR byref crossing), (c) generic-byref
(`ref T`/`out T` with `T` a generic parameter), (d) explicit-interface byref,
(e) `fixed` unmanaged-pinning blocks. When a Ref Slot targeting one of these
deferred sub-cases is consumed, the relevant arm SHALL throw a
`NotImplementedException` tagged with `Step 17` (or `Step 13b` for the
CLR-binding-owned sub-cases) rather than silently mis-handle it.

#### Scenario: stind on a CLR-object field hash ref throws tagged NIE
- WHEN `ldflda` of a CLR object field produces a Ref Slot and `stind_i4`
  consumes it
- THEN the arm SHALL throw a Step-17-tagged `NotImplementedException`
  (CLR-field-hash stind/ldind is deferred to Step 13b).

### Requirement: Legacy reference preserved

All Step 17 machinery SHALL live behind `#if ENABLE_NEO_MODE`. The Legacy
interpreter (`ExecuteR`, `ILIntepreter.Register.cs`) is the SEMANTIC reference
for byref/ldloca/ldflda/stind/ldind dispatch (via the `ObjectTypes`
discriminator) and SHALL NOT be modified by this change.
