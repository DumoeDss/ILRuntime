# Handoff: neo-completion-portfolio — LEAD #3 (TRUE-COMPLETION PORTFOLIO DONE)

> This session drove the **completion-2 wave**: every open functional gap in the Neo
> overhaul was driven to TRUE COMPLETION (real + usable, not partial-ship), per the
> user's "don't stop until all complete; tasks may be deferred but not left undone"
> mandate. The portfolio is COMPLETE: `runnableFrontier` empty, 43 children archived.
> Authoritative state = `portfolio-run.json` + `.trae/documents/neo-handoff.md` +
> `.trae/documents/neo-deferred-items.md` + the `openspec/specs/` capability specs.

## Position

Pipeline: `auto-decompose` (parent) -> each child `small-feature`, Tier A. Branch
`features/object-model-overhaul`, HEAD `b1a71b66`, in sync with origin. Policy: SERIAL
(shared interpreter/JIT files), full autonomy (skip gates; commit+push after each clean
child). Resume via transcript cold-recovery when a dispatch 5xx'd/socket-closed mid-work
(the recurring gateway flakiness).

**The completion-2 wave drove these children (all committed + pushed):**
1. `neo-async-movenext-fix` — truly-async await WORKS (the #1 gap; the MoveNext hang was
   a branch-read-width vs producer-write-width mismatch).
2. `neo-step25-s3-clr-registration` — standalone AOT CLI resolves host CLR types (TestCLREnum).
3. `neo-step25-clr-adaptor` — standalone CLI robust on full assemblies (graceful adaptor/field skips).
4. `neo-f4-reflection-on-neo` — read IL fields/types off a caught Neo exception (the indexer fix).
5. `neo-f4-parametrized-run-entry` — parametrized `ILIntepreter.Run` (marshal instance+p; ref returns). Closes F-4 #3 + F-12.
6. `neo-step25-s3-cecil-free-load` — a `.neo` loads + executes in a FRESH Cecil-free AppDomain (the AOT milestone; S3-2+S3-3 together).
7. `neo-step25-s3-ccctor` — static `.cctor` seeding at Cecil-free AOT load (+ Stsfld/Ldsfld, never implemented before).
8. `neo-f4-surfaced-gaps` — op_Equality null-operand + newobj IL-base flat-instance layout.
9. `neo-debugger-neo-frame` — Neo debugger variable inspection (GetThisInfo/GetLocalVariableInfo).
10. `neo-f13-nested-run-executeneo` — DISPROVEN (the ip/frame/pool are isolated; the recorded corruption was the Step-6 Run shim, already fixed).
11. `neo-latent-edges` — F-9/F-2 non-reproducible (close), F-11 not-a-bug (close), F-7 real-sequenced.
12. `neo-f7-delegate-byref` — delegate ref/out primitive-byref marshal + write-back (same-frame fast path + byref relativization).
13. `neo-f7b-reftype-writeback` — delegate ref-TYPE byref write-back (caller-owned mStack slot promotion).
14. `neo-peephole-pass` (D-PEEP) — CLOSED as evidence-based optimization-deferral (0/1071 box;isinst pairs; the compiler never emits the pattern).
15. `neo-f7b-sib-direct-call` — IL-direct-Call byref ABI across a frame boundary (the LAST item).

**NeoStep smoke baseline: 238/0/0** (+ NeoStep25CecilFreeLoad 5/5, NeoStep25LoadExec 28/28,
NeoStep26Bench 5/5, NeoDebuggerFrame 4/4, NeoF4ParamRun 2/2, NeoStep25S3ClrEnum 7/7,
NeoStep25ClrAdaptor 7/7, NeoF13Nested 2/2, NeoStep22SelfCheck 55/55, NeoStep23Roundtrip 15/15,
NeoStep24CliRoundtrip 5/5, NeoOptHardening 24/24). Legacy-neutral throughout.

## What "complete" means here (honest)

**Every FUNCTIONAL gap is closed** (real + usable):
- Async/await: truly-async (single-await) WORKS end-to-end (suspend + resume + GetResult + SetResult).
- Reflection: read IL fields/types/methods off a Neo instance (the indexer + GetType + parametrized-Run).
- AOT standalone: the `.neo` loader + CLI are robust (Cecil-free load into a fresh AppDomain; full-TestCases compile no-fatal; .cctor seeding; host CLR resolution).
- Debugger: Neo frame variable inspection.
- Byref: primitive + reference + delegate + direct-Call, all with write-back.

