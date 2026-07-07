# Tasks — neo-step25-runtime-loader (Step 25 S1: runtime `.neo` loader + ILMethod AOT-init)

> Implementation checklist for the S1 scoped slice (see proposal.md + design.md).
> All work is `#if ENABLE_NEO_MODE`; the self-check is `#if ENABLE_NEO_MODE &&
> DEBUG`. Do NOT modify the JIT/Cecil path, `ExecuteNeo`, the optimizer, the
> Step-22 template mechanism, or the Step-23/24 artifacts.

## 1. ILMethod AOT-init dual-path (the core seam)

- [x] 1.1 Add the Neo-only `internal bool isNeoAotBody` field (default `false`)
      to `ILMethod` (`ILRuntime/CLR/Method/ILMethod.cs`), gated `#if
      ENABLE_NEO_MODE`.
- [x] 1.2 Add `internal void InitCodeBodyFromNeo(NeoMethodDefRecord rec)` to
      `ILMethod` (Neo-only). Populate `compiledFrame` field-by-field from `rec`:
      `NeoExecuteBody`, `LocalInfos`, `ParamInfos`, `TotalStructSize`,
      `TotalRefSize`, `ParamPrimitiveSize`, `ParamReferenceCount`,
      `LocalsPrimitiveSize`, `LocalsReferenceCount`, `ReturnPrimitiveSize`,
      `ReturnRefCount`, `StackRegisterCount`, `LocalIsReference`,
      `NeoCatchException{RegIndex,ByteOffset,RefOffset}`.
- [x] 1.3 In `InitCodeBodyFromNeo`: rebuild `compiledFrame.SwitchTargets`
      (`Dictionary<int,int[]>`) from the serialized `KeyValuePair<int,int[]>[]`,
      and `compiledFrame.NeoCallParams` from `NeoCallParamMapRecord[]`
      (resolving `PrimitiveByRefElemType` CLR `System.Type[]` back from aqnames
      via `Type.GetType(aqname)` / `appdomain.GetType`).
- [x] 1.4 In `InitCodeBodyFromNeo`: rebuild the runtime EH structures
      (`Method.ExceptionHandler[]` / the `exceptionHandlerR` field the Neo
      runtime reads) from `NeoExceptionHandlerRecord[]` (body-index ranges map
      1:1 to `NeoExecuteBody` positions; `CatchType` resolved from
      `CatchTypeRefIdx` via the loader-provided resolver -- see 2.3). Trace the
      EXACT field `ExecuteNeo`'s EH dispatch reads (find it in
      `ILIntepreter.Neo.cs` `CheckExceptionType` / the EH lookup) and populate
      THAT field.
- [x] 1.5 In `InitCodeBodyFromNeo`: set the ILMethod-level mirrors
      `bodyRegister = rec.NeoExecuteBody`, `stackRegisterCnt =
      rec.StackRegisterCount`, `jumptablesR = compiledFrame.SwitchTargets`, and
      `isNeoAotBody = true`.
- [x] 1.6 Modify the `BodyRegister` getter (`:389`) to short-circuit when
      `isNeoAotBody` is `true` (return `bodyRegister` without calling
      `InitCodeBody`). Gate the short-circuit `#if ENABLE_NEO_MODE`. Confirm the
      `CompiledFrame` getter (`:423`) already sees the populated frame (it
      checks `NeoExecuteBody != null`) -- no change needed there.
- [x] 1.7 Resolve OQ1/OQ2/OQ3 (design.md): set `localVarCnt` from
      `LocalInfos.Length` for consistency; leave `registerSymbols` null; the
      catch-type resolver handles IL types via `LoadedTypes` (CLR types are
      round-2).

## 2. NeoAssemblyLoader (the `.neo` -> ILMethod binder)

- [x] 2.1 Create `ILRuntime/Runtime/NeoAOT/NeoAssemblyLoader.cs` (Neo-only,
      `internal static class`). Add `internal static NeoLoadReport Attach(
      AppDomain appdomain, NeoAssemblyModel model)`.
- [x] 2.2 In `Attach`: iterate `model.MethodDefs`; for each record, resolve
      `MethodRefIdx` -> `MethodReferencePatchInfo`; resolve the declaring-type
      full name via `model.TypeRefs[mr.DeclaringTypeIdx].Name`; look up the
      `ILType` via `appdomain.LoadedTypes[fullName]`. On a type miss, record a
      skip + continue.
- [x] 2.3 Match the method on the ILType by name + parameter count (skip
      `IsGenericInstance` methods). On a match, call `ilm.InitCodeBodyFromNeo(
      rec)` with a catch-type resolver closure (resolves a TypeRef idx -> IType
      via `LoadedTypes` / `GetType(aqname)`). On a miss, record a skip.
