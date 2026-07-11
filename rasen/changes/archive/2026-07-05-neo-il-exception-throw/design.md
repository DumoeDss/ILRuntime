# Design -- neo-il-exception-throw

Closes deferred Neo item **D-IL-EXCEPTION-THROW** by enabling an IL class that
inherits `System.Exception` to (a) LOAD and (b) be THROWN and CAUGHT end-to-end
on both engines. This is the second half of the exception follow-up sequence
(CATCH-COMPLETE closed the `CheckExceptionType` matcher; this change makes the
matcher actually reachable for an IL-thrown exception).

## Context (verified against current code)

Two independent gaps block the positive IL-catch test:

### Gap (a) -- load-time `TypeLoadException`

`ILType.cs:1399-1423` resolves an IL class's CLR base type. For
`class MyEx : System.Exception`, the base resolves to the CLRType for
`System.Exception`. The code then looks up a `CrossBindingAdaptor` in
`appdomain.CrossBindingAdaptors` keyed by `baseType.TypeForCLR`
(`ILType.cs:1412-1418`):

```csharp
if (appdomain.CrossBindingAdaptors.TryGetValue(baseType.TypeForCLR, out adaptor))
    baseType = adaptor;
else
    throw new TypeLoadException("Cannot find Adaptor for:" + baseType.TypeForCLR);
```

There is NO registered adaptor for `System.Exception`. The runtime ships only
`AttributeAdapter` (`Adapters/CLRCrossBindingAdaptors.cs`, registered at
`AppDomain.cs:231`); the test harness adds more
(`ILRuntimeTestBase/Adapters/helper.cs:22-29`) but none for `System.Exception`.
So loading `class MyEx : System.Exception` crashes before any method runs.

### Gap (b) -- run-time `Throw` NRE on IL instances

The `Throw` opcode reads the throw operand from the throw register's mStack slot
and casts to `Exception`:

- Neo: `GetNeoException` (`ILIntepreter.Neo.cs:3204-3212`):
  ```csharp
  Exception ex = mStack[objIndex] as Exception;
  if (ex == null) throw new NullReferenceException();
  return ex;
  ```
- Legacy: `Throw` arm (`ILIntepreter.Register.cs:5307-5312`):
  ```csharp
  var ex = mStack[objRef->Value] as Exception;
  throw ex;
  ```

