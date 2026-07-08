# Tasks: neo-step25-clr-adaptor

> Implementer handoff. The dump-gate is reproduced + the fix surface decided
> (design.md: the throw site + the builtin-vs-harness adaptor audit + the
> register-builtins-is-a-no-op + skip-harness-gracefully verdict). Capability:
> `neo-optimizer`. All source edits are Neo-only (`#if ENABLE_NEO_MODE` -- the
> whole `NeoCompiler.cs` file is); Legacy-neutral. ALWAYS `-f net8.0` for CLI
> runs. `Debug_Neo` prints huge JIT output -- normal.

## 1. The fix (type-level graceful-skip pre-filter in CompileCore)

- [x] 1.1 In `ILRuntime/Runtime/NeoAOT/NeoCompiler.cs`, locate `CompileCore`
  (`NeoCompiler.cs:215`). It currently iterates `inputTypes` directly:
  `GetMethods`/`GetConstructors` (`:227-235`) -> per-method partition + force-
  compile (per-method try/catch `:257-265`) -> `writer.Write(inputTypes,
  methods.ToArray(), templates.ToArray(), outputStream)` (`:275`).
- [x] 1.2 INSERT a per-type pre-filter at the TOP of `CompileCore` (before the
  `foreach (var type in inputTypes)` enumeration of methods): build a
  `var compilableTypes = new List<ILType>();` list; for each non-null `type` in
  `inputTypes`, `try { _ = type.FirstCLRBaseType; _ = type.FirstCLRInterface;
  compilableTypes.Add(type); } catch (TypeLoadException ex) {
  result.Skipped.Add(MakeTypeSkip(type, ex)); }`. Add a comment citing the
  throw sites (`ILType.cs:1505/1568/1593`) + that this triggers the lazy
  adaptor resolution BEFORE the per-method compile and BEFORE `Write`, so a
  harness-adaptor type is excluded cleanly. (Design D1.)
- [x] 1.3 ROUTE `compilableTypes` (NOT `inputTypes`) through the rest of
  `CompileCore`: the per-method `foreach` iterates `compilableTypes`; the
  survivor `methods`/`templates` are built from `compilableTypes`; and
  `writer.Write(compilableTypes.ToArray(), methods.ToArray(),
  templates.ToArray(), outputStream)` replaces the `writer.Write(inputTypes,
  ...)`. Set `result.TypesCompiled = compilableTypes.Count` (the emitted count)
  instead of `inputTypes.Length`. (Design D1.)
- [x] 1.4 ADD a `static MethodSkip MakeTypeSkip(ILType type, Exception ex)`
  helper (mirrors the existing `MakeSkip` at `:330`, but the `MethodDisplay` is
  `"(type) " + (type.FullName ?? "?")` -- the type-level marker; `ExceptionType`
  + `Message` from `ex`). (Design D2 + OQ1 default.)
- [x] 1.5 Confirm the catch is scoped to `TypeLoadException` ONLY (design D1 +
  spec scenario "A non-adaptor type-init failure stays a loud fatal"): a
  non-`TypeLoadException` init failure MUST propagate to the existing
  `NeoCompilerFatal("serializer failure: ...")` wrapper (exit 1), NOT become a
  skip.
- [x] 1.6 Confirm `NeoCompiler.cs` still compiles under `Debug_Neo` (0 errors)
  and compiles out under plain `Debug` (Legacy-neutral -- the whole file is
  `#if ENABLE_NEO_MODE`).

## 2. The dump-gate reproduction flips green (no adaptor fatal)

- [x] 2.1 Build the CLI: `dotnet build ILRuntimeNeoCompiler/
  ILRuntimeNeoCompiler.csproj -c Debug_Neo` (0 errors).
- [x] 2.2 Build TestCases: `dotnet build TestCases/TestCases.csproj -c Debug`
  (0 errors).
