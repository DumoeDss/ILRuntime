## MODIFIED Requirements

### Requirement: Throw opcode executes under Neo

The `Throw` `ExecuteNeo` arm SHALL read the exception object from the throw
instruction's register-1 ref slot (the mStack index stored at
`frameBase + ip->Register1`) and throw it into the enclosing per-iteration C#
try-catch so that `HandleException` searches the current frame's exception
handler table (`method.ExceptionHandlerRegister`). Throwing a null exception
object (ref index `-1`) SHALL throw a CLR `NullReferenceException`.

When the throw operand is an `ILTypeInstance` that is not directly a CLR
`Exception` (an IL-defined exception whose IL type inherits `System.Exception`
via a registered `CrossBindingAdaptor`), the arm SHALL obtain the CLR exception
by reading the instance's `CLRInstance` (the adaptor's `Adapter`, which IS-A
`System.Exception`) and throw THAT, instead of producing a null `Exception`.
This unwrap SHALL apply symmetrically to the Legacy `Throw` arm in `ExecuteR`
(`ILIntepreter.Register.cs`), which has the identical `mStack[idx] as Exception`
gap; the fix is shared-engine (the `CrossBindingAdaptors` registration and the
`ILTypeInstance.CLRInstance` bridge are engine-agnostic). If, after the unwrap
attempt, the object is still not an `Exception` (a non-exception IL instance),
the arm SHALL throw a CLR `NullReferenceException` (throwing a non-exception
object is invalid).

For any throw operand that is already a CLR `Exception` (the existing case:
CLR-raised exceptions from arithmetic / null-deref / host bindings), the arm
SHALL behave byte-for-byte as before -- the unwrap fallback is unreachable.

#### Scenario: An exception thrown inside a try block is offered to the frame's handlers
- **WHEN** an IL method under `ENABLE_NEO_MODE` executes an instruction inside a
  `try` region that throws (for example `int x = 1 / 0;`, whose host-raised
  `DivideByZeroException` enters the outer catch), and the method has a matching
  catch handler
- **THEN** `HandleException` is invoked against the current frame's handler
  table with the throw address inside the try range, the handler is found, and
  execution jumps to the handler start -- not to the method's `default`/
  `NotImplementedException` path.

#### Scenario: An IL-typed exception is thrown and caught by an IL catch clause

- **WHEN** an IL method (under EITHER `ENABLE_NEO_MODE` or the Legacy engine)
  defines `class MyEx : System.Exception`, constructs and throws an instance of
  it (`throw new MyEx();`) inside a `try` region, and the method has a
  `catch (MyEx)` clause naming that same IL type
- **THEN** the `Throw` arm unwraps the throw operand's `CLRInstance` to obtain
  the CLR `Exception` (the registered `System.Exception` adaptor's `Adapter`
  instance), throws it into the outer catch, and the `catch (MyEx)` clause is
  selected by `CheckExceptionType` -- instead of the throw producing a
  `NullReferenceException` because the operand is an `ILTypeInstance` rather
  than a CLR `Exception`.

#### Scenario: An IL-typed exception thrown by a CLR-base catch clause

- **WHEN** an IL method throws an instance of an IL `class MyEx :
  System.Exception` and the method has a `catch (System.Exception e)` clause
- **THEN** the catch clause is selected (the thrown object's CLR projection is
  the adaptor's `Adapter`, which IS-A `System.Exception`, so the CLRType
  `IsAssignableFrom` arm of `CheckExceptionType` matches), proving the Throw
  arm's `CLRInstance` unwrap produced a real CLR `Exception`.

#### Scenario: A CLR exception throw is unchanged by the IL-instance unwrap

- **WHEN** an IL method throws a CLR `Exception` that is NOT an `ILTypeInstance`
  (for example the `DivideByZeroException` from `1 / 0`, or a CLR exception
  thrown from a host/binding method)
- **THEN** the `Throw` arm's first `as Exception` succeeds and the
  `ILTypeInstance.CLRInstance` fallback is NOT executed, so the existing CLR-
  exception throw behavior is byte-for-byte preserved on BOTH engines.

#### Scenario: Throwing a null exception remains a NullReferenceException

- **WHEN** the throw operand's ref index is `-1` (a null exception object)
- **THEN** the `Throw` arm throws a CLR `NullReferenceException` (unchanged),
  on BOTH engines, regardless of whether the IL-instance unwrap fallback exists.

## ADDED Requirements

### Requirement: An IL class inheriting System.Exception loads and is throwable

A `System.Exception` `CrossBindingAdaptor` SHALL be registered by default in
every `AppDomain` (alongside the existing built-in `AttributeAdapter`), so that
an IL-defined class that inherits `System.Exception`
(e.g. `class MyEx : System.Exception {}`) loads without throwing
`TypeLoadException`. The adaptor's nested `Adapter` class SHALL inherit
`System.Exception` and implement `CrossBindingAdaptorType`, so that an IL
exception instance's `CLRInstance` (established in the `ILTypeInstance`
constructor via `FirstCLRBaseType.CreateCLRInstance`) IS-A CLR `Exception`.
This registration is engine-agnostic (the `CrossBindingAdaptors` dictionary is
shared by `ExecuteNeo` and `ExecuteR`) and is the prerequisite that makes the
Throw arm's `CLRInstance` unwrap (see the modified Throw requirement) produce a
real CLR `Exception`.

This is the load-time half of closing deferred Neo item
**D-IL-EXCEPTION-THROW** (the CATCH-COMPLETE `CheckExceptionType` IL branch was
necessary-but-not-sufficient; without this adaptor the IL exception class
cannot be loaded at all).

#### Scenario: An IL class inheriting System.Exception loads

- **WHEN** an IL assembly containing `class MyEx : System.Exception {}` is
  loaded into an `AppDomain` (under EITHER `ENABLE_NEO_MODE` or the Legacy
  engine)
- **THEN** the type resolves successfully (its `BaseType` is the registered
  `System.Exception` `CrossBindingAdaptor`), instead of `ILType.cs` throwing
  `TypeLoadException("Cannot find Adaptor for:System.Exception")`.

#### Scenario: An IL exception instance carries a CLR Exception projection

- **WHEN** an IL method constructs `new MyEx()` for an IL `class MyEx :
  System.Exception` and the runtime instantiates the `ILTypeInstance`
- **THEN** the instance's `CLRInstance` is the adaptor's `Adapter` object (a
  CLR `System.Exception` subclass), so that the Throw arm can obtain a real
  `Exception` from it via `CLRInstance`.

#### Scenario: The Exception adaptor is registered by default

- **WHEN** a fresh `AppDomain` is constructed (no test-harness adaptor
  registration)
- **THEN** the `System.Exception` `CrossBindingAdaptor` is present in
`AppDomain.CrossBindingAdaptors` (mirroring the existing `AttributeAdapter`
  built-in), so any consumer (test harness, Unity host, AOT loader) can load
  and throw IL exception types without registering an adaptor first.

#### Scenario: An IL exception subtype inherits the Exception adaptor

- **WHEN** an IL class derives from another IL exception class
  (`class DerivedEx : MyEx` where `MyEx : System.Exception`)
- **THEN** the subtype loads and is throwable/caught end-to-end (its IL base
  chain resolves to the `System.Exception` adaptor via the existing
  `ILType.CanAssignTo` base-type walk), proving the adaptor supports the
  derived-before-base catch ordering.
