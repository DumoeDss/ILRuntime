## Context

This child resolves **B1** — the async-suspend blocker carried by the
`neo-step20-async-suspend` PARTIAL ship and the `neo-completion-portfolio`
handoff. The prior framing: the closed-generic
`AwaitUnsafeOnCompleted<TA,TSM>` (2 generic args) resolves on the Legacy
`RedirectMap` but NOT on the Neo `RedirectMapNeo`, while the 1-generic-arg
`Start<TSM>` resolves on both. That finding was made with a RACY
`Task.Delay(N)` probe (the prior session's binding failure mode: a sync-
completing task makes the await appear to take the completion branch when it
does not). The `hasRedirect=True` contradiction (the redirect is "found" but
reportedly does not dispatch) was never resolved with a deterministic probe.
This child's propose phase MUST resolve it from a HEAD `38133af8` dump.

**The dump-gate verdict (static + read-only runtime, this propose phase):**

1. **The redirect-lookup is ARITY-AGNOSTIC.** `CLRMethod.TryGetRedirection<T>`
   (`ILRuntime/CLR/Method/CLRMethod.cs:111-131`) is generic over the map and
   branches ONLY on `def.IsGenericMethod && !def.IsGenericMethodDefinition`:
   for a closed-generic call it does
   `map.TryGetValue(def.GetGenericMethodDefinition(), out redirect)` first,
   falling back to `map.TryGetValue(def, out redirect)`. There is NO branch on
   generic-argument count. The 1-arg `Start<TSM>` and the 2-arg
   `AwaitUnsafeOnCompleted<TA,TSM>` are served by the IDENTICAL code path. The
   prior handoff's "suspect arity-specific handling in
   `TryGetRedirection`/`GetGenericMethodDefinition`" (OQ1 of the suspend design)
   is STATICALLY UNSUPPORTED by the code.

2. **`AwaitUnsafeOnCompleted_Neo` is a TAGGED NIE, not partially wired.**
   `CLRRedirections.AsyncNeo.cs:427-437`:
   `throw new NotImplementedException("Neo async suspend path: neo-step20-async-
   suspend (Step 20 suspend slice)")`. There is no suspend machinery to
   "dispatch correctly" to; the body throws. So the framing "minimal fix that
   makes the deterministic probe's truly-async await dispatch correctly" is
   really asking about Phase 2 (the suspend body), which is OUT of scope for B1.

3. **The redirect IS registered on the Neo map.** `RegisterAwaiters`
   (`CLRRedirections.AsyncNeo.cs:789-809`) registers the OPEN generic method
   definition (`m.IsGenericMethodDefinition`, the 2-arg `AwaitUnsafeOnCompleted`)
   to `AwaitUnsafeOnCompleted_Neo` via `RegisterCLRMethodRedirectionNeo`.
   `CLRRedirectionsAsyncNeo.Register(this)` runs in the `AppDomain` ctor
   (`ILRuntime/Runtime/Enviorment/AppDomain.cs:253`, under
   `#if ENABLE_NEO_MODE`) BEFORE the test-harness `CLRBindings.Initialize`
   (`ILRuntimeTestBase/TestBase/TestSession.cs:66`). Registration is FIRST-
   registered-wins (`AppDomain.cs:764-765`: `if (!ContainsKey) map[mi] = func`),
   so the custom open-def NIE registration wins over the autogen
   `AwaitUnsafeOnCompleted_*_Neo` round-trip stubs (which key on specific CLOSED
   instantiations and are shadowed by the open-def-first lookup order).

4. **The dispatch site consults `RedirectionNeo`.** `InvokeNeoClrMethod`
   (`ILIntepreter.Neo.cs:634-642`) reads `clrMethod.RedirectionNeo`
   (`CLRMethod.cs:145-155`), and if non-null dispatches to it and returns. So if
   the redirect resolves, the NIE is invoked.

5. **No current test exercises B1.** `NeoStep20_TC8_IncompleteAwaitHitsTaggedNIE`
   was REMOVED (it needed the suspend slice). All remaining NeoStep20 tests
   (TC1/TC4/TC6/TC7) are sync-completing; the `IsCompleted` short-circuit skips
   `AwaitUnsafeOnCompleted`. Read-only baseline this propose phase: `NeoStep20`
   = 9/9 green (HEAD `38133af8`), confirming no test reaches the NIE (a thrown
   NIE would fail the test).

**Verdict:** the static evidence is consistent with **outcome 3** (B1 not
reproducible as a resolution bug; the redirect resolves to the tagged NIE). The
`hasRedirect=True` finding from the disproven sibling stands. The deterministic
probe is the binding gate that confirms this empirically before any conclusion.

## Goals / Non-Goals

**Goals:**
- Design a DETERMINISTIC `TaskCompletionSource`-style probe that FORCES an await
  through `AwaitUnsafeOnCompleted` (IsCompleted deterministically false; no
  `Task.Delay` race).
