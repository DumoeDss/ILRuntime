# Tasks: neo-step25-s3-ccctor (S3-4 TRUE COMPLETION)

> Ordered. Each task is small + independently checkable. Neo-only (`#if
> ENABLE_NEO_MODE`); Legacy-neutral. Build + smoke with `-f net8.0` (see
> planning-context). A green smoke does NOT prove the gate -- the capstone +
> the adversarial body-mutation cell (Task 9) are mandatory.

## 1. Record: add `StaticFields[]` to `NeoTypeDefRecord` + bump `.neo` Version

- [x] 1.1 `ILRuntime/Runtime/NeoAOT/NeoAssembly.cs`: add
      `public NeoFieldLayoutRecord[] StaticFields;` to `NeoTypeDefRecord` (parallel
      to the instance `Fields[]`; reuse the existing `NeoFieldLayoutRecord` struct
      `{FieldRefIdx, PrimitiveOffset, ReferenceOffset}`). Document it (instance vs
      static, the S3-4 rationale).
- [x] 1.2 `NeoAssembly.cs`: bump `NeoAssemblyFormat.Version` 2 -> 3. Update the
      Version comment (V3 adds the per-static-field `StaticFields[]` parallel to
      the instance `Fields[]`; additive; the Cecil-free loader's Version guard
      rejects a V2 `.neo` for the static-field path).
- [x] 1.3 Confirm `StaticCtorMethodRefIdx` field ALREADY exists in the record
      (recorded at `NeoAssemblyWriter.cs:883`, read at `NeoAssemblyReader.cs:248`,
      unused by the loader). NO record change for it -- just consumed later.

## 2. Writer: serialize `StaticFields[]`

- [x] 2.1 `ILRuntime/Runtime/NeoAOT/NeoAssemblyWriter.cs` `BuildTypeDef` (around
      `:883`): build `rec.StaticFields` from the Cecil-computed
      `type.staticFieldOffsets` + `type.staticFieldTypes` + the static-field name
      order. Reuse the instance `BuildFieldLayout` pattern (FieldRef by name +
      type). A type with no static fields writes an empty array.
- [x] 2.2 `NeoAssemblyWriter.cs` `WriteTypeDef` (around `:406`): write
      `StaticFields[]` (length-prefixed; each entry = FieldRefIdx + PrimitiveOffset
      + ReferenceOffset, the SAME shape as the instance field layout). Match the
      instance-field serialization block exactly (proven layout).
- [x] 2.3 Sanity: a `Debug_Neo` build of the CLI compiles cleanly with the new
      serialization.

## 3. Reader: deserialize `StaticFields[]`

- [x] 3.1 `ILRuntime/Runtime/NeoAOT/NeoAssemblyReader.cs` `ReadTypeDef` (around
      `:248`): read `StaticFields[]` (length-prefixed; mirror the instance-field
      read block). Assign to `td.StaticFields`.
- [x] 3.2 Version guard: the Cecil-free loader path rejects a V2 `.neo` for the
      static-field path (a V2 `.neo` with no static fields still Cecil-free-loads
      -- treat a missing array as "no static fields"). The same-AppDomain path
      ignores the array.

## 4. Factory: install the static-field layout + track the `.cctor`

