# Design: neo-step24-ilrt-neoc

## Context

Step 22 shipped the in-memory generic-method template mechanism
(`GenericMethodTemplate` + `CloneAndPatch`, `GenericMethodTemplate.cs`). Step 23
shipped the `.neo` serialization format under `ILRuntime/Runtime/NeoAOT/`
(`NeoAssemblyWriter` / `NeoAssemblyReader` / `NeoAssemblyModel` / the table
records). Both are exercised host-side by `NeoStep22SelfCheck` /
`NeoStep23RoundtripCheck` driven through `ILRuntimeTestCLI`. Step 24 is the
command-line INTEGRATION layer that wires them end-to-end on a real input
assembly: Cecil-load -> bulk JIT compile -> template capture -> Step-23 serialize
-> `.neo` output.

The load-bearing grounding (verified verbatim on HEAD, Step 23 archived
2026-07-07):

- **`NeoAssemblyWriter.Write(ILType[] types, ILMethod[] methods,
  GenericMethodTemplate[] templates, Stream stream)`** is the serializer entry
  (`NeoAssemblyWriter.cs:467`). It builds the ref tables, calls `CompileFresh`
  per method internally (`NeoAssemblyWriter.cs:909`, which runs `new
  JITCompiler(...).Compile(...)` -- the per-occurrence Neo JIT), assembles
  `NeoTypeDefRecord` / `NeoMethodDefRecord` / `NeoTemplateRecord`, and writes the
  header + 7 tables.
- **`AppDomain.LoadAssembly(stream)`** -> `InitializeFromModule(module)`
  (`AppDomain.cs:639/669`) iterates Cecil `module.GetTypes()` (FLATTENS nested
  types) and `AddType`-registers an `ILType` for every one. Post-load,
  `appdomain.LoadedTypes` holds every type in the assembly.
- **`ILType.GetMethods()`** (`ILType.cs:1316`) -> all non-ctor methods;
  **`ILType.GetConstructors()`** -> instance ctors + static `.cctor`. Both are
  enumerated.
- **`ILMethod.BodyRegister`** (`ILMethod.cs:389`, internal) -- the on-demand
  compile entry: reading it triggers `InitCodeBody(true)` -> `JITCompiler.Compile`
  (compiles WITHOUT running). This is also the template-capture trigger.
- **`ILMethod.MakeGenericMethod(IType[])`** (`ILMethod.cs:1151`, public) -- creates
  a generic-instance `ILMethod`.
- **Template capture path** (`ILMethod.cs:705-735`): inside `InitCodeBody`, a
  generic instance whose definition has no cached template AND
  `IsCaptureEligible(genericArguments)` (every arg ref/primitive) attaches a
  `TemplateCapture` to the JIT, which fills it; `genericDefinition.StoreGenericTemplate(cap)`
  (`ILMethod.cs:1201`) caches it on `genericDefinition.GenericMethodTemplateCache`
  (`ILMethod.cs:1196`, internal). The existing `GenericMethodTemplateOps.ForceBuildTemplate`
  (`GenericMethodTemplate.cs:626`, `#if DEBUG && ENABLE_NEO_MODE`) wraps this as
  `MakeGenericMethod(int-per-param).BodyRegister` then reads the cache.
- **Visibility boundary:** every NeoAOT type (`NeoAssemblyWriter/Reader/Model/
  NeoRefTableBuilder`) and the compile/cache entries (`ILMethod.BodyRegister`,
  `ILMethod.GenericMethodTemplateCache`, `GenericMethodTemplateOps.ForceBuildTemplate`)
  is `internal`. There is NO source-level `InternalsVisibleTo` (verified -- the
  binary `grep` hits are inside compiled dependency DLLs, not ILRuntime source).
- **PatchTool** (`PatchTool/Program.cs` + `PatchTool.csproj`) -- the standalone-tool
  structural reference. `ILRuntimeTestCLI.csproj` -- the `Debug_Neo` configuration
  with `DefineConstants=ENABLE_NEO_MODE` reference.

## Goals / Non-Goals

**Goals:**
- A new `ILRuntimeNeoCompiler/` console-app project that builds standalone
  (`dotnet build ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj -c Debug_Neo`),
  NOT grafted onto the sln (the sln cannot build whole).
