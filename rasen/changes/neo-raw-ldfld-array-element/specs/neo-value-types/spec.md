## ADDED Requirements

### Requirement: Raw Ldfld with a CLR-struct array-element owner

When the Neo JIT's typed field-splitter lowers a CIL `ldfld` whose field is declared on a CLR type, it
leaves the raw `OpCodeREnum.Ldfld` opcode in place with `OperandLong = (typeHash << 32) | fieldHash`
(identical to Legacy's raw encoding; the field's declaring type is a CLRType, so the typed splitter --
which fires only for an ILType declaring type -- does not apply). The owner of such an `ldfld` may be a
**CLR-struct array element**: the CIL shape `x = clrStructArray[i].field;` lowers to
`ldelema <StructType>; ldfld <field>`, where `ldelema` on a CLR value-type-element array produces an
8-byte byref `(arrIdx, elementIdx)` whose `off` half is the ELEMENT INDEX (not a byte offset) -- the same
encoding the `stind_*`/`ldind_*` consumer arms, the raw `Stfld` array-element arm, and Legacy's
`ObjectTypes.ArrayReference` writeback consume. The Neo interpreter SHALL execute raw `Ldfld` for this
array-element owner and return the field value -- it MUST NOT reinterpret the byref ints as the struct's
flat managed bytes (silent corruption).

Because the Neo frame is untyped, a raw `Ldfld` value-type owner cannot be safely distinguished from a
flat-bytes local-value owner at RUNTIME alone (a flat-bytes struct's first int -- a field value -- can
coincidentally index an `Array` in `mStack`, producing a false-positive array detection and silent
corruption; this asymmetry with raw `Stfld` -- whose value-type owner is ALWAYS a byref -- is why the
`Stfld` runtime `mStack[objIdx] is Array` detection does NOT transfer verbatim to `Ldfld`). The Neo JIT
SHALL therefore mark the array-element-byref owner shape at JIT time: when the owner register of a raw
`Ldfld` (CLRType declaring owner) is the dest of an immediately-preceding `ldelema`, the JIT SHALL set a
dedicated marker bit in the instruction's spare `Operand4` field (free for the raw `Ldfld`, which
otherwise carries only `OperandLong`).

At runtime, in the raw `Ldfld` value-type-owner branch, when the marker is set the interpreter SHALL
resolve the array-element owner by reading `arrIdx = *(int*)(frameBase + ownerOff)` (the mStack index of
the `System.Array`) and `elementIdx = *(int*)(frameBase + ownerOff + 4)` (the element index), then
perform the symmetric READ of the raw `Stfld` array-element WRITE: box the element via
`Array.GetValue(elementIdx)` (a boxed copy of the struct) and reflection-read the field via the
already-resolved `FieldInfo.GetValue(boxedElem)`, then marshal the resulting field value to the dest
register by the field's CLR type category (primitive -> `NeoWritePrimitiveToFrame`, value-type ->
`WriteNeoValueType`, reference -> `mStack` push). When the marker is clear, the existing flat-bytes
local-value path (box the whole struct via `ReadNeoValueType` + `FieldInfo.GetValue`) SHALL run
unchanged. The existing typed field arms, the CLR ref-type raw-owner branch, the flat-bytes CLR value-
type raw-owner branch (child-4), and the IL-instance-CLR-base raw-owner branch (child-9) SHALL be
unaffected.

#### Scenario: Read a CLR-struct field through an array-element owner (Ldfld, value-type declaring)
- **WHEN** IL code executes `ldfld` on a field declared on a CLR value type, and the owner slot holds a
  `ldelema`-produced array-element byref `(arrIdx, elementIdx)` with `mStack[arrIdx]` a CLR-struct
  `System.Array`, and the JIT has set the array-element-byref marker on the instruction
- **THEN** the Neo interpreter boxes the element at `elementIdx` via `Array.GetValue` and reflection-reads
  the field via `FieldInfo.GetValue`, marshals the value to the dest register by field category, and
  returns the correct field value -- it does NOT reinterpret the byref ints `(arrIdx, elementIdx)` as the
  struct's flat managed bytes

#### Scenario: Element index is decoded correctly on the read across multiple indices
- **WHEN** the same field is read through array-element byrefs at several distinct element indices
- **THEN** each read returns the value of the field in the addressed element (the element index, not the
  array mStack index, is the addressing half), proving the `(arrIdx, elementIdx)` decode is correct on
  the READ side

#### Scenario: Flat-bytes local-value owner is unaffected (no false positive)
- **WHEN** IL code executes a raw `ldfld` on a CLR-struct field whose owner is a flat-bytes local value
  (loaded by value via `ldloc`/`ldsfld`, the JIT marker clear), and the struct's first int field value
  happens to fall in `mStack` range where an `Array` resides
- **THEN** the interpreter takes the flat-bytes local-value path (it does NOT treat the owner as an array
  element), because the JIT marker authoritatively distinguishes the byref owner from the flat-bytes
  owner -- no runtime `mStack[objIdx] is Array` coincidence can misroute it

#### Scenario: Regression probe faults without the fix
- **WHEN** a NeoStep probe that allocates a CLR-struct array, writes a field per element, and reads it
  back (`x = arr[i].field`) is run against a build without the array-element-byref marker + read branch
- **THEN** the probe fails (the raw `Ldfld` reinterprets the byref ints as the struct fields -> a wrong
  read value -> the probe's deliberate `1/0` DivideByZero guard fires)

#### Scenario: No regression to the typed arms or the sibling raw-owner arms
- **WHEN** the marker-gated array-element read branch is added to the raw `Ldfld` handler and the marker
  is stamped in the JIT
- **THEN** the full NeoStep smoke continues to pass with zero failures (baseline 365/0/0), proving the
  ILType-declaring-type typed arms (`Ldfld_*`/`Stfld_*`/`ldfld.value`/`stfld.value`), the flat-bytes CLR
  value-type raw-owner arm (child-4 / child-21 TC3/TC4), the CLR ref-type raw-owner arm, the IL-instance-
  CLR-base raw-owner arm (child-9), and the raw `Stfld` array-element arm (child-19) are unaffected
