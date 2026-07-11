# Planning Context — neo-step25-s3-full-decoupling

> SEED for the planner. Read THIS FIRST, then the docs it points at, then
> research only what is missing. APPEND durable findings after propose.

## What this change is (one line)

Step 25 **S3** — the FULL Cecil-decoupling + cross-AppDomain slice: rebuild an
`ILType` from a `.neo` `NeoTypeDefRecord` WITHOUT Cecil (field layout +
VTable), load a `.neo` into an AppDomain that has NOT Cecil-loaded the types,
re-resolve token hashes across that AppDomain boundary (Approach 1), seed static
`.cctor`s at load, and register host-CLR assemblies for CLR-type resolution
(the Step-24 `TestCLREnum` gap). This is the LARGEST Step-25 slice; significant
PARTIAL-SHIP is expected.

## Authoritative prior context (READ BEFORE PROPOSING — do not re-research)

1. `openspec/changes/archive/2026-07-07-neo-step25-runtime-loader/{design.md,planning-context.md,ship-log.md}`
   — S1 (same-AppDomain non-generic). The `isNeoAotBody` dual-path +
   `InitCodeBodyFromNeo` + `NeoAssemblyLoader.Attach` are the foundation s3
   extends. The S1 ship-log's "deferrals" section is the s3 scope.
2. `openspec/changes/archive/2026-07-07-neo-step25-runtime-loader/` S2 follow-up
   (`neo-step25-s2-generic-at-load`, archived 2026-07-07) — generic-at-load
   (no-T-identity-token slice). s3 extends the AOT path further.
3. `openspec/changes/neo-completion-portfolio/handoff/lead-1.md` — "Key
   decisions" (Cross-AppDomain token re-resolution = APPROACH 1; the
   Cecil-coupling-zero-at-execution hinge) + "Dead ends" (the parametrized-Run
   prerequisite; `AppDomain.Dispose` required).
4. `.trae/documents/neo-deferred-items.md` — the **STEP-25-PARTIAL** row (§2) +
   §3 detail is the authoritative s3 tracker. **F-11** (eager-compile stale
   cache) + **F-12** (Run ref-return) are S3-related follow-ups from S2.
5. `openspec/changes/neo-completion-portfolio/planning-context.md` — build/test
   commands + the AOT-chain hinge.

## This child is LARGE — dump-gate EACH sub-surface and SCOPE IT (binding)

s3 spans 5 sub-surfaces. The dump-gate discipline (which disproved orientation
hypotheses ~5× this session) is binding. For each, probe on HEAD and decide
SMALL (ship) vs LARGE (defer). Expect to ship a COHERENT SLICE and defer the
rest — do NOT try to land all 5 in one diff (unreviewable).

### Sub-surface 1 — ILType AOT-init from NeoTypeDefRecord (Cecil-decoupling)
- Rebuild an `ILType`'s field layout (`TotalPrimitiveSize` /
  `TotalReferenceCount` / per-field `PrimitiveOffset`/`ReferenceOffset`) +
  VTable from the `.neo` `NeoTypeDefRecord`, WITHOUT Cecil. S1 did
  `ILMethod.InitCodeBodyFromNeo`; s3 does the `ILType` equivalent.
- Probe: does `NeoTypeDefRecord` (Step 23) carry enough to rebuild the layout +
  VTable? What does `ILType`'s ctor read from Cecil today (`TypeDefinition`)?
  Cite file:line in `ILType.cs`.

### Sub-surface 2 — Cecil-free AppDomain load (the true AOT scenario)
- Load a `.neo` into an AppDomain that has NOT Cecil-loaded the types (no
  `Mono.Cecil` `TypeDefinition`). This is where token hashes computed at
  compile time (a DIFFERENT AppDomain) must re-resolve.
- Probe: what does `AppDomain.LoadAssembly` require today (a Cecil stream)?
  Can a `.neo`-only load path bypass it? This is the LARGEST sub-surface —
  likely DEFER unless a narrow seam exists.

### Sub-surface 3 — Cross-AppDomain token-hash re-resolution (Approach 1)
- Record the compile-time `GetHashCode()` per ref entry in the `.neo`; the
  loader re-registers resolved refs under the RECORDED hash (so the runtime
  token maps `mapTypeToken`/`mapMethod`/`fieldTokenMapping`/`jumptables` match).
