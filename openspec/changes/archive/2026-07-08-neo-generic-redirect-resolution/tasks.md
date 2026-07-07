## 1. Dump-gate confirm (apply open)

- [x] 1.1 Re-verified on HEAD `38133af8`: `CLRMethod.TryGetRedirection`
  (`ILRuntime/CLR/Method/CLRMethod.cs:111-131`) is arity-agnostic -- it branches
  ONLY on `def.IsGenericMethod && !def.IsGenericMethodDefinition` (trying
  `GetGenericMethodDefinition()` first, then the closed `def`). NO generic-arg-
  count branch; `Start<TSM>` and `AwaitUnsafeOnCompleted<TA,TSM>` use the
  identical path.
- [x] 1.2 Confirmed `AwaitUnsafeOnCompleted_Neo`
  (`CLRRedirections.AsyncNeo.cs:427-437`) is still a tagged NIE (not partially
  wired), `RegisterAwaiters` (`:789-809`) registers the open generic def
  (`m.IsGenericMethodDefinition`, 2-arg), and `AppDomain` ctor (`:253`, under
  `#if ENABLE_NEO_MODE`) calls `CLRRedirectionsAsyncNeo.Register(this)` first-
  registered-wins (`:764-765` `if (!ContainsKey) map[mi] = func`) before the
  harness `CLRBindings.Initialize`.
- [x] 1.3 Probe wiring = Option A (host-side `TestCLRBinding` incomplete-Task
  cell). No blocker seen: the `SetAsyncVoidCell`/`GetAsyncVoidCell` pattern
  (TestClass3.cs:184-186) is the model; the IL test reads the cell via the
  reflection-fallback `CLRMethod.Invoke`. Option B not needed.

## 2. Deterministic probe + control (the gate)

- [x] 2.1 Added the deterministic truly-incomplete-awaiter probe to
  `TestCases/NeoStep20Test.cs` (`NeoStep20_IncompleteAwaitProbe` + driver
  `NeoStep20_TC8_IncompleteAwaitHitsTaggedNIE`): awaits a
  `TaskCompletionSource`-backed Task whose `SetResult` is never called before
  the assertion. Driver asserts via the `1/0` pattern.
- [x] 2.2 Added the sync-completing CONTROL probe (`NeoStep20_CompletedAwaitControl`
  + driver `NeoStep20_TC9_SyncControlSkipsAwaitUnsafeOnCompleted`): awaits
  `Task.FromResult(42)`; asserts completed, NOT faulted, result 43 (must NOT
  reach `AwaitUnsafeOnCompleted`).
- [x] 2.3 (Option A) Added `TestCLRBinding` host-side accessors
  (`GetIncompleteTask`, `IsTaggedAsyncNIE`, `IsFaultedWithTaggedAsyncNIE`) in
  `ILRuntimeTestBase/TestFramework/TestClass3.cs`, mirroring the
  `SetAsyncVoidCell`/`GetAsyncVoidCell` pattern.

## 3. Run the probe and decide the outcome (the binding gate)

- [x] 3.1 Built + ran the deterministic probe (filter `NeoStep20_TC8`). VERDICT:
  outcome **2a-DEEP**, NOT the predicted outcome 3. The probe does NOT throw the
  tagged NIE -- it HANGS. See `planning-context.md` (Append findings) for the
  full empirical trace and the exonerated-vs-real-bug split.
- [x] 3.2 Verdict recorded in `planning-context.md` (Append findings). The
  verdict SUPERSEDES the design's outcome-3 prediction and its proposed fix
  sites: NO `TryGetRedirection`/JIT-call-operand edit is warranted (both are
  exonerated); the real blocker is a MoveNext control-flow bug (suspend-path
  scope, deferred).

## 4. Conditional: outcome 2a minimal resolution fix (ONLY if 3.1 shows a real bug)

- [x] 4.1 SUPERSEDED / DEFERRED. 3.1 DID surface a real bug, but NOT the
  redirect-RESOLUTION bug this group anticipated. B1 redirect resolution is
  EXONERATED (the custom open-def NIE redirects ARE registered on RedirectMapNeo
  -- 17 Await keys confirmed; `TryGetRedirection` is arity-agnostic and correct;
  the closed-generic call's `GetGenericMethodDefinition()` matches the
  registered open def). The real bug is a MoveNext control-flow bug in the
  truly-async path (after `get_IsCompleted` returns false the state machine
  hangs without reaching `AwaitUnsafeOnCompleted` or `GetResult`). That is
  suspend-path territory (the `neo-step20-async-suspend` resume child), NOT a
  B1 redirect fix. Per the binding STOP/partial-ship discipline, NO engine edit
  is applied in this change; the fix is deferred.
- [x] 4.2 N/A (3.1 did NOT show the tagged NIE -- it hung). Group 4 is closed as
  SUPERSEDED (the design's premise that B1 is a redirect-resolution bug is
  refuted).

## 5. Regression + adversarial gates

- [x] 5.1 `NeoStep20` slice = `Ran 12 tests, 0 failed, 1 ignored` (TC8 is
  [Ignored] -- the hang reproducer; TC9 control + TC10 IsCompleted-diagnostic
  pass; the 9 original green). EXIT=0 (no hang). The async helper bodies are
  PRIVATE so the harness does not auto-discover the hanging helper.
- [x] 5.2 Full `NeoStep` smoke = `Ran 218 tests, 0 failed, 1 ignored` (215
  baseline + TC9 + TC10 + TC8-ignored). No regression (engine is at HEAD -- all
  diagnostic instrumentation was reverted).
- [ ] 5.3 `NeoOptHardening` = 24/24. NOT RE-RUN -- no engine/shared-engine edit
  landed (group 4 deferred), so the 24/24 baseline is unchanged by this
  test-only change. LEAD may re-run to confirm at ship.
- [x] 5.4 Legacy-neutral: plain `Debug` CLI build = 0 errors; Legacy `NeoStep20`
  filter = `Ran 12 tests, 0 failed, 1 ignored` (TC9/TC10 pass in Legacy too; TC8
  ignored). All edits test-only.
- [x] 5.5 Adversarial load-bearing check: N/A -- no engine fix was applied (the
  engine is at HEAD). The probe itself IS the adversarial proof: it
  deterministically surfaces the bug (hangs) where a green sync smoke (TC9) does
  not. The stash-toggle is implicit: the engine is unmodified, and TC8 hangs on
  HEAD (the bug present) -- when the deferred control-flow fix lands, TC8 is
  un-ignored and turns green.

## 6. Verdict documentation + deferral update

- [x] 6.1 `design.md` Open Questions updated with the resolved OQ1/OQ2 verdicts
  (outcome 2a-DEEP; B1 exonerated; real blocker = MoveNext control-flow bug).
- [x] 6.2 `.trae/documents/neo-deferred-items.md` STEP-20-PARTIAL row updated:
  B1 resolution EXONERATED (not a redirect bug); the remaining async blocker is
  the MoveNext control-flow bug in the truly-async path, owned by the
  `neo-step20-async-suspend` resume child.
- [x] 6.3 Noted the openspec validator pre-existing false-failure on `neo-async`
  in `handoff/implementer-1.md` (not introduced by this change; LEAD does manual
  archive merge).
