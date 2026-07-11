# Review Report — implement-neo-catch-complete

**Reviewer:** verify-stage (adversarial, author != verifier)
**Date:** 2026-07-04
**Scope:** D-CHECKEX — extend shared `CheckExceptionType` (`ILIntepreter.cs:5823`) for
non-`CLRType` (IL) catch clauses. Shipped diff = +34/-1 in ONE file
(`ILRuntime/Runtime/Intepreter/ILIntepreter.cs`). No test file shipped (deferral adjudicated
below).

---

## Executive verdict: **CLEAN (with one Minor + one follow-up)**

The shipped code is correct, matches `design.md` §2 verbatim, is Legacy-neutral, and closes
the `CheckExceptionType` NIE. There are **0 Blocker**, **0 Major**, **1 Minor**
(documentation nit), and **1 Follow-up** (necessary-but-not-sufficient scope recorded). The
positive IL-catch path is **unreachable from any test today** — a genuine harness limitation,
not a cop-out — so verification rests on the neutrality argument + build + Neo smoke + an
independent stash-proven Legacy-neutrality reproduction (all done).

**Severity counts:** Blocker 0 | Major 0 | Minor 1 | Follow-up 1

---

## What I verified (evidence, not "looks fine")

### A. IL branch construction is correct (Priority 1) — **CLEAN**

`ILIntepreter.cs:5835-5867`. Traced every sub-branch against the actual dependency code:

- **`ILTypeInstance.CanAssignTo` reuses Step 15 infra correctly.** `ILTypeInstance.cs:968`
  delegates to `this.type.CanAssignTo(type)`; `ILType.cs:2232-2263` walks `this == type` →
  `BaseType.CanAssignTo` (IL inheritance chain incl. a `CrossBindingAdaptor` base) →
  `Implements[i].CanAssignTo` (Step 11 interface table). Covers same-type, IL subtype caught
  by IL base, IL type caught by IL interface catch. Confirmed.
- **`explicitMatch` semantics are consistent with the CLRType arm.** CLRType arm (5829-5832)
  does `exception.GetType() == catchType.TypeForCLR` (exact) vs `IsAssignableFrom`. The IL
  branch mirrors both: exact `exIl.Type == catchType` (IL identity) when explicit, else
  `CanAssignTo`; and in the CLR fallback `exception.GetType() == ctClr` vs `IsAssignableFrom`.
  Symmetric and correct.
- **Null-guards are complete.** `if (exception == null) return false;` (5843) before any deref.
  `Type ctClr = catchType.TypeForCLR; if (ctClr == null) return false;` (5862-5864) before
  `IsAssignableFrom`. No NRE path introduced.
- **Ordering is correct.** `as ILTypeInstance` (5845) is tested BEFORE the CLR fallback. A CLR
  exception thrown into an IL catch clause: `as ILTypeInstance` → null → falls to CLR fallback
  (correct). An IL instance (only possible when it IS-A CLR Exception via adaptor): caught by
  the `as ILTypeInstance` arm first (also correct — the IL-type identity is the more precise
  match). Order is right.
- **NO false-match.** A CLR exception vs a plain (non-adaptor) IL catch: `as ILTypeInstance`
  null → CLR fallback → `ctClr == typeof(ILTypeInstance)` (ILType.cs:1186) →
  `IsAssignableFrom(CLR ex type)` → **false** (a CLR `Exception` is not assignable to
  `ILTypeInstance`). Correctly does NOT match.
- **NO missed-match.** An IL instance vs its own IL catch: `CanAssignTo` → `this==type` →
  true. IL subtype vs IL base catch: `BaseType` walk → true. Interface catch: `Implements`
  walk → true.

**Pair-by-pair matrix I traced** (exception arg → catchType → result):

