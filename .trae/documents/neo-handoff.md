# Neo Overhaul — Handoff Document

> **Read this FIRST when continuing the ILRuntime Neo work.**
> Companion to `neo-implementation-steps.md` (the 26-step roadmap) and
> `neo-deferred-items.md` (the deferred-items resolution map).
> Last updated: 2026-07-08. Branch: `features/object-model-overhaul`. HEAD `de0ef01c`. **Step 25 S3-2 (Cecil-free load) SHIPPED** -- a `.neo` loads + executes in a FRESH Cecil-free `new AppDomain()` (no Mono.Cecil at load). The 3 inseparable parts shipped together: (a) `.neo` Version 2 with NAME-based `NeoTokenBinding[]` tables (APPROACH-1 cross-AppDomain token re-resolution -- records every baked identity hash, incl. Cecil TypeRef/MethodRef identity hashes which are ALSO process-global-counter-based); (b) `ILType.CreateFromNeoRecord` + `ILMethod.CreateFromNeoShell` Cecil-free factories (the S3-partial rebuild INSTALLED); (c) `AppDomain.LoadNeoAssembly` (two-pass build + hash re-registration + interface-method shell synthesis). Capstone `NeoStep25CecilFreeLoad` 5/5 (Cecil-free load+exec of `NeoStep25S3Probe.Compute` [field read + virtual + interface dispatch] + M1 body-mutation + M2 layout-mutation, adversarially re-proven load-bearing). NeoStep 223/0/0; NeoStep25LoadExec 28/28 unchanged; Legacy-neutral. Open work = the S3-2 sequenced follow-ons (sub-surface 4 `.cctor` seeding + CLR base/interface on the Cecil-free path + generic instances on the Cecil-free path) + the non-S3 deferred items in `neo-deferred-items.md` (async Phase-2 MoveNext fix is the highest-value non-S3 item).
> Authoritative current state lives in this file + `neo-deferred-items.md` +
> the `openspec/specs/` capability specs + `openspec/changes/archive/`, and the
> portfolio run state in
> `openspec/changes/neo-completion-portfolio/{planning-context.md,portfolio-run.json}`.

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
  - **Neo** (`Debug_Neo` + `useRegister=true`, filter `NeoStep`): **190/190 green**
    as of HEAD `0aafdb34` (2026-07-06), after the 15-child portfolio run. Related
    filters: **NeoOptHardening 24/24**, **NeoStep20 9/9**. (Full Neo run still has
    failures — all unimplemented ops, e.g. rank-2+ arrays, truly-async suspend,
    AOT; only the `NeoStep` smoke matters day-to-day.)
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

## 2. Current state (as of 2026-07-06, HEAD `0aafdb34`)

### Completed Neo steps (JIT path)
Steps **1-20** of the 26-step roadmap, plus all derived follow-ups and the
**15-child completion-portfolio run** (2026-07-05 → 2026-07-06). Foundation
Steps 1-18 + the early derived steps landed in prior sessions (1-10 macros /
object model / static fields / Neo frame / arithmetic / managed stack / CLR
interop / call / VTable+callvirt; **11** interface dispatch · **12** in-frame
value types + inline field access · **12b** `Move_Vt`/`LowerMove` · **13**
Box/Unbox · **14** exception handling · **15** isinst/castclass · **16** rank-1
arrays · **17** ref/out + Ref Slot + ldelema · **18** CLR newobj; derived
**[OPT-HARDEN]** K1, **13b** unified CLRMethod param layout, **[CATCH-COMPLETE]**
CheckExceptionType IL branch).

The **portfolio run** then delivered these 15 children (in execution order):
1. **neo-vt-this-addr** [Q-VT-NEWOBJ] — IL value-type `newobj` + `call VT ctor`
   via ldloca; D2 in-frame VT-this/newobj-dest field-access lowering (smoke 91→99).
