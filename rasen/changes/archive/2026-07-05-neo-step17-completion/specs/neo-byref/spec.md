## MODIFIED Requirements

### Requirement: constrained. runtime arm (D-CONSTRAINED)

The Neo VM SHALL dispatch `constrained.callvirt T.M` where the constrained type
`T` is a value type, regardless of whether the C# compiler emits the
`constrained.` prefix as `ldloca v; constrained T; callvirt M` (the struct
`this` arrives as an 8-byte frame-native Ref Slot from `ldloca`/`ldarga`). The
dispatch SHALL be atomic: the JIT SHALL fuse the `Constrained` prefix onto the
callvirt (carrying the constrained type token) so the callvirt resolver sees the
constrained type, and the trailing `Constrained` opcode SHALL be a runtime
no-op.

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
call-site type's) SHALL be the dispatched method.

The previously-deferred sub-cases -- a constrained call where the runtime `this`
is already a boxed object (the box-once no-op), and interface-on-VT-constrained
beyond the common `IEquatable<T>`/`IComparable<T>` shape -- MAY remain tagged
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

#### Scenario: No regression on existing byref / addrAlias callers

- **WHEN** the existing NeoStep smoke suite is run after this change
- **THEN** every previously-green byref/ref/out/ldelema/addrAlias case remains
  green (the constrained dispatch is a new caller of the byref model; the
  addrAlias COEXIST gate and the call-lowering are unchanged for the non-
  constrained shapes).

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

## ADDED Requirements

### Requirement: CopyNeoCallArguments boxed-source branch (F-5 closure)

The Neo VM SHALL ensure that the boxed-source branch of `CopyNeoCallArguments`
(reached when a boxed struct `this` flows from the `constrained.callvirt`
box-once path) does not silently mis-copy the boxed receiver. The branch SHALL
either (a) correctly copy the boxed struct's flat
bytes into the dest slot (via the same `ReadNeoValueType`-style read the Box
arm's inverse uses), or (b) store the boxed object's mStack index into the dest
slot when the dest is an `object`-typed `this` (matching the reference-type
`this` passing convention), or (c) throw a `NotImplementedException` tagged
`Step 17` naming the unhandled boxed-source sub-case. The branch SHALL NOT
perform a `CopyBlock(frameBase + offset, ...)` that treats an mStack field
offset as a struct address. The `CopyNeoCallThisBack` documentation SHALL state
that it covers mutating INSTANCE METHODS, not constructors (the newobj path
does not invoke it).

#### Scenario: boxed this from constrained.callvirt is copied correctly

- **WHEN** the `constrained.callvirt` box-once path produces a boxed `this` that
  flows through `CopyNeoCallArguments` to a CLR redirect delegate
- **THEN** the boxed-source branch SHALL pass the boxed object correctly (a
  boxed reference for an `object`-typed `this`, or flat bytes for a value-typed
  dest) and SHALL NOT silently mis-copy an mStack field offset as a struct
  address.

## MODIFIED Requirements

### Requirement: Deferred byref sub-cases throw tagged NIE

The Neo VM SHALL throw a tagged `NotImplementedException` for byref sub-cases
not supported by this step. The Neo VM is NOT required to support in this step: (a) the `stobj`/`ldobj`
ref-slot portion of a value-type copy through `stobj`/`ldobj` (a value type WITH
reference fields is not yet correctly copied through `stobj`/`ldobj`; primitive-
field value types are fully supported), (b) CLR-object stind/ldind via field
hash (`(objMStackIdx, fieldHash)`), (c) CLR-method `ref`/`out` parameters (the
IL-to-CLR byref crossing), (d) generic-byref (`ref T`/`out T` with `T` a
generic parameter), (e) explicit-interface byref, (f) `fixed` unmanaged-pinning
blocks, (g) interface-on-VT-constrained beyond the common shape. When a Ref Slot
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

- **WHEN** `ldflda` of a CLR object field produces a Ref Slot and `stind_i4`
  consumes it
- **THEN** the arm SHALL throw a Step-17-tagged `NotImplementedException`
  (CLR-field-hash stind/ldind is deferred to a follow-up).