- [x] 4.1 `ILRuntime/CLR/TypeSystem/ILType.cs` `CreateFromNeoRecord` (after the
      instance-layout block, `~:1324`): add a static-layout block that installs
      `staticFieldMapping{name=i}`, `staticFieldTypes[i]` (resolved by name from
      the FieldRef's type via the existing `ResolveNamedIType` helper), and
      `staticFieldOffsets[i] = {PrimitiveOffset, ReferenceOffset}` from
      `rec.StaticFields`. Mirror the instance block. A type with an empty
      `StaticFields[]` sets empty arrays + an empty mapping.
- [x] 4.2 Same factory: set the type's `staticConstructor` field from
      `rec.StaticCtorMethodRefIdx`. The factory ALREADY builds a `.cctor` shell
      into `t.constructors` (the `.cctor` name match at `:1349`). Set
      `staticConstructor` = that shell (or resolve via `StaticCtorMethodRefIdx` ->
      the matching constructor). Leave `staticConstructorCalled = false`.
- [x] 4.3 Confirm the Stsfld / Ldsfld baked-token path works UNCHANGED: the
      static-field index (`GetFieldIndex` -> `staticFieldMapping[name]`, `ILType.cs:
      2405`) resolves once `staticFieldMapping` is populated (4.1). No JIT/token
      change.

## 5. Static-instance ctor: Cecil-free-safe branch

- [x] 5.1 `ILRuntime/Runtime/Intepreter/ILTypeInstance.cs` `ILTypeStaticInstance`
      ctor Neo branch (`:50-74`): add a `#if ENABLE_NEO_MODE` guard -- if
      `type.isNeoAotType`, SKIP the `type.TypeDefinition.Fields` loop (which would
      NRE/throw on a Cecil-free type). The `byte[]` / `AutoList` sizing (driven by
      the static totals) + per-field offset access (`GetStaticFieldOffset`) are
      ALREADY Cecil-free-safe once Task 4 installs `staticFieldOffsets`.
- [x] 5.2 Confirm a Cecil-free ILType with static fields constructs an
      `ILTypeStaticInstance` without reading `TypeDefinition` (a debug assert or a
      smoke step).

## 6. Loader: seed the `.cctor` at Cecil-free load

- [x] 6.1 `ILRuntime/Runtime/Enviorment/AppDomain.cs` `LoadNeoAssembly` (`:719-
      792`): add a final step (5), AFTER the `NeoAssemblyLoader.Attach` body-
      binding step (4) + AFTER the hash re-registration step (3). For each built
      Cecil-free ILType whose `rec.StaticCtorMethodRefIdx != -1`: resolve the live
      `.cctor` ILMethod (from `iltype.staticConstructor` set in Task 4.2) and call
      `appdomain.Invoke(cctor, null, null)` (the Legacy call at `ILType.cs:209`).
      Wrap each invoke in best-effort try/catch (a throwing `.cctor` is recorded as
      a skip, never fatal). A `StaticCtorMethodRefIdx == -1` type is skipped.
- [x] 6.2 Confirm the seed runs AFTER body binding (so the `.cctor`'s CompiledFrame
      is populated from the `.neo`) + AFTER hash re-registration (so the `.cctor`'s
      Stsfld tokens resolve).

## 7. Probe: `TestCases/NeoStep25S3CctorProbe`

- [x] 7.1 `TestCases/NeoStep25S3CctorProbe.cs`: a top-level non-generic IL class,
      BCL-refs-only, with (a) a `public static int SVal;`, (b) a `.cctor` that sets
      `SVal = <known non-zero constant>` (e.g. 777), (c) a `public static int
      ReadStatic()` returning `SVal`. Document the expected value + the capstone +
      the adversarial mutation cell.
- [x] 7.2 Confirm the probe compiles into `TestCases.dll` cleanly (`dotnet build
      TestCases/TestCases.csproj -c Debug`).

## 8. Self-check: extend `NeoStep25CecilFreeLoadCheck` with the `.cctor` cell

- [x] 8.1 `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep25CecilFreeLoadCheck.cs`:
      add a `.cctor`-capstone cell (or a sibling check) that compiles
      `NeoStep25S3CctorProbe` in AppDomain A, Cecil-free-loads the `.neo` into a
      fresh AppDomain B, invokes `ReadStatic()` via `domainB.Invoke`, and asserts
      the result EQUALS the `.cctor`-set constant (NOT zero).
- [x] 8.2 Adversarial body-mutation cell: deserialize `modelA` into `model2`,
      MUTATE the `.cctor` body's `Ldc_I4` stored constant in
      `model2.MethodDefs[k].NeoExecuteBody[i].TokenInteger` (locate the `.cctor`
      MethodDef by `.cctor` name + declaring type; locate the `Ldc_I4` op by
      `OpCodeREnum.Ldc_I4`) BEFORE `LoadNeoAssembly`, load into a fresh B2, invoke
      `ReadStatic()` -> assert the result is the MUTATED constant. A Cecil-fallback
      or a default-zero read FAILS.

## 9. CLI hook + build + smoke

- [x] 9.1 `ILRuntimeTestCLI/Program.cs`: confirm the existing
      `NeoStep25CecilFreeLoad` special mode drives the extended self-check (no new
      mode needed if the `.cctor` cell is folded into the existing check; else add
      a hook).
- [x] 9.2 Build: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c
      Debug_Neo` (0 errors) + `dotnet build TestCases/TestCases.csproj -c Debug`
      (0 errors).
- [x] 9.3 Capstone smoke: `dotnet run -c Debug_Neo -f net8.0 --project
      ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/
      TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep25CecilFreeLoad`
      -> the `.cctor` cell PASSES (capstone + mutation cell).
- [x] 9.4 Regression: `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI
      --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll
      HotfixAOT/Patched/HotfixAOT.patch true NeoStep` -> the NeoStep smoke stays
      green (no regression). ALSO run `NeoStep25LoadExecCheck` (the S1/S2/S3
      roundtrip) -> unchanged (the same-AppDomain path ignores `StaticFields[]`).
- [x] 9.5 Legacy-neutral: a plain `Debug` build of the CLI (no `ENABLE_NEO_MODE`)
      compiles cleanly (all new code compiles out).

## 10. Validation (the gate is NOT a green smoke)

- [x] 10.1 The capstone cell PASSES (`.cctor`-set static reads correctly on a
      Cecil-free load).
- [x] 10.2 The adversarial body-mutation cell PASSES (mutated `.cctor` constant
      reflected) -- WITHOUT it the gate is UNPROVEN.
- [x] 10.3 The NeoStep smoke + `NeoStep25LoadExecCheck` regression is green.
- [x] 10.4 A plain-`Debug` build is byte-identical (Legacy-neutral).