2. **neo-opt-harden-2** [F-MAJ-1] — `AllocateLocalStackSpaces` declares a CLR-VT
   LOCAL as flat bytes (representation-sizing fix; 99→100, +NeoOptHard 16/16).
3. **neo-il-exception-throw** [D-IL-EXCEPTION-THROW] — built-in `ExceptionAdaptor`
   + `Throw` IL-instance unwrap on BOTH engines (100→108; D-CHECKEX fully reachable).
4. **neo-step13-area4** [D-13B 4a+4b] — CLR binding value-type-`this` direct-call
   + `Unsafe.Unbox<T>` boxed direct-call (closes F-3 direct-`call`) (108→117).
5. **neo-step17-completion** [D-CONSTRAINED a/d/M2] — `constrained.`-on-VT full
   dispatch (runtime Constrained arm owns dispatch) + CLR-array ldelema + F-5
   NIE-guard (117→130).
6. **neo-step19-delegate** [Step 19] — `ldftn`/`ldvirtftn` + delegate newobj +
   `InvokeILMethod` Neo (fresh pooled interpreter) + multicast + Combine/Remove
   redirects (130→140).
7. **neo-k2fam-bridge** [K2-FAM] — TEST-ONLY (subsumed by opt-harden-2 + review-fix
   + 13b); 6 regression guards (140→146).
8. **neo-vt-ldflda-inline** [F-6] — `ldflda` on in-frame VT (marker stamp +
   3-way runtime dispatch) (146→154).
9. **neo-opportunistic-cleanup** [N-CGTUN + N-TC2] — Cgt_Un comment +
   Step 14 TC2 tighten (154).
10. **neo-array-completion** [D-ARR rank-1] — `Stelem_I`/`Ldelem_I` + F-4
    width matrix for CLR-array Stind/Ldind (154→161).
11. **neo-double-combine-quirk** [F-8 / OPT-HARDEN-3] — dead `Operand3` write
    clobbers 8-byte immediate via the `OpCodeR` union (161, +NeoOptHard 24/24).
12. **neo-step13-area4-refandstind** [D-13B 4c+4d] — CLR-method ref/out typed-ref
    bridge + CLR-object stind/ldind/stobj/ldobj via field identity (161→175).
13. **neo-step17-stobj-refloop** [D-CONSTRAINED b] — Stobj/Ldobj ref-region copy +
    IL-VT-with-ref-fields constrained (175→181).
14. **neo-step20-async** [Step 20 sync slice] — builder redirects + Start→MoveNext
    + awaiter/Task overrides (**PARTIAL**: sync `Task<int>`/void/exception green;
    rest were blocked on F-10) (181→186).
15. **neo-clrstruct-field-of-il** [F-10] — IL-instance CLR-struct field `ldflda`
    offset (all-three-arms Stfld_Ref/Ldfld_Ref/Ldflda); unblocked Step 20 sync
    void+exception (186→190).

### Test smoke progression
Neo `NeoStep`: 37 (after 12) → 41 → 49 → 58 → 65 → 72 → 81 → 84 → **91** (end of
prior sessions) → 99 → 100 → 108 → 117 → 130 → 140 → 146 → 154 → 161 → 175 → 181
→ 186 → **190** → **198** → **204** → **205** → **210** (S1, 2026-07-07) → **215** (S2) → **218** (post-B1 + S3 partial, 2026-07-08). **NeoOptHardening 24/24.** **NeoStep20 9/9.** AOT self-checks: **NeoStep22SelfCheck 55/55, NeoStep23Roundtrip 15/15, NeoStep24CliRoundtrip 5/5, NeoStep25LoadExec 28/28** (21 S1/S2 + 7 S3: layout/VTable/interface structural-equiv + field-offset + VTable-slot-swap mutation).
Legacy 519 baseline unaffected (still ~518/519; the regression reference).