| exception (the `ex` arg at 5602) | catchType | path taken | result | verdict |
|---|---|---|---|---|
| CLR `DivideByZeroException` | CLRType `Exception` | unchanged arm 5832 | true | unchanged ✓ |
| CLR exception, plain-IL catch | ILType | `as ILTypeInstance` null → fallback → `IsAssignableFrom` false | false | correct, no false-match ✓ |
| `ILTypeInstance` (own type) | ILType same | `CanAssignTo` → `this==type` true | true | correct ✓ |
| `ILTypeInstance` subtype | IL base catch | `CanAssignTo` → `BaseType` walk true | true | correct ✓ |
| `ILTypeInstance` unrelated | unrelated IL catch | `CanAssignTo` false | false | correct, no false-match ✓ |
| null | ILType | guard 5843 → false | false | correct, no NRE ✓ |
| null | CLRType | unchanged arm 5830 `exception.GetType()` | **NRE** | pre-existing, NOT introduced (see finding F1) |

### B. Test-deferral is legitimate (Priority 2) — **CLEAN**

Independently verified all 3 blockers the implementer cited:

1. **CS0155** — `catch (T)` requires `T : System.Exception`. A plain IL class is not an
   Exception → host C# compiler rejects the catch token. Real.
2. **TypeLoadException on `class X:System.Exception`** — `ILType.cs:1418` throws
   `TypeLoadException("Cannot find Adaptor for:System.Exception")` at `InitializeBaseType`
   when no `CrossBindingAdaptor` for `System.Exception` is registered. The harness registers
   none. Real.
3. **Throw `as Exception` NRE** — **both engines confirmed by direct read:**
   - Legacy `ILIntepreter.Register.cs:5310`: `var ex = mStack[objRef->Value] as Exception;
     throw ex;` — a plain IL class is an `ILTypeInstance`, `as Exception` is null, `throw ex`
     throws `NullReferenceException` (not the IL instance).
   - Neo `ILIntepreter.Neo.cs:3159-3161`: `GetNeoException` does
     `Exception ex = mStack[objIndex] as Exception; if (ex == null) throw new
     NullReferenceException();` — explicit NRE on a non-Exception IL instance.

Net: the new IL branch is **genuinely unreachable** from any host-compiled test (the `ex` arg
at `CheckExceptionType` is `obj as Exception` at `ILIntepreter.cs:5602`; a plain IL instance
yields `null` there → hits the new `exception == null` guard → false, never the
`CanAssignTo` arm). The deferral is a **legitimate harness limitation**, not a cop-out. A
positive IL-catch test requires the Exception `CrossBindingAdaptor` infra + Throw handling for
IL exceptions — separate work (recorded as follow-up F2).

### C. D-CHECKEX scope is partial-close (Priority 3) — necessary-but-not-sufficient (Follow-up F2)

