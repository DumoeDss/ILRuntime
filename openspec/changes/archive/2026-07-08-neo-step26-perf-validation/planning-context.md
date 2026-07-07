# Planning Context — neo-step26-perf-validation

> SEED for the planner. Read THIS FIRST, then the docs it points at, then
> research only what is missing. APPEND durable findings after propose.

## What this change is (one line)

Step 26 — the Neo **perf-validation + hardening** capstone: a benchmark suite
(Neo-vs-Legacy), reflection / cross-domain / thread-safety edge coverage, and
the Neo adaptation of the **debugger** (`DebugService` reading Neo frame vars)
and **CrossBindingAdapter** paths. This is the LAST numbered AOT-chain step;
deps satisfied (Step 20-async, Step 25 all shipped).

## Authoritative prior context (READ BEFORE PROPOSING — do not re-research)

1. `openspec/changes/neo-completion-portfolio/handoff/lead-1.md` — portfolio
   handoff ("Done/Remaining", "Key decisions", "Dead ends", "Working set").
2. `openspec/changes/neo-completion-portfolio/planning-context.md` — the
   portfolio seed (build/test commands, codebase gotchas, the AOT-chain hinge,
   commit/push convention). Step 26 is portfolio task #16 there (Wave 5).
3. `.trae/documents/neo-handoff.md` — repo-wide handoff (§4 gotchas, §5 deferred
   items). The F-4 / NEO-IL-EX-FIELDACCESS row is RELATED (reading IL fields off
   a Neo instance via reflection) — assess whether Step 26 subsumes it.
4. `.trae/documents/neo-deferred-items.md` — authoritative deferred-items
   tracker; scan for any Step-26-routed item.

## This child is BROAD — the propose phase MUST dump-gate each sub-surface and SCOPE IT

Step 26 is several independent sub-surfaces. The dump-gate discipline (which
disproved orientation hypotheses ~5× this session) is binding: probe EACH
sub-surface on HEAD `cfbf8ff9` before designing, and PARTIAL-SHIP the proven
slice + defer the rest (the accepted outcome — precedent: every prior Wave-5
child). Do NOT try to land all 5 sub-surfaces in one diff; that is
unreviewable. Suggested sub-changes if a sub-surface is large:

### Sub-surface A — Benchmark suite (Neo-vs-Legacy perf)
- ADDITIVE, low-risk. A standalone benchmark harness measuring field access /
  method call / value-type / virtual dispatch / array over N iterations, Neo
  (`Debug_Neo`) vs Legacy (plain `Debug` + useRegister). Report ratios, NOT
  absolute targets (this env is not the perf baseline host).
- Probe: does `ILRuntimeTestCLI` already have any timing scaffold? Is there a
  `Benchmark*` test class? (Likely none — Step 26 creates it.)

### Sub-surface B — Reflection edges on Neo instances
- Reflection on a Neo `ILTypeInstance` (`byte[] Primitives + AutoList
  ManagedObjects`) — does `appdomain.Invoke`/field-reflection work? The F-4
  follow-up (reading IL fields/methods off a CAUGHT IL exception) is the same
  family — 4 broken read paths are documented in neo-deferred-items F-4.
- Probe whether F-4 is subsumed/independent. If Step 26 closes F-4, note it.

### Sub-surface C — Thread-safety / cross-domain
- Is `ExecuteNeo` safe under a pooled-interpreter / multi-thread host? Are
  AppDomain maps touched concurrently? Probe the pooling model (Step 19 delegate
  uses a fresh pooled interpreter; Step 20 async resumes on a fresh pooled
  interpreter). This may be DOCUMENTATION-only (a stated single-threaded
  contract) rather than a code fix.

### Sub-surface D — Debugger adaptation (the trickiest)
- `DebugService` reads frame vars. Under Neo, the frame is `byte* frameBase` +
  `AutoList mStack` (NOT Legacy `StackObject[]`). Does the debugger protocol
  today read `StackObject[]` (Legacy) and NIE/mis-read on Neo? Probe
  `DebugService` + the debugger protocol path. This may be LARGE (touches
  debugger machinery) — likely DEFER if it needs deep protocol work; the
  dump-gate decides.