### Capability specs (`openspec/specs/`)
10 total — the original 9 + **`neo-async`** [NEW from Step 20]:
`neo-dispatch`, `neo-value-types`, `neo-boxing`, `neo-exceptions`,
`neo-type-checks`, `neo-arrays`, `neo-byref`, `neo-optimizer`, `neo-newobj`,
`neo-async`. These are the durable canonical specs; per-child deltas are merged
in at archive time.

### Git
Branch `features/object-model-overhaul`, pushed to `origin` (GitHub,
`DumoeDss/ILRuntime.git`). HEAD `0aafdb34`. The 15 portfolio children are each
their own commit + archived change under
`openspec/changes/archive/2026-07-0X-neo-*`.

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

### Earned lessons reaffirmed by the 15-child portfolio run (2026-07-05 → 07-06)
- **Probe before fixing — the JIT/runtime dump is the arbiter, not the propose-time
  hypothesis.** K1 / Q-NEWOBJ / Q-STRUCT / K2-FAM were all subsumed or
  mis-attributed; the dump-gate (Block-0 reproducer) is what separates a real root
  cause from a plausible one. Q-VT-NEWOBJ's propose-time hypothesis was only
  PARTLY right — the dump found the load-bearing bug (inline-stfld owner-type
  clobber) the planner missed.
- **The propose-time blast-radius sweep is NOT authoritative.** F-10's design
  premise ("only `ldflda` broken; `Stfld_Ref`/`Ldfld_Ref` already correct") was
  DISPROVEN by the Block-0 dump — all THREE heap field-access arms were broken.
  Trust the reproducer dump over the design's "this path is safe" claim.
- **A green smoke does NOT prove an optimizer/lowering gate.** F-1 / F-8 / F-10
  were all silent-corruption or OOB that the smoke passed (TC1/TC7 passed by a
  layout accident). Construct the adversarial probe; stash-toggle FAIL-on-HEAD
  is the proof.
- **Don't ship a fix that doesn't work — STOP.** The F-10-R1 runtime reorder was
  DISPROVEN by the fixer (regressed 6 F-6-only probes, 190→184); the Step 20 sync
  slice was STOPPED at F-10 (the stacked-pre-existing-edges case) and split into
  its own child. A green smoke is not worth a silent-wrong-result fix.

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

Most items surfaced during Steps 11-20 are now **resolved** (the 15-child
portfolio run closed the bulk). The full per-item detail + resolution evidence is
in `neo-deferred-items.md` (§2 master table, §3 per-item, §4 resolved) — that file
is the source of truth; this section is a quick orientation.

### Resolved by the portfolio run (don't re-litigate)
- **K1** (FCP VT-move mis-propagation) → [OPT-HARDEN] `ldloca-kill`.
- **K2** (Step 8 VT-by-value param copy) → Step 13b.
- **K2-FAM** (boxed-ref CLR-VT-local bridge) → `neo-k2fam-bridge` (TEST-ONLY;
  subsumed by opt-harden-2 + review-fix + 13b).
- **Q-VT-NEWOBJ / [VT-THIS-ADDR]** (IL value-type `newobj`) → `neo-vt-this-addr`.
- **Q-NEWOBJ / Q-STRUCT / Q-LONG** — non-reproducible on HEAD (closed).
- **F-MAJ-1** (CLR-VT local representation) → `neo-opt-harden-2`.
- **F-3 / NEO-BYREF-THIS** (CLR struct instance method byref-`this`) — direct-`call`
  shape → `neo-step13-area4`; `callvirt`/`constrained.callvirt` → `neo-step17-completion`.
- **D-CHECKEX** (CheckExceptionType NIE) → [CATCH-COMPLETE] + `neo-il-exception-throw`.
- **D-IL-EXCEPTION-THROW** (end-to-end IL exception catch) → `neo-il-exception-throw`.
- **D-CONSTRAINED** (a/b/d + M2) → `neo-step17-completion` ({a,d,M2}) +
  `neo-step17-stobj-refloop` (b). **(c) edges remain** (see Open below).
