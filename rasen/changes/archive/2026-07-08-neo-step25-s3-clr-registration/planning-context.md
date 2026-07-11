# Planning Context — neo-step25-s3-clr-registration (S3-5 TRUE COMPLETION)

> SEED for the planner. TRUE-COMPLETION: the standalone AOT CLI + the .neo loader
> SHALL resolve host CLR types (the Step-24 TestCLREnum gap is the reproducer).

## What this change is (one line)

S3-5: make the standalone `ilrt_neoc` precompile CLI (and the runtime `.neo`
load path) resolve **host CLR types** so a `.neo` compiled from an assembly that
references a host CLR assembly (e.g. one declaring a CLR enum like `TestCLREnum`)
does not fatal-fail on CLR-type resolution. The Step-24 gap: the standalone CLI
does not register the host CLR assemblies, so a `TypeRef` to a host CLR type
fails to resolve.

## The reproducer (the dump-gate)

Per the prior handoff: "the full-TestCases.dll CLI run fails with a CLR-type-
resolution fatal (`TestCLREnum`, a host CLR assembly the standalone CLI doesn't
register)." So:
1. Run `ilrt_neoc` on an input containing a `TestCLREnum` (or similar host-CLR-
   enum) reference -> confirm the fatal (the reproducer). Cite the error + the
   resolution site.
2. The fix: register the host CLR assemblies (the ones the input references) so
   the Cecil `TypeReference` -> CLR `Type` resolution works at compile + the
   `.neo` `TypeRef` -> CLR `Type` resolution works at load.

## Authoritative prior context (READ BEFORE PROPOSING)

1. `openspec/changes/archive/2026-07-07-neo-step24-ilrt-neoc/{design.md, ship-log.md}`
   -- Step 24 (the standalone CLI); the TestCLREnum gap is the noted deferral
   ("host-CLR-assembly registration (TestCLREnum)").
2. `openspec/changes/archive/2026-07-07-neo-step25-runtime-loader/` -- the .neo
   loader; the CLR-type resolution path at load.
3. `openspec/changes/neo-completion-portfolio/handoff/lead-2.md` -- portfolio
   context (S3-5 is a completion-wave item).
4. The AOT code: `ILRuntime/Runtime/NeoAOT/NeoCompiler.cs` (the CLI driver; the
   reference-assembly handling at `:131-134` -- the "silent-skip" the S3 planner
   flagged) + `ILRuntimeNeoCompiler/Program.cs` (the CLI arg parse: input.dll +
   ref-assembly paths).
5. `.trae/documents/neo-deferred-items.md` -- the STEP-25-PARTIAL row (S3-5 is
   the CLR-registration deferral).

## Dump-gate (binding)

On HEAD `d3092cb7`:
1. Reproduce the TestCLREnum fatal via the CLI (or an equivalent host-CLR-enum
   reference). What is the EXACT resolution site that fails (Cecil `TypeReference`
   -> CLR `Type` at compile? the `.neo` `TypeRef` -> CLR `Type` at load? both?)?
   Cite file:line.
2. How does the IN-PROCESS path (the runtime AppDomain, which CAN resolve host
   CLR types via `LoadedModules`/assembly-qualified names) differ from the
   standalone CLI path? The fix likely mirrors the in-process registration.
3. Is the fix a CLI-side assembly registration (pass the host CLR assemblies as
   refs + register them in the compile AppDomain) or a `.neo` format/loader
   change (record the CLR aqname + resolve at load)?

The dump decides. Do NOT guess.

## Scope (TRUE COMPLETION)

Ship the fix that makes the TestCLREnum-shaped input compile + load + run via the
standalone CLI without a CLR-resolution fatal. The success criterion: a `.neo`
compiled from a TestCLREnum-referencing input, loaded + executed, resolves the
host CLR enum correctly (no fatal; the enum value round-trips). Construct an
adversarial self-check (a NeoStep25-style cell, or a CLI roundtrip assertion)
proving the host-CLR-type resolution works end-to-end.

## Build + test (CRITICAL -- always `-f net8.0`)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj -c Debug_Neo   # the standalone CLI
dotnet build TestCases/TestCases.csproj -c Debug
# reproduce the fatal: ilrt_neoc on a TestCLREnum-referencing input -> the CLR-resolution fatal
# regression: NeoStep 219/0/0; NeoStep25LoadExec 28/28; NeoStep22/23/24 self-checks
```
ALWAYS `-f net8.0`; CLI filter is a Contains substring (no `|`). `Debug_Neo`
prints huge JIT output -- normal.

## Spec authoring traps (from handoff)

- `specs/neo-optimizer/spec.md` delta PURE ASCII; start every requirement body
  with "... SHALL ..." on the FIRST hard-wrapped line. Capability: **neo-optimizer**.
- Legacy is the REFERENCE. Neo-only `#if ENABLE_NEO_MODE`. Legacy-neutral.

## Deliverables

`proposal.md`, `design.md` (the dump-gate verdict: the exact resolution site +
the fix surface + whether it's CLI-side registration or .neo format), `specs/neo-optimizer/spec.md`
(delta), `tasks.md`. Success = TestCLREnum-shape compiles+loads+runs via the CLI.