- **Approaches 2/3 (name-based GetHashCode) REJECTED** — identity-uniqueness is
  relied on by SHARED maps (Legacy regression risk). s3 owns Approach 1.
- Probe: where are the token maps populated today? Cite file:line. Depends on
  sub-surface 2 (a Cecil-free load is where re-resolution is needed).

### Sub-surface 4 — Static .cctor seeding via .neo
- Run a type's static constructor (`.cctor`) at AOT load (the `NeoTypeDefRecord`
  carries a static-ctor ref). Probe: how does the Legacy/JIT path trigger
  `.cctor` today? Is it lazy (first field access) or eager?

### Sub-surface 5 — Full CLR aqname / host-CLR-assembly registration
- The Step-24 `TestCLREnum` gap: register host CLR assemblies so CLR-type
  resolution works without Cecil. Probe: what does the standalone CLI
  (`ilrt_neoc`) miss today (the `TestCLREnum` host-CLR-assembly the CLI doesn't
  register)? Cite file:line in `NeoCompiler.cs` / the CLI.

## Scope guidance (the dump-gate decides; PARTIAL-SHIP is the EXPECTED outcome)

Realistic ship slices (best-first):
1. **ILType AOT-init same-AppDomain** (sub-surface 1, SAME AppDomain as compile)
   — the natural extension of S1/S2. Likely the SHIP slice (the token maps
   already resolve same-AppDomain; no re-resolution needed).
2. **Sub-surface 5 (CLR registration)** if SMALL (register the host-CLR
   assemblies the CLI misses).
3. DEFER sub-surfaces 2 + 3 (Cecil-free load + cross-AppDomain re-resolution —
   the LARGEST; the true AOT scenario) + 4 (.cctor) if LARGE.

**STOP / partial-ship is binding.** Do NOT force the full decoupling past the
dump-gate. Ship the coherent slice (likely sub-surface 1 same-AppDomain) +
defer the rest with honest spec deltas + `neo-deferred-items.md` updates.

## The parametrized-Run prerequisite (F-12, from S2)

A parametrized V2 (needed for full s3 functional coverage) requires a fuller
`ILIntepreter.Run` entry or an internal-call-driven invocation (the Step-6 shim
is parameterless-only; `NeoBoxReturnValue` handles primitive returns only —
F-12). Probe whether the s3 ship slice needs it; if the same-AppDomain
parameterless probe suffices (as in S1/S2), defer it.

## Build + test (CRITICAL — copy from portfolio planning-context)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll \
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep25LoadExec   # the S2 capstone (21/21)
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll \
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep             # 218/0/1 (post-B1)
# AOT standalone tool: dotnet build ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj -c Debug_Neo
```
ALWAYS `-f net8.0`; CLI filter is a `Contains` substring (no `|`). `Debug_Neo`
prints huge JIT output — normal. A test taking >10s usually = interpreter
infinite loop — kill + investigate.

## Spec authoring traps (from handoff)

- `specs/neo-optimizer/spec.md` delta PURE ASCII (a single non-ASCII byte breaks
  the validator). ASCII-primary throughout. Start every requirement body with
  "... SHALL ..." on the FIRST hard-wrapped line.
- Capability: **neo-optimizer** (the AOT toolchain). The openspec validator is
  flaky (pre-existing 9/10 false-failures on OTHER capabilities); `neo-optimizer`
  is the one that validates. LEAD does manual archive merges.
- Legacy is the REFERENCE. Neo-only `#if ENABLE_NEO_MODE`. Legacy-neutral.

## Deliverables

`proposal.md`, `design.md` (the per-sub-surface dump-gate table: SMALL vs LARGE,
each file:line-cited; the SHIP-vs-DEFER scope decision), `specs/neo-optimizer/spec.md`
(delta), `tasks.md`. Scope honestly; significant partial-ship is the expected
outcome for a child this large.

## Findings -- neo-step25-s3-full-decoupling (propose, 2026-07-08)

### The per-sub-surface dump-gate verdict (HEAD `3ec1fa37`, file:line-cited)

| # | Sub-surface | Verdict |
|---|-------------|---------|
| 1 | ILType AOT-init from `NeoTypeDefRecord` (layout + VTable rebuild), same-AppDomain | **SMALL -> SHIP** |
| 2 | Cecil-free AppDomain load (the true AOT scenario) | **LARGE -> DEFER** |
| 3 | Cross-AppDomain token-hash re-resolution (APPROACH 1) | **LARGE -> DEFER (depends on 2)** |
| 4 | Static `.cctor` seeding via `.neo` | **DEFER (depends on 2 for functional value)** |
| 5 | CLR aqname / host-CLR-assembly registration (Step-24 `TestCLREnum` gap) | **STRETCH -> lean DEFER** |

