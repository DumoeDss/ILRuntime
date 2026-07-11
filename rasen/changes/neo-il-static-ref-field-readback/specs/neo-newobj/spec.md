# neo-newobj spec delta — neo-il-static-ref-field-readback

> MODIFIED capability `neo-newobj`. The existing "Q-NEWOBJ dest/arg aliasing
> contract" is extended to cover the **reference-argument** case it previously
> missed (the contract was verified only for a primitive `intArg`).

## Requirement: Q-NEWOBJ dest/arg aliasing contract

A `newobj` of a reference or value type with one or more constructor arguments,
placed immediately after a `newarr` (or any producer whose result is still live
in an adjacent frame region), SHALL produce the correctly-constructed object
with all ctor arguments bound to their actual values. The Call/Newobj lowering's
dest-register handling SHALL NOT observe a stale value from a preceding live
temp in the newobj dest register's frame/ref region. A `new T(intArg)` in
isolation and a `new T(intArg)` immediately after a `newarr` SHALL yield
identical, correct results. A `new T(refArg)` whose newobj dest register aliases
the `refArg` register (the canonical `ldstr/ldloc refArg; newobj(refArg)`
eval-stack lowering, e.g. a C# lazy-init `if (x == null) x = new T(refArg);`)
SHALL likewise bind the ctor's reference argument to the actual arg object, not
to the newly-constructed instance.

The frame allocator (`AllocateLocalStackSpaces`) gives every distinct temp
register a DISTINCT frame byte region and a DISTINCT mStack ref slot, so the
newobj dest, a preceding `newarr` array temp, and a primitive ctor argument each
occupy non-aliasing regions; no special newobj-lowering fix is required for a
PRIMITIVE argument's aliasing to hold. A REFERENCE argument is different: its
slot value is an mStack index, and when the newobj dest register aliases the
reference arg's register (same register ⇒ same `RefOffset`), the arg's mStack
index EQUALS the dest ref slot index (`newobjDstIdx = frameRefBase +
dstRefOffset`). The IL reference-type `newobj` arm SHALL, BEFORE storing the new
instance at `mStack[newobjDstIdx]`, detect this collision — the dest's
`dstRefOffset` appears in the call's ref-source map AND the arg index at the
dest register equals `newobjDstIdx` — and re-base the colliding reference arg
object to a fresh mStack slot (rewriting the source register), so
`CopyNeoCallArguments` copies the arg object's index into the ctor frame rather
than the about-to-be-clobbered dest slot. The discriminator (ref-source-map
membership) SHALL exclude a primitive `int` argument whose value coincidentally
equals `newobjDstIdx` (its register has no ref slot and is never re-based).

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

#### Scenario: newobj whose dest aliases a reference argument (lazy-init)

- **WHEN** an IL method lazy-initializes a static or local reference field via
  `if (x == null) x = new T(refArg);` (so the newobj dest register aliases the
  `refArg` register) and then reads a field of `x` whose value was set by the
  ctor from `refArg`
- **THEN** the read field equals `refArg` (the ctor received `refArg`, not the
  new instance), AND the dest register ends up holding the new instance. A
  primitive `int` argument whose value happens to equal the dest ref slot index
  is left unchanged (the ctor receives the int, not a re-based index).
