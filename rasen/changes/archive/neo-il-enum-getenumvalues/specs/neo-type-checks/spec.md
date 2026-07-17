## ADDED Requirements

### Requirement: IL-enum System.Type wrapper answers enum reflection virtuals

ILRuntime's IL-enum `System.Type` wrapper (`ILRuntimeType`) SHALL override the
`System.Type` enum-reflection virtuals so that the framework `System.Enum.GetValues`,
`System.Enum.GetNames`, and `System.Enum.GetUnderlyingType` -- and any other
reflection-based enum query that delegates to those virtuals -- work on an IL-defined
enum surfaced as `System.Type` (the object an IL `typeof(EnumT)` evaluates to, and the
argument the autogen `System_Enum_Binding` redirects forward). Concretely:

- `IsEnum` SHALL return `true` for an IL-defined enum and `false` otherwise.
- `GetEnumUnderlyingType()` SHALL return the enum's underlying primitive
  `System.Type` (the CLR type of the enum's `value__` field, i.e. `ILType.enumType`
  surfaced via `ILType.TypeForCLR`).
- `GetEnumValues()` SHALL return a one-dimensional, zero-based `Array` whose element
  type is the underlying type and whose elements are the enum's named members'
  constant values, ordered by UNSIGNED binary value (parity with
  `System.Enum.GetValues`/`GetEnumNames`, which sort by unsigned binary value, NOT
  declaration order -- a negative member sorts LAST). Each element SHALL be the
  underlying-type value of the corresponding Cecil `IsLiteral`/`HasConstant` field.
- `GetEnumNames()` SHALL return the enum's member names ordered by unsigned binary
  value (parity with the framework).

These overrides live in shared (non-`ENABLE_NEO_MODE`) reflection code and are purely
additive: a call that previously fell through to the base `System.Type` virtual (which
throws a bare `NotImplementedException`) now returns data. An `ILRuntimeType` for a
non-enum IL type SHALL be unaffected (`IsEnum` `false`; the `GetEnum*` methods are
never invoked on the happy path and throw `ArgumentException` if misused, matching the
base `System.Type` contract).

#### Scenario: Enum.GetValues returns an IL enum's member values
- **WHEN** an IL method under `ENABLE_NEO_MODE` evaluates
  `System.Enum.GetValues(typeof(IlEnumT))` where `IlEnumT` is an IL-defined enum with
  members `A = 0, B = 10, C = 20`
- **THEN** the call returns without throwing (on HEAD it throws the bare
  `NotImplementedException` from `System.Type.GetEnumValues()`), the returned array
  has length `3`, its element type is the underlying type (`int`), and
  `(int)result.GetValue(0) == 0`, `(int)result.GetValue(1) == 10`,
  `(int)result.GetValue(2) == 20`.

#### Scenario: Enum.GetNames returns an IL enum's member names
- **WHEN** an IL method under `ENABLE_NEO_MODE` evaluates
  `System.Enum.GetNames(typeof(IlEnumT))` for the same enum
- **THEN** the call returns without throwing, the result has length `3`, and
  `result[0] == "A"`, `result[1] == "B"`, `result[2] == "C"`.

#### Scenario: Enum.GetUnderlyingType returns the IL enum's underlying primitive
- **WHEN** an IL method under `ENABLE_NEO_MODE` evaluates
  `System.Enum.GetUnderlyingType(typeof(IlEnumT))` for an enum whose `value__` is
  `int`
- **THEN** the call returns without throwing and the result equals `typeof(int)`.

#### Scenario: IsEnum is correct for an IL enum and a non-enum IL type
- **WHEN** reflection queries `typeof(IlEnumT).IsEnum` and `typeof(IlClassT).IsEnum`
  for an IL-defined enum and an IL-defined class respectively
- **THEN** the former returns `true` and the latter returns `false`.

#### Scenario: No regression on existing enum/type-check usage
- **WHEN** the existing NeoStep smoke suite is run after this change
- **THEN** every previously-green case remains green (the change is additive shared
  reflection code; no `ExecuteNeo` arm, JIT pass, object-model, or binding behavior
  changes).