- A `public NeoCompiler` driver class inside the ILRuntime assembly that wraps the
  internal NeoAOT + JIT + template machinery behind one public seam, so the
  separate-assembly CLI can drive it without `InternalsVisibleTo`.
- Bulk enumeration of every IL method in the input assembly (incl. nested types,
  generic methods, ctors, static ctors), force-compiled via Step 23's writer.
- Template capture for every generic method definition via synthesized
  capture-eligible instantiation (no call site needed).
- Per-method error reporting (skip-with-warning; partial-failure exit code).
- A V1 CLI-roundtrip self-check (load-bearing) that proves the wiring end-to-end,
  reusing Step 23's comparators.

**Non-Goals:**
- The runtime `.neo` LOADER + Cecil-decoupling + V2 functional (Step 25).
- Cross-AppDomain token-hash re-resolution (Step 25).
- Perf (Step 26).
- Any change to JIT / runtime / optimizer / Step-22 / Step-23 behavior (additive).

## Decisions

### D1. New project layout + the public-driver seam

Two new code units, in different assemblies:

**(a) `ILRuntime/Runtime/NeoAOT/NeoCompiler.cs`** (INSIDE the ILRuntime assembly,
`#if ENABLE_NEO_MODE`). `public sealed class NeoCompiler` + `public sealed class
NeoCompilerResult`. This is the bulk-compile driver. Being in-assembly, it reaches
every internal it needs (`NeoAssemblyWriter`, `NeoAssemblyReader`,
`ILMethod.BodyRegister`, `ILMethod.GenericMethodTemplateCache`,
`GenericMethodTemplateOps`). Public surface:

```csharp
namespace ILRuntime.Runtime.NeoAOT
{
    public sealed class NeoCompiler
    {
        // Load the input + ref assemblies into a FRESH AppDomain, enumerate every
        // IL type/method in the input module, capture every generic-method
        // template, serialize via NeoAssemblyWriter.Write to outputStream.
        // Per-method compile failures are caught + recorded in result.SkippedMethods;
        // they do NOT abort the run.
        public NeoCompilerResult Compile(string inputAssemblyPath,
            IReadOnlyList<string> referenceAssemblyPaths, Stream outputStream);

        // Stream-based overload (used by the V1-A host-side self-check, which
        // hands the input as a Stream from the already-loaded TestCases module).
        public NeoCompilerResult Compile(Stream inputStream,
            IReadOnlyList<Stream> referenceStreams, ModuleDefinition inputModule,
            AppDomain appdomain, Stream outputStream);
    }
    public sealed class NeoCompilerResult
    {
        public int TypesCompiled;
        public int MethodsCompiled;        // methods emitted to MethodDefTable
        public int TemplatesCaptured;      // templates emitted to TemplateTable
        public List<MethodSkip> Skipped = new List<MethodSkip>();
        public bool IsComplete => Skipped.Count == 0;
    }
    public sealed class MethodSkip
    {
        public string MethodDisplay;       // declaring-type-full-name.method-name(params)
        public string ExceptionType;       // e.g. "NotImplementedException"
        public string Message;
    }
}
```

Rationale for the stream overload: the V1-A self-check runs in-process against a
probe type that is ALREADY loaded in the test CLI's AppDomain (in `TestCases.dll`).
Re-loading `TestCases.dll` into a fresh AppDomain would duplicate / re-initialize
state; instead the self-check hands the driver the existing module + AppDomain +
the probe type set, and the driver compiles + serializes that subset. The
file-path overload (used by the literal CLI) wraps the stream overload: it builds
a fresh AppDomain, Cecil-loads the input + refs, and calls the stream overload
with `inputModule = the loaded input module`.

