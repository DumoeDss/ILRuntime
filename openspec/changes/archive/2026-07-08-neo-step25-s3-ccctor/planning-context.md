# Planning Context — neo-step25-s3-ccctor (S3-4 TRUE COMPLETION)

> SEED for the planner. TRUE-COMPLETION: a type's static constructor (`.cctor`)
> runs when a `.neo` Cecil-free-loads, so static-field state initializes.

## What this change is

S3-4: static `.cctor` seeding at AOT load. Today `.cctor` is SUPPRESSED under
`ENABLE_NEO_MODE` (`ILType.cs:186-200`); per-static-field offsets are NOT in the
`.neo` `NeoTypeDefRecord` (only the static totals). When a `.neo` Cecil-free-loads
(S3-2), a type with static fields + a `.cctor` would have UNINITIALIZED static
state (the `.cctor` never runs). This child seeds the `.cctor` at AOT load.

## The dump-gate (binding)

On HEAD `f32f816e`:
1. How is `.cctor` triggered on the JIT/Legacy path today? (Lazy on first static-
   field access? Eager at type init?) Cite file:line. Why is it suppressed under
   Neo (`ILType.cs:186-200`)?
2. Does the `.neo` `NeoMethodDefRecord` carry the `.cctor` (the static ctor IS a
   method, compiled into the `.neo`)? The `.cctor` is a `MethodDef`, so it IS in
   the `.neo`. The gap is RUNNING it at load + the per-static-field offsets.
3. Per-static-field offsets: the `NeoTypeDefRecord` carries instance field
   `NeoFieldLayoutRecord`s but NOT static (S3-partial flagged this). Does the
   `.cctor` seeding NEED per-static-field offsets, or can the `.cctor` body (which
   writes statics via static-field tokens) run without them (the static-field
   token resolves via the token maps)?
4. **Scope-aware:** is the fix SMALL (run the `.cctor` MethodDef at AOT load via
   the parametrized-Run entry -- `neo-f4-parametrized-run-entry` shipped the
   parametrized Run; the `.cctor` is a parameterless static method so the OLD
   parameterless Run suffices; or run it directly via ExecuteNeo) + extend the
   record with per-static-field offsets IF needed) or LARGE? Ship if tractable.

## Authoritative prior context

1. `openspec/changes/archive/2026-07-08-neo-step25-s3-cecil-free-load/` -- S3-2
  (the Cecil-free load; the `.cctor` seeding was a sequenced non-goal).
2. `openspec/changes/archive/2026-07-08-neo-f4-parametrized-run-entry/` -- the
   parametrized Run entry (for invoking the `.cctor`).
3. `ILType.cs:186-200` (the `.cctor` suppression) + `NeoAssembly.cs`
  (NeoTypeDefRecord -- does it carry static-field offsets? the `.cctor` MethodDef
   ref?) + `NeoAssemblyLoader.cs` / `AppDomain.LoadNeoAssembly` (the Cecil-free
   load -- where the `.cctor` seeding hooks in).
4. `.trae/documents/neo-deferred-items.md` -- STEP-25-PARTIAL (sub-surface 4 row).

## Scope (TRUE COMPLETION)

Ship the `.cctor` seeding so a Cecil-free-loaded type with static fields + a
`.cctor` has initialized static state. Capstone: a probe type with a static field
+ a `.cctor` that sets it; Cecil-free-load + read the static field -> the `.cctor`-
set value, not default. Adversarial: mutate the `.cctor` body constant -> the read
reflects the mutation (proves the `.cctor` ran, not a default).

## Build + test (CRITICAL -- always `-f net8.0`)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep25CecilFreeLoad   # S3-2 capstone (add the .cctor cell) -- or a new filter
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep     # 223/0/0 regression
```
ALWAYS `-f net8.0`. `Debug_Neo` prints huge JIT output -- normal.

## Spec authoring traps
- `specs/neo-optimizer/spec.md` delta PURE ASCII; SHALL-first. Capability: **neo-optimizer**.
- Legacy is the REFERENCE. Neo-only `#if ENABLE_NEO_MODE`. Legacy-neutral.

## Deliverables
`proposal.md`, `design.md` (the dump-gate verdict + the .cctor-seeding design +
whether per-static-field offsets are needed), `specs/neo-optimizer/spec.md`
(delta), `tasks.md`. Success = a Cecil-free-loaded type's `.cctor`-set static
field reads correctly.
