# Tasks: neo-step24-ilrt-neoc

## 1. The public NeoCompiler driver (in ILRuntime)

- [x] 1.1 Create `ILRuntime/Runtime/NeoAOT/NeoCompiler.cs`, file-gated
  `#if ENABLE_NEO_MODE`. Add `public sealed class NeoCompiler`,
  `public sealed class NeoCompilerResult { int TypesCompiled; int MethodsCompiled;
  int TemplatesCaptured; List<MethodSkip> Skipped; bool IsComplete; }`, and
  `public sealed class MethodSkip { string MethodDisplay; string ExceptionType;
  string Message; }`.
- [x] 1.2 Implement the host-side type-set overload `Compile(IReadOnlyList<ILType>
  inputTypes, Stream outputStream)` -- used by the V1-A self-check (resolves the
  OQ1/OQ3 "small probe set" need: the planned stream/module overload cannot scope
  to a single probe type, since its module filter yields ALL TestCases types).
  Shares the SAME `CompileCore` as the CLI entry:
  - Enumerate `inputTypes`; for each: enumerate `GetMethods()` + `GetConstructors()`;
    route each `ILMethod` to non-generic (`methods[]`) or generic-definition
    (template capture). Skip `CLRMethod`s. Skip generic INSTANCES (only definitions
    capture).
  - Capture each generic definition via `CaptureTemplate` (task 1.4).
  - Force-compile each non-generic method inside a per-method try/catch (task 1.5);
    record skips; build the survivor `methods[]`.
  - Call `new NeoAssemblyWriter().Write(inputTypes, methods, templates,
    outputStream)`.
  - Populate + return `NeoCompilerResult`.
- [x] 1.3 Implement the file-path overload `Compile(string inputAssemblyPath,
  IReadOnlyList<string> referenceAssemblyPaths, Stream outputStream)`: build a
  `DefaultAssemblyResolver`, `AddSearchDirectory` for the input dir + each ref dir,
  Cecil-read the input module WITH the resolver (`ReaderParameters`), drive
  `appdomain.InitializeFromModule(inputModule)` directly (so the resolver-armed
  module is the one registered), `LoadAssembly` each ref in a try/catch (V1
  heuristic: a CLR ref that fails to load as IL is skipped -- CLR fallback resolves
  it), filter `LoadedTypes` to the input module, then call `CompileCore`. Wrap
  input-load / serializer fatals as `NeoCompilerFatal` (exit 1).
- [x] 1.4 Implement `CaptureTemplate(appdomain, definition, result)`: if
  `definition.GenericMethodTemplateCache != null` return it; else synthesize
  `IType[] captureArgs` = one `appdomain.IntType` per generic parameter,
  `definition.MakeGenericMethod(captureArgs) as ILMethod`, try/catch reading
  `capInstance.BodyRegister` (triggers the capture), return
  `definition.GenericMethodTemplateCache`. On any throw / null cache, record a
  `MethodSkip` and return null. Reaches the internal `BodyRegister` +
  `GenericMethodTemplateCache` directly (in-assembly) -- NOT the DEBUG-gated
  `ForceBuildTemplate` (so it works in `Release_Neo`).
- [x] 1.5 Implement the per-method force-compile + skip: for each non-generic
  `ILMethod`, `_ = ilm.BodyRegister;` inside `try { ... } catch (Exception ex) {
  result.Skipped.Add(MakeSkip(ilm, ex)); }`. Survivors go to `methods[]`. (This is
  D6-preferred: pre-compile for the skip check; `Write` re-compiles via
  `CompileFresh`, which is deterministic -- the Step-23 self-check already relies
  on that.)
- [x] 1.6 `MakeSkip(ILMethod, Exception)` -> `MethodSkip { MethodDisplay =
  $"{DeclearingType.FullName}.{Name}({param FullNames})", ExceptionType =
  ex.GetType().Name, Message = ex.Message }`.

## 2. The CLI project (ILRuntimeNeoCompiler/)