**(b) `ILRuntimeNeoCompiler/`** (NEW project, SEPARATE assembly).
- `ILRuntimeNeoCompiler.csproj` -- mirrors `PatchTool.csproj`: `OutputType=Exe`,
  `TargetFrameworks=net8.0;netcoreapp3.0`,
  `Configurations=Debug;Release;Debug_Neo;Release_Neo`, with
  `<DefineConstants>ENABLE_NEO_MODE</DefineConstants>` on the `Debug_Neo` +
  `Release_Neo` property groups (copied from `ILRuntimeTestCLI.csproj`), a
  `<ProjectReference Include="..\ILRuntime\ILRuntime.csproj" />`, and
  `<Reference Include="ILRuntime.Mono.Cecil">` + `ILRuntime.Mono.Cecil.Pdb` hint
  refs to `..\Dependencies\netstandard2.0\` (so the CLI can Cecil-load the input
  for the resolver search-directory setup, exactly as PatchTool does). NO NuGet
  parser dependency for V1 (raw positional args).
- `Program.cs` -- the thin CLI wrapper (see D5).

### D2. Program.cs flow (the thin wrapper)

```
Main(args):
  parse positional: input, output, [refs...]
  if args.Length < 2: print usage; return 1
  if !File.Exists(input): print "input not found"; return 1
  open outputStream = File.Create(output)   // overwrite
  var driver = new NeoCompiler();
  NeoCompilerResult result;
  try { result = driver.Compile(input, refs, outputStream); }
  catch (FatalException ex) {               // input-load / serializer fatal
      stderr("FATAL: " + ex); return 1;
  }
  stdout($"compiled {result.MethodsCompiled} methods, {result.TemplatesCaptured} templates, {result.TypesCompiled} types");
  foreach (skip in result.Skipped) stderr($"  SKIP {skip.MethodDisplay}: {skip.ExceptionType}: {skip.Message}");
  return result.IsComplete ? 0 : 2;         // 0 = complete, 2 = partial
```

No runtime/JIT/serializer logic in `Program.cs` -- only arg parse, stream
management, report printing, exit-code mapping. This keeps the tool project
trivial and the compile logic in the ILRuntime assembly (where internals live).

### D3. NeoCompiler.Compile -- the bulk-compile driver

```
Compile(inputStream, refStreams, inputModule, appdomain, outputStream):
  result = new NeoCompilerResult();

  // 1. Enumerate ILTypes whose TypeDefinition.Module == inputModule
  //    (LoadedTypes holds input + any loaded refs; filter to the input only).
  var inputTypes = [t for t in appdomain.LoadedTypes.Values
                    where t is ILType it && it.TypeDefinition.Module == inputModule];

  // 2. Partition each type's methods into non-generic (-> methods[]) and
  //    generic-DEFINITION (-> template capture). Generic INSTANCES are never
  //    enumerated (they are created on demand at runtime); only definitions.
  var methods = new List<ILMethod>();
  var templates = new List<GenericMethodTemplate>();
  foreach (var type in inputTypes) {
      foreach (var m in type.GetMethods().Concat(type.GetConstructors())) {
          var ilm = m as ILMethod;
          if (ilm == null) continue;                       // CLRMethod -- not IL, skip
          if (ilm.GenericParameterCount > 0 && !ilm.IsGenericInstance) {
              // generic DEFINITION -> capture template (D4). On failure, skip-record.
              var tpl = CaptureTemplate(appdomain, type, ilm, result);
              if (tpl != null) templates.Add(tpl);
          } else {
              methods.Add(ilm);                            // non-generic -> MethodDefTable
          }
      }
  }

  // 3. Serialize. NeoAssemblyWriter.Write calls CompileFresh per method internally
  //    (the per-occurrence Neo JIT). Per-method compile failures inside Write are
  //    caught by a try/except around EACH method's CompileFresh -- see D6 (the
  //    writer today does NOT catch; the driver must drive compile-per-method with
  //    its own catch to honor skip-with-warning). [Refined in D6.]
  new NeoAssemblyWriter().Write(inputTypes.ToArray(), methods.ToArray(),
                                templates.ToArray(), outputStream);

  result.TypesCompiled = inputTypes.Count;
  result.MethodsCompiled = methods.Count;
  result.TemplatesCaptured = templates.Count;
  return result;
