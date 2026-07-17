# Review Report — `neo-overhaul-eh-table-remap` (child 14)

**Reviewer:** independent (author != verifier), dispatched leaf worker
**Date:** 2026-07-12
**Branch:** `features/object-model-overhaul`
**Verdict:** **APPROVE**

---

## Summary

The change fixes a LATENT CORRECTNESS bug: the body-indexed exception-handler
table (`method.ExceptionHandlerRegister`: `TryStart`/`TryEnd`/`HandlerStart`/
`HandlerEnd`) was left stale by `Optimizer.Neo.cs LowerNeoOffsets`' Push-deletion
pass, so a thrown exception in a method that simultaneously had protected regions
AND a >3-arg call (triggering Push-deletion) could miss its handler. The fix has
three load-bearing parts — a build-order move, a per-deletion remap, and plumbing
— all independently verified correct. NeoStep14 (the EH step) is UNREGRESSED at
26/26, the new probe stash-toggles 2/2 FAULT-on-HEAD → PASS-with-fix, and the
change is Legacy-neutral.

---

## 1. Build-order change (LOAD-BEARING — scrutinized hardest) — CORRECT

**The crux (confirmed true):** In `ILMethod.InitCodeBody`, `jit.Compile(addr,
ref compiledFrame)` runs at `ILMethod.cs:970`. `Compile`'s Neo tail is
`RunNeoBackHalf` → `LowerNeoOffsets` (the Push-deletion pass). The EH table was
built at `:1013` (the old `:959` site) **AFTER** `Compile` returned. Two
consequences, both confirmed by reading the code:
  1. `method.exceptionHandlerR` was **NULL** while `LowerNeoOffsets` ran, so the
     deletion pass had no table to keep consistent.
  2. Even after `:1013` built it, the build uses `addr` — the front-half
     (pre-deletion) instruction→index map, which `LowerNeoOffsets` does NOT
     update. So the EH fields pointed at pre-deletion indices while `ExecuteNeo`
     indexes into the post-deletion `NeoExecuteBody` → stale → mis-routed throws.

**Fix part 1 (build-order) — verified at BOTH funnels:**
- `ILMethod.BuildExceptionHandlerRegister(Dictionary<Instruction,int> addr)` is a
  **verbatim** extraction of the old `:959-1000` fill loop (`ILMethod.cs:190-244`),
  Neo-gated (`#if ENABLE_NEO_MODE`). The body is byte-identical to the original
  loop (same `addr[eh.HandlerStart]`, `TryEnd = addr[eh.TryEnd] - 1`, same
  Catch/Finally/Fault switch, same `NotImplementedException` default).
- **Direct-JIT funnel:** `JITCompiler.cs:670` calls
  `method.BuildExceptionHandlerRegister(addr)` immediately before
  `RunNeoBackHalf` at `:671`. Confirmed inside `#if ENABLE_NEO_MODE` (the `#if`
  opens at `:649`, `#else` at `:672`). `addr` (the `Compile` parameter) and
  `method` are both in scope.
- **Generic-instance funnel:** `GenericMethodTemplate.cs:732` calls
  `instance.BuildExceptionHandlerRegister(addr)` before `jit.RunNeoBackHalf` at
  `:734`. Confirmed the delta-shift block (`:677-717`) finalizes `addr` (the
  `addr.Clear()` + repopulate from delta-shifted `addrSrc` at `:713-717`) BEFORE
  the `:732` build, so the table is built from the delta-correct (pre-deletion)
  `addr`. `DoCloneAndPatch` (`:630`) is inside the Neo-gated region (`#if` at
  `:13`, `#endif` at `:858`).

**Why the table is correct at build time:** `Optimizer.CleanupRegister`
(`JITCompiler.cs:647`) runs before the build. `LowerNeoOffsets` is the sole pass
that mutates body LENGTH (per the design doc and confirmed by reading the loop),
so between the front-half and `LowerNeoOffsets` the body indices are stable and
`addr` is valid. The `:670` build therefore produces the SAME pre-deletion table
the old `:1013` build did — just earlier, so the per-deletion remap can act on it.