This fix closes the `CheckExceptionType` NIE specifically. End-to-end IL-exception catch still
needs: (a) an Exception `CrossBindingAdaptor` for IL `class X:Exception` (load-time), and
(b) Throw handling so an IL instance is thrown as itself (both engines' `as Exception` NRE).
This change is **necessary but not sufficient** for the user-facing feature. **Worth
shipping alone:** it is correct, Legacy-neutral, closes the NIE, and removes a latent crash on
a shared hot path. Deferring a correct, zero-regression fix until a separate infra task lands
would leave a known crash in place for no benefit. **Verdict: ship it; track F2.**

### D. Legacy-neutrality + regression (Priority 4) — **CLEAN (independently reproduced)**

- **Neo smoke (Debug_Neo):** `91 ran, 0 failed` — matches 91/91 baseline. Zero Neo regression.
- **Legacy NeoStep14 filter (plain Debug):** `9 ran, 3 failed`. I **independently** confirmed
  the 3 are pre-existing by `git stash`-ing the fix, rebuilding, and re-running on baseline
  HEAD `57e0af54`: **identical 3 failed**. Restored the fix (back to +34/-1). The 3 failures
  (`ArgumentOutOfRangeException` at `AssignToRegister:5651` via `ExecuteR:5327`) are
  Neo-targeted tests that were never green under Legacy. Not caused by this change.
- **Unreachability for CLRType catches:** confirmed the new branch runs ONLY when
  `!(catchType is CLRType)`. Every existing catch test names a CLRType (grep of `TestCases/*.cs`
  found zero non-Exception catch types) → all enter the byte-for-byte unchanged arm at 5827.
  Legacy catch behavior is byte-identical.

### E. Spec reconciliation (Priority 5) — Minor (F1)

The change's MODIFIED delta reworks "Legacy exception handling is unchanged" to note the
shared touch is Legacy-neutral (only ADDS a branch unreachable for CLRType catches). The
substance is **accurate**. One imprecision: the **canonical** `openspec/specs/neo-exceptions/
spec.md:166-172` currently lists `HandleException` / `GetCorrespondingExceptionHandler` /
`FindExceptionHandlerByBranchTarget` as the "shared handler-matching engine" that "SHALL NOT
modify", and does NOT name `CheckExceptionType` — yet `CheckExceptionType` is a helper called
by `GetCorrespondingExceptionHandler` (line 5609) and this change modifies it. When the delta
archives and merges, the canonical list should name `CheckExceptionType` explicitly (or soften
"as-is" → "only the non-CLRType branch is added, unreachable for CLRType catches"). Minor doc
nit; does not affect behavior.

---

## Findings

### F1 — Minor — `openspec/specs/neo-exceptions/spec.md:166-172` + change delta
**What:** The canonical "Legacy exception handling is unchanged" requirement enumerates the
shared engine methods but omits `CheckExceptionType`, which this change modifies. The change's
MODIFIED delta rewording is correct in substance but does not name `CheckExceptionType`
either.
**Why:** A future reader of the canonical spec will see "Neo reuses the shared engine as-is"
which is now slightly imprecise (one predicate method gained an IL branch).
**Fix:** When archiving (merging the delta into the canonical spec), add `CheckExceptionType`
to the method list and state the addition is unreachable for CLRType catches. (No action on
shipped code.)

### F2 — Follow-up (not a defect of this change) — end-to-end IL-exception catch
**What:** Closing the `CheckExceptionType` NIE is necessary but not sufficient to actually
catch an IL-typed exception end-to-end. Remaining blockers:
1. Register a CLR `System.Exception` `CrossBindingAdaptor` so IL `class X:System.Exception`
   loads (`ILType.cs:1418`).
2. Throw handling for IL exceptions: both `GetNeoException` (`ILIntepreter.Neo.cs:3159`) and
   the Legacy Throw arm (`ILIntepreter.Register.cs:5310`) do `... as Exception`, NRE-ing on a
   plain IL instance.
**Why:** Until these land, the new `CanAssignTo` arm in `CheckExceptionType` is inert (the IL
instance never reaches it as a non-null arg). Reserve a positive IL-catch test for that pass.
**Fix:** Separate change / future step. Track as a deferred Neo item (suggest
`D-IL-EXCEPTION-THROW`).

---

## Pre-existing observation (NOT introduced, out of scope, informational only)

The unchanged CLRType arm (`ILIntepreter.cs:5830`, `exception.GetType()`) would NRE if a null
`exception` ever reached it. The new IL branch's `if (exception == null) return false;`
(5843) is *more* defensive than the CLRType arm. The null-exception path can only arise if
`obj as Exception` at line 5602 yields null (a non-Exception object reaching the catch
matcher), which today is the unreachable IL-throw case. Not a regression; not in scope;
flagged for completeness.

---

## Verdict summary

- **(a) IL-branch construction:** **CORRECT.** No false-match, no missed-match across all
  traced (exception, catchType) pairs; null-guards complete; ordering correct; `explicitMatch`
  symmetric with the CLRType arm; reuses Step 15 `CanAssignTo` correctly.
- **(b) Test-deferral:** **LEGITIMATE.** All 3 blockers independently verified (CS0155,
  TypeLoadException, both engines' Throw `as Exception` NRE). The branch is unreachable from
  any host-compiled test. Genuine harness limitation.
- **(c) Ship-the-CheckExceptionType-piece-alone:** **WORTHWHILE.** Correct + Legacy-neutral
  (stash-proven) + closes a latent shared-engine crash on the catch hot path; deferring a
  zero-regression fix until a separate infra task lands would leave a known crash in place.

**Ship it.** Address F1 wording at archive time; track F2 as follow-up work.
