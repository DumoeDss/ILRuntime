# Planning Context — neo-peephole-pass (D-PEEP TRUE COMPLETION)

> SEED. D-PEEP: the `box T; isinst U` peephole fusion. Scope-AWARE: this is a PURE
> OPTIMIZATION (the path works un-fused). Dump-gate whether the fusion is worth a new
> ADDITIVE optimizer pass, or close as accepted-known optimization deferral.

## What this change is

A compile-time peephole: fuse the CIL pair `box T; isinst U` (box a value type then
immediately type-check it) into a single direct check, avoiding the heap-box allocation
on the hot path. This is a PURE OPTIMIZATION -- the `box;isinst` path is functionally
CORRECT un-fused (Step 15 + Step 18). The prior D-PEEP child (neo-peephole-isinst) closed
it as scoped-deferral (needs a new pass framework; PatchKind wrong shape).

## The scope-aware question (binding -- is it worth a new framework?)

The fusion requires:
1. A NEW ADDITIVE optimizer pass that pattern-matches ADJACENT opcodes (`box T` followed
   by `isinst U`) -- no peephole/fusion pass exists today (the optimizer passes are
   FCP/BCP/ELDC/InlineMethod/RegisterCleanup + the Neo back-half; none pattern-match
   adjacent opcodes).
2. A DEF-USE/LIVENESS analysis: the `box` dest must be DEAD after the `isinst` (copy-prop
   can move the box away; the F-8 discipline). The fusion is legal ONLY when the boxed
   object is unobserved after the check.
3. A FUSED opcode on a STANDALONE `OpCodeR` field (carrying both the T + U type tokens),
   obeying the OpCodeR-union discipline (F-8: never alias a wide-immediate field).

**The dump-gate question:** is this fusion worth a new pass framework (a measurable hot-path
win -- avoiding a heap box per `isinst`-on-a-value-type), or is it a marginal micro-
optimization not worth the framework (the box;isinst path is rare + correct un-fused)?

If worth it -> SHIP the additive pass + the adversarial equivalence probe (fused == un-fused,
incl. null/wrong-type/subclass/observed-box cases).
If marginal -> CLOSE as accepted-known optimization deferral (the path works; the fusion is
a non-functional micro-optimization; the F-11 precedent -- F-11 was closed as "not-a-bug,
stale body is correct JIT, an optimization gap").

## Dump-gate (binding -- on HEAD `162ce992`)

1. How common is the `box T; isinst U` pattern in real IL? (Probe the TestCases + a
   representative workload -- is it a hot path or rare?)
2. Is the new ADDITIVE pass tractable (mirror an existing pass's structure -- e.g. the
   `addrAlias`/`liveAliasMap` adjacent-opcode machinery from Step 17, or the FCP/BCP
   pattern) + a liveness check (the box dest dead after isinst)? Or does it need a full
   new dataflow framework?
3. The fused-opcode encoding: which standalone `OpCodeR` field carries the 2 type tokens
   (T + U)? Confirm disjointness from wide-immediate fields (the F-8 discipline).
4. **Scope decision:** SHIP (the fusion is a clean additive pass with a measurable win) OR
   CLOSE (marginal micro-optimization, not worth the framework; the path works un-fused).

## Authoritative prior context

1. `openspec/changes/archive/2026-07-08-neo-peephole-isinst/` -- the prior D-PEEP scoped-
  deferral (PatchKind wrong shape; no fusion pass exists; needs the framework).
2. `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.*.cs` -- the existing optimizer
  passes (FCP/BCP/ELDC/InlineMethod/RegisterCleanup + the Neo back-half TypeSpecialize/
  Allocate/LowerNeoOffsets) + the Step-17 `addrAlias`/`liveAliasMap` adjacent-opcode
  machinery (the closest precedent for an adjacent-pattern pass).
3. `ILRuntime/Runtime/Intepreter/OpCodes/OpCode.cs` -- OpCodeR (`[StructLayout(Explicit)]`,
  the standalone fields + the F-8 wide-immediate-aliasing discipline).
4. `.trae/documents/neo-deferred-items.md` -- D-PEEP row.

## Build + test (CRITICAL -- always `-f net8.0`)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep15   # the isinst/castclass step
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep     # 235/0/0 regression
```
ALWAYS `-f net8.0`. `Debug_Neo` prints huge JIT output -- normal.

## Spec authoring traps
- `specs/neo-optimizer/spec.md` (or neo-type-checks) delta PURE ASCII; SHALL-first. Legacy is the REFERENCE. Neo-only `#if ENABLE_NEO_MODE`. Legacy-neutral (an additive optimizer pass compiles out under plain Debug).

## Deliverables
`proposal.md`, `design.md` (the scope-aware verdict: SHIP-the-pass vs CLOSE-as-optimization-
deferral, with the dump-gate evidence -- how common is box;isinst + is the pass tractable),
`specs/<cap>/spec.md` (delta), `tasks.md`.
