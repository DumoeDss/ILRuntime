## 1. Implement the IL-enum reflection virtuals on ILRuntimeType

- [x] 1.1 In `ILRuntime/Reflection/ILRuntimeType.cs`, add `public override bool IsEnum => type.IsEnum;` (defensive -- ensures the framework's `IsEnum` gates see an IL enum as an enum).
- [x] 1.2 Add `public override Type GetEnumUnderlyingType()`: guard `if (!type.IsEnum) throw new ArgumentException(...)` then return the underlying type via `type.TypeForCLR` (for an enum, `ILType.TypeForCLR` returns `enumType.TypeForCLR` -- `ILType.cs:1966-1970`).
- [x] 1.3 Add `public override Array GetEnumValues()`: guard on `IsEnum`; enumerate `type.TypeDefinition.Fields`, select `IsLiteral && HasConstant` fields; build `Array.CreateInstance(GetEnumUnderlyingType(), n)` and `SetValue` each `FieldDefinition.Constant` in declaration order. (May instead reuse the wrapper's `GetFields(BindingFlags.Public|BindingFlags.Static)` + `ILRuntimeFieldInfo.GetRawConstantValue()`/`GetValue(null)` -- same data; Cecil-direct is the recommended default.)
- [x] 1.4 Add `public override string[] GetEnumNames()`: guard on `IsEnum`; enumerate the same `IsLiteral && HasConstant` Cecil fields and collect `field.Name` in declaration order.
- [x] 1.5 Confirm no `#if ENABLE_NEO_MODE` is introduced (the fix is shared reflection code; Legacy-neutral by construction).

## 2. NeoStep probe

- [x] 2.1 Create `TestCases/NeoStepIlEnumGetValuesTest.cs` with a public IL-defined enum (e.g. `NeoStepIlEnumProbe { A = 0, B = 10, C = 20 }`) and `[ILRuntimeTest] public static` probe method(s) whose names embed `NeoStep` (so the `NeoStep` filter picks them up).
- [x] 2.2 Probe TC1: call `System.Enum.GetValues(typeof(NeoStepIlEnumProbe))`, assert `Length == 3`, element type is `int`, and values `0/10/20` at indices `0/1/2`; throw on any mismatch.
- [x] 2.3 Probe TC2: call `System.Enum.GetNames(typeof(NeoStepIlEnumProbe))`, assert names `"A"/"B"/"C"` in order; throw on any mismatch.
- [x] 2.4 Probe TC3: call `System.Enum.GetUnderlyingType(typeof(NeoStepIlEnumProbe))`, assert result equals `typeof(int)`; throw on mismatch.

## 3. Verify

- [x] 3.1 Build: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental` (0 errors); `dotnet build TestCases/TestCases.csproj -c Debug` (never TestCases with Debug_Neo).
- [x] 3.2 Stash-toggle: with the fix reverted (overrides removed), the probe FAULTS with the bare `NotImplementedException` from `System.Type.GetEnumValues()` on HEAD; with the fix applied, the probe PASSES.
- [x] 3.3 NeoStep smoke green: `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` => 348 prior + new probes (expected ~350-351/0). Confirm `NeoStep*` enum/type-check cases unregressed.
- [x] 3.4 Legacy-neutral: `dotnet run -c Debug -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` => the documented 17-failure baseline holds (both new probes pass under Legacy too).
- [x] 3.5 (If feasible) Full unfiltered Neo smoke: confirm the `System.Type.GetEnumValues()` bare-NIE occurrences (~8) are gone (the run still hits the known pre-existing Dict-NRE crash; counts are pre-crash).
