# Proposal: neo-step24-ilrt-neoc

## What

Ship the `ilrt_neoc` standalone precompile CLI -- the Step-24 integration layer
of the Neo AOT toolchain (`object-model-neo-design.md` section 8.3). The CLI
Cecil-loads an input assembly (+ reference assemblies for type resolution), runs
the Neo JIT (`JITCompiler.Compile`) over EVERY IL method in bulk, captures a
generic-method template for every generic method definition (Step 22), serializes
the compiled frames + type metadata + templates via Step 23's `NeoAssemblyWriter`,
and writes a standalone `.neo` file. It is the command-line surface that drives
the already-shipped Step 22 (templates) + Step 23 (serializer) pieces end-to-end.

Capability: `neo-optimizer`. The change is **additive + Neo-only + Legacy-neutral**:
it adds a NEW tool + one new public driver class; it does NOT modify the JIT, the
runtime, `ExecuteNeo`, the optimizer, the Step-22 template mechanism, or the
Step-23 serializer behavior.

## Why

Steps 22 + 23 shipped the in-memory template mechanism and the `.neo` serialization
format, but they are exercised only by host-side self-checks driven through the
test CLI's `TestCases.dll`. There is no command-line path that takes an arbitrary
input assembly and produces a `.neo`. Step 24 is that path. It is the integration
gate that proves the pieces compose correctly on a real assembly (not just the
curated probe set), and it produces the artifact the Step-25 runtime loader will
consume. The runtime `.neo` LOADER + Cecil-decoupling + functional
deserialize-to-`ExecuteNeo` (V2) are Step 25; perf is Step 26.

## Key decisions (grounded in the actual APIs)

### D1. CLI structure: a NEW standalone console-app project (NOT a mode of an existing CLI)

Create a new `ILRuntimeNeoCompiler/` project (`ILRuntimeNeoCompiler.csproj` +
`Program.cs`), mirroring `PatchTool/` (the existing standalone-tool structural
reference: `OutputType=Exe`, multi-target `net8.0;netcoreapp3.0`, the four
`Debug/Release/Debug_Neo/Release_Neo` configurations with
`DefineConstants=ENABLE_NEO_MODE` on the `*_Neo` configs, a `ProjectReference` to
`ILRuntime.csproj`, and `Reference` hints to `ILRuntime.Mono.Cecil` + `.Pdb`).

Rationale:
- `object-model-neo-design.md` section 8.3 states `ilrt_neoc` is a tool SEPARATE
  from `PatchTool` (PatchTool is HybridPatch AOP; `ilrt_neoc` is Neo precompile).
- A separate project matches the PatchTool precedent and keeps the tool concern
  isolated from the test runner (`ILRuntimeTestCLI`).
