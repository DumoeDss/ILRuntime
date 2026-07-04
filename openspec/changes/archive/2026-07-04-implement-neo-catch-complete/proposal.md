## Why

The shared engine's catch-type matcher, `CheckExceptionType`
(`ILIntepreter.cs:5823-5836`), throws `NotImplementedException` for any catch
clause whose `CatchType` is not a `CLRType` -- i.e. a catch clause naming an
**IL-defined type** (an `ILType`). Because Neo catch dispatch calls into this
shared matcher (`HandleException` -> `GetCorrespondingExceptionHandler` ->
`CheckExceptionType`), a thrown exception can never be matched against an
`ILType` catch clause; it instead throws NIE (deferred Neo item **D-CHECKEX**).
This closes the last deferred item from the catch-complete sequence (Step 15's
isinst/`CanAssignTo` landed the assignability primitive this fix needs).

## What Changes

- Extend `CheckExceptionType` with a branch for a non-`CLRType` catch type.
  For an `ILType` catch type, determine assignability of the thrown exception
  to that IL type (reusing Step 15's `ILTypeInstance.CanAssignTo` /
  `ILType.CanAssignTo` and the CLR `IsAssignableFrom` fallback) instead of
  throwing NIE.
- The check must handle both thrown-object shapes that reach the matcher:
  - a CLR `Exception` thrown natively (e.g. `DivideByZeroException` from `1/0`,
    or a CLR `Exception` thrown from a host/binding method) -- matched by
    querying the IL catch type's CLR projection (`TypeForCLR`) for CLR
    assignability, OR by resolving the thrown CLR type to its IL type when the
    thrown exception is an IL-thrown-then-wrapped object; and
  - an `ILTypeInstance` (an IL-thrown exception whose runtime object is the IL
    instance) -- matched via `ILTypeInstance.CanAssignTo(catchType)`.
- The NIE is replaced by a defined `bool` result, so the caller
  (`GetCorrespondingExceptionHandler`) gets a yes/no for IL catch types and the
  nearest-handler search proceeds normally.

No new opcodes, no JIT change, no `ExecuteNeo` change. The change is confined to
the single shared method `CheckExceptionType`.

## Decision: SHARED-engine fix, not Neo-only

This is a **shared-engine gap fix**. `CheckExceptionType` lives in
`ILIntepreter.cs` and is reached identically by **both** `ExecuteR` (Legacy) and
`ExecuteNeo`: both interpreters wrap their dispatch loop in a per-iteration C#
try-catch that calls the shared `HandleException`
(`ILIntepreter.Register.cs:5323` for Legacy; `ILIntepreter.Neo.cs:~2050` for
Neo), which calls `GetCorrespondingExceptionHandler` (`ILIntepreter.cs:5609`),
which calls `CheckExceptionType`. **Legacy has the SAME NIE gap** -- it never
handled ILType catch clauses either (both engines hit line 5835). So:

- This is NOT a Neo-only behavior change. It is a long-standing shared gap.
- The fix MUST be correct for BOTH engines (Legacy is the reference). It is NOT
  gated behind `#if ENABLE_NEO_MODE`.
- The "Legacy is unchanged" requirement in the existing `neo-exceptions` spec
  is scoped to the Neo Step 14 port (which did not modify the shared engine).
  This change deliberately modifies the shared engine -- both engines gain IL
  catch support; neither regresses (a `bool` replacing an NIE can only change
  `throw`-into-NIE outcomes into matches-or-no-match outcomes, which is the
  intended new behavior; every existing CLR-type catch test exercises the
  unchanged `CLRType` branch).

## Legacy-neutrality argument

- **CLRType catch clauses (the existing, tested path) are untouched.** The new
  branch is reached ONLY when `!(catchType is CLRType)`. Every Legacy + Neo
  catch test in the suite today uses a `CLRType` catch (`DivideByZeroException`,
  `Exception`, `NullReferenceException`), so those keep hitting the original
  `IsAssignableFrom` logic verbatim -- zero behavior delta for them.
- **The removed NIE was never a valid outcome.** Before this fix, an ILType
  catch clause threw NIE on BOTH engines (a crash, never a pass). After the
  fix it returns a `bool`. There is no previously-passing Legacy behavior that
  depends on the NIE, so nothing regresses.
- **Gate:** validate with a Legacy catch smoke filter (plain `Debug` build) +
  the existing 519-test Legacy baseline reference, in addition to the full Neo
  NeoStep smoke.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `neo-exceptions`: ADDED requirement -- `CheckExceptionType` SHALL accept a
  non-`CLRType` (IL) catch type and resolve it via `CanAssignTo` instead of
  throwing NIE. This extends the Step 14 catch capability to IL-typed catch
  clauses. (Note: although the requirement lives under the Neo `neo-exceptions`
  capability, the implementation is in shared `ILIntepreter.cs` and applies to
  Legacy too -- see decision above.)

## Impact

- **Code:** one method, `ILIntepreter.CheckExceptionType`
  (`ILIntepreter.cs:5823-5836`). No JIT, no `ExecuteNeo` arm, no object-model
  change. Reuses `ILTypeInstance.CanAssignTo` (`ILTypeInstance.cs:968`) /
  `ILType.CanAssignTo` (`ILType.cs:2232`) + `CLRType.TypeForCLR.IsAssignableFrom`.
- **Hot path:** `CheckExceptionType` runs once per `(handler, thrown-exception)`
  candidate during catch dispatch on BOTH engines. The added branch is a single
  `is CLRType` test that CLR-type catches already fall through; the new work
  runs only for IL catch types (rare). Negligible cost.
- **Regression surface:** every catch clause (Neo + Legacy). Gates: full NeoStep
  smoke (Neo) + a Legacy catch filter + the 519-test Legacy baseline.
- **Dependencies:** Step 15 (isinst/`CanAssignTo`) -- already landed.
- **Tests:** add `TestCases/NeoCatchCompleteTest.cs` (or extend
  `NeoStep14Test.cs`). Cover an IL catch type. A pure IL-throw+IL-catch needs an
  IL exception type constructible via IL class newobj (Step 8b ref-type newobj,
  available; Step 18 IL-VT newobj is deferred -- use an IL CLASS exception). If
  a clean IL-throw is not achievable this pass, fall back to a CLR-thrown
  exception caught by an IL catch clause that the IL type maps from (the catch
  matches via the CLR assignability fallback) -- both paths exercise the new
  branch.