For an IL exception instance, `mStack[idx]` is an `ILTypeInstance`, NOT a CLR
`Exception` -- so `as Exception` is `null` and the throw NREs (Neo explicitly;
Legacy throws `null`, which the CLR turns into a NullReferenceException). The
CLR `Exception` (the adaptor's `Adapter` subclass) lives at
`ILTypeInstance.CLRInstance`, established in the `ILTypeInstance` ctor
(`ILTypeInstance.cs:352-361`):

```csharp
if (type.FirstCLRBaseType is Enviorment.CrossBindingAdaptor)
    clrInstance = ((Enviorment.CrossBindingAdaptor)type.FirstCLRBaseType)
                    .CreateCLRInstance(type.AppDomain, this);
else
    clrInstance = this;
```

So once the Exception-adaptor is registered (gap a), an IL exception's
`CLRInstance` IS a CLR `Exception` (the `Adapter`). The Throw arm must read it.

### Established prior art

- **CATCH-COMPLETE** (archived `2026-07-04-implement-neo-catch-complete`)
  established that exception machinery is **shared-engine**: the shared
  `HandleException` / `GetCorrespondingExceptionHandler` / `CheckExceptionType`
  in `ILIntepreter.cs` is reached identically by `ExecuteR` and `ExecuteNeo`.
  Its `CheckExceptionType` IL branch already handles the case where the inner
  exception is an `ILTypeInstance` (via `CanAssignTo`) -- but that branch is
  only reachable if an IL exception actually propagates as an `ILTypeInstance`.
  After THIS change, an IL exception propagates as its CLR `Exception`
  projection (the adaptor's `Adapter`), so the existing `CheckExceptionType`
  CLRType arm + the IL branch BOTH work (the `catch (System.Exception)` clause
  is a CLRType and matches via `IsAssignableFrom`; the `catch (MyEx)` IL clause
  matches via the IL branch's `CanAssignTo` on the ILType, which the adaptor's
  `CLRInstance`/`ILInstance` bridge exposes).
- **Step 14** (archived `2026-07-04-implement-neo-step14`) established the
  catch-handler entry: the caught exception object is stored in the catch
  handler's reserved ref slot, and the Neo arm unwraps `ILRuntimeException` to
  its inner `Exception`. The Throw arm feeds the outer try-catch.
- **`AttributeAdapter`** (`Adapters/CLRCrossBindingAdaptors.cs`) is the minimal
  built-in adaptor pattern: a nested `Adapter : Attribute,
  CrossBindingAdaptorType` that forwards `ToString()`. The ExceptionAdaptor
  mirrors this exactly with `Adapter : System.Exception`.

## Goals / Non-Goals

**Goals:**
- An IL `class X : System.Exception` LOADS (no TypeLoadException).
- An IL exception instance can be THROWN and CAUGHT end-to-end on BOTH engines.
- The shared Throw arm correctly unwraps `ILTypeInstance.CLRInstance` to the
  CLR `Exception`.
- The fix is Legacy-neutral (Legacy 518/519 baseline holds; the new positive
  IL-catch test passes on BOTH engines).
- Adversarial probe tests cover the 8 cases listed in the proposal.

**Non-Goals:**
- Async exception propagation (Step 20).
- IL `filter` / `endfilter` (rare in C#; remain NIE).
- Exception types inheriting OTHER CLR exception subclasses
  (e.g. `class X : InvalidOperationException`) -- out of scope; the registered
  adaptor is for `System.Exception` only. Such a class would need its own
  adaptor (the user can register one). Document as a known limitation.
- Adapting the full `System.Exception` virtual surface (`GetBaseException`,
  `GetObjectData`, `Data`/`HelpLink`/`Source` are non-virtual or serialization-
  related). Only the overridable members that matter for catch-and-inspect
  (`Message` get, `ToString()`) are forwarded. This is sufficient for the
  validated test set; extend later if a real need surfaces.
- Modifying any Legacy behavior that is NOT the same bug. (Legacy's Throw arm
  has the identical `as Exception` bug; fixing it is in scope. No other Legacy
  change.)
- CrossBindingAdaptor for interfaces an IL exception implements (an IL
  `class X : Exception, IDisposable` hits the "inheriting and implementing
  interface at the same time is not supported yet" guard at
  `ILTypeInstance.cs:366` -- a pre-existing ILRuntime limitation; out of scope).

## Decisions

### D1: SHARED-engine fix, not Neo-only

The Throw `as Exception` bug exists IDENTICALLY in `ExecuteNeo`
(`ILIntepreter.Neo.cs:3204-3212`) and `ExecuteR`
(`ILIntepreter.Register.cs:5307-5312`). The adaptor registration is in
`AppDomain` (used by both engines). This is NOT a Neo-only behavior change.

Per the "Legacy is the reference, not a target" convention, modifying Legacy is
permitted when (1) Legacy has the SAME bug and (2) the fix is a genuine Legacy
improvement, not a Neo workaround. Both hold:
- Legacy's Throw arm NREs on an IL exception instance today -- it has never
  worked. There is no passing Legacy test that depends on the NRE.
- The fix (unwrap `CLRInstance`) is the correct behavior for BOTH engines.
- CATCH-COMPLETE already set the precedent: it modified the shared
  `CheckExceptionType` for the same reason, under this same `neo-exceptions`
  capability, and was Legacy-neutral.

The fix is NOT gated `#if ENABLE_NEO_MODE`. Gate: the new positive IL-catch
test passes on BOTH engines (Neo `Debug_Neo` AND plain `Debug` +
`useRegister=true`), and the 518/519 Legacy baseline holds.

**Alternative considered:** Neo-only fix (gate the unwrap
`#if ENABLE_NEO_MODE`, register the adaptor only in the Neo test harness).
REJECTED -- it would leave Legacy's identical bug unfixed and split the Throw
logic across two arms. The shared fix is cleaner and matches CATCH-COMPLETE.

### D2: ExceptionAdaptor as a BUILT-IN (registered in `AppDomain`), not the test harness

The adaptor is registered in the `AppDomain` constructor alongside
`AttributeAdapter` (`AppDomain.cs:231`), not in
`ILRuntimeTestBase/Adapters/helper.cs`. Rationale:
- The Throw-unwrap (D3) requires the IL exception's `CLRInstance` to BE a CLR
  `Exception`. That only happens if the adaptor created it. Without the adaptor
  registered, the IL class can't even load (gap a). So the adaptor is a runtime
  prerequisite, not a test-harness convenience.
- Shipping it as a built-in matches `AttributeAdapter` (also a built-in for a
  sealed-ish system type) and makes `throw new MyEx()` work in any AppDomain,
  not just the test harness.
- It is engine-agnostic (the `CrossBindingAdaptors` dictionary is shared).

**Alternative considered:** register only in the test harness. REJECTED -- it
would make the feature test-harness-specific and any other consumer (Unity app,
AOT loader) would still hit the TypeLoadException. A built-in is the durable
choice.

### D3: The Throw-unwrap mechanism

When the throw operand is NOT directly an `Exception`, the arm unwraps via
`((ILTypeInstance)o).CLRInstance`. Concretely:

```csharp
object o = mStack[objIndex];           // (Neo) or mStack[objRef->Value] (Legacy)
Exception ex = o as Exception;
if (ex == null && o is ILTypeInstance ili)
    ex = ili.CLRInstance as Exception;  // the adaptor's Adapter (a CLR Exception)
if (ex == null)
    throw new NullReferenceException(); // throwing a non-exception object is invalid
return ex;
```

Why `CLRInstance` and not `ili.Type.FirstCLRBaseType...`: `CLRInstance` is the
already-instantiated adaptor `Adapter` object (set in the `ILTypeInstance` ctor,
`ILTypeInstance.cs:356`). It IS-A `System.Exception` (the adaptor's `Adapter`
inherits it). This is the SAME unwrap ILRuntime uses elsewhere for CLR-method
dispatch on an IL instance (`ILIntepreter.cs:2936`,
`ILIntepreter.Register.cs:3557`, `Extensions.cs:311`,
`AppDomain.cs:1450`). We are reusing the established bridge, not inventing one.

**Non-exception IL instance:** an `ILTypeInstance` whose IL type does NOT
inherit a CLR Exception has `CLRInstance == this` (`ILTypeInstance.cs:360,
372`), so `ili.CLRInstance as Exception` is null -> the arm NREs. Correct:
throwing a non-exception object is invalid in the CLR (and the existing Neo arm
already NREs on null). Legacy throws `null` -> NullReferenceException. Both
behaviors are preserved for the non-exception case.

### D4: The ExceptionAdaptor's `Adapter` forwards `Message` + `ToString()`

The nested `Adapter : System.Exception, CrossBindingAdaptorType`:
- Holds `ILTypeInstance instance` + `AppDomain appdomain` (mirrors
  `AttributeAdapter.Adapter`).
- Forwards `ToString()` (virtual; the IL class may override) via the cached-
  `IMethod` pattern from `AttributeAdapter`.
- Forwards `Message`: `System.Exception.Message` is a virtual get in some
  runtimes but is NOT overridable on netstandard in a way ILRuntime's pattern
  catches cheaply. The initial implementation exposes the IL instance's
  `Message` field/property via reflection on read IF the IL type declares one;
  otherwise falls back to `instance.Type.FullName`. The "IL exception with a
  message field readable in catch" probe (probe 7) validates the chosen
  mechanism -- if reflection-on-read is brittle, the test passes the message
  via a field read on the caught `ILTypeInstance` (the catch slot stores the
  unwrapped inner; design open question OQ1 resolves which object the catch
  slot holds for an IL exception -- see Open Questions).

### D5: Which object does the catch slot hold for an IL exception?

Two representations are in flight after this change: the `ILTypeInstance` (the
IL view) and the `Adapter : Exception` (the CLR view, = `CLRInstance`). The
catch machinery (`HandleException` -> `GetCorrespondingExceptionHandler` ->
`CheckExceptionType`) and the catch-slot store (Step 14 design §6) need to
agree. The Throw arm throws the CLR `Adapter` (an `Exception`), so:
- `catch (System.Exception e)`: CLRType catch; `CheckExceptionType` CLRType arm
  matches via `IsAssignableFrom(typeof(Adapter))` -> true. The catch slot
  stores the `Adapter`. Reading `e.Message` on the Adapter forwards to the IL
  instance (D4). Reading an IL-declared field requires casting back to the IL
  view.
- `catch (MyEx e)` (IL catch type): `CheckExceptionType` IL branch. The thrown
  object is the `Adapter` (a CLR `Exception`, NOT an `ILTypeInstance`), so the
  existing IL branch's `exception as ILTypeInstance` is null, and it falls to
  the `TypeForCLR.IsAssignableFrom` fallback. The IL catch type's `TypeForCLR`
  is the adaptor's `Adapter` type (`ILType.cs` TypeForCLR for an adaptor-based
  IL type), and `IsAssignableFrom(typeof(Adapter))` is true. So the catch
  matches. The catch slot stores the `Adapter`.

**Implication:** for an IL-typed catch clause, the catch slot holds the `Adapter`
(the CLR view), and reading IL-declared fields requires
`((CrossBindingAdaptorType)e).ILInstance` to recover the `ILTypeInstance`. This
is the standard ILRuntime cross-domain pattern. The probe tests must exercise
BOTH the `catch (Exception)` and `catch (MyEx)` shapes, and (for the message-
field probe) read the field via the ILInstance bridge.

This is an open question to confirm at apply (OQ1) by inspecting what the catch
slot actually holds via a temporary `Console.WriteLine` in the catch arm (the
CATCH-COMPLETE design anticipated the inner object could be EITHER an
`ILTypeInstance` OR its CLR projection -- here it is the CLR projection).

## Risks / Trade-offs

- **[The adaptor's `Adapter` is a minimal forwarder]** -- only `Message` +
  `ToString()` are forwarded; `Source`/`HelpLink`/`StackTrace`/`Data` are NOT
  (non-virtual or serialization). An IL exception that sets `ex.Source` will
  NOT round-trip it on the caught CLR view. -> Mitigation: document as a known
  limitation; the validated probes do not depend on these. Extend later.
- **[Throw-unwrap on a non-exception IL instance]** must NRE, not swallow. ->
  Mitigation: the `if (ex == null) throw new NullReferenceException();` guard
  is preserved AFTER the unwrap attempt (D3). Probe: throwing a plain IL class
  (no Exception base) -- but this requires an IL class WITHOUT a CLR base,
  which loads fine and whose `CLRInstance == this` -> NRE. NOT in the validated
  set (would need a class that is throwable-but-not-Exception, which C# forbids
  at compile time -- the C# compiler requires the throw operand to be
  `Exception`-typed). So this path is unreachable from C# and is a defensive
  guard only.
- **[Catch-slot representation mismatch (D5)]** -- if the catch slot holds the
  `Adapter` but a probe reads an IL field directly off the caught `e`, the read
  NREs. -> Mitigation: probes read IL fields via `((CrossBindingAdaptorType)e).ILInstance`,
  and the catch-by-base-type probe reads `e.Message` (forwarded). Confirm at
  apply via OQ1.
- **[Cross-contamination with CLR catches (probe 8)]** -- a method with both an
  IL-exception catch and a CLR-exception catch must dispatch each thrown
  exception to the RIGHT clause. -> Mitigation: this is exactly what
  `CheckExceptionType`'s two-pass nearest-match already does (CATCH-COMPLETE);
  probe 8 validates no regression.
- **[Legacy-neutral risk]** -- the shared Throw change touches `ExecuteR`. ->
  Mitigation: the unwrap is a strict generalization (`o as Exception` first,
  then the IL fallback; for any existing CLR `Exception` operand the first
  `as` succeeds and the fallback is unreachable -- byte-identical behavior).
  Gate: 518/519 Legacy baseline + Legacy catch filter + the new positive test
  on plain `Debug`.
- **[Infinite-loop risk]** -- exception-unwind bugs tend to loop. -> Mitigation:
  every probe must run <10s (handoff test rule); kill + investigate if exceeded.

## Migration Plan

- Additive only: a new adaptor file + one ctor line + the unwrap fallback in
  two Throw arms. No existing API changes; no data-format change.
- Rollback = revert the 4 file changes (trivial). No persisted state depends on
  the adaptor being registered (it is created fresh per AppDomain).
- No AOT-format impact (this change is JIT/runtime; the `.neo` AOT layer (Steps
  22-26) would need to register the same adaptor in its runtime loader, but
  that is the loader's concern, not this change's).

## Open Questions

- **OQ1 (RESOLVED at apply):** which object does the Neo catch slot actually
  hold for an IL exception -- the `Adapter` (CLR view) or the `ILTypeInstance`
  (IL view)? **CONFIRMED: the `Adapter` (CLR view).** A temporary
  `Console.WriteLine` in the Neo catch-handler slot-store
  (`ILIntepreter.Neo.cs:3105`) printed
  `ex.GetType = ILRuntime.Runtime.Adapters.ExceptionAdaptor+Adapter` for every
  IL-exception probe. The catch slot stores the `Adapter` (an
  `ExceptionAdaptor.Adapter`), NOT the `ILTypeInstance`. Consequences (all
  confirmed by the green probes):
  - `catch (System.Exception e)` -> `CheckExceptionType` CLRType arm,
    `IsAssignableFrom(typeof(Adapter))` true -> matches (probe 3.2 green).
  - `catch (MyEx e)` (IL catch type) -> `CheckExceptionType` IL branch; the
    thrown `Adapter` is NOT an `ILTypeInstance`, so the branch's
    `exception as ILTypeInstance` is null and it falls to the
    `TypeForCLR.IsAssignableFrom` fallback. The IL catch type's `TypeForCLR`
    is the `Adapter` type, so the match succeeds (probes 3.1, 3.3, 3.4, 3.5,
    3.6, 3.7 all green).
  - The catch-clause VARIABLE `e` (typed `MyEx`) holds the `Adapter`; declaring
    `e` does NOT require a projection back to `ILTypeInstance` -- the slot
    value is used opaquely by the IL catch body (verified: probe 3.7 with
    `catch (MyEx e) { ... }` passes; `e` is usable for `isinst`).
  - **Reading IL-declared fields/methods off the caught `e` is constrained on
    Neo:** the standard `((CrossBindingAdaptorType)e).ILInstance` bridge
    REQUIRES `callvirt` on a CLR interface (`CrossBindingAdaptorType::
    get_ILInstance`) against the `Adapter` receiver, and that callvirt-on-CLR-
    interface-where-receiver-is-the-Adapter path is NOT supported by ExecuteNeo
    today (throws `InvalidCastException`: "Object does not match target
    type"). `e.GetType()` likewise NIEs (`callvirt.clr` on `Object.GetType`).
    The `isinst`/`is` path WORKS (probe 3.7 asserts `e is MyEx`). This is a
    separate Neo callvirt limitation, NOT an exception-throw bug; documented
    here so the message-field probe (3.7) asserts via `isinst`, and reading IL
    fields off a caught IL exception is flagged as a follow-up (depends on Neo
    callvirt-on-CLR-interface / CLR-method-on-Adapter support).

- **OQ2 (RESOLVED at apply):** does the ExceptionAdaptor need to forward
  `Message` for the message-field probe? **NO -- the minimal adaptor (forward
  `ToString()` only, mirroring `AttributeAdapter` exactly) is shipped.** Two
  reasons forced this:
  1. Forwarding `Message` via `appdomain.Invoke(getMessage, instance)` does NOT
     work in Neo mode: the public `Run`/`Invoke` re-entry path
     (`ILIntepreter.cs:87-120`) ignores the `instance` argument under
     `ENABLE_NEO_MODE` (the Step-6 entry shim only handles no-arg static
     methods) -- so an IL `get_Message` override would have no `this` and NRE.
     This is a pre-existing Neo re-entry gap, unrelated to exception throw.
  2. Reading the IL field directly off the `ILTypeInstance` via the indexer
     also does NOT work in Neo: `ILTypeInstance.this[index]` returns `null`
     under `ENABLE_NEO_MODE` (Legacy-only `StackObject[] fields` path; Neo uses
     `byte[] Primitives + AutoList`).
  Given both Message-forwarding paths are blocked on Neo (separate follow-ups),
  the minimal adaptor was chosen and probe 3.7 asserts the caught object's
  identity via `isinst` (the same opcode the catch matcher uses) instead of
  reading an IL field. The `MyEx` class retains a `Msg` field + `Message`
  override for the throw side (forwarding will activate once the Neo re-entry /
  indexer gaps close).

### Actual edit sites (verified)

- `ILRuntime/Runtime/Adapters/ExceptionAdaptor.cs` (NEW) -- minimal adaptor,
  `Adapter : System.Exception, CrossBindingAdaptorType`, forwards `ToString()`
  only (mirrors `AttributeAdapter`).
- `ILRuntime/Runtime/Enviorment/AppDomain.cs:231-237` --
  `RegisterCrossBindingAdaptor(new Adapters.ExceptionAdaptor());` next to
  `AttributeAdapter`.
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs:3204-3224` --
  `GetNeoException`: `o as Exception` first, then
  `ILITypeInstance)o).CLRInstance as Exception` fallback, then NRE.
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Register.cs:5307-5320`
  -- Legacy `Throw` arm, IDENTICAL unwrap (NOT `#if ENABLE_NEO_MODE`-gated).
- `TestCases/NeoStep14Test.cs` -- 8 `NeoStep14_ILEx_*` probes + the `MyEx` /
  `DerivedEx` IL exception classes.

### Deviation from proposal

- Probe 3.7 asserts via `e is MyEx` (isinst) rather than reading an IL-declared
  field value. Rationale: the two field/message read paths
  (`CrossBindingAdaptorType.ILInstance` bridge callvirt; `ILTypeInstance`
  indexer; `appdomain.Invoke` re-entry) are each blocked by separate pre-
  existing Neo gaps. The probe still exercises OQ1 (the catch slot holds the
  CLR `Adapter`, and the IL catch clause matches) end-to-end. Reading IL fields
  off a caught IL exception is recorded as a follow-up (depends on Neo
  callvirt-on-CLR-interface / `appdomain.Invoke` instance-method support).
