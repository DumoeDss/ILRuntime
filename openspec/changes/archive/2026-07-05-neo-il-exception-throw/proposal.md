## Why

End-to-end throw+catch of an **IL-typed exception** (`class MyEx : System.Exception
{}`) is blocked on TWO pieces that the prior `D-CHECKEX` / CATCH-COMPLETE follow-up
did not close (it only fixed the `CheckExceptionType` matcher, which is
necessary-but-not-sufficient):

- **(a) TypeLoadException at load time.** An IL class that inherits
  `System.Exception` cannot even be loaded: `ILType.cs:1418` throws
  `TypeLoadException("Cannot find Adaptor for:System.Exception")` because there
  is no registered `System.Exception` `CrossBindingAdaptor`. Only
  `AttributeAdapter` is shipped as a built-in (`AppDomain.cs:231`); the test
  harness registers several others (`ILRuntimeTestBase/Adapters/helper.cs`) but
  none for `System.Exception`.
- **(b) Throw `as Exception` NRE at run time.** The `Throw` opcode does
  `mStack[idx] as Exception` on BOTH engines (Neo `GetNeoException` at
  `ILIntepreter.Neo.cs:3204-3212`, Legacy arm at
  `ILIntepreter.Register.cs:5307-5312`). For an IL exception the mStack slot
  holds an `ILTypeInstance`, NOT a CLR `Exception` -- the `as Exception` yields
  `null` and the throw NREs. The CLR `Exception` (the adaptor's `Adapter`
  subclass) lives at `ILTypeInstance.CLRInstance`, not in the mStack slot.

Closes deferred Neo item **D-IL-EXCEPTION-THROW**. Once both pieces are in place,
the **positive IL-catch test reserved since CATCH-COMPLETE becomes authorable**:
define `class MyEx : System.Exception`, throw it, catch it, and assert on the
caught object. This is the last gap before `throw new ILExceptionType()` works.

## What Changes

- **Register a built-in `System.Exception` `CrossBindingAdaptor`** so an IL
  `class X : System.Exception` loads. The adaptor's nested `Adapter` class
  inherits `System.Exception` (and implements `CrossBindingAdaptorType`), so an
  IL exception instance's `CLRInstance` IS a CLR `Exception`. Register it by
  default in `AppDomain` (mirrors the existing `AttributeAdapter` built-in at
  `AppDomain.cs:231`) -- this is engine-agnostic.
- **Handle IL instances in `Throw` on BOTH engines.** When the throw operand is
  an `ILTypeInstance` that is not directly an `Exception`, obtain the CLR
  exception via `((ILTypeInstance)o).CLRInstance as Exception` (the adaptor's
  `Adapter`). This is the shared-engine Throw-unwrap fix.
- **Shared-engine decision (resolved):** both fixes are **shared-engine**, NOT
  Neo-only. The adaptor registration lives in `AppDomain` (used by both
  engines); the Throw `as Exception` bug exists IDENTICALLY in `ExecuteNeo` and
  `ExecuteR`. Legacy is the reference, and Legacy has the SAME bug -- so fixing
  both arms is a genuine Legacy improvement, not a Neo workaround. The fix MUST
  be **Legacy-neutral-tested** (the new positive IL-catch test passes on BOTH
  engines via plain `Debug` + `useRegister=true`, and the 518/519 Legacy
  baseline holds). See design for the full Legacy-neutrality argument.
- **Enable the positive IL-throw+IL-catch test.** Add adversarial probe tests
  to `TestCases/NeoStep14Test.cs` (the exception step): `class MyEx : Exception`
  thrown+caught; catch by base `catch (Exception)`; catch by exact type
  `catch (MyEx)`; catch ordering (derived before base); `throw;` rethrow; IL
  exception propagating across an IL->IL call; IL exception with a message
  field read in catch; mixed IL+CLR exception catches in one method (no cross-
  contamination). Tests CATCH internally (try/catch sets a flag) and assert --
  they do NOT let the exception escape (the harness treats an uncaught
  exception as failure).

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `neo-exceptions`: the Throw requirement SHALL handle an IL-typed exception
  operand by unwrapping its `CLRInstance` (the adaptor's CLR `Exception`), not
  NRE. A new requirement SHALL state that an IL class inheriting
  `System.Exception` loads (a `System.Exception` CrossBindingAdaptor is
  registered by default). The DEFERRED markers on IL-typed throw/catch are
  flipped to delivered. (Implementation is shared-engine: the adaptor lives in
  `AppDomain` and the Throw unwrap applies to both `ExecuteNeo` and `ExecuteR`,
  mirroring the CATCH-COMPLETE shared-engine precedent under this same
  capability.)

## Impact

- **Code:**
  - `ILRuntime/Runtime/Adapters/ExceptionAdaptor.cs` (new) -- the
    `CrossBindingAdaptor` with a nested `Adapter : System.Exception,
    CrossBindingAdaptorType` (forwarding the common overridable
    `Exception` members: `Message`, `ToString()`; optional: `GetBaseException`,
    `Data`/`HelpLink` are non-virtual so not forwarded). Mirrors
    `AttributeAdapter` structure.
  - `ILRuntime/Runtime/Enviorment/AppDomain.cs:231` -- register
    `new ExceptionAdaptor()` alongside `AttributeAdapter`.
  - `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` --
    `GetNeoException` (`:3204-3212`): when `mStack[objIndex]` is not directly an
    `Exception`, fall back to `((ILTypeInstance)o).CLRInstance as Exception`
    before the NRE.
  - `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Register.cs` --
    `Throw` arm (`:5307-5312`): same unwrap (`mStack[objRef->Value]` ->
    `ILTypeInstance.CLRInstance as Exception`). SHARED-engine; NOT Neo-gated.
- **Hot path:** `Throw` runs once per IL throw (rare); the unwrap is one extra
  `is`/`as` on the existing read. Negligible.
- **Regression surface:** every IL throw+catch on BOTH engines. Gates: full
  `NeoStep` smoke (Neo, 100/100 baseline at HEAD after neo-opt-harden-2) + a
  Legacy catch filter (plain `Debug` + `useRegister=true`) + the 518/519 Legacy
  baseline reference. The new positive IL-catch tests MUST pass on BOTH engines.
- **Dependencies:** CATCH-COMPLETE (`CheckExceptionType` IL branch) -- already
  landed (necessary-but-not-sufficient; this change makes it sufficient).
  Step 18 CLR `newobj` -- already landed (so `throw new MyEx()` reaches the
  Throw arm). Step 14 try/catch/finally -- already landed.
- **Tests:** extend `TestCases/NeoStep14Test.cs` with the 8 adversarial probe
  cases (the `NeoStep` filter catches them). Each must run <10s (infinite-loop
  guard; exception-unwind bugs tend to loop).
- **Risk:** MEDIUM. The Throw arm is on the rare throw path (low hot-path
  risk), but the Exception-adaptor registration touches the AppDomain ctor
  (every AppDomain gains the adaptor) and the Throw unwrap changes a shared-
  engine opcode. Adversarial probes MANDATORY (Step 17 B1 / OPT-HARDEN K1
  lesson: green smoke does NOT prove an exception gate correct). The biggest
  risk is the Throw-unwrap ordering: an `ILTypeInstance` whose IL type does NOT
  inherit a CLR Exception has `CLRInstance == this` (an `ILTypeInstance`, not an
  `Exception`) -> `as Exception` is null -> must NRE (throwing a non-exception
  object is invalid), NOT silently swallow.
