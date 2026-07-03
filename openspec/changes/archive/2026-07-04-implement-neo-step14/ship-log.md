# Ship Log -- implement-neo-step14

Neo Step 14: Structured exception handling in the Neo register VM -- `throw`,
`catch`, `finally`, `leave`, and `rethrow` with correct nesting and cross-frame
propagation, reusing the shared handler-matching engine that Legacy already
uses. DELIVERED scope is the full Step 14 surface minus the explicit non-goals.

## Ship verdict: CLEAN

Adversarial review by an independent verifier (author != reviewer), see
`review-report.md`. 0 Blocker / 0 Major / 0 unresolved Minor. 2 Minor findings
filed as accepted-known INFO (non-blocking, see below) + 2 Nit. Top risk
(Optimizer.RegisterCleanup.cs Legacy-neutrality -- the catch-slot JIT annotation
that protects `baseRegStart` from register-compaction) confirmed CLEAN: the
annotation is Neo-only via `#if ENABLE_NEO_MODE` and Legacy is byte-for-byte
unchanged. Review-loop applied 0 rounds (no fix needed).

## Verification evidence

Builds (per CLAUDE.md subset; the full sln cannot build -- VSIX net472 vs
netstandard2.1 NU1201, unrelated to Neo):
- `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` -> 0 errors
  (transitively builds ILRuntime / ILRuntimeTestBase / LitJson).
- `dotnet build TestCases/TestCases.csproj -c Debug` -> 0 errors, produces
  `TestCases/bin/Debug/netstandard2.1/TestCases.dll`.

FULL NeoStep smoke (regression gate, same TestCases.dll + pre-generated
HotfixAOT.patch baseline):
- Filter `NeoStep`: 58 ran, 0 failed (was 49/49 after Step 13; +9 new Step 14
  exception cases).
- Filter `NeoStep14`: 9 ran, 0 failed (the +9 new cases for this step).
- Zero existing cases regressed; no Step-tagged NotImplementedException thrown
  on the delivered paths; no test exceeded the 10s infinite-loop watch.

## Review summary

CLEAN. The independent verifier confirmed:
- Throw/Rethrow/Leave/Leave_S/Endfinally arms + catch-object write are correct
  against the shared `HandleException` /
  `GetCorrespondingExceptionHandler` / `FindExceptionHandlerByBranchTarget`
  engine; Neo reuses the engine as-is and does NOT modify it.
- Cross-frame propagation is correct and self-cleaning: pendingThrow handoff +
  bottom-cleanup + post-fixed rethrow so an exception escaping a callee is
  offered to the caller's handler table (not silently unwound), and a method
  that throws uncaught leaves no frame/mStack leak.
- The catch-slot JIT annotation (protects `baseRegStart` from register
  compaction) is Neo-only and Legacy-neutral -- the top risk is CLEAN.
- All new code is inside `#if ENABLE_NEO_MODE`; Legacy (`ExecuteR`) and the
  shared engine (`ILIntepreter.cs`) untouched.

Review-loop: 0 rounds (review-report CLEAN on first pass).

## Delivered scope

DELIVERED this pass:
- `Throw` arm -- reads the exception object from the throw instruction's
  register-1 ref slot; null-ref slot throws a CLR `NullReferenceException`.
- `Rethrow` arm -- re-raises the current frame's `lastCaughtEx`.
- `Leave` / `Leave_S` arm -- jumps to the leave target but first diverts into
  any enclosing finally (via `FindExceptionHandlerByBranchTarget`), recording
  the leave target in `finallyEndAddress`.
- `Endfinally` arm -- re-throws `lastCaughtEx` when the finally was entered via
  an in-flight exception (`finallyEndAddress < 0`); resumes at the recorded
  leave target when entered via a `Leave` (`finallyEndAddress >= 0`).
- Catch-handler entry -- truncates `mStack.Count` to `frameRefBase +
  totalRefSize` and stores the caught exception object in the catch handler's
  reserved reference slot.
