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
parameters, return values, and instance `this`) were deferred to a Step 13b
follow-up in the base capability. Step 13b (`implement-neo-step13b`) closes the
**by-value parameter and return-value** portion of that deferral; the byref CLR
crossing, the value-type instance `this` direct-call (Area 4), and CLR-object
`stind`/`ldind` via field hash remain deferred.

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

### Requirement: CLR value-type by-value parameter reading (call ABI)

A CLR value type passed BY VALUE as a method parameter SHALL be laid out in the
callee param region as flat bytes sized by `GetPrimitiveSize(type)` via the
SAME `AllocateNeoCallParamSlot` callee-layout helper used for all other
parameter types. The call-lowering SHALL NOT use the caller source register's
slot shape for a CLR struct parameter (the temporary caller-temp-slot fallback
is REMOVED). The single source of truth for a CLR value type's flat-byte size
is `GetNeoValueTypeManagedSize`.

Both CLR-param readers -- the reflection fallback `CLRMethod.Invoke(byte*)` and
the autogen redirect delegate emitted by `AppendArgumentCodeNeo` -- SHALL read
a CLR struct parameter by its actual slot width via the `ReadNeo*`/
`ReadNeoValueType` helpers, advancing the cursor by exactly the parameter's
flat-byte size, so that the reader layout and the callee layout are byte-
consistent.

- A CLR struct WITH a registered `ValueTypeBinder` SHALL be read via the binder
  (the binder maps ref fields).
- A pure-primitive CLR struct (no ref fields) WITHOUT a binder SHALL be read
  via `ReadNeoValueType` (flat bytes -> boxed `T`).
- A CLR struct WITH ref fields and NO binder SHALL throw a clearly-tagged
  `NotImplementedException` directing the user to register a binder.

#### Scenario: CLR struct by-value parameter round-trip
- WHEN a method declares a CLR value type parameter (e.g.
  `int Sum(TestVector3NoBinding v)`) and is called with a CLR struct argument
- THEN the callee receives the struct's primitive bytes unchanged (the sum of
  its fields equals the value computed from the argument), and no
  `ArgumentOutOfRangeException` is thrown (the K2 regression).

### Requirement: CLR value-type by-value return value (call ABI)

A CLR value type RETURN value SHALL be written into the caller's dest frame
slot (sized by `AllocateLocalStackSpaces`) as flat bytes via
`WriteNeoValueType` (or the binder for structs with refs), by both the
reflection return path (`InvokeNeoClrMethod`) and the autogen
`GetReturnValueCodeNeo` return path. The K2-family "reads a primitive field
value as an mStack index" mis-copy SHALL NOT occur for a struct return.

#### Scenario: CLR struct return value round-trip
- WHEN a method returns a CLR value type (e.g. `TestVector3NoBinding Make(...)`)
- THEN the caller's dest local holds the struct's primitive bytes unchanged,
  and a subsequent field read on the returned value is correct.

### Requirement: CLR value-type instance method this reading (Area 4b)

A CLR value-type instance method invoked from IL SHALL read its value-type
`this` as the struct's flat bytes via the Step 13b `ReadNeoValueType` helper,
NOT as a 4-byte mStack index. This applies whether the call is lowered to a
direct `call` on an in-frame struct local (`ldloca; call`), to a `newobj`-
equivalent constructor call (`initobj; ldloca; call . ctor`), or to a
`callvirt` on a CLR struct override. Both CLR-`this` readers -- the reflection fallback
`CLRMethod.Invoke(byte*)` `HasThis` arm and the autogen redirect delegate
emitted by `GenerateMethodWraperCode_Neo` -- SHALL agree byte-for-byte on the
`this`-slot representation, so that the value-type `this` is read consistently
across the reflection and autogen paths.

The `this`-slot representation is discriminated by the RefSlot's
`objectIndex` half (the same encoding used by the Step 17 byref / stind / ldind
arms): a frame-native byref `this` (`objectIndex == -1`, produced by `ldloca`
on an in-frame struct local) is read by dereferencing the 8-byte Ref Slot to the
frame byte offset and reading `ReadNeoValueType` there; a boxed-struct `this`
(`objectIndex >= 0`) is read by unboxing the mStack object.

- A CLR struct instance method WITH a registered `ValueTypeBinder` that has
  reference fields SHALL throw a clearly-tagged `NotImplementedException`
  directing the user to register a flat-bytes binder path (the Neo-cursor binder
  API does not exist yet -- matches the Step 13b param-read deferral).
- A CLR struct instance method whose `this` is a pure-primitive struct (no
  reference fields) WITHOUT a binder SHALL read via `ReadNeoValueType` (flat
  bytes -> the runtime struct).
