## ADDED Requirements

### Requirement: ldobj / stobj of a CLR value-type array element through a ldelema byref

When IL code performs a value-type-sized copy to/from a CLR value-type ARRAY ELEMENT via the pointer
opcodes -- the CIL shape `ldelema <ElementType>; ldobj <ElementType>` (load the whole struct element
into a temp) and `ldelema <ElementType>; <...>; stobj <ElementType>` (store a struct result back to the
element address), as produced by a compound read-modify-write such as `arr[i] += <struct>` -- the Neo
`Ldobj` and `Stobj` arms (in `ExecuteNeo`) SHALL handle an `Array` owner. They MUST NOT throw the
"Step 17/13b: field/element access on a CLR object via the IL-instance path is deferred" tagged
`NotImplementedException` reached today via `GetNeoILInstance` when the byref owner resolves to an
`Array`.

The `ldelema` producer encodes such a byref as the 8-byte Ref Slot `(arrIdx, elementIdx)` where `arrIdx`
is the mStack index of the `System.Array` and `elementIdx` IS THE ELEMENT INDEX (NOT a byte offset, NOT
a field hash) -- the same convention the `stind_*`/`ldind_*` arms, the raw `Stfld`/`Ldfld` array-element
arms (children 19/24), and the byref-param marshal (child-25) consume.

For `Ldobj` (READ), the arm SHALL decode `(arrIdx, elementIdx)` from the byref at `ip->SrcOffset` (the
source address), read the element via `((Array)mStack[arrIdx]).GetValue(elementIdx)` (which boxes a
value-type element), and flatten the boxed value into the dest at `frameBase + ip->DstOffset` via
`WriteNeoValueType` (or `Unsafe.InitBlock` to zero on a null element). For `Stobj` (WRITE-back), the
arm SHALL decode `(arrIdx, elementIdx)` from the byref at `ip->DstOffset` (the dest address), box the
src flat bytes at `frameBase + ip->SrcOffset` into the element type via `ReadNeoValueType`, and store
the value back via `((Array)mStack[arrIdx]).SetValue(boxed, elementIdx)` -- the symmetric WRITE of
child-19's raw-`Stfld` array-element arm and child-25's write-back arm.

The runtime SHALL detect the `Array` owner by a content check `mStack[objIdx] is Array` -- NO JIT
marker is required. This is sound because `ldobj`/`stobj`'s pointer operand is ALWAYS a byref (CIL
`ldobj`/`stobj` copy a value type to/from an ADDRESS, never a direct value); the byref's `objIdx` half
is always a genuine mStack index set by a byref producer (or `-1` for a frame-native byref, which is
dispatched by the preceding `objIdx == -1` branch). This differs from the raw-`Ldfld` array-element
case (child-24), where the owner register could hold flat bytes whose first int was a struct field
value and a JIT marker was required to disambiguate.

The existing branches in each arm -- the frame-native byref path (`objIdx == -1`, with the Step-17b
ref-region copy), the CLR-object-field path (`NeoIsClrObject`, Area 4d), and the IL-instance path
(`GetNeoILInstance`, incl. the F-10 boxed-CLR-struct-field sub-case) -- SHALL be unaffected. The
`primSize` computed for a CLR struct (`ilType == null` -> `AppDomain.GetPrimitiveSize(t)` ->
`Unsafe.SizeOf<T>`) is correct for the element's managed size.

#### Scenario: compound read-modify-write on a struct array element (ldobj read + stobj write-back)
- **WHEN** IL code executes `arr[0] += TestVector3.One` on a `TestVector3[]` (a blittable CLR struct, 3
  floats, with `operator +` and a static `One = (1,1,1)`), which lowers to `ldelema; ldobj;
  op_Addition; stobj`
- **THEN** the `ldobj` SHALL load the whole `arr[0]` element via `Array.GetValue(0)` +
  `WriteNeoValueType` into the temp, `op_Addition` SHALL compute the sum, and the `stobj` SHALL store
  the result back via `ReadNeoValueType` + `Array.SetValue(result, 0)` -- so a subsequent read of
  `arr[0]` reflects the incremented value (e.g. the element-wise sum increases by exactly `X+Y+Z` of
  `One`), NOT a `NotImplementedException`

#### Scenario: whole-struct element read at a chosen index (element-index decode)
- **WHEN** IL code executes `TestVector3 v = arr[i];` on a `TestVector3[]` (lowered to `ldelema;
  ldobj`), reading distinct indices `i0` and `i1` into separate locals
- **THEN** each `ldobj` SHALL read the CORRECT element (the `elementIdx` half of the byref, not the
  `arrIdx` half, addresses the element), so the two locals hold the two distinct host-written element
  values, and a HOST CLR helper summing their fields returns the exact combined sum -- a wrong element-
  index decode yields a different sum

#### Scenario: read-back correctness (exact field values, not just non-throwing)
- **WHEN** a NeoStep probe loads a struct array element via `ldobj` and asserts the element's field
  values via a HOST CLR helper (CLR-side float arithmetic, sidestepping the interpreter's pre-existing
  `conv.i4`-float-bit-reinterpret bug)
- **THEN** the assertion SHALL compare against the EXACT expected field-value sum (hand-checked
  against the inputs), failing via a deliberate `1/0` (DivideByZero) on any mismatch -- proving the
  flat-byte copy is field-faithful, not merely non-throwing

#### Scenario: regression probe faults without the fix
- **WHEN** the NeoStep probe is run against a build WITHOUT the `Array` branch in the `Ldobj` arm (and
  the `Stobj` arm)
- **THEN** the probe fails with the "Step 17/13b: field/element access on a CLR object via the IL-
  instance path is deferred ... Owner type: <ElementType>[]" `NotImplementedException` (the `ldobj`
  read NIE on HEAD)

#### Scenario: No regression to the sibling value-type-copy branches
- **WHEN** the `Array` branch is added to the `Ldobj` and `Stobj` arms
- **THEN** the full NeoStep smoke continues to pass with zero failures (baseline 371/0), proving the
  frame-native byref path (`objIdx == -1`, incl. the Step-17b ref-region copy for VT-with-ref-fields),
  the CLR-object-field path (`NeoIsClrObject`, Area 4d), the IL-instance path (`GetNeoILInstance`, incl.
  F-10), and the sibling array-element opcodes (raw `Stfld`/`Ldfld` children 19/24, `stind`/`ldind`
  child-15, byref-param marshal child-25) are unaffected