- The `ILRuntime.sln` CANNOT build whole (the VS2022 debugger VSIX, net472,
  cannot consume this branch's `netstandard2.1`, NU1201 -- handoff section 1).
  A new project MUST build standalone via `dotnet build
  ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj -c Debug_Neo`. It is NOT added
  to the sln.
- A mode/subcommand on `ILRuntimeTestCLI` was rejected: it would couple the AOT
  compiler to the test harness and bloat the test runner's arg surface; the design
  explicitly wants a separate tool.

### D2. The compile driver is a PUBLIC class INSIDE the ILRuntime assembly; the CLI is a thin wrapper

The NeoAOT types (`NeoAssemblyWriter`, `NeoAssemblyReader`, `NeoAssemblyModel`,
`NeoRefTableBuilder`) and the force-compile / template entries
(`ILMethod.BodyRegister`, `ILMethod.GenericMethodTemplateCache`,
`GenericMethodTemplateOps.ForceBuildTemplate`) are ALL `internal`, and there is NO
source-level `InternalsVisibleTo` (verified). A separate-assembly CLI therefore
CANNOT reach them directly.

Decision: add ONE new `public` class, `NeoCompiler`, under
`ILRuntime/Runtime/NeoAOT/` (`#if ENABLE_NEO_MODE`, so Legacy compiles it out).
`NeoCompiler` lives in the ILRuntime assembly, so it reaches every internal type
it needs; it is the single public seam. Its public surface is one entry:

```csharp
public sealed class NeoCompiler
{
    public NeoCompilerResult Compile(string inputAssemblyPath,
        IReadOnlyList<string> referenceAssemblyPaths, Stream outputStream);
}
public sealed class NeoCompilerResult
{
    public int TypesCompiled;
    public int MethodsCompiled;
    public int TemplatesCaptured;
    public List<string> SkippedMethods;   // name + skip reason (e.g. NIE on an unimplemented op)
    public bool IsComplete => SkippedMethods.Count == 0;
}
```

The CLI's `Program.cs` (in the new project) is a THIN wrapper: parse args, open
streams, call `NeoCompiler.Compile(...)`, print the report to stdout/stderr, set
the exit code. NO runtime/JIT/serializer/template knowledge leaks into the tool
project. This mirrors how the (in-assembly) `NeoStep23RoundtripCheck` reaches the
internal NeoAOT types today.

### D3. Bulk method enumeration + force-compile (grounded in AppDomain + ILType)

- **Load + type enumeration.** `appdomain.LoadAssembly(inputStream)` calls
  `InitializeFromModule(module)` (`AppDomain.cs:669`), which iterates Cecil
  `module.GetTypes()` -- this FLATTENS nested types -- and creates + registers
  (`AddType`) an `ILType` for every type. So after `LoadAssembly`,
  `appdomain.LoadedTypes` holds EVERY type in the assembly (input + any loaded
  refs). The driver filters the OUTPUT to types whose `TypeDefinition.Module ==
  inputModule` (so ref-assembly types are compiled-against but NOT emitted).
- **Method enumeration per type.** `ILType.GetMethods()` (`ILType.cs:1316`,
  returns `List<IMethod>` of all non-ctor methods) PLUS `ILType.GetConstructors()`
  (instance ctors + static `.cctor`). Both are enumerated.
- **Force-compile WITHOUT invoking.** Reading `ILMethod.BodyRegister`
  (`ILMethod.cs:389`) triggers `InitCodeBody(true)` -> `JITCompiler.Compile(addr,
  ref frame)`, which compiles the method body without running it. The driver does
  NOT need to pre-compile non-generic methods: Step 23's `NeoAssemblyWriter.Write`
  calls `CompileFresh` (which wraps `new JITCompiler(...).Compile(...)`) internally
  per method in its `methods[]` array. So the driver collects the `ILMethod[]` and
  hands them to `Write`; `Write` compiles.

### D4. Generic-template capture WITHOUT a call site: SYNTHESIZE a capture-eligible instantiation

Step 22 captures a template "from the first concrete capture-eligible
instantiation" (`GenericMethodTemplate.cs:383`, `StoreFromCapture`), triggered
inside `InitCodeBody` when `IsCaptureEligible(genericArguments)` is true (every
generic arg a reference type or a primitive -> the front-half instruction stream
is T-invariant). A generic method definition with NO call site in the assembly has
no instantiation, hence no capture.

Decision: the driver SYNTHESIZES a capture-eligible instantiation per generic
method definition -- one primitive (`AppDomain.IntType`) per generic parameter --
via `definition.MakeGenericMethod(IType[])` (`ILMethod.cs:1151`, public), then
READS the instance's `BodyRegister` to trigger `InitCodeBody` -> the capture hook
-> `StoreGenericTemplate` caches the template on
`definition.GenericMethodTemplateCache`. This is EXACTLY what the existing
`GenericMethodTemplateOps.ForceBuildTemplate` (`GenericMethodTemplate.cs:626`)
does. `int`-per-parameter is universally capture-eligible (`IsCaptureEligible`
only rejects non-primitive value types; `int` passes regardless of `class`/`struct`
constraints, because the capture only needs the T-INVARIANT front-half, and
struct-T is handled at runtime CloneAndPatch via the Initobj-prefix rebuild).

Step 22 **REJECTED** compiling the open definition directly (it corrupts shared
AppDomain caches -- the documented quirk, reaffirmed in
`NeoStep23RoundtripCheck` line 233-243: "compiling an open definition is
non-deterministic"). Synthesis is therefore the ONLY correct path. Confirmed.

Implementation note: the driver replicates the 3-line capture inline (it is
in-assembly, so it reaches the internal `BodyRegister` + `GenericMethodTemplateCache`
directly). It does NOT call the DEBUG-gated `ForceBuildTemplate` (`#if DEBUG &&
ENABLE_NEO_MODE`), so the driver is production-usable in `Release_Neo` too. This
means **NO Step-22 code change is required** (the capture path it exercises is the
existing production `InitCodeBody` capture hook; only a NEW caller is added).

CRITICAL correctness rule: a generic method DEFINITION (GenericParameterCount > 0
&& !IsGenericInstance) MUST go to the `templates[]` array (via capture), NEVER to
`methods[]`. Passing a generic definition to `NeoAssemblyWriter.Write`'s
`methods[]` would make `CompileFresh` compile the open definition (non-deterministic
cache corruption). The driver enforces this split.

### D5. Error reporting: skip-with-warning + partial-failure exit code

A method whose compile throws (most commonly a `NotImplementedException` for an
unimplemented op -- Step 19+ delegate/async/etc. -- but also any Cecil-resolution
or JIT failure) MUST NOT abort the whole-assembly compile. The driver catches
per-method, records `{methodName, exceptionType, message}` in
`NeoCompilerResult.SkippedMethods`, logs a warning to stderr, OMITS the method
from the emitted `MethodDefTable`, and continues. The `.neo` is still written
(the compiled subset). The CLI's exit code reflects partial failure:
- `0` -- every method + template compiled (`IsComplete`).
- `2` -- one or more methods skipped (partial `.neo` written; the skipped set is
  reported to stderr + a summary line to stdout).
- `1` -- a fatal error prevented any `.neo` output (bad args, input file missing,
  the input assembly failed to load, the serializer threw).

Rationale: an AOT precompile of a large assembly will routinely encounter
not-yet-implemented ops (the Neo overhaul is in progress); fail-fast would make
the tool useless until every op lands. Report-and-skip lets the usable subset ship
and makes the gap legible. (Whether the skipped set is itself serialized into the
`.neo` as a "skipped methods" table for the Step-25 loader is DEFERRED; V1 reports
to stderr + omits.)

### D6. Reference-assembly resolution: Cecil resolver + AppDomain fallback (V1)

Mirror PatchTool's structural approach: a Cecil `DefaultAssemblyResolver` with
each reference assembly's directory added via `AddSearchDirectory`, so Cecil can
resolve the input module's `AssemblyReferences` when reading. The input assembly
is registered with the AppDomain via `LoadAssembly`. During `JITCompiler.Compile`,
the JIT resolves TypeRefs/MethodRefs via `appdomain.GetType(typeRef, ...)`;
mscorlib/System types resolve as CLR types via the AppDomain's existing fallback,
which covers the BCL references a typical V1 test assembly has.

Cross-IL-assembly references (the input references another hotfix IL assembly):
the driver `LoadAssembly`-es the IL ref assembly too (its types enter
`LoadedTypes` for resolution) but filters them out of the emitted output by
module. Robust IL-vs-CLR reference classification + cross-AppDomain token-hash
re-resolution are Step-25 concerns (the loader needs the same machinery); V1's
small test assembly references only the BCL, so this is sufficient and faithful.

### D7. Argument surface

Positional (mirrors `ILRuntimeTestCLI`'s simple positional convention; no NuGet
dependency for V1):

```
ilrt_neoc <input.dll> <output.neo> [reference-assembly paths...]
```

- `<input.dll>` -- the IL assembly to precompile (required).
- `<output.neo>` -- the output `.neo` path (required; created/overwritten).
- `[reference-assembly paths...]` -- optional vararg paths to reference assemblies
  (IL hotfix refs + any CLR refs whose directories are not already on the Cecil
  resolver search path). Each existing path's directory is added to the Cecil
  resolver; IL refs are also `LoadAssembly`-ed into the AppDomain.

## VERIFICATION (V1 CLI roundtrip -- the load-bearing gate)

Step 24 is the integration surface; verification = an END-TO-END roundtrip of the
CLI wiring. Two flavors, the first load-bearing:

- **(V1-A, load-bearing) Host-side CLI-roundtrip self-check.** A new
  `NeoStep24CliRoundtripCheck.Run(appdomain)` (mirrors `NeoStep23RoundtripCheck`),
  driven via the existing CLI special-mode hook (`ILRuntimeTestCLI/Program.cs`:
  `if (nameFilter == "NeoStep24CliRoundtrip")`). It builds a small dedicated probe
  TYPE (a few non-generic methods + a generic method + a try/catch method + a
  struct-local method -- a handful, NOT the large `TestCases.dll`, to keep the
  compile fast), then calls the SAME `NeoCompiler` driver the CLI uses to produce
  a `.neo` in a `MemoryStream`, READS it back with `NeoAssemblyReader.Read`, and
  asserts the roundtrip EQUALS the in-memory compile -- REUSING Step 23's
  comparators (`OpCodeRsEqual`, `MethodDefsEqual`, `TemplatesEqual`,
  `TypeDefsEqual`). This proves the driver wiring (enumeration + force-compile +
  template capture + serialize + the input-module filter) is correct
  end-to-end, in-process, deterministically, fast. The probe type lives in
  `TestCases/` (compiled into `TestCases.dll`) so the existing harness loads it.
- **(V1-B, smoke) Literal CLI invocation.** A second check that shells out to the
  built `ILRuntimeNeoCompiler.exe` on a tiny DLL, checks the exit code, and reads
  the produced `out.neo` with `NeoAssemblyReader`. This confirms arg-parse + file
  I/O + the exit-code contract. Lighter weight than V1-A is acceptable; it is the
  smoke, not the gate.

V2 functional (deserialize -> `ExecuteNeo`) is Step 25.

### Regression gate (Step 24 is additive)

- NeoStep smoke **205/205**.
- NeoStep23Roundtrip **15/15** (Step 24 must not change the serializer).
- NeoStep22SelfCheck **55/55** (Step 24 must not change the template mechanism).
- NeoOptHardening 24/24, NeoStep20 9/9.
- V1-A CLI roundtrip: all probe cells PASS.
- Legacy-neutral: plain-`Debug` + `useRegister=true` NeoStep-filter run shows the
  SAME pre-existing Legacy failure set with and without the change (the new
  `NeoCompiler` class + the new project compile out of plain `Debug`).

## Scope + non-goals

**In scope:**
- The `ILRuntimeNeoCompiler/` project (`Program.cs` thin CLI wrapper +
  `ILRuntimeNeoCompiler.csproj` mirroring PatchTool).
- The `public NeoCompiler` driver class + `NeoCompilerResult`
  (`ILRuntime/Runtime/NeoAOT/NeoCompiler.cs`, `#if ENABLE_NEO_MODE`): Cecil load
  via the AppDomain, type/method enumeration, force-compile delegation to
  `NeoAssemblyWriter.Write`, template capture via synthesized instantiation, the
  input-module output filter, ref-assembly handling, per-method error reporting.
- The V1-A host-side roundtrip self-check (`NeoStep24CliRoundtripCheck`) + the CLI
  hook + a small dedicated probe type in `TestCases/`.
- The V1-B literal-CLI smoke (optional; if timeboxed, V1-A is the gate).

**Non-goals (defer):**
- The runtime `.neo` LOADER + ILType/ILMethod Cecil-decoupling dual-path + V2
  functional deserialize-to-`ExecuteNeo` (Step 25).
- Cross-AppDomain token-hash re-resolution + CLR-by-aqname indexing + static
  `.cctor` seeding (Step 25; the `.neo` reference tables are already serialized
  by Step 23 so Step 25 can build the maps).
- Perf benchmarks (Step 26).
- A "skipped methods" table INSIDE the `.neo` (V1 reports to stderr; the loader
  contract is Step 25).
- Robust IL-vs-CLR reference-assembly classification beyond BCL + IL-ref
  `LoadAssembly` (Step 25).

**Non-goal (binding):** do NOT change JIT / runtime / `ExecuteNeo` / optimizer /
Step-22 template / Step-23 serializer behavior. Step 24 is additive; the sole new
runtime-assembly code is the `NeoCompiler` class (a new caller of existing,
unchanged internals). Legacy-neutral.

## Open question

- **OQ1 (probe placement):** the V1-A probe type -- add a SMALL dedicated
  `NeoStep24CliProbe` class to `TestCases/` (a handful of methods, compiled into
  the existing `TestCases.dll`), vs. a brand-new tiny project that produces its
  own small DLL. Default: the dedicated class in `TestCases/` (zero new build
  wiring; the harness already loads `TestCases.dll`; the driver filters to the
  probe type's module so the roundtrip is over a small set). Confirm at apply
  (the roundtrip must stay sub-second; `TestCases.dll` is large but the probe
  TYPE is small, and the driver compiles only the probe type's methods for V1-A).
