# Ship Log — neo-step24-ilrt-neoc

> Change: `neo-step24-ilrt-neoc` (portfolio child of `neo-completion-portfolio`)
> Capability: `neo-optimizer`
> Branch: `features/object-model-overhaul`
> Date: 2026-07-07
> Pipeline: small-feature, Tier A, full autonomy.

## Verdict

**SHIPPED — clean (V1 scope).** Review verdict **APPROVE-WITH-FINDINGS** (0 Blocker,
0 Major CODE defect); the 2 Majors are documentation/scope (the full-`TestCases.dll`
deferral rationale + the V1-A coverage qualification), corrected in-flight by the
LEAD (handoff §6.A). Step-25 hardening items recorded.

## Delivered scope (Step 24 -- the `ilrt_neoc` standalone precompile CLI)

A CLI integration layer that drives Step 22/23 end-to-end. ADDITIVE (no
JIT/runtime/Step-22-behavior/Step-23 change):

- **`public sealed class NeoCompiler`** (`ILRuntime/Runtime/NeoAOT/NeoCompiler.cs`,
  NEW, `#if ENABLE_NEO_MODE`) -- the SINGLE in-assembly public seam (every NeoAOT
  type + `ILMethod.BodyRegister` + `GenericMethodTemplateCache` + `ForceBuildTemplate`
  is `internal` with NO `InternalsVisibleTo`; the in-assembly public class is the
  only way the tool project reaches them). Two public overloads sharing one private
  `CompileCore`:
  - `Compile(string inputAssemblyPath, IReadOnlyList<string> refs, Stream output)` --
    the CLI entry (Cecil resolver + `ModuleDefinition.ReadModule` +
    `InitializeFromModule` + module-filter + `CompileCore`).
  - `Compile(IReadOnlyList<ILType> inputTypes, Stream output)` -- the V1-A host-side
    entry (scoped probe set; reuses the caller's AppDomain).
  - `CompileCore`: per type `GetMethods()`+`GetConstructors()`; PARTITIONS generic-defs
    (-> `templates[]` via a SYNTHESIZED capture-eligible `int`-per-param
    `MakeGenericMethod`+`BodyRegister` -- no call site needed; a generic definition
    NEVER goes to `methods[]`) vs non-generic (-> per-method try/catch force-compile,
    survivors to `methods[]`); then the UNCHANGED `NeoAssemblyWriter.Write`.
    `NeoCompilerResult`/`MethodSkip`/`NeoCompilerFatal`. Exit codes 0 (clean) / 2
    (partial -- methods skipped) / 1 (fatal).
- **`ILRuntimeNeoCompiler/`** (NEW tool project, mirror `PatchTool.csproj`, Neo-only,
  `AssemblyName=ilrt_neoc`, builds STANDALONE via
  `dotnet build ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj -c Debug_Neo`, NOT
  in the sln). `Program.cs` is a thin wrapper (arg parse, AppDomain, refs, call
  `NeoCompiler.Compile`, output, exit code).
- **`NeoStep24CliRoundtripCheck.cs`** (V1-A host-side self-check, 5 cells: driver +
  header + counts/split + model1==model2 via Step-23 comparators + independent
  fresh-body) + `TestCases/NeoStep24CliProbe.cs` (outer + nested type, non-generic +
  generic) + the `NeoStep24CliRoundtrip` CLI hook in `ILRuntimeTestCLI/Program.cs`.
- **Minimal additive accessor:** 3 Step-23 comparators (`MethodDefsEqual`/
  `TemplatesEqual`/`TypeDefsEqual`) widened `private`->`internal` for reuse (NO logic
  change; `OpCodeRsEqual` was already internal). Step 23's roundtrip still 15/15.

## Verification evidence

- **V1-A CLI roundtrip: 5/5 cells PASS** (`NeoStep24CliRoundtrip`): compile the probe
  via `NeoCompiler.Compile` -> stream -> `NeoAssemblyReader.Read` -> assert == the
  in-memory compile via Step 23's comparators. Proves `CompileCore` (enumeration +
  partition + template synthesis + serialize).
- **V1-B literal CLI smoke:** arg-parse/exit-code (no-args/bad-input -> exit 1); a
  tiny BCL-refs-only DLL -> exit 0, magic `4e 52 4c 49` (LE `0x494C524E` = "ILRN"),
  valid `.neo`.