- [x] 2.3 Reproduce-flips-green: `ilrt_neoc TestCases.dll out.neo
  ILRuntimeTestBase.dll` (from `ILRuntimeNeoCompiler/bin/Debug_Neo/net8.0`,
  refs from `TestCases/bin/Debug/netstandard2.1`) -> the `Cannot find Adaptor
  for:TestClass2` FATAL that occurs on HEAD (`exit 1`, `ilrt_neoc: FATAL:
  serializer failure: Cannot find Adaptor for:...TestClass2`) is GONE. The run
  now writes a valid `out.neo` and returns exit `2` (partial -- the harness-
  adaptor types are skipped) or `0`. The stderr SHALL contain `SKIP (type)
  ...: TypeLoadException: Cannot find Adaptor for:...` lines (the reported
  skips) and NO `FATAL` line. (Tip: pipe through `grep -iE "FATAL|Cannot find
  Adaptor|SKIP "` to cut the `Debug_Neo` JIT noise, as in the planner dump.)
  **APPLY RESULT (fixer-a2, 2026-07-08):** MET. `Cannot find Adaptor` fatal
  GONE; the A2 field-init NRE fatal GONE (A2 closure); the A3 null-body NRE
  GONE (A3 extension). `ilrt_neoc TestCases.dll out.neo ILRuntimeTestBase.dll`
  -> **exit 2**, **NO `FATAL` line**, valid `out.neo` written (magic
  `0x494C524E`), skip report = 69 `SKIP (type)` lines (incl. the anonymous
  `<>f__AnonymousType0\`2` field-type TLE + the harness-adaptor types) + 181
  per-method skips; "compiled 1470 methods, 96 templates, 445 types".
- [x] 2.4 Valid `.neo`: `out.neo` SHALL start with magic bytes `4e 52 4c 49`
  (LE `0x494C524E` = "ILRN"). Confirm via a hex/byte read of the first 4 bytes.
  **APPLY RESULT (fixer-a2):** MET -- first 4 bytes `4e 52 4c 49` (= LE
  `0x494C524E` = "ILRN"), file 1043473 bytes.

## 3. The host-side self-check (load-bearing -- the explicit skip assertion)

- [x] 3.1 Add `NeoStep25ClrAdaptorCheck.cs` under
  `ILRuntime/Runtime/Intepreter/RegisterVM/` (`#if ENABLE_NEO_MODE && DEBUG`,
  mirrors `NeoStep25S3ClrEnumCheck` / `NeoStep25LoadExecCheck`). It loads a
  small dedicated probe assembly with an IL type whose CLR base needs an
  adaptor the compile AppDomain does NOT register (a `class X : SomeClrClass`
  type, where `SomeClrClass` is a CLR class with NO registered adaptor in the
  bare `new AppDomain()`), drives the FILE-PATH `NeoCompiler.Compile` overload,
  and asserts: (a) `Compile` does NOT throw (no `NeoCompilerFatal`); (b)
  `result.IsComplete == false`; (c) `result.Skipped` contains a `(type)
  <X.FullName>` entry whose `ExceptionType == "TypeLoadException"` and whose
  `Message` contains `"Cannot find Adaptor for:"`; (d) a NON-adaptor type in
  the SAME probe IS compiled (present in the result, NOT in `Skipped`). (Design
  D2 + OQ2 default.)
- [x] 3.2 Add the probe type (a dedicated `NeoClrProbe`-style project or an
  existing probe extended with `class AdaptorProbeClrBase {}` + an IL `class
  AdaptorProbe : AdaptorProbeClrBase` -- the CLR base has no adaptor; confirm
  the bare `new AppDomain()` registers none for it). (OQ2 default.)
  **APPLY NOTE:** the task's literal `AdaptorProbeClrBase {}` suggestion would
  NOT trigger the adaptor lookup (a base defined IN the IL assembly is an
  ILType, and the lookup at `ILType.cs:1593` fires only for a CLRType base).
  Shipped instead as `NeoClrProbe.AdaptorProbe : ILRuntimeTest.TestFramework.
  TestClass2` (a real harness-adaptor CLR class) + `NeoClrProbe.ExceptionProbe
  : System.Exception` (a built-in-adaptor CLR class) -- the faithful shapes.
- [x] 3.3 Wire the `NeoStep25ClrAdaptor` CLI hook in
  `ILRuntimeTestCLI/Program.cs` (mirrors `NeoStep25S3ClrEnum` /
  `NeoStep25LoadExec`; computes the probe DLL + ref paths from the TestCases
  path).
- [x] 3.4 Run the self-check: `NeoStep25ClrAdaptor` -> all cells passed, 0
  failed. The decisive cell: BEFORE the fix `Compile` throws
  `NeoCompilerFatal("serializer failure: Cannot find Adaptor...")`; AFTER the
  fix it returns a `NeoCompilerResult` with the type in `Skipped` (exit-2
  equivalent, no fatal).

## 4. The V1-B literal-CLI smoke (standalone-process no-fatal proof)

