# Planning Context — neo-peephole-isinst (D-PEEP)

> SEED for the planner. Read THIS FIRST, then the docs it points at, then
> research only what is missing. APPEND durable findings after propose.

## What this change is (one line)

**D-PEEP** — a compile-time peephole fusion: `box T; isinst U` (box a value
type then immediately type-check it) fuses into a single direct check, via a
`PatchKind.IsinstResult` patch-table entry. Pure optimization (non-functional);
lowest portfolio priority. The handoff tagged it "BLOCKED on patch-infra (neither
the fusion pass nor PatchKind exists)."

## CRITICAL dump-gate: the handoff's "PatchKind does not exist" may be STALE

The handoff (`lead-1.md`) said "neither the fusion pass nor PatchKind exists."
But that was written BEFORE `neo-step22-generic-template` shipped. **Step 22
introduced `PatchEntry` + a `PatchKind` enum** (for generic-method template
T-identity patches — see the canonical `neo-optimizer` spec's "PatchEntry
captures only T-identity operand sites" requirement + `GenericMethodTemplate.cs`).
So the planner MUST dump-gate on HEAD `70505eba`:
- Does `PatchKind` exist NOW (post-Step-22)? What values does it have? Could an
  `IsinstResult` kind be ADDED to it, or is it generic-template-specific?
- Does any peephole/fusion pass exist (a pass that pattern-matches adjacent
  opcodes + emits a fused form)? Or only the Step-22 template-patch mechanism?
- Is the `box T; isinst U` pattern even EMITTED by the JIT today (does the Neo
  JIT lower `isinst` to a form a fusion pass could see), or is `isinst` already
  lowered/specialized away?

This determines whether the child is:
1. **TRACTABLE** (Step-22's PatchKind/PatchEntry infra hosts the isinst fusion, or
   a small fusion pass is additive) -> SHIP.
2. **STILL BLOCKED** (the fusion needs a NEW peephole pass + a broader PatchKind;
   substantial new infra) -> STOP-at-blocker / scoped-deferral (document the
   prerequisite; defer to a patch-infra child). The accepted outcome for a
   "lowest priority, pure optimization" item.

## Authoritative prior context (READ BEFORE PROPOSING)

1. `openspec/changes/neo-completion-portfolio/handoff/lead-1.md` — "Remaining"
   (peephole BLOCKED on patch-infra; lowest priority) + "Dead ends".
2. `openspec/changes/archive/2026-07-07-neo-step22-generic-template/` — Step 22:
  `PatchEntry` + `PatchKind` + the template-patch mechanism (the infra that may
   now host the fusion). Read its `design.md` for the PatchKind values + the
   patch-apply path.
3. `.trae/documents/neo-deferred-items.md` — the **D-PEEP** row (§2) + §3 detail.
4. The canonical `openspec/specs/neo-optimizer/spec.md` "PatchEntry captures only
   T-identity operand sites" requirement — the PatchKind discipline (a PatchField
   must be a standalone OpCodeR field, never aliasing a wide-immediate).
5. `openspec/changes/neo-completion-portfolio/planning-context.md` — build/test
   commands + the OpCodeR-union gotcha (F-8 / OPT-HARDEN-K1: any new patch field
   MUST NOT alias a wide-immediate field).

## Scope guidance (the dump-gate decides; STOP-at-blocker is the accepted outcome)

The peephole is a PURE OPTIMIZATION (non-functional) and LOWEST priority. Do NOT
force substantial new infra (a peephole pass framework + a broad PatchKind
redesign) for a non-functional gain — that is the "force a fix past the
dump-gate" anti-pattern. Realistic outcomes:
1. **Tractable fusion on existing infra** (Step-22 PatchKind + an additive fusion
   case) -> SHIP the fusion + a regression guard proving the fused form produces
   the same result as the un-fused `box;isinst`.
2. **STOP-at-blocker** -> document the prerequisite (a peephole pass + PatchKind
   extension) in the spec delta + `neo-deferred-items.md`; close as a scoped-
   deferral (the `neo-async-controlflow-iscompleted` / no-op precedent). The
   portfolio is COMPLETE either way — this is the last child.

## The OpCodeR-union discipline is binding

ANY new patch field (an `IsinstResult` PatchKind) MUST be a STANDALONE `OpCodeR`
field whose byte range does NOT alias a wide-immediate field a runtime consumer
reads (`OperandLong`/`OperandDouble` @12-19, `OperandFloat` @8-11). The F-8 /
OPT-HARDEN-K1 / double-combine defects were ALL OpCodeR-union aliasing. Probe
the available standalone fields before designing the patch encoding.

## Build + test (CRITICAL — copy from portfolio planning-context)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll \
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep15   # the isinst/castclass step
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll \
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep     # 218/0/1 regression
```
ALWAYS `-f net8.0`; CLI filter is a `Contains` substring (no `|`). `Debug_Neo`
prints huge JIT output — normal. A test taking >10s usually = interpreter
infinite loop — kill + investigate.

## Spec authoring traps (from handoff)

- `specs/neo-type-checks/spec.md` OR `specs/neo-optimizer/spec.md` delta PURE
  ASCII; start every requirement body with "... SHALL ..." on the FIRST
  hard-wrapped line. (isinst is `neo-type-checks`; the patch-infra is
  `neo-optimizer` — pick the primary capability by where the change lands.)
- The openspec validator is flaky (pre-existing 9/10 false-failures on non-
  `neo-optimizer` capabilities); LEAD does manual archive merges.
- Legacy is the REFERENCE. Neo-only `#if ENABLE_NEO_MODE`. Legacy-neutral.

## Deliverables

`proposal.md`, `design.md` (the patch-infra dump-gate verdict: PatchKind exists
post-Step-22? fusion tractable or STOP? + the SHIP-vs-DEFER decision), `specs/<cap>/spec.md`
(delta), `tasks.md`. Scope honestly; STOP-at-blocker is the accepted outcome for
this lowest-priority pure-optimization item.