### Sub-surface E — CrossBindingAdapter adaptation
- Do `CrossBindingAdaptor`s (the CLR<->IL bridge) work under Neo? Probe a
  representative adaptor (e.g. the Step 14 `ExceptionAdaptor`, or `InheritanceAdapter`).
  Likely MOSTLY works (adaptors call back via `DelegateAdapter.NeoInvokeSub`,
  which Step 19 wired); probe for gaps.

## The dump-gate plan (MANDATORY before designing each sub-surface)

For each sub-surface, on HEAD:
1. Is there a real gap, or is it already-working / documentation-only?
2. If a gap: is it SMALL (a focused fix) or LARGE (broad machinery)? If LARGE,
   defer it (documented) and ship the SMALL ones + the additive benchmark.
3. Cite file:line evidence.

The binding lessons (re-affirmed all session): a green smoke does NOT prove a
gate correct; STOP/partial-ship if a designed fix is wrong; probe BEFORE
designing. Expect 2-3 of the 5 sub-surfaces to be no-ops or documentation-only
(the prior pattern).

## Scope recommendation (propose-phase, adjust per dump)

Likely SHIP: (A) the benchmark suite (additive, the core deliverable) + the
SMALL real gaps among B/C/E + documentation of the single-threaded contract (C).
Likely DEFER: (D) deep debugger-protocol work if it needs broad machinery;
the large remainder of any sub-surface. Record each deferral honestly in the
spec delta + `neo-deferred-items.md`.

## Build + test (CRITICAL — copy from portfolio planning-context)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll \
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep   # 215/215 (post-S2 baseline)
# A benchmark filter (whatever prefix the new harness uses) + regression filters separately
```
ALWAYS `-f net8.0`; the CLI filter is a `Contains` substring (no `|`
alternation). `Debug_Neo` prints huge JIT output — normal. For Legacy perf,
build plain `Debug` + `useRegister=true`.

## Spec authoring traps (from handoff)

- `specs/<capability>/spec.md` delta PURE ASCII (a single non-ASCII byte like
  U+00A7 breaks the validator).
- Start every requirement body with "... SHALL ..." on the FIRST hard-wrapped
  line.
- Capability: pick the best-fit existing capability (`neo-optimizer` for the
  benchmark/AOT-validation surface, OR a cross-cutting one). If the change
  spans capabilities, put the primary delta in `neo-optimizer` and reference
  others. The openspec validator is flaky (pre-existing 9/10 false-failures);
  `neo-optimizer` is the one that validates. LEAD does manual archive merges.

## Deliverables

`proposal.md`, `design.md` (with the per-sub-surface dump-gate table: real-gap
vs no-op vs doc-only, each file:line-cited), `specs/<cap>/spec.md` (delta),
`tasks.md`. Scope honestly; partial-ship is the expected outcome for a child
this broad.

## Findings -- neo-step26-perf-validation (2026-07-07, propose; HEAD f3be2788)

NOTE: instructions cited HEAD cfbf8ff9; the actual tip is f3be2788 (Step 25 S2
shipped). Probed on f3be2788 (one commit ahead; the S2 change is additive AOT
loader work, irrelevant to the 5 Step-26 sub-surfaces).

### Per-sub-surface dump-gate verdict (HEAD f3be2788)

- **A Benchmark suite -- REAL GAP (SMALL, ADDITIVE).** No Neo-vs-Legacy
  comparison harness exists. `ILRuntimeTestCLI` has NO timing scaffold (only
  the Step-22..25 special-mode self-check hooks + the generic test loop).
  `TestCases/Test01.cs` HAS `UnitTest_Performance1..15+` methods tagged
  `[ILRuntimeTest(IsPerformanceTest = true)]` that use an INTERPRETED
  `Stopwatch`, BUT the harness marks them IGNORED (probe: ran
  `UnitTest_Performance2` under Debug_Neo -> "Ran 1 tests, 0 failed, 1 ignored";
  no `time:` line, no NIE -- the interpreted Stopwatch does not crash, but the
  ignored-path gives no clean timing). So the bench MUST use HOST-SIDE timing
  (a `NeoStep26BenchCheck` self-check that invokes parameterless bench methods
  via `appdomain.Invoke` and times them with a real host `Stopwatch`), matching
  the Step-22..25 self-check pattern. SHIP.
- **B Reflection edges / F-4 -- DOC-ONLY (DEFER).** CONFIRMED F-4: the
  `ILTypeInstance` indexer + several accessors are `#if !ENABLE_NEO_MODE`
  (ILTypeInstance.cs:27,86,94,379,... -- compiled OUT under Neo), so reflection
  field-reads off a Neo instance return null. F-4's 4 broken read paths are
  each a SEPARATE mechanism (callvirt-on-CLR-interface; callvirt.clr GetType;
  appdomain.Invoke instance re-entry; ILTypeInstance Neo indexer) -> a
  cross-binding-adaptor follow-up, NOT a perf-validation step. Step 26 does
  NOT subsume F-4. The working reflection coverage (catch + isinst) is already
  guarded by `NeoStep14_ILEx_*` (neo-il-exception-throw). DOC-ONLY for Step 26.