- A CLR struct instance method whose `this` is a struct WITH reference fields
  and NO binder SHALL throw a clearly-tagged `NotImplementedException`
  directing the user to register a binder (matches the Step 13b param-read
  guard).

This closes the F-3 / NEO-BYREF-THIS pre-existing defect class: a direct
`new ClrStruct(args)` in interpreted IL, a `local.InstanceMethod()` call on a
CLR struct local, and a `callvirt` on a CLR struct override SHALL no longer
throw `ArgumentOutOfRangeException` from `CLRMethod.Invoke` reading the byref
`this` as a 4-byte mStack index.

#### Scenario: CLR struct instance method called on an in-frame local
- **WHEN** an IL method declares a CLR struct local `v` (e.g.
  `TestVector3NoBinding`), constructs it via `new TestVector3NoBinding(x, y, z)`
  (which lowers to `initobj; ldloca; call .ctor`), and calls an instance method
  on `v` (e.g. `v.LengthSquared()` returning a primitive)
- **THEN** the method compiles and runs under `ENABLE_NEO_MODE`, the instance
  method reads the struct's fields correctly from the in-frame byref `this`, and
  no `ArgumentOutOfRangeException` is thrown (the F-3 regression).

#### Scenario: `new ClrStruct(args)` constructs the struct end-to-end
- **WHEN** an IL method executes `TestVector3NoBinding v = new
  TestVector3NoBinding(100f, 200f, 300f)` and reads back a field (via a host
  helper that takes the struct by value)
- **THEN** the constructed struct carries the constructor's arguments, the
  byref `this` constructor call reads/writes the in-frame struct bytes, and no
  `ArgumentOutOfRangeException` is thrown (the F-3 reproducer).

#### Scenario: callvirt on a CLR struct override
- **WHEN** an IL method declares a CLR struct that overrides a virtual method
  (e.g. `ToString`) and calls it via `callvirt` on the struct local
- **THEN** the call dispatches to the override on the byref value-type `this`
  with no boxing allocation (the override's `this` is read via the same value-
  type-`this` mechanism as a direct `call`).

### Requirement: CLR value-type instance method boxed direct-call and write-back (Area 4a)

A CLR value-type instance method invoked on a boxed struct SHALL execute
correctly via the generated `*Neo` autogen wrapper, and a mutating method SHALL
propagate its mutation back to the box. The `this` arrives as a 4-byte mStack
index pointing at a boxed struct (a struct that flowed through `object`). The wrapper SHALL unbox the boxed object to a local `T`, invoke
the method on the local, and -- for a method that mutates `this` -- write the
(possibly-mutated) local back to the box (the Neo value-type-`this` write-back,
a flat-bytes re-box of the local into the mStack ref slot).

This is the Neo analog of the Legacy `WriteBackInstance` path. The Neo
`*Neo` wrapper does NOT emit the Legacy `WriteBackInstance` (the Step 13b
finding); the Area 4a write-back IS the value-type-`this` flat-bytes re-box. A
reference-type instance method call is byte-for-byte unchanged (no write-back;
the box IS the object).

The reflection fallback (`CLRMethod.Invoke`) inherits the CLR `MethodInfo.Invoke`
semantics for a boxed-struct instance call (the box is not mutated by the CLR;
the call operates on a copy) and SHALL NOT perform the autogen re-box.

#### Scenario: boxed CLR struct method call round-trip
- **WHEN** an IL method boxes a CLR struct local (`object o = v;`), then calls
  a non-mutating instance method on the box (e.g. via a host helper that
  dispatches through `object`)
- **THEN** the method executes correctly and reads the struct's fields as they
  were at box time.

#### Scenario: mutating instance method on a boxed struct propagates (autogen)
- **WHEN** a CLR struct instance method that mutates `this` (e.g. a `Reset()`
  that zeroes the fields) is invoked on a boxed struct via the autogen `*Neo`
  wrapper, and the box is subsequently unboxed
- **THEN** the mutation propagates to the box (the autogen wrapper re-boxes the
  mutated local), so the subsequent unbox observes the mutation. (Conservative
  default; the reflection-fallback path matches CLR `MethodInfo.Invoke`
  semantics and does NOT propagate.)

### Requirement: Boxed-ref-local to flat-bytes-param bridge (K2-FAM closure)

When a CLR value type LOCAL (stored as a boxed-object reference per the
`CLR value-type Box with and without ValueTypeBinder` requirement) is passed
BY VALUE to a CLR method, the call param-setup SHALL unbox the local's
primitive bytes into the callee's flat-bytes param slot (reusing the unbox
helpers), NOT copy the 4-byte mStack index. This closes the K2-FAM pre-existing
bug where a scalar/boxed-ref CLR value-type local was mis-read as an mStack
index at the call boundary.

