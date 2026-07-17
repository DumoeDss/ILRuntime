## ADDED Requirements

### Requirement: IL-instance owner with a CLR-base-declared instance field (raw Stfld and Ldfld)

When the Neo JIT's typed field-splitter lowers a CIL `stfld`/`ldfld` whose field is declared on a
CLR type, it leaves the raw `OpCodeREnum.Stfld`/`Ldfld` opcode in place with
`OperandLong = (typeHash << 32) | fieldHash` (identical to Legacy's raw encoding; the field's
declaring type is a CLRType, so the typed splitter -- which fires only for an ILType declaring
type -- does not apply). A field declared on a CLR base type may be accessed through an owner that
is an `ILTypeInstance` (an IL type that inherits the CLR base via a `CrossBindingAdaptor`) or its
`CrossBindingAdaptorType` wrapper. The Neo interpreter SHALL execute these raw opcodes for that
hybrid owner -- they MUST NOT throw the Step-tagged "IL-instance owner with a CLR-base field is
deferred" `NotImplementedException`.

A CLR-base field does NOT live in the IL instance's `Primitives`/`ManagedObjects` IL-field layout.
The interpreter SHALL resolve the owner's `ILTypeInstance` (unwrapping a `CrossBindingAdaptorType`
to its `.ILInstance` when the owner slot holds the wrapper) and read or write the field on the
instance's `CLRInstance` -- the wrapped CLR object created by
`CrossBindingAdaptor.CreateCLRInstance`, which IS-A the CLR base type and therefore carries the
CLR base's instance fields as real CLR fields. The read/write SHALL go through the field-hash
reflection accessor (`CLRType.GetFieldValue` / `SetFieldValue`, walked up the base-type chain),
byte-identical to Legacy's `ILTypeInstance.AssignFromStack` / read-indexer CLR-inherited `else`
branch. The existing typed field arms and the CLR ref-type / CLR value-type raw-owner arms SHALL
be unaffected.

#### Scenario: Read a CLR-base field through an IL-instance owner (Ldfld)
- **WHEN** IL code executes `ldfld` on a field declared on a CLR base type, and the owner slot
  holds an `ILTypeInstance` (or its `CrossBindingAdaptorType` wrapper) whose IL type inherits that
  CLR base
- **THEN** the Neo interpreter unwraps to the `ILTypeInstance`, reads the field off its
  `CLRInstance` via `CLRType.GetFieldValue`, and places the value in the destination register,
  without throwing `NotImplementedException`

#### Scenario: Write a CLR-base field through an IL-instance owner (Stfld)
- **WHEN** IL code executes `stfld` on a field declared on a CLR base type, and the owner slot
  holds an `ILTypeInstance` (or its `CrossBindingAdaptorType` wrapper) whose IL type inherits that
  CLR base
- **THEN** the Neo interpreter unwraps to the `ILTypeInstance`, writes the source value to the
  field on its `CLRInstance` via `CLRType.SetFieldValue`, without throwing
  `NotImplementedException`, and without mutating the `ILTypeInstance` itself

#### Scenario: Unwrap both owner representations
- **WHEN** the owner slot holds a `CrossBindingAdaptorType` (the Adaptor wrapper) rather than the
  `ILTypeInstance` directly
- **THEN** the handler resolves `.ILInstance` first and then uses `.CLRInstance`, behaving
  identically to the direct-`ILTypeInstance` case

#### Scenario: Regression probe faults without the fix
- **WHEN** a NeoStep probe that constructs an IL type inheriting a CLR base and reads/writes the
  base's instance field is run against a build that still has the deferred `NotImplementedException`
- **THEN** the probe fails (the raw opcode throws the tagged
  "IL-instance owner with a CLR-base field is deferred" NIE)

#### Scenario: No regression to the typed arms or the sibling raw-owner arms
- **WHEN** the IL-instance-owner branch is added to the raw `Stfld`/`Ldfld` handlers
- **THEN** the full NeoStep smoke continues to pass with zero failures (baseline 326/0/0),
  proving the ILType-declaring-type typed arms (`Ldfld_*`/`Stfld_*`/`ldfld.value`/`stfld.value`)
  and the CLR ref-type / CLR value-type raw-owner arms are unaffected
