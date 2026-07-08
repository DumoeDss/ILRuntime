# neo-exceptions

Structured exception handling for IL code executed by the Neo register VM
(`ENABLE_NEO_MODE`): `throw`, `catch`, `finally`, `leave`, and `rethrow`, with
correct nesting and cross-frame propagation. This capability is implemented in
`ExecuteNeo` (`ILIntepreter.Neo.cs`) and reuses the shared handler-matching
engine (`HandleException` / `GetCorrespondingExceptionHandler` /
`FindExceptionHandlerByBranchTarget` in `ILIntepreter.cs`) that Legacy
(`ExecuteR`) already uses; it does NOT modify Legacy or the shared engine.

This capability is distinct from `neo-dispatch` (vtable/interface) and
`neo-value-types`: it governs control-flow unwinding and per-frame mStack/frames
discipline on the Neo `byte*` frame model, not dispatch or value storage.

IL `filter` blocks and `endfilter` are explicitly out of scope (rare in C#;
remain `NotImplementedException`). CLR-typed `throw new T(...)` is bounded by the
availability of CLR `newobj` (a separate Step 18 concern); this capability is
specified and validated using exception sources that do not require newobj
(CLR-raised exceptions from arithmetic/null-deref, rethrow of a caught object).

## Requirements

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

### Requirement: Catch-handler entry restores mStack and stores the exception object

`ExecuteNeo` SHALL, on entry to a catch handler (`isCatch == true` from
`HandleException`), truncate `mStack.Count` to `frameRefBase + totalRefSize`
(discarding any temporary ref slots pushed inside the faulted try region) AND
SHALL install the caught exception object into the catch handler's reserved
reference slot so the catch body can read the exception variable. The exception
object placed in the slot SHALL be the unwrapped inner exception when the
propagated object was an `ILRuntimeException` (matching Legacy behavior).

#### Scenario: The caught exception is reachable inside the catch body
- **WHEN** an IL method under `ENABLE_NEO_MODE` catches an `Exception e` thrown
  by `int x = 1 / 0;` and the catch body inspects `e` (for example tests
  `e is DivideByZeroException`)
- **THEN** the catch body observes the same exception instance that was thrown
  (the `is`/cast check succeeds), proving the exception object was correctly
  stored in the catch handler's ref slot.

#### Scenario: mStack does not leak across a caught exception
- **WHEN** an IL method under `ENABLE_NEO_MODE` repeatedly enters a try region
  that pushes temporary reference slots, throws, and catches
- **THEN** `mStack.Count` is restored to `frameRefBase + totalRefSize` on each
  catch entry and the method can be called many times without `mStack` growing
  without bound.

### Requirement: Leave executes enclosing finally blocks

The `Leave` / `Leave_S` `ExecuteNeo` arm SHALL jump to the leave target
(`ip->Operand`) but SHALL first divert into any finally block whose try range
encloses the leave instruction's address and is exited by the leave (determined
via `FindExceptionHandlerByBranchTarget`), recording the leave target in
`finallyEndAddress` so the subsequent `Endfinally` resumes at the leave target.
This matches Legacy `Leave` semantics and guarantees that a finally block always
runs when control leaves its try region, whether by normal control flow or by
leaving on account of a `return`/`break`/`continue`.

#### Scenario: finally runs on a normal Leave with no exception
- **WHEN** an IL method under `ENABLE_NEO_MODE` runs `int r = 0; try { r = 1; }
  finally { r += 10; }` (no exception is thrown)
- **THEN** the finally body executes and the method yields `11`, proving the
  `Leave` arm routed control through the finally before reaching the leave
  target.

### Requirement: Endfinally re-propagates or resumes correctly

The `Endfinally` `ExecuteNeo` arm SHALL behave according to why the finally was
entered. When the finally was entered because an exception is in flight
(`finallyEndAddress < 0`, set by `HandleException` for finally/fault), the arm
SHALL re-throw `lastCaughtEx` so the exception-propagation search resumes from
this frame. When the finally was entered because of a `Leave`
(`finallyEndAddress >= 0`), the arm SHALL jump to the recorded leave target
(routing through any further enclosing finally via
`FindExceptionHandlerByBranchTarget`) and reset `finallyEndAddress` to 0.

