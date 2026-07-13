# tasks -- neo-async-statemachine-null (Wave-2 child C8)

## Phase 1 -- re-audit (DONE)
- [x] Build CLI (Debug_Neo) + TestCases (Debug), 0 errors each.
- [x] Reproduce on Neo (name-filter `AsyncAwaitTest`): `Ran 13, 4 failded`,
      all 4 `ArgumentNullException('stateMachine')` at
      `AsyncMethodBuilderCore.Start` <- autogen `Start_1_Neo`.
- [x] Pin root cause: non-generic `AsyncTaskMethodBuilder.Start` (and
      `AsyncValueTaskMethodBuilder.Start`) NOT registered on `RedirectMapNeo`;
      autogen stub reads SM as `IAsyncStateMachineAdaptor` (null in Neo) ->
      framework Start throws ANE. Legacy `#else Start_1` works (grounding
      518/519).

## Phase 2 -- implement (DONE)
- [x] `CLRRedirections.AsyncNeo.cs` `Register()`: add open-generic `Start`
      registration to the non-generic `AsyncTaskMethodBuilder` block ->
      `AsyncTaskMethodBuilder_Start_Neo`.
- [x] Same for the non-generic `AsyncValueTaskMethodBuilder` block ->
      `AsyncValueTaskMethodBuilder_Start_Neo`.
- [x] No JIT / optimizer / object-model / binding change. Additive only.

## Phase 3 -- verify (DONE; truth = full-smoke number)
- [x] Rebuild CLI (Debug_Neo), 0 errors.
- [x] Name-filter `AsyncAwaitTest` after fix: `Ran 13, 0 failded` (4 flipped).
- [x] FULL SMOKE (no filter): `Ran 916, 110 failded` = `114 -> 110` (4
      flipped, 0 regressions; 110 failures contain no `AsyncAwait*` and no
      `NeoStep` test).
- [x] NeoStep filter: `Ran 382, 0 failded` (baseline holds).
- [x] Legacy-neutral: structural (entire file is `#if ENABLE_NEO_MODE`;
      registration uses the Neo-only `RedirectMapNeo`).

## Out of scope
- `[NEO-CLRSTRUCT-FIELD-OF-IL]` surface untouched (this child only registers
  the missing redirect; the non-generic Task path now passes regardless).
- No new NeoStep probe added (the 4 C8 tests `AsyncAwaitTest.TestRun/TestRun1/
  TestRun4/TestClass.Show1` are the permanent regression canaries -- they are
  non-NeoStep TestCases, exercised by the full smoke; they fail loud with the
  ANE on any regression of this redirect).