- **D-LDELEMA** (fully) — Step 17 IL-VT-array path + `neo-step17-completion` CLR-array
  remainder + `neo-array-completion` F-4 width matrix.
- **F-5 / NEO-CALLARG-BOXED-SRC** → `neo-step17-completion` (NIE-guard; branch
  unreachable).
- **F-6 / NEO-VT-FLDADDR** (ldflda on in-frame VT) → `neo-vt-ldflda-inline`.
- **D-13B** (fully, Areas 4a/4b/4c/4d + Area 5) → Step 13b + `neo-step13-area4` +
  `neo-step13-area4-refandstind`.
- **N-CGTUN** + **N-TC2** → `neo-opportunistic-cleanup`.
- **D-ARR** (rank-1) → `neo-array-completion`. **Multi-dim remains** (see Open).
- **F-8 / NEO-DOUBLE-COMBINE** → `neo-double-combine-quirk` (OpCodeR-union
  `Operand3` high-4-byte clobber — the 3rd concrete instance of the §4 gotcha).
- **F-10 / NEO-CLRSTRUCT-FIELD-OF-IL** → `neo-clrstruct-field-of-il`.

### Open follow-ups (the remaining portfolio children + accepted-known edges)
- **AOT toolchain (Steps 22-26)** — the next big focus; see §6.
- **neo-step20-async-suspend** — **PARTIAL 2026-07-06**: shipped Phase 1
  reachability unblockers (B3 `Nop` case, B2 non-generic-awaiter `void-GetResult`
  guard, `Task.Delay` redirect; Neo 204/204, NeoStep20 sync 9/9). Phase 2 (the
  suspend machinery) STOPPED at 2 stacked pre-existing blockers (a false-positive
  probe — green via blocking `GetResult` on an incomplete Task — was caught +
  removed; F-10/K1 discipline). Deferred to 2 split children:
  **`neo-async-controlflow-iscompleted`** (the `brtrue`-after-`get_IsCompleted`
  register mismatch — `get_IsCompleted` writes `DstOffset` but `brtrue.s` reads
  `SrcOffset` → always takes the completion path → `AwaitUnsafeOnCompleted` never
  called; masks B1; **next, highest value**) + **`neo-generic-redirect-resolution`**
  (B1: the 2-generic-arg redirect resolves only on the Legacy map, not
  `RedirectMapNeo`; broad shared dispatch; re-dump-gate AFTER the control-flow
  child). The sync-slice infrastructure + `HoistNeoILValueToHeap` + `ILAsyncContext`
  skeleton are the shipped foundation.
- **Step 20 redirect-coverage edges (TC2/TC3/TC5)** — non-generic Task `Start`
  redirect null-SM; ValueTask builder NRE; multi-await `Task<int>.get_Result`
  redirect. Fold into a `neo-step20-async` round 2 / async-suspend.
- **neo-array-multidim** — multi-dimensional arrays (rank-2+). **RESOLVED 2026-07-06**
  (autogen-binder path already worked; closed 3 reflection-fallback defects in
  `CLRMethod.cs`/`ILIntepreter.Neo.cs`: null-ref-return encoding, null-`this` NRE
  guard, shared `TargetInvocationException` unwrap at 6 sites — Legacy-neutral).
  Neo 198/198. IL VT-element `[,]` stays a Non-Goal.
