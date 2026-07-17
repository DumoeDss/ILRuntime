## ADDED Requirements

### Requirement: Byref array-element param marshal through the shared field marshal

When a `ref`/`out` IL parameter whose referent is a CLR-array ELEMENT is passed to a CLR-method call
(the CIL shape `ldelema <ElementType>; call <method>(..., <ElementType>&, ...)`), the Neo call-arg
marshal (`CopyNeoCallArguments`, forward) and the post-call write-back (`CopyNeoCallThisBack`, reverse)
BOTH route the byref to the shared `NeoMarshalByrefFieldToSlot` helper. That helper's `target is Array`
branch SHALL marshal the element to/from the callee slot -- it MUST NOT throw a "CLR-array-element byref
param is not handled" `NotImplementedException`.

The `ldelema` producer encodes such a byref as the 8-byte Ref Slot `(arrIdx, elementIdx)` where `arrIdx`
is the mStack index of the `System.Array` and `elementIdx` (the helper's `off` parameter) IS THE ELEMENT
INDEX (NOT a byte offset, NOT a field hash) -- the same convention the `stind_*`/`ldind_*` arms, the raw
`Stfld`/`Ldfld` array-element arms, and Legacy's `ObjectTypes.ArrayReference` writeback consume. A
reference-type-element array byref is UNREACHABLE here (`ldelema` rejects a non-value-type element), so
the branch only ever sees a value-type element (primitive / enum / struct).

For the FORWARD deref (`isWrite == false`), the helper SHALL read the element via
`Array.GetValue(elementIdx)` (which boxes a value-type element) and flatten the boxed value into the
callee slot by element category (primitive/enum/struct -> `WriteNeoValueType`; reference -> mStack index
push) -- identical to the existing CLR-object-field branch, with `Array.GetValue` in place of the field-
hash accessor. For the WRITE-BACK (`isWrite == true`), the helper SHALL box the callee slot's flat bytes
by the element type via `ReadNeoValueType` (reference -> read the mStack index), then write the value
back via `Array.SetValue(value, elementIdx)` -- the symmetric WRITE of the raw-`Stfld` array-element arm.
When the element type was not propagated by the call signature (`elemType == null`), the helper SHALL
recover it from `array.GetType().GetElementType()`.

The element type and slot size arrive via the call map's `PrimitiveByRefElemType`/`PrimitiveSize`
(Step-13 Area-4c), already correct for a value-type element (the same plumbing the frame-native and
CLR-object-field byref branches consume). NO JIT, optimizer, object-model, or CLR-binding change is
required. The existing `ILTypeInstance` branch, the CLR-object-field branch, and the frame-native byref
path SHALL be unaffected.

#### Scenario: ref primitive-element array param mutates end-to-end (forward + write-back)
- **WHEN** IL code executes `call <m>(ref byteArr[i])` where `byteArr` is a `byte[]` and `<m>` is a CLR
  method `static void M(ref byte b) { b = (byte)(b + 5); }`, and the Neo call routes through the
  reflection fallback (`CopyNeoCallArguments` / `CopyNeoCallThisBack`)
- **THEN** the forward deref SHALL materialize `byteArr[i]` into the callee slot (1 byte) via
  `Array.GetValue(i)` + `WriteNeoValueType`, the CLR callee SHALL mutate it, and the write-back SHALL
  store the mutated byte back via `ReadNeoValueType` + `Array.SetValue(mutated, i)` -- so a subsequent
  `ldelem.u1 byteArr[i]` reads the mutated value (e.g. `byteArr[1] == 25` after `M(ref byteArr[1])` on a
  `20`-initialized element)

#### Scenario: ref struct-element array param mutates end-to-end (box/mutate/unbox)
- **WHEN** IL code executes `call <m>(ref vectorArr[i])` where `vectorArr` is a `TestVector3[]` (a
  blittable CLR struct, 3 floats, no reference fields) and `<m>` is a CLR method that mutates the struct
  fields by reference
- **THEN** the forward deref SHALL box the element via `Array.GetValue(i)` and flatten the 12 struct
  bytes into the callee slot, the CLR callee SHALL mutate the boxed struct, and the write-back SHALL
  re-box the callee slot via `ReadNeoValueType(typeof(TestVector3), ...)` and store it back via
  `Array.SetValue` -- so a SECOND call on the same element observes the mutated value, proving the write-
  back persisted into the array element (not just a non-throwing call)

#### Scenario: the byref param reaches the helper for BOTH forward and write-back
- **WHEN** a `ref`/`out` array-element param is marshalled through a CLR call
- **THEN** `NeoMarshalByrefFieldToSlot` SHALL be invoked with `isWrite == false` (forward,
  `CopyNeoCallArguments`) AND with `isWrite == true` (write-back, `CopyNeoCallThisBack`) for the SAME
  byref slot, and the `Array` branch SHALL handle BOTH invocations -- a single branch covers both
  directions (they share this helper)

#### Scenario: element index decoded correctly across multiple indices
- **WHEN** `ref arr[i]` is passed at two distinct element indices `i0` and `i1` in the same array
- **THEN** each call SHALL mutate the correct element (the `elementIdx` half of the byref, not the
  `arrIdx` half, addresses the element), so `arr[i0]` and `arr[i1]` hold their respective mutations

#### Scenario: regression probe faults without the fix
- **WHEN** a NeoStep probe that allocates a CLR array, passes `ref arr[i]` to a CLR method that reads
  and writes the element, and asserts the persisted mutation is run against a build WITHOUT the `Array`
  branch in `NeoMarshalByrefFieldToSlot`
- **THEN** the probe fails with the "Step 13 Area 4c: a CLR-array-element byref param is not handled"
  `NotImplementedException` (forward deref), OR -- if only the forward were fixed -- a wrong/lost value
  on the write-back

#### Scenario: No regression to the sibling byref branches
- **WHEN** the `Array` branch is added to `NeoMarshalByrefFieldToSlot`
- **THEN** the full NeoStep smoke continues to pass with zero failures (baseline 368/0), proving the
  `ILTypeInstance` branch (IL heap field + the F-10 boxed-CLR-struct-field sub-case), the CLR-object-
  field branch (the field-hash accessor + child-9 IL-CLR-base), and the frame-native byref path
  (`objIdx == -1`) are unaffected
