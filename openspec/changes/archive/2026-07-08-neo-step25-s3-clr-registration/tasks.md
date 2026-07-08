# Tasks: neo-step25-s3-clr-registration

> Implementer handoff. The dump-gate is reproduced + the fix surface decided
> (design.md). Capability: `neo-optimizer`. All source edits are Neo-only
> (`#if ENABLE_NEO_MODE`); Legacy-neutral. ALWAYS `-f net8.0` for CLI runs.

## 1. The fix (CLI-side host-CLR registration)

- [x] 1.1 In `ILRuntime/Runtime/NeoAOT/NeoCompiler.cs`, locate the
  reference-assembly loop in `Compile(string, IReadOnlyList<string>, Stream)`
  (~lines 119-136), which today does `using (var rs = File.OpenRead(rp))
  appdomain.LoadAssembly(rs);` in a try/catch.
- [x] 1.2 REPLACE that loop body with host-CLR registration: for each
  non-empty, existing reference path, `try { System.Reflection.Assembly.
  LoadFrom(rp); } catch { /* best-effort skip */ }`. Add a comment citing the
  mirror of the in-process model + the `AppDomain.cs:1409` fatal it closes.
- [x] 1.3 Keep the Cecil resolver search-directory setup above it (~lines
  80-95) UNCHANGED (still needed for Cecil to read the input's
  AssemblyReferences). Do NOT touch `InitializeFromModule` or the input-module
  filter.
- [x] 1.4 Confirm `NeoCompiler.cs` still compiles under `Debug_Neo` (the only
  config where `#if ENABLE_NEO_MODE` is live). Confirm plain `Debug` still
  compiles the file out (Legacy-neutral). [NeoCompiler is fix-ONLY -- a
  temporary additive type-filter was reverted to honor design D4 ("no Step-24
  artifact change beyond the fix"); see apply note A2.]

## 2. The dump-gate reproduction flips green

- [x] 2.1 Build the CLI: `dotnet build ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj -c Debug_Neo` (0 errors).
- [x] 2.2 Build TestCases: `dotnet build TestCases/TestCases.csproj -c Debug` (0 errors).
- [x] 2.3 Reproduce-flips-green: run `ilrt_neoc <focused probe> out.neo ILRuntimeTestBase.dll`
  -> EXIT 0, valid `.neo` (magic `4e 52 4c 49`, 1481 bytes), 3 methods / 0
  templates / 0 skipped, NO `Cannot find Type` on stderr. [See apply note A1
  for why the focused `NeoClrProbe.dll` is the input rather than the full
  TestCases.dll.] BEFORE the fix this fatal-aborts (`Cannot find
  Type:...TestCLREnum`); AFTER it writes a clean `.neo`.
- [x] 2.4 Adversarial contrast: the bare run (no refs) STILL fails on
  TestCLREnum. On the focused probe the two enum methods SKIP with
  `KeyNotFoundException: Cannot find Type:...TestCLREnum` (the .ctor, which has
  no enum ref, compiles -> exit 2); on the full TestCases.dll the bare run
  FATALS (`Cannot find Type:...TestCLREnum`, exit 1 -- the original Step-24
  reproducer). Proves the host CLR assembly is the resolution source.

## 3. The host-side round-trip self-check (load-bearing)

- [x] 3.1 Add `NeoStep25S3ClrEnumCheck.cs` under
  `ILRuntime/Runtime/Intepreter/RegisterVM/` (`#if ENABLE_NEO_MODE && DEBUG`,
  mirrors `NeoStep25LoadExecCheck`). It loads the probe assembly, drives the
  FILE-PATH `NeoCompiler.Compile` overload with the probe path +
  ILRuntimeTestBase ref path, asserts no fatal + no `TestCLREnum` skip, reads
  the `.neo` back via `NeoAssemblyReader.Read`, `NeoAssemblyLoader.Attach`,
  then invokes the CLR-enum-reading methods and asserts the enum value
  round-trips. [OQ1 resolved to the dedicated `NeoClrProbe` assembly -- see
  apply note A1.]
- [x] 3.2 Add the probe type `NeoClrProbe.ClrEnumProbe` (a dedicated
  `NeoClrProbe` project referencing ILRuntimeTestBase) returning
  `TestCLREnum.Test2`'s int + an enum-equality variant, for an explicit `==`
  check (OQ2).
- [x] 3.3 Wire the `NeoStep25S3ClrEnum` CLI hook in
  `ILRuntimeTestCLI/Program.cs` (mirrors `NeoStep25LoadExec`; computes the
  probe DLL + ILRuntimeTestBase ref paths from the TestCases path).
- [x] 3.4 Run the self-check: `NeoStep25S3ClrEnum` -> 7/7 cells passed, 0
  failed (LoadAssembly(probe) + file-path Compile 3 methods / 0 skipped +
  Attach 3/0 + ReadEnum round-trip + ReadEnumCmp round-trip). The enum value
  round-trips compile -> .neo -> load -> ExecuteNeo.

## 4. The V1-B literal-CLI smoke (standalone-process proof)

- [x] 4.1 Run the standalone CLI binary on the focused `NeoClrProbe.dll` with
  `ILRuntimeTestBase.dll` as the ref. EXIT 0 (NOT 1/2), writes a `.neo` with
  magic `0x494C524E`, 3 methods / 0 skipped. [The standalone process does NOT
  have ILRuntimeTestBase pre-loaded -- `Assembly.LoadFrom` is what resolves
  TestCLREnum. See apply note A1 for the full-TestCases contrast.]
- [x] 4.2 `out.neo` is loadable: the self-check's `NeoAssemblyReader.Read`
  returns a non-null model (3 method defs), then Attach + invoke succeed (cell
  3.4).

## 5. Regression + Legacy-neutrality

- [x] 5.1 NeoStep smoke green: 219 tests, 0 failed (current green baseline;
  zero regressions).
- [x] 5.2 NeoStep22/23/24/25 self-checks unchanged: 22=55/55, 23=15/15,
  24=5/5, 25(NeoStep25LoadExec)=28/28.
- [x] 5.3 Legacy-neutral build: `dotnet build ILRuntime/ILRuntime.csproj -c
  Debug` -> 0 errors (`NeoCompiler.cs` compiles out under plain Debug; the fix
  is `#if ENABLE_NEO_MODE`).
- [x] 5.4 Legacy NeoStep-filter run: the fix is `#if`-gated -> Legacy
  byte-identical (no Legacy behavior change; the NeoCompiler ref-loop edit is
  unreachable under plain Debug).

## 6. Ship

- [x] 6.1 Update `.trae/documents/neo-deferred-items.md`: mark the STEP-25
  `TestCLREnum` / host-CLR-assembly-registration deferral RESOLVED (points at
  this change). ALSO log the newly-revealed `TestClass2` cross-binding-adaptor
  gap as a SEPARATE deferred item (see apply note A1).
- [ ] 6.2 Run `openspec validate neo-step25-s3-clr-registration` (or the
  review-cycle) -> archive on approval. [Pending the review/archive phase.]

---

## Apply notes (deltas from the plan, for the reviewer/LEAD)

- **A1. The full-`TestCases.dll` compile fatals on a SEPARATE, deeper gap
  (`TestClass2` adaptor), newly revealed by this fix.** With the fix applied,
  `ilrt_neoc TestCases.dll out.neo ILRuntimeTestBase.dll` gets PAST every
  TestCLREnum reference (the fix works -- the standalone process resolves
  TestCLREnum as CLRType) but then fatals at the SERIALIZER stage with
  `TypeLoadException: Cannot find Adaptor for:ILRuntimeTest.TestFramework.
  TestClass2` (exit 1). Root cause: the standalone CLI's fresh AppDomain
  registers NO CLR cross-binding adaptors (the in-process test harness
  registers them via `CLRBindings.Initialize` + `RegisterCrossBindingAdaptor`;
  the standalone CLI does not). Some TestCases IL types INHERIT host CLR
  classes (e.g. TestClass2), and serializing their TypeDef record needs the
  adaptor. This is NOT the host-CLR-type-RESOLUTION concern S3-5 closes (an
  enum is a primitive -- no adaptor); it is the "standalone CLI registers no
  CLR adaptors" gap, a distinct deferral (logged in neo-deferred-items). The
  success criterion is "no longer fatals ON TestCLREnum" + "the enum
  round-trips" -- both MET (the with-ref run fatals on TestClass2, NOT
  TestCLREnum; the bare-run contrast still fatals TestCLREnum). To get a CLEAN
  standalone exit-0 + a clean round-trip .neo, the design's OQ1 default (a
  dedicated probe assembly) is used: `NeoClrProbe.dll` references ONLY the CLR
  enum (no host CLR class base -> no adaptor), so it compiles + loads + runs
  end-to-end with no fatal.