- [x] 4.1 Run the standalone CLI binary on the FULL `TestCases.dll` with
  `ILRuntimeTestBase.dll` as the ref: `ilrt_neoc TestCases.dll out.neo
  ILRuntimeTestBase.dll`. EXIT `0` or `2` (NOT `1`). NO `FATAL` /
  `Cannot find Adaptor` line on stderr. The harness-adaptor types
  (`TestClass2/3/4`, `ClassInheritanceTest/2`, `IDisposable`, interface-based)
  appear as `SKIP (type) ...` lines. (The standalone process does NOT register
  harness adaptors -- this is the generic-CLI robustness proof.)
  **APPLY RESULT (fixer-a2, 2026-07-08):** MET. **Exit 2** (NOT 1). **ZERO
  `ilrt_neoc: FATAL` lines** (grep count = 0). The harness-adaptor types +
  the A2 anonymous type appear as `SKIP (type) ...` lines (69 type-skips). The
  standalone generic-CLI robustness is proven end-to-end on full TestCases.
- [x] 4.2 `out.neo` is valid: magic `0x494C524E`, and loadable -- read it back
  via `NeoAssemblyReader.Read` in the self-check (cell 3.1 step) and confirm a
  non-null `NeoAssemblyModel`.
  **APPLY RESULT (fixer-a2):** MET on full TestCases -- `out.neo` magic
  `0x494C524E`, 1043473 bytes. (The self-check's read-back of the PROBE .neo
  was already valid -- cell 2 of `NeoStep25ClrAdaptorCheck`, unchanged.)

## 5. Regression + Legacy-neutrality

- [x] 5.1 NeoStep smoke green: the current green baseline (e.g. 219/0/0 or the
  apply-time current count), 0 failed (ZERO regressions -- the fix is in
  `CompileCore`, which the NeoStep runtime never calls).
  **APPLY RESULT:** `Ran 219 tests, 0 failded, 0 ignored, 0 todos` -- ZERO
  regressions.
- [x] 5.2 NeoStep22/23/24/25 self-checks unchanged: 22=55/55, 23=15/15, 24=5/5,
  25-LoadExec=28/28, 25-S3ClrEnum=7/7 (the fix touches only the standalone
  compile path; the in-process self-check compile path is unaffected for
  adaptor-RESOLVING probes). **APPLY RESULT:** NeoStep25S3ClrEnum re-run 7/7
  (the representative in-process self-check that drives `Compile`); the others
  use adaptor-resolving probes (no CLR base) so the pre-filter is a no-op for
  them -- unaffected, not re-run individually.
- [x] 5.3 Legacy-neutral build: `dotnet build ILRuntime/ILRuntime.csproj -c
  Debug` -> 0 errors (`NeoCompiler.cs` compiles out under plain Debug).
  **APPLY RESULT:** 0 errors confirmed.
- [x] 5.4 S3-5 TestCLREnum shape still works (no regression): the host-CLR-type
  resolution path (`Assembly.LoadFrom` ref loop at `NeoCompiler.cs:138-154`) is
  UNCHANGED -- a TestCLREnum-referencing input still compiles clean (the enum
  is a primitive, no adaptor). Re-run `NeoStep25S3ClrEnum` (7/7).
  **APPLY RESULT:** NeoStep25S3ClrEnum 7/7 confirmed.

## 6. Ship

- [x] 6.1 Update `.trae/documents/neo-deferred-items.md`: mark the STEP-25
  `TestClass2` / cross-binding-adaptor gap RESOLVED by this change (points at
  this change). The S3-5 apply note A1 logged it; this change closes it.
  **APPLY RESULT:** STEP-25-CLR-ADAPTOR marked RESOLVED (adaptor scope);
  `NEO-AOT-FIELDINIT-NRE` row added by implementer-1, then marked RESOLVED by
  fixer-a2 (A2 closure + A3 extension).
- [x] 6.2 Run `openspec validate neo-step25-clr-adaptor` (or the review-cycle)
  -> archive on approval. **READY** (fixer-a2): the binding exit-0/2-on-full-
  TestCases bar is MET (A2 + A3 closed); all sections green; Legacy-neutral.
  The review-cycle / archive is the LEAD's next step.

## 7. A2 closure + A3 extension (fixer-a2, 2026-07-08)

The implementer's recommended Option 2 (clean, spec-preserving TLE fix),
applied verbatim, PLUS the A3 null-body-method sibling that surfaced during
the A2 verify (the NEXT fatal after A2 closed -- same binding bar).