- Run the probe on HEAD `38133af8` to settle B1 empirically: does the tagged
  NIE fire (resolve + dispatch = outcome 3), or does something else happen
  (a real resolution bug = outcome 2)?
- Ship the probe as a permanent regression guard + the verdict documentation.
- Pin the canonical `neo-async` spec's "tagged deferral" scenario to REQUIRE the
  deterministic probe (make it proven + falsifiable, not aspirational).

**Non-Goals (deferred):**
- The Phase-2 suspend/resume machinery (`AwaitUnsafeOnCompleted_Neo` body +
  `ILAsyncContext<T>.MoveNext` resumption + `get_Task` context branch). That is
  the `neo-step20-async-suspend` resume scope, a LARGE child. B1 is scoped to
  redirect RESOLUTION only.
- `AwaitOnCompleted` (ExecutionContext/SynchronizationContext capture).
- Multi-await suspend, `ValueTask<T>` suspend, `async void` suspend.
- A zero-alloc `ValueTask<T>` (the suspend path may allocate).

## Decisions

### D1: The deterministic-probe design (the gate)

The probe MUST make the await's `IsCompleted` deterministically `false` AND make
the outcome observable, without any timing race:

- The awaitable is a `TaskCompletionSource`-backed `Task` whose `SetResult` is
  NOT called before the assertion (the Task is genuinely, permanently
  incomplete for the duration of the test).
- The async method body is `await incompleteTask; return v;`. At the await,
  `TaskAwaiter_T_GetIsCompleted_Neo` (`CLRRedirections.AsyncNeo.cs:478-489`)
  reads the awaiter's task and returns `task.IsCompleted` = `false`.
- The C# compiler's lowered `IsCompleted` short-circuit then falls through to
  `AwaitUnsafeOnCompleted<TA,TSM>`, which the redirect-lookup resolves. The
  EXPECTED HEAD behavior is the tagged NIE.
- The test driver observes the outcome via try/catch around the async call:
  catching the `NotImplementedException` whose message references
  `neo-step20-async-suspend` = the redirect resolved + dispatched (outcome 3).
  A DIFFERENT exception, no exception, or a sync-completion = a real resolution
  bug (outcome 2).

The probe is FALSEIFIABLE: it distinguishes "NIE fired" from "did not reach the
NIE" deterministically. A `Task.Delay(N)` probe is REJECTED (it can
sync-complete). The probe is the successor to the removed
`NeoStep20_TC8_IncompleteAwaitHitsTaggedNIE`.

### D2: The scope decision (the dump-gate decides; partial-ship EXPECTED)

Three outcomes, decided by the probe:

1. **B1 resolved + Phase-2 suspend machinery proven on the deterministic probe.**
   NOT PURSUED here. This would require implementing the Phase-2 NIE body
   (suspend + `ILAsyncContext<T>` resumption + `get_Task` context), which is the
   `neo-step20-async-suspend` resume scope. Forcing it here violates the
   STOP/partial-ship discipline (the prior suspend child correctly STOPPED at 2
   stacked blockers rather than shipping a silent-wrong suspend).

2. **B1 resolved (the verdict) + Phase 2 deferred.** Ship the probe as a
   regression guard + the verdict. If the probe reveals a REAL resolution bug
   (outcome 2a), also ship the minimal arity/key fix in
   `TryGetRedirection` or the JIT call-operand construction. The suspend
   machinery defers. (The `neo-step20-async-suspend` precedent.)

3. **B1 NOT reproducible on HEAD with the deterministic probe** (the static
   verdict; EXPECTED). Ship the probe as a regression guard + document that the
   2-generic-arg redirect resolves to the tagged NIE via the same arity-
   agnostic path as `Start<TSM>`. The suspend MACHINERY is the only remaining
   async gap. NO engine change. (The `neo-async-controlflow-iscompleted` /
   `neo-k2fam-bridge` precedent: closed no-op / test-only.)

**STOP / partial-ship is binding.** The implementer does NOT force a Phase-2
suspend fix past the probe. Expected outcome: 3 (or 2a only if the probe
surfaces a real bug).

### D3: Probe wiring (apply-time decision, two options)

The test framework runs parameterless public static methods, so the incomplete
`Task` must reach the IL async method via either:

- **Option A (host-side cell):** a `TestCLRBinding` accessor returns a
  never-completed `TaskCompletionSource<int>.Task` created on the host side
  (mirrors the existing `SetAsyncVoidCell`/`GetAsyncVoidCell` pattern). The IL
  test reads it and awaits it. Avoids needing a `TaskCompletionSource` CLR
  binding in the interpreter. PREFERRED (smallest surface).
- **Option B (IL-side construction):** the IL test constructs a
  `TaskCompletionSource<int>` directly. Requires the interpreter to newobj a
  real `TaskCompletionSource<T>` (a CLR type not in the autogen bindings) via
  reflection fallback. Adds a new CLR-construction dependency; use only if
  Option A is blocked.