- **neo-step17-generic-byref-etc** — generic-byref (`ref T`/`out T` with `T`
  generic), `fixed` unmanaged-pinning, interface-on-VT-constrained (the Step 17 (c)
  edges) + **F-10-R1**. **RESOLVED 2026-07-06**: per-sub-item dump-gate found 3/4
  sub-items are NO-OPs (generic-byref is type-agnostic; interface-on-VT-constrained
  already covered by the {a,d,M2,b} cohorts; `fixed` is blocked by the unimplemented
  `Conv_U`/`Conv_I` opcodes — rerouted to a future pointer step, NOT a byref gap).
  The 1 real gap = **F-10-R1**: the JIT-discriminator gate
  (`TypeSpecializeNeoOpcodes case Ldflda:` clears the F-10 marker when F-6 stamps,
  `JITCompiler.cs:913`; keys on the operand value-category, NOT `!IsValueType`;
  runtime arm unchanged — the runtime reorder was disproven). Neo 204/204,
  Legacy-neutral. Forward-looking audit note added (re-audit the gate when boxed-IL-VT
  interface-callvirt lands — see F-10-R1 §3 in `neo-deferred-items.md`).
- **peephole-isinst [D-PEEP]** — `box T; isinst U` fusion + `PatchKind.IsinstResult`
  (needs a patch-infra that doesn't exist yet).
- **Smaller accepted-known / latent upstream gaps:** **F-4** (Stind/Ldind CLR-array
  I4-only is mostly resolved; UIntPtr-primitive + ref-array upstream gaps remain
  unreachable), **F-9** (inlined-IL-method return-move mis-classification),
  **F-7** (delegate ref/out marshaling in `DelegateAdapter.NeoInvokeSub` — CLR→IL
  direction, distinct from the 4c IL→CLR bridge), **F-2** (inliner ref-only-VT
  ref-fold), the cross-frame byref parameter limitation, and the nested-VT-field-byref
  upstream gap.

---

## 6. What's next on the roadmap

The Neo JIT path now covers **Steps 1-20** (sync) + all derived follow-ups. The
remaining work falls into three buckets:

### A. AOT toolchain (Steps 22-26) — the next BIG focus
A separate multi-week sub-project; **pure optimization layer, no functional
impact** (the JIT path already runs everything the smoke covers). The portfolio
has these as pending children with a dependency chain:
- **Step 22** `neo-step22-generic-template` — `PatchEntry` + templateBody+patches
  + `CloneAndPatch` runtime instantiation. **DONE 2026-07-07**: the generic-method
  template mechanism shipped (in-memory; additive + Neo-only + Legacy-neutral).
  `RunNeoBackHalf` factored out of `Compile` (shared, behavior-preserving);
  `PatchEntry` captures T-identity token sites (incl. `Constrained` type-token +
  T-qualified method-token, captured from the CIL body); `CloneAndPatch` rebuilds
  the Initobj prefix + shifts branches/Leave+Leave_S/addr + re-runs the back-half.
  V1 structural-equivalence self-check **55/55** (11 methods × 5 T); Neo 205/205,
  NeoOptHard 24/24, NeoStep20 9/9, Legacy-neutral. Review-loop round 1 caught +
  fixed a Blocker (the `Constrained` token wasn't patched -- green smoke hid it).
  **Next: Step 23** (`.neo` serializer). Follow-up: struct-T × inliner
  CloneAndPatch gap (the serializer must not assume the Initobj prefix is the only
  Initobj site).
- **Step 23** `neo-step23-neoassembly` — `.neo` binary format (header + tables) +
  serializer/deserializer + roundtrip. **DONE 2026-07-07**: the `.neo` serialization
  layer shipped under a new `ILRuntime/Runtime/NeoAOT/` namespace (Neo-only, additive,
  Legacy-neutral). `NeoAssemblyWriter`/`Reader` serialize OpCodeR[] raw-24-byte LE
  (via MemoryMarshal, captures all union aliases), CompiledFrame load-bearing fields
  (EH as body-indices), type metadata, + faithful GenericMethodTemplate (every
  Initobj site, not just the prefix). REUSES HybridPatch's reference tables. V1
  roundtrip self-check **15/15** (matrix incl. multi-Constrained-pair, nested EH,
  real-aqname byref, multi-interface). Neo 205/205, NeoOptHard 24/24, NeoStep20 9/9,
  NeoStep22SelfCheck 55/55, Legacy-neutral. **Next: Step 25** (runtime loader).
  Step-25 deferrals: cross-AppDomain token re-resolution, CLR aqname indexing,
  static .cctor seeding, V2 functional deserialize->ExecuteNeo.
- **Step 24** `neo-step24-ilrt-neoc` — `ilrt_neoc` standalone precompile CLI. **DONE
  2026-07-07**: the CLI integration layer shipped. A NEW `public NeoCompiler` driver
  (`ILRuntime/Runtime/NeoAOT/NeoCompiler.cs`, `#if ENABLE_NEO_MODE`) is the single
  in-assembly seam that bulk-compiles an assembly: enumerates every IL type/method
  in the input module, partitions non-generic methods (-> MethodDefTable via
  `NeoAssemblyWriter.Write`) from generic-method definitions (-> TemplateTable via a
  SYNTHESIZED capture-eligible `int`-per-param instantiation -- no call site needed),
  force-compiles each non-generic method in a per-method try/catch (skip-with-warning
  + exit codes 0/2/1), and calls the UNCHANGED Step-23 `NeoAssemblyWriter.Write`. A
  NEW `ILRuntimeNeoCompiler/` console-app project (mirror `PatchTool.csproj`, Neo-only,
  builds STANDALONE via `dotnet build ILRuntimeNeoCompiler/ILRuntimeNeoCompiler.csproj
  -c Debug_Neo`, NOT in the sln) is a thin CLI wrapper. V1 CLI-roundtrip self-check
  **5/5** (`NeoStep24CliRoundtripCheck`: header + counts+split + model1==model2 via
  Step-23 comparators + independent fresh-body check). Neo 205/205, NeoStep23Roundtrip
  15/15, NeoStep22SelfCheck 55/55, Legacy-neutral. Minimal additive accessor: 3 Step-23
  comparators (`MethodDefsEqual`/`TemplatesEqual`/`TypeDefsEqual`) widened private ->
  internal for reuse. **V1 boundary (review round-0 Major-1/2, corrected):** the
  full-`TestCases.dll` CLI run FAILS with a CLR-type-resolution fatal
  (`Cannot find Type:ILRuntimeTest.TestFramework.TestCLREnum` -- a CLR enum in
  `ILRuntimeTestBase`, a host CLR assembly the standalone CLI never registers; NOT
  a Step-19 JIT stall as an earlier note mis-stated). This is design D7's "V1 =
  BCL-only ref assemblies" boundary -> the skip-with-warning/exit-2 contract does
  NOT engage for non-BCL CLR refs (serialize aborts -> exit 1). V1-A proves
  `CompileCore` (enumeration + partition + template synthesis + serialize) via the
  explicit-types overload; V1-B covers the CLI happy path (the sample has only BCL
  refs) -- the CLI-specific Cecil-resolver / `InitializeFromModule` / ref
  `LoadAssembly` / module-filter-exclusion / `Dispose` paths are V1-A-UNVERIFIED
  (Step-25 hardening: robust IL-vs-CLR ref classification + the module-filter's
  exclusion correctness). Step-25 deferrals: the runtime `.neo` LOADER + V2
  functional deserialize->ExecuteNeo + the ref-classification + module-filter
  hardening + registering host CLR assemblies (TestCLREnum etc.) for a full
  `TestCases.dll` compile.
- **Step 25** `neo-step25-runtime-loader` — `.neo` runtime loader + ILType/ILMethod
  Cecil-decoupling dual-path. **PARTIAL (S1) 2026-07-07**: the non-generic load+execute
  shipped + V2-PROVEN. KEY: Cecil-coupling is ZERO at execution (ExecuteNeo reads only
  `CompiledFrame.NeoExecuteBody` + token hashes) -> Step 25 is init-only. ILMethod
  `isNeoAotBody` dual-path (`InitCodeBodyFromNeo` populates CompiledFrame from the .neo;
  BodyRegister short-circuits) + `NeoAssemblyLoader.Attach` (same-AppDomain, non-generic
  bind). V2 capstone **11/11** (`NeoStep25LoadExec`: deserialize + ExecuteNeo == JIT for
  a non-generic matrix, incl. a body-mutation guard proving the AOT body genuinely runs).
  Neo 210/210, all AOT gates green, Legacy-neutral (ILMethod SHARED, Neo-gated).
  **S2 (generic-instantiation-at-load) SHIPPED 2026-07-07**
  (`neo-step25-s2-generic-at-load`): the no-T-identity-token generic slice.
  `NeoAssemblyLoader.Attach` consumes `model.Templates` +
  `GenericMethodTemplateOps.BuildFromNeoRecord` (re-resolves `VariableTypes` from
  `VariableTypeRefIdxs`; rejects T-identity-token/MethodToken patches w/ non-none
  CecilTokenKind) + `ILMethod.InitTemplateFromNeo` (OVERWRITES the JIT-captured
  template); the Step-22 hook (UNCHANGED) routes generic instances through
  CloneAndPatch from the AOT template. V2 capstone **21/21** (4 functional T-kinds
  int/long/ref/struct + structural-equivalence incl. string-T via `BodiesEqual` +
  template body-mutation on a fresh ref-T instance + fresh-instance). Neo 215/215,
  NeoStep22 55/55 + NeoStep23 15/15 + NeoStep24 5/5 unchanged, Legacy-neutral.
  **S3 PARTIAL SHIPPED 2026-07-08** (`neo-step25-s3-full-decoupling`): sub-surface
  1 -- `ILType.RebuildFromNeoRecord` (reconstructs instance layout + Neo VTable +
  interface map from `NeoTypeDefRecord`; `naturalAlignment` re-derived from the
  resolved field types) + `NeoAssemblyLoader.ResolveVTableFromRecord` +
  `ILType.NeoVTableSlotKeysForAOT` accessor + `NeoStep25LoadExecCheck` S3 cells
  (layout/VTable/interface structural-equiv + field-offset + VTable-slot-swap
  mutation); capstone 28/28, NeoStep 218/0/1, NeoStep22/23/24 unchanged,
  Legacy-neutral. **DEFERRED sub-surfaces 2/3/4/5**: Cecil-free AppDomain load
  (sub-surface 2) + cross-AppDomain APPROACH-1 token-hash re-resolution under a
  `.neo` Version bump (sub-surface 3; Approaches 2/3 REJECTED) + `.cctor` seeding
  (sub-surface 4) + full CLR aqname / host-CLR-assembly registration (sub-surface
  5, the Step-24 `TestCLREnum` gap). The Step-6 Run-shim parameterless limitation
  (F-11 / F-12 in `neo-deferred-items.md`) does NOT block the S3 structural-
  equivalence slice (host-side comparison; no Run-shim invocation). See
  `neo-deferred-items.md` STEP-25-PARTIAL. **Next: Step 26** (perf) or the S3
  sub-surface 2/3/4/5 follow-up (the Cecil-free load + cross-AppDomain).
- **Step 26** `neo-step26-perf-validation` — benchmarks + reflection/thread-safety
  edges + debugger adaptation.

### B. The async finish (Step 20 remainder)
- **`neo-step20-async-suspend`** — the truly-async suspend/resume slice
  (`AwaitUnsafeOnCompleted` → frame-to-heap hoist + `ILAsyncContext` resumption).
  Infrastructure is shipped; F-10 (the prerequisite) is resolved.
- **Step 20 redirect-coverage edges (TC2/TC3/TC5)** — fold into a Step 20 round 2.

### C. Smaller completions (independent, pick up between A/B)
- **`neo-array-multidim`** — multi-dimensional arrays (rank-2+). **DONE 2026-07-06**
  (see §5).
- **`neo-step17-generic-byref-etc`** — generic-byref / `fixed` / interface-on-VT-
  constrained (Step 17 (c) edges) + the F-10-R1 JIT-discriminator gate. **DONE
  2026-07-06** (see §5).
- **`neo-peephole-isinst`** — `box T; isinst U` fusion (needs patch-infra).

### Workflow
The user drives via the **autopilot portfolio** (`/openspec-opsx-auto
auto-decompose ...`), **one child per run, commit + push after each clean child**
(see §3). The portfolio state (executionOrder, completedChildren, runnableFrontier,
each child's status/smoke/review) lives in
`openspec/changes/neo-completion-portfolio/portfolio-run.json`; the per-child
scope/findings/follow-ups live in the companion `planning-context.md`. **Pick the
next child from the runnable frontier** (deps satisfied) — typically the next AOT
step (Step 22), or async-suspend, or a smaller completion.

---

## 7. Where everything lives (orientation map)

- **`.trae/documents/`** (read before any Neo work — CLAUDE.md says so):
  - `neo-implementation-steps.md` — the 26-step roadmap + dependency graph.
  - `neo-deferred-items.md` — the deferred-items resolution map (authoritative
    current-state tracker; maintained as items resolve).
  - `neo-handoff.md` — THIS FILE.
  - `object-model-design.md` / `object-model-neo-design.md` — the object-model +
    Neo-interpreter design (Call convention, VTable, exception, Box, async, etc.).
- **`openspec/specs/<capability>/spec.md`** — the **10** durable canonical
  capability specs: `neo-dispatch`, `neo-value-types`, `neo-boxing`,
  `neo-exceptions`, `neo-type-checks`, `neo-arrays`, `neo-byref`, `neo-optimizer`,
  `neo-newobj`, **`neo-async`** (NEW from Step 20). Deltas merge in at archive time.
- **`openspec/changes/neo-completion-portfolio/`** — the **durable portfolio
  state**: `portfolio-run.json` (the 15 done + the pending children, the dependency
  DAG, executionOrder, runnableFrontier, each child's status/smoke/review) +
  `planning-context.md` (the full portfolio plan + every child's `## Findings`
  outcome + `## Follow-ups discovered` F-1..F-10). This is the portfolio
  source-of-truth; read it before picking the next child.
- **`openspec/changes/archive/`** — per-child history: the Steps 1-18 era is
  `2026-07-04-implement-neo-step*/` (and the derived `opt-hardening` /
  `catch-complete`); the **15 portfolio children** are `2026-07-0X-neo-*`
  (e.g. `2026-07-05-neo-vt-this-addr/`, `2026-07-06-neo-clrstruct-field-of-il/`).
  Each holds proposal.md / design.md / tasks.md / review-report.md / ship-log.md /
  planning-context.md — mine these for prior-art decisions and probe methodology.
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
2. Read this file + `neo-deferred-items.md` (the open follow-ups) + the portfolio
   docs (`openspec/changes/neo-completion-portfolio/{portfolio-run.json,
   planning-context.md}` — the runnableFrontier + each child's status).
3. Decide the next unit of work: an **AOT step** (22→…→26, the big focus),
   **`neo-step20-async-suspend`** (the async finish), or a smaller completion
   (`neo-array-multidim` / `neo-step17-generic-byref-etc` / `neo-peephole-isinst`).
4. Build the CLI (`dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`)
   + TestCases (`dotnet build TestCases/TestCases.csproj -c Debug`) — confirm 0 errors.
5. Run the `NeoStep` smoke — confirm **190/190** (NeoStep), **24/24** (NeoOptHard),
   **9/9** (NeoStep20) — the environment-healthy baseline.
6. Drive the child via `/openspec-opsx-auto auto-decompose <description>` (or the
   per-stage worker pattern in §3). **Commit + push after each clean child.**
7. Update `neo-deferred-items.md` (and this file's §2/§5) + the portfolio
   `portfolio-run.json` if the child resolves or surfaces an item.
