# Planning Context — neo-step24-ilrt-neoc (LEAD seed)

> SEED for the planner. Read THIS FIRST, then the AOT design + Step 23, then
> research only what is missing. APPEND durable findings.

## User intent

Continue the Neo AOT toolchain. This child = **Step 24**: the `ilrt_neoc` standalone
precompile CLI -- Cecil-loads an input DLL, runs `JITCompiler.Compile()` per method
(Neo), generates generic templates, calls Step 23's `NeoAssemblyWriter`, outputs a
`.neo` file. Capability `neo-optimizer`. Full autonomy; LEAD commits + pushes.

## What Step 24 delivers (from `.trae/documents/object-model-neo-design.md` §8.3)

The §8.3 tool flow:
```
ilrt_neoc:
  Cecil-load the input assembly (+ ref assemblies for type resolution)
  -> for EACH method: JITCompiler.Compile() (Neo) -> OpCodeR[] + CompiledFrame
  -> generic methods: templateBody + patches (Step 22)
  -> serialize CompiledFrame + type metadata (Step 23 NeoAssemblyWriter)
  -> output the .neo file
```
Step 23 (the serializer) + Step 22 (templates) are DONE. Step 24 is the CLI
INTEGRATION that drives them end-to-end from a command line.

## Current state (the pieces Step 24 wires together -- all DONE)

- **`JITCompiler.Compile`** (`ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`)
  -- the Neo JIT entry. Today it runs ON-DEMAND (a method compiles when first
  invoked). Step 24 needs BULK compile: enumerate every method in the assembly +
  compile each upfront. Find how the runtime enumerates an assembly's methods
  (the AppDomain / ILType method-enumeration API) + how to force-compile a method
  without invoking it.
