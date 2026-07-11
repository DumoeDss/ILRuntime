# Planning Context — neo-step25-clr-adaptor (STEP-25-CLR-ADAPTOR TRUE COMPLETION)

> SEED for the planner. TRUE COMPLETION: the standalone AOT CLI handles IL types
> whose CLR-class base needs a CrossBindingAdaptor (no serializer fatal).

## What this change is (one line)

After S3-5 (TestCLREnum resolved), the standalone `ilrt_neoc` on a full
`TestCases.dll` gets past the enum but FATALS at the serializer with
`Cannot find Adaptor for:ILRuntimeTest.TestFramework.TestClass2` (exit 1).
`TestClass2` is a CLR class used as a base by IL types; the in-process harness
registers a `CrossBindingAdaptor` for it via `CLRBindings.Initialize`, but the
standalone CLI registers NONE. This child makes the standalone CLI ROBUST to
adaptor-requiring types (no fatal).

## The dump-gate (binding -- the dump decides the fix surface)

On HEAD `6ee984ea`:
1. Reproduce: `ilrt_neoc TestCases.dll out.neo <refs>` -> the `Cannot find
   Adaptor for:TestClass2` serializer fatal. Cite the throw site (the
   `ILType` ctor / the adaptor-lookup; the S3-5 planner noted
   `ILType.cs:1412-1418` throws `TypeLoadException("Cannot find Adaptor for:X")`).
2. How does the IN-PROCESS harness avoid it? `CLRBindings.Initialize` (called in
   the test harness) registers the adaptors. Does the standalone CLI have an
   equivalent hook? What adaptors are BUILT-IN (e.g. the Step-14
   `ExceptionAdaptor` is built-in in the AppDomain ctor) vs test-harness-specific?
3. The fix-surface question: is the right fix (a) REGISTER the built-in adaptors
   in the standalone CLI's compile AppDomain (the ExceptionAdaptor + any
   ILRuntime-builtin), AND (b) make the serializer/loader GRACEFULLY SKIP types
   needing UNREGISTERED (test-harness-specific) adaptors (exit 2, partial `.neo`,
   NOT a fatal)? The CLI is generic -- it cannot know a given input's test-harness-
   specific adaptor needs, so graceful-skip is the robust contract for those.

## Scope (TRUE COMPLETION -- robustness, not test-harness-coupling)

The standalone CLI is a GENERIC tool. The robust contract: it SHALL NOT fatal on
adaptor-requiring types. Two complementary fixes:
- REGISTER the ILRuntime BUILT-IN adaptors (the ones that ship with the runtime --
  e.g. the `ExceptionAdaptor` from Step 14; audit what's built-in vs harness-only)
  in the compile AppDomain, so types whose base needs a built-in adaptor resolve.
- For types whose base needs a test-harness-SPECIFIC (non-builtin) adaptor, the
  serializer/loader SHALL SKIP the type gracefully (record it in the skip report;
  exit 2, partial `.neo`), NOT fatal. This is the same additive-skip contract as
  the existing NeoCompiler per-method try/catch skip.

Success criterion: `ilrt_neoc TestCases.dll out.neo <refs>` produces a valid
`.neo` (exit 0 or 2) with NO `Cannot find Adaptor` fatal; the adaptor-requiring
types are either resolved (builtin adaptor) or skipped (reported). The S3-5
TestCLREnum shape still works (no regression).

## Authoritative prior context

1. `openspec/changes/archive/2026-07-08-neo-step25-s3-clr-registration/{design.md, ship-log.md}`
   -- S3-5; this CLR-ADAPTOR gap is the durable finding #1 it surfaced.
2. `openspec/changes/archive/2026-07-07-neo-step24-ilrt-neoc/` -- Step 24 (the CLI).
3. `ILRuntime/Runtime/NeoAOT/NeoCompiler.cs` (the CLI driver; the per-method skip
   try/catch is the skip-contract precedent) + `ILType.cs:1412-1418` (the
   adaptor-lookup throw) + the AppDomain ctor (built-in adaptor registration --
   the Step-14 ExceptionAdaptor) + `ILRuntimeTestBase`'s `CLRBindings.Initialize`
   (the harness adaptor registration -- test-harness-specific, NOT to be coupled
   into the generic CLI).

## Build + test (CRITICAL -- always `-f net8.0`)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
# reproduce: ilrt_neoc TestCases.dll out.neo <refs> -> Cannot find Adaptor:TestClass2 fatal
# success: ilrt_neoc TestCases.dll out.neo <refs> -> valid .neo (exit 0/2), no fatal, adaptor-requiring types resolved-or-skipped
# regression: NeoStep 219/0/0; NeoStep25LoadExec 28/28; NeoStep25S3ClrEnum 7/7
```
ALWAYS `-f net8.0`. `Debug_Neo` prints huge JIT output -- normal.

## Spec authoring traps
- `specs/neo-optimizer/spec.md` delta PURE ASCII; SHALL-first bodies. Capability: **neo-optimizer**.
- Legacy is the REFERENCE. Neo-only `#if ENABLE_NEO_MODE`. Legacy-neutral.

## Deliverables
`proposal.md`, `design.md` (the dump-gate verdict: the throw site + builtin-vs-
harness adaptor audit + the register-builtin-and-skip-harness fix), `specs/neo-optimizer/spec.md`
(delta), `tasks.md`. Success = CLI robust on TestCases.dll (no adaptor fatal).
