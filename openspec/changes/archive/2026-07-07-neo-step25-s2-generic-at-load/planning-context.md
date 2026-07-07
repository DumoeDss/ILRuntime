# Planning Context — neo-step25-s2-generic-at-load

> SEED for the planner. Read THIS FIRST, then the docs it points at, then
> research only what is missing. APPEND durable findings after propose.

## What this change is (one line)

Step 25 **S2 follow-up**: generic-instantiation-at-load — at `.neo` load time,
clone + patch a generic method's compiled template (from Step 22's
`GenericMethodTemplate` machinery) into the consuming AOT `ILMethod.BodyRegister`,
so a generic method call on an AOT-loaded type runs WITHOUT falling back to JIT.
S1 (the parent, archived) proved `.neo` deserialize + `ExecuteNeo` == JIT for
**non-generic** methods; S2 extends the AOT path to generic methods.

## Authoritative prior context (READ BEFORE PROPOSING — do not re-research)

1. `openspec/changes/neo-completion-portfolio/handoff/lead-1.md` — the
   portfolio-level handoff. Sections "Done/Remaining", "Key decisions",
   "Dead ends & gotchas", "Working set", "Next action" (#1 is THIS child).
2. `openspec/changes/neo-completion-portfolio/planning-context.md` — the
   portfolio seed (build/test commands, codebase gotchas, the AOT-chain design
   hinge, commit/push convention). DO NOT re-derive any of this.
3. `openspec/changes/archive/2026-07-07-neo-step25-runtime-loader/design.md`
   + `planning-context.md` + `ship-log.md` — the S1 design + what was deferred
   to S2/S3. **The S1 V2 self-check (`NeoStep25LoadExecCheck`) is the capstone
   to extend.**
4. `openspec/changes/archive/2026-07-07-neo-step22-generic-template/` — Step 22:
   `PatchEntry`, `templateBody+patches`, `CloneAndPatch` runtime instantiation.
   This is the mechanism S2 reuses at load.
5. `.trae/documents/neo-deferred-items.md` — the **STEP-25-PARTIAL** row (§2)
   + §3 detail is the authoritative per-item tracker for S2/S3.

## The design hinge S2 inherits (DO NOT re-litigate)

- **Cecil-coupling is ZERO at execution.** `ExecuteNeo` reads only
  `CompiledFrame.NeoExecuteBody` + token hashes via AppDomain maps. So S2, like
  S1, is **init-only** (populate the AOT `ILMethod.BodyRegister` from the `.neo`
  template + patched body at load), NOT a change to `ExecuteNeo`.
- **`OpCodeR` serialization = raw 24-byte little-endian** via
  `MemoryMarshal.AsBytes` (captures all `[StructLayout(Explicit)]` union
  aliases). Versioned header (Version=1).
- **The `public NeoCompiler` seam** (Step 24): all NeoAOT types +
  `ILMethod.BodyRegister` + `ForceBuildTemplate` are `internal` with NO
  `InternalsVisibleTo`.
- **Cross-AppDomain token re-resolution = Approach 1** is S3, NOT S2. S2 stays
  same-AppDomain (the S1 `NeoAssemblyLoader.Attach` path).

## KNOWN prerequisite surfaced by S1 (the load-bearing thing to design around)

> From the S1 ship-log + STEP-25-PARTIAL: "the Neo host entry `ILIntepreter.Run`
> is a Step-6 PARAMETERLESS-ONLY shim (does not marshal `object[] p` into the
> frame, nor allocate the full frame ref region). A parametrized V2 (needed for
> S2/S3) needs either a fuller Run entry or an internal-call-driven invocation."

So S2 has TWO possible shapes — the propose phase must DUMP-GATE which is real:
- **(P1) extend `ILMethod.BodyRegister` population at load** so a generic call
  on an AOT type resolves the template + runs `CloneAndPatch` at load (or at
  first-call), then the existing parameterless V2 self-check still works because
  the generic method is invoked the same way. Probe: how does a generic method
  call on an AOT-loaded type resolve its template TODAY (S1 skips generic-defs)?
- **(P2) a parametrized probe entry** if the generic call needs args marshaled
  and the parameterless shim can't reach it.

Do NOT assume P1 or P2. Probe the JIT/AOT path on HEAD first (the dump-gate
discipline that disproved orientation hypotheses 5× this session).

## Files the planner should map (from S1/Step22 — confirm at probe)

- `ILRuntime/Runtime/NeoAOT/NeoAssemblyLoader.cs` — the S1 same-AppDomain
  `Attach` path (where generic-def is currently skipped).
- `ILRuntime/Runtime/NeoAOT/NeoCompiler.cs` — the public seam; `ForceBuildTemplate`.
- `ILRuntime/Runtime/Intepreter/RegisterVM/GenericMethodTemplate.cs` — Step 22
  `CloneAndPatch` + template/patch tables (the mechanism S2 reuses at load).
- `ILRuntime/CLR/Method/ILMethod.cs` — `isNeoAotBody` dual-path (S1) +
  `BodyRegister` (Step 22 cache).
- `TestCases/NeoStep25LoadExecCheck.cs` (host-side self-check) — extend with a
  generic-method matrix (mirror the Step 22 11×5 = 55 structural-equivalence
  matrix, but now through the AOT load path, not JIT).

## Probe / dump-gate (MANDATORY before designing — the binding lesson)

Before designing the fix, on HEAD `cfbf8ff9`:
1. How is a generic method call resolved on a **JIT** ILType today
   (`GenericMethodTemplate` per-occurrence instantiation)? Read the JIT path.
2. On an **AOT-loaded** ILType (S1 path), what happens to a generic method call
   right now? (S1 skips generic-defs — so it either keeps JIT or NIEs. Confirm
   WHICH and WHERE.)
3. Is the parameterless V2 entry sufficient to exercise a generic call, or does
   a generic call need marshaled args (→ P2)?

The dump is the arbiter, not the hypothesis. STOP and partial-ship if the
designed fix is wrong (precedent: `neo-step20-async-suspend` Phase 1).

## Build + test (CRITICAL — copy from portfolio planning-context)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo   # CLI; NEVER build TestCases with Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll \
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep25LoadExec   # the S2 capstone filter (extend the matrix)
# regression: NeoStep (210/210), NeoStep22SelfCheck (55/55), NeoStep23Roundtrip (15/15), NeoStep24CliRoundtrip (5/5)
# AOT standalone tool: dotnet build ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj -c Debug_Neo
```
ALWAYS `-f net8.0`; the CLI filter is a simple `Contains` substring (no `|`
alternation — run each filter separately). `Debug_Neo` prints huge JIT output
(`OUTPUT_JIT_RESULT`) — normal; filter stdout for the pass/fail summary.

## Spec authoring traps (from handoff)

- Author `specs/neo-optimizer/spec.md` delta PURE ASCII (a single non-ASCII
  byte like U+00A7 silently breaks the validator).
- Start every requirement body with "... SHALL ..." on the FIRST hard-wrapped
  line (the validator scans only the first line for SHALL/MUST).
- The openspec validator is flaky in this env (`validate --all` shows 9/10
  failing on a PRE-EXISTING `Missing ## Purpose` condition); the
  `neo-optimizer` capability is the one that validates. Archive merges may need
  a manual merge + plain `mv` (handoff authorizes this).

## Capability + deliverables

- Capability: **neo-optimizer** (S2 is the AOT toolchain capability, same as
  S1/Step22/23/24/25).
- Deliverables: `proposal.md`, `design.md` (with the P1/P2 dump-gate decision),
  `specs/neo-optimizer/spec.md` (MODIFIED delta extending the AOT-loader reqs
  to generic methods), `tasks.md`.