- **Step 22 `GenericMethodTemplate`** (`GenericMethodTemplate.cs`) -- the template
  capture is triggered by the first instantiation. Step 24 must ensure the CLI's
  bulk compile captures templates (it may need to instantiate representative
  generic methods, OR the template is captured from the definition -- re-read
  Step 22's capture mechanism: "captured from the first concrete capture-eligible
  instantiation"). Determine how the CLI drives template capture for a generic
  method that has no call site in the assembly.
- **Step 23 `NeoAssemblyWriter`** (`ILRuntime/Runtime/NeoAOT/NeoAssemblyWriter.cs`)
  -- the serializer to call. Takes the compiled bodies + type metadata + templates
  -> writes a `.neo`.
- **`PatchTool/`** (`PatchTool.csproj`) -- a STANDALONE tool .csproj (HybridPatch
  AOP). Use it as the STRUCTURAL reference for `ilrt_neoc` (a separate console-app
  project: arg parse, Cecil load, ref-assembly resolution, output). The design says
  `ilrt_neoc` is a SEPARATE tool from PatchTool.
- **`ILRuntimeTestCLI/`** -- another CLI reference (arg parse pattern, the `-f net8.0`
  multi-targeting).

## KEY design questions for the planner to dump-gate + decide

1. **New project vs a mode of an existing CLI?** The design says a SEPARATE tool
   (`ilrt_neoc`). Create a new `ILRuntimeNeoCompiler/` (or `ilrt_neoc/`) console-app
   project, OR add a `neoc` subcommand/mode to ILRuntimeTestCLI. Decide (a separate
   project matches the design + PatchTool precedent; a mode avoids a new .csproj
   build-wiring). The sln CANNOT build whole (the VS2022 debugger VSIX NU1201) --
   a new project must build standalone (`dotnet build <new>.csproj`).
2. **Bulk method enumeration + force-compile.** How does the CLI enumerate EVERY
   method in the assembly (including nested types, generic methods, ctors, static
   ctors) + force-compile each without invoking? Find the AppDomain/ILType method-
   enumeration API + a "compile but don't run" entry (likely `ILMethod.BodyRegister`
   getter / `Prewarm` / a Compile call).
3. **Generic-template capture without a call site.** Step 22 captures a template
   from the first concrete instantiation. A generic method with no call site in the
   assembly has no instantiation. Determine: does the CLI synthesize a capture-
   eligible instantiation (ref/primitive T) to drive capture, OR does it capture
   from the definition (Step 22 rejected definition-compile -- it corrupts caches;
   so the CLI must synthesize an instantiation). Decide + justify.
4. **Ref-assembly resolution.** The CLI Cecil-loads the input DLL + its references
   (for type resolution). Mirror PatchTool's ref-assembly handling. The AppDomain
   needs the ref assemblies registered to resolve TypeRefs/MethodRefs during compile.
5. **Output.** Write the `.neo` to the output path (arg). Error reporting (a method
   that fails to compile -- NIE for an unimplemented op -- should be reported, not
   crash the whole compile; decide: skip-with-warning vs fail-fast).

## VERIFICATION (CLI integration test -- the load-bearing gate)

Step 24 is the integration surface; verification = an END-TO-END CLI test:
- **(V1) CLI roundtrip:** run `ilrt_neoc <test.dll> <out.neo> <refs>` on a small
  test assembly (the existing TestCases.dll, or a dedicated small one), produce
  `out.neo`, then READ `out.neo` with Step 23's `NeoAssemblyReader` + assert it
  roundtrips (the deserialized model matches the in-memory compile -- reuse Step 23's
  comparators). This proves the CLI wiring (arg parse + Cecil load + bulk compile +
  template capture + serialize) is correct end-to-end.
- **(V2) Functional execute:** the actual "load .neo + ExecuteNeo" is Step 25. Step
  24 ships the CLI + V1; V2 lands at Step 25.
- **Regression gate:** NeoStep smoke 205/205 + NeoStep23Roundtrip 15/15 + NeoStep22SelfCheck
  55/55 (Step 24 is a new tool; it must NOT change the runtime/JIT/serializer
  behavior -- additive).

## Scope + non-goals

- **In scope:** the `ilrt_neoc` CLI (arg parse, Cecil load, ref assemblies, bulk
  JIT compile, template capture, Step 23 serialize, .neo output, error reporting);
  the V1 CLI roundtrip test.
- **Non-goals (defer):** the runtime `.neo` LOADER + Cecil-decoupling + V2 functional
  (Step 25); perf benchmarks (Step 26). Step 24 ships the compiler CLI + V1, NOT the
  loader.
- **Non-goal:** do NOT change JIT/runtime/Step-22/Step-23 behavior (additive). Confirm
  Legacy-neutral (the CLI is Neo-only; Legacy unaffected).

## Probe BEFORE designing (binding)

Ground the CLI in the ACTUAL APIs: how does the runtime enumerate an assembly's
methods + force-compile? Read `JITCompiler.Compile`, `ILMethod.BodyRegister`/
`Prewarm`, the AppDomain method/type enumeration. Read `PatchTool/Program.cs` for the
CLI structure (arg parse + Cecil load + ref assemblies). Do NOT design the CLI from
§8.3 alone.

## Build + test (CRITICAL)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo   # the runtime (the CLI links it)
dotnet build <new-neoc-project>.csproj -c Debug_Neo                  # the new CLI (if a new project)
dotnet build TestCases/TestCases.csproj -c Debug
```
- Build CLI with `Debug_Neo`; NEVER TestCases with `Debug_Neo`.
- A new project must build STANDALONE (the sln can't build whole -- the VS2020 VSIX
  NU1201). `dotnet build <new>.csproj` only.
- NeoStep 205/205 + NeoStep23Roundtrip 15/15 + NeoStep22SelfCheck 55/55 = REGRESSION
  gate (Step 24 is additive).
- Build-cache gotcha: confirm the DLL rebuilt after an edit.

## Codebase gotchas (full detail in handoff section 4)

- The sln CANNOT build whole (the VS2022 debugger VSIX NU1201); a new project builds
  standalone via `dotnet build <new>.csproj`.
- The CLI links the ILRuntime runtime (which is `#if ENABLE_NEO_MODE`-gated for Neo) --
  build the CLI with `Debug_Neo` so the Neo paths are active.
- `OpCodeR`/CompiledFrame/GenericMethodTemplate/NeoAssemblyWriter are all Neo-only.
- Test harness is NOT xUnit; the V1 CLI test is a `public static` method OR a CLI
  invocation the test harness drives.
- Write tool corrupts ~0.5% of CJK; author ASCII-primary.
- A test taking >10s usually = an interpreter infinite loop -- but the CLI compile of
  a LARGE assembly (TestCases.dll has many methods) could legitimately take time;
  scope the V1 test to a SMALL assembly to keep it fast.

## Likely new files / fix sites (dump-locked)

- A new project `ILRuntimeNeoCompiler/` (or `ilrt_neoc/`): `Program.cs` (arg parse +
  Cecil load + bulk compile + serialize + output), `NeoCompiler.cs` (the bulk-compile
  driver: enumerate methods + force-compile + template-capture), `*.csproj`.
- Possibly a test assembly or a dedicated small DLL for the V1 roundtrip (TestCases.dll
  is large; a small dedicated one keeps the CLI test fast).
- A test hook to drive the V1 CLI roundtrip (mirror Step 23's NeoStep23RoundtripCheck,
  but invoking the CLI / the NeoCompiler driver).

## Regression risk: LOW.

Step 24 is a new tool (additive; no runtime/JIT/serializer change). The risk is "does
the CLI correctly wire the existing pieces?" (the V1 roundtrip gates this) + "does a
new project break the build?" (build it standalone). Gate: NeoStep 205/205 +
NeoStep23Roundtrip 15/15 + NeoStep22SelfCheck 55/55 + the V1 CLI roundtrip + Legacy-
neutral.

## Maintain this file

APPEND durable findings after propose (the CLI structure decision, the bulk-compile
API, the template-capture-without-call-site decision, the V1 roundtrip design). Do
NOT append chatter.

## Findings -- neo-step24-ilrt-neoc (propose, 2026-07-07)

Artifacts authored + openspec-valid: `proposal.md`, `design.md`,
`specs/neo-optimizer/spec.md` (3 ADDED Requirements), `tasks.md`. Capability
`neo-optimizer`. All grounded in the actual APIs on HEAD.

### CLI structure decision: NEW standalone project + a public in-assembly driver (D1/D2)

A new `ILRuntimeNeoCompiler/` console-app project (mirror `PatchTool.csproj`:
`OutputType=Exe`, `net8.0;netcoreapp3.0`, the 4 configs with `DefineConstants=
ENABLE_NEO_MODE` on `*_Neo`, `ProjectReference` to `ILRuntime.csproj`, Cecil
`Reference` hints). Builds STANDALONE (`dotnet build ILRuntimeNeoCompiler/
ILRuntimeNeoCompiler.csproj -c Debug_Neo`); NOT added to the sln (the sln can't
build whole, NU1201). NOT a mode of `ILRuntimeTestCLI` (the design wants a separate
tool; avoids coupling to the test harness).

VISIBILITY BOUNDARY (the key discovery): every NeoAOT type (`NeoAssemblyWriter`/
`Reader`/`Model`/`NeoRefTableBuilder`) + the force-compile/cache entries
(`ILMethod.BodyRegister`, `ILMethod.GenericMethodTemplateCache`,
`GenericMethodTemplateOps.ForceBuildTemplate`) is `internal`, and there is NO
source-level `InternalsVisibleTo` (the binary grep hits are inside compiled
dependency DLLs, not ILRuntime source). So a separate-assembly CLI CANNOT reach
them. Decision: ONE new `public sealed class NeoCompiler` inside the ILRuntime
assembly (`Runtime/NeoAOT/NeoCompiler.cs`, `#if ENABLE_NEO_MODE`) wraps all the
internals and exposes one public seam (`Compile(...)` -> `NeoCompilerResult` with
`TypesCompiled`/`MethodsCompiled`/`TemplatesCaptured`/`Skipped`/`IsComplete`).
`Program.cs` is a THIN wrapper (arg parse + streams + report + exit code). This
keeps ALL compile logic in-assembly (where internals are reachable) + the tool
project trivial, and means NO `InternalsVisibleTo` is needed.

### Bulk-compile API (grounded)

- `appdomain.LoadAssembly(stream)` -> `InitializeFromModule(module)` (AppDomain.cs:
  669) iterates Cecil `module.GetTypes()` (FLATTENS nested types) -> `AddType` an
  `ILType` per type. Post-load, `appdomain.LoadedTypes` holds EVERY type in the
  assembly. Output filter: `it.TypeDefinition.Module == inputModule` (ref-assembly
  types compiled-against but NOT emitted).
- Per type: `GetMethods()` (ILType.cs:1316) + `GetConstructors()` (instance ctors
  + static `.cctor`). Both enumerated.
- Force-compile WITHOUT invoke: read `ILMethod.BodyRegister` (ILMethod.cs:389) ->
  `InitCodeBody(true)` -> `JITCompiler.Compile`. `NeoAssemblyWriter.Write` ALSO
  `CompileFresh`-es each method internally (NeoAssemblyWriter.cs:909), so the
  driver need only collect + hand the survivor set to `Write`.

### Template-capture-without-call-site decision: SYNTHESIZE (D4) -- confirmed, no Step-22 change

Step 22 captures from the first capture-eligible instantiation
(`GenericMethodTemplate.cs:383` `StoreFromCapture`, triggered in `InitCodeBody`
when `IsCaptureEligible(genericArguments)` -- every arg ref/primitive). A generic
def with no call site has no instantiation. Decision: the driver SYNTHESIZES one
primitive (`AppDomain.IntType`) per generic param via `definition.MakeGenericMethod(
IType[])` (public), then reads the instance's `BodyRegister` to trigger the capture
-> `StoreGenericTemplate` caches on `definition.GenericMethodTemplateCache`.
`int`-per-param is UNIVERSALLY capture-eligible (`IsCaptureEligible` only rejects
non-primitive value types; struct-T is a CloneAndPatch-time concern, not capture).
This is exactly what the DEBUG-gated `GenericMethodTemplateOps.ForceBuildTemplate`
(GenericMethodTemplate.cs:626) does; the driver replicates the 3 lines inline so
it works in `Release_Neo` too -- NO Step-22 change, NO call to the DEBUG helper.
Step 22 REJECTED open-definition compile (corrupts caches) -- synthesis is the
only path. CONFIRMED.

CORRECTNESS RULE (binding): a generic definition MUST go to `templates[]` (via
capture), NEVER to `methods[]` (else `Write`'s `CompileFresh` compiles the open
def -> cache corruption).

### Error-reporting policy: skip-with-warning + partial-failure exit code (D5/D6)

A method that NIEs (or any compile throw) is caught per-method, recorded in
`NeoCompilerResult.Skipped` (method display name + exception type + message),
omitted from the `.neo`, and the compile CONTINUES. Exit codes: `0` complete; `2`
partial (skips, `.neo` still written); `1` fatal (no `.neo`). Implementation
(D6-preferred, binding): the driver force-compiles each non-generic method in its
own try/catch for the skip set, then hands survivors to `NeoAssemblyWriter.Write`
(which re-`CompileFresh`-es -- deterministic, as Step-23's self-check already
relies on). `NeoAssemblyWriter` is UNCHANGED (no Step-23 modification). 2x
tool-time compile is the accepted cost (AOT compile-time, not runtime; runtime
perf is Step 26).

### V1 roundtrip design: host-side self-check reusing Step-23 comparators (D7/V1-A)

V1-A (load-bearing): `NeoStep24CliRoundtripCheck.Run(appdomain)` (mirror
`NeoStep23RoundtripCheck`), via the existing `ILRuntimeTestCLI` hook (`if
(nameFilter == "NeoStep24CliRoundtrip")`). It builds a SMALL dedicated probe type
in `TestCases/` (`NeoStep24CliProbe`: a non-generic + a generic + a try/catch + a
struct-local + ctors), calls the SAME `NeoCompiler` driver (stream overload -- hands
the existing TestCases module + AppDomain, does NOT re-load TestCases.dll) to
produce a `.neo` in a `MemoryStream`, READs it with `NeoAssemblyReader.Read`, and
asserts equality REUSING Step 23's comparators (`OpCodeRsEqual`/`MethodDefsEqual`/
`TemplatesEqual`/`TypeDefsEqual`) + asserts the generic/non-generic split. V1-B
(literal CLI smoke) is optional/timeboxed; V2 functional is Step 25.