- Cross-frame propagation -- self-cleaning handoff of an unhandled callee
  exception to the caller's handler table (pendingThrow + bottom-cleanup +
  post-fixed rethrow); consistent frame/mStack state on every exit path.
- Catch-slot JIT annotation in `JITCompiler.cs` -- marks the catch-handler ref
  slot so `Optimizer.RegisterCleanup.cs` (Neo-only) does not compact it into
  `baseRegStart`; `#if ENABLE_NEO_MODE`-gated so Legacy is unaffected.

NON-GOALS (explicitly out of scope, remain NIE / deferred):
- IL `filter` / `endfilter` blocks -- remain `NotImplementedException` (rare in
  C#); not part of Step 14.
- async exceptions -- deferred to Step 20.
- CLR `new T(...)` as an exception source for `throw` -- bounded by CLR `newobj`
  (Step 9 / 18); this capability is specified and validated using exception
  sources that do not require newobj (CLR-raised arithmetic/null-deref,
  rethrow of a caught object).
- stack-overflow guard -- deferred to Step 26.

## Accepted-known (INFO, non-blocking; recorded from the review)

- catch-wrapper (Minor-info): the catch slot stores the `ILRuntimeException`
  wrapper (matches Legacy exactly, `ILIntepreter.Register.cs:5327`); the spec's
  "unwrapped" wording is loose. May surface identically in Legacy when `isinst`
  (Step 15) lands.
- TC2-no-isinst (Minor-info): TC2 asserts `e != null` because the type-check in
  the catch (`isinst`) is Step 15; the exception identity/round-trip behavior
  itself is correct.

PRE-EXISTING carryovers (NOT Step 14 regressions):
- K1 -- FCP mis-propagates value-type Moves (Step 12b carryover; pre-existing
  optimizer bug). Out of Step 14 scope.
- K2-family -- the Move path mis-handles scalar/constant -> boxed-ref CLR-VT
  local assignment (Step 13 carryover; pre-existing).
- Step 13 area 3 (`constrained.` -> Step 17) + areas 4-5 (Step 13b) -- deferred,
  untouched by this pass.

## Files changed

Runtime (new code under `#if ENABLE_NEO_MODE`):
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- the
  Throw/Rethrow/Leave/Leave_S/Endfinally `ExecuteNeo` arms + catch-entry
  mStack/slot write + self-cleaning cross-frame propagation. Legacy
  (`ExecuteR`) and the shared engine (`ILIntepreter.cs`) untouched.
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- Neo-only
  (`#if ENABLE_NEO_MODE`) catch-slot annotation that protects the catch handler
  ref slot from register compaction.
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.RegisterCleanup.cs` --
  Neo-only respect for the catch-slot annotation; the top risk reviewed and
  confirmed Legacy-neutral (Legacy path unchanged).

Tests:
- `TestCases/NeoStep14Test.cs` -- new, 9 green tests covering throw-catch,
  try-finally, finally-on-leave, endfinally re-propagation, rethrow, nested
  finally, cross-frame catch, cross-frame caller-side finally, and uncaught
  leak-free escape.

## Git note

All Step 14 changes are uncommitted in the working tree on branch
`features/object-model-overhaul`. Per the SHIPPER brief, the LEAD commits and
pushes after this step; no commit is made here. No source edits made during
ship/archive (ship-log write + spec sync + directory move only).

## Stage 2 (archive) outcome

- Spec sync: `openspec/specs/neo-exceptions/spec.md` CREATED -- the ADDED delta
  resolved to a new capability (no prior spec existed), delta markers dropped,
  all requirement text + scenarios preserved, U+FFFD-free (ASCII verified).
- Archive: change moved to
  `openspec/changes/archive/2026-07-04-implement-neo-step14/` (`.openspec.yaml`
  and `auto-run.json` moved with it).
- `openspec list` shows no active changes after archive.