The implementer picks Option A unless the dump shows a blocker.

### D4: Legacy stance + gating

Legacy is the REFERENCE. The probe is a Neo-only regression guard
(`NeoStep20_*` naming). Any engine edit (outcome 2a only) is
`#if ENABLE_NEO_MODE`-gated or in Neo-only files; Legacy `ExecuteR` and the
autogen Legacy arms are untouched. The regression reference for a shared-
engine edit is the full `NeoStep` smoke + Legacy-neutral plain-`Debug` build.

## Risks / Trade-offs

- **[B1 is shared dispatch (outcome 2a only)]** -> a `TryGetRedirection` fix
  touches the generic-method-redirect lookup used by ALL generic CLR redirects,
  not just async. Mitigation: the probe settles whether a fix is even needed
  BEFORE any edit; if needed, the arity-agnostic lookup means the blast-radius
  is contained (the change is in the closed-generic key/`GetGenericMethodDefinition`
  equality, not an arity branch). Full `NeoStep` smoke (215/215) is the gate.
- **[The probe could mask a sync-completion] ->** the classic F-10/K1 silent-
  wrong-result (the prior session's exact failure mode). Mitigation: the probe
  uses a PERMANENTLY incomplete `TaskCompletionSource`-backed Task (SetResult
  never called before the assertion), so `IsCompleted` cannot race to true; and
  a sync-completing control (await `Task.FromResult`) asserts the probe is
  specific (does NOT NIE on a complete await).
- **[Hang / infinite loop] ->** a malformed resumption that never reaches a
  terminal state looks like a hang. Mitigation: the probe has NO resumption on
  HEAD (the NIE fires synchronously inside `DriveMoveNext`); >10s = kill +
  investigate (the unittest_guide rule). The probe must not spin-wait on the
  incomplete Task.
- **[Outcome 3 ships "only a test"] ->** looks like a no-op. Mitigation: the
  verdict documentation (this design + the spec scenario) is the deliverable;
  it converts an UNFALSIFIABLE prior claim into a PROVEN, regression-guarded
  fact, and isolates the remaining gap (Phase-2 machinery) for the suspend
  follow-up. This is the `neo-async-controlflow-iscompleted` precedent.

## Migration Plan

No migration (pure feature add / test add for Neo; Legacy unchanged). Rollback =
revert the change directory + the `TestCases`/optional engine edits; the
`AwaitUnsafeOnCompleted_Neo` / `ILAsyncContext<T>.MoveNext` return to throwing
the tagged NIE, and the NeoStep20 sync slice (9/9) is unaffected (it never
suspends).

## Open Questions

- **OQ1 (RESOLVED at apply -- the gate):** the deterministic probe's incomplete
  await does NOT throw the tagged NIE (NOT outcome 3). It HANGS. Verdict:
  **outcome 2a-DEEP** -- the probe surfaced a real bug, but NOT the
  redirect-resolution bug the static evidence predicted. B1 redirect RESOLUTION
  is EXONERATED (the custom `AwaitUnsafeOnCompleted`/`AwaitOnCompleted` open-def
  NIE redirects ARE registered on `RedirectMapNeo` -- 17 Await keys confirmed
  empirically; `TryGetRedirection` is arity-agnostic and correct; the
  closed-generic call's `GetGenericMethodDefinition()` matches the registered
  open def). The real blocker is a MoveNext control-flow bug in the truly-async
  path: after `get_IsCompleted` returns false (verified correct via TC10 + a
  redirect trace), the state machine NEVER reaches the `AwaitUnsafeOnCompleted`
  call (its Call handler never fires) AND never reaches `GetResult` either --
  it loops/hangs between them. NO `TryGetRedirection` / JIT-call-operand edit is
  warranted (both exonerated). The fix is deferred to the suspend follow-up
  (`neo-step20-async-suspend`). The probe (TC8) is shipped `[Ignored]` as the
  hang-reproducer; TC9 (sync control) + TC10 (IsCompleted diagnostic) ship as
  active hang-proof guards.
- **OQ2 (RESOLVED at apply):** Option A (host-side `TestCLRBinding` cell) was
  used; no blocker. `GetIncompleteTask` returns a never-completed
  `TaskCompletionSource<int>.Task`; the IL test reads it via the reflection
  fallback. Option B not needed.
- **OQ3 (out of scope, for the suspend follow-up):** the Phase-2 sink-swap
  mechanism (`_currentAsyncContext` ThreadStatic vs `SmTaskMap`), already
  designed in the archived `neo-step20-async-suspend` design D2/D3/D4. Not
  re-litigated here. NOTE: the MoveNext control-flow bug surfaced by this
  change's probe (OQ1 verdict) is a NEWLY-ISOLATED blocker for the suspend
  follow-up -- it must be fixed BEFORE the tagged NIE body is reachable, so the
  suspend child's first task is this control-flow fix (the redirect already
  resolves).