### Regression: additive

NeoStep 205/205 + NeoStep23Roundtrip 15/15 + NeoStep22SelfCheck 55/55 +
NeoOptHardening 24/24 + NeoStep20 9/9 + V1-A all-pass + Legacy-neutral (new code
compiles out of plain `Debug`). The ONLY new runtime-assembly code is the `public
NeoCompiler` class (a new caller of unchanged internals). No JIT/runtime/optimizer/
Step-22/Step-23-behavior change.

### Open questions for apply

- OQ1: D6-preferred (2x compile) vs a new `NeoAssemblyWriter.Write` overload
  accepting pre-built records (a small additive Step-23 change). Default: D6-
  preferred. Confirm with LEAD if 2x tool-time compile is judged unacceptable.
- OQ2: V1-B literal-CLI smoke in Step 24 or defer to Step 25. Default: minimal V1-B
  if time; V1-A is the gate.
- OQ3: probe type placement -- dedicated `NeoStep24CliProbe` in `TestCases/`
  (default, no new build wiring) vs a new tiny project.

## Findings -- neo-step24-ilrt-neoc (apply, 2026-07-07)

Step 24 SHIPPED: the `ilrt_neoc` standalone precompile CLI + the `public NeoCompiler`
driver. Neo 205/205, NeoStep23Roundtrip 15/15, NeoStep22SelfCheck 55/55, V1-A CLI
roundtrip 5/5, Legacy-neutral. All additive; no JIT/runtime/Step-22/Step-23 behavior
change.