SHIP = sub-surface 1 only. DEFER = 2/3/4 (+ 5 unless a clean seam exists).
Evidence + the SHIP-vs-DEFER rationale: `design.md` dump-gate table.

### Why sub-surface 1 is SMALL (the record is complete for layout + VTable)

`NeoTypeDefRecord` (`NeoAssembly.cs:162-179`) carries the RESULTS of the Cecil
init computations: `TotalPrimitiveSize`/`TotalReferenceCount`/static totals;
`Fields[]` = `NeoFieldLayoutRecord{FieldRefIdx,PrimitiveOffset,ReferenceOffset}`
(exactly `ILTypeFieldOffset` at `ILType.cs:18-22`); `VTableMethodRefIdxs[]`;
`Interfaces[]`; `BaseTypeRefIdx`. The Cecil init produces pure data
(`InitializeFields` `:2007-2180`; `BuildNeoVTable` `:444-525`). So a rebuild +
a structural-equivalence self-check is the S2 `BodiesEqual` pattern for types.
The rebuild is consumed ONLY by the DEBUG self-check (it does NOT install on a
live `ILType` -> zero shared-mutable-state hazard -> Legacy-neutral by gating).

### Two honest record gaps (forward-compat, NOT blockers)

- (a) `naturalAlignment` (`ILType.cs:99`) is NOT in the record. The rebuild
  RE-DERIVES it from the resolved field types + compares (proving the
  re-derivation matches Cecil).
- (b) Per-STATIC-field offsets (`staticFieldOffsets`) are NOT in the record
  (only the static totals). Deferred with sub-surface 4. The SHIP slice
  compares the static totals (carried).

### The mutation cell is load-bearing (the S1/S2 discipline)

A structural-equivalence cell alone is INSUFFICIENT: the record is built FROM
Cecil's values at serialize time (Step 23), so equality can hold trivially. The
mutation cell (mutate a field offset / swap a VTable slot in an INDEPENDENT
`model2` BEFORE rebuild -> assert DIVERGENCE) is the proof the rebuild reads the
record. Mandatory in the self-check.

### F-12 (parametrized-Run) does NOT block this slice

The ship slice is a HOST-SIDE structural comparison (no `ILIntepreter.Run`
invocation). F-12 + the parameterless-only Run shim are irrelevant here (same
as S2's structural-equivalence cell). DEFER (already on STEP-25-PARTIAL).

### Adversarial-probe plan (a green smoke does NOT prove the gate)

- Structural-equivalence: layout (field-by-field + totals + `naturalAlignment`)
  + VTable (slot-by-slot) + slot-key map (key-by-key, OQ2) + interface offsets.
- Mutation: field-offset mutation AND a VTable-slot swap (two mutation cells --
  one for the layout path, one for the VTable path).
- Probe readiness (OQ1): if the existing `NeoStep25LoadProbe` lacks 2+ fields of
  differing widths + a base virtual + an interface impl, ADD a dedicated
  `NeoStep25S3Probe`.

### Capability + files the implementer will touch

- `ILRuntime/CLR/TypeSystem/ILType.cs` (SHARED, Neo-only builder -- Legacy-neutral gate).
- `ILRuntime/Runtime/NeoAOT/NeoAssemblyLoader.cs` (Neo-only `ResolveVTableFromRecord` helper).
- `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep25LoadExecCheck.cs` (DEBUG + Neo cells).
- Maybe `TestCases/NeoStep25S3Probe.cs` (OQ1).
- Capability: `neo-optimizer` (delta: 2 ADDED requirements; pure ASCII; SHALL-first).

### Verdict to the LEAD

PARTIAL SHIP. Sub-surface 1 (ILType layout + VTable rebuild, structural-
equivalence + mutation proven) is the coherent reviewable slice; sub-surfaces
2/3/4 (the Cecil-free load + cross-AppDomain APPROACH 1 re-resolution +
`.cctor`) are LARGE + coupled + need a `.neo` Version bump -- deferred with
honest spec deltas. Sub-surface 5 (CLR registration) is a stretch. All 4
artifacts authored + apply-ready (`openspec status`: 4/4).
