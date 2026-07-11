# neo-exceptions

## ADDED Requirements

### Requirement: IL-typed catch clauses are matched, not NIE

The matcher SHALL select a catch clause naming an IL type when the thrown
exception is assignable to it. The shared catch-type matcher `CheckExceptionType`
(in `ILIntepreter.cs`, used by
BOTH the Neo `ExecuteNeo` and Legacy `ExecuteR` paths via the shared
`HandleException` / `GetCorrespondingExceptionHandler`) SHALL accept a catch
clause whose `CatchType` is a non-`CLRType` (an `ILType`) and return a defined
boolean match result, instead of throwing `NotImplementedException`.

For an IL catch type, the matcher SHALL determine assignability of the thrown
exception to that IL type by the runtime shape of the (already-unwrapped)
exception object:

- If the exception object is an `ILTypeInstance`, the matcher SHALL return
  whether `ILTypeInstance.CanAssignTo(catchType)` is true (exact-`ILType`
  identity when the search is in explicit-match mode). This covers IL subtype
  caught by IL base, and an IL type caught by an IL interface catch clause (via
  the `Implements` walk).
- Otherwise (a CLR `Exception`), the matcher SHALL return whether the IL catch
  type's `TypeForCLR` is assignable from the thrown exception's CLR type (exact
  CLR-type equality in explicit-match mode).

This requirement SUPERSEDES the prior behavior in which a non-`CLRType` catch
type unconditionally threw `NotImplementedException` (deferred Neo item
**D-CHECKEX**). The `catchType == null` (catch-all) and `catchType is CLRType`
arms remain unchanged.

This requirement is placed under the `neo-exceptions` capability (which governs
Neo catch dispatch), but the implementation is in shared `ILIntepreter.cs` and
applies to BOTH Neo and Legacy: it fixes a long-standing shared-engine gap, so
Legacy gains IL catch support too, with no change to any `CLRType`-catch
behavior (the new branch is unreachable for CLRType catch clauses).

#### Scenario: an IL-defined exception caught by its own IL type

- **WHEN** an IL method (under either `ENABLE_NEO_MODE` or the Legacy engine)
  defines an IL class exception type, throws an instance of it inside a `try`
  region, and the method has a `catch` clause naming that same IL type
- **THEN** the catch clause is selected by `GetCorrespondingExceptionHandler`
  (via `CheckExceptionType` returning true) and the catch body executes,
  instead of the dispatch throwing `NotImplementedException`.

#### Scenario: an IL subtype exception caught by an IL base catch clause

- **WHEN** an IL method throws an instance of an IL exception type that derives
  (in the IL type system) from another IL type, and the method has a `catch`
  clause naming the base IL type
- **THEN** the base catch clause matches via the `CanAssignTo` base-type walk
  and the catch body executes.

#### Scenario: a non-matching IL catch clause does not falsely match

- **WHEN** an IL method throws an exception (IL or CLR) and the method has a
  `catch` clause naming an UNRELATED IL type
- **THEN** that catch clause does NOT match (`CheckExceptionType` returns
  false), and the exception-propagation search continues to the next handler
  or the finally/unhandled path -- no false positive match.

#### Scenario: CLR-type catch clauses are unchanged

- **WHEN** a catch clause names a `CLRType` (e.g. `DivideByZeroException`,
  `System.Exception`)
- **THEN** the matcher uses the original `catchType.TypeForCLR.IsAssignableFrom`
  (or exact-equality in explicit-match mode) logic, byte-for-byte unchanged,
  regardless of whether the engine is Neo or Legacy -- no behavioral regression
  for any existing CLRType-catch test.

## MODIFIED Requirements

### Requirement: Legacy exception handling is unchanged

This change SHALL NOT modify the Legacy interpreter path (`ExecuteR` in
`ILIntepreter.Register.cs`). It DOES modify the shared handler-matching engine
(`CheckExceptionType` in `ILIntepreter.cs`), but only by ADDING a branch for
non-`CLRType` catch types that was previously an unconditional
`throw new NotImplementedException()`. This addition is Legacy-neutral: every
`CLRType` catch clause (which is what every existing Legacy catch test uses)
still enters the unchanged `catchType is CLRType` arm, so Legacy catch behavior
is preserved. Legacy gains IL-typed catch support as a side effect (the prior
NIE was never a passing outcome on either engine).

#### Scenario: Legacy CLRType-catch tests are byte-for-byte preserved

- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and Legacy catch tests
  (which all use CLRType catch clauses) are run
- **THEN** every Legacy catch test continues to pass unchanged, because the
  `CLRType` arm of `CheckExceptionType` is unmodified and the new IL branch is
  unreachable for CLRType catch clauses.