### The NeoCompiler seam (`ILRuntime/Runtime/NeoAOT/NeoCompiler.cs`, `#if ENABLE_NEO_MODE`)

`public sealed class NeoCompiler` -- the SINGLE in-assembly seam (every NeoAOT type +
`ILMethod.BodyRegister` + `GenericMethodTemplateCache` is internal, no
`InternalsVisibleTo`, so this is the only way the tool project reaches them). Two
public overloads sharing one private `CompileCore`:
- `Compile(string inputAssemblyPath, IReadOnlyList<string> referenceAssemblyPaths,
  Stream outputStream)` -- the CLI entry: builds a `DefaultAssemblyResolver`
  (input dir + each ref dir on the search path), Cecil-reads the input module WITH
  the resolver (`ReaderParameters`), drives `appdomain.InitializeFromModule(module)`
  DIRECTLY (so the resolver-armed module is the one registered -- `LoadAssembly(Stream)`
  would re-read without the resolver), `LoadAssembly`-es each ref in a try/catch (V1
  heuristic: a CLR ref that fails to load as IL is skipped, CLR fallback resolves it),
  filters `LoadedTypes` to the input module, calls `CompileCore`. Fatals -> `NeoCompilerFatal`.
- `Compile(IReadOnlyList<ILType> inputTypes, Stream outputStream)` -- the V1-A host-side
  entry (resolves OQ1/OQ3's "small probe set" need; see below).

`CompileCore`: per type, `GetMethods()` + `GetConstructors()`; partition generic
DEFINITIONS (`GenericParameterCount > 0 && !IsGenericInstance`) -> template capture
(NEVER `methods[]`), non-generic -> force-compile (`_ = ilm.BodyRegister` in a
per-method try/catch; survivors to `methods[]`, throws to `Skipped`). Then
`new NeoAssemblyWriter().Write(inputTypes, methods, templates, outputStream)` --
UNCHANGED Step-23 serializer (D6-preferred: 2x tool-time compile accepted; Write
CompileFresh-es the survivors again, deterministically).

`CaptureTemplate`: replicates `ForceBuildTemplate`'s 3 lines INLINE (synthesize one
`appdomain.IntType` per generic param via `MakeGenericMethod`, read `BodyRegister` to
trigger the `InitCodeBody` capture hook, read back `GenericMethodTemplateCache`). Works
in `Release_Neo` (no dependency on the DEBUG-gated `ForceBuildTemplate`). No Step-22 change.