- [x] 2.4 `NeoLoadReport` (new): `List<string> Attached` + `List<(string
      reason, string target)> Skipped`. Return it from `Attach`.
- [x] 2.5 The loader SHALL NOT touch the `TemplateTable` (generic definitions --
      S2 deferred). It SHALL accept a `.neo` that carries templates (they are
      simply not consumed).

## 3. The V2 functional self-check (the capstone)

- [x] 3.1 Create `TestCases/NeoStep25LoadProbe.cs` -- the dedicated probe type
      (non-generic methods only): `ArithProbe` (arithmetic + return a value),
      `TryCatchProbe` (try/catch + return), `MixedLocalsProbe` (various locals +
      a byref parameter + return). Mirror the Step-24 `NeoStep24CliProbe`
      pattern. Each method returns a deterministic primitive for result
      comparison.
- [x] 3.2 Create `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep25LoadExecCheck.cs`
      (or the NeoAOT folder alongside the loader -- match the Step-23/24
      self-check placement), `#if ENABLE_NEO_MODE && DEBUG`. Implement
      `Run(AppDomain)` returning a pass/fail report.
- [x] 3.3 In `Run`: select the probe ILType (Cecil-loaded in the test
      AppDomain); compile a `.neo` via `new NeoCompiler().Compile(new[] {
      probeType }, ms)`; `ms.Position = 0`; `var model =
      NeoAssemblyReader.Read(ms)`.
- [x] 3.4 In `Run`: for each probe method, `Invoke` via the JIT path (capture
      `resultJIT`) BEFORE attach. Then `NeoAssemblyLoader.Attach(appdomain,
      model)`. Then `Invoke` each probe method again (capture `resultAOT`, now
      via the AOT body + `ExecuteNeo`).
- [x] 3.5 In `Run`: assert `resultJIT == resultAOT` per method via the value/
      divide path (`if (!Equal(resultJIT, resultAOT)) { int x = 1/0; }`).
      `Equal` compares the deterministic primitive results. Report the attached
      + skipped methods from the `NeoLoadReport`.
- [x] 3.6 Add the `NeoStep25LoadExec` hook to `ILRuntimeTestCLI/Program.cs`
      (mirror the `NeoStep24CliRoundtrip` block): `if (nameFilter ==
      "NeoStep25LoadExec") { var r = NeoStep25LoadExecCheck.Run(session.
      Appdomain); ... print pass/fail cells ... }`.

## 4. Build + regression gate + Legacy-neutral

- [x] 4.1 `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`
      (transitively builds ILRuntime + ILRuntimeTestBase + LitJson) -- 0 errors.
- [x] 4.2 `dotnet build TestCases/TestCases.csproj -c Debug` (NEVER
      `Debug_Neo`) -- 0 errors; the probe type compiles into `TestCases.dll`.
- [x] 4.3 Run the V2 capstone:
      `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build --
      TestCases/bin/Debug/netstandard2.1/TestCases.dll
      HotfixAOT/Patched/HotfixAOT.patch true NeoStep25LoadExec`
      -- assert every probe method's JIT == AOT (no DivideByZero).
- [x] 4.4 Regression smoke (additive):
      `... true NeoStep` -- 205/205.
      `... true NeoStep23Roundtrip` -- 15/15.
      `... true NeoStep22SelfCheck` -- 55/55.
      `... true NeoStep24CliRoundtrip` -- 5/5.
- [x] 4.5 Legacy-neutral: `dotnet build ILRuntimeTestCLI -c Debug` -- 0 errors
      (the loader + the AOT-init + the self-check compile out). Stash-toggle
      plain-`Debug` + `useRegister=true` NeoStep-filter run -- SAME pre-existing
      failure set with and without the change.
- [x] 4.6 Build-cache gotcha: confirm `TestCases.dll` rebuilt after editing the
      probe type (re-run the `TestCases` build before the V2 run).

## 5. Findings + ship

- [x] 5.1 Append durable findings to `openspec/changes/neo-step25-runtime-
      loader/planning-context.md` (the Cecil-coupling enumeration, the dual-path
      mechanism, the S1 scope + deferrals, the cross-AppDomain decision, the V2
      functional design) -- done at propose.
- [x] 5.2 Record the round-2 deferrals (S2 generic-at-load, S3 full ILType
      decoupling + cross-AppDomain + .cctor seeding) in
      `.trae/documents/neo-deferred-items.md` at ship (the authoritative
      deferred-items map).
