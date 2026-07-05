## MODIFIED Requirements

### Requirement: IL value-type construction via newobj

The Neo VM SHALL support construction of an IL value type via the `newobj`
instruction, using the caller's frame byte region (the dest register's slot,
sized and aligned for the value type by the frame allocator) as the construction
site. The `newobj` SHALL zero-initialize the dest region before invoking the
ctor, and the ctor's `this` SHALL be a frame-native Ref Slot so that field
assignments inside the ctor propagate into the caller's frame slot. This is the
value-type analog of the Step 8b reference-type newobj and relies on the Step 17
byref/Ref-Slot model for the ctor `this`.

The detailed construction contract (frame zero-init, Ref-Slot `this`, ctor
writeback, base-ctor chain, default-ctor and ctor-with-args scenarios) is owned
by the `neo-newobj` capability; the byref-`this` call-ABI caller contract is
owned by the `neo-byref` capability. This capability owns the **end-to-end
in-frame-address consistency** that makes the two agree: a value-type `this`
(param slot 0 of a VT instance method) and a value-type `newobj` dest SHALL be
tracked as in-frame addresses for ALL field access (ctor `stfld` + caller
`ldfld`), so no method can lower a single logical VT field access to a mix of
in-frame `_Inline` and heap `GetNeoILInstance` arms.

The JIT type-specialization pass SHALL seed the dest register of a `Newobj` of
an IL value type with the constructed VT type (the same rule already applied
for `Ldloca` and `Ldflda` dests), so the field-access discriminator
(`TryRewriteFieldAccessForInline`) recognizes the dest as an in-frame value type
and rewrites the caller's subsequent `ldfld`/`stfld` to the `_Inline` variant.
The heap-object field-access opcodes and their `ExecuteNeo` arms SHALL remain
unchanged.

The `addrAlias`/`liveAliasMap` machinery SHALL additionally treat a value-type
`this` parameter (param slot 0 of a method whose declaring type is an IL value
type) as a pre-existing in-frame address root for the duration of the method
body, so the ctor's `this.field` chains fold to compile-time offsets exactly as
an `ldloca`-produced address would. (A value-type `newobj` dest needs no new
`addrAlias` entry on the caller side -- the dest register IS the owning slot,
resolved directly once typed.)

#### Scenario: IL value-type newobj with a multi-field ctor

- **WHEN** an IL method emits `new S(args)` for an IL value type `S` whose ctor
  sets multiple fields via `this.field = ...` (a mix of primitive and reference
  fields), and the caller reads those fields from the newobj result
- **THEN** the ctor's `this.field =` writes lower uniformly to `_Inline`
  opcodes, the caller's field reads lower uniformly to `_Inline` opcodes, both
  ends resolve against the SAME dest frame byte/ref region, and every field
  round-trips with the value the ctor assigned -- with no heap `ILTypeInstance`
  allocated for the value type and no post-ctor copy-back.

#### Scenario: IL value-type local-form ctor (`VT x = new VT(args)`)

- **WHEN** an IL method executes `S x = new S(args);` (which the C# compiler
  lowers to `ldloca x; call S::ctor`) and then reads `x.field`
- **THEN** the ctor's `this.field =` writes land in the caller's frame slot for
  `x`, the subsequent `x.field` read returns the assigned value, and no opaque
  `NullReferenceException` is thrown. (This is the common C# idiom; it shares
  the VT-`this` in-frame-address root with the newobj-instruction form.)

#### Scenario: VT newobj result read after intervening heap writes / register reuse

- **WHEN** an IL method constructs `new S(args)`, then performs one or more
  unrelated heap writes or call results that reuse eval-stack registers, and
  only THEN reads a field of the newobj result
- **THEN** the field read returns the value the ctor assigned (no stale/clobbered
  value from the intervening operations); the `liveAliasMap` per-instruction
  snapshot correctly tracks the newobj dest's live range across the reuse.

#### Scenario: Nested value type constructed via newobj

- **WHEN** an IL value type `Outer` contains a nested value-type field `Inner
  inner` and is constructed via `new Outer(args)`, where the Outer ctor sets
  `this.inner.x` and `this.inner.y`
- **THEN** the nested-field writes resolve to single `_Inline` accesses at the
  correct absolute offsets in the dest region, and reading `outer.inner.x` /
  `outer.inner.y` from the caller returns the assigned values.

#### Scenario: VT returned from a method then field-read

- **WHEN** an IL method calls a method `S Make()` returning an IL value type,
  stores the result, and reads a field of it
- **THEN** the return-value path (which lowers to a `Move_Vt` into the caller's
  dest slot) produces an in-frame value whose subsequent field read returns the
  value assigned inside `Make`.

#### Scenario: Base-ctor chain does not re-allocate the VT this

- **WHEN** an IL value-type ctor `S(int a) : base(a)` calls a base IL ctor that
  sets a field via `this.field =`
- **THEN** the chained base-ctor `Call` forwards the same in-frame `this`
  (param slot 0) without allocating a fresh frame region, and the field set by
  the base ctor is observable in the caller's dest slot after `new S(a)`.

#### Scenario: No regression on existing in-frame value-type usage

- **WHEN** the existing NeoStep smoke suite (NeoStep6 through NeoStep18) is run
  after this change
- **THEN** every previously-green case remains green (the additive dest-typing
  and the `this`-root extension do not perturb the existing `_Inline` /
  `addrAlias` fast paths for `ldloca`/`ldflda`-rooted accesses).
