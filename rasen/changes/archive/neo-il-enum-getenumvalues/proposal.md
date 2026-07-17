## Why

`System.Enum.GetValues(typeof(IlEnum))` / `GetNames` / `GetUnderlyingType` throw a
bare `NotImplementedException` ("The method or operation is not implemented.") when
the enum is an IL-defined enum surfaced as `System.Type`. The autogen Neo redirect
`System_Enum_Binding.GetValues_0_Neo` (`System_Enum_Binding.cs:72`) does NOT throw --
it reads `@enumType` and calls the framework `System.Enum.GetValues(@enumType)`. The
NIE originates DEEPER: the framework calls the `Type.GetEnumValues()` / `GetEnumNames()`
/ `GetEnumUnderlyingType()` virtuals on ILRuntime's IL-enum `System.Type` wrapper
(`ILRuntimeType`), which does NOT override them -- so they fall through to the base
`System.Type` implementation that throws a bare `NotImplementedException` (only the
framework's own `RuntimeType` overrides those virtuals). This was surfaced and DEFERRED
by the `neo-bare-nie` child (its design.md site 7 / D3, "a binding/IL-enum-Type-
representation investigation"). This change closes that follow-up. ~8 pre-crash full-
Neo-smoke hits.

## What Changes

- **IMPLEMENT** the IL-enum reflection virtuals on `ILRuntimeType`
  (`ILRuntime/Reflection/ILRuntimeType.cs`):
  - `public override bool IsEnum => type.IsEnum;` (defensive: guarantees the
    framework's `IsEnum` gates -- which `System.Enum.GetValues/GetNames/
    GetUnderlyingType` use to reject non-enum types -- see an IL enum as an enum).
  - `public override Type GetEnumUnderlyingType()` -> the enum's underlying primitive
    type (the `ILType.enumType` set in `InitializeFields`, exposed via the existing
    `ILType.TypeForCLR` enum branch, `ILType.cs:1966-1970`).
  - `public override Array GetEnumValues()` -> an `Array` of the underlying type whose
    elements are the named members' constant values.
  - `public override string[] GetEnumNames()` -> the members' names.
  The member set and per-member values come straight from Cecil
  `type.TypeDefinition.Fields` (the `IsLiteral` / `HasConstant` fields, i.e. the
  enum's named constants), reading `FieldDefinition.Constant` -- which returns the
  boxed underlying value (`Mono.Cecil/.../FieldDefinition.cs:139`, already used by
  `ILRuntimeFieldInfo.GetRawConstantValue` `:168` and `GetValue` `:180-181`). This is
  exactly how the framework `RuntimeType` implements these virtuals.
- **No binding change.** The autogen `GetValues_0_Neo` / `GetNames_1_Neo` redirects
  are correct as-is (they delegate to the framework, which now sees working virtuals).
  `Enum.GetUnderlyingType` is not even redirected -- the IL call goes straight to the
  framework `System.Enum.GetUnderlyingType(Type)`, which calls our new override.
- **Add a NeoStep probe** (`TestCases/NeoStepIlEnumGetValuesTest.cs`) that calls
  `Enum.GetValues` / `Enum.GetNames` / `Enum.GetUnderlyingType` on an IL-defined enum
  and asserts values, names, and the underlying type. The probe FAULTS on HEAD (the
  bare NIE) and PASSES after the fix (child-1/child-2 probe discipline).

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `neo-type-checks`: ADD a requirement that ILRuntime's IL-enum `System.Type` wrapper
  (`ILRuntimeType`) SHALL answer the enum reflection virtuals (`IsEnum`,
  `GetEnumUnderlyingType`, `GetEnumValues`, `GetEnumNames`) for an IL-defined enum, so
  the framework `System.Enum.GetValues/GetNames/GetUnderlyingType` and any other
  reflection-based enum query work on an IL enum surfaced as `System.Type`. This is the
  type-identity/reflection-query surface on IL types already owned by this capability
  (alongside `CanAssignTo` / `IsAssignableFrom`); it is NOT a value-type-storage change.

## Impact

- **Code**: `ILRuntime/Reflection/ILRuntimeType.cs` only (4 new/overridden members,
  ~50-70 lines). No JIT/optimizer/object-model change, no new opcode, no binding change.
- **Probes**: new `TestCases/NeoStepIlEnumGetValuesTest.cs`.
- **Legacy**: Neutral by construction -- the fix is in shared (non-`#if
  ENABLE_NEO_MODE`) reflection code and is purely additive (overrides that currently
  fall through to a bare throw). The Legacy redirect `GetValues_0`
  (`System_Enum_Binding.cs:88`) makes the SAME framework call, so Legacy benefits too;
  the Legacy smoke baseline (the documented 17-failure set) is unchanged.
- **Smoke**: NeoStep **348/0** must hold and grow by the new probe(s) (~350-351/0); the
  full (unfiltered) Neo smoke loses the ~8 `System.Type.GetEnumValues()` bare-NIE hits.
