## MODIFIED Requirements

### Requirement: Byref `this` for value-type constructor invocation

The byref call-ABI SHALL additionally serve as the `this` parameter of an IL
value-type constructor invoked via `newobj`. The `newobj` arm SHALL seed the
ctor's callee param region `this` slot (param slot 0) with a frame-native
address of the caller's dest region, so the ctor treats `this` as a managed
pointer to the caller's frame slot. Field assignments in the ctor
(`this.field = ...`) SHALL resolve through the existing `stind_*`/`stfld` /
`_Inline` machinery into the caller's dest region, exactly as an explicit
byref/out parameter would.

The seeding mechanism SHALL be consistent with the callee's `ParamInfos[0]`
layout, which for a value-type ctor is sized as the in-frame value
(`Size = TotalPrimitiveSize`, `RefCount = TotalReferenceCount`). The runtime
SHALL seed the callee's slot-0 bytes (and, for a value type with reference
fields, the callee's slot-0 ref slots) so that the ctor's `_Inline` field
accesses -- resolved through the `addrAlias`/`liveAliasMap` root at param slot
0 -- target the caller's dest region. (Equivalently, the runtime MAY byte-copy
the dest region into the callee's `this` slot before the call and back after
the call; the contract is that the ctor's `this`-relative writes are observable
in the caller's dest slot.)

This is a new caller of the byref call-ABI (a byref `this`), not a change to
the ABI itself. The detailed newobj-side construction contract is owned by the
`neo-newobj` capability; the end-to-end in-frame-address consistency (the
`addrAlias` root at param slot 0 and the typed newobj dest) is owned by the
`neo-value-types` capability.

#### Scenario: IL value-type newobj passes the dest as the ctor this

- **WHEN** an IL method emits `new S(args)` for an IL value type `S` and the
  ctor writes `this.field = value`
- **THEN** the ctor's `this` (param slot 0) resolves to the caller's dest frame
  region, the field write lands in that region, and after the ctor returns the
  caller's newobj result dest holds the assigned field value -- with no heap
  `ILTypeInstance` for the value type and no separate post-ctor copy that could
  diverge from the ctor's writes.

#### Scenario: Value-type ctor with reference fields

- **WHEN** an IL value type `S` has one or more reference fields and is
  constructed via `new S(args)` where the ctor assigns those reference fields
  via `this.refField = ...`
- **THEN** the reference-field assignments propagate to the caller's dest ref
  slots (the callee's slot-0 ref slots are seeded to address the caller's dest
  ref region), and reading those fields from the caller returns the assigned
  references (incl. the null case).

#### Scenario: No regression on existing byref callers

- **WHEN** the existing NeoStep smoke suite is run after this change
- **THEN** every previously-green byref/ref/out/ldelema case remains green
  (the byref call-ABI itself is unchanged; only a new `this` caller is added).