**Status:** PARTIAL / DEFERRED (Step 13b apply pass 2026-07-04). The
return-source shape (a CLR struct local obtained from a CLR method RETURN,
which is stored as flat bytes by the return-value requirement) IS closed: that
local is already flat bytes and works without a bridge, and is covered by the
by-value parameter scenario. The Box/Initobj-source shape (a local created via
Box/Initobj, then passed by value) remains DEFERRED: it is not exercised by any
green scenario and has no clean test surface (it needs IL-side `ldfld`/`stfld`
on CLR struct fields, a separate deferred concern). The Box/Initobj-source half
is a documented accepted-known deferred item (silent wrong-result for that
specific source shape), NOT a regression of a previously-green case.

#### Scenario: CLR struct local passed by value
- WHEN a CLR struct is declared as a local, assigned, and then passed by value
  to a CLR method
- THEN the callee observes the assigned field values (no mStack-index
  misinterpretation). (DEFERRED for the Box/Initobj-source shape; the
  return-source shape is covered by the by-value parameter scenario.)

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

### Requirement: Out-of-scope deferrals (explicit, accepted-known)

The following CLR value-type concerns remain DEFERRED and SHALL continue to
throw a clearly-tagged `NotImplementedException` (NOT silently misbehave):

- CLR-method `ref`/`out` parameters (Area 4c): a byref Ref Slot crossing into a
  CLR `ref T` argument requires a typed-reference bridge (copy-in / call /
  copy-out, or a pinned frame address). The IL-method byref path from
  `neo-byref` remains the green target. The autogen `ByRef` branch stays a
  clearly-tagged NIE.
- CLR-object `stind`/`ldind`/`stobj`/`ldobj` via field hash (Area 4d): a Ref
  Slot whose `objectIndex` addresses a CLR object (not an `ILTypeInstance`)
  needs `Ldflda` field-hash stamping and consumer-arm dispatch to
  `CLRType.Get/SetFieldValue(hash, target)`. The current Step 17 arms dispatch
  only on frame-native (`objectIndex == -1`) and heap-IL (`GetNeoILInstance`);
  a CLR-object target stays a clearly-tagged NIE.
- The Box/Initobj-source half of the boxed-ref-local to flat-bytes-param bridge
  (K2-FAM): see that requirement's DEFERRED status.
- A CLR struct instance method `this` (or by-value param) WITH reference fields
  and NO registered `ValueTypeBinder`: throw a clearly-tagged
  `NotImplementedException` directing the user to register a binder (the
  Neo-cursor binder API for ref fields does not exist yet).

The value-type instance `this` direct-call lowering (Area 4b) and the
`Unsafe.Unbox<T>` boxed direct-call mode (Area 4a) are NO LONGER deferred --
they are delivered by the `neo-step13-area4` change (see the "CLR value-type
instance method `this` reading" and "CLR value-type instance method boxed
direct-call and write-back" requirements). The Neo wrapper does NOT emit the
Legacy `WriteBackInstance`; the Area 4a value-type-`this` write-back is a
flat-bytes re-box.

Also accepted-known (not a deferral, a pre-existing bug): the F-2 /
INLINER-REFONLY-VT inliner mis-compile for a ref-only VT local constructed via
`new S(refArgs)` (inlined `stfld.ref.inline` writes do not survive to the
following in-frame `ldfld.ref` read). Suspect: the JIT inliner's ref-fold over
a 0-prim-size VT local. Target for a future optimizer-hardening step or the
K2-FAM bridge child.

#### Scenario: Deferred items still throw clearly-tagged NIEs
- **WHEN** an IL method invokes a CLR method with a `ref`/`out` parameter, or
  executes `stind`/`ldind` on a byref to a CLR object field, or passes a CLR
  struct WITH reference fields and NO binder as a by-value param or as the
  instance `this`
- **THEN** the operation throws a clearly-tagged `NotImplementedException`
  (NOT a silent mis-behavior), and the tag directs the user to the relevant
  follow-up (register a binder / typed-ref bridge / field-hash plumbing).

#### Scenario: No regression on existing CLR instance calls
- **WHEN** the existing NeoStep smoke suite (NeoStep6 through NeoStep18 +
  NeoOptHardening + the NeoStep14_ILEx_* probes) is run after this change
- **THEN** every previously-green case remains green; reference-type CLR
  instance method calls are byte-for-byte unchanged (the value-type-`this`
  discriminator keys on `DeclearingType.IsValueType`, which is FALSE for every
  reference type).