- **C Thread-safety / cross-domain -- DOC-ONLY.** The engine is single-
  threaded cooperative: `ILIntepreter.cs:56,151,2164` + Neo/Register arms check
  `Thread.CurrentThread.ManagedThreadId == AppDomain.UnityMainThreadID` (the
  coroutine pump). `DelegateAdapter` (DelegateAdapter.cs:979,1009-1013) uses a
  POOLED interpreter (isolation per-callback, NOT concurrent execution). There
  is no `new Thread()`/`QueueUserWorkItem` in the interpreter core and no
  `lock` on the AppDomain maps -> a single AppDomain is NOT safe for concurrent
  multi-threaded access; that is a DOCUMENTED contract, not a bug. SHIP a spec
  requirement + a comment block stating the single-threaded contract. No code
  change.
- **D Debugger -- LARGE GAP (DEFER).** `DebugService.cs` is built around
  `StackObject*` + `intp.Stack.Frames.Peek().BasePointer` (the Legacy frame).
  Variable inspection is ALREADY Neo-gated to graceful degradation:
  `GetThisInfo` returns "Neo this inspection is not supported yet."
  (DebugService.cs:207-209); `GetLocalVariableInfo` returns "Neo local
  variable inspection is not supported yet." (DebugService.cs:268-271). The
  stacktrace instruction dump IS Neo-adapted (reads CompiledFrame.NeoExecuteBody
  at :143-148). A real fix = reading `byte* frameBase` + `AutoList mStack` +
  CompiledFrame.LocalInfos across ~8 methods (GetThisInfo,
  GetLocalVariableInfo, AddStackFrameInfoVariables, ResolveCurrentFrameBasePointer,
  DumpStack, GetValueExpandable, VisitValueTypeReference, GetStackObjectText) --
  substantial deep debugger-protocol work. DEFER per the planning-context
  guidance ("likely DEFER if it needs deep protocol work").
- **E CrossBindingAdapter -- NO-OP (the gap = F-4).** Step 19 wired
  `DelegateAdapter.NeoInvokeSub` (the CLR->IL callback). The ExceptionAdaptor
  (neo-il-exception-throw) works end-to-end (throw+catch green). The remaining
  gap is F-4 path 1 (`((CrossBindingAdaptorType)e).ILInstance` callvirt-on-CLR-
  interface) = the same deferred cross-binding-adaptor follow-up. NO-OP for
  Step 26.

### SHIP vs DEFER split (proposed)

