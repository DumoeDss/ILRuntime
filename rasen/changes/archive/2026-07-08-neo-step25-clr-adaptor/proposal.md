## Why

The standalone `ilrt_neoc` precompile CLI (Step 24) fatal-aborts on any input
containing an IL type whose base class is a host CLR class that needs a
`CrossBindingAdaptor`: `TypeLoadException: Cannot find Adaptor
for:ILRuntimeTest.TestFramework.TestClass2` (exit 1), reproduced on HEAD
`6ee984ea`. The standalone CLI's fresh compile AppDomain registers the
ILRuntime BUILT-IN adaptors (the `ExceptionAdaptor` + `AttributeAdapter` from
the AppDomain ctor), but NONE of the test-harness-specific adaptors the
in-process test runtime registers via `ILRuntimeHelper.Init` (helper.cs:22-29).
A generic compile tool cannot -- and MUST NOT -- couple a test-harness
adaptor set. This is the durable finding #1 surfaced by the S3-5 TRUE-
COMPLETION slice (its apply note A1), and the last gap blocking a clean
full-`TestCases.dll` AOT compile. The TRUE-COMPLETION bar for the Step-25 AOT
chain is that `ilrt_neoc TestCases.dll out.neo <refs>` produces a valid `.neo`
(exit 0 or 2) with NO adaptor fatal: adaptor-requiring types are either
RESOLVED (a built-in adaptor) or SKIPPED gracefully (a harness-specific
adaptor).

## What Changes

- The standalone CLI (`NeoCompiler.CompileCore`, the shared core both the
  file-path CLI overload and the host-side overload call) SHALL GRACEFULLY
  SKIP any input IL type whose lazy CLR-base/interface adaptor resolution
  throws `TypeLoadException("Cannot find Adaptor for:...")` -- the throw
  sites at `ILType.cs:1505` (CLR interface), `:1568` (generic-instance CLR
  base), `:1593` (non-generic CLR base, the TestClass2 case). The type is
  recorded in the existing skip report, ALL its methods are omitted, the
  `.neo` is still written for the survivor subset, and the CLI returns exit
  code 2 (partial) -- NOT exit 1 (fatal). This is the SAME additive-skip
  contract as the existing per-method try/catch in `CompileCore`, lifted to
  TYPE granularity.
- The fix SHALL pre-filter the input type set by eagerly triggering the lazy
  adaptor resolution (touching each type's `FirstCLRBaseType` +
  `FirstCLRInterface` getters, which drive `InitializeBaseType` /
  `InitializeInterfaces`) INSIDE a per-type try/catch, so a type needing an
  UNREGISTERED adaptor is excluded BEFORE the per-method compile loop AND
  before `NeoAssemblyWriter.Write` (whose TypeDef emission at
  `NeoAssemblyWriter.cs:755`/`:762` also touches `BaseType`). Only the
  survivor types are passed to the loop and to `Write`.
- The ILRuntime BUILT-IN adaptors (`Adapters.ExceptionAdaptor` at
  `AppDomain.cs:244`, `Adapters.AttributeAdapter` at `AppDomain.cs:237`) are
  ALREADY registered by the AppDomain ctor, which `new AppDomain()` in
  `NeoCompiler.Compile` (NeoCompiler.cs:69) calls. Therefore an IL type whose
  CLR base needs a built-in adaptor (e.g. `class X : System.Exception`,
  `class Y : System.Attribute`) RESOLVES TODAY in the standalone CLI -- no
  additional built-in registration is needed. The fix surface is the graceful
  skip of types needing a NON-builtin (harness-specific) adaptor.
- The standalone CLI SHALL NOT couple any test-harness-specific adaptor (the 8
  adaptors `ILRuntimeHelper.Init` registers: `TestClass2Adapter`,
  `TestClass3Adaptor`, `TestClass4Adaptor`, `ClassInheritanceTestAdaptor`,
  `ClassInheritanceTest2Adaptor`, `InterfaceTestAdaptor`,
  `IDisposableAdapter`, `IAsyncStateMachineClassInheritanceAdaptor`). Those
  live in the `ILRuntimeTestBase` test-framework assembly; the generic CLI
  cannot reference it. Types needing one of them are SKIPPED, never resolved,
  by this change.
- NO `.neo` format change and NO `.neo` loader change. The skip is purely on
  the COMPILE side (`NeoCompiler.CompileCore`); the load side
  (`NeoAssemblyLoader`) is unchanged (it already skips a type/method it
  cannot bind -- the additive contract).
- `ILRuntimeNeoCompiler/Program.cs` is UNCHANGED. The type-level skips reuse
  the existing `NeoCompilerResult.Skipped` report list (with a clear type-
  level display marker) so the CLI's existing `SKIP <display>` printer + the
  `IsComplete` -> exit-2 logic apply unchanged.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `neo-optimizer`: add a requirement that the standalone AOT compile CLI
  SHALL gracefully SKIP (not fatal) an IL type whose CLR-class base or CLR
  interface needs a `CrossBindingAdaptor` that is NOT registered in the
  compile AppDomain (the harness-specific adaptor case), recording the type
  in the skip report and writing a partial `.neo` (exit 2); and that the
  built-in adaptors registered by the AppDomain ctor (ExceptionAdaptor,
  AttributeAdapter) SHALL keep resolving (an IL type inheriting a built-in-
  adaptor CLR base is NOT skipped). The CLI SHALL NOT couple any test-harness-
  specific adaptor into the generic compile path.

## Impact

- **Code:** `ILRuntime/Runtime/NeoAOT/NeoCompiler.cs` (the `CompileCore`
  method shared by both `Compile` overloads; a new per-type pre-filter try/
  catch around the lazy adaptor resolution + a `MakeTypeSkip` helper, +
  routing the survivor type list to the existing per-method loop AND
  `NeoAssemblyWriter.Write`). Neo-only (`#if ENABLE_NEO_MODE` -- the whole
  `NeoCompiler.cs` file is). Legacy-neutral (the file compiles out under
  plain `Debug`).
- **No change** to: `ExecuteNeo`, the JIT, the optimizer, the Step-22 template
  mechanism, the Step-23 `.neo` format, the Step-25 `.neo` loader, `ILMethod`,
  `ILType`, `AppDomain`'s adaptor-lookup / resolution methods,
  `NeoAssemblyWriter`, or `ILRuntimeNeoCompiler/Program.cs`. The fix is a new
  caller of existing, unchanged `ILType` lazy-init getters.
- **Tooling:** `ILRuntimeNeoCompiler/Program.cs` is UNCHANGED (the type-skip
  reuses the existing `Skipped` report + exit-2 path).
- **Tests:** a new host-side self-check (mirrors `NeoStep25LoadExecCheck` /
  `NeoStep25S3ClrEnumCheck`) proving a CLR-class-base type is skipped (not
  fatal) + a V1-B literal-CLI smoke on the full `TestCases.dll` proving exit
  0/2 with NO `Cannot find Adaptor` fatal. Regression gates: NeoStep smoke
  (green), NeoStep22/23/24/25 self-checks (unchanged).
- **Risk:** LOW. The change converts a fatal (exit 1, no `.neo`) into a
  partial (exit 2, valid `.neo` for the survivor subset) for types the CLI
  could NEVER have compiled anyway (they need an adaptor the generic CLI does
  not register). Every type that resolves today (built-in adaptor, or no CLR
  base) is unaffected: the pre-filter's eager init is exactly the resolution
  the compile would do moments later, and the init is memoized, so there is
  no behavior change for resolving types -- only the THROW path flips from
  fatal to skip.
