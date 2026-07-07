## ADDED Requirements

### Requirement: ilrt_neoc is a standalone precompile CLI that bulk-compiles an IL assembly to a .neo via the Step-22 template + Step-23 serializer layers

The `ilrt_neoc` tool SHALL be a standalone console-app project (`ILRuntimeNeoCompiler/`)
SEPARATE from `PatchTool` and `ILRuntimeTestCLI` (design doc section 8.3: "ilrt_neoc
is independent from PatchTool"). The tool SHALL build standalone via `dotnet build
ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj -c Debug_Neo` and SHALL NOT be
added to `ILRuntime.sln` (the sln cannot build whole -- the VS2022 debugger VSIX
NU1201). The tool's argument surface SHALL be positional:
`ilrt_neoc <input.dll> <output.neo> [reference-assembly paths...]`. The tool SHALL
be a thin wrapper: it SHALL parse args, open streams, invoke a single public
`NeoCompiler.Compile(...)` entry, print the compile report, and map the outcome to
an exit code (`0` = every method + template compiled; `2` = one or more methods
skipped, `.neo` still written; `1` = fatal, no `.neo`). The tool project SHALL
contain NO runtime / JIT / serializer / template logic -- all compile logic SHALL
live in the `public NeoCompiler` driver class inside the ILRuntime assembly
(`ILRuntime/Runtime/NeoAOT/NeoCompiler.cs`, gated `#if ENABLE_NEO_MODE`), which is
the single public seam that reaches the internal NeoAOT + JIT + template
machinery. This whole requirement is Neo-only: the `NeoCompiler` class + the tool
SHALL compile out of plain `Debug`, so Legacy `ExecuteR` is byte-identical.

#### Scenario: Standalone tool builds and runs without the sln
- **WHEN** `dotnet build ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj -c Debug_Neo`
  is run on a clean tree
- **THEN** the build SHALL succeed with zero errors (transitively building
  `ILRuntime.csproj`)
- **AND** running `ILRuntimeNeoCompiler.exe <input.dll> <out.neo>` on a small IL
  assembly SHALL produce a `out.neo` whose first 4 bytes are the `.neo` Magic
  `0x494C524E` ("ILRN") and whose header `Version` is the Step-23 version
- **AND** the tool SHALL NOT require the `ILRuntime.sln` to build (it builds by
  project file alone)

#### Scenario: Arg-parse + exit-code contract
- **WHEN** the tool is invoked with fewer than 2 positional args, or a non-existent
  input path
- **THEN** it SHALL print a usage message and return exit code `1` without creating
  the output file
- **WHEN** the tool is invoked on a valid input where every IL method compiles
- **THEN** it SHALL return exit code `0` and the output `.neo` SHALL contain every
  non-generic IL method in the `MethodDefTable` and every generic-method definition
  in the `TemplateTable`
- **WHEN** the tool is invoked on an input where some methods throw during compile
  (e.g. a `NotImplementedException` for an unimplemented op)
- **THEN** it SHALL NOT abort; it SHALL skip those methods, emit the `.neo` with the
  compiled subset, report each skipped method (declaring-type-full-name.method-name +
  exception type + message) to stderr, and return exit code `2`

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and the Legacy register VM
  runs the NeoStep-filter smoke
- **THEN** the smoke SHALL show the SAME pre-existing Legacy failure set with and
  without this change (stash-toggle proof), because the `NeoCompiler` class is gated
  `#if ENABLE_NEO_MODE` and the `ILRuntimeNeoCompiler/` project is a separate tool
  the runtime never references

### Requirement: The NeoCompiler driver enumerates every IL method, force-compiles via the Step-23 writer, and captures every generic-method template via synthesized instantiation -- additive and Legacy-neutral

The `NeoCompiler` driver SHALL enumerate every IL type in the INPUT module only
(filtering `appdomain.LoadedTypes` by `TypeDefinition.Module == inputModule`, so
reference-assembly types are compiled-against but NOT emitted), and for each type
SHALL enumerate both `GetMethods()` and `GetConstructors()`. The driver SHALL
partition methods into non-generic (emitted to the `MethodDefTable` via
`NeoAssemblyWriter.Write`, which `CompileFresh`-compiles each) and generic-method
DEFINITIONS (`GenericParameterCount > 0 && !IsGenericInstance`). A generic-method
definition SHALL be emitted to the `TemplateTable` by SYNTHESIZING a capture-
eligible instantiation -- one primitive (`AppDomain.IntType`) per generic parameter
via `definition.MakeGenericMethod(IType[])`, then triggering the existing
`InitCodeBody` capture hook (reading the instance's `BodyRegister`) so
`StoreGenericTemplate` caches the template on
`definition.GenericMethodTemplateCache`. The driver SHALL NOT compile the open
generic definition directly (Step 22 REJECTED this -- it corrupts shared AppDomain
caches); synthesis is the only path. The driver SHALL NOT pass a generic definition
to `NeoAssemblyWriter.Write`'s `methods[]` (that would compile the open definition).
The driver SHALL force-compile each non-generic method inside a per-method try/catch
BEFORE handing the survivor set to `Write`, so a method that throws during compile
is recorded as a skip (method display name + exception type + message) and omitted,
NOT propagated. The driver SHALL NOT modify `NeoAssemblyWriter`, the JIT, the
optimizer, the Step-22 template mechanism, or the runtime; it is a new caller of
existing, unchanged internals.

#### Scenario: Every IL method and every generic definition in the input is emitted
- **WHEN** `NeoCompiler.Compile` is run on an input assembly containing a mix of
  non-generic methods (instance + static + instance ctor + static `.cctor`) and
  generic-method definitions, across top-level and nested types
- **THEN** the resulting `.neo` `MethodDefTable` SHALL contain EVERY non-generic IL
  method (including constructors and static constructors, including methods on
  nested types)
- **AND** the `TemplateTable` SHALL contain a template for EVERY generic-method
  definition in the input module
- **AND** the `MethodDefTable` SHALL contain NO generic-method definition (the
  generic/non-generic split is enforced)
- **AND** the `TypeDefTable` SHALL contain every IL type in the input module and NO
  type from a reference assembly (the input-module filter holds)

#### Scenario: Template capture via synthesized instantiation, no call site needed
- **WHEN** the input contains a generic method `T M<T>(T v, int n)` with NO call site
  in the assembly (no concrete instantiation)
- **THEN** the driver SHALL synthesize `M<int>` (one `int` per generic parameter),
  trigger the capture, and emit a template whose `TemplateBody` is the T-invariant
  register-index body
- **AND** the template's `Patches[]` SHALL equal what the in-process Step-22
  `ExtractPatches` produces for that definition (the synthesized-`int` capture path
  is the same `InitCodeBody` hook a real `M<int>` call site would take)
- **AND** the driver SHALL NOT compile the open `M<T>` definition directly (no
  cache-corrupting open-definition compile occurs)

#### Scenario: A method that throws during compile is skipped, not fatal
- **WHEN** a non-generic method's compile throws (e.g. `NotImplementedException` for
  an unimplemented op) and other methods compile cleanly
- **THEN** the driver SHALL record the throwing method in `NeoCompilerResult.Skipped`
  (method display name + exception type + message)
- **AND** the `.neo` SHALL still be written, containing every method that DID compile
  and every template that DID capture
- **AND** `NeoCompilerResult.IsComplete` SHALL be `false` (driving exit code `2`)
- **AND** a generic definition whose template CAPTURE throws SHALL likewise be
  recorded as a skip and omitted from the `TemplateTable` (the Step-25 runtime falls
  back to per-occurrence JIT for it -- the additive contract holds)

### Requirement: The V1 CLI roundtrip self-check proves the driver wiring end-to-end, reusing the Step-23 comparators

The V1 load-bearing gate for Step 24 SHALL be a host-side self-check
(`NeoStep24CliRoundtripCheck.Run(appdomain)`) driven via the existing
`ILRuntimeTestCLI` special-mode hook (`if (nameFilter == "NeoStep24CliRoundtrip")`),
mirroring the Step-22 / Step-23 self-check pattern. The self-check SHALL build a
small dedicated probe set (a handful of non-generic methods + a generic method + a
try/catch method + a struct-local method -- NOT the large `TestCases.dll` method
set, to keep the compile sub-second), invoke the SAME `NeoCompiler` driver the CLI
uses to produce a `.neo` in a `MemoryStream`, READ it back with
`NeoAssemblyReader.Read`, and assert the deserialized `NeoAssemblyModel` EQUALS the
in-memory compile. The equality assertions SHALL REUSE the Step-23 comparators:
`OpCodeRsEqual` (raw-24-byte `OpCodeR[]`), `MethodDefsEqual` (frame field-for-field),
`TemplatesEqual` (template faithful, every patch), `TypeDefsEqual` (type layout +
VTable + interfaces). The self-check SHALL additionally assert the driver's
generic/non-generic split: the deserialized `MethodDefTable` contains no generic
definition and the `TemplateTable` contains every generic definition in the probe.
V2 functional (deserialize -> `ExecuteNeo`) is explicitly Step 25 and is NOT
exercised here. The self-check + the probe type SHALL be Neo-only (compile out of
plain `Debug`); the NeoStep + NeoStep23Roundtrip + NeoStep22SelfCheck regression
smoke SHALL stay green.

#### Scenario: V1 CLI roundtrip passes for the probe matrix
- **WHEN** `NeoStep24CliRoundtripCheck.Run(appdomain)` is invoked host-side (DEBUG+Neo)
- **THEN** every probe method's deserialized `NeoMethodDefRecord` SHALL equal the
  in-memory compile (`OpCodeRsEqual` on `NeoExecuteBody`; `MethodDefsEqual` on the
  full frame: `LocalInfos`/`ParamInfos`/`TotalStructSize`/`TotalRefSize`/all scalars/
  `LocalIsReference`/`NeoCatchException*`/`SwitchTargets`/`NeoCallParams`/
  `ExceptionHandlers`)
- **AND** the probe's generic-method template SHALL roundtrip faithfully
  (`TemplatesEqual`: `TemplateBody` byte-for-byte, every `PatchEntry`, the front-half
  scalars, `InitObjPrefixRegisters`, `VariableTypeRefIdxs`, `ConstrainedTypeRefIdxs`,
  `ConstrainedMethodRefIdxs`)
- **AND** the probe type's `NeoTypeDefRecord` SHALL roundtrip (`TypeDefsEqual`:
  field layout, VTable method-refs, interface entries, static-ctor ref)
- **AND** the header (`Magic`/`Version`) + the table counts (`TypeDefs`/`MethodDefs`/
  `Templates`) SHALL match the driver's emitted set

#### Scenario: Generic/non-generic split is asserted by the self-check
- **WHEN** the self-check deserializes the probe's `.neo`
- **THEN** the `MethodDefTable` SHALL contain NO generic-method definition
  (every entry is a non-generic method, incl. ctors)
- **AND** the `TemplateTable` SHALL contain a template for EVERY generic-method
  definition present in the probe type

#### Scenario: Regression smoke stays green (additive)
- **WHEN** the driver + the self-check + the probe type are added (`Debug_Neo`)
- **THEN** the NeoStep smoke SHALL stay 205/205, NeoStep23Roundtrip 15/15,
  NeoStep22SelfCheck 55/55, NeoOptHardening 24/24, NeoStep20 9/9 (ZERO regressions;
  the driver is a new caller of unchanged internals, and the self-check is additive)
- **AND** a plain-`Debug` + `useRegister=true` NeoStep-filter run SHALL show the SAME
  pre-existing Legacy failure set with and without the change (Legacy-neutral;
  everything new is gated `#if ENABLE_NEO_MODE`)
