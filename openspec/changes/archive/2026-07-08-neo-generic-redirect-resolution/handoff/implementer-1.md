# Implementer handoff — neo-generic-redirect-resolution (B1)

**HEAD:** `38133af8` · **Stage:** apply (small-feature pipeline) · **Date:** 2026-07-08
**Status:** HANDOFF — partial-ship (test-only). Substantial work remains (the
deferred MoveNext control-flow fix), owned by the `neo-step20-async-suspend`
resume child. This change is ready for the LEAD's ship/archive decision.

## One-line verdict

The deterministic probe **DISPROVED the design's outcome-3 prediction** and
surfaced **outcome 2a-DEEP**: B1 redirect *resolution* is EXONERATED (the tagged
NIE redirect IS correctly registered and would resolve), but a **MoveNext
control-flow bug** in the truly-async path makes the probe HANG before the
`AwaitUnsafeOnCompleted` call is ever reached. Per STOP discipline, NO engine
edit was applied; the probe ships `[Ignored]` + two hang-proof active guards.

## What shipped (test-only; engine at HEAD)

Files changed:
- `TestCases/NeoStep20Test.cs` — added TC8 (probe, `[Ignored]`), TC9 (sync
  control), TC10 (`IsCompleted`-only diagnostic); the two async helper bodies
  are PRIVATE so the harness does not auto-discover the hanging helper. Class
  comment updated with the verdict.
- `ILRuntimeTestBase/TestFramework/TestClass3.cs` — added `using System.Threading.Tasks;`
  + `TestCLRBinding.GetIncompleteTask` (never-completed TCS Task), `IsTaggedAsyncNIE`,
  `IsFaultedWithTaggedAsyncNIE` (host-side inspectors; mirror the
  `SetAsyncVoidCell`/`GetAsyncVoidCell` pattern).
- `openspec/changes/neo-generic-redirect-resolution/{tasks.md, design.md,
  planning-context.md}` — verdict recorded (OQ1/OQ2 resolved).
- `.trae/documents/neo-deferred-items.md` — STEP-20-PARTIAL UPDATE 2026-07-08.

ALL temp engine instrumentation was reverted via
`git checkout -- ILRuntime/CLR/Method/CLRMethod.cs ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs ILRuntime/Runtime/Enviorment/AppDomain.cs ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`.
The engine is byte-identical to HEAD `38133af8`. Confirmed: `git status` shows
only the test/docs files modified under `ILRuntimeTestBase/` + `TestCases/` + openspec/trae.

## Gates (all green)

- NeoStep20 slice: `Ran 12 tests, 0 failed, 1 ignored` (EXIT=0, no hang).
- NeoStep smoke: `Ran 218 tests, 0 failed, 1 ignored` (215 baseline + TC9 + TC10
  + TC8-ignored; no regression).
- Legacy-neutral: plain `Debug` CLI build = 0 errors; Legacy NeoStep20 =
  `Ran 12 tests, 0 failed, 1 ignored`.
- NeoOptHardening: not re-run (no engine edit landed; 24/24 baseline unchanged).
- openspec validator: `neo-async` likely false-fails `validate` (PRE-EXISTING
  flakiness on capabilities other than `neo-optimizer`; not introduced here).
  LEAD does manual archive merge.

## The empirical trace (temp instrumentation, now reverted)

Probe run: filter `NeoStep20_TC8`, `Debug_Neo`, `-f net8.0`. Hangs (timeout 124).

Key data points (the prints were `B1_*`, all removed before ship):
1. `B1_POSTREG mapCount=69 awaitKeys=17 mapHash=48285313` — right after the custom
   `CLRRedirectionsAsyncNeo.Register`, the Neo map has 17 Await keys.
2. `B1_REGNEO` — every `AwaitUnsafeOnCompleted`/`AwaitOnCompleted` open-def NIE
   registration lands on the Neo map (hash 48285313); the autogen pass grows it
   to 245+ entries. Registration is correct.
3. `B1_LOOKUP` (in `TryGetRedirection`) — fires only for the **Legacy** map
   (hash 33726620, 47 entries, 0 Await keys), driven by the JIT compile-time
   `cm.Redirection` check at `JITCompiler.cs:2123`/`:2215`. The **Neo** map is
   never consulted for the closed-generic call's compile-time check, but the
   Neo map (48285313) DOES have the keys. The lookup itself is correct: the
   closed call's `GetGenericMethodDefinition()` handle (140709099823464) EXACTLY
   matches the registered open def's handle.
4. `B1_GETTER` (in `RedirectMapNeo` getter) — always returns the SAME Neo map
   (48285313). Same AppDomain (`appInst=31914638` throughout). No map reassignment.
5. `B1_DIAG` (the tagged NIE body) — **count = 0**. The NIE is never dispatched.
6. `B1_INVOKE` (in `InvokeNeoClrMethod`) — fires once for `GetAwaiter`
   (`redirectNeoNull=False`). NEVER fires for `AwaitUnsafeOnCompleted`.
