# neo-dispatch

Neo mode method dispatch over the class VTable (`neoVTable`): class virtual
dispatch (Step 10) and interface dispatch (Step 11), both resolved against the
same class VTable with one shared slot-key scheme.

## Requirements

### Requirement: Neo IL-delegate to CLR-delegate conversion at the CLR boundary

In Neo mode, whenever an IL delegate (represented as a `MethodDelegateAdapter` /
`FunctionDelegateAdapter`, both `ILTypeInstance` subclasses) crosses the IL->CLR
boundary -- storing into a CLR delegate-typed static field, or passing as a
delegate-typed argument to a CLR method through an autogen Neo CLR binding -- the
adapter SHALL be converted to the real CLR delegate (`Action` / `Func` / a custom
delegate type) via `Type.CheckCLRTypes(value)` (which routes through
`IDelegateAdapter.GetConvertor` -> `DelegateManager.ConvertToDelegate`). This
SHALL mirror the Legacy behavior (`ILIntepreter.Register.cs` Stsfld applies
`f.FieldType.CheckCLRTypes`, and the Legacy autogen setters/bindings emit
`typeof(T).CheckCLRTypes(..., IsDelegate)`). A raw adapter SHALL NOT reach a CLR
`(ConcreteDelegate)` cast. Neo's reflection fallback (`CLRMethod.Invoke`) already
applies this conversion; the autogen `RedirectionNeo` path and the Stsfld CLR
field store SHALL apply it too.

#### Scenario: Storing an IL delegate into a CLR delegate static field

- **WHEN** Neo mode executes `Stsfld` on a CLR static field whose declared type
  is a delegate, and the source value is a `MethodDelegateAdapter` /
  `FunctionDelegateAdapter`
- **THEN** the Neo Stsfld CLR-static-field arm SHALL convert the value via
  `fieldType.CheckCLRTypes(value)` before calling `CLRType.SetStaticFieldValue`,
  so the autogen setter receives a real CLR delegate and the `(T)v` cast succeeds

#### Scenario: Passing an IL delegate as a CLR method argument (autogen binding)

- **WHEN** Neo mode calls a CLR method (through an autogen `RedirectionNeo`
  binding) whose parameter type is a delegate, and the argument value is a
  `MethodDelegateAdapter` / `FunctionDelegateAdapter`
- **THEN** the autogen Neo binding SHALL read the argument via
  `typeof(T).CheckCLRTypes(ILIntepreter.ReadNeoReference(...), IsDelegate)` (the
  form the binding generator already emits for delegate params), so the raw cast
  `(T)ReadNeoReference(...)` receives a real CLR delegate

#### Scenario: Matching-type and non-delegate fields/params unaffected

- **WHEN** the value is already a real CLR delegate, or the field/parameter type
  is not a delegate
- **THEN** `CheckCLRTypes` SHALL pass the value through unchanged (`obj is
  Delegate` short-circuit; the non-delegate branch is untouched), preserving
  byte-for-byte behavior for every non-delegate reference static field and
  non-delegate binding parameter
