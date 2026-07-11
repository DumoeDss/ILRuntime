# Tasks: neo-step25-s2-generic-at-load

> Ordered for the dump-gate discipline: probe BEFORE each engine change. Every
> engine edit is `#if ENABLE_NEO_MODE`; tests are `#if ENABLE_NEO_MODE && DEBUG`
> (self-check) or plain (probe type). Legacy is the REFERENCE, not a target --
> keep it byte-identical.

## 1. Dump-gate (BEFORE any engine change)

- [x] 1.1 Confirm on HEAD `cfbf8ff9`: `NeoAssemblyLoader.Attach`
      (`NeoAssemblyLoader.cs:51-93`) iterates ONLY `model.MethodDefs`; the
      generic skips at `:61-65` (instance) + `:86-90` (definition) + `:112`
      (MatchMethod). Confirm `model.Templates` is deserialized but unconsumed.
- [x] 1.2 Confirm the Step-22 hook in `ILMethod.InitCodeBody`
      (`ILMethod.cs:720-762`) routes a generic instance through
      `GenericMethodTemplateOps.TryInstantiate` when
      `genericDefinition.GenericMethodTemplateCache != null`. Confirm
      `StoreGenericTemplate` (`ILMethod.cs:1392-1404`) guards overwrite.
- [x] 1.3 Confirm `ILIntepreter.Run` (`ILIntepreter.cs:87-120`) runs a
      parameterless wrapper that makes an internal call (S1's `MixedLocalsProbe`
      -> `BumpRef` is green). A generic `Call` is the same shape. => P1
      confirmed; P2 not needed.
- [x] 1.4 Probe the chosen generic probe method (D5/OQ2): does its register-
      index template body carry an OBSERVABLE `Ldc_I4` constant for the
      body-mutation cell? If the optimizer folds it, pick a different mutation
      target. Record the chosen target.
      => DUMP-RESOLVED: `ConstGeneric<T>` TemplateBody = `[Ldc_I4=1234567, Br_S,
      Ret]`; the `Ldc_I4 1234567` is observable (not folded). Mutation target =
      that operand -> 7654321.

## 2. Engine: the ILMethod AOT template setter

- [x] 2.1 Add `ILMethod.InitTemplateFromNeo(GenericMethodTemplate template)`
      (`ILMethod.cs`, `#if ENABLE_NEO_MODE`, near `StoreGenericTemplate` at
      `:1392`). It sets `genericMethodTemplate = template` DIRECTLY (no
      `if (!= null) return` guard -- it OVERWRITES). Neo-only.
- [x] 2.2 Confirm `GenericMethodTemplateCache` getter (`:1387-1390`) still
      returns null for an instance; only the definition caches. No change to
      `StoreGenericTemplate`'s guard.

## 3. Engine: BuildFromNeoRecord reconstruction helper

- [x] 3.1 Add `GenericMethodTemplateOps.BuildFromNeoRecord(definition,
      appdomain, model, NeoTemplateRecord rec, Func<int, TypeReference>
      resolveVariableType)` (`GenericMethodTemplate.cs`, `#if ENABLE_NEO_MODE`).
      Mirrors `StoreFromCapture` (`:383-404`) but takes the `.neo` record.
- [x] 3.2 Populate the Cecil-free fields from the record: `TemplateBody`,
      `LocVarRegStart`, `TotalRegCnt`, `NeoCatchExRegFinal`, `StackRegisterCount`,
      `VarCnt`, `InitObjPrefixLength`, `InitObjPrefixRegisters`,
      `SwitchTargets` (pairs -> dict). Set `Definition = definition`.
- [x] 3.3 Re-resolve `VariableTypes` from `rec.VariableTypeRefIdxs` via the
      `resolveVariableType` closure (TypeRef idx -> Cecil `TypeReference`).
      Resolve OQ1 (provide the full array of length `>= varCnt`). A miss ->
      return null (skip).
      => DUMP-RESOLVED OQ1: `Echo<T>` has `varCnt=2`, `VariableTypeRefIdxs=[3,3]`
      (both = generic param "T"). The closure maps the name "T" to
      `definition.Definition.GenericParameters[0]`. `BuildInitObjPrefix` reads
      `VariableTypes[v]` unconditionally -> a full non-null array is required
      (provided).
