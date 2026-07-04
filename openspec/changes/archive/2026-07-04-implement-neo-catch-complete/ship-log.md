# Ship Log -- implement-neo-catch-complete

**Change:** `implement-neo-catch-complete`
**One-line:** Extend the SHARED catch-type matcher `CheckExceptionType`
(`ILIntepreter.cs`) to accept an IL-typed (`ILType`) catch clause instead of
unconditionally throwing `NotImplementedException` -- closes deferred Neo item
**D-CHECKEX** (partial). Shared-engine fix; NOT `ENABLE_NEO_MODE`-gated; both
Neo `ExecuteNeo` and Legacy `ExecuteR` benefit.
**Stage:** SHIP (LEAD-only `/ship` skipped -- `openspec-gstack-ship` is
Rails/JS-centric, does not apply; no PR).

## Verdict: CLEAN

Review-loop clean (0 rounds). Adversarial review-report: **0 Blocker / 0 Major /
1 Minor (F1, doc) / 1 Follow-up (F2)**. F1 (canonical spec method-enumeration
omits `CheckExceptionType`) is fixed at archive time (see Stage 2). F2 is an
accepted-known follow-up, recorded below.

## Verification evidence

- **CLI build (`Debug_Neo`):** 0 errors.
- **TestCases build (`Debug`):** 0 errors.
- **Neo smoke (full NeoStep filter):** **91/91, 0 failed** -- zero regression vs
  the 91/91 baseline (no new tests added).
- **Legacy-neutrality (stash-proven):** the new IL branch is unreachable for any
  CLRType catch clause (every existing catch test names a CLRType -> enters the
  byte-for-byte unchanged `catchType is CLRType` arm). Independently reproduced:
  `git stash` the fix, rebuild, re-run the NeoStep14 catch filter on baseline
  HEAD `57e0af54` -> **identical** 3 pre-existing Legacy failures (Neo-targeted
  tests that were never green under Legacy; `ArgumentOutOfRangeException` at
  `AssignToRegister:5651` via `ExecuteR:5327`). Restore -> back to +34/-1. The
  basic Legacy try/catch/finally tests (the passing 6 in that filter) exercise
  the unchanged CLRType arm. No regression attributable to this change.

## Review summary

- **IL-branch construction: CORRECT.** No false-match, no missed-match across
  all traced (exception, catchType) pairs; null-guards complete; ordering
  correct (`as ILTypeInstance` before CLR fallback); `explicitMatch` symmetric
  with the CLRType arm; reuses Step 15 `ILTypeInstance.CanAssignTo` ->
  `ILType.CanAssignTo` (base + `Implements` walk) correctly.
- **Test-deferral: LEGITIMATE.** A positive IL-catch test is not authorable
  this pass -- `catch (T)` is rejected by the host C# compiler unless
  `T : System.Exception` (CS0155); an IL `class X:System.Exception` then fails
  to LOAD (`TypeLoadException: Cannot find Adaptor for:System.Exception`,
  `ILType.cs:1418`); and Throw handling `mStack[idx] as Exception` NREs on a
  plain IL instance (both engines). So the new `CanAssignTo` arm is genuinely
  unreachable from any host-compiled test today. Verified by build + the
  shared-engine neutrality argument + the full NeoStep smoke + the independent
  stash-proven Legacy-neutrality reproduction.
- **Ship-the-piece-alone: WORTHWHILE.** Correct + Legacy-neutral + closes a
  latent shared-engine crash on the catch hot path. Deferring a zero-regression
  fix until separate infra lands would leave a known crash in place for no
  benefit.

## Delivered scope

- **`CheckExceptionType` IL branch** (`ILIntepreter.cs`, ~line 5823): the former
  `else throw new NotImplementedException();` for a non-`CLRType` catch type is
  replaced by:
  - `exception == null` -> `return false;`
  - `exception as ILTypeInstance` -> exact `exIl.Type == catchType` when
    `explicitMatch`, else `exIl.CanAssignTo(catchType)` (IL base + interface).
  - otherwise (CLR) -> `catchType.TypeForCLR` null-guard, then exact-equal
    (explicit) or `IsAssignableFrom` (non-explicit).
  The `catchType == null` (catch-all) and `catchType is CLRType` arms are
  byte-for-byte unchanged. NO `#if ENABLE_NEO_MODE` gating -- shared-engine fix.

- **D-CHECKEX partial close:** this fix closes the `CheckExceptionType` NIE
  specifically. End-to-end IL-exception catch still needs (a) a registered CLR
  `System.Exception` `CrossBindingAdaptor` (IL `class X:Exception` load) and
  (b) Throw handling that throws an IL instance as itself (both engines'
  `... as Exception` NRE). The new `CanAssignTo` arm is inert until those land.

### Accepted-known follow-up (F2)

- **D-CHECKEX partial / F2:** end-to-end IL-exception catch needs the
  `System.Exception` `CrossBindingAdaptor` + Throw-for-IL handling. Track as a
  deferred Neo item (suggest `D-IL-EXCEPTION-THROW`). A positive IL-catch test
  is reserved for that pass.

## Files changed

- `ILRuntime/Runtime/Intepreter/ILIntepreter.cs` -- **+34 / -1** (the IL branch
  in `CheckExceptionType`; brief comment citing D-CHECKEX).
- **No test file shipped** (deferral adjudicated above; harness cannot author an
  ILType catch clause).
- openspec change artifacts (`proposal.md`, `design.md`, `review-report.md`,
  `tasks.md`, `specs/neo-exceptions/spec.md`, this `ship-log.md`,
  `.openspec.yaml`, `auto-run.json`).

## Git note

Uncommitted (LEAD handles commit+push). LEAD stages ONLY:
`ILRuntime/Runtime/Intepreter/ILIntepreter.cs` + the
`openspec/changes/implement-neo-catch-complete/` tree (and, at archive, the
canonical spec merge under `openspec/specs/neo-exceptions/`). NOT staged: the
`.pdb` churn under `Dependencies/`, the `.gitignore` modification, `nuget.config`,
`CLAUDE.md`, `.claude/`, `.vscode/`, and the stray `C:...step15_*.log` files --
all unrelated noise.
