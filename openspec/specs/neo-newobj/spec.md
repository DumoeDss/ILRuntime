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

### Requirement: IL value-type newobj -- DEFERRED (Q-VT-NEWOBJ / [VT-THIS-ADDR])

The IL value-type `newobj` path is **DEFERRED**. The `ExecuteNeo` `Newobj` arm
SHALL throw a loud, Step-18-tagged `NotImplementedException`
(`"Neo Newobj IL value-type is not implemented (Step 18 D1 blocked on VT
field-access lowering consistency; see D2)"`) when the constructor's declaring
type is an IL value type, rather than constructing a silently-wrong object.

The intended design (to be delivered by the [VT-THIS-ADDR] follow-up): the arm
SHALL NOT allocate a heap `ILTypeInstance`; it SHALL zero-initialize the dest
register's frame byte region (the construction site) and pass the ctor a
frame-native **Ref Slot** `(-1, destFrameByteOffset)` as `this`, so the ctor's
`this.field =` writes propagate into the caller's dest slot with no post-ctor
copy-back.

The blocker is a VT field-access lowering consistency mismatch: a VT ctor's
`this` (param slot 0) is laid out and seeded as the in-frame declaring value
type (NOT an 8-byte byref), so `this.field =` rewrites to `_Inline` and writes
the callee's own frame slot; meanwhile the caller passes `this` as an mStack
index or a Ref Slot, and `addrAlias` only tracks `ldloca`/`ldflda`-produced
addresses -- never a `this` param or a newobj dest -- so the caller's and
callee's VT representations never agree. A heap-instance-`this` fallback is NOT
simpler: the callee still seeds `this` as the VT type and lays out an in-frame
VT-sized slot, so a heap index would be reinterpreted as a frame offset. The fix
is the D2 change (track a VT `this` / newobj-dest as an in-frame address for ALL
field access), which touches Step 12 and every VT instance method -- too broad
for Step 18.

The **local form** `VT x = new VT(args)` (the common C# idiom) compiles to
`ldloca x; call ctor`, NOT a `newobj` instruction, so it bypasses this NIE and
crashes opaquely (`NullReferenceException` at the first `stfld`) -- the same
VT-`this` root cause, folded into [VT-THIS-ADDR]. It is pre-existing (Step 12
tests use only `default(T)` + direct field-set, never a user ctor).

#### Scenario: IL value-type newobj throws a tagged NIE (newobj-instruction form)

- **WHEN** an IL method emits a real `newobj` of an IL value type (e.g. `return
  new S(args);` for a non-inlined result)
- **THEN** the `Newobj` arm SHALL throw the Step-18-tagged
  `NotImplementedException`; it SHALL NOT silently mis-construct the value type.

#### Scenario: IL value-type local-form ctor (deferred, opaque crash)

- **WHEN** an IL method executes `S x = new S(args);` (compiles to `ldloca x;
  call ctor`)
- **THEN** until [VT-THIS-ADDR] lands, the ctor's first `this.field =`
  NullRefs opaquely. This is a known pre-existing failure, tracked under
  Q-VT-NEWOBJ-local / [VT-THIS-ADDR], not a regression introduced by this
  capability.
