# neo-newobj

`newobj` instruction support for the Neo register VM (`ENABLE_NEO_MODE`),
covering the two paths left open after Step 8b (IL reference-type newobj): **IL
value-type** newobj and **CLR-type** newobj. It also establishes the dest/arg
register-aliasing contract that the Q-NEWOBJ fix (a `new T(intArg)` immediately
following a `newarr`) restores.

IL reference-type `newobj` (Step 8b) is unchanged and is the reference for the
non-VT IL path. Delegate `newobj` remains a Step-19 non-goal.

## Requirements

### Requirement: IL value-type newobj constructs in the caller's frame

The `ExecuteNeo` `Newobj` arm, when the constructor's declaring type is an IL
value type, SHALL NOT allocate a heap `ILTypeInstance`. Instead it SHALL use the
dest register's frame byte region (already sized and aligned by the frame
allocator for the value type) as the construction site. The arm SHALL
zero-initialize the dest region's `TotalPrimitiveSize` bytes and null every one
of the dest region's `TotalReferenceCount` mStack ref slots before invoking the
constructor, so that fields the ctor does not explicitly set hold the CLR
value-type default.

#### Scenario: IL value-type newobj with a parameterless ctor

- **WHEN** an IL method executes `struct S { int a; } S s = new S();` and reads
  `s.a`
- **THEN** the newobj does not allocate a heap object, `s.a` reads as `0` (the
  zero-init), and no `NotImplementedException` is thrown.

#### Scenario: IL value-type newobj with a field-setting ctor

- **WHEN** an IL method executes `struct S { int a; public S(int v){ a = v; } }`
  and `S s = new S(7);` then reads `s.a`
- **THEN** the ctor's `this.a = v` writes through the frame address into the
  caller's dest slot, and `s.a` reads as `7` -- the value is in the caller's
  frame, not a temporary heap copy.

#### Scenario: IL value-type newobj with a reference field

- **WHEN** an IL method executes `struct S { int a; string b; public S(string
  t){ a = 1; b = t; } }` and `S s = new S("x");` then reads `s.a` and `s.b`
- **THEN** `s.a == 1`, `s.b` refers to `"x"`, both fields are written into the
  caller's frame (the primitive byte region and the dest's mStack ref slot
  respectively), and the un-set-state default for any field not assigned by the
  ctor is the value-type zero/null.

### Requirement: IL value-type ctor `this` is a frame-native Ref Slot

For an IL value-type `newobj`, the constructor's `this` parameter SHALL be
passed as an 8-byte frame-native **Ref Slot** `(-1, destFrameByteOffset)` in the
callee param region's `this` slot (reusing the Step 17 byref call-ABI). The
constructor SHALL operate on the caller's frame data directly through that
address -- `stfld`/`stind` of `this.field` resolves into the caller's dest slot
with no copy-back after the ctor returns. The newobj SHALL NOT push a fresh
mStack `this` object for the value-type case (the `this` is an address, not an
object).

#### Scenario: ctor field assignment propagates to the caller frame

- **WHEN** a value-type ctor does `this.field = value` and the caller reads the
  field after `newobj` returns
- **THEN** the caller observes the ctor's assignment without any post-ctor copy
  step, because the ctor wrote through a frame-native Ref Slot into the caller's
  dest region.

#### Scenario: base-ctor chain forwards the byref this

- **WHEN** a value-type ctor `: base(...)` calls its base IL ctor
- **THEN** the base ctor receives the same frame-native `this` Ref Slot
  (propagated as a normal byref first parameter) and its field assignments land
  in the same caller frame slot; no intermediate heap allocation occurs.

### Requirement: CLR-type newobj allocates and Constructs a CLR object

The `ExecuteNeo` `Newobj` arm, when the constructor's declaring type is a
`CLRType`, SHALL route the ctor to `InvokeNeoClrMethod` with `isNewobj = true`
instead of throwing `NotImplementedException`. The dest register SHALL be a
reference temp holding the new object's mStack index.

- With a Neo Redirection registered on the ctor's `CLRMethod`, the redirect
  delegate SHALL run with `isNewobj = true` and is responsible for allocating
  the object and writing it into the caller's dest mStack ref slot.
- Without a Neo Redirection, `CLRMethod.Invoke(targetBase, mStack, true)` SHALL
  construct the object via reflection (`ConstructorInfo.Invoke`) and the returned
  object SHALL be stored into the caller's dest mStack ref slot, with its index
  written to the dest byte offset.

#### Scenario: CLR reference-type newobj via reflection

- **WHEN** an IL method executes `var list = new System.Collections.Generic.List<int>();`
  (a CLR type with no Neo Redirection) and uses the result
- **THEN** the newobj constructs the `List<int>` via reflection, the dest
  register holds the new object, and no `NotImplementedException` is thrown.

#### Scenario: CLR type newobj with a Neo Redirection

- **WHEN** an IL method constructs a CLR type whose ctor has a registered Neo
  Redirection and the redirect is newobj-capable
- **THEN** the redirect delegate runs with `isNewobj = true`, allocates the
  object, and writes it into the caller's dest ref slot; the result is usable.

#### Scenario: CLR newobj of a generic closed type

- **WHEN** an IL method executes `new Dictionary<int, string>()` (a closed
  generic CLR type)
- **THEN** the newobj resolves the closed-generic ctor and constructs it, with
  the dest register holding the new object.

### Requirement: Q-NEWOBJ dest/arg aliasing contract

A `newobj` of a reference or value type with one or more constructor arguments,
placed immediately after a `newarr` (or any producer whose result is still live
in an adjacent frame region), SHALL produce the correctly-constructed object
with all ctor arguments bound to their actual values. The Call/Newobj lowering's
dest-register handling SHALL NOT observe a stale value from a preceding live
temp in the newobj dest register's frame/ref region. A `new T(intArg)` in
isolation and a `new T(intArg)` immediately after a `newarr` SHALL yield
identical, correct results.

#### Scenario: newobj-with-arg after a newarr

- **WHEN** an IL method executes `T[] a = new T[n]; T item = new T(intArg);`
  and reads `item.field`
- **THEN** `item.field` equals the value passed for `intArg` (the ctor received
  the correct argument), and the array `a` remains intact (the newobj dest did
  not clobber the array reference).

#### Scenario: No regression on newobj-with-arg in isolation

- **WHEN** an IL method executes `T item = new T(intArg);` with no preceding
  `newarr` (the Step 16 TC7 form)
- **THEN** `item.field` equals the value passed for `intArg`, unchanged from the
  pre-Step-18 behavior.

### Requirement: Non-goals of newobj completion

The following remain unimplemented and SHALL continue to throw a Step-tagged
`NotImplementedException` (they are NOT introduced by this capability):

- **Delegate `newobj`** (`new System.Action(foo)`, `new Func<...>(bar)`) -- the
  arm throws `NotImplementedException("Neo Newobj delegate is not implemented")`.
  Delegate construction requires `ldftn`/`ldvirtftn` + DelegateAdapter (Step 19).
- **CLR value-type `newobj` with reference fields and no ValueTypeBinder** via
  the reflection path -- inherits the Step 13b
  `NeoClrStructHasReferenceField` NIE guard; a binder-backed CLR value type
  works via the binder/autogen path.
- **Generic-parameter value-type `newobj`** spanning IL/CLR -- deferred with the
  generic-byref follow-up.

#### Scenario: Delegate newobj still throws a clear NIE

- **WHEN** an IL method executes `new System.Action(this.Foo)`
- **THEN** the `Newobj` arm throws `NotImplementedException` naming the delegate
  case (not a silent wrong result, and not a blanket CLR NIE).