SHIP: (A) the benchmark suite [core, additive, Neo-only, Legacy-neutral] +
(C) the single-threaded-contract documentation [spec requirement + a comment].
DEFER (documented in the spec delta + neo-deferred-items.md): (D) debugger
variable inspection under Neo; (B)/(E) the F-4 four-mechanism reflection-on-
Neo fix (a cross-binding-adaptor follow-up; Step 26 does NOT subsume F-4).

### Benchmark design (locked)

- A host-side self-check `NeoStep26BenchCheck.Run(appdomain)` (gated
  `#if ENABLE_NEO_MODE && DEBUG`), driven via the existing CLI special-mode
  hook (`if (nameFilter == "NeoStep26Bench")`), mirroring Step-22..25.
- A dedicated `TestCases/NeoStep26BenchProbe.cs` with 5 parameterless static
  methods (BenchFieldAccess / BenchMethodCall / BenchValueType /
  BenchVirtualDispatch / BenchArray), each a tight N-iteration loop returning a
  primitive (int/long) -- avoids F-12 (ref-return) + the Step-6 parameterless-
  Run shim limitation.
- The self-check invokes each via `appdomain.Invoke` (re-entry), times it with
  a HOST `System.Diagnostics.Stopwatch`, asserts the return EQUALS the known-
  expected value (the correctness-of-measurement gate, divide-assert pattern),
  and emits structured `BENCH:<name>:<iterations>:<elapsedTicks>` lines.
- The self-check does NOT assert a ratio threshold (this env is not the perf
  baseline host); it only asserts each bench COMPLETED + returned the expected
  value + produced a finite positive timing (the gate is measurement works +
  the bench did the right work, NOT "Neo is fast" -- the "green smoke does not
  prove a gate" lesson).
- A runner script (`scripts/run-neo-bench.ps1` + `.sh`) runs the CLI under
  Debug_Neo AND plain Debug (useRegister=true), parses the BENCH lines, prints
  `name | neo_ms | legacy_ms | ratio`. The SAME TestCases.dll is interpreted by
  both engines -> the ratio is apples-to-apples.

### Capability chosen

`neo-optimizer` (primary; it already houses the Step-22..25 AOT/self-check
requirements -- templates, .neo format, loader, self-checks). The reflection /
threading / debugger / adaptor deferrals are documented as cross-references in
the delta. The Step-26 delta ADDS: (1) a benchmark-suite requirement; (2) a
single-threaded-contract requirement; (3) a deferred-follow-ups requirement
(D debugger + F-4 reflection + E cross-binding callvirt-on-CLR-interface).

### Files the implementer will touch

- `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep26BenchCheck.cs` (NEW, Neo-only
  host-side self-check) -- mirrors NeoStep25LoadExecCheck structure.
- `TestCases/NeoStep26BenchProbe.cs` (NEW) -- the 5 bench methods.
- `ILRuntimeTestCLI/Program.cs` -- add the `NeoStep26Bench` special-mode hook
  (mirrors the Step-25 hook; ~20 lines under `#if ENABLE_NEO_MODE`).
- `scripts/run-neo-bench.ps1` + `scripts/run-neo-bench.sh` (NEW) -- the 2-config
  runner + ratio reporter.
- `ILRuntime/Runtime/Intepreter/ILIntepreter.cs` -- a SINGLE comment block near
  the `UnityMainThreadID` check (the single-threaded-contract doc; no code
  change).
- (OPTIONAL) `scripts/README-neo-bench.md` -- how to read the ratios.

### Regression risk: LOW.

The benchmark is additive (a new self-check + a new probe + a CLI hook + a
script), all Neo-only / config-agnostic. No engine change (C is a comment).
Gate: full NeoStep smoke (215/215) + Legacy-neutral (the probe methods are
parameterless static, runnable on both engines; the host-side self-check is
Neo-gated). The bench self-check is a SEPARATE filter (`NeoStep26Bench`); it
does NOT run under the `NeoStep` regression filter.

### Partial-ship signal

EXPECTED (this child is broad). 3 of 5 sub-surfaces (B/D/E) are defer/doc-only
(the prior pattern). Ship the proven slice (A + C doc); defer D + F-4 honestly.
