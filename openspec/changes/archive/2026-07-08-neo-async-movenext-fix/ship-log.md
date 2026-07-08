# Ship Log - neo-async-movenext-fix

## Round 0 (impl-async): truly-async await shipped

3 pieces + TC8 GREEN. See `tasks.md` Status section + `design.md`.

## Round 1 (fixer / review-loop): 2 MEDIUM findings fixed + regression test

**Reviewer verdict:** APPROVE-WITH-FINDINGS (`review-report.md`). 2 MEDIUM
findings on the general truly-async path (the green smoke is structurally blind
to both). Both fixed; C/D/E accepted-known documented; 2 NEW deferred items
recorded.

### Finding A (MEDIUM) -- FIXED (one-line + regression test)

`_currentAsyncContext` was set only `if (sink != null)` in `DriveMoveNextCore`
(`CLRRedirections.AsyncNeo.cs`). A sync `DriveMoveNext` (a nested async's
`Start`) during a RESUME inherited the outer resume context -> the nested SM's
`SetResult` misrouted its result to the OUTER bridge (`ctx_A.CompleteResult`),
and the outer SM's own later `SetResult` DOUBLE-COMPLETED `ctx_A` (wrong bridge
value + `InvalidOperationException`).

**Fix:** `_currentAsyncContext = sink;` UNCONDITIONAL. A sync nested drive now
CLEARS the outer context (sink == null); the existing `prevCtx` save/restore
reinstates it for the resumed SM's continued execution; top-level sync drives
(`prevCtx == null`) are a no-op.

**Regression test:** `NeoStep20_TC11_NestedSyncAsyncAfterSuspend`
(`TestCases/NeoStep20Test.cs`). A truly-async SM (single await, TC8-style
suspend) that, after resume, CALLS a sync nested async (fire-and-forget; the
call triggers the nested's `Start` -> sync `DriveMoveNext` INSIDE the resume
scope = Finding A's scenario). GATE 1 `!t.IsCompleted`; GATE 2 `!t.IsFaulted`;
GATE 3 `t.Result == n + 50`.

**Stash-toggle proof (binding):**
- POST-FIX: TC11 PASSes. `t.Result == 73` (n=23; sm_A's own value; ctx_A
  completed exactly once).
- PRE-FIX (one-line reverted): TC11 FAILs at GATE 3, `t.Result == 6` (the
  misrouted nested value, `!= 73`); the double-complete trace appears but the
  harness records a clean "1 failed" (no process crash). TC8 stays GREEN
  throughout (single-level, unaffected).

### Finding B (MEDIUM) -- FIXED (silent-wrong -> fail-LOUD)

`GetAwaitedTaskFromSm` reverse-scanned the SM's fields and returned the
highest-field-index `Task`, NOT "the currently awaited one." For a multi-`Task`
SM this was SILENT-WRONG (wrong continuation target + wrong `GetResult`) -- the
forbidden silent-corruption class (the stobj-refloop M1 lesson: a recovery-miss
MUST fail the SAME way across all shapes).

**Fix:** when the direct-`Task` scan is AMBIGUOUS (more than one `Task` field on
the SM), throw a TAGGED `NotImplementedException` ("Neo async multi-Task awaiter
not supported (single-Task shape only); the currently-awaited Task cannot be
disambiguated" -> deferred multi-await follow-up). The SINGLE-`Task` shape (TC8 /
explicit-local single await) keeps working (the common case). This converts the
silent-wrong into a loud, tagged failure for the deferred multi-`Task` case.
Single-Task-supported / multi-Task-deferred scope documented in the spec delta.

### C/D/E -- accepted-known (document only)

- **C (LOW):** Option A's unconditional 8-byte `*(long*)retDst` write is unsafe if
  the `IsCompleted` dest slot is ever 4 bytes. Option B (generic zero-fill) is the
  proper fix; deferred.
- **D (LOW):** `SmContextMap[sm]` entry leaks on the driver thread (bounded growth;
  the cross-thread resume removes it on the wrong thread).
- **E (INFO):** custom / non-`Task` awaiters fault at suspend (not silent); not
  previously documented as a limitation.

All three documented in `specs/neo-async/spec.md` (Accepted-known limitations).

### NEW deferred items (surfaced building TC11)

5. **Multi-await SM double-suspends.** >= 2 `await` expressions in one SM
   re-suspend on the already-completed first Task after resume (broader than the
   original "two incomplete awaits" deferral). TC11 is single-await; the nested
   sync async is a plain call.
6. **`RecoverSmForGetTask` scan misidentifies the resuming SM during a nested async
   call in a resume** -> returns the resuming SM's (incomplete) bridge -> reading
   the nested's result BLOCKS. TC11 uses fire-and-forget to avoid it. Cleaning
   `SmContextMap[sm]` at resume start is the follow-up.

Both documented in `specs/neo-async/spec.md` + `tasks.md`.

### Final counts (round 1; all GREEN)

| Gate | Result |
|------|--------|
| Neo `NeoStep20` (TC8 + TC11) | **13/0/0** |
| Neo `NeoStep` full regression | **219/0/0** (218 + TC11; no regressions) |
| Neo `NeoStep25LoadExec` AOT | **28/28 cells passed** |
| Legacy `NeoStep20` (`useRegister=true`) | **13/0/0** (TC8 + TC11) |
| Plain `Debug` build (Legacy-neutral) | **0 errors** |
| `Debug_Neo` CLI build | **0 errors** |
| `Debug` TestCases build | **0 errors** |

### Files changed (round 1)

- `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs` -- Finding A (one-line
  unconditional `_currentAsyncContext = sink;`) + Finding B (tagged NIE for
  ambiguous multi-Task scan in `GetAwaitedTaskFromSm`).
- `TestCases/NeoStep20Test.cs` -- TC11 + two private probe helpers.
- `openspec/changes/neo-async-movenext-fix/tasks.md` -- round-1 section + 2 new
  deferred items.
- `openspec/changes/neo-async-movenext-fix/specs/neo-async/spec.md` -- Finding A
  scenario + Finding B scenario + Accepted-known limitations section.
- `openspec/changes/neo-async-movenext-fix/ship-log.md` -- this file.

### Durable findings (round 1, 1-3 lines)

1. Finding A's bug is the `if (sink != null)` conditional on a ThreadStatic that
   MUST be cleared for nested sync drives during a resume -- the save/restore
   pattern is correct, but the conditional assignment defeated it. Any per-drive
   ThreadStatic that participates in a save/restore must be assigned
   unconditionally (the restore handles the reinstatement).
2. A reverse-scan "pick one" heuristic over hoisted fields is silent-wrong when
   ambiguous; convert to a tagged NIE for the ambiguous case (fail-loud), keep the
   unambiguous common case fast. Never let a recovery heuristic silently pick the
   wrong field.
3. The truly-async path has TWO more deferred edges beyond the review's findings:
   multi-await SMs (double-suspend loop) and the nested-call-in-resume
   `get_Task` scan (misidentifies the resuming SM, blocks on its bridge). Both
   block "nested async inside a resumed async" beyond the fire-and-forget shape.
