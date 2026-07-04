# neo-boxing

Boxing and unboxing of value types in the Neo register VM (`ENABLE_NEO_MODE`),
covering both IL value types and CLR value types (with and without a registered
`ValueTypeBinder`), plus CLR value-type `Initobj` and `constrained.` callvirt
specialization on a value-type `this`.

This capability is distinct from `neo-value-types` (in-frame storage, inline
field access, and whole-struct copy): boxing bridges the flat in-frame value-
type representation to a heap representation (`ILTypeInstance` for IL types, a
boxed CLR object for CLR types), and `constrained.` is a dispatch concern that
layers on top of `neo-dispatch`.

The CLR-call-ABI aspects of value-type handling (CLR struct by-value
parameters, return values, and instance `this`) are owned by `neo-value-types`
and are deferred to a Step 13b follow-up; this capability does not change the
call ABI.

## Requirements

### Requirement: IL value-type Box and Unbox

The `Box` and `Unbox`/`Unbox_Any` `ExecuteNeo` arms SHALL box an in-frame IL
value type into a new `ILTypeInstance` and unbox an `ILTypeInstance` back into
an in-frame IL value type, reusing the frame<->heap copy helpers
(`CopyFrameToIL` / `CopyILToFrame`) so that the operation is a pure primitive
byte copy plus a per-reference-slot copy, with no format conversion and no
descriptor allocation. Boxing an IL enum or IL primitive SHALL copy exactly the
underlying primitive size. Boxing an IL reference type SHALL be a no-op (the
same instance flows through). Unboxing a null source SHALL throw
`NullReferenceException`; unboxing a boxed value of an incompatible type SHALL
throw `InvalidCastException`.

#### Scenario: IL value type with reference fields round-trips through box and unbox
- **WHEN** an IL method declares a value type `S { int a; string b; }`, sets
  `s.a = 7; s.b = "hi";`, executes `object o = s;` (box) then `S t = (S)o;`
  (unbox), and reads `t.a` and `t.b`
- **THEN** the method compiles under `ENABLE_NEO_MODE` without throwing,
  `t.a == 7`, `t.b == "hi"`, and `object.ReferenceEquals(t.b, s.b)` is true
  (the boxed instance's reference fields share identity with the source).

#### Scenario: IL enum and IL primitive box/unbox preserve the value
- **WHEN** an IL enum `E { A = 3 }` or an IL primitive is boxed to `object`
  and unboxed back
- **THEN** the round-tripped value equals the original (the underlying
  primitive size is copied, no more).

### Requirement: CLR value-type Box with and without ValueTypeBinder

The `Box` `ExecuteNeo` arm SHALL box an in-frame CLR value type into a boxed
CLR object. When the CLR type has a registered `ValueTypeBinder`, the arm
SHALL materialize the boxed CLR struct from the frame flat bytes via the
binder (a binder Neo helper that reads the frame byte region and mStack ref
run into a boxed `T`). When the CLR type has no binder, the arm SHALL box the
value by reading the frame flat bytes into a managed `T` local and boxing it
by value, and this no-binder path SHALL be restricted to CLR value types with
no reference fields (pure-primitive CLR structs); a no-binder CLR value type
with reference fields SHALL throw a `NotImplementedException` tagged for the
Step 13b follow-up, directing the user to register a `ValueTypeBinder`. The
boxed object SHALL be installed in the frame's mStack reference region at the
destination ref slot, and the destination byte slot SHALL hold the mStack index
(or -1 if null).

> **Deferred to Step 13b -- "no-binder struct-with-refs throws NIE" applies
> only to the flat-bytes representation.** The NIE scenario above was premised
> on CLR value-type locals being stored as flat bytes. The actual Neo
> representation (set by `JITCompiler.AllocateLocalStackSpaces`, CLR-VT branch)
> stores a CLR value-type LOCAL as a **boxed object reference** (a 4-byte
> mStack index slot, `RefCount = 1`, `localIsRef = true`), with no frame ref
> slots for the binder to map. Box/Unbox/Initobj for a CLR value-type local
> therefore operate on the boxed-ref representation uniformly
> (`PerformMemberwiseClone` / `CreateDefaultInstance`), and a no-binder struct
> WITH reference fields is representable and handled WITHOUT a NIE for the
> local path. The "struct-with-refs-and-no-binder throws NIE" requirement
> belongs to the **flat-bytes** representation (CLR struct array elements,
> by-value params, IL-typed fields), which is implemented in the Step 13b
> follow-up (areas 4-5). The 13b change MUST carry the flat-bytes NIE scenario
> when it introduces that representation. CLR enums follow the same boxed-ref
> path as CLR structs (a boxed enum is a boxed value type).

#### Scenario: CLR pure-primitive struct boxes and unboxes without a binder
- **WHEN** an IL method declares a CLR struct `Vector3`-like local `v`
  (`struct { float x; float y; float z; }`), sets its fields, executes
  `object o = v;`, mutates `v`, then unboxes `Vector3 w = (Vector3)o;`
- **THEN** the method compiles and runs under `ENABLE_NEO_MODE`, the unboxed
  `w` carries the values present at box time (not the later mutation), and no
  `ValueTypeBinder` registration is required.

#### Scenario: CLR struct with a ValueTypeBinder boxes and unboxes
- **WHEN** a CLR struct with reference fields has a registered
  `ValueTypeBinder`, and an IL method boxes and unboxes it
- **THEN** the binder is consulted to map the frame bytes and ref slots to and
  from the boxed CLR struct, and the round-trip preserves both primitive and
  reference field values.

### Requirement: CLR value-type Unbox and Unbox_Any

The `Unbox`/`Unbox_Any` `ExecuteNeo` arms SHALL copy a boxed CLR struct back
into the frame's flat bytes. With a binder, the binder Neo helper SHALL write
the primitives and ref slots from the boxed object into the frame. Without a
binder, the arm SHALL unbox-copy by reading the boxed struct by value
(`(T)obj`) and writing its bytes into the frame byte region; this path is
restricted to pure-primitive CLR structs and SHALL throw for structs with
reference fields. Unboxing a null source SHALL throw `NullReferenceException`.

#### Scenario: CLR struct unbox into an in-frame local
- **WHEN** an IL method unboxes a boxed CLR struct into an in-frame local and
  reads a field
- **THEN** the field value equals the value present in the boxed struct.

### Requirement: CLR value-type Initobj

The `Initobj` `ExecuteNeo` arm SHALL zero-initialize the frame byte region for
a CLR value-type local (and null any reference slots the local occupies). With
a binder, a binder Neo helper MAY be used; without a binder, the arm SHALL use
an `InitBlock` of the CLR type's primitive size and null the local's reference
slots.

#### Scenario: CLR struct local initobj zeroes the value
- **WHEN** an IL method declares a CLR struct local and applies `Initobj`
  (default-initialization, e.g. via `default(T)` or `initobj`)
- **THEN** the local's primitive bytes are zero and its reference fields (if
  any, with a binder) are null.

