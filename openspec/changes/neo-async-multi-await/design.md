# Design: neo-async-multi-await

## Context

The truly-async suspend/resume path shipped in `neo-async-movenext-fix` for a
state machine with a SINGLE genuinely-incomplete await. The three pieces (Piece 1
the `get_IsCompleted` bool zero-extension; Piece 2 `AwaitUnsafeOnCompleted_Neo`
suspend; Piece 3 `ILAsyncContext<T>.MoveNext` resume + sink-swap + TCS bridge)
are PROVEN GREEN by `NeoStep20_TC8_TrulyAsyncSuspendResume` and
`NeoStep20_TC11_NestedSyncAsyncAfterSuspend`.

This change closes the multi-await gap that `neo-async-movenext-fix` review
Finding B explicitly routed here: an SM with >= 2 `await` expressions. Finding B
installed a TAGGED NIE in `GetAwaitedTaskFromSm` for the > 1-`Task`-field case
(converting a silent-wrong pick into a loud failure) and documented multi-await
as deferred to this child.

### The dump-gate finding (the dump is the arbiter)

A gated instruction-level tracer was run on the multi-await SM at HEAD
`311a6915`. The reproducer is a deterministic `TaskCompletionSource`-backed SM
with TWO genuinely-incomplete awaits (`NeoStep20_TwoIncompleteAwaitsProbe`:
`await GetIncompleteTask()` then `await GetIncompleteTask2()`, two distinct
incomplete Tasks at two distinct await points). HEAD FAILS exactly as predicted:

```
System.NotImplementedException: Neo async multi-Task awaiter not supported
  (single-Task shape only); the currently-awaited Task cannot be disambiguated.
  State machine hoists 2 Task fields.
  (neo-async-movenext-fix finding B -> deferred multi-await follow-up)
  at CLRRedirectionsAsyncNeo.GetAwaitedTaskFromSm(...)   line 595
  at CLRRedirectionsAsyncNeo.SuspendStateMachine(...)    line 523
  at CLRRedirectionsAsyncNeo.AwaitUnsafeOnCompleted_Neo  line 496
```

The await faults the returned `Task` instead of suspending. The probe is the
binding reproducer (deterministic, NOT a racy `Task.Delay`).

The field/state layout dump (captured at BOTH suspend points during the same
run) is the load-bearing evidence. The SM type is
`<NeoStep20_TwoIncompleteAwaitsProbe>d__17`:

```
[ASYNC-DUMP] suspend #1 (await1, <>1__state = 0):
  fields: <>1__state(0) <>t__builder(1) <a>5__1(2) <va>5__2(3)
          <b>5__3(4) <vb>5__4(5) <>s__5(6) <>s__6(7) <>u__1(8)
  ManagedObjects (4):  mo[0]=AsyncTaskMethodBuilder<int>
                       mo[1]=Task<int>          (<a>: await1 operand, set)
                       mo[2]=null               (<b>: await2 operand, NOT set yet)
                       mo[3]=TaskAwaiter<int>   (<>u__1: await1's awaiter)

[ASYNC-DUMP] suspend #2 (await2, <>1__state = 1):
  ManagedObjects (4):  mo[0]=AsyncTaskMethodBuilder<int>
                       mo[1]=Task<int>          (<a>: STILL set from await1)
                       mo[2]=Task<int>          (<b>: await2 operand, NOW set)
                       mo[3]=TaskAwaiter<int>   (<>u__1: await2's awaiter, OVERWRITTEN)
```

Three facts drive the design:

1. **`<>1__state` is the per-await discriminator.** It is `0` at the first
   suspend, `1` at the second. Roslyn writes the await's state number into
   `<>1__state` BEFORE calling `AwaitUnsafeOnCompleted`. So the active await is
   always knowable. (The dump did NOT need to decode Roslyn's state->field map,
   however -- see fact 3.)

