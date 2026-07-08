# Planning Context — neo-step25-s3-cecil-free-load (S3-2 TRUE COMPLETION)

> SEED for the planner. TRUE-COMPLETION: load a `.neo` into an AppDomain that has
> NOT Cecil-loaded the types -- the TRUE AOT deployment scenario (no Mono.Cecil at
> load). Scope-AWARE: this is the hardest remaining AOT piece; ship the tractable
> slice, sequence the rest.

## What this change is

S3-2: the Cecil-free AppDomain load. Today `NeoAssemblyLoader.Attach` runs in the
SAME AppDomain that Cecil-compiled the `.neo` (S1/S2/S3-partial). The TRUE AOT
scenario is a FRESH AppDomain that has the `.neo` but NOT the Cecil `TypeDefinition`s
-- the runtime resolves types PURELY from the `.neo` tables. The S3-partial child
shipped the ILType layout + VTable REBUILD from `NeoTypeDefRecord` as a DEBUG
completeness proof (NOT installed on a live type); S3-2 INSTALLS it + provides the
Cecil-free load path.

## The dump-gate (binding -- the dump decides the fix surface + scope)

On HEAD `de0ef01c`:
1. What does `AppDomain.LoadAssembly` require today (a Cecil stream)? Where does
   the `.neo`-only load path bypass it? Cite file:line.
2. The S3-partial `ILType.RebuildFromNeoRecord` rebuilds layout + VTable as PURE
   DATA but does NOT install on a live ILType. S3-2 must INSTALL it (or build a
   fresh ILType from the record). What does an ILType ctor read from Cecil
   (`TypeDefinition`) that the `.neo` `NeoTypeDefRecord` must substitute? Is the
   record complete enough (the S3-partial ship-log flagged `naturalAlignment` +
   per-static-field offsets as gaps)?
3. The token-resolution path: same-AppDomain (S1/S2) resolves via the existing
   `mapTypeToken`/`mapMethod` (populated at Cecil-load). A Cecil-free load has NO
   Cecil-load -- so the token maps must be populated from the `.neo` reference
   tables. Does S3-2 need cross-AppDomain re-resolution (S3-3, Approach 1), or is
   there a same-process Cecil-free path where the refs resolve via the host CLR
   (the S3-5 `Assembly.LoadFrom` pattern)?
4. **Scope-aware:** S3-2 is the hardest AOT piece. Realistic outcomes: (a) a
   Cecil-free load into a FRESH AppDomain in the SAME PROCESS (the host CLR
   resolves CLR refs via `Assembly.LoadFrom`; IL refs via the `.neo` tables --
   NO cross-AppDomain token re-resolution needed since it's same-process) --
   SHIP if tractable; (b) the cross-PROCESS/cross-AppDomain-identity re-resolution
   (S3-3) SEQUENCED. The dump decides the boundary.

## The likely ship slice (the dump refines)

A Cecil-free load into a fresh same-process AppDomain:
- A new `AppDomain` load entry that takes a `.neo` (not a Cecil stream) + the host
  CLR ref assemblies.
- For each `NeoTypeDefRecord`: build a live `ILType` from the record (install the
  S3-partial rebuild -- layout + VTable + interface map) WITHOUT a Cecil
  `TypeDefinition`.
- The `.neo` `TypeRef`/`MethodRef`/`FieldRef` tables resolve via the host CLR
  (`Assembly.LoadFrom` for CLR types, S3-5 pattern) + the `.neo` `TypeDef` table
  for IL types.
- A capstone self-check: compile a `.neo` in AppDomain A; load it into a FRESH
  AppDomain B (no Cecil); execute a method via ExecuteNeo -> correct result.
  Adversarial: a body-mutation / structural-equivalence probe (the S3-partial
  pattern) proving the Cecil-free load genuinely uses the `.neo` tables.

## Authoritative prior context

1. `openspec/changes/archive/2026-07-08-neo-step25-s3-full-decoupling/` -- S3-
  partial (the ILType RebuildFromNeoRecord + ResolveVTableFromRecord; the
   completeness proof; the NOT-installed-on-live-type constraint S3-2 lifts).
2. `openspec/changes/archive/2026-07-08-neo-step25-s3-clr-registration/` +
   `neo-step25-clr-adaptor/` -- S3-5 (host CLR ref registration via
   `Assembly.LoadFrom`) + the CLI robustness (the patterns S3-2 reuses for the
   fresh-AppDomain CLR refs).
3. `openspec/changes/archive/2026-07-07-neo-step25-runtime-loader/` -- S1 (the
   same-AppDomain loader; `NeoAssemblyLoader.Attach`).
4. `ILRuntime/Runtime/NeoAOT/NeoAssemblyLoader.cs` + `NeoAssembly.cs` (the
   `NeoTypeDefRecord` + ref tables) + `AppDomain.cs` (`LoadAssembly`,
   `LoadedTypes`, `mapTypeToken`/`mapMethod`).
5. `.trae/documents/neo-deferred-items.md` -- STEP-25-PARTIAL (S3-2 row).

## Build + test (CRITICAL -- always `-f net8.0`)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep25LoadExec   # 28/28 (S1/S2/S3-partial; the S3-2 capstone adds the Cecil-free cell)
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep     # 223/0/0 regression
```
ALWAYS `-f net8.0`. `Debug_Neo` prints huge JIT output -- normal.

## Spec authoring traps
- `specs/neo-optimizer/spec.md` delta PURE ASCII; SHALL-first. Capability: **neo-optimizer**.
- Legacy is the REFERENCE. Neo-only `#if ENABLE_NEO_MODE`. Legacy-neutral.

## Deliverables
`proposal.md`, `design.md` (the dump-gate verdict: the Cecil-free load boundary;
install-the-rebuild design; same-process-fresh-AppDomain vs cross-AppDomain scope;
file:line-cited), `specs/neo-optimizer/spec.md` (delta), `tasks.md`. Success = a
`.neo` loaded into a Cecil-free fresh AppDomain executes correctly.
