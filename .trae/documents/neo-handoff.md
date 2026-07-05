# Neo Overhaul — Handoff Document

> **Read this FIRST when continuing the ILRuntime Neo work.**
> Companion to `neo-implementation-steps.md` (the 26-step roadmap) and
> `neo-deferred-items.md` (the deferred-items resolution map).
> Last updated: 2026-07-05. Branch: `features/object-model-overhaul`.
> Authoritative current state lives in this file + `neo-deferred-items.md` +
> the `openspec/specs/` capability specs + `openspec/changes/archive/`.

This doc captures everything a fresh session needs to pick up the Neo overhaul:
the environment, what's done, the workflow that's been working, the non-obvious
codebase gotchas, the open follow-ups, and where everything lives.

---

## 1. Project & environment (the load-bearing facts)

- **ILRuntime** = pure-C# IL interpreter runtime (Ourpalm, MIT). Loads .NET DLLs
  (CIL), JITs them to `OpCodeR` (register VM), interprets via `ExecuteNeo` (Neo)
  or `ExecuteR` (Legacy). Used for Unity/iOS hot-update where there's no JIT.
- **This branch** (`features/object-model-overhaul`) is doing the **Neo overhaul**:
  new object model (`byte[] Primitives + AutoList ManagedObjects`) + new
  interpreter `ExecuteNeo` (`byte*` compact frame). Conditional compilation:
  - `ENABLE_NEO_MODE` = on → Neo (new model + `ExecuteNeo`); off → Legacy
    (`StackObject[]` + `ExecuteR`). **The two object models MUST NOT be mixed.**
  - `USE_OLD_OBJ_MODEL` = legacy macro, being cleaned up, overlaps `!ENABLE_NEO_MODE`.
- **Neo progresses by 26 steps** (see `neo-implementation-steps.md`). Unimplemented
  instructions throw `NotImplementedException` tagged with a Step number inside
  `ExecuteNeo` — that is a **TODO, not a bug**.

### Build (CRITICAL — the sln CANNOT build whole)
`ILRuntime.sln` fails: the `Debugging/VS2022/ILRuntimeDebuggerLauncher2022`
(net472 VSIX) can't consume this branch's `netstandard2.1` (NU1201). **Build only
the dev subset:**
```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo   # transitively builds ILRuntime/ILRuntimeTestBase/LitJson, 0 errors
dotnet build TestCases/TestCases.csproj -c Debug                      # -> TestCases/bin/Debug/netstandard2.1/TestCases.dll
```
- `nuget.config` (repo root) uses `<clear/>` + nuget.org to bypass a broken
  machine-level NuGet source (`N:\...\Shared\NuGetPackages` old VS residue, NU1301).
  Deleting it restores defaults.