- [x] 7.1 **ILType.InitializeFields TLE throw** (`ILRuntime/CLR/TypeSystem/
  ILType.cs`, `#if ENABLE_NEO_MODE`-gated): when `fieldType` (instance) /
  `staticFieldType` (static) is null after the `FindGenericArgument` /
  `appdomain.GetType` assignment, throw `TypeLoadException("Cannot resolve
  field type '...' for type '...'")` BEFORE the `IsPrimitive` NRE site.
  Mirrors the adaptor-lookup throw sites at `ILType.cs:1505/1568/1593`. Neo-
  gated (inside the existing `#if ENABLE_NEO_MODE` block that already gated
  the NRE) -> Legacy byte-identical (plain `Debug` ILRuntime build = 0 errors;
  the null-fieldType path was already latent in Legacy and is NOT reached by
  the Neo-only AOT CLI).
- [x] 7.2 **CompileCore pre-filter field-init trigger** (`ILRuntime/Runtime/
  NeoAOT/NeoCompiler.cs`): add `_ = type.TotalPrimitiveSize;` to the per-type
  pre-filter's try block (after `FirstCLRBaseType`/`FirstCLRInterface`).
  `TotalPrimitiveSize`'s getter calls `InitializeFields` when `fieldMapping==
  null`, so the A2 TLE fires INSIDE the existing `TypeLoadException` catch ->
  the type enters `result.Skipped` (exit 2), NEVER reaching `Write`/
  `BuildTypeDef`. The narrow-TLE-catch invariant (design D1) is UNCHANGED --
  no broadened catch.
- [x] 7.3 **A3 null-body-method silent-skip** (`NeoCompiler.cs`, CompileCore
  per-method loop): add `if (ilm.Definition != null && !ilm.Definition.HasBody)
  continue;` (mirrors the existing `IsGenericInstance` silent-skip). A
  delegate `Invoke`/`BeginInvoke`/`EndInvoke` (or abstract/extern/PInvoke
  method) has `MethodDefinition.HasBody == false`; `ILMethod.InitCodeBody`
  guards `if (def.HasBody)` so `BodyRegister` returns null WITHOUT throwing,
  the force-compile loop silently admitted it to `methods[]`, and
  `NeoAssemblyWriter.CompileFresh`'s JIT NREd on the null body
  (`JITCompiler.Compile` :360). This is a PRE-COMPILE FILTER (not a broadened
  catch): a body-bearing method whose JIT NREs still throws in the force-
  compile try below and is recorded as a skip; a genuine `CompileFresh` bug on
  a body-bearing method still stays a loud fatal.
- [x] 7.4 Re-run the full matrix after the A2+A3 fix: NeoStep25ClrAdaptor 7/7,
  NeoStep 219/0/0, NeoStep25S3ClrEnum 7/7, `ilrt_neoc TestCases.dll out.neo
  ILRuntimeTestBase.dll` -> exit 2, NO fatal, valid `.neo` (magic
  `0x494C524E`, 1MB, 69 type-skips + 181 method-skips, "compiled 1470 methods,
  96 templates, 445 types"), Legacy-neutral (plain Debug ILRuntime 0 errors).

---

## Apply notes (deltas from the plan, for the reviewer/LEAD -- to be filled at apply)

- **STATUS (fixer-a2, 2026-07-08):** The adaptor fix (Section 1) shipped green
  (implementer-1). The A2 gap (anonymous-type field-init NRE) is now CLOSED
  (Section 7) via the clean, spec-preserving TLE fix, AND the A3 null-body-
  method NRE (surfaced during the A2 verify) is closed by a pre-compile
  silent-skip. The binding exit-0/2-on-full-`TestCases.dll` bar (tasks 2.3 /
  2.4 / 4.1 / 4.2) is now **MET**: `ilrt_neoc TestCases.dll out.neo
  ILRuntimeTestBase.dll` -> **exit 2**, **NO fatal** (neither `Cannot find
  Adaptor` NOR the field-init NRE NOR the null-body NRE), valid `.neo` (magic
  `0x494C524E`). NeoStep25ClrAdaptor 7/7, NeoStep 219/0/0, NeoStep25S3ClrEnum
  7/7, Legacy-neutral. The change is ready for archive (task 6.2).

- **A2 (RESOLVED 2026-07-08 by fixer-a2 -- was: the anonymous-type field-init
  NRE; blocked exit-0/2).** The original diagnosis (kept for the record): After the adaptor pre-filter closes the `Cannot find Adaptor`
  fatal, the standalone CLI on the FULL `TestCases.dll` advances to a NEW,
  DIFFERENT fatal: `serializer failure: Object reference not set to an
  instance of an object.` Stack: `ILType.InitializeFields` (`ILType.cs:~2292`,
  `fieldType.IsPrimitive` on a null `fieldType`) <- `NeoAssemblyWriter.
  BuildTypeDef` (`type.TotalPrimitiveSize`) <- `Write` <- `CompileCore`.
  Reproduced type: `<>f__AnonymousType0`2<j,k>` -- a compiler-generated
  ANONYMOUS type (an OPEN GENERIC type definition whose fields are generic-
  parameter-typed); `appdomain.GetType(field.FieldType)` returns null
  (`FindGenericArgument` on the open definition yields null) -> `IsPrimitive`
  NREs. The adaptor pre-filter does NOT trigger field init (only
  `FirstCLRBaseType`+`FirstCLRInterface`), so this survivor passes the
  pre-filter and NREs during `Write`. This is NOT a CrossBindingAdaptor
  issue (anonymous types need no adaptor). Per THIS change's spec scenario
  "a non-adaptor type-init failure stays a loud fatal" (which uses exactly
  "a genuine NullReferenceException" as its example), the NRE correctly
  fatals today -- the change INTENTIONALLY does not catch it (catching it
  would violate that scenario + the design D1 narrow-`TypeLoadException`
  catch). The full-TestCases standalone smoke therefore still exits 1 (on
  the NRE, NOT on `Cannot find Adaptor`); the skip report is not printed
  (Compile throws before returning). RECOMMENDED follow-on (clean, spec-
  preserving): make `ILType.InitializeFields` throw `TypeLoadException` when
  `GetType` returns null (mirroring the adaptor sites at `ILType.cs:1505/
  1568/1593`), then add `_ = type.TotalPrimitiveSize;` to the `CompileCore`
  pre-filter -> the EXISTING TLE catch skips it (exit 2) with NO spec-
  scenario violation. ALTERNATIVE (needs a spec amendment): broaden the
  pre-filter catch to skip field-init NREs directly. Logged as
  `NEO-AOT-FIELDINIT-NRE` in `.trae/documents/neo-deferred-items.md`.
  **CLOSURE (fixer-a2):** the RECOMMENDED clean fix shipped EXACTLY as
  specified (Section 7.1 + 7.2). The `NEO-AOT-FIELDINIT-NRE` deferred row is
  marked RESOLVED. A3 (the null-body-method CompileFresh NRE, a sibling that
  surfaced during the A2 verify) was also closed (Section 7.3) -- it was the
  NEXT fatal after A2 closed, blocking the same binding bar.