- [x] 3.4 Rebuild `Patches`: for each `NeoPatchEntryRecord`, set `InstrIdx` /
      `Field` / `Kind` / `GenericParamIdx` + `CecilToken = null`. If ANY patch
      has `Kind == TypeToken || Kind == MethodToken` with a non-`none`
      `CecilTokenKind` (OQ3), RETURN NULL (reject -> skip -> JIT fallback).
      `IsRefMoveFlag` patches are fine (CecilToken null, re-derived by the
      back-half).
      => DUMP-RESOLVED OQ3: `Echo`/`ConstGeneric` both have `Patches=[]` (no
      T-identity token -- the no-T-identity-token slice). The rejection path is
      implemented for correctness but not exercised by the probe.
- [x] 3.5 Set `Addr = null`, `Symbols = null`, `ConstrainedTypeTokens = null`,
      `ConstrainedMethodTokens = null` (not read at CloneAndPatch; S2 probe has
      no try/catch in the generic method).

## 4. Engine: the loader consumes model.Templates

- [x] 4.1 Extend `NeoAssemblyLoader.Attach` (`NeoAssemblyLoader.cs`) with a
      second loop over `model.Templates` (after the `model.MethodDefs` loop).
- [x] 4.2 For each `NeoTemplateRecord`: resolve `DefinitionMethodRefIdx` ->
      `MethodReferencePatchInfo`; resolve the declaring type via
      `LoadedTypes[fullName]`; match the open generic definition (name + param
      count + `GenericParameterCount > 0 && !IsGenericInstance`). A miss ->
      skip (additive contract) + report. (Matcher = `MatchGenericDefinition`.)
- [x] 4.3 Build the `resolveVariableType` closure (TypeRef idx -> Cecil
      `TypeReference`): IL type -> `iltype.TypeReference` / module; generic
      parameter -> `definition.Definition.GenericParameters[k]`; CLR type ->
      `appdomain.LoadedModules[0].ImportReference(clrType.TypeForCLR)`.
      (Resolver = `NeoAssemblyLoader.ResolveVariableType`.)
- [x] 4.4 Call `BuildFromNeoRecord`; on non-null, call `def.InitTemplateFromNeo
      (template)`; on null, skip + report. Add to `report.Attached` /
      `report.Skipped`.

## 5. Tests: the probe type

- [x] 5.1 Extend `TestCases/NeoStep25LoadProbe.cs` with a generic method (e.g.
      `T Echo<T>(T v)`) + a variant carrying an observable constant for the
      body-mutation cell (per 1.4). NON-NESTED + BCL-refs-only.
      (`Echo<T>(T v){T tmp=v;return tmp;}` + `ConstGeneric<T>(){return 1234567;}`
      + top-level `struct NeoStep25ProbeVal{public int X;}`.)
- [x] 5.2 Add non-generic PARAMETERLESS wrappers: `WrapEchoInt` /
      `WrapEchoLong` / `WrapEchoStruct` / `WrapEchoRef` (each calls the generic
      method at a concrete T). Pin the expected value of each.
      (WrapEchoRef uses `ConstGeneric<string>()` -> int 1234567, NOT
      `Echo<string>` -> string: the Run shim's `NeoBoxReturnValue` handles
      primitive returns ONLY -- a reference (string) return reads raw bytes, a
      PRE-EXISTING shim limitation, not an S2 regression. The ref-T
      CloneAndPatch correctness is proven structurally via the Echo<string>
      structural-equivalence cell + at runtime via the ConstGeneric<string>
      mutation cell.)

## 6. Tests: the capstone (the binding gate)

- [x] 6.1 Extend `NeoStep25LoadExecCheck.Run` (`NeoStep25LoadExecCheck.cs`)
      with the generic cells: compile `.neo` (captures the template) -> read ->
      `Attach` (binds + overwrites) -> invoke the wrappers via `ExecuteNeo` ->
      assert each EQUALS its expected value.
- [x] 6.2 Add the FRESH-INSTANCE cell: before-run invokes `WrapEchoInt`
      (caches `Echo<int>`); after-attach invokes `WrapEchoLong` (a fresh
      concrete T not instantiated before attach -> routes through CloneAndPatch
      via the AOT template).
      (WrapEchoLong is the fresh-instance cell for value-T. The mutation cell
      drives a fresh ref-T instance via MakeGenericMethod -- see APPLY-NOTE on
      6.3.)