- Build the **CLI** with `Debug_Neo`; **NEVER** build TestCases with `Debug_Neo`
  (output path is unchanged, but the config is wrong for it). `dotnet` 8.0.400.
  `msbuild` is not on PATH (doesn't matter for dotnet builds).

### Tests (custom reflection framework, NOT xUnit)
Tests = `public static` parameterless methods in `TestCases/` (optionally
`[ILRuntimeTest]`), compiled to the TestCases DLL, run by the interpreter.
```bash
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll \
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep
```
- **ALWAYS pass `-f net8.0`** to `dotnet run` (CLI multi-targets netcoreapp3.0 + net8.0).
- `Debug_Neo` prints LOTS of JIT/optimizer output (`OUTPUT_JIT_RESULT` macro) —
  normal; filter stdout for the pass/fail summary line.
- A test taking **>10 s** usually means an interpreter infinite loop — kill + investigate.
- **Baselines (re-verify if the DLL/patch change):**
  - **Neo** (`Debug_Neo` + `useRegister=true`, filter `NeoStep`): **91/91 green**
    as of HEAD `180d73a0`. (Full Neo run is ~430/519 failing on purpose — unimplemented
    ops; only the `NeoStep` smoke matters day-to-day. `NeoOptHardening` tests run
    under a separate filter.)
  - **Legacy/register** (plain `Debug` + `useRegister=true`): the 519-test green
    baseline is ~518/519 (1 long-unlocated fail). This is the **regression
    reference** for shared-engine/shared-pass changes — confirm Legacy-neutral.

### Other env notes
- Repo has many checked-in binary deps (`Dependencies/*.dll`/`*.pdb`); `git status`
  always has `.pdb`/`.gitignore` churn — **noise, ignore it** (don't commit it).
  Git LFS is enabled; verify LFS pulled if dep DLL sizes look wrong.
- The **Write tool corrupts ~0.5% of CJK characters to U+FFFD** on large payloads.
  Author files **primarily ASCII**; after a large CJK write, validate/fix with a
  pure-ASCII PowerShell codepoint check (see memory `cjk-write-encoding-corruption`).

---

## 2. Current state (as of 2026-07-05, HEAD `180d73a0`)

### Completed Neo steps (JIT path)
Steps **1-18** of the 26-step roadmap, plus derived follow-ups:
- 1-10 (macros, object model, static fields, Neo frame, arithmetic, managed stack,
  CLR interop, call, VTable+callvirt) — done before this session series.
- **11** interface dispatch · **12** in-frame value types + inline field access ·
  **12b** `Move_Vt` + `LowerMove` (VT copy) · **13** Box/Unbox (areas 1-2) ·
  **14** exception handling (try/catch/finally) · **15** isinst/castclass ·
  **16** array element access (rank-1) · **17** ref/out + Ref Slot + ldelema ·
  **18** CLR type newobj (+ `throw new ClrException`).
- Derived: **[OPT-HARDEN]** K1 FCP fix · **13b** unified CLRMethod param layout
  (closes K2) · **[CATCH-COMPLETE]** `CheckExceptionType` IL branch.

### Test smoke progression
Neo `NeoStep`: 37 (after 12) → 41 (12b) → 49 (13) → 58 (14) → 65 (15) → 72 (16)
→ 81 (17) → 84 (13b) → **91 (18)**. (`NeoOptHardening` K1 tests run separately;
CATCH-COMPLETE added no new test.) Legacy 519 baseline unaffected.

### openspec capabilities (`openspec/specs/`)
`neo-dispatch`, `neo-value-types`, `neo-boxing`, `neo-exceptions`, `neo-type-checks`,
`neo-arrays`, `neo-byref`, `neo-optimizer`, `neo-newobj`. (9 total.) These are the
durable canonical specs; per-step deltas are merged in at archive time.

### Git
Branch `features/object-model-overhaul`, in sync with `origin` (GitHub,
`DumoeDss/ILRuntime.git`). Recent commits (newest first):
`180d73a0` docs(neo) roadmap refresh · `83584d47` catch-complete · `57e0af54` step 18 ·
`06abf866` step 13b · `21b68d92` step 17 · `e3fa8ef2` opt-hardening ·
`0a6bd46a` deferred-items doc · `d7519950` step 16 · `cf4a0331` step 15 ·
`d7350b3a` step 14 · `9e71caf2` step 13 · `6a8d1d2c` step 12b · `424b9730` step 12 ·
`d24e4415` step 11.

---

## 3. The workflow that's been working (reuse it)

The user drives the Neo overhaul via the **openspec autopilot**
(`/openspec-opsx-auto auto-decompose ...`), one step per run, **commit + push
after each step's review is clean**. Each run = a full pipeline:

`propose → apply → verify → review-loop → ship → archive → (LEAD commit + push)`

- **Tier A** (`CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1`): the LEAD (you) orchestrates
  **role-isolated leaf workers** — `planner` (propose), `implementer` (apply),
  `reviewer` (verify), `fixer` (review-loop), `shipper` (ship+archive). Workers
  never spawn subagents; the LEAD is the sole orchestrator. Hand off between
  stages through the **change directory** (`openspec/changes/<name>/`:
  proposal.md, design.md, tasks.md, specs/, review-report.md, ship-log.md) +
  `planning-context.md` (seed) + `auto-run.json` (run-state).
- **Author != verifier** is hard-enforced: reviewer ≠ implementer; the fixer of a
  finding ≠ its author; the re-reviewer ≠ the fixer.
- **The `NeoStep<N>Test.cs` convention**: add `TestCases/NeoStep<N>Test.cs`
  (`public static` parameterless methods) per step; the `NeoStep` smoke filter
  catches them. Run the FULL `NeoStep` smoke (not just the new step) as the
  regression gate — value types / calls / exceptions are pervasive.

### Skill-mapping gotchas (the OPSX pipeline's stage→skill map has gaps for THIS repo)
- **propose** = `openspec-propose` ✓ · **apply** = `openspec-apply-change` ✓ ·
  **verify** = `openspec-gstack-review` ✓ (write findings to `review-report.md`
  yourself — the skill prints but saves nothing) · **archive** =
  `openspec-archive-change` ✓ (sync delta → `openspec/specs/<cap>/spec.md`,
  move to `archive/`; its validator false-positives sometimes — a manual move is
  fine, the synced canonical spec is the durable record).
- **review-loop** has NO discrete skill → the **LEAD drives it**: each pass = a
  reviewer worker via `openspec-gstack-review`; route fixes to a non-author
  fixer; re-confirm the delta with a non-author reviewer. Cap 3 rounds.
- **ship** (`openspec-opsx-ship`) has no clean skill; `openspec-gstack-ship` is a
  **Rails/JS `/ship` workflow** (`bin/test-lane`, `VERSION`, Greptile, `gh pr`)
  — a **hard MISFIT** for this C# repo. **Do NOT invoke it.** Just write
  `ship-log.md` (verification evidence + review verdict + delivered/deferred scope).
- **commit/push** is NOT part of openspec finalization here — the LEAD does it
  per the user's per-phase authorization (see §4 commit rules).

### The review-loop earned its keep (adversarial non-author review caught real bugs)
- **Step 17 B1 (Blocker)**: the addrAlias COEXIST gate had silent corruption on
  register reuse (a global, never-reset `hasFoldableUse`; an escaped byref read a
  stale folded offset). The 79/0 smoke MISSED it; the reviewer reproduced it with
  a probe. Fix was subtler than the reviewer's direction (static addrAlias
  last-write-wins + branch/compare operand wrongly alias-resolved) → per-instruction
  `liveAliasMap` snapshot + branch/Initobj split. Lesson: **a green smoke does NOT
  prove an optimizer gate correct** — construct adversarial reuse/escape probes.
- **OPT-HARDEN K1**: the planner's designed fix was a no-op (wrong root cause);
  the implementer STOPPED rather than ship a broken fix; re-attempt with the
  corrected `ldloca-kill` succeeded. Lesson: **probe the IR dump before designing
  an optimizer fix; STOP if the fix doesn't work, don't force it.**

### Commit + push convention
- Stage **precisely**: the step's source files + test + `openspec/` artifacts +
  the deferred-items doc if updated. **Exclude** the `.pdb`/`.gitignore`/
  `nuget.config`/`.claude/`/`.vscode/`/`CLAUDE.md` churn (pre-existing, unrelated).
- Commit message: `Neo step N: <topic>` (or `Neo opt-hardening:` / `Neo
  catch-complete:` / `docs(neo):`). End every commit with:
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
- `git push origin features/object-model-overhaul` after each phase (the user
  pre-authorizes push per phase). Confirm the branch is in sync before the next step.

### Deferred-items doc maintenance
When a step CLOSES a deferred item, **update `neo-deferred-items.md`** (move to
§4 Resolved, or mark partial, or add a new follow-up) and commit it with the
step. Keep the doc from drifting — it's the authoritative current-state tracker.

---

## 4. Non-obvious codebase gotchas (these bite)

- **`OpCodeR` is `[StructLayout(LayoutKind.Explicit)]`** — `Register1`/`DstOffset`
  alias @offset 4, `Register2`/`SrcOffset` @6, `Register3`/`Operand` @8, `Operand2`
  @12, `Operand3` @16, `Operand4` @20 (`OpCode.cs`). The offset-lowering pass
  (`LowerNeoOffsets` in `Optimizer.Neo.cs`) **overwrites register indices with
  byte offsets**. So a pass that needs a register INDEX must run BEFORE
  `LowerNeoOffsets` (e.g. `LowerMove` runs inside `TypeSpecializeNeoOpcodes`,
  pre-lowering). If you read `ip->Register1` at RUNTIME, it holds a byte offset
  (post-lowering), not an index. The Step 12 + OPT-HARDEN lessons were exactly
  this index-vs-offset confusion. **Snapshot `preOp = op` before a lowering case
  mutates it.**
- **Shared vs Neo-only optimizer passes.** FCP / BCP / copy-prop / RegisterCleanup
  are **SHARED** (used by Legacy `ExecuteR` too) — a change there MUST be
  Legacy-neutral (gate with `#if ENABLE_NEO_MODE`, or confirm plain-`Debug`
  compiles it out; verify with a Legacy filter run). `LowerNeoOffsets`, the
  `addrAlias`/`liveAliasMap` gate, `AllocateNeoCallParamSlot` are **Neo-only**
  (inside `Optimizer.Neo.cs`, file-gated). The CLR binding generator emits
  **separate `*Neo` variants** under `#if ENABLE_NEO_MODE` (`GenerateMethodWraperCode_Neo`,
  `AppendArgumentCodeNeo`, `RegisterCLRMethodRedirectionNeo`) — only touch those;
  Legacy CLR binding is the reference.
- **The Neo frame model.** A frame = `byte[]`-style byte region (`frameBase`) +
  `AutoList mStack` ref region (`frameRefBase`). An IL value-type LOCAL = flat
  bytes (`slot.Offset..+TotalPrimitiveSize`) + ref slots
  (`mStack[frameRefBase+slot.RefOffset..+TotalReferenceCount]`) — same shape as an
  `ILTypeInstance` (`byte[] Primitives` + `AutoList ManagedObjects`), so a copy is
  a byte-copy + ref-copy (the `CopyFrameToIL`/`CopyILToIL`/`CopyILToFrame` helpers
  + `Move_Vt`). A **CLR value-type LOCAL** is a **boxed object reference**
  (`RefCount=1`, `localIsRef=true`), NOT flat bytes (flat bytes exist only for
  array elements / by-value params / IL-typed fields). An 8-byte **Ref Slot** =
  `(objectIndex:int, offset:int)`: `-1` = frame-native (absolute byte offset);
  `>=0` = mStack object (field offset). `addrAlias` (Step 12) folds
  `ldloca;[ldflda;]stfld/ldfld/initobj` chains to compile-time offsets as a
  zero-overhead fast path; the Step 17 COEXIST gate (`liveAliasMap` + escape
  consumer-scan) makes a dest real only when its address escapes.
- **Test harness limitations** (NOT xUnit). A test = `public static` parameterless
  method. **Throw-asserting tests are hard**: `throw new T()` needs newobj. As of
  now: `throw new ClrException()` + try/catch IS green-testable (Step 18 CLR newobj
  + Step 14 catch); `throw new ILExceptionType()` is NOT (needs the
  Exception-adaptor — see D-IL-EXCEPTION-THROW). For negative/exception cases use
  the **DivideByZero-assertion pattern** (`int x = 1/0;` raises
  `DivideByZeroException`; assert via the value path) or a try/catch that sets a
  flag. `[ExpectedException]` does not exist.
- **Legacy is the REFERENCE, not a target.** `ILIntepreter.Register.cs` `ExecuteR`
  has the mature implementations (VTable, exception handling, newobj, etc.). When
  implementing a Neo step, read the Legacy arm for the SEMANTICS (Neo uses the
  frame/Ref-Slot model, not StackObject, but the dispatch logic is the reference).
  Never modify Legacy to make Neo work.

---

## 5. Deferred items — current state (authoritative: `neo-deferred-items.md`)

### Resolved (don't re-litigate)
- **K1** — FCP value-type-move mis-propagation (copy-then-mutate silent bug). Fixed
  via the `ldloca-kill` (Neo-only, Legacy-neutral) in [OPT-HARDEN].
- **K2** — Step 8 VT-by-value param copy (reads primitive as mStack index). Fixed
  in Step 13b (unified CLRMethod param layout + `ReadNeoValueType`).
- **D-LDELEMA** — `ldelema` opcode. Fixed in Step 17 (IL VT array path; CLR
  primitive-array ldelema still NIE).
- **Q-NEWOBJ / Q-STRUCT / Q-LONG** — NOT reproducible on HEAD (probes pass;
  distinct frame regions per register). Closed as non-reproducible.

### Open follow-ups (the next high-value work)
- **[VT-THIS-ADDR]** (highest value) — IL value-type `newobj` (Q-VT-NEWOBJ). Blocked
  on VT field-access lowering consistency: a VT ctor's `this` is laid out as
  in-frame bytes, so `this.field=` lowers to in-frame `_Inline` writes, but
  `addrAlias` only tracks `ldloca`/`ldflda` addresses → caller (index/Ref-Slot)
  and callee (in-frame bytes) representations can't agree. A heap-instance-`this`
  fallback fails for the same reason (it IS the broad D2 change, which would break
  the existing in-frame VT tests). The `newobj`-instruction path surfaces a loud
  Step-18 NIE; the LOCAL form (`VT x = new VT()`) compiles to `ldloca;call ctor`
  and crashes opaquely (pre-existing). **Touches Step 12 + every VT instance method.**
- **[OPT-HARDEN-2]** — F-MAJ-1: `AllocateLocalStackSpaces` slot-reuse/liveness bug
  with 2+ simultaneous CLR struct locals → silent wrong result (one struct's slot
  corrupted while another is live). Pre-existing (stash-proven); Step 13b made it
  reachable. The Step 13b tests work around it (single CLR struct local at a time).
- **D-IL-EXCEPTION-THROW** — end-to-end IL-exception catch needs (a) a registered
  `System.Exception` `CrossBindingAdaptor` (`ILType.cs:1418` TypeLoadException
  without it) + (b) Throw handling for IL instances (both engines do
  `mStack[idx] as Exception` → a plain IL class NREs). The `CheckExceptionType` IL
  branch (CATCH-COMPLETE) is necessary-but-not-sufficient. Positive IL-catch test
  reserved for this pass.

### Partial closes (the boxed/follow-up halves)
- **K2-FAM** boxed-ref bridge (CLR VT local sourced from Box/Initobj, passed by
  value) — deferred; the flat-bytes / return-sourced path works (Step 13b).
- **Step 13 areas 4** — CLR binding `Unsafe.Unbox<T>` direct-call + value-type-`this`
  (WriteBackInstance elimination is a no-op for Neo); **CLR-method ref/out** (typed-
  ref bridge); **CLR-object stind/ldind via field hash**. All → a future step
  (13b was Neo-only-codegen; Legacy untouched).
- **Step 17** — `constrained.`-on-VT full dispatch (callvirt needs byref `this`);
  `Stobj`/`Ldobj` ref-slot loop (primitives only now); generic-byref / `fixed` /
  interface-on-VT-constrained.
- **Opportunistic** — peephole + `PatchKind.IsinstResult` (no patch-infra exists);
  array-completion variants (`Stelem_I`, generic-token `Ldelem`/`Stelem`, native
  `Ldelem_I`/`U8`, multi-dim); `cgt-un` comment-nit; catch-wrapper (matches Legacy,
  accept).

---

## 6. What's next on the roadmap

The Neo JIT path covers Steps 1-18 (+ derived). Remaining roadmap:
- **Step 19** — Delegates (`ldftn`/`ldvirtftn` + DelegateAdapter; CLR→IL call
  convention for `InvokeILMethod`). Dep: Steps 8/9/10.
- **Step 20** — Async/Await (Builder redirect + ILAsyncContext). Large.
- **Steps 21-26** — debugger integration + Neo AOT (`ilrt_neoc` precompiles `.neo`;
  pure optimization layer — Step 22-26).

**Recommended next:** either **Step 19 (delegates)** (continues the roadmap) or
**[VT-THIS-ADDR]** (the highest-value fix — unlocks full value-type construction;
it's a prerequisite for several partial closes). The user's standing pattern:
drive via the autopilot, commit + push per phase.

---

## 7. Where everything lives (orientation map)

- **`.trae/documents/`** (read before any Neo work — CLAUDE.md says so):
  - `neo-implementation-steps.md` — the 26-step roadmap + dependency graph.
  - `neo-deferred-items.md` — the deferred-items resolution map (authoritative
    current-state tracker; maintained as items resolve).
  - `neo-handoff.md` — THIS FILE.
  - `object-model-design.md` / `object-model-neo-design.md` — the object-model +
    Neo-interpreter design (Call convention, VTable, exception, Box, async, etc.).
- **`openspec/specs/<capability>/spec.md`** — the 9 durable canonical capability
  specs (deltas merged in at archive time).
- **`openspec/changes/archive/2026-07-04-implement-neo-step*/`** — per-step
  proposal.md / design.md / tasks.md / review-report.md / ship-log.md /
  planning-context.md (the detailed history; mine these for prior-art decisions).
- **`TestCases/NeoStep<N Test.cs`** + `NeoOptHardeningTest.cs` — the tests.
- **Core code (`ILRuntime/`):**
  - `Runtime/Intepreter/ILIntepreter.cs` — shared engine (`HandleException`,
    `GetCorrespondingExceptionHandler`, `CheckExceptionType`, `FindExceptionHandlerByBranchTarget`).
  - `Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` — `ExecuteNeo` (the giant switch).
  - `Runtime/Intepreter/RegisterVM/ILIntepreter.Register.cs` — Legacy `ExecuteR` (reference).
  - `Runtime/Intepreter/RegisterVM/JITCompiler.cs` — CIL→`OpCodeR` translation.
  - `Runtime/Intepreter/RegisterVM/Optimizer.*.cs` — `Optimizer.Neo.cs` (Neo-only:
    `LowerNeoOffsets`, `addrAlias`/`liveAliasMap`, `AllocateNeoCallParamSlot`),
    `Optimizer.FCP.cs`/`Optimizer.BCP.cs`/`Optimizer.Utils.cs`/`Optimizer.RegisterCleanup.cs` (shared).
  - `CLR/TypeSystem/ILType.cs` — field layout, `NaturalAlignment`, VTable,
    interface map, `CanAssignTo`.
  - `Runtime/Intepreter/ILTypeInstance.cs` — instance memory layout.
  - `Runtime/Intepreter/OpCodes/OpCodeREnum.cs` + `OpCode.cs` — the opcodes + the
    `[StructLayout(Explicit)]` union.
  - `Runtime/CLRBinding/` + `CLR/Method/CLRMethod.cs` — CLR binding (Neo `*Neo` variants).
- **Memory (`~/.claude/projects/.../memory/`):** `dev-build-test-workflow`,
  `cjk-write-encoding-corruption`, `openspec-autopilot-skill-mapping`,
  `csharp-lsp-mcp-plugin`. (Auto-surfaced next session via MEMORY.md.)
- **CLAUDE.md** (repo root) — the project instructions; the entry point that
  points to `.trae/documents/`.

---

## 8. Quick-start checklist for the next session

1. `git log --oneline -5` + `git status -sb` — confirm you're on
   `features/object-model-overhaul`, in sync with origin, clean (modulo .pdb noise).
2. Read this file + `neo-deferred-items.md` (the §1 sequencing + the open follow-ups).
3. Decide the next unit of work (Step 19, or [VT-THIS-ADDR], or another follow-up).
4. Build the CLI (`dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`)
   + TestCases (`dotnet build TestCases/TestCases.csproj -c Debug`) — confirm 0 errors.
5. Run the `NeoStep` smoke — confirm 91/91 (the environment-healthy baseline).
6. Drive the step via `/openspec-opsx-auto auto-decompose <description>` (or the
   per-stage worker pattern in §3). Commit + push after review is clean.
7. Update `neo-deferred-items.md` (and this file's §2/§5) if the step resolves or
   surfaces an item.
