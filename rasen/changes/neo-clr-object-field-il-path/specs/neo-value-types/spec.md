## ADDED Requirements

### Requirement: Typed field-access guard owner resolution

The Neo typed field/address arms (`Ldfld_*`, `Stfld_*`, `Stfld_Value`,
`Ldfld_Value`, and the `Stobj`/`Ldobj` IL-instance fallback) resolve the owner
`ILTypeInstance` through the shared `GetNeoILInstance(mStack, objIndex)` guard.
Because these typed arms are JIT-emitted ONLY for an IL-declaring field, the
owner is expected to be an `ILTypeInstance`. The guard SHALL discriminate the
non-`ILTypeInstance` owner shapes that actually reach it rather than throwing a
single "deferred" `NotImplementedException`:

- A **null** owner (an mStack slot holding null -- reachable because the Neo
  `ldsfld` IL-static-reference arm materializes a stored null as a valid mStack
  index pointing at a null entry) SHALL surface as a
  `NullReferenceException`, matching CLR semantics for `ldfld`/`stfld`/
  `ldobj`/`stobj` on a null instance. It SHALL NOT throw a
  `NotImplementedException`.
- A `CrossBindingAdaptorType` owner (an IL type that inherits a CLR base,
  round-tripped through CLR code / reflection / a generic collection so the
  slot holds the CLR adaptor wrapper) SHALL resolve to its `ILInstance`, on
  which the IL-declared field lives. A `CrossBindingAdaptorType` whose
  `ILInstance` is null SHALL surface as a `NullReferenceException`.
- Any other CLR shape SHALL keep the defensive Step-tagged
  `NotImplementedException` (the byref consumers and the raw `Ldfld`/`Stfld`
  handlers route genuine CLR objects to the field-hash accessor before reaching
  this guard, so this remains a fail-loud guard for the unexpected).

#### Scenario: Null owner at a typed ldfld throws NullReferenceException

- **WHEN** IL code performs a typed `ldfld` (e.g. `Ldfld_I4`) whose owner is a
  null IL static reference field loaded by `ldsfld` (owner register carries
  `objIndex >= 0` with `mStack[objIndex] == null`),
- **THEN** the Neo runtime SHALL throw a `NullReferenceException` (NOT a
  `NotImplementedException`), so a surrounding `catch (NullReferenceException)`
  in IL code matches and handles it.

#### Scenario: Null owner at a typed reference ldfld throws NullReferenceException

- **WHEN** IL code performs a typed `Ldfld_Ref` whose owner is a null IL static
  reference field,
- **THEN** the Neo runtime SHALL throw a `NullReferenceException`.

#### Scenario: CrossBindingAdaptor owner unwraps to its ILInstance

- **WHEN** IL code performs a typed `ldfld`/`stfld` (IL-declared field) whose
  owner register holds a `CrossBindingAdaptorType` wrapper,
- **THEN** the guard SHALL return the wrapper's `ILInstance` and the field
  access SHALL proceed against that instance's `Primitives`/`ManagedObjects`
  (byte-identical intent to the raw `Ldfld`/`Stfld` handler's adaptor unwrap).

#### Scenario: Defensive NIE retained for unexpected CLR owners

- **WHEN** the guard receives a non-null owner that is neither an
  `ILTypeInstance` nor a `CrossBindingAdaptorType`,
- **THEN** it SHALL throw the Step-tagged `NotImplementedException` (fail-loud
  guard), preserving the previous defensive behavior for genuinely unexpected
  shapes.