- **Regression gates:** NeoStep **205/205**, NeoStep23Roundtrip **15/15**,
  NeoStep22SelfCheck **55/55** -- zero regressions.
- **Legacy-neutral:** plain-`Debug` builds 0 errors (NeoCompiler + self-check compile
  out under `#if ENABLE_NEO_MODE`); NeoStep-filter Legacy run = same pre-existing
  failure set (all Step-24 runtime edits are `#if`-gated).
- **Standalone build:** `ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj` builds
  standalone (`dotnet build`, NOT in the sln -- NU1201).

## Gotcha found + fixed at apply
The first V1-B runs HUNG ON EXIT (printed the report + wrote the `.neo` but never
terminated). Root cause: `new AppDomain()` field-initializes `AsyncJITCompileWorker`,
whose ctor starts a FOREGROUND thread looping on an `AutoResetEvent` until `Dispose()`.
Fix: the file-path overload wraps its AppDomain in `try { ... } finally {
appdomain.Dispose(); }` (mirrors the test CLI's `session.Dispose()`). No runtime
change (`AppDomain.Dispose` already existed); only the CLI's OWN fresh AppDomain is
disposed (the host-side V1-A overload does NOT dispose the caller's AppDomain).

## V1 boundary + the corrected deferral rationale (review Major-1/Major-2)
The full-`TestCases.dll` CLI run FAILS with a CLR-type-resolution fatal (`Cannot find
Type:ILRuntimeTest.TestFramework.TestCLREnum` -- a CLR enum in `ILRuntimeTestBase`, a
host CLR assembly the standalone CLI never registers). This is design D7's "V1 =
BCL-only ref assemblies" boundary -> the skip-with-warning/exit-2 contract does NOT
engage for non-BCL CLR refs (serialize aborts -> exit 1). (An earlier note mis-stated
this as a "Step-19 JIT stall" -- corrected.) V1-A proves `CompileCore`; V1-B covers
the happy path (BCL refs only); the CLI-specific Cecil-resolver / `InitializeFromModule`
/ ref `LoadAssembly` / module-filter-exclusion / `Dispose` paths are V1-A-UNVERIFIED
(Step-25 hardening).

## OQ resolutions
- **OQ1:** D6-preferred (2x tool-time compile; `NeoAssemblyWriter` UNCHANGED).
- **OQ2:** minimal V1-B included (arg-parse/exit-code + tiny DLL smoke).
- **OQ3:** dedicated `NeoStep24CliProbe` in `TestCases/`.

## Deferred (per plan / Step-25 follow-ups)
- Runtime `.neo` LOADER + Cecil-decoupling + V2 functional deserialize->ExecuteNeo
  (Step 25).
- Robust IL-vs-CLR ref classification + module-filter-exclusion correctness +
  registering host CLR assemblies (TestCLREnum etc.) for a full `TestCases.dll`
  compile (Step 25).
- Multi-generic-param + generic-on-generic-type template capture (review Minor-1);
  compiler-synthetic-type emission filtering (Minor-2); non-public/virtual/abstract
  method enumeration coverage (Minor-3); dead `Skipped == null` branch (Minor-4).
- Perf benchmarks (Step 26).
- The throwaway `Step24V1BSample/` fixture DELETED (reviewer recommendation; not
  referenced by any committed automation).

## Lessons reaffirmed
- **The dump-gate + adversarial review caught a mis-diagnosis.** The implementer's
  "Step-19 JIT stall" deferral rationale was corrected by the reviewer's adversarial
  full-`TestCases.dll` probe (it's a CLR-type-resolution fatal, a V1-scope boundary,
  not a delegate/async gap). Accurate deferral rationale matters -- the next session
  relies on it.
- **The internals-visibility discovery shaped the design.** The planner's finding
  (no `InternalsVisibleTo`, all NeoAOT types internal) -> the `public NeoCompiler`
  in-assembly seam (cleaner than IVT or broad visibility-widening).
- **A hang on exit is a real bug, not a flake.** The `AsyncJITCompileWorker` foreground-
  thread hang was caught + fixed (AppDomain.Dispose); a process that never terminates
  breaks automation.