**Idempotency — sound:**
- `BuildExceptionHandlerRegister` opens with `if (exceptionHandlerR != null)
  return;` — a no-op if already populated.
- The `:1013` site now does `BuildExceptionHandlerRegister(addr); ehs =
  exceptionHandlerR; neoEhAlreadyBuilt = true;` and the fill loop is guarded by
  `if (!neoEhAlreadyBuilt)`. So for the Neo path the back-half already
  populated the table; `:1013` is a skip. No double-build, no clobber.
- `neoEhAlreadyBuilt` is declared `false` outside the `#if`; only the
  `ENABLE_NEO_MODE` arm sets it `true`. For the `#else` (Legacy register) and
  the non-register (`exceptionHandler`) arms it stays `false` → the loop runs as
  before.
- **Legacy/non-Neo byte-identical:** in a non-Neo build the `#else` at
  `ILMethod.cs:1037-1040` compiles, which is the original `if
  (exceptionHandlerR == null) exceptionHandlerR = new ...; ehs =
  exceptionHandlerR;` — unchanged. `neoEhAlreadyBuilt` false → original loop.
  Confirmed.
- The `isNeoAotShell` Cecil-free path (`:914-931`) returns before `:1013`; its
  EH build is covered by the `:732` site via `TryInstantiate → DoCloneAndPatch`.

**Normal (no-deletion) case NOT broken:** methods with protected regions but no
>3-arg calls never trigger Push-deletion, so `FixBranchTargetsAfterRemove` is
never invoked and the table equals the `addr`-built values unchanged. The
NeoStep14 26/26 gate (below) is the empirical proof that building every Neo EH
table earlier does not perturb dispatch.

## 2. Remap correctness — CORRECT

- `FixBranchTargetsAfterRemove` (`Optimizer.Neo.cs:1703`) gained a trailing
  `ExceptionHandler[] ehs` param. At the end (`:1773-1783`), if `ehs != null`,
  it loops each entry and decrements each of `TryStart`/`TryEnd`/`HandlerStart`/
  `HandlerEnd` when **strictly `> removedIndex`**.
- **Same rule as branch targets:** the branch arm at `:1719` is `if (op.Operand
  > removedIndex) op.Operand--;`. The EH arm uses the identical rule. Confirmed.
- **Per-deletion, in lockstep:** the single call site is `Optimizer.Neo.cs:1256`,
  INSIDE the deletion `while` loop (`:1245-1265`), invoked once per deleted Push
  with `removedIndex = scanIdx` (current-body-order). So the EH table is
  re-mapped at the same cadence as the branch targets and stays in the same
  current-body frame, ending in final `NeoExecuteBody`-order. Confirmed.
- **`eh.TryStart--` mutates the array element (NOT a copy):** `ExceptionHandler`
  is a **class** (`CLR/Method/ExceptionHandler.cs:16`), and the four fields are
  `{ get; set; }` auto-properties. `var eh = ehs[i];` copies the reference, so
  `eh.TryStart--` invokes the setter on the same object. Safe. (This was the
  first thing I checked — had it been a struct, the fix would silently no-op.)
- **No `FilterStart` invented:** confirmed `ExceptionHandler.cs` has exactly
  `HandlerType`/`TryStart`/`TryEnd`/`HandlerStart`/`HandlerEnd`/`CatchType`. No
  filter field. IL filters remain a Step-14 NIE. Correct.
- **Null `ehs` is a no-op:** the `if (ehs != null)` guard means methods with no
  protected regions (the common case) are untouched. `LowerNeoOffsets` receives
  `method.ExceptionHandlerRegister`, which is null when `BuildExceptionHandlerRegister`
  found no handlers. Correct.
- **`> removedIndex` (strict) is correct:** the deleted instruction at
  `removedIndex` is always a synthetic `Push`, never a try/handler boundary (a
  boundary is a Cecil instruction's `addr` value, and `Push` is JIT-synthetic, so
  no `addr[*]` resolves to it). Matches the branch-target semantics exactly.