2. **`ManagedObjects` is FIELD-DECLARATION order, NOT assignment order.** Both
   `<a>` (mo[1]) and `<b>` (mo[2]) are `Task` references. By the second suspend
   BOTH are non-null. The existing reverse-scan ("count `Task` fields; > 1 is
   ambiguous") genuinely CANNOT distinguish them -- Finding B's NIE is correct
   for the scan-based approach.

3. **`<>u__1` is a SINGLE awaiter field, REUSED across awaits of the same
   awaiter type.** Roslyn overwrites `<>u__1` with the CURRENT awaiter before
   each `AwaitUnsafeOnCompleted`. At suspend #1 `mo[3]` is await1's
   `TaskAwaiter<int>`; at suspend #2 `mo[3]` is await2's `TaskAwaiter<int>`.
   The `TaskAwaiter` wraps a single `m_task` field (read via reflection by the
   existing `GetAwaiterTask` helper). So **`<>u__1`'s awaiter is ALWAYS the
   currently-awaited `Task`'s awaiter** -- zero ambiguity, regardless of how
   many `Task` operand fields the SM hoists.

**REFUTED candidate (the "read the awaiter from the suspend frame slot 1"
approach):** the dump measured the suspend frame's slot-1 byref as
`(objIdx=27676168, off=422)` -- a garbage `objIdx`, NOT a valid mStack index or
the `-1` frame-native sentinel. The `AwaitUnsafeOnCompleted(ref builder, ref
awaiter, ref sm)` awaiter argument does NOT surface as a clean `(objIdx, off)`
Ref Slot at `frameBase+8` the way `SetResult`'s builder byref does. Reading the
awaiter from the frame is therefore NOT a robust primary path. The awaiter
FIELD (`<>u__1`, mo[3]) is the reliable source. (The implementer may re-probe
the frame layout if a frame-read is desired for an optimization, but the
design does NOT depend on it.)

### Root-cause diagnosis (one line)

The suspend path scans the SM's hoisted `Task` operand fields (ambiguous for
multi-await) instead of reading the SINGLE reused `<>u__1` awaiter field, which
Roslyn ALWAYS overwrites with the active awaiter before suspend.

## Goals / Non-Goals

**Goals:**
- **G1 (the fix):** `GetAwaitedTaskFromSm` recovers the active `Task` from the
  `<>u__1` awaiter field (`GetAwaiterTask` on the awaiter in `ManagedObjects`),
  so a multi-await SM registers its continuation on the CORRECT `Task` at EACH
  suspend and reads the CORRECT awaiter's `GetResult` at EACH resume.
- **G2 (resume correctness):** `TaskAwaiter_T_GetResult_Neo`'s fallback (the
  reloaded awaiter's `m_task` is null after the F-10 boxed-struct heap round-
  trip) recovers the active `Task` the same awaiter-field way (state-aware), not
  via the ambiguous scan.
- **G3 (fail-loud narrowing):** the single-Task NIE guard is RETAINED only for
  the genuinely-unsolvable shape (no resolvable awaiter field AND no unambiguous
  single `Task`), NOT for "2+ Task fields". A recovery-miss still fails loud.
- **G4 (deterministic gate):** `NeoStep20_TC12_TwoIncompleteAwaits` drives an SM
  through suspend -> resume -> suspend -> resume -> SetResult with deterministic
  TCS completions; the multi-await probe (NOT a green smoke) is the binding
  success criterion. A second host-side incomplete-`Task` cell pair is added so
  the SM can await TWO genuinely-incomplete Tasks.
- Legacy byte-identical (every edit `#if ENABLE_NEO_MODE` or in a Neo-only file).

**Non-Goals (sequenced, not parked):** `ExecutionContext`/`SynchronizationContext`
capture; `ValueTask<T>` / `async void` SUSPEND path; custom / non-`Task` awaiters
(already fail-loud); the zero-alloc custom `Task<T>` (the TCS bridge stays).

## Decisions

### D1: `GetAwaitedTaskFromSm` -- prefer the `<>u__1` awaiter field (the active awaiter)

The dump proves `<>u__1` always holds the active awaiter at suspend. Rewrite the
recovery order so the awaiter-derived `Task` is the PRIMARY source:

