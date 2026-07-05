# neo-newobj

`newobj` instruction support for the Neo register VM (`ENABLE_NEO_MODE`),
covering the two paths left open after Step 8b (IL reference-type newobj): **IL
value-type** newobj and **CLR-type** newobj. It also establishes the dest/arg
register-aliasing contract that the Q-NEWOBJ investigation (a `new T(intArg)`
immediately following a `newarr`) verified.

IL reference-type `newobj` (Step 8b) is unchanged and is the reference for the
non-VT IL path. Delegate `newobj` remains a Step-19 non-goal.

## Requirements

### Requirement: CLR-type newobj Allocates and Constructs a CLR object

The `ExecuteNeo` `Newobj` arm, when the constructor's declaring type is a
`CLRType`, SHALL route the ctor to `InvokeNeoClrMethod` with `isNewobj = true`
instead of throwing `NotImplementedException`. The dest register SHALL be a
reference temp holding the new object's mStack index.

- Without a Neo Redirection, `CLRMethod.Invoke(targetBase, mStack, true)` SHALL
  construct the object via reflection (`ConstructorInfo.Invoke`) and the returned
  object SHALL be stored into the caller's dest mStack ref slot, with its index
  written to the dest byte offset. The early-return that skips the dest store
  SHALL apply only to the redirect path (which owns its dest write) and to the
  void/non-newobj (`retDstPtr == null`) case; the reflection-newobj path SHALL
  store its result.
- With a Neo Redirection registered on the ctor's `CLRMethod`, the redirect
  delegate runs with `isNewobj = true` and is responsible for allocating the
  object and writing it into the caller's dest mStack ref slot. In practice a
  redirected CLR ctor is rewritten to `Call_Redirect` at JIT time and does not
  reach the Newobj arm; the Newobj arm's `isNewobj = true` path is therefore
  always the reflection path.

#### Scenario: CLR reference-type newobj via reflection

- **WHEN** an IL method executes `var list = new System.Collections.Generic.List<int>();`
  (a CLR type with no Neo Redirection) and uses the result
- **THEN** the newobj constructs the `List<int>` via reflection, the dest
  register holds the new object, the dest mStack ref slot receives the object
  and the dest byte offset receives its index, and no
  `NotImplementedException` is thrown.

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

#### Scenario: CLR newobj enables CLR exception allocation from IL

- **WHEN** an IL method executes `throw new System.InvalidOperationException()`
  and a caller wraps the call in `try`/`catch (InvalidOperationException)`
- **THEN** the CLR newobj allocates the exception object, the `throw` surfaces
  it, and the caller's `catch` block runs -- a side-benefit of CLR-type newobj,
  not a separately-tracked capability.

### Requirement: Q-NEWOBJ dest/arg aliasing contract

A `newobj` of a reference or value type with one or more constructor arguments,
placed immediately after a `newarr` (or any producer whose result is still live
in an adjacent frame region), SHALL produce the correctly-constructed object
with all ctor arguments bound to their actual values. The Call/Newobj lowering's
dest-register handling SHALL NOT observe a stale value from a preceding live
temp in the newobj dest register's frame/ref region. A `new T(intArg)` in
isolation and a `new T(intArg)` immediately after a `newarr` SHALL yield
identical, correct results.

The frame allocator (`AllocateLocalStackSpaces`) gives every distinct temp
register a DISTINCT frame byte region and a DISTINCT mStack ref slot, so the
newobj dest, a preceding `newarr` array temp, and the ctor argument each occupy
non-aliasing regions; no special newobj-lowering fix is required for the aliasing
to hold.

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
  `Newobj` arm throws `NotImplementedException("Neo Newobj delegate is not
  implemented")`. Delegate construction requires `ldftn`/`ldvirtftn` +
  DelegateAdapter (Step 19). In practice real CLR delegates are blocked upstream
  at the `Ldftn` opcode (Step 6), which emits before `newobj`; the delegate-NIE
  arm fires only for a hypothetical IL-defined delegate type.
- **CLR value-type `newobj` with reference fields and no ValueTypeBinder** via
  the reflection path -- inherits the Step 13b
  `NeoClrStructHasReferenceField` NIE guard; a binder-backed CLR value type
  works via the binder/autogen path.
