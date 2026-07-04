# neo-arrays (delta)

## MODIFIED Requirements

### Requirement: Ldelema produces an element-address Ref Slot

`OpCodeREnum.Ldelema` SHALL be implemented in `ExecuteNeo` (this capability
supersedes the prior "Ldelema is out of scope" requirement). The arm SHALL
produce a Ref Slot `(arrayMStackIndex, elementByteOffset)` where
`elementByteOffset` is computed at runtime from the element index and the
array's element layout, resolved from the array's CLR type (mirroring the
existing `Ldelem`/`Stelem` array-kind resolution). `Ldelema`'s result SHALL be
consumed only by the `neo-byref` store/load-indirect opcodes
(`stind_*`/`ldind_*`/`stobj`/`ldobj`); the result is unexercisable without
those consumers, which this step (the `neo-byref` capability) provides.

#### Scenario: ldelema address consumed by stind then ldind
- WHEN `ldelema arr, i` produces a Ref Slot consumed by `stind_i4` and later by
  `ldind_i4`
- THEN the stored value SHALL be observable through the subsequent load, and
  through a direct `Ldelem_I4` of the same element.

#### Scenario: ldelema no longer reports unimplemented
- WHEN `OpCodeREnum.Ldelema` is reached in `ExecuteNeo`
- THEN it executes (producing a Ref Slot) instead of throwing a Step-tagged
  `NotImplementedException`.
