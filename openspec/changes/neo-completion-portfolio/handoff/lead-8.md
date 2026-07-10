# Handoff: neo-completion-portfolio — LEAD #8 (relay at ~89% context; MAJOR milestone)

> Read THIS FIRST (supersedes lead-7), then `portfolio-run.json` (authoritative per-child status) +
> `planning-context.md`. Session relay (real context ~89% of the 1M window; the openspec probe's
> `limit=200000` is a STALE soft-target — real model glm-5.2 / Opus 4.8 `[1m]`; relay when
> `contextTokens/1000000 >= ~0.9`). Successor: the 2 PARKED children (8, 17) — and re-audit their
> "foundational" framing against the child-4 lesson (see below).

## Position
HEAD **`5ec934c9`** (pushed, in sync). NeoStep **274/0/0** (was 241 at lead-6, 263 at lead-7).
**This session (lead-7 + lead-8 continuation) shipped 15 children; 2 remain PARKED.**

## 🏁 MAJOR MILESTONE — 15 shipped this session
| child | commit | one-line |
|---|---|---|
| neo-ret-vt-with-ref-fields (HIGH#1) | `4e32dec6` | Ret opcode value-type-with-ref-fields return |
| neo-async-valuetask-asyncvoid (4) | `9c9b795d` | **ValueTask<T> + async-void suspend** (UNBLOCKED — see lesson) |
| neo-async-taskrun-ildelegate (5) | `b895e350` | Task.Run(syncILLambda) — stale autogen binding |
| neo-async-valuetask-zeroalloc (6) | `5ec934c9` | **Zero-alloc(reduced) ValueTask suspend** (IValueTaskSource-backed) |
| neo-aot-clrbase-iface (7) | `652eca41` | Cecil-free CLR base/interface (CrossBindingAdaptor) |
| neo-aot-crossprocess (9) | `21a0d086` | Cross-process .neo portability (TEST-ONLY, proven-by-construction) |
| neo-aot-multi-hotfix (10) | `de407c8a` | Cecil-free cross-assembly refs (lazy re-resolution) |
| neo-debugger-ilvt-local (11) | `d544d25e` | Debugger IL-VT-local field reconstruction |
| neo-debugger-aot-body (12) | `48af2123` | AOT-body variable inspection (.neo V4 local-var metadata) |
| neo-debugger-cli-protocol (13) | `b80170f3` | **DAP capstone** (in-proc adapter, ~7 core methods) |
| neo-byref-clr2il-delegate (14) | `e4ceffc1` | CLR->IL delegate byref callback (2-layer gap) |
| neo-byref-ldind-ref-heap (15) | `83992168` | ldind_ref heap-IL-ref-field (content-based dispatch) |
| neo-aot-byref-wireup (16) | `a289e0a4` | NO precompile gap (byref convertor is runtime-only) |
| neo-qstruct-reproducer (18) | `5203b5eb` | NON-REPRO confirmed-closed + 3 guards |
| neo-qlong-reproducer (19) | `5203b5eb` | NON-REPRO confirmed-closed + 3 guards |
(+ `98bc6651` disprove-B1-field-layout — a foundational investigation that DISPROVED the field-layout
gap; 4 guards.)

## 🔑 THE KEY LESSON — child 4's "foundational gaps" were DISPROVEN (re-audit 8 & 17!)
Child 4 was PARKED (lead-7) on "a cluster of foundational engine gaps" (B1 field-layout, B2 binder).
**Both were WRONG** — the real blockers were tractable, child-4-scope fixes:
- **B1 (field-layout collision) was a RED HERRING** — a CLR-struct-with-ref field + a sibling primitive
  field sharing `PrimitiveOffset` is BENIGN (disjoint `Primitives[]`/`ManagedObjects[]` storage). The
  real bug was a `curPrim` call-arg-marshalling bug (the ValueTask builder `this` is 16 flat bytes, not
  8; hardcoded `curPrim+=8` undershot). Fix: `BuilderThisManagedSize(method)`.
- **B2 (binder NIE) was a REGISTRATION miss** (the `<string>` builder/awaiter/Task members weren't
  registered → reflection fallback → Area-4b NIE), NOT a foundational binder gap. Fix: register `<string>`.
**IMPLICATION FOR THE PARKED 8 & 17:** re-investigate whether their "foundational"/"multi-step" framing
is real or a mis-attribution. Child 8 (Cecil-free generic) has a concrete reproducer + a bounded 5-site
plan; child 17 (IL-VT multidim) has a green stashed ctor fix + 3 boxing sub-gaps — the ctor fix is
shared/non-Neo-gated (needs Legacy-suite verification) but the boxing sub-gaps may be tractable like B1.

## PARKED (2 children — the remaining work)

### child 8 `neo-aot-generic-cecilfree` — bounded multi-step (5-site JIT back-half rework)
Cecil-free generic-instance load reads Cecil at 5 sites (ILMethod GenericParameterCount :189 +
MakeGenericMethod :1434 + FindGenericArgument :415; JITCompiler BuildInitialRegisterTypes :1220 +
AllocateLocalStackSpaces :1648 [RunNeoBackHalf is Cecil-fed]; NeoAssemblyLoader.ResolveVariableType :221)
+ the .neo lacks the open def's GenericParameterCount/generic-param names. Fix = a `.neo` V5
GenericParamNames table + Cecil-free generic shell + Cecil-free JIT back-half + S2 bind. A GREEN
reproducer is committed (`NeoStep25CecilFreeGenericProbe`; G1 template-bind + G2 fresh-instance
template-mutation FAIL on HEAD = forward signal). Route to `neo-aot-generic-cecilfree-backhalf`. Bounded
plan in `openspec/changes/neo-aot-generic-cecilfree/tasks.md`. **Durable pitfall:** a trivial generic
method INLINES into its caller → functional cells pass WITHOUT exercising the Cecil-free generic
mechanism; only template-mutation guards (G1/G2) are load-bearing.

### child 17 `neo-array-multidim-ilvt` — ctor fix (stashed, green) + 3 boxing sub-gaps
IL-VT-element `[,]` fails at ARRAY-CTOR token resolution (a SHARED bug: `ILType.GetConstructor`/`GetMethod`
walked the empty array TypeReference). A GREEN load-bearing sub-fix was found (ILType array-ctor/GetMethod
delegates to the underlying CLR array type via `ResolveArrayClrType`; benefits ALL IL array types) but it's
SHARED/non-Neo-gated + only build-Legacy-neutral verified → **STASHED** (`child17-array-multidim-ilvt-partial`)
for the follow-up to ship with full Legacy verification. The CORE then hits 3 coupled F-7B/F-10-complexity
boxing sub-gaps (box IL-VT arg for reflection CLR call; unbox for Get return; multi-dim ldelema). Route to
`neo-array-multidim-ilvt-boxing`. `openspec/changes/neo-array-multidim-ilvt/{blocked,design,tasks}.md`.

## Other follow-ups (surfaced, not blocking)
- **NeoStep24CliRoundtrip is 1/5 PRE-EXISTING** (lead-6's "5/5" was stale; confirmed by stashing all
  session work) — a Step-24 CLI-roundtrip regression dig + baseline refresh.
- **`neo-aot-delegate-exe-parity`** — a GENERAL AOT-vs-JIT delegate/callback body discrepancy (non-byref
  controls ALSO fail; surfaced by child 16). Step-24 domain.
- **Child 13's `next`/step** — a Neo step-resume engine gap (soft-PASS in the DAP check). Plus full DAP
  compliance (evaluate/watch/conditional-bp/threads/pause).
- **Stale-autogen-binding regen sweep** (2 found: child 5 Task.Run + neo-f13). A one-off `*_Binding.cs`
  regen pass.
- The heap IL-VT-FIELD read (F-4 indexer) could lift child 11's recursion (frame-local path is richer).

## Durable findings (cross-cutting — carry forward; full list in each ship-log)
1. **`CrossBindingAdaptor : IType` (NOT ILType)** — base/interface chain walks must short-circuit `is
   CrossBindingAdaptor` (child 7).
2. **APPROACH-1 hash non-determinism is IRRELEVANT for portability** (name-aliased at load; child 9).
3. **Silent NULL field types = the Cecil-free generic failure mode**; `ReResolveCrossAssemblyRefs` pattern
   (child 10).
4. **NEVER bit-flag a byref's OFFSET half** — content-based dispatch (child 15; CLR FieldInfo hashes are
   non-deterministic).