7. `B1_ISC` (in `TaskAwaiter_T_GetIsCompleted_Neo`) — fires once:
   `taskCompleted=False`, wrote 0 to `retDst`. So `get_IsCompleted` correctly
   returns **false** for the incomplete Task.
8. `B1_CALLPRE` (in the `ExecuteNeo` `Call` case) — **never fires**. The
   `AwaitUnsafeOnCompleted` `Call` is never reached.
9. `B1_GETRESULT` (in `TaskAwaiter_T_GetResult_Neo`) — **never fires**. GetResult
   is never reached either.

Conclusion: after `get_IsCompleted` returns false, the state machine hangs in a
loop / block transition that reaches NEITHER the `AwaitUnsafeOnCompleted` call
NOR `GetResult`. TC9 (sync-completing control) passes because a completed Task
takes the `IsCompleted == true` short-circuit and never exercises this branch.
TC10 (direct `incomplete.GetAwaiter().IsCompleted`) passes because it isolates
the `get_IsCompleted` redirect from the MoveNext bug.

## Distilled decisions / dead ends / eliminated hypotheses

- **ELIMINATED: "B1 is a redirect-resolution bug."** The redirect IS registered
  and the lookup IS correct. The design's proposed fix sites
  (`CLRMethod.TryGetRedirection` and the JIT call-operand in `JITCompiler.cs`)
  are both exonerated and would NOT fix the hang. Do NOT touch them for B1.
- **ELIMINATED: "the autogen `default(TaskAwaiter<int>)` stub wins."** The custom
  `TaskAwaiter_T_GetIsCompleted_Neo` IS dispatched (first-registered-wins; custom
  registers in the AppDomain ctor before `CLRBindings.Initialize`). TC10 + B1_ISC
  prove it returns false correctly.
- **ELIMINATED: "the `brtrue` reads the wrong offset and routes to GetResult
  regardless."** The 2026-07-07 `neo-async-controlflow-iscompleted` update made
  this claim ("control-flow is NOT a bug on HEAD") from a RACY `Task.Delay` dump.
  The deterministic TCS probe contradicts it: GetResult is NOT reached either
  (B1_GETRESULT never fires). So the misroute-to-GetResult hypothesis is wrong;
  the hang is a genuine loop/block-transition between the `brtrue` and both
  branches. The 2026-07-07 "disproven" conclusion is PARTIALLY RE-OPENED (see
  `.trae/documents/neo-deferred-items.md` UPDATE 2026-07-08).
- **CONFIRMED: the probe is the binding adversarial proof.** A green sync smoke
  (TC9, the existing 9/9) does NOT exercise the false-`IsCompleted` branch — the
  exact F-10/K1 silent-wrong-result lesson. The deterministic TCS probe is what
  surfaced this bug.

## Next action (for the LEAD / the suspend follow-up)

1. **Ship/archive decision (LEAD):** this is a test-only partial-ship that
   converts the prior UNFALSIFIABLE B1 claim into a PROVEN, isolated finding.
   Recommend: ship as-is (TC9 + TC10 active guards; TC8 `[Ignored]`
   reproducer). The change DELIVERS the verdict (B1 exonerated; real blocker
   isolated) even though it ships no engine fix — that is the `neo-async-
   controlflow-iscompleted` / `neo-k2fam-bridge` precedent (closed test-only).
2. **The MoveNext control-flow fix is owned by `neo-step20-async-suspend`.** It
   MUST land before the tagged-NIE body is reachable. The follow-up's first task:
   instruction-level trace of the probe's MoveNext after the `get_IsCompleted`
   `brtrue` to find the loop/block-transition that prevents the
   `AwaitUnsafeOnCompleted` `Call` from being reached. Start from
   `NeoStep20_TC8` (un-ignore it as a local reproducer) and trace `ExecuteNeo`
   block transitions. Once the call IS reached, the redirect already resolves
   (verified) and the tagged NIE fires → TC8 turns green (re-enable it).
3. **Do NOT re-litigate B1 redirect resolution.** It is exonerated. The 2-generic-
   arg `AwaitUnsafeOnCompleted<TA,TSM>` resolves on `RedirectMapNeo` via the same
   arity-agnostic path as `Start<TSM>` (both verified registered + looked up).

## Reproduction

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
# The hang (un-ignore TC8 locally to reproduce; it is shipped [Ignored]):
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep20_TC8
# → times out (124). TC9 + TC10 pass; NeoStep20 slice is 12/0/1 (1 ignored).
```

## Validator note (task 6.3)

`openspec validate` for `neo-async` is a PRE-EXISTING false-failure in this env
(9/10 false-failures on capabilities other than `neo-optimizer`). NOT introduced
by this change. The LEAD does the manual archive merge.