```

The input-module filter (`it.TypeDefinition.Module == inputModule`) is what keeps
ref-assembly types OUT of the emitted `.neo` even when their assemblies were
`LoadAssembly`-ed for resolution.

### D4. Template capture (synthesized capture-eligible instantiation)

```
CaptureTemplate(appdomain, declaringType, definition, result):
  if (definition.GenericMethodTemplateCache != null)
      return definition.GenericMethodTemplateCache;        // already captured
  // Synthesize one primitive per generic parameter (int is universally
  // capture-eligible: IsCaptureEligible only rejects non-primitive value types).
  var captureArgs = new IType[definition.GenericParameterCount];
  for (int i = 0; i < captureArgs.Length; i++) captureArgs[i] = appdomain.IntType;
  ILMethod capInstance;
  try { capInstance = definition.MakeGenericMethod(captureArgs) as ILMethod; }
  catch (Exception ex) { result.Skipped.Add(MakeSkip(definition, ex)); return null; }
  try { _ = capInstance.BodyRegister; }                    // triggers InitCodeBody -> capture
  catch (Exception ex) { result.Skipped.Add(MakeSkip(definition, ex)); return null; }
  return definition.GenericMethodTemplateCache;            // cached by StoreGenericTemplate
```

This is the production (non-DEBUG) equivalent of `ForceBuildTemplate`. It reaches
the internal `BodyRegister` + `GenericMethodTemplateCache` directly (in-assembly).
It does NOT depend on the DEBUG-gated `ForceBuildTemplate`, so it works in
`Release_Neo`. No Step-22 code change.

NOTE on multi-arg / constrained generics: `int`-per-param is capture-eligible
regardless of `class`/`struct` constraints (capture only needs the T-INVARIANT
front-half; `IsCaptureEligible` checks `IsValueType && !IsPrimitive`, which `int`
fails-to-reject). A generic param constrained to a specific reference type still
captures via `int` (the front-half is T-invariant for any ref/primitive arg-set).
A generic param constrained to a NON-primitive struct: `int` STILL captures
(struct-T is a CloneAndPatch-time concern, not a capture-time concern; the
template captures the open front-half). So `int`-per-param is universally correct.
The Step-22 V1 equivalence self-check (55/55) already proves CloneAndPatch from an
`int`-captured template produces the correct body for struct-T.

### D5. Argument surface + exit codes

Positional (see proposal D7): `ilrt_neoc <input.dll> <output.neo> [refs...]`.
Exit codes: `0` complete; `2` partial (skipped methods, `.neo` still written); `1`
fatal (no `.neo`).

### D6. Per-method error handling -- the skip-with-warning boundary

`NeoAssemblyWriter.Write` today calls `CompileFresh` per method WITHOUT a
per-method try/catch -- a single NIE would propagate. To honor skip-with-warning,
the driver must compile EACH method with its own catch BEFORE handing the survivor
set to `Write`. Two implementation options:

- **(D6-preferred) Pre-compile-then-write.** The driver force-compiles each
  non-generic `ILMethod` itself (read `ilm.BodyRegister` -- already compiled by
  the capture path for generic-adjacent methods; cheap / idempotent for the rest)
  inside a per-method try/catch, records skips, and hands ONLY the survivor set
  to `Write`. `Write` then `CompileFresh`-es them again (re-compile is
  deterministic and cheap -- it is what `NeoStep23RoundtripCheck` relies on for
  its body-equality cross-check at line 240). This keeps `NeoAssemblyWriter.Write`
  UNCHANGED (no Step-23 modification) and gives the driver full control of the
  skip set.
- (D6-alt, rejected) Wrap a per-method catch INSIDE `NeoAssemblyWriter.Write`.
  Rejected: it modifies Step-23 code (violates the additive/no-Step-23-change
  binding) and entangles the serializer with error policy.

Decision: **D6-preferred**. The driver force-compiles each method (catch per
method), records skips, and passes survivors to `Write`. `Write` is unmodified.

Edge: a generic definition's template CAPTURE may itself throw (e.g. the body
references an unimplemented op). D4's CaptureTemplate already try/catches the
capture and records a skip; the definition then has no template (Step-25 runtime
would fall back to per-occurrence JIT -- the additive contract holds).

### D7. Reference-assembly handling

- Build a Cecil `DefaultAssemblyResolver`; `AddSearchDirectory` for the input's
  own directory and each existing ref path's directory. Read the input module
  with that resolver so Cecil resolves the module's `AssemblyReferences`.
- Register the input with the AppDomain via `LoadAssembly(inputStream)`.
- For each ref path that is an IL hotfix assembly (heuristic V1: attempt
  `LoadAssembly(refStream)` in a try/catch; if it loads ILTypes, they enter
  `LoadedTypes` for resolution; CLR/BCL refs that the AppDomain already resolves
  via CLR fallback need not be loaded). Ref types are compiled-against but
  filtered out of the output by the input-module filter (D3 step 1).
- V1's small test assembly references only the BCL, so this is sufficient.
  Robust IL-vs-CLR classification + cross-AppDomain hash re-resolution are Step 25.

## Risks / Trade-offs

- **[Two compiles per method (D6-preferred)]** -- the driver force-compiles for
  the skip check, then `Write` `CompileFresh`-es again. Cost: 2x JIT per method
  at COMPILE time (a tool-time cost, not runtime). Benefit: `NeoAssemblyWriter`
  stays unchanged (no Step-23 modification) + full skip-set control. Acceptable
  for V1 (AOT compile-time cost is not the perf gate -- Step 26 is runtime perf).
  If compile-time cost matters for huge assemblies later, a Step-24 follow-up can
  thread the already-compiled frames into `Write` (a `Write` overload that accepts
  pre-built `NeoMethodDefRecord[]`); deferred.
- **[Generic-definition template capture for methods referencing unimplemented
  ops]** -- CaptureTemplate try/catches and records a skip; the definition ships
  with no template. The Step-25 runtime falls back to per-occurrence JIT for it
  (the additive contract: the template path is an optimization; the fallback
  stays). Acceptable.
- **[Open-definition compile corruption]** -- the driver MUST NOT pass a generic
  definition to `methods[]` (D3 enforces the split). This is the single most
  important correctness rule; the V1-A roundtrip asserts the emitted
  `MethodDefTable` contains NO generic definitions and the `TemplateTable`
  contains every generic definition in the probe.
- **[Cecil `module.GetTypes()` vs `LoadedTypes`]** -- the driver enumerates
  `appdomain.LoadedTypes` (already ILType-wrapped by `InitializeFromModule`),
  not Cecil's `module.GetTypes()` directly. This ensures every emitted type has a
  fully-initialized `ILType` (field layout, VTable, interface map) as the
  serializer expects. The input-module filter uses the Cecil `Module` equality.
- **[Ref-assembly over-loading]** -- `LoadAssembly`-ing a CLR ref (e.g.
  mscorlib.dll) would wrongly ILType-wrap its types. V1 heuristic: only
  `LoadAssembly` a ref if a probe `appdomain.GetType(refTypeName)` returns null
  (i.e. the AppDomain genuinely cannot resolve it); BCL refs resolve via CLR
  fallback and are skipped. Step 25 hardens this.

## Migration Plan

N/A -- additive. New project + new `public` class + new test hook + new probe
type. Rollback = delete `ILRuntimeNeoCompiler/`, delete
`Runtime/NeoAOT/NeoCompiler.cs`, delete the `NeoStep24CliRoundtripCheck` + its CLI
hook + the probe type. No shared code depends on any of them.

## Open Questions

- **Q1 (D6 vs a `Write` overload):** preferred D6-preferred (two compiles, no
  Step-23 change). If the review judges the 2x tool-time compile unacceptable for
  the V1-A self-check's probe scale, the fallback is a new `NeoAssemblyWriter.Write`
  overload that accepts pre-built records -- a small additive Step-23 change.
  Default: D6-preferred. Confirm at apply.
- **Q2 (V1-B literal-CLI smoke scope):** include the V1-B process-launch smoke in
  Step 24, or defer it to Step 25's loader test (which will invoke the CLI for
  real)? Default: include a MINIMAL V1-B (one tiny DLL, exit-code + magic-byte
  check) since it exercises arg-parse + file I/O the self-check does not; but
  V1-A is the gate, so V1-B may be timeboxed. Confirm at apply.
- **Q3 (probe type placement):** dedicated `NeoStep24CliProbe` in `TestCases/`
  (default) vs. a new tiny project. Default: in `TestCases/` (no new build
  wiring). Confirm at apply.