Result types: `NeoCompilerResult` (`TypesCompiled`/`MethodsCompiled`/`TemplatesCaptured`/
`Skipped: List<MethodSkip>`/`IsComplete`), `MethodSkip` (`MethodDisplay`/`ExceptionType`/
`Message`), `NeoCompilerFatal : Exception`.

### The tool project (`ILRuntimeNeoCompiler/`)

`ILRuntimeNeoCompiler.csproj` mirrors `PatchTool.csproj` (`OutputType=Exe`,
`net8.0;netcoreapp3.0`, the 4 configs) + copies `ILRuntimeTestCLI.csproj`'s
`<DefineConstants>ENABLE_NEO_MODE</DefineConstants>` on `Debug_Neo`/`Release_Neo`,
`ProjectReference` to `ILRuntime.csproj`, Cecil `Reference` hints. `AssemblyName=
ilrt_neoc`. Builds STANDALONE (`dotnet build ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj
-c Debug_Neo`, 0 errors); NOT in the sln (NU1201). `Program.cs` is a thin wrapper
(arg parse + `File.Create` + `NeoCompiler.Compile` + report + exit 0/2/1); Neo-only
(`#if ENABLE_NEO_MODE`, with a plain-`Debug` stub so the project still builds without Neo).

### OQ resolutions (at apply)