### Requirement: constrained. callvirt specialization on a value type

A `constrained.` + `callvirt` sequence whose constrained token resolves to a
value type `T` SHALL exhibit the following OBSERVABLE behavior: when `T`
declares or overrides the target method, the call SHALL execute on the
value-type `this` address with no boxing allocation; when the target method is
inherited from `System.Object` (or otherwise not declared on `T`), the
value-type `this` SHALL be boxed once and the call SHALL dispatch on the boxed
object. The reference-type constrained case and the unconstrained callvirt
case SHALL be byte-for-byte unchanged from the prior behavior. The interface-
dispatch path (`Callvirt_Interface`) SHALL be excluded from this value-type
specialization (a constrained callvirt resolving to an interface method stays a
box + interface dispatch).

This requirement is MODIFIED to permit realization either (a) by compile-time
JIT lowering (the original phrasing) OR (b) by a runtime `Constrained` arm
executed after the callvirt (the realization chosen by Step 17, because the Neo
JIT currently re-appends the `Constrained` opcode after the callvirt and cannot
perform the compile-time lowering). Both realizations MUST satisfy the same
observable scenarios.

#### Scenario: constrained callvirt to an inherited object method on a struct
- **WHEN** a generic method calls `constrained. T` then `callvirt ToString()`
  where `T : struct` and `T` does not override `ToString`
- **THEN** the value-type `this` is boxed once and `object.ToString()` is
  invoked on the box; the result is the struct's default string representation.

#### Scenario: constrained callvirt to a struct-declared method
- **WHEN** a generic method calls `constrained. T` then `callvirt` a method
  that `T` overrides
- **THEN** the call is lowered to a direct call on the in-frame `this` address
  with no boxing allocation.

#### Scenario: No regression on existing callvirt and dispatch behavior
- **WHEN** the existing NeoStep smoke suite (NeoStep6 through NeoStep12b) is
  run after this change
- **THEN** every previously-green case remains green; the reference-type
  constrained path, the unconstrained virtual/interface dispatch paths, and
  all CLR binding tests are unchanged (this change does not touch the CLR call
  ABI, which is deferred to Step 13b).

### Requirement: Constrained runtime arm resolution basis

The runtime `Constrained` arm SHALL be enabled by the `neo-byref` value-type
address model: the value-type `this` address required for both the no-box
direct-call case and the box-once case is a Ref Slot produced by
`ldarga`/`ldloca` (see the `neo-byref` capability). Where the constrained
value-type specialization needs more than the byref model provides (e.g.
interface-on-VT constrained callvirt with cross-model signature matching), the
arm SHALL throw a Step-17-tagged `NotImplementedException` rather than
silently mis-dispatch.