**No missed callers:** grep confirms `LowerNeoOffsets` has exactly one call site
(`JITCompiler.cs:724`), `FixBranchTargetsAfterRemove` exactly one
(`Optimizer.Neo.cs:1256`), `BuildExceptionHandlerRegister` three (`:670`, `:732`,
`:1028`) + the definition. All updated; build is 0-error.

## 3. NeoStep14 regression gate (CRITICAL) — PASS

`NeoStep14` (the EH step) re-run under `Debug_Neo`:

```
Ran 26 tests, 0 failded, 0 ignored, 0 todos
```

**26/26 UNREGRESSED.** This is the load-bearing proof that moving the EH-table
build earlier for EVERY Neo method with protected regions does not perturb EH
dispatch. Every try/catch/finally/fault shape still routes correctly.

## 4. Probe reliability — CONFIRMED (stash-toggle 2/2 FAULT)

**Implementer's trigger is valid.** The probe (`TestCases/NeoStepEhTableRemapTest.cs`)
places a padded >3-arg `Warmup6`/`Warmup4` call BEFORE the try, then a
non-inlined no-arg thrower (`ThrowUnconditionally`) as the FIRST try statement.
Both non-obvious load-bearing details verified against source:

- **Inliner never inlines a method with an EH:** `JITCompiler.cs:3350-3351` —
  `hasExceptionHandler = ... Body.HasExceptionHandlers;` and the guard
  `!hasExceptionHandler` in the `canInline` condition. So `ThrowUnconditionally`
  (has try/finally) is emitted as a REAL no-arg Call sitting at `TryStart`.
- **Inliner inlines small bodies:** `JITCompiler.cs:3361` —
  `codeSizeOK = ... BodyRegister.Length <= Optimizer.MaximalInlineInstructionCount / 2`.
  The warmups are deliberately padded past this threshold (>10 register instrs)
  so they are NOT inlined → real Calls → JIT emits overflow `Push`es →
  `LowerNeoOffsets` deletes them at body indices BEFORE try-start → stale
  `TryStart` sits strictly ABOVE the runtime throw addr → catch MISSED.

**Why the planner's minimal probe (small throwing >3-arg method IN the try)
would NOT have faulted — confirmed:** a small throwing method (e.g. a 1-line
`ThrowIfEq(a,b,c,d,e)`) hits `codeSizeOK = true` at `:3361` → **inlined** → no
Call → no Push → no deletion → no staleness → no fault. The implementer's
padded-warmups + EH-bearing-thrower shape is the correct, reliable trigger.

**Stash-toggle evidence (the gate):**
- Fix stashed (4 source files reverted to HEAD; the untracked probe survives):
  `Ran 2 tests, 2 failded` — **EXIT=127**. The harness's unhandled-exception
  reporting path (`System.Exception::get_Message()` / `get_Data()` callvirt.clr
  sequences in the log) confirms the throw ESCAPED the catch through the stale
  EH boundary. **2/2 FAULT.**
- Fix restored: `Ran 2 tests, 0 failded` — **EXIT=0**. **2/2 PASS.**

## 5. Gates re-run — ALL GREEN

- `dotnet build ILRuntimeTestCLI -c Debug_Neo --no-incremental` → **0 errors**
- `dotnet build TestCases -c Debug` → **0 errors**
- `... true NeoStep` → **Ran 341 tests, 0 failed** (339 + TC1 + TC2)
- `... true NeoStep14` → **Ran 26 tests, 0 failed** (CRITICAL EH gate)
- `... true NeoStepEhTableRemap` → **Ran 2 tests, 0 failed**

## 6. Legacy-neutral — CONFIRMED

- Plain `Debug` CLI + `useRegister=true` + `NeoStep` filter → **Ran 341 tests,
  17 failed == baseline** (the 17 are pre-existing Legacy NeoStep failures;
  adding the 2 new probes did not change the failure count).