5. **A "foundational gap" framing may be a MIS-ATTRIBUTION** (child 4's B1/B2 were a marshalling bug +
   a registration miss). Re-audit before accepting "foundational" (child 8/17).
6. **The ValueTask builder `this` marshals as 16 flat bytes** (vs Task's 8) — any builder-byref-`this`
   redirect must use the actual managed size (child 4).
7. **`LowerNeoOffsets` ordering** (re-affirmed, neo-ret-vt): stamp register INDEX before LowerR1.
8. **`DebuggerServer.IsAttached` + send-event methods must be `virtual`** for an in-proc frontend (child 13).

## Build/test (CRITICAL — ALWAYS `-f net8.0`; CLI=`Debug_Neo`, NEVER TestCases with `Debug_Neo`)
```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental   # after touching engine files
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep   # 274/0/0
```
`Debug_Neo` prints LOTS of JIT output — grep the summary. >10-60s run = infinite loop → KILL. **PUSH
GOTCHA:** `git config lfs.https://github.com/DumoeDss/ILRuntime.git/info/lfs.locksverify false` (once;
persists in `.git/config`). Commit trailer `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
Use `git commit -F .git/cmsg.txt` then `git push` separately. **⚠️ commit-hygiene lesson (lead-8):** before
`git commit`, run `git status` + check the INDEX (col-1 `M` = staged) — a selective `git add` does NOT
unstaged already-staged files; lead-8's `98bc6651` accidentally committed the child-4 partial (red VT
probes) this way. Always verify the staged set before committing. Subagents CAN run bare
`dotnet build`/`dotnet run` (allowlisted) + Grep/Read tools (NOT bash grep/tail/cd). Subagent dispatches
occasionally 502/socket — recover from on-disk work + transcripts (`<session>/subagents/agent-*.jsonl`);
the LEAD taking over a focused fix directly is a viable fallback.

## Next action — successor session (lead-9)
1. **Re-audit the 2 parked children (8, 17) against the child-4 lesson** — their "foundational/multi-step"
   framing may be a mis-attribution. For 17: pop `child17-array-multidim-ilvt-partial`, verify the ctor
   fix Legacy-neutral (full 519-suite), then tackle the 3 boxing sub-gaps (they may be tractable). For 8:
   the 5-site back-half plan is bounded; start with the `.neo` V5 GenericParamNames table.
2. **NeoStep24CliRoundtrip 1/5 regression dig** (pre-existing) + baseline refresh.
3. The other follow-ups above (DAP `next`/step, delegate-exe-parity, stale-binding regen).
4. **Drive DEEP (1M context)** — relay near the real limit (~90%+), not early.

Resume: fresh session, `/opsx:auto neo-completion-portfolio`, read THIS document first.