1. **Scan `ManagedObjects` for an awaiter** (a boxed `TaskAwaiter` /
   `TaskAwaiter<T>` / a `ConfiguredTaskAwaitable.ConfiguredTaskAwaiter` etc., per
   the existing `GetAwaiterTask` recognition). The awaiter field is the active
   one. `GetAwaiterTask(awaiter)` extracts its `m_task` -> the active `Task`.
   RETURN it. This is unambiguous for any number of awaits (the single reused
   `<>u__1` is overwritten before each suspend).
2. **Fallback (single-`Task` shape, the TC8 / explicit-local case):** if NO
   awaiter is found in `ManagedObjects` (the awaiter was not hoisted as a boxed
   object -- e.g. it lived only in a frame local and the SM hoisted just the
   `Task` operand), scan for a directly-hoisted `Task`. If EXACTLY ONE, return it
   (the common single-await shape, unchanged from HEAD). If MORE THAN ONE, the
   scan is ambiguous -> throw the TAGGED NIE (Finding B's fail-loud, NARROWED to
   this case only).
3. **Last fallback:** the existing awaiter-`m_task` box scan (unchanged).

**Why the awaiter-first order is correct:** the awaiter is written to `<>u__1`
AFTER the `Task` operand is stored and AFTER `GetAwaiter()` ran on it, so the
awaiter's `m_task` is the SAME `Task` the SM is about to suspend on. Roslyn
reuses `<>u__1` for awaits of the same awaiter type (the dump confirms a single
`<>u__1` for two `Task<int>` awaits); for awaits of DIFFERENT awaiter types
Roslyn generates `<>u__2`, `<>u__3`, ... but ONLY the active one is non-default
at suspend time (the others are still default-awaiters, which `GetAwaiterTask`
recognizes as carrying a null `m_task` and skips). So "scan for an awaiter whose
`m_task` is non-null" is robust to multi-awaiter-TYPE shapes too.

