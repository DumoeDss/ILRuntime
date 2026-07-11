## ADDED Requirements

### Requirement: CLR-declaring-type instance field access via raw Stfld and Ldfld

When the Neo JIT's typed field-splitter lowers a CIL `stfld`/`ldfld` whose field is
declared on a CLR type (not an ILType), it intentionally leaves the raw
`OpCodeREnum.Stfld`/`Ldfld` opcode in place with
`OperandLong = (typeHash << 32) | fieldHash` (identical to Legacy's raw encoding). The Neo
interpreter SHALL execute these raw opcodes — they MUST NOT reach the Step-6
`NotImplementedException` default. The handler decodes `(typeHash, fieldHash)` from
`OperandLong`, resolves the declaring `CLRType` and the `FieldInfo`, resolves the owner
object from the owner register slot, and reads or writes the field. The offset-lowering
pass SHALL lower the raw `Ldfld`/`Stfld` `Register1`/`Register2` into `DstOffset`/
`SrcOffset` (same shape as the typed arms) so the handler has valid operand offsets.

The handler SHALL discriminate the owner shape and cover at least: (a) a boxed CLR
reference-type object whose `mStack` index is in the owner slot; (b) a CLR value-type
local read by value (`ldfld`) — owner slot holds the struct's flat bytes; (c) a CLR
value-type local written via its address (`stfld`) — owner slot holds a frame-native byref
to the struct's flat bytes. Each unhandled owner shape SHALL fail with a Step-tagged
`NotImplementedException` (fail loud) rather than silently corrupt execution.

#### Scenario: Read a CLR reference-type instance field (Ldfld)
- **WHEN** IL code executes `ldfld` on a field declared on a CLR reference type, with the
  owner object boxed in `mStack`
- **THEN** the Neo interpreter reads the field via the CLR reflection accessor
  (`CLRType.GetFieldValue`) and places the value in the destination register, without
  throwing `NotImplementedException`

#### Scenario: Write a CLR reference-type instance field (Stfld)
- **WHEN** IL code executes `stfld` on a field declared on a CLR reference type, with the
  owner object boxed in `mStack`
- **THEN** the Neo interpreter writes the source value to the field via the CLR reflection
  accessor (`CLRType.SetFieldValue`), without throwing `NotImplementedException`

#### Scenario: Read a CLR value-type struct field by value (Ldfld)
- **WHEN** IL code executes `ldfld` on a primitive field declared on a CLR value type,
  where the owner is a local struct loaded by value (flat bytes in the owner register slot)
- **THEN** the Neo interpreter reads the primitive at the field's native offset within the
  struct's flat bytes and places it in the destination register, without throwing
  `NotImplementedException`

#### Scenario: Write a CLR value-type struct field via its address (Stfld)
- **WHEN** IL code executes `stfld` on a primitive field declared on a CLR value type,
  where the owner is the struct's frame-native byref (produced by `ldloca`/`ldflda`)
- **THEN** the Neo interpreter writes the source primitive at the field's native offset
  within the struct's flat bytes, without throwing `NotImplementedException`

#### Scenario: Regression probe faults without the fix
- **WHEN** a NeoStep probe exercising a CLR-declaring-type field read and write is run
  against a build that lacks the raw `Stfld`/`Ldfld` handler
- **THEN** the probe fails (the raw opcode reaches the Step-6
  `NotImplementedException`)

#### Scenario: No regression to the typed field arms
- **WHEN** the raw `Stfld`/`Ldfld` handler and the offset-lowering entry are added
- **THEN** the full NeoStep smoke continues to pass with zero failures (baseline 314/0/0),
  proving the ILType-declaring-type typed arms (`Ldfld_*`/`Stfld_*`/`ldfld.value`/
  `stfld.value`) are unaffected