- **A2. A temporary additive `typeFullNameFilter` on `NeoCompiler.Compile` was
  reverted.** It was prototyped to enable a focused compile of a single
  TestCases probe type, but the dedicated `NeoClrProbe` assembly (A1) is the
  principled solution and honors design D4 ("ILRuntimeNeoCompiler/Program.cs
  is UNCHANGED" + "no Step-24 artifact change beyond the fix"). `NeoCompiler`
  ships fix-ONLY (the `Assembly.LoadFrom` ref loop); `Program.cs` is
  byte-identical.

- **A3. The probe is loaded in-process via `appdomain.LoadAssembly(probeFs)`
  with the FileStream intentionally left OPEN** (Cecil's
  `ModuleDefinition.ReadModule(stream)` reads metadata lazily from the stream,
  mirroring `TestSession.Load` which holds its FileStream as a field for the
  AppDomain's lifetime). Disposing it breaks lazy method resolution at Attach
  (`ObjectDisposedException: Cannot access a closed file`). Reclaimed at CLI
  process exit (a short-lived debug self-check).

- **OQ1/OQ2/OQ3 resolved:** OQ1 = dedicated `NeoClrProbe` assembly (the fast,
  clean, focused input) + the full-TestCases V1-B for the standalone-process
  TestCLREnum-resolved contrast (it gets past TestCLREnum, fataling separately
  on TestClass2). OQ2 = the probe returns the enum's underlying int for an
  explicit `== 1` assertion (the Neo Run shim's primitive-return path;
  `NeoBoxReturnValue` typeof(int) arm). OQ3 = `Assembly.LoadFrom` (load-from
  context; matches the host-AppDomain-resident semantics of the in-process
  model).
