## ADDED Requirements

### Requirement: Standalone AOT CLI registers host CLR reference assemblies with the host CLR

The standalone `ilrt_neoc` precompile CLI SHALL register every reference
assembly passed on the command line with the host CLR via
`System.Reflection.Assembly.LoadFrom` (in the file-path `NeoCompiler.Compile`
overload), so the compile AppDomain's CLR-type fallback
(`AppDomain.GetType(string)`, the live
`System.AppDomain.CurrentDomain.GetAssemblies()` scan) resolves host CLR types
as `CLRType`. This mirrors the in-process runtime model, where a host CLR
assembly defining a type referenced by the IL (e.g. a CLR enum such as
`TestCLREnum`) is resident in the host `System.AppDomain` and is found by the
same fallback without any explicit registration call.

The registration SHALL be best-effort: a reference that cannot be CLR-loaded
(BCL assembly already loaded, native/ref-only metadata, missing file) SHALL be
caught and skipped, and resolution SHALL fall back to the existing CLR/BCL
scan. A failed `LoadFrom` SHALL NOT fatal-abort the compile.

#### Scenario: Input referencing a host CLR enum compiles without a CLR-resolution fatal

- **WHEN** `ilrt_neoc` is run on an input IL assembly whose methods reference a
  host CLR enum defined in a non-BCL host CLR assembly, AND that host assembly
  is passed as a reference path
- **THEN** the compile SHALL resolve the enum's Cecil `TypeReference` token to
  a `CLRType` (NOT an `ILType`) and SHALL write a valid `.neo` (magic
  `0x494C524E`) with exit code 0 (clean) or 2 (partial, for unrelated
  unimplemented-op skips), and SHALL NOT emit `Cannot find Type` on stderr.

#### Scenario: Host CLR ref that cannot be CLR-loaded is skipped, not fatal

- **WHEN** a reference path passed to the CLI is not CLR-loadable (already
  loaded, ref-only metadata, or missing) AND `Assembly.LoadFrom` throws
- **THEN** the CLI SHALL catch the exception, skip that reference, and continue
  compiling without fatal-aborting (exit 1 is reserved for input-load /
  serializer fatals, not for a skipped reference).

#### Scenario: Host CLR type resolves at .neo load time via the existing CLR path

- **WHEN** a `.neo` produced from a host-CLR-type-referencing input is loaded
  in an AppDomain where the host CLR assembly is registered
- **THEN** the `.neo` loader SHALL resolve the host CLR type via
  `NeoAssemblyLoader.ResolveTypeRefToIType` -> `appdomain.GetType(fullName)`
  (the same CLR fallback), producing the same `CLRType` the compile recorded,
  with NO `.neo` format extension required.

### Requirement: Host CLR reference assemblies MUST NOT be registered via the IL LoadAssembly path

The standalone CLI SHALL NOT register a host CLR reference assembly via
`AppDomain.LoadAssembly` (the IL hotfix load path). `LoadAssembly`-ing a host
CLR assembly wraps its types as `ILType` in `mapType`, which `AppDomain.GetType
(string)` returns BEFORE reaching the CLR fallback, shadowing the real CLR type
with an `ILType`. For a CLR enum this shadow both mis-resolves the type and
produces a downstream failure during compile.

#### Scenario: Host CLR enum is not shadowed by an ILType wrap

- **WHEN** a host CLR assembly defining a CLR enum is passed as a reference to
  the CLI
- **THEN** the CLI SHALL NOT call `AppDomain.LoadAssembly` on it, and the
  enum's Cecil `TypeReference` SHALL resolve to a `CLRType` (the real CLR
  enum), so that a method reading the enum after `.neo` load + attach executes
  the enum equality correctly (the enum value round-trips).

#### Scenario: Removed LoadAssembly-the-ref path regresses no verified scenario

- **WHEN** the prior `LoadAssembly(refStream)` reference loop is replaced by
  the `Assembly.LoadFrom` registration
- **THEN** no previously-green self-check or smoke (NeoStep, NeoStep22/23/24/25
  self-checks) SHALL regress, because the replaced path had no verified
  coverage (Step-24 V1-B was BCL-refs-only; the ref-`LoadAssembly` path was
  V1-A-UNVERIFIED).
