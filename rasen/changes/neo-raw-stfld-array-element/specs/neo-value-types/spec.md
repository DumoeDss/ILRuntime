## ADDED Requirements

### Requirement: Raw Stfld with a CLR-struct array-element owner

When the Neo JIT's typed field-splitter lowers a CIL `stfld` whose field is declared on a CLR type, it
leaves the raw `OpCodeREnum.Stfld` opcode in place with
`OperandLong = (typeHash << 32) | fieldHash` (identical to Legacy's raw encoding; the field's
declaring type is a CLRType, so the typed splitter -- which fires only for an ILType declaring type --
does not apply). The owner of such a `stfld` may be a **CLR-struct array element**: the CIL shape
`clrStructArray[i].field = x` lowers to `ldelema <StructType>; stfld <field>`, where `ldelema` on a
CLR value-type-element array produces an 8-byte byref `(arrIdx, elementIdx)` whose `off` half is the
ELEMENT INDEX (not a byte offset) -- the same encoding the `stind_*`/`ldind_*` consumer arms and
Legacy's `ObjectTypes.ArrayReference` writeback consume. The Neo interpreter SHALL execute raw
`Stfld` for this array-element owner -- it MUST NOT throw the Step-tagged "array-element field write
is deferred (stfld on a CLR array element)" `NotImplementedException`.

The interpreter SHALL resolve the array-element owner by reading `objIdx = *(int*)(frameBase +
ownerOff)` (the mStack index of the `System.Array`) and `elementIdx = *(int*)(frameBase + ownerOff +
4)` (the element index), then perform a box/mutate/unbox round-trip byte-identical to Legacy's
`ObjectTypes.ArrayReference` writeback: box the element via `Array.GetValue(elementIdx)` (a boxed copy
of the struct), reflection-write the field via the already-resolved `FieldInfo.SetValue(boxedElem,
value)`, and write the mutated struct back via `Array.SetValue(boxedElem, elementIdx)`. The source
`value` is the field-category-boxed object already produced earlier in the raw `Stfld` handler
(primitive / value-type / reference). The existing typed field arms, the CLR ref-type / CLR value-type
frame-byref raw-owner arms (child-4), and the IL-instance-CLR-base raw-owner arm (child-9) SHALL be
unaffected.

This applies to BOTH raw-`Stfld` declaring-type branches: the CLR value-type declaring branch (the
reachable `clrStructArray[i].field = x` case) and the CLR ref-type declaring branch (unreachable via
`ldelema` on a ref-type-element array, which throws, but routed through the same box/mutate/unbox for
symmetry and fail-soft). Raw `Ldfld` with an array-element owner is NOT covered by this requirement
(see Rationale: the Neo frame is untyped, so a raw `Ldfld` value-type owner cannot be safely
distinguished from a flat-bytes local owner without a JIT marker; deferred to a follow-up).

#### Scenario: Write a CLR-struct field through an array-element owner (Stfld, value-type declaring)
- **WHEN** IL code executes `stfld` on a field declared on a CLR value type, and the owner slot holds
  a `ldelema`-produced array-element byref `(arrIdx, elementIdx)` with `mStack[arrIdx]` a CLR-struct
  `System.Array`
- **THEN** the Neo interpreter boxes the element at `elementIdx` via `Array.GetValue`, reflection-
  writes the field via `FieldInfo.SetValue`, and writes the mutated struct back via
  `Array.SetValue`, without throwing `NotImplementedException`, and the mutated value is observable
  in the array element on a subsequent read

#### Scenario: Element index is decoded correctly across multiple indices
- **WHEN** the same field is written through array-element byrefs at several distinct element indices
- **THEN** each write lands in the correct element (the element index, not the array mStack index, is
  the addressing half), proving the `(arrIdx, elementIdx)` decode is correct

#### Scenario: Regression probe faults without the fix
- **WHEN** a NeoStep probe that allocates a CLR-struct array and writes a field through an element
  (`arr[i].field = x`) is run against a build that still has the deferred `NotImplementedException`
- **THEN** the probe fails (the raw `Stfld` throws the tagged "array-element field write is deferred"
  NIE on the first element write)

#### Scenario: No regression to the typed arms or the sibling raw-owner arms
- **WHEN** the array-element branch is added to the raw `Stfld` handler
- **THEN** the full NeoStep smoke continues to pass with zero failures (baseline 352/0/0), proving the
  ILType-declaring-type typed arms (`Ldfld_*`/`Stfld_*`/`ldfld.value`/`stfld.value`) and the CLR
  ref-type / CLR value-type frame-byref / IL-instance-CLR-base raw-owner arms are unaffected
