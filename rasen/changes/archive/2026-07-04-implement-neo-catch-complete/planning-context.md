# Planning Context — implement-neo-catch-complete

Seeds the planner. Read FIRST, then research only what is missing. Append
durable findings to section 8 after propose.

## 1. User intent
> "按照你建议的顺序，继续按当前流水线推进后续内容！"

This run = **[CATCH-COMPLETE]** — the LAST phase of the deferred-items sequence.
A small follow-up: extend the shared `CheckExceptionType` to handle `ILType`
(non-CLRType) catch clauses (closes **D-CHECKEX**). Committed AND pushed after
review clean (user pre-authorized commit+push per phase).

Prior state (committed + pushed): Steps 11-18 + OPT-HARDEN + 13b. HEAD=`57e0af54`. NeoStep smoke baseline = 91/91.

## 2. Decompose decision (LEAD, already taken)
**SKIP decompose.** One small, coherent slice (extend one shared-engine type
check). Pipeline: propose → apply → verify → review-loop → ship → archive →
(LEAD commits + pushes).

## 3. Scope — D-CHECKEX (from `.trae/documents/neo-deferred-items.md`)
The shared engine's `CheckExceptionType` (`ILIntepreter.cs:5835`) throws NIE for
catch types that are NOT `CLRType` — i.e. an `ILType` catch clause. Neo catch
matching uses this shared path (Step 14 does NOT use the `isinst` opcode for
catch dispatch). So catching a thrown IL-typed exception into an `ILType` catch
clause is not yet supported (it throws NIE instead of matching).
**Fix:** extend `CheckExceptionType` to handle an `ILType` catch type — use
`CanAssignTo` (Step 15's isinst/CanAssignTo infrastructure) to test whether the
thrown exception's type is assignable to the catch clause's ILType. This is a
SHARED-ENGINE change (`ILIntepreter.cs`, used by both Neo and Legacy) — the fix
benefits BOTH and must be correct for both (it's a gap fix, not a Neo-only
behavior change). Verify Legacy-neutrality (Legacy either had the same NIE gap
or handles ILType catch a different way — confirm).

Dependency: Step 15 (isinst / CanAssignTo enables the assignability check).

## 4. RESEARCH REQUIRED (planner)
- `CheckExceptionType` (`ILIntepreter.cs:~5823-5840` per the Step 14/15 findings) — read the current logic: it tests `catchType.TypeForCLR.IsAssignableFrom(exception.GetType())`. For an ILType catch type, what is `catchType.TypeForCLR`? (An ILType's TypeForCLR is typically `typeof(ILTypeInstance)` or the base — so `IsAssignableFrom(exception.GetType())` likely returns false or the ILType-specific check is missing → the NIE at :5835). Determine the exact NIE condition + the correct ILType assignability check (`ILType.CanAssignTo` / `CanAssignToCross`).
- How the thrown exception's type is represented at the catch (an `ILTypeInstance` thrown → its `.Type` is the ILType; a CLR exception thrown → its `GetType()`). The check must handle: IL catch type + IL thrown type (CanAssignTo); IL catch type + CLR thrown type (a CLR exception caught by an IL catch clause that the IL type maps from — e.g. catching `System.Exception`); etc.
- Whether Legacy `ExecuteR` reaches the same `CheckExceptionType` (shared) and whether Legacy already handles ILType catch (if Legacy works and Neo doesn't, the gap is Neo-specific dispatch; if both NIE, it's a shared gap). This determines whether the fix is shared-engine (helps both) or needs a Neo-specific path.
- The Step 14 catch infrastructure (`GetCorrespondingExceptionHandler`, `HandleException`) + Step 15 isinst/CanAssignTo.
- `TestCases/NeoStep14Test.cs` (catch tests) for the test convention.

## 5. Hard constraints (from CLAUDE.md — obey exactly)
- Branch `features/object-model-overhaul`; the change is in the SHARED engine (`ILIntepreter.cs` `CheckExceptionType`) — NOT behind `#if ENABLE_NEO_MODE` (it's a shared gap fix). VERIFY it's correct for BOTH Neo and Legacy (Legacy is the reference; if Legacy already handles ILType catch correctly, don't break it; if Legacy has the same gap, the fix helps both). If the fix can't be made correct for both, gate the ILType branch appropriately.
- **Build:** CLI `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` → 0 errors. TestCases `dotnet build TestCases/TestCases.csproj -c Debug` (NEVER Debug_Neo).
- **Run tests:** FULL NeoStep smoke (Neo, Debug_Neo): `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` → all-green (was 91/91; your catch tests add, NO regression). ALSO run a **Legacy** smoke (plain `Debug` CLI build + a catch-test filter) to confirm the shared-engine change doesn't regress Legacy — the Legacy 519-test baseline (plain Debug + useRegister=true) is the reference; you don't need all 519, but a representative Legacy catch filter + the code-read argument.
- Tests = `public static` parameterless; optional `[ILRuntimeTest]`. Add `TestCases/NeoCatchCompleteTest.cs` (or extend NeoStep14Test), ASCII. Cover: throw an IL-typed exception, catch it in an ILType catch clause (now that Step 18 CLR newobj + Step 14 catch work, an IL exception type can be constructed/thrown/caught). Note: `throw new ILExceptionType()` needs IL newobj (Step 18 IL-VT newobj is deferred, but IL REF-type newobj works — Step 8b); so use an IL CLASS exception type. If throw-via-new isn't achievable for the IL type, catch a host-thrown exception into an IL catch clause that maps it.
- Test >10s = infinite loop — kill and investigate.
- **REGRESSION CAUTION:** `CheckExceptionType` is on the catch-dispatch hot path for EVERY exception (Neo + Legacy). A bug breaks catch matching for all. Full NeoStep smoke (Neo) + a Legacy catch filter are the gates.
- **CJK write caveat:** Author ASCII.

## 6. What the planner must produce (propose stage)
Invoke `openspec-propose` (Skill) OR author directly. Artifacts under `openspec/changes/implement-neo-catch-complete/`:
- `proposal.md` — Why / What Changes / Impact + the shared-engine-vs-Neo-only decision + the Legacy-neutrality argument.
- `design.md` — concrete, code-grounded: the `CheckExceptionType` ILType branch (CanAssignTo/CanAssignToCross); how it handles IL-thrown + CLR-thrown exceptions against an IL catch type; the Legacy-neutrality argument; edge cases (Exception base, interface catch, nested). Non-goals.
- `specs/<capability>/spec.md` — ADDED requirement (extend `neo-exceptions` — the Step 14 catch capability — with the ILType-catch-type requirement).
- `tasks.md` — checkbox tasks for one implementer pass.

Author != verifier: you ONLY propose.

## 7. Append-only findings log
(planner appends durable decisions/constraints discovered during propose here)

## 8. Planner findings (2026-07-04 propose)

### Shared-vs-Neo-only: SHARED gap (both engines NIE) -- fix helps BOTH
`CheckExceptionType` (`ILIntepreter.cs:5823-5836`) is reached identically by
Legacy and Neo: both interpreters wrap their dispatch loop in a per-iteration
C# try-catch calling the shared `HandleException`
(`ILIntepreter.Register.cs:5323` Legacy; `ILIntepreter.Neo.cs` Neo) ->
`GetCorrespondingExceptionHandler` (`:5609`) -> `CheckExceptionType`. The NIE at
`:5835` (non-CLRType catch) fires on BOTH engines. **Legacy has the SAME gap --
it never handled ILType catch either.** Therefore this is a shared-engine gap
fix, NOT a Neo-only behavior change. The fix is NOT gated behind
`#if ENABLE_NEO_MODE`; both engines gain IL catch support.

### The exact IL branch (design.md section 2)
Replace `else throw new NotImplementedException();` with: null-guard -> `as
ILTypeInstance` (exact `exIl.Type == catchType` if explicitMatch else
`exIl.CanAssignTo(catchType)`) -> else CLR fallback
(`catchType.TypeForCLR.IsAssignableFrom(exception.GetType())`, exact-equal if
explicitMatch). Reuses Step 15's `ILTypeInstance.CanAssignTo`/`ILType.CanAssignTo`
(`ILType.cs:2232`, walks BaseType+Implements). `CanAssignToCross` does NOT exist
in this codebase (planning-context listed it as a candidate -- only `CanAssignTo`
exists). The `exception` arg is ALREADY unwrapped by callers
(`GetCorrespondingExceptionHandler:5602` + `HandleException:4750-4758`), so this
method must NOT re-unwrap.

### Legacy-neutrality argument
The new branch is UNREACHABLE for every existing catch test (all name a CLRType
-- `DivideByZeroException`/`Exception`/etc.); those keep entering the unchanged
`catchType is CLRType` arm. The replaced NIE was never a passing outcome. So:
zero behavior delta on existing tests; the change can only turn a prior
NIE-crash into a match/no-match. Hot-path cost: one extra `is CLRType` test that
CLR catches already short-circuit.

### Testability -- yes, IL throw+catch is now achievable
An IL CLASS exception type can be constructed via IL ref-type newobj (Step 8b,
available; Step 18 IL-VT newobj is deferred, so use an IL CLASS exception, not
struct). `throw new ILEx(); catch (ILEx)` exercises the `as ILTypeInstance`
sub-branch. If a clean IL-throw is blocked this pass, the fallback
(`TypeForCLR.IsAssignableFrom` sub-branch) is exercisable by catching a
CLR-thrown exception (`1/0`'s `DivideByZeroException`) into an IL catch clause
whose IL type projects to an assignable CLR type. Both sub-branches are
testable.

### Spec reconciliation note
The existing `neo-exceptions` spec requirement "Legacy exception handling is
unchanged" was scoped to the Neo Step 14 port (which did not modify the shared
engine). This change deliberately modifies shared `ILIntepreter.cs` -- the spec
delta MODIFIES that requirement to state the shared-engine touch is
Legacy-neutral (only ADDS a branch unreachable for CLRType catches). No new
capability; extends `neo-exceptions`.

### Status
`openspec status --change implement-neo-catch-complete --json` ->
`isComplete: true`, all artifacts `done`. `openspec validate
implement-neo-catch-complete` -> `is valid`. Apply-ready.

## 9. Apply-stage findings (2026-07-04 apply)

### The fix -- shipped as designed
`CheckExceptionType` (`ILIntepreter.cs:5823-`) NIE replaced with the IL branch
from design.md sec 2 verbatim: `catchType == null` -> true; `catchType is
CLRType` -> unchanged (exact-equal if explicitMatch else `IsAssignableFrom`);
else the NEW branch -- `exception == null` -> false; `exception as
ILTypeInstance` (`exIl.Type == catchType` if explicitMatch else
`exIl.CanAssignTo(catchType)`); else CLR fallback (`catchType.TypeForCLR`
null-guard; exact-equal if explicitMatch else `IsAssignableFrom`). NO
`#if ENABLE_NEO_MODE` gating (shared-engine fix; correct for both engines).
The `exception` arg is already unwrapped by callers; the method does NOT
re-unwrap. +34/-1 lines.

### Testability finding (IMPORTANT -- supersedes sec 8's optimism)
The sec-8 testability note was overly optimistic. In practice an ILType catch
clause is **NOT authorable in a host-compiled test DLL this pass**, on either
engine, for two compounding reasons:
1. **Host compiler:** `catch (T)` is REJECTED by the C# compiler with **CS0155**
   ("catch/throw type must derive from System.Exception") unless `T :
   System.Exception`. A plain IL class is not an Exception, so it cannot be a
   CatchType token.
2. **Runtime load:** declaring the IL class as `class X : System.Exception`
   (which the compiler accepts) then fails to LOAD --
   `TypeLoadException: Cannot find Adaptor for:System.Exception` at
   `ILType.InitializeBaseType` (`ILType.cs:1418`). ILRuntime REQUIRES a
   registered `CrossBindingAdaptor` for any IL type inheriting a CLR
   `Exception`. No such adaptor is registered in the test harness, and adding
   one is out of scope.
3. **Throw path (even if loaded):** the Throw opcode on both engines resolves
   its operand via `mStack[idx] as Exception` (`ILIntepreter.Neo.cs:3159` GetNeoException;
   `ILIntepreter.Register.cs:5310`); a plain IL class is an `ILTypeInstance`,
   not an Exception, so `as Exception` is null -> `NullReferenceException`.
   Throwing an IL instance requires the IL type to BE a CLR Exception (adaptor).

Net: the new IL branch is **unreachable from any host-compiled test today** --
true for every existing catch test (all name a CLRType) AND any new test one
could author. Verification therefore rests on: (a) clean build (Debug_Neo CLI
0 errors; TestCases Debug 0 errors); (b) the shared-engine neutrality argument
(the new branch is unreachable for every CLRType catch, which is all of them);
(c) full NeoStep smoke (no regression). No `NeoCatchCompleteTest.cs` was added
(a draft was written, then removed when it broke the assembly load). A positive
IL-catch test is RESERVED for a future pass that registers a CLR-Exception
CrossBindingAdaptor.

### Neo smoke -- 91/91, no regression
`dotnet run -c Debug_Neo -f net8.0 ... true NeoStep` -> **91/91 pass, 0 fail**,
identical to the 91/91 baseline. The changed method is on the catch-dispatch
hot path; no Neo catch test regressed.

### Legacy neutrality -- confirmed (3 pre-existing failures NOT from this change)
Plain-`Debug` CLI build + `NeoStep14` filter under Legacy: 6 pass / 3 fail. The
3 failures (`ArgumentOutOfRangeException` at `AssignToRegister:5651` via
`ExecuteR:5327`, on the nested / native-fault NeoStep14 tests) are
**PRE-EXISTING**: a `git stash` of the fix and re-run on baseline HEAD
`57e0af54` reproduces the IDENTICAL 3 failures. Those tests catch CLRTypes
(`DivideByZeroException` / `NullReferenceException`), so they enter the
UNCHANGED `catchType is CLRType` arm; the new IL branch is unreachable for them.
The 6 passing Legacy catch tests exercise the unchanged CLRType arm of
`CheckExceptionType` and still pass. No regression attributable to this change.
(These NeoStep14 tests are Neo-targeted and were never green under Legacy --
the Legacy 519-test baseline remains the standing reference.)