- **OQ1 (2x-compile vs a Write overload):** RESOLVED to D6-preferred (2x tool-time
  compile; `NeoAssemblyWriter` UNCHANGED). The 2x compile was never the long pole --
  the V1-A probe compile is sub-second. No Step-23 change needed.
- **OQ2 (V1-B literal-CLI smoke):** RESOLVED to include a MINIMAL V1-B. The arg-parse
  + exit-code contract is proven (no-args -> exit 1 + usage; bad-input -> exit 1).
  The file-path overload on a SMALL delegate-free DLL (`Step24V1BSample`) produces a
  valid `.neo` (magic `ILRN`) AND exits CLEANLY with code 0 + 0 skips. NOTE: the full
  `TestCases.dll` V1-B is NOT run to completion -- it stalls on delegate/async methods
  (Step 19+ incomplete JIT paths can hang the force-compile; NIE-throws ARE caught, but
  a JIT path that loops does not throw). This is a Step 19 gap, NOT a Step 24 bug; V1-A
  is the load-bearing gate and is green.
- **OQ3 (probe placement):** RESOLVED to a dedicated `NeoStep24CliProbe` in `TestCases/`
  (outer + nested type; non-generic + generic + try/catch + struct-local + ctors).

### DEVIATION from the plan (OQ1/structural): the V1-A entry is `Compile(IReadOnlyList<ILType>, Stream)`, NOT the planned stream/module overload

The plan pinned a `Compile(Stream, IReadOnlyList<Stream>, ModuleDefinition, AppDomain,
Stream)` overload for V1-A. That overload's input-MODULE filter yields EVERY type in
the probe's module (TestCases.dll = hundreds of types), so it CANNOT scope to a small
probe set (contradicts the binding "sub-second, NOT the large TestCases.dll" V1-A
requirement). Resolved by replacing it with a `Compile(IReadOnlyList<ILType> inputTypes,
Stream)` host-side entry that takes the probe type set EXPLICITLY. It shares the SAME
`CompileCore` as the CLI file-path entry -- the enumeration/partition/capture/serialize
wiring is IDENTICAL, just scoped. The file-path (CLI) entry still does the full
module-filter path. This is the cleanest way to honor both "the SAME driver" and "small
probe set." (If a future caller needs the module-scoped host entry, it is a trivial
wrapper: filter `LoadedTypes` by module then call this overload -- as the file-path
entry already does.)

### V1-A roundtrip design (the load-bearing gate, 5/5 PASS)

`NeoStep24CliRoundtripCheck.Run` (5 cells): (1) drive `new NeoCompiler().Compile(probeTypes,
ms)` + `NeoAssemblyReader.Read` -- asserts `driverResult.IsComplete` (the probe compiles
cleanly; a skip here is a wiring bug); (2) header magic + version; (3) counts + the
generic/non-generic split (`MethodDefs == ngen count`, `Templates == gdef count`,
`TypeDefs == probeTypes count`, every `ngen` non-generic, every `gdef` a definition);
(4) `model1 == model2` element-wise where `model2` = a DIRECT `NeoAssemblyWriter.Write`
on the same partitioned arrays (so the ref-idx scheme matches) via the Step-23
comparators `TypeDefsEqual`/`MethodDefsEqual`/`TemplatesEqual`; (5) independent fresh-
body check (each non-gen `MethodDef.NeoExecuteBody` byte-equals a fresh `CompileFresh`;
each `Template.TemplateBody` byte-equals the in-memory `ForceBuildTemplate` body).
Proves the driver wiring (enumerate + partition + capture + force-compile + serialize +
the input-set filter) end-to-end.

