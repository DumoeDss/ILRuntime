# neo-dispatch

(add this ADDED requirement to the existing `openspec/specs/neo-dispatch/spec.md`)

### Requirement: Neo redirect for System.Activator.CreateInstance on IL types

Under Neo mode (`ENABLE_NEO_MODE`), the three `System.Activator.CreateInstance`
overloads (the generic `CreateInstance<T>()`, `CreateInstance(Type)`, and
`CreateInstance(Type, object[])`) SHALL each have a hand-written Neo redirect
registered on `RedirectMapNeo` in the `AppDomain` ctor, mirroring the
`InitializeArrayNeo` / `DelegateCombineNeo` precedent. The Neo redirects SHALL
reproduce the hand-written Legacy redirects' IL-type-vs-CLR-type
discrimination: for an IL type (read from `method.GenericArguments[0]` for the
generic overload, or from the `Type` param cast to `ILRuntimeType` for the
Type overloads) the redirect SHALL construct the instance via
`ILType.Instantiate()` (or `ILType.Instantiate(object[])` for the two-arg
overload), NOT via the host `System.Activator` (which cannot construct an IL
type and throws `MissingMethodException` on `ILTypeInstance`). The redirects
SHALL override (first-registered-wins for the non-generic overloads; generic-
definition-precedence via `TryGetRedirection` for the generic overload) the
autogen non-functional `*Neo` stubs by registering in the `AppDomain` ctor
(which runs before the test-harness binding initializer). The created instance
SHALL be written to the caller's dest ref slot via the Neo object-return
convention (non-null -> store at `retRefBase` and write the index to `retDst`;
null -> write the Neo null sentinel `-1` to `retDst`). The hand-written Legacy
redirects SHALL remain on `RedirectMap` and SHALL be byte-identical under
`!ENABLE_NEO_MODE`.

#### Scenario: Generic Activator.CreateInstance on an IL reference type

- **WHEN** Neo mode executes `Activator.CreateInstance<AnILRefType>()` (IL
  reference type as the generic argument)
- **THEN** the Neo `CreateInstanceNeo` redirect SHALL read
  `method.GenericArguments[0]` as the IL type, call `ILType.Instantiate()`, and
  write the resulting instance to the dest ref slot, and SHALL NOT call the
  host `System.Activator.CreateInstance<ILTypeInstance>()` (which would throw
  `MissingMethodException`)

#### Scenario: Activator.CreateInstance(Type) on an IL type

- **WHEN** Neo mode executes `Activator.CreateInstance(typeof(AnILType))`
- **THEN** the Neo `CreateInstance2Neo` redirect SHALL read the `Type` param
  via `ReadNeoReference`, recognize it as an `ILRuntimeType`, call
  `ILRuntimeType.ILType.Instantiate()`, and write the instance to the dest ref
  slot; for a null `Type` it SHALL write the Neo null sentinel (`-1`) to the
  dest

#### Scenario: Activator.CreateInstance(Type, object[]) on an IL type

- **WHEN** Neo mode executes
  `Activator.CreateInstance(typeof(AnILType), arg1, arg2, ...)` (the params
  overload, lowered to `CreateInstance(Type, object[])`)
- **THEN** the Neo `CreateInstance3Neo` redirect SHALL read the `Type` and the
  `object[]` params via `ReadNeoReference`, null-check each element (throwing
  `ArgumentNullException` on a null element, matching the Legacy redirect),
  call `ILRuntimeType.ILType.Instantiate(object[])` with the args, and write
  the instance to the dest ref slot so the constructed instance's fields
  reflect the supplied constructor arguments

#### Scenario: Hand-written Neo redirect overrides the autogen stub

- **WHEN** the Neo redirects are registered for the three
  `System.Activator.CreateInstance` overloads in the `AppDomain` ctor
- **THEN** the custom Neo redirects SHALL override the autogen
  `System_Activator_Binding.CreateInstance_*_Neo` stubs (first-registered-wins
  for the non-generic overloads; generic-definition precedence via
  `TryGetRedirection` for the generic overload), so the `ILType.Instantiate`
  path is the active implementation and the host-`Activator`-calling autogen
  stubs are NOT invoked

#### Scenario: Legacy Activator redirects unchanged

- **WHEN** the runtime operates under `!ENABLE_NEO_MODE`
- **THEN** the hand-written Legacy redirects (`CLRRedirections.CreateInstance`
  / `CreateInstance2` / `CreateInstance3`) SHALL remain registered on
  `RedirectMap` and behave exactly as before, and the Neo redirects / Neo
  registration SHALL NOT be compiled