- [x] 6.3 Add the TEMPLATE BODY-MUTATION cell: fresh `.neo` -> find the generic
      template's `TemplateBody` -> mutate the chosen `Ldc_I4` (per 1.4) ->
      `Attach` -> invoke a wrapper calling a fresh instance -> assert the
      MUTATED value (proves CloneAndPatch ran the AOT template body, not the
      JIT-captured one).
      (APPLY-NOTE: the runtime `Call` in a wrapper resolves its generic callee
      via `AppDomain.GetMethod(token)`, which returns the instance REGISTERED
      during the compile step's force-compile of the wrapper -- its
      `bodyRegister` is already cached with the unmutated template, so a wrapper
      invoke cannot observe the mutation. The mutation cell instead drives a
      FRESH instance via `constDef.MakeGenericMethod(string)` + a direct
      `appdomain.Invoke(freshInstance, null)` (MakeGenericMethod returns a NEW
      ILMethod, bodyRegister null -> InitCodeBody -> CloneAndPatch against the
      MUTATED AOT template). Same proof, fresh ref-T instance.)
- [x] 6.4 Add the STRUCTURAL-EQUIVALENCE cell (DEBUG host-side): add a
      `CompileViaAotTemplateNeoBody` hook (mirrors `CompileViaTemplateNeoBody`
      at `GenericMethodTemplate.cs:637` but uses the `.neo`-reconstructed
      template); assert `BodiesEqual` (`:651`) vs the per-occurrence JIT body
      for each concrete T.
      (Covers int/long/string/struct T -- string-T added to prove ref-T
      CloneAndPatch structurally.)
- [x] 6.5 Keep the existing non-generic S1 cells green (ArithProbe /
      TryCatchProbe / MixedLocalsProbe / TypedCatchProbe / ConstProbe body-
      mutation). The extension is ADDITIVE. (S1's 11/11 stay green.)

## 7. Regression + Legacy-neutral verification

- [x] 7.1 `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`
      (0 errors). `dotnet build TestCases/TestCases.csproj -c Debug`.
- [x] 7.2 Run the capstone: `dotnet run -c Debug_Neo -f net8.0 --project
      ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/
      TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep25LoadExec`
      (all cells pass, incl. the new generic + mutation + structural cells).
      => 21/21 cells passed, 0 failed (was 11/11 S1).
- [x] 7.3 Regression filters (each separately -- the CLI filter is a
      `Contains` substring, no `|`): `NeoStep` (210/210), `NeoStep22SelfCheck`
      (55/55), `NeoStep23Roundtrip` (15/15), `NeoStep24CliRoundtrip` (5/5).
      => NeoStep 215/215 (was 210 + 5 new probe methods, 0 failed),
      NeoStep22 55/55, NeoStep23 15/15, NeoStep24 5/5.
- [x] 7.4 Legacy-neutral: plain `Debug` + `useRegister=true`, NeoStep-filter
      run -> SAME pre-existing failure set with and without the change
      (stash-toggle). Everything new is `#if ENABLE_NEO_MODE`.
      => plain-`Debug` build = 0 errors (all Neo code compiles out). NeoStep
      Legacy run = 215 tests, 8 failed -- ALL pre-existing (NeoStep6 NaN etc.),
      NONE from the new probe methods (Echo/ConstGeneric/WrapEcho*). Legacy
      byte-identical (engine edits are `#if ENABLE_NEO_MODE`).
- [x] 7.5 If 7.2 shows the Cecil re-resolution is intractable for even the
      no-T-identity-token slice (OQ1 miss, or `BuildInitJPrefix` cannot recover
      `VariableTypes`), PARTIAL-SHIP: keep the structural-equivalence proof
      (host-side DEBUG, where `def.Body` is present) + document the production-
      AOT deferral to S3. Do NOT force a fix past the dump-gate.
      => N/A: full success (no partial-ship). All 4 adversarial cells pass for
      the no-T-identity-token slice. OQ1/OQ2/OQ3 resolved from the dump.

## 8. Spec sync (post-implementation)

- [x] 8.1 Update `.trae/documents/neo-deferred-items.md` STEP-25-PARTIAL row:
      mark the S2 generic-template-at-load slice SHIPPED (no-T-identity-token
      case); the T-identity-token + cross-AppDomain + EH-bearing cases remain
      S3.
- [x] 8.2 Update `.trae/documents/neo-handoff.md` Last-updated/HEAD + the S2
      status line.