### Minimal additive accessor (the only non-#if-gated runtime-assembly edit)

3 Step-23 comparators in `NeoStep23RoundtripCheck.cs` widened `private static` ->
`internal static` (`MethodDefsEqual`, `TemplatesEqual`, `TypeDefsEqual`; `OpCodeRsEqual`
was already `internal`) so the Step-24 self-check reuses them without duplication. This
is additive (no behavior change; the file is `#if ENABLE_NEO_MODE && DEBUG` so it
compiles out of plain Debug regardless). No `InternalsVisibleTo`; no broad visibility
widening.

### V1-B result

- Arg-parse + exit-code: no-args -> exit 1 + usage; bad-input -> exit 1. PASS.
- File-path overload on a tiny delegate-free DLL (`Step24V1BSample`, 3 methods incl. 1
  generic): exit 0, output `.neo` first-4-bytes == Magic (`ILRN`), 0 skips, process
  exits CLEANLY (no leftover). PASS. (The full-`TestCases.dll` run is deferred -- stalls
  on incomplete delegate/async JIT paths; V1-A is the gate.)

### Gotcha fixed at apply: the file-path overload MUST Dispose() the AppDomain

The first V1-B runs hung on exit: the tool printed its report + wrote the `.neo` but
the process never terminated. Root cause: `AppDomain` initializes a field
`AsyncJITCompileWorker jitWorker = new AsyncJITCompileWorker()` (AppDomain.cs:79), and
the worker's ctor starts a FOREGROUND thread (no `IsBackground`) that loops on an
`AutoResetEvent` until `Dispose()`. `new AppDomain()` without `Dispose()` leaves that
thread alive -> the process never exits. Fix: the `Compile(string, ...)` file-path
overload now wraps its AppDomain usage in `try { ... } finally { appdomain.Dispose(); }`
(mirrors the test CLI's `session.Dispose()`). The host-side `Compile(IReadOnlyList<ILType>,
Stream)` overload does NOT dispose -- it reuses the caller's AppDomain (the caller owns
the lifecycle; disposing it would break the test CLI's session). After the fix, the tool
exits cleanly with code 0. (No runtime change -- AppDomain.Dispose already existed; this
is the driver correctly cleaning up the AppDomain IT created.)

### Regression gate (additive)

Neo 205/205, NeoStep23Roundtrip 15/15, NeoStep22SelfCheck 55/55, V1-A 5/5. Legacy-
neutral: plain-`Debug` ILRuntimeTestCLI builds 0 errors (NeoCompiler + the self-check
compile out under `#if ENABLE_NEO_MODE`); the NeoStep-filter Legacy run shows the SAME
pre-existing failure set (NeoStep cases under ExecuteR -- expected; the runtime DLL is
byte-identical since all Step-24 runtime edits are `#if`-gated and `NeoStep23RoundtripCheck.cs`
itself compiles out of plain Debug, making the comparator `internal` edits moot there).

### New files

- `ILRuntime/Runtime/NeoAOT/NeoCompiler.cs` (the driver, `#if ENABLE_NEO_MODE`).
- `ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj` + `Program.cs` (the CLI tool).
- `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep24CliRoundtripCheck.cs` (V1-A gate,
  `#if ENABLE_NEO_MODE && DEBUG`).
- `TestCases/NeoStep24CliProbe.cs` (the probe: outer + nested type).
- `Step24V1BSample/` (tiny DLL for the V1-B file-path smoke; can be deleted post-ship).

### Edits to existing files (additive)

- `ILRuntimeTestCLI/Program.cs`: the `NeoStep24CliRoundtrip` CLI hook (inside the existing
  `#if ENABLE_NEO_MODE` block).
- `NeoStep23RoundtripCheck.cs`: 3 comparators `private` -> `internal` (the accessor above).
