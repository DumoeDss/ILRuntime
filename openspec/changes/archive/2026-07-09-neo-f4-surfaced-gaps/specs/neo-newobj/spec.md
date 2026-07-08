# neo-newobj delta -- neo-f4-surfaced-gaps

## MODIFIED requirements

### Requirement: IL reference-type newobj allocates a flat instance covering ALL fields (own + inherited-from-IL-base)

The `ExecuteNeo` `Newobj` arm, when the constructor's declaring type is an
`ILType` of reference kind, SHALL allocate the new `ILTypeInstance` via
`ilNewobjType.Instantiate(false)` and that instance's flat storage -- the
`byte[] Primitives` (sized to `TotalPrimitiveSize`) and the `AutoList
ManagedObjects` (sized to `TotalReferenceCount`) -- SHALL cover ALL instance
fields, including fields INHERITED from an IL base type, so that a subsequent
`stfld` / `ldfld` on an inherited field (which indexes
`ManagedObjects[ReferenceOffset]` / `Primitives[PrimitiveOffset]` directly,
using the JIT-stamped offset) lands in bounds.

`ILType.InitializeFields` SHALL compute `TotalPrimitiveSize` and
`TotalReferenceCount` as FLAT totals: the offset accumulators SHALL start at
the IL base type's already-flat `TotalPrimitiveSize` / `TotalReferenceCount`
(when `BaseType is ILType`), so this type's own field region begins AFTER the
inherited region. This mirrors Legacy's flat `TotalFieldCount` (which
accumulates the IL base) and is required because the Neo runtime field-access
arms assume flat absolute offsets. This requirement is Neo-only (the
`#if ENABLE_NEO_MODE` arm of `InitializeFields`); the Legacy arm is
byte-identical.

A derived IL type whose IL base declares a reference field, constructed via a
`DerivedCtor(args) : base(args)` ctor whose base ctor assigns the arg to that
field, SHALL execute the base ctor's `stfld` without throwing (in particular,
`ManagedObjects` SHALL be non-null and sized to hold the inherited field).

#### Scenario: Derived IL exception ctor with :base(msg) assigns the inherited field
- **WHEN** an IL type `MyEx : System.Exception` declares `public string Msg;`
  and a ctor `MyEx(string msg) { Msg = msg; }`, and a derived IL type
  `DerivedEx : MyEx` declares `DerivedEx(string msg) : base(msg) { }`, and an
  IL method constructs `new DerivedEx("derived-msg")` and reads the `Msg`
  field off the recovered `ILTypeInstance` via the Neo indexer.
- **THEN** the construction SHALL NOT throw, and `Msg` SHALL equal
  `"derived-msg"` (the arg passed through the `:base(msg)` chain reaches the
  inherited field).
- **AND** a plain `new MyEx("ctor-msg")` (no IL base) SHALL continue to set
  `Msg == "ctor-msg"` (regression: the non-derived path is unchanged).

#### Scenario: Plain (non-derived) IL ctor string arg is unchanged
- **WHEN** an IL method constructs `new MyEx("ctor-msg")` and reads `Msg`.
- **THEN** `Msg` SHALL equal `"ctor-msg"` (the parametrized-Run follow-on
  already closed this path; this scenario guards against regression).