- [x] 2.1 Create `ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj` mirroring
  `PatchTool.csproj`: `OutputType=Exe`, `TargetFrameworks=net8.0;netcoreapp3.0`,
  `Configurations=Debug;Release;Debug_Neo;Release_Neo`. Add the
  `<DefineConstants>ENABLE_NEO_MODE</DefineConstants>` property groups for
  `Debug_Neo` + `Release_Neo` (copy from `ILRuntimeTestCLI.csproj`). Add
  `<ProjectReference Include="..\ILRuntime\ILRuntime.csproj" />` + Cecil `Reference`
  hints (`ILRuntime.Mono.Cecil` + `.Pdb` -> `..\Dependencies\netstandard2.0\`).
  Do NOT add to `ILRuntime.sln`.
- [x] 2.2 Create `ILRuntimeNeoCompiler/Program.cs` (thin wrapper): parse positional
  `<input> <output> [refs...]`; if `<2` args or missing input -> usage + return 1;
  `File.Create(output)`; `new NeoCompiler().Compile(input, refs, outputStream)`
  inside try/catch (fatal -> stderr + return 1); print the report
  (`compiled {MethodsCompiled} methods, {TemplatesCaptured} templates, {TypesCompiled}
  types`); per-skip stderr line; `return result.IsComplete ? 0 : 2`.
- [x] 2.3 Default namespace + AssemblyName set in the csproj (`RootNamespace=
  ILRuntimeNeoCompiler`, `AssemblyName=ilrt_neoc`). PatchTool has NO explicit
  AssemblyInfo (SDK auto-generates), so none added (mirrored exactly).

## 3. Build verification (standalone)

- [x] 3.1 `dotnet build ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj -c
  Debug_Neo` -- 0 errors (transitively builds ILRuntime). If it fails, fix the
  csproj refs / config (do NOT add to the sln).
- [x] 3.2 Confirm `ILRuntime.Runtime.NeoAOT.NeoCompiler` is reachable from the tool
  (the ProjectReference exposes the public class; internals are NOT needed by
  `Program.cs`).

## 4. V1-A host-side self-check (the load-bearing gate)

- [x] 4.1 Add a small dedicated probe type to `TestCases/`
  (`NeoStep24CliProbe.cs`): a handful of methods -- a non-generic `ProbeBasic`,
  a generic `GenericProbe<T>`, a `TryCatchProbe`, a `MixedLocals` (a struct local),
  an instance ctor + a static ctor, PLUS a nested `NestedProbe` (non-generic +
  generic) to exercise multi-type + nested-type enumeration. Enough to exercise the
  generic/non-generic split + template capture + frame diversity. SMALL (sub-second
  compile).
- [x] 4.2 Create
  `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep24CliRoundtripCheck.cs`,
  file-gated `#if ENABLE_NEO_MODE && DEBUG`, `public static class
  NeoStep24CliRoundtripCheck { public class Result { ... } public static Result
  Run(AppDomain appdomain); }`. Mirror `NeoStep23RoundtripCheck`'s structure.
- [x] 4.3 In `Run`: locate the probe types (outer + nested) in
  `appdomain.LoadedTypes`; call `new NeoCompiler().Compile(probeTypes, ms)` (the
  explicit-types overload, handing the EXISTING TestCases-loaded types so it does
  NOT re-load `TestCases.dll`); read back via `NeoAssemblyReader.Read(ms)`.
- [x] 4.4 Assert via Step-23 comparators:
  - Header `Magic` + `Version`.
  - Counts: `MethodDefs.Length` == non-generic probe method count;
    `Templates.Length` == generic-definition count; `TypeDefs.Length` == probe type
    count.
  - `model1 == model2` element-wise (model2 = a DIRECT `NeoAssemblyWriter.Write` on
    the same partitioned arrays, so the ref-idx scheme matches) via
    `NeoStep23RoundtripCheck.TypeDefsEqual` / `MethodDefsEqual` / `TemplatesEqual`.
  - Independent body check: each non-gen `MethodDef.NeoExecuteBody` byte-equals a
    fresh `NeoAssemblyWriter.CompileFresh`; each `Template.TemplateBody` byte-equals
    the in-memory `ForceBuildTemplate` body (`OpCodeRsEqual`).
  - The generic/non-generic split: every `ngen` entry is non-generic; every `gdef`
    is a generic definition.
- [x] 4.5 Add the CLI hook in `ILRuntimeTestCLI/Program.cs` (mirror the
  `NeoStep22SelfCheck` / `NeoStep23Roundtrip` blocks): `if (nameFilter ==
  "NeoStep24CliRoundtrip") { var r = NeoStep24CliRoundtripCheck.Run(session.Appdomain);
  ...; return r.Failed <= 0 ? 0 : -1; }` inside the existing `#if ENABLE_NEO_MODE`
  block.

## 5. V1-B literal-CLI smoke (optional; timeboxed)

- [x] 5.1 V1-B literal-CLI smoke (minimal; V1-A is the gate). Arg-parse + exit-code
  contract proven directly: no-args -> exit 1 + usage; non-existent input -> exit 1.
  File-path overload on a SMALL delegate-free DLL (`Step24V1BSample`, 3 methods incl.
  1 generic) -> exit 0, output `.neo` first-4-bytes == Magic, `NeoAssemblyReader`
  reads it. NOTE: the full-`TestCases.dll` V1-B is NOT run to completion -- it stalls
  on delegate/async methods (Step 19+ incomplete JIT paths can hang the force-compile;
  NIE-throws ARE caught per-method, but a JIT path that loops does not throw). This is
  a Step 19 gap, NOT a Step 24 bug; V1-A (the load-bearing gate) is green.

## 6. Regression gate + Legacy-neutral

- [x] 6.1 Build: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`
  (0 errors) + `dotnet build TestCases/TestCases.csproj -c Debug` (0 errors) +
  `dotnet build ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj -c Debug_Neo` (0
  errors).
- [x] 6.2 Run the V1-A gate: **5/5 cells PASS** (driver + header + counts/split +
  model1==model2 + fresh-body).
- [x] 6.3 Run the regression smoke: **NeoStep 205/205, 0 failed**.
- [x] 6.4 Run `NeoStep23Roundtrip` (**15/15**) + `NeoStep22SelfCheck` (**55/55**).
  (NeoOptHardening 24/24 + NeoStep20 9/9 are unchanged Step-22/20 smokes -- not
  re-run; Step 24 is additive and touches neither.)
- [x] 6.5 Legacy-neutral: plain-`Debug` ILRuntimeTestCLI builds **0 errors**
  (NeoCompiler + the self-check compile out under `#if ENABLE_NEO_MODE`); the
  plain-Debug NeoStep-filter run shows the SAME pre-existing Legacy failure set
  (8 -- NeoStep cases under ExecuteR; expected). All Step-24 runtime-assembly edits
  are `#if`-gated; `NeoStep23RoundtripCheck.cs` itself compiles out of plain Debug,
  so the comparator `internal` edits are moot there -> the Legacy runtime DLL is
  byte-identical.

## 7. Ship

- [x] 7.1 Confirm the change is additive (diff: new `NeoCompiler.cs`, new
  `ILRuntimeNeoCompiler/` project, new `NeoStep24CliRoundtripCheck.cs`, new
  probe type, the CLI hook -- NO edits to JIT / runtime / optimizer /
  Step-22 / Step-23 serializer LOGIC; the only Step-23 edit is 3 comparators
  `private` -> `internal`, additive).
- [ ] 7.2 Commit + push per the repo convention (Co-Authored-By trailer). Branch
  off `features/object-model-overhaul`. **(LEAD step -- the implementer leaves
  artifacts on disk; the LEAD commits + pushes.)**
- [x] 7.3 Append durable findings to
  `openspec/changes/neo-step24-ilrt-neoc/planning-context.md` (the `## Findings --
  (apply, 2026-07-07)` section: the NeoCompiler seam, the tool project, the OQ
  resolutions, the V1-overload deviation, the V1 roundtrip design + result, the
  accessor added, the V1-B result).
- [x] 7.4 Update `.trae/documents/neo-handoff.md` section 6.A: Step 24 marked DONE
  with the smoke numbers (Neo 205/205, NeoStep23Roundtrip 15/15, NeoStep22SelfCheck
  55/55, V1-A 5/5) + the V1-A result; Step 25 recorded as next.

## Notes

- **D6-preferred is binding:** pre-compile each non-generic method (per-method
  catch) for the skip set, then let `NeoAssemblyWriter.Write` re-`CompileFresh`.
  Do NOT modify `NeoAssemblyWriter` for error handling (additive / no Step-23
  change). If the 2x tool-time compile is judged unacceptable at apply, fall back
  to Q1's `Write` overload (a small additive Step-23 change) -- confirm with the
  LEAD first.
- **Generic/non-generic split is the single most important correctness rule.**
  Never pass a generic definition to `methods[]`.
- **No `InternalsVisibleTo`.** The driver is `public` and in-assembly; `Program.cs`
  references only the public `NeoCompiler` surface.
- **ASCII-primary authoring** (the Write tool corrupts ~0.5% of CJK on large
  payloads). All new files are ASCII.
