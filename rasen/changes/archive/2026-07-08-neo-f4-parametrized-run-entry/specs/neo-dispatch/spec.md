## ADDED Requirements

### Requirement: Neo host re-entry invocation marshals instance and params

Under Neo mode (`ENABLE_NEO_MODE`), the `ILIntepreter.Run(ILMethod method, object instance, object[] p)` entry (reached via `AppDomain.Invoke`) SHALL marshal `instance` and `p` into the Neo callee frame so the invoked IL method runs with its `this` and parameters.
For an instance method (`method.HasThis`), the entry SHALL unwrap a
`CrossBindingAdaptorType` receiver to its `ILInstance` (matching the Legacy `Run`
arm) and write `instance` into the callee slot-0 (`this`) before the parameter
loop. The entry SHALL write each `p` element into the callee param region per its
`CompiledFrame.ParamInfos` layout using the same per-param marshal used by the
Step-19 delegate callback (reference param -> mStack index; primitive -> direct
typed write; CLR value type -> `WriteNeoValueType`). The entry SHALL reserve the
callee frame reference region (`CompiledFrame.TotalRefSize` mStack slots) before
marshalling, SHALL zero the locals primitive region, and SHALL ref-init unassigned
local reference slots, so the frame matches what `ExecuteNeo` expects. The Legacy
`Run` arm (which already marshals `instance` + `p` via `PushObject` /
`PushParameters` and `ExecuteR`) SHALL remain byte-identical under
`!ENABLE_NEO_MODE`. A null `instance` for an instance method SHALL throw a clear
`NullReferenceException`, matching the Legacy arm.

#### Scenario: IL instance method invoked from the host runs with the right this

- **WHEN** the host resolves an IL instance method (for example the
  `Message`-style override of a caught IL exception type) and invokes it via
  `AppDomain.Invoke(instanceMethod, caughtException)` under Neo mode
- **THEN** `Run` SHALL write `caughtException` into the callee slot-0 `this`,
  `ExecuteNeo` SHALL execute the override with that `this`, and the host SHALL
  observe the override's behavior (the field read off the caught instance), NOT a
  `NullReferenceException` from a missing `this`

#### Scenario: IL method with parameters invoked from the host

- **WHEN** the host invokes an IL method (static or instance) with a non-empty
  `object[] p` via `AppDomain.Invoke` under Neo mode
- **THEN** `Run` SHALL write each element of `p` into the callee param region per
  its `ParamInfos` layout, so the method body reads the host-supplied argument
  values (reference args via mStack index, primitives by direct typed write, CLR
  value types via `WriteNeoValueType`), matching the delegate-callback arg
  marshal

#### Scenario: Null instance for an instance method

- **WHEN** `Run` is called with `method.HasThis == true` and `instance == null`
  (after `CrossBindingAdaptorType` unwrapping) under Neo mode
- **THEN** `Run` SHALL throw a `NullReferenceException` (matching the Legacy arm
  at `ILIntepreter.cs:143-144`) and SHALL NOT proceed to `ExecuteNeo` with an
  uninitialized slot-0

#### Scenario: Legacy Run arm unchanged

- **WHEN** `Run` is called under `!ENABLE_NEO_MODE`
- **THEN** the Legacy arm SHALL push `instance` via `PushObject`, push `p` via
  `PushParameters`, and execute via `ExecuteR`/`Execute` exactly as before, and
  the Neo parametrized extension SHALL NOT alter the Legacy code path

### Requirement: Neo host re-entry returns reference-type results correctly

`ILIntepreter.Run`, under Neo mode, SHALL read the return value of a `Run`-driven
invocation with type discrimination: for a non-value-type non-void return, it
SHALL read the mStack reference index stored at the return slot and return the
boxed object (`mStack[retIdx]`, or null for the Neo null sentinel `retIdx == -1`);
for a value-type non-void return, it SHALL box the primitive bytes via
`NeoBoxReturnValue`; for a void return, it SHALL return null. The entry SHALL NOT
route a reference-type return through `NeoBoxReturnValue` (which handles
primitives only and would reinterpret the mStack index as raw bytes). The Legacy
`Run` arm return read (`StackObject.ToObject` on the return slot) SHALL remain
byte-identical under `!ENABLE_NEO_MODE`.

#### Scenario: Reference-type return from a host invocation

- **WHEN** the host invokes a parameterless IL method whose return type is a
  reference type (for example `static string EchoRef()` returning a literal
  string) via `AppDomain.Invoke` under Neo mode
- **THEN** `Run` SHALL read the return slot as an mStack reference index and
  return the boxed string, NOT the raw integer reinterpretation of that index

#### Scenario: Value-type return from a host invocation

- **WHEN** the host invokes an IL method whose return type is a primitive value
  type (for example `int`) via `AppDomain.Invoke` under Neo mode
- **THEN** `Run` SHALL box the return bytes via `NeoBoxReturnValue` and return
  the boxed primitive, exactly as the parameterless Step-6 shim did (the
  value-type path is unchanged)

#### Scenario: Void return from a host invocation

- **WHEN** the host invokes an IL method with a void return type via
  `AppDomain.Invoke` under Neo mode
- **THEN** `Run` SHALL return null and SHALL NOT read the uninitialized return
  slot

#### Scenario: Legacy return read unchanged

- **WHEN** `Run` is called under `!ENABLE_NEO_MODE`
- **THEN** the Legacy arm SHALL read the return via
  `method.ReturnType.TypeForCLR.CheckCLRTypes(StackObject.ToObject(...))` exactly
  as before, and the Neo reference-return branch SHALL NOT alter the Legacy code
  path