- **Generic-parameter value-type `newobj`** spanning IL/CLR -- deferred with the
  generic-byref follow-up.

#### Scenario: Delegate newobj still throws a clear NIE

- **WHEN** an IL method executes `new System.Action(this.Foo)`
- **THEN** the call throws a `NotImplementedException` (via `Ldftn` upstream, or
  via the `IsDelegate` arm for an IL-defined delegate) -- not a silent wrong
  result, and not a blanket CLR NIE.

### Requirement: IL value-type newobj

The `ExecuteNeo` `Newobj` arm SHALL construct an IL value type using the
caller's frame byte region (the dest register's slot, sized and aligned for the
value type by `AllocateLocalStackSpaces`) as the construction site. The arm
SHALL NOT allocate a heap `ILTypeInstance` for the value-type case. The
construction steps SHALL be:

1. **Zero-initialize** the dest region (`Unsafe.InitBlock(..., 0,
   TotalPrimitiveSize)`) and null every one of the dest's `TotalReferenceCount`
   ref slots, matching `Initobj` semantics (a ctor may set only some fields).
2. **Seed the ctor's `this`** (callee param slot 0) with a frame-native address
   of the caller's dest region, so the ctor's `this.field =` writes land in the
   caller's dest slot (the `neo-byref` capability owns the seeding mechanism).
3. **Copy the remaining ctor args** into callee param slots [1..] via the
   standard `CopyNeoCallArguments` path.
4. **Invoke the ctor** via `InvokeNeoCallTarget(ctor, isNewobj=true, ...)`. No
   mStack `this` object is pushed (contrast the IL reference-type path); no
   post-ctor copy-back is performed (the frame-native `this` makes the ctor's
   writes land directly in the caller's dest region).

The newobj-dest-as-in-frame-VT contract -- the dest register of a VT `newobj`
SHALL be typed as the constructed VT by the JIT type-specialization pass so the
caller's subsequent field reads/writes lower to `_Inline` -- is owned by the
`neo-value-types` capability.

The arm SHALL continue to throw a loud, tagged `NotImplementedException` for
the **non-goals** (delegate newobj, no-binder CLR-VT-with-reference-fields
newobj, generic-parameter VT newobj) rather than constructing a silently-wrong
object.

#### Scenario: IL value-type newobj with a parameterless ctor

- **WHEN** an IL method emits `new S()` for an IL value type `S` with a
  parameterless ctor (which may be a no-op or set defaults), and reads a field
  of the result
- **THEN** the dest region is zero-initialized, the ctor runs, and the field
  read returns either the zero default or the value the ctor assigned.

#### Scenario: IL value-type newobj with a ctor that sets fields

- **WHEN** an IL method emits `new S(args)` whose ctor sets one or more fields
  via `this.field = ...`, and the caller reads those fields
- **THEN** every field the ctor assigned reads back with the assigned value
  (primitive and reference fields alike), with no heap `ILTypeInstance`
  allocated for the value type and no post-ctor copy-back.

#### Scenario: IL value-type local-form ctor (`VT x = new VT(args)`)

- **WHEN** an IL method executes `S x = new S(args);` (compiles to
  `ldloca x; call S::ctor`) and reads `x.field`
- **THEN** the ctor's `this.field =` writes land in the caller's frame slot for
  `x`, the subsequent `x.field` read returns the assigned value, and no opaque
  `NullReferenceException` is thrown.

#### Scenario: IL value-type newobj result survives intervening operations

- **WHEN** an IL method constructs `new S(args)`, performs unrelated operations
  that reuse eval-stack registers, and only then reads a field of the result
- **THEN** the field read returns the ctor-assigned value (no stale/clobbered
  value from the intervening operations).

#### Scenario: Non-goals remain NIE-tagged

- **WHEN** an IL method emits a delegate `newobj` (`new Action(foo)`), a
  no-binder CLR-VT-with-reference-fields `newobj`, or a generic-parameter VT
  `newobj`
- **THEN** the `Newobj` arm SHALL throw the corresponding tagged
  `NotImplementedException`; it SHALL NOT silently mis-construct.
