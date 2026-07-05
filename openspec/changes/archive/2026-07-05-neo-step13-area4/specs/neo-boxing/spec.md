## ADDED Requirements

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

The `this`-slot representation is discriminated by the Ref Slot's
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

## MODIFIED Requirements

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
  and NO registered `ValueTypeBinder`: throw a clearly-tagged `NotImplementedException`
  directing the user to register a binder (the Neo-cursor binder API for ref
  fields does not exist yet).

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
