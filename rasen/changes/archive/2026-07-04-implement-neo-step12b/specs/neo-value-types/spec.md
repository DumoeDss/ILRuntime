# neo-value-types

## ADDED Requirements

### Requirement: Whole value-type copy and assignment

The Neo VM SHALL correctly copy an entire in-frame value type on assignment
(`T a = b;`), local initialization, `starg` of a value type, and any other IL
site that lowers to a register `Move` whose destination is an in-frame value
type. The copy SHALL preserve C# value semantics: every primitive/value field
is copied independently so that subsequent mutation of the source does not
affect the destination, while reference fields share object identity (shallow
copy, matching CLR struct copy semantics).

A value-type copy with one or more reference fields (`TotalReferenceCount > 0`)
SHALL be lowered to a dedicated `Move_Vt` opcode whose `ExecuteNeo` arm copies
the destination's full primitive byte range with `Unsafe.CopyBlock` and then
copies each of the destination's reference-field mStack slots individually
(preserving the Neo null convention on both source and destination). A
pure-primitive value type (`TotalReferenceCount == 0`) MAY keep the existing
`Move` opcode, since its byte `CopyBlock` is already correct with no reference
slots to copy.

The JIT SHALL make the copy-vs-no-copy decision at compile time from the
destination register's static type, before Neo offset-lowering overwrites the
register index with a byte offset. The destination slot's primitive size and
reference count SHALL be encoded into the opcode's standalone (non-union-aliased)
Operand fields so they survive offset-lowering intact.

#### Scenario: Pure-primitive value-type copy

- **WHEN** an IL method declares `Vector3 a; Vector3 b; ... b = a;` (a value
  type with only primitive fields, `TotalReferenceCount == 0`)
- **THEN** the assignment executes correctly (all three fields copied), the
  JIT emits a plain `Move` (its byte CopyBlock suffices), and mutating `a.x`
  after the copy does not change `b.x`.

#### Scenario: Value type with a single reference field

- **WHEN** an IL method declares `struct S { int a; string b; }`, assigns
  `S x; ... S y = x;`, and the source has a non-null `b`
- **THEN** after the copy `y.a == x.a`, `y.b` refers to the same string
  instance as `x.b` (shared identity), and mutating `x.a` does not change
  `y.a`. No `NotImplementedException` is thrown and no reference is truncated.

#### Scenario: Value type with multiple reference fields

- **WHEN** an IL method declares `struct S { string p; string q; string r; }`
  with `TotalReferenceCount == 3` and copies `S a = b;` where `b` has distinct
  non-null values for all three reference fields
- **THEN** all three reference fields are copied independently (none truncated,
  none dropped), `a.p == b.p`, `a.q == b.q`, `a.r == b.r` (identity shared per
  shallow-copy semantics), and the JIT emits `Move_Vt` (not the single-ref
  `Move`).

#### Scenario: Nested value type copy

- **WHEN** an IL method declares `struct Inner { int x; string s; } struct
  Outer { Inner i; int y; }`, populates an `Outer` source, and copies
  `Outer o2 = o1;`
- **THEN** the copy reproduces every primitive and reference field of the
  nested value type in one operation (no recursion), the nested `Inner`'s
  primitive and reference fields are correctly placed in the destination, and
  mutating a source value field after the copy does not affect the destination.

#### Scenario: Copy-aliasing independence

- **WHEN** an IL method copies `S b = a;` and then writes a different value to
  a primitive field of `a`
- **THEN** the corresponding field of `b` retains its pre-mutation value (the
  primitive CopyBlock is a value copy, not an alias).

### Requirement: LowerMove JIT pass placement and union safety

The JIT pass that selects `Move_Vt` over `Move` (LowerMove) SHALL run on the
register-index form of the method body, before Neo offset-lowering, and SHALL
derive the destination's value type from the per-register static type map that
the type-specialization pass already maintains. This is required because Neo
offset-lowering overwrites register indices with byte offsets (the `OpCodeR`
explicit-layout union aliases `Register1/2/3` with `DstOffset/SrcOffset/
OperandOffset`), so a register index needed for type lookup is unavailable
after lowering.

The value type's primitive size and reference count SHALL be encoded into the
`Move_Vt` opcode's standalone Operand fields (offsets that do not alias any
register or byte-offset field), not into the aliased register/offset union
fields. This guarantees the encoded data survives offset-lowering and is
readable by the `ExecuteNeo` arm.

The pass SHALL run after the Backwards/Forward Copy-Propagation passes
(BCP/FCP), which operate on `Move` instructions in register-index form and may
elide them. The pass SHALL only ever observe the surviving `Move`s, so it does
not perturb and is not starved by copy-elision.

#### Scenario: BCP/FCP still elide redundant value-type Moves

- **WHEN** a method contains a redundant value-type `Move` that BCP or FCP can
  eliminate (e.g. a self-move `a = a`, or a copy whose result is overwritten
  before use)
- **THEN** BCP/FCP elide the `Move` before LowerMove runs, LowerMove never
  converts it to `Move_Vt`, and the resulting body contains no dead value-type
  copy. Previously-green NeoStep cases that rely on copy-elision of value-type
  Moves remain green.

#### Scenario: Reference-type Moves are unaffected

- **WHEN** a method performs a reference-type assignment (`object o = other;`)
  or a single-reference value-type-field load
- **THEN** the JIT emits a plain `Move` with the existing single-reference-copy
  semantics; LowerMove does NOT rewrite it to `Move_Vt`, and the existing
  reference-move behavior is unchanged.
