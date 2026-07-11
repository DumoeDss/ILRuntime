# Handoff: neo-completion-portfolio — LEAD #3 (common-case shipped; harder variants = next session)

> This session drove the **completion-2 wave**: the COMMON case of every open functional
> gap in the Neo overhaul was driven to real+usable completion. **Honest caveat:** the
> harder variants of each feature were written off as "Non-Goals / deferred" — that
> framing over-claimed "TRUE-COMPLETION". They are NOT done. Next session must drive them
> to FULL completion (no more deferral). The portfolio's `runnableFrontier` is empty
> (43 children archived) but the Remaining list below is the real open work.
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

**The COMMON case of every feature shipped** (real + usable):
- Async/await: truly-async **single-await** Task<T> WORKS end-to-end (suspend + resume + GetResult + SetResult). [multi-await + ExecutionContext + ValueTask/async-void + Task.Run(ilLambda) remain — see Remaining]
- Reflection: read IL fields/types/methods off a Neo instance (the indexer + GetType + parametrized-Run).
- AOT standalone: the `.neo` loader + CLI are robust (Cecil-free load for **self-contained IL types**; full-TestCases compile no-fatal; .cctor seeding; host CLR resolution). [CLR-base/generic on the Cecil-free path remain]
- Debugger: Neo frame variable inspection (**primitive + reference + CLR-struct locals + this/fields**). [IL-VT-local + AOT-body + CLI protocol remain]
- Byref: primitive + reference + delegate-Invoke + direct-Call, all with write-back. [CLR->IL delegate-callback-with-byref + the Step-17 ldind_ref heap-ref remain]

**Genuinely closed (honest — not bugs / not reproducible):**
- D-PEEP: the box;isinst fusion has ZERO payoff (0/1071 pairs across 3 real DLLs; the compiler never emits the pattern). Closed on evidence.
- F-9/F-2: non-reproducible on HEAD (JIT-body-disproven).
- F-11: not-a-bug (stale-but-correct JIT body; an optimization gap).
- F-13: disproven (the ip/frame/pool are isolated; the recorded corruption was the Step-6 Run shim).

## Remaining — the COMPLETE task list for the next session (drive to FULL completion; NO deferral)

**User directive: these are ALL to be completed next session. Do not write them off as
"Non-Goals" or "deferred" — that framing over-claimed completion this round. Each is a real
task; ship it to functional completion (the COMMON case shipped this round; these are the rest).**
Drive them like the completion-2 children: dump-gate -> implement -> adversarial verify ->
Legacy-neutral. Categorized:

**Async (complete the truly-async machinery beyond single-await Task<T>):**
- Multi-await suspend/resume (an SM with >=2 incomplete awaits double-suspends -- the state-machine state/awaiter-slot issue).
- `AwaitOnCompleted` ExecutionContext / SynchronizationContext capture (`AwaitOnCompleted_Neo` currently mirrors `AwaitUnsafeOnCompleted` without the capture).
- `ValueTask<T>` suspend path + `async void` suspend path.
- IL-delegate-through-CLR-method round-trip (`Task.Run(ilLambda)`).
- (perf) zero-alloc `ValueTask<T>` (the suspend path may allocate a `Task<T>`/TCS bridge).

**AOT / S3-2 Cecil-free (complete the standalone-AOT coverage):**
- CLR base/interface resolution on the Cecil-free path (a Cecil-free type whose base/interface is a CLR type needing a CrossBindingAdaptor).
- Generic-method/type instances on the Cecil-free path (S2 T-identity-token re-resolution + cross-AppDomain generic-instance).
- Cross-PROCESS load (P1-built `.neo` in P2; APPROACH-1 hashes are already process-independent).
- Multi-hotfix-assembly cross-references (one IL hotfix referencing another IL hotfix's types).

**Debugger (complete the variable-inspection surface):**
- IL-value-type-LOCAL reconstruction (the placeholder string -> the struct's fields).
- AOT-body variable inspection (`registerSymbols` null on AOT -> serialize var metadata into `.neo`).
- CLI debugger-protocol capstone (the VSCode DAP frontend `Debugging/VS2022/` + the ~6 protocol/frontend methods).

**Byref / array (complete the byref + multi-dim-IL-VT coverage):**
- CLR->IL delegate callback with a byref param (`List.ForEach(ilActionWithRefParam)`) -- the reverse direction of F-7.
- The Step-17 `ldind_ref` heap-IL-ref-field read (`ILIntepreter.Neo.cs:3862`).
- AOT (`ilrt_neoc`) wire-up of the F-7 byref map.
- IL VT-element multi-dim array `[,]` (a multi-dim array with IL value-type elements) + multi-dim `Address` (ldelema) -- real remaining gaps from neo-array-multidim.

**Need a reproducer first (construct one on current HEAD; if it reproduces -> fix; if not ->
confirmed-closed, NOT deferred):**
- Q-STRUCT (struct-local + field-mutation + element-read temp-renumber).
- Q-LONG (long default-zero compare / conv.i8 quirk).

**NOTE (doc consistency, already done):** the 5 master-table rows stale after the completion-2 wave
(F-2 / F-9 / F-11 / D-PEEP / F-7B-SIB) were corrected to their actual 2026-07-09 completed state.

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

## Next action — next session drives the Remaining list to FULL completion (no deferral)

The completion-2 wave shipped the COMMON case of every feature; it over-claimed "TRUE-COMPLETION"
by writing the harder variants off as "Non-Goals". **Next session's job is to finish them all** —
the complete task list above. Treat each as a real must-do child: dump-gate -> implement ->
adversarial verify -> Legacy-neutral. Do NOT re-introduce the "Non-Goals / deferred" escape hatch
(that framing is what left this tail). Pick the highest-value first (async multi-await; S3-2
Cecil-free CLR-base/generic) and drive to functional completion. The only legitimate "close" is a
reproducer that proves it's not a bug (the Q-* pattern) — anything else gets DONE.