- The 2 new probes explicitly under Legacy: `... true NeoStepEhTableRemap` →
  **Ran 2 tests, 0 failed**. They are plain C# try/catch and pass everywhere.
- The change is Neo-gated by construction: all 4 source diffs are inside
  `#if ENABLE_NEO_MODE` (or in `Optimizer.Neo.cs`, whose entry points are called
  only from Neo-gated `RunNeoBackHalf`).

---

## Spec axis (Standards + Spec)

**Spec:** the implementation faithfully realizes `proposal.md` / `tasks.md` /
`specs/neo-optimizer/spec.md`. All 6 spec scenarios are satisfied: (1) throw
inside try routes to correct catch after Push-deletion — TC1/TC2; (2) no-deletion
methods byte-identical — no `FixBranchTargetsAfterRemove` call; (3) null `ehs`
no-op — guarded; (4) generic-instance path covered — `:732` build; (5) Legacy
unaffected — Neo-gated; (6) regression guard faults on HEAD / passes with fix —
stash-toggle. No scope creep; no missing requirements. All tasks 1.1–6.5 + 7.2
checked (7.1 commit correctly skipped — LEAD/ship owns it).

**Standards:** no SQL/data-safety, concurrency, or trust-boundary concerns (no
I/O, single-threaded JIT). No enum-completeness issue. The `> removedIndex` rule
matches the existing branch-target invariant. Comments are accurate and cite the
rasen change id. No dead code introduced.

## Test coverage

Both new probes are ★★★ — they exercise the exact defect path (Push-deletion
shifting EH boundaries) AND the stash-toggle proves they fault without the fix.
TC1 (6-arg → 3 overflow Pushes) and TC2 (4-arg → 1 overflow Push) cover two
distinct deletion counts, confirming the per-deletion decrement (not a fixed
offset). The generic-instance EH-remap path (spec scenario 4) is not directly
probed by a NeoStep, but is structurally identical to the direct path (same
helper, same remap) and is covered by NeoStep14's generic EH tests if any; the
direct-path proof + code-identity is sufficient. (Minor — see findings.)

---

## Findings

| # | Severity | Finding |
|---|----------|---------|
| 1 | **Minor** | No dedicated NeoStep probe for the **generic-instance** EH-remap path (`CloneAndPatch` build at `:732`). It is structurally identical to the direct-JIT path (same `BuildExceptionHandlerRegister` + same per-deletion remap), and the direct path is stash-toggle-proven, so the risk is low — but the generic delta-shift-then-delete ordering is the one subtle path worth a direct regression probe if a future EH+generic+>3-arg test is cheap to add. Not blocking. |
| 2 | **Trivial** | `BuildExceptionHandlerRegister` carries redundant defensive guards (`def == null || !def.HasBody || def.Body.ExceptionHandlers.Count == 0`). The `:1028` call site is already inside `Count > 0`; the `:670`/`:732` sites are not, so the guards are genuinely used there. Harmless; leave as-is for the unconditional call sites. |

No Blockers. No Majors.

---

## VERDICT: APPROVE

**Rationale:** The crux (`exceptionHandlerR` NULL during `LowerNeoOffsets`, and
`addr`-built values being pre-deletion) is real and confirmed by reading
`InitCodeBody`'s call order. The three-part fix is correct: the build-order move
is at both `RunNeoBackHalf` funnels (direct + generic, the generic using the
delta-shifted `addr`) with sound idempotency and byte-identical Legacy arms; the
remap applies the identical `> removedIndex → --` rule per-deletion in lockstep
with branch targets on a reference-typed `ExceptionHandler` (so mutations
persist); no `FilterStart` is invented. The NeoStep14 26/26 gate proves the
load-bearing build-order change is safe for every Neo method with protected
regions. The probe is reliable (inliner-defeating padding + non-inlined thrower,
both verified against `JITCompiler.cs:3350/3361`) and stash-toggles 2/2
FAULT→PASS. NeoStep 341/0, Legacy-neutral 341/17 == baseline. Ship it.