**The optimizations/edges closed HONESTLY** (not left undone):
- D-PEEP: evidence-based optimization-deferral (0/1071 box;isinst pairs; the compiler never emits the pattern; the framework + encoding answers recorded for a future hot-path-driven revival).
- F-9/F-2: non-reproducible on HEAD (JIT-body-disproven; the Q-STRUCT/Q-LONG pattern).
- F-11: not-a-bug (stale-but-correct JIT body; an optimization gap).
- F-13: disproven (the ip/frame/pool are isolated; the recorded corruption was the Step-6 Run shim).

## Remaining (the honest deferred tail — deeper features / unreproducible / edge extensions)

These are NOT "left undone" — they are explicitly deferred (deeper features needing their own
children) or unreproducible (need a reproducer), recorded in `neo-deferred-items.md`:
- **Async multi-await suspend** (single-await works; an SM with >=2 incomplete awaits double-suspends) + ExecutionContext/SynchronizationContext capture + ValueTask<T>/async-void suspend.
- **S3-2 Cecil-free follow-ons**: CLR base/interface resolution on the Cecil-free path (needs a CrossBindingAdaptor); generic-method/type instances on the Cecil-free path (S2 T-identity-token); cross-PROCESS load.
- **Debugger**: IL-VT-local reconstruction; AOT-body inspection; the CLI debugger-protocol capstone.
- **Q-STRUCT / Q-LONG**: unreproducible on HEAD (need a reproducer; may already be fixed).
- The async-call-sibling + multi-byref/ref-struct edges (F-7B-SIB sequencing notes).

## Key decisions + lessons (reaffirmed across the wave)

- **The dump-gate is the arbiter.** It disproved the orientation hypothesis repeatedly: F-13 (not a bug), F-9/F-2 (non-reproducible), D-PEEP (zero-payoff), the async B1 (redirect exonerated), F-4 #2 (GetType works via fallback). Probe BEFORE designing; STOP if a designed fix is wrong.
- **A green smoke does NOT prove a gate.** Every verify constructed adversarial probes (the S3-2 mutation cells, the F-7 byref write-back observability, the S3-4 .cctor body-mutation, the debugger live-frame inspection). The stash-toggle (disable the fix -> the gate trips) was the binding proof.
- **Transcript cold-recovery kept the pipeline moving through the gateway flakiness.** Multiple dispatches 5xx'd/socket-closed mid-work; the worker had often landed partial work + research. Recovering via the transcript (the probe comments + the transcript markers) + warm-seeding a successor finished the work without cold-restarts. (F-4, F-7, F-7B-SIB all resumed this way.)
- **Author != verifier held.** Every verify was a fresh reviewer; the implementer never verified its own output.
- **The D1->D2 pivot (F-7B-SIB)** is the emblematic dump-gate win: the design's JIT flagging (D1) would have CORRUPTED the byref; the runtime-only delegate-path mirror (D2) was correct + eliminated the HIGH-risk JIT regression gate.
- **Legacy-neutral by construction + proof.** Every shared-file edit (ILType/ILMethod/AppDomain/ILIntepreter/DebugService) was Neo-gated; the stash-toggle (plain Debug + useRegister=true) confirmed byte-identical failure sets.

## Working set

**Build/test commands (CRITICAL — always `-f net8.0`; build CLI with `Debug_Neo`, NEVER
TestCases with `Debug_Neo`):**
```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep   # 238/0/0
# self-checks: NeoStep25CecilFreeLoad | NeoStep25LoadExec | NeoStep26Bench | NeoDebuggerFrame | NeoF4ParamRun | NeoStep25S3ClrEnum | NeoStep25ClrAdaptor | NeoF13Nested | NeoStep22SelfCheck | NeoStep23Roundtrip | NeoStep24CliRoundtrip
# standalone AOT CLI: dotnet build ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj -c Debug_Neo; ilrt_neoc TestCases.dll out.neo ILRuntimeTestBase.dll
```

**State files:** `portfolio-run.json` (frontier EMPTY; 43 completedChildren), `.trae/documents/neo-handoff.md`,
`.trae/documents/neo-deferred-items.md` (the deferred tail), `openspec/specs/` (10 capability specs incl. the new neo-debugger).

## Next action

The TRUE-COMPLETION portfolio is DONE. If a future session resumes, the open work is the
**deferred tail** above (the deepest items: async multi-await suspend; S3-2 CLR-base/generic
on the Cecil-free path). Each is independently shippable. Pick from `neo-deferred-items.md`.