#### Scenario: try-finally runs the finally then propagates an uncaught exception
- **WHEN** an IL method under `ENABLE_NEO_MODE` runs `try { int x = 1 / 0; }
  finally { sideEffect(); }` with no enclosing catch in the same method
- **THEN** the finally body executes (`sideEffect` runs) and the
  `DivideByZeroException` continues to propagate (it is not swallowed by the
  absence of a catch).

#### Scenario: nested finally blocks run outermost on leave
- **WHEN** a `Leave` crosses both an inner and an outer finally region
- **THEN** both finally bodies run (inner then outer) before control reaches the
  leave target, via successive `Endfinally` diversions.

### Requirement: Rethrow re-raises the in-flight exception

The `Rethrow` `ExecuteNeo` arm SHALL throw the current frame's `lastCaughtEx`,
re-entering the outer catch so the exception propagates as if originally thrown
at the catch site.

#### Scenario: rethrow propagates to an outer handler
- **WHEN** an IL method under `ENABLE_NEO_MODE` catches an exception, executes
  `rethrow`, and an outer try/catch surrounds the inner
- **THEN** the outer catch observes the same exception instance and the inner
  catch's post-rethrow code does not run.

### Requirement: Cross-frame propagation offers the exception to caller handlers

The Neo caller frame SHALL, when a callee IL method invoked from a `Call` /
`Newobj` / `Callvirt` /
`Callvirt_Interface` / `Callvirt_CLR` arm finishes with an unhandled exception
(the callee's `ExecuteNeo` set `unhandledException = true`), the CALLER frame
SHALL offer that exception to the caller's own exception handler table rather
than silently unwinding. Concretely, the caller SHALL re-enter its outer
try-catch with the pending exception object and the call-site instruction
address, so `HandleException` searches the caller's `method.ExceptionHandlerRegister`
at the call address (which lies inside any caller-side enclosing try range). The
caller SHALL NOT execute a `return null` that bypasses both the caller's
handlers and the caller's frame/mStack cleanup.

#### Scenario: an exception thrown in a callee is caught in the caller
- **WHEN** an IL method `Caller` under `ENABLE_NEO_MODE` contains
  `try { Callee(); return -1; } catch { return 9; }` and `Callee` executes
  `int x = 1 / 0;` (no local catch)
- **THEN** `Caller` returns `9`, proving the exception propagated across the
  call boundary and was matched against `Caller`'s handler table.

#### Scenario: a caller-side finally runs when a callee throws
- **WHEN** `Caller` has `try { Callee(); } catch { } finally { sideEffect(); }`
  and `Callee` throws
- **THEN** the caller-side catch runs AND the caller-side finally runs (the
  caller frame's try/finally state is intact, not corrupted by the cross-frame
  unwind).

### Requirement: Frame and mStack state stay consistent across unwinds

`ExecuteNeo` SHALL, on every exit path -- normal `Ret`, caught-and-resumed
execution, and unhandled-exception re-throw -- leave the shared
runtime state consistent: the current frame is popped from the frames stack
exactly once, and `mStack.Count` is truncated back to the entry `frameRefBase`.
A thrown exception that propagates out of `ExecuteNeo` SHALL NOT leave the
current frame on the frames stack or leave the frame's reserved mStack region
allocated. The shared `HandleException` frame-pop loop remains the authority for
batch-popping intervening frames when a handler is found in an ancestor frame.

#### Scenario: a method that throws unhandled leaves no frame/mStack leak
- **WHEN** an IL method under `ENABLE_NEO_MODE` throws an exception that is not
  caught anywhere and is allowed to escape to the host
- **THEN** after the escape the frames stack and `mStack.Count` are back to the
  state before the method was entered (no leaked frame, no leaked reserved ref
  slots), so subsequent invocations are unaffected.

### Requirement: Legacy exception handling is unchanged

This change SHALL NOT modify the Legacy interpreter path (`ExecuteR` in
`ILIntepreter.Register.cs`). It DOES touch the shared handler-matching engine
in `ILIntepreter.cs` -- specifically `CheckExceptionType`, the predicate called
by `GetCorrespondingExceptionHandler` -- but only by ADDING a branch for
non-`CLRType` (IL) catch types that was previously an unconditional
`throw new NotImplementedException()`. This addition is Legacy-neutral: every
`CLRType` catch clause (which is what every existing Legacy catch test uses)
still enters the unchanged `catchType is CLRType` arm, so the new IL branch is
unreachable for any CLRType catch and Legacy catch behavior is preserved.
(`HandleException`, `GetCorrespondingExceptionHandler`, and
`FindExceptionHandlerByBranchTarget` remain unmodified; Neo reuses the shared
engine as-is.)

#### Scenario: Legacy behavior is byte-for-byte preserved
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE`
- **THEN** all pre-existing Legacy exception-handling tests continue to pass
  unchanged (no behavioral regression in `ExecuteR`), because the `CLRType` arm
  of `CheckExceptionType` is unmodified and the new IL branch is unreachable
  for CLRType catch clauses.

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

### Requirement: ILTypeInstance field indexer reads IL-declared fields under Neo

Under `ENABLE_NEO_MODE`, the `ILTypeInstance.this[int index]` indexer `get` SHALL
return the value of the IL-declared field at `index`, reading from the Neo object
model (`byte[] Primitives` for primitive/enum fields; `AutoList ManagedObjects`
for reference fields and boxed CLR-struct fields), and SHALL NOT return `null` for
a populated field. The indexer is the standard CLR-side bridge for reading an
IL-declared field off a recovered `ILTypeInstance` (a direct cast to the IL type
is impossible -- the two are unrelated CLR types -- so field read-off a recovered
instance goes through the indexer; this is the path generated
cross-binding-adaptor property forwarders and host reflection use).

The indexer `get` SHALL gate IL-field vs CLR-inherited by
`index >= 0 && index < type.TotalFieldCount` (the analogue of the Legacy
`index < fields.Length` gate; under Neo `fields` is the primitive BYTE array, so
the byte-length MUST NOT be used as the field-count gate), resolve the field
offset via `type.GetFieldOffset(index)` and the field type via
`type.GetField(index, ...)` (both recurse through the IL base-type chain), and
dispatch on the field's TypeForCLR: primitive fields SHALL be read from
`Primitives` at the field's `PrimitiveOffset` by width and boxed; enum fields
SHALL be read as the underlying primitive and boxed as the enum; reference fields
SHALL be read from `ManagedObjects[ReferenceOffset]`; CLR-struct fields of an IL
instance (the `neo-clrstruct-field-of-il` layout, stored boxed at
`ManagedObjects[ReferenceOffset]`) SHALL be returned from that slot. An index in
the CLR-inherited range SHALL fall through to the existing `FirstCLRBaseType`
branch (`clrType.GetFieldValue(index, clrInstance)`), byte-identical to Legacy.
An IL value-type field (reconstruction required) SHALL throw a TAGGED
`NotImplementedException` rather than return wrong data (accepted-known edge;
rare for exception types). The `set` arm SHALL mirror `get` for the supported
shapes. The Legacy `get`/`set` arms (the `StackObject[] fields` path) SHALL be
byte-for-byte unchanged.

#### Scenario: Reading an IL-declared field off a caught exception via the indexer
- **WHEN** an IL method under `ENABLE_NEO_MODE` catches an IL-defined exception,
  recovers its `ILTypeInstance` via the `CrossBindingAdaptorType.ILInstance`
  bridge, and reads an IL-declared reference/primitive field through the
  `ILTypeInstance` indexer
- **THEN** the indexer SHALL return the field's actual value (the value the
  constructor / field-set stored), and SHALL NOT return `null` for a populated
  non-null field
- (On HEAD `b0041e74` the indexer `get` returns `null` unconditionally under
  `ENABLE_NEO_MODE`. ADVERSARIAL PROBE: `NeoStep14_ILEx_IndexerFieldRead` -- throw
  `new MyEx("idx-msg")`, recover `ili`, read the `Msg` field through a bound CLR
  helper via `ili[fieldIndex]`, assert the value equals `"idx-msg"`.)

## NOTES (F-4 / NEO-IL-EX-FIELDACCESS resolution 2026-07-08, apply overturned the design's #2 verdict)

- **Path #1 (`((CrossBindingAdaptorType)e).ILInstance` callvirt-on-CLR-interface):** ALREADY WORKS on HEAD (probe-confirmed; returns a non-null ILTypeInstance). The prior F-4 characterization that this throws `InvalidCastException` is STALE -- an intervening change (Step 19/20 cross-binding + callvirt-CLR dispatch) closed it. No engine edit; a documentation correction.
- **Path #2 (`e.GetType()`):** ALREADY WORKS on HEAD via the reflection fallback (the prior `-96` symptom was `Type.op_Equality` hitting a separate null-operand autogen-binding gap, NOT GetType). A Neo `ObjectGetType_Neo` redirect was prototyped then REMOVED -- it does not fire (a `typeof(object).GetMethod("GetType")` vs `typeof(Exception).GetMethod("GetType")` MethodInfo key-mismatch) and the fallback already works. The F-4 spec-delta "SHALL register ObjectGetType_Neo" requirement is NOT merged -- it would record a non-existent fix. The op_Equality null-operand gap is recorded separately (a general Neo autogen-binding follow-up).
- **Path #4 (`ILTypeInstance.this[index]` indexer):** SHIPPED (the load-bearing fix) -- see the "ILTypeInstance field indexer reads IL-declared fields under Neo" requirement above. Stash-toggle-confirmed (HEAD returned null/the wrong value; with-fix returns the field value).
- **Path #3 (`appdomain.Invoke(instanceMethod, e)` instance re-entry):** REMAINS DEFERRED (the parametrized-Run ABI extension; also F-12 / STEP-25-PARTIAL). NOT unblocked by `neo-async-movenext-fix` (which routes through DriveMoveNextCore + a fresh interpreter calling ExecuteNeo directly, NOT the public Run). Tracked as follow-on child `neo-f4-parametrized-run-entry` (the LEAD SHALL drive it next under the TRUE-COMPLETION mandate). Reading an IL instance METHOD off a caught exception stays blocked until then; reading the IL TYPE (#2) and IL-declared FIELDS (#4) are closed.
- **Two NEW pre-existing gaps surfaced + sequenced:** (a) the op_Equality null-operand autogen-binding gap; (b) `new MyEx("...")` string-arg-to-IL-exception-ctor stores `this` into the string field instead of the arg. Both recorded in `.trae/documents/neo-deferred-items.md`.

### Requirement: A null reference operand read by an autogen Neo CLR binding SHALL yield null, not throw

The autogen Neo CLR method redirections (e.g.
`System_Type_Binding.op_Equality_1_Neo`, `System_String_Binding.op_Equality_*_Neo`,
and every autogen Neo binding that reads a reference-typed operand) read their
reference operands via `ILIntepreter.ReadNeoReference`. When an operand is the
Neo null sentinel (the mStack index `-1`, emitted by `Ldnull` and by any
expression producing a null reference), `ReadNeoReference` SHALL return `null`
rather than indexing `mStack[-1]`. This SHALL be implemented by applying the
established Neo null-sentinel convention (`(idx >= 0) ? mStack[idx] : null`,
already used at the `CLRMethod.Invoke` Neo arg read and at the `Ldelem_Ref`
null encoding) inside `ReadNeoReference` itself, so the fix covers the entire
autogen-binding defect class in one place. This requirement is Neo-only;
Legacy uses `StackObject.ToObject` (a different path) and is byte-identical.

#### Scenario: Type.op_Equality with a null operand does not throw
- **WHEN** an IL method obtains a non-null `System.Type t` (e.g. via
  `new MyEx("x").GetType()`) and evaluates `t == null`, which lowers to
  `Type.op_Equality(t, null)` (the right operand is the null sentinel).
- **THEN** the `op_Equality` redirection SHALL execute without throwing
  (no `ArgumentOutOfRangeException` from `mStack[-1]`).
- **AND** the result SHALL be `false` (`non-null == null` per CLR semantics).

#### Scenario: Type.op_Equality both-null and non-null operands
- **WHEN** an IL method evaluates `Type.op_Equality(null, null)`.
- **THEN** the result SHALL be `true`.
- **AND** **WHEN** it evaluates `Type.op_Equality(a, b)` with both non-null.
- **THEN** the result SHALL follow CLR reference/type equality semantics.