**NOTE for the implementer (resolve at apply):** confirm with a dump that a
default/uninitialized `TaskAwaiter` (m_task == null) is correctly SKIPPED by the
awaiter scan (so a stale `<>u__2` from a prior await does not shadow the active
`<>u__1`). If Roslyn zero-inits unused awaiter fields, `GetAwaiterTask` returns
null and the scan naturally skips them. If a stale non-null awaiter can persist,
prefer the HIGHEST-index non-null awaiter (the most-recently-written field is the
active one -- consistent with Roslyn's overwrite-before-suspend). The dump in
this design shows only the single-`<>u__1` shape; the multi-awaiter-TYPE shape is
a follow-up probe the implementer should construct if a test needs it (a
multi-await-different-TYPES cell, e.g. `await taskInt; await taskString;`).

### D2: `TaskAwaiter_T_GetResult_Neo` fallback -- state-aware active-Task recovery

At resume, MoveNext reloads the awaiter from `<>u__1` (the field MoveNext
dispatches to by `<>1__state`) and calls `GetResult`. The reload is therefore
ALREADY the correct (active) awaiter. The existing fallback
(`task == null -> GetAwaitedTaskFromSm(sm)`) fires only when the reloaded
awaiter's `m_task` did not survive the F-10 boxed-struct heap round-trip. After
D1, `GetAwaitedTaskFromSm` returns the active `Task` (awaiter-first), so the
fallback is multi-await-correct by construction. NO separate change to
`GetResult` beyond reusing the D1-fixed `GetAwaitedTaskFromSm`. (The implementer
confirms with the TC12 resume trace that the resumed `GetResult` reads await2's
`Task` at the second resume, not await1's stale one.)

### D3: The single-Task NIE guard -- narrowed, not removed

Finding B's TAGGED NIE is RETAINED but its trigger is NARROWED from "the SM hoists
> 1 `Task` field" to "the scan found > 1 `Task` field AND no awaiter-derived
`Task` could be recovered" (i.e. the genuinely-ambiguous case where neither the
awaiter nor a single unambiguous `Task` identifies the active await). This keeps
the fail-loud guarantee (a recovery-miss never silently picks the wrong field)
while unblocking the common multi-await shape (which resolves via the awaiter).
The NIE message is updated to reflect the narrowed condition.

### D4: TC12 + the second incomplete-Task host cell (the deterministic gate)

The existing host cell (`TestCLRBinding.GetIncompleteTask` /
`CompleteIncompleteTask`) is a SINGLE self-resetting `TaskCompletionSource<int>`;
it cannot back TWO simultaneous incomplete awaits (completing it swaps in a fresh
one, so the SM's first await operand goes stale). TC12 needs TWO independent
incomplete Tasks:

- **Host (`TestClass3.cs`):** add `s_incompleteTcs2` + `GetIncompleteTask2()` +
  `CompleteIncompleteTask2(int)` -- an independent, self-resetting
  `TaskCompletionSource<int>`, byte-identical contract to the existing pair.
- **Probe (`NeoStep20Test.cs`):** `NeoStep20_TwoIncompleteAwaitsProbe` awaits
  `GetIncompleteTask()` (state 0) then `GetIncompleteTask2()` (state 1) and
  returns `va + vb`. PRIVATE (the harness must not auto-discover it).
- **Driver (`NeoStep20_TC12_TwoIncompleteAwaits`):**
  1. `var t = probe();` GATE 1 `if (t.IsCompleted) dz;` -- await1 truly suspended.
  2. `CompleteIncompleteTask(va);` -- resume #1 -> `GetResult(va)` -> await2
     suspends. (Cannot directly observe "await1 resumed"; proceed.)
  3. `CompleteIncompleteTask2(vb);` -- resume #2 -> `GetResult(vb)` -> SetResult.
  4. Bounded spin-wait (capped iterations + periodic `Thread.Yield()`, NO real
     delay) for `t.IsCompleted`; exhaust -> divide-by-zero (a stuck/deadlocked
     resume FAILS rather than hanging -- the >10s rule).
  5. GATE 2 `if (t.Result != va + vb) dz;` -- BOTH resumes ran `GetResult` at the
     CORRECT await point with the CORRECT awaiter.
- TC12 is `[Ignored]` until the fix lands (a hanging/faulting test cannot live in
  the smoke); UN-IGNORED by the implementer once green. The implementer's
  stash-toggle (fix in / fix out) is the binding proof: TC12 FAILs on HEAD (the
  tagged NIE faults the task) and PASSES after the fix.

The probe is deterministic (TCS-backed, NO `Task.Delay` race -- the lesson from
`neo-async-controlflow-iscompleted`'s racy-probe false-negative).

### D5: Sequencing + blast-radius

D1 is the load-bearing change (one method, `GetAwaitedTaskFromSm`, in a Neo-only
file). D2 is a no-op beyond reusing D1. D3 narrows an NIE message. D4 is test +
host-cell. No JIT change, no optimizer change, no shared-engine change, no
`ILAsyncContext<T>` change (the context already holds the SM + MoveNext; the
resume re-enters MoveNext which reloads the correct awaiter by state). The full
`NeoStep` smoke (238/238 at HEAD) is the regression gate; the NeoStep20 slice
(13/13) is the async-specific gate. Legacy is untouched (the file is Neo-only;
plain-`Debug` compiles it out).

## Risks / Trade-offs

- **[Stale non-null awaiter shadowing the active one]** -> if Roslyn does NOT
  zero-init unused awaiter fields (`<>u__2` from a prior await persisting non-
  null), the awaiter scan could pick a stale awaiter. Mitigation: D1's note --
  prefer the highest-index non-null awaiter (most-recently-written = active); the
  implementer dump-gates this on a multi-awaiter-TYPE probe before trusting it.
  The single-`<>u__1` shape (the common case) is unaffected.
- **[The awaiter's `m_task` is null at suspend]** -> `GetAwaiterTask` returns
  null for a default awaiter; the scan skips it. If ALL awaiters are null at
  suspend (should not happen -- MoveNext just stored a real awaiter), fall through
  to the single-`Task` scan. Mitigation: the order (awaiter-first, then single-
  Task, then awaiter-box) covers every observed shape; the NIE fires only for the
  genuinely-ambiguous case.
- **[Cross-thread resume correctness]** -> the second resume runs on the
  threadpool thread that completed `task2`; the sink-swap (`_currentAsyncContext`)
  and the fresh-pooled-interpreter pattern (Piece 3) already handle this for the
  single-await case. TC12's two-resume shape is the adversarial extension. The
  Finding-A unconditional `_currentAsyncContext = sink;` is unchanged (a sync
  nested drive during EITHER resume clears the context correctly).
- **[TC12 hangs instead of failing]** -> a malformed multi-await resume that
  loops or deadlocks. Mitigation: TC12's bounded spin-wait (max iterations) FAILS
  on timeout via divide-by-zero; the >10s rule kills a true hang.
- **[The `SmContextMap` driver-thread leak (review Finding D) compounds across 2
  suspends]** -> each suspend parks a context on the driver-thread
  `SmContextMap`; the resume (threadpool thread) removes it on the WRONG thread.
  For TC12 (one SM, two suspends) the SAME `sm` key is re-parked on the second
  suspend (overwriting the first entry -- correct, only one context per SM at a
  time). Bounded growth (Finding D's accepted-known); NOT a correctness issue for
  TC12.

## Migration Plan

No migration (pure feature add for Neo; Legacy unchanged). Rollback = revert the
change directory + the `GetAwaitedTaskFromSm` edit; the single-await path
(TC8/TC11) is unaffected (the awaiter-first order returns the same `Task` the
single-`Task` scan did for the single-await shape), and the multi-await shape
returns to the tagged NIE.

## Open Questions

- **OQ1 (resolve at apply, D1):** does a multi-awaiter-TYPE SM (e.g.
  `await taskInt; await taskString;`) zero-init its unused awaiter fields, or must
  the scan prefer the highest-index non-null awaiter? Construct a
  `NeoStep20_TC13` (OPTIONAL keeper) if a test needs it; otherwise record the
  decision from a dump and move on (the common same-type multi-await shape --
  TC12 -- does not exercise this).
- **OQ2 (resolve at apply, D2):** confirm via the TC12 resume trace that the
  resumed `GetResult` at the SECOND resume reads `task2` (state 1's awaiter), not
  a stale `task1`. If it reads stale, the `<>u__1` reload at resume is not
  state-correct and a deeper MoveNext-state fix is needed (out of scope; route to
  a follow-up). TC12 GATE 2 is the binding check.

## Apply findings

Implementer (completion-3 wave, HEAD `311a6915`). All edits Neo-only (the engine
file is `#if ENABLE_NEO_MODE`-gated; the test/host files are Neo-test-only). D1
and D2 were applied as designed; D3 was applied as designed; D4 was applied and
EXPOSED a missing load-bearing piece the design under-specified (see the
deviation below) -- resolved.

### What changed (file:line)

**D1 + D3 -- `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs`,
`GetAwaitedTaskFromSm` (the method immediately preceding the `SetStateMachine_Neo`
redirect).** Rewrote the recovery order to AWAITER-FIRST:

1. Scan `ManagedObjects` (highest-index first) for a boxed `TaskAwaiter` /
   `TaskAwaiter<T>` whose `GetAwaiterTask` yields a non-null `Task`. Return the
   HIGHEST-index non-null (the active awaiter -- D1's OQ1 mitigation for a
   potential stale non-null `<>u__2` from a prior await of a different type). This
   is unambiguous for any number of awaits.
2. FALLBACK (single-Task shape): if NO awaiter resolved, scan for a directly-
   hoisted `Task`. Exactly one -> return it; MORE THAN ONE -> throw the NARROWED
   tagged NIE (D3): "Neo async multi-Task awaiter not supported (no resolvable
   awaiter and an ambiguous multi-Task scan); ... State machine hoists N Task
   fields and no awaiter field (<>u__1) yielded a Task. (neo-async-movenext-fix
   finding B, narrowed by neo-async-multi-await D3)". The common multi-await shape
   (resolvable awaiter) does NOT reach this branch.
3. Last fallback: the awaiter-box scan (defensive; covered by 1).

**D2 -- `TaskAwaiter_T_GetResult_Neo` fallback.** No code change: the existing
`task == null -> GetAwaitedTaskFromSm(sm)` fallback (the F-10 boxed-struct heap
round-trip recovery) now returns the active Task via the D1 awaiter-first path.
Multi-await-correct by construction. Confirmed by the TC12 resume trace (OQ2
resolved: the second resume reads task B, not a stale task A -- TC12 GATE 2
`t.Result == va + vb` PASSES).

**D4 -- test + host cell.**
- `ILRuntimeTestBase/TestFramework/TestClass3.cs`: added
  `s_incompleteTcs2` + `GetIncompleteTask2()` + `CompleteIncompleteTask2(int)` --
  an independent self-resetting `TaskCompletionSource<int>`, byte-identical
  contract to the existing pair (atomic swap, deterministic across orderings).
- `TestCases/NeoStep20Test.cs`: added `using ILRuntimeTest;` (for the
  `[ILRuntimeTest(Ignored = ...)]` attribute, which lives in namespace
  `ILRuntimeTest`, not `ILRuntimeTest.TestFramework`), the PRIVATE probe
  `NeoStep20_TwoIncompleteAwaitsProbe` (awaits `GetIncompleteTask()` then
  `GetIncompleteTask2()`, returns `va + vb`), and the driver
  `NeoStep20_TC12_TwoIncompleteAwaits` (GATE 1 `!t.IsCompleted`, drive A, bounded
  spin, drive B, bounded spin, GATE 2 `!t.IsFaulted && t.Result == va + vb`).

### Deviation from D1-D4 + why (LOAD-BEARING)

The design (D5) asserted "the `ILAsyncContext<T>` change is NONE (the context
already holds the SM + MoveNext; the resume re-enters MoveNext which reloads the
correct awaiter by state)" and the Risks section assumed "the SAME `sm` key is
re-parked on the second suspend -- correct, only one context per SM at a time."
**The code did NOT realize that assumption.** `SuspendStateMachine` constructed a
FRESH `ILAsyncContext<T>` (with a fresh `TaskCompletionSource<T>` bridge) on EVERY
suspend and parked it on `SmContextMap[sm]`, overwriting the prior entry.

Symptom with D1 alone (D2/D3/D4 in, D5's reuse NOT in): TC12 FAILs at the resume-#2
spin-wait (`!resumed2`, line 430). Diagnosis via a temporary `[ASYNC-DUMP]` trace
in `GetAwaitedTaskFromSm` (since reverted) confirmed D1 IS working: at BOTH
suspends the awaiter IS at `mo[3]` with a non-null `awaiterTask` (so the
continuation IS registered on the CORRECT Task at each suspend). The failure was
DOWNSTREAM: the bridge the driver/test holds is context1's `tcs.Task`; at suspend
#2 a context2 was created, orphaning context1's `tcs`. Resume #2's `SetResult`
then routed `va+vb` to context2's bridge, leaving context1's bridge (the one the
test polls) forever incomplete -> the resume-#2 spin-wait exhausted.

**Fix (the deviation): `SuspendStateMachine` now REUSES the existing context
across suspends.** It checks `SmContextMap[sm]` first; if a context is already
parked (a prior suspend), it reuses it (its `stateMachine`/`moveNextMethod`/`tcs`
bridge are all still valid and are the bridge the driver observes) and re-binds
the resume `Action` onto that SAME instance. Only the FIRST suspend constructs a
new context and parks it. This is what D5/Risks ASSUMED was already happening;
the code now matches the assumption. With this + D1, TC12 PASSES.

(Reflection cost on reuse: one `GetMethod("MoveNextInternal")` + one
`Delegate.CreateDelegate` per suspend. Acceptable -- a per-suspend cache is a
later micro-opt; correctness first. A per-SM `resumeMi` cache could fold this.)

### Verification evidence

HEAD health (Block 0.1, before any edit): CLI `Debug_Neo` 0 errors, TestCases
`Debug` 0 errors; `NeoStep20` 13/0/0; `NeoStep` 238/0/0.

HEAD failure (Block 0.4, D4 in, fix out): TC12 FAILs -- "Attempted to divide by
zero" at the post-resume `t.IsFaulted` gate (line 435 under the pre-fix line
numbering); the tagged NIE faults the bridge at suspend #1.

Stash-toggle (Block 2.2 -- BINDING), TC12 driver un-ignored throughout:
- **fix OUT** (`git stash push -- CLRRedirections.AsyncNeo.cs`; HEAD engine,
  TC12 + host cell in): TC12 FAILs -- 1 test failed, "Attempted to divide by
  zero" (the tagged NIE faults the bridge at suspend #1).
- **fix IN** (`git stash pop`; full fix): TC12 PASSes -- 1 test, 0 failed.

Final gates (fix in):
| Gate | Result |
|------|--------|
| `NeoStep20` (TC1/4/6/7/8/9/10/11 + TC12) | **14/0/0** (13 + TC12) |
| `NeoStep` full regression | **239/0/0** (238 + TC12, 0 regressions) |
| `NeoOptHardening` | **24/0/0** (unchanged) |
| CLI `Debug_Neo` build | **0 errors** |
| TestCases `Debug` build | **0 errors** |
| Legacy-neutral (plain `Debug`) | AsyncNeo.cs compiles out (0 errors from it). NOTE: the CLI has a PRE-EXISTING plain-`Debug` build break (`NeoF13NestedProbe.cs` requires `/unsafe`, enabled only in `Debug_Neo` -- unrelated to this change, from F-13 commit `6d0efe68`); the Legacy `useRegister=true` smoke could not be run via the CLI for that reason. |

### Durable findings (async-machinery, for FUTURE async children)

1. **A multi-await SM suspends N times but the driver observes ONE bridge -- the
   first suspend's context.** The `ILAsyncContext<T>` bridge (`tcs.Task` the
   driver/test polls, returned by `get_Task` via `SmContextMap[sm]`) lives ON the
   context. Constructing a fresh context per suspend orphans every prior bridge
   and routes later `SetResult`s to the wrong (orphaned) bridge -- the test polls
   context1 forever. MUST reuse `SmContextMap[sm]` across suspends of the same SM
   (re-bind the resume `Action` onto the SAME instance). The resume (`MoveNextInternal`
   -> `ResumeAsync` -> `DriveMoveNextCore(_, sink=this)`) and the suspend-time
   `SmContextMap[sm]` re-park are both keyed on the SAME context instance.
2. **`<>u__1` (the reused awaiter field) IS reliably present in `ManagedObjects`
   with a non-null `m_task` at SUSPEND time** (dump-confirmed: `mo[3]` =
   `TaskAwaiter<int>` with the active `Task<int>` at both suspends). The F-10
   boxed-struct heap round-trip that NULLS the awaiter's `m_task` at RESUME
   (reading via `ldfld` of the hoisted CLR-struct-with-ref-field) does NOT affect
   the suspend-time boxed view -- so the awaiter-first scan is a sound PRIMARY
   path at suspend; the `GetResult` null-`m_task` fallback (which reuses D1) is
   only needed at resume.
3. **`[ILRuntimeTest(Ignored = ...)]` lives in namespace `ILRuntimeTest`, NOT
   `ILRuntimeTest.TestFramework`.** `NeoStep20Test.cs` had only
   `using ILRuntimeTest.TestFramework;`; adding `using ILRuntimeTest;` is required
   for the attribute (the host-cell types like `TestCLRBinding` are in the
   `.TestFramework` sub-namespace; the attribute + `ILRuntimeTestAttribute` are in
   the parent).