- **A1 (built-in adaptors are a NO-OP).** The dump PROVED the AppDomain ctor
  (`AppDomain.cs:237`/`:244`) already registers the only two built-in adaptors
  (`AttributeAdapter`, `ExceptionAdaptor`), and the compile AppDomain is
  constructed via `new AppDomain()` (`NeoCompiler.cs:69`) which runs the ctor.
  So an IL `class X : System.Exception` resolves TODAY. The fix surface is
  ONLY the graceful skip (task 1) -- NO built-in adaptor registration is added.
  Record this so a future reader does not mis-attribute the fix to a "register
  the built-in adaptors in the CLI" change that does not exist.
- **OQ1/OQ2/OQ3 defaults (CONFIRMED at apply):** OQ1 = the `(type) <FullName>`
  marker reusing `MethodSkip` (Program.cs unchanged) -- shipped exactly so
  (the CLI prints `SKIP (type) <FullName>: TypeLoadException: ...` unchanged).
  OQ2 = a small dedicated probe (`NeoClrProbe.AdaptorProbe : TestClass2` +
  `NeoClrProbe.ExceptionProbe : System.Exception`) for the explicit skip
  assertion -- shipped (7/7 self-check); the full-`TestCases.dll` V1-B is the
  standalone no-ADAPTOR-fatal proof (the adaptor fatal IS gone; the remaining
  fatal is the A2 field-init NRE, a distinct gap). OQ3 = the host-side
  `Compile(IReadOnlyList<ILType>, Stream)` overload gets the skip for free
  (same `CompileCore`) -- confirmed, no separate edit (the NeoStep25ClrAdaptor
  self-check drives the FILE-PATH overload; both route through CompileCore).
