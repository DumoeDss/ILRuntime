## Why

The standalone `ilrt_neoc` precompile CLI (Step 24) fatal-aborts on any input
whose IL references a **host CLR type** defined in a non-BCL host assembly
(e.g. `ILRuntimeTest.TestFramework.TestCLREnum` in `ILRuntimeTestBase.dll`):
`KeyNotFoundException: Cannot find Type:ILRuntimeTest.TestFramework.TestCLREnum`
at `AppDomain.cs:1409`. The standalone CLI process never registers the host CLR
assembly, so the JIT's Cecil-`TypeReference`-to-`IType` resolution finds it
nowhere. This is the Step-24 V1 deferral ("host-CLR-assembly registration
(TestCLREnum)") and the one gap blocking a full-`TestCases.dll` AOT compile. The
TRUE-COMPLETION bar for the Step-25 AOT chain is that a `.neo` compiled from a
host-CLR-type-referencing input loads and runs with the host CLR type resolved
correctly.

## What Changes

- The standalone CLI (`NeoCompiler.Compile`, the file-path overload at
  `ILRuntime/Runtime/NeoAOT/NeoCompiler.cs`) SHALL register every reference
  assembly passed on the command line with the host CLR (`Assembly.LoadFrom`)
  so the AppDomain's CLR-type fallback (`GetType(string)` at
  `AppDomain.cs:1068`, the live `System.AppDomain.CurrentDomain.
  GetAssemblies()` scan) resolves host CLR types as `CLRType`. This mirrors the
  in-process model (where host CLR assemblies are project references already
  resident in the host `System.AppDomain`).
- The current ref-handling loop (`NeoCompiler.cs:119-136`) which
  `appdomain.LoadAssembly(refStream)`-es every ref SHALL be replaced: loading a
  host CLR assembly as IL wraps its types as `ILType` (shadowing the real CLR
  type) and is provably wrong (it lets compile proceed past the type ref but
  produces a downstream `NullReferenceException`, reproduced on HEAD). Host CLR
  assemblies MUST be registered on the CLR side, not the IL side.
- An end-to-end self-check SHALL prove a TestCLREnum-shaped input compiles to a
  `.neo` via the standalone CLI (no fatal), then loads + runs with the host CLR
  enum value round-tripping correctly.
- NO `.neo` format change and NO `.neo` loader change. The load-side
  (`NeoAssemblyLoader.ResolveTypeRefToIType` at `NeoAssemblyLoader.cs:274`)
  already routes CLR types through `appdomain.GetType(fullName)` (the same CLR
  fallback), so a `.neo` carrying a host-CLR-type ref loads correctly in any
  AppDomain where the host CLR assembly is registered (the in-process test
  harness today; a future standalone Cecil-free loader is the deferred
  sub-surface 2 concern).
- Multi-hotfix-assembly cross-references (one IL hotfix assembly referencing
  another IL hotfix assembly, which DO need `LoadAssembly`) remain a deferred
  V1 limitation; this change's scope is host-CLR-assembly registration only.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `neo-optimizer`: add a requirement that the standalone AOT compile CLI SHALL
  register host CLR reference assemblies with the host CLR so Cecil
  `TypeReference` tokens to host CLR types resolve as `CLRType` at compile time
  (closing the Step-24 `TestCLREnum` fatal), and that the fix MUST NOT register
  host CLR assemblies via the IL `LoadAssembly` path (which shadows CLR types).

## Impact

- **Code:** `ILRuntime/Runtime/NeoAOT/NeoCompiler.cs` (the file-path
  `Compile(string, IReadOnlyList<string>, Stream)` overload's reference-assembly
  loop, ~`NeoCompiler.cs:119-136`). Neo-only (`#if ENABLE_NEO_MODE`).
  Legacy-neutral (the whole file compiles out under plain `Debug`).
- **No change** to: `ExecuteNeo`, the JIT, the optimizer, the Step-22 template
  mechanism, the Step-23 `.neo` format, the Step-25 `.neo` loader, `ILMethod`,
  `ILType`, or `AppDomain`'s resolution methods. The fix is a single new caller
  of the existing CLR-resolution path.
- **Tooling:** `ILRuntimeNeoCompiler/Program.cs` is UNCHANGED (it already
  forwards ref paths to `NeoCompiler.Compile`); the fix is entirely in the
  driver.
- **Tests:** a new host-side self-check (mirrors `NeoStep25LoadExecCheck`) +
  a minimal V1-B literal-CLI smoke on a TestCLREnum-referencing DLL. Regression
  gates: NeoStep smoke (green), NeoStep22/23/24/25 self-checks (unchanged).
- **Risk:** LOW. The replaced `LoadAssembly(refStream)` loop was never verified
  by any passing test (Step-24 V1-B was BCL-refs-only; the ref-`LoadAssembly`
  path is documented V1-A-UNVERIFIED). The new `Assembly.LoadFrom` path is
  best-effort try/catch; a ref that cannot be CLR-loaded (rare) is skipped, and
  resolution falls back to the existing BCL/CLR scan.
