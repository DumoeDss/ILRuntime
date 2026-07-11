# Ship Log -- neo-il-exception-throw (D-IL-EXCEPTION-THROW)

**Date:** 2026-07-05
**Branch:** `features/object-model-overhaul`
**Working tree:** UNCOMMITTED (LEAD commits after ship).
**Review verdict:** APPROVED -- 0 Blocker, 0 Major, doc-only Minors (M1, M2),
Trivials T1/T2 noted. Skill `openspec-gstack-review`.

## Delivered scope

Closes deferred Neo item **D-IL-EXCEPTION-THROW**: an IL class that inherits
`System.Exception` (`class MyEx : System.Exception`) now (a) LOADS and (b) is
THROWN + CAUGHT end-to-end on BOTH engines. This is the second half of the
exception follow-up sequence (CATCH-COMPLETE closed the `CheckExceptionType`
matcher; that was necessary-but-not-sufficient -- without this change the IL
exception class cannot be loaded at all, so the CATCH-COMPLETE IL branch was
dead code).

### Two shared-engine fixes (NOT Neo-gated; both engines had the identical bug)

This is a **shared-engine** change in the CATCH-COMPLETE precedent, NOT a Neo-
only workaround. Both `ExecuteNeo` and `ExecuteR` had the IDENTICAL `Throw`
bug, and the adaptor registration lives in `AppDomain` (engine-agnostic).
Fixing both arms is a genuine Legacy improvement (Legacy had NEVER successfully
thrown an IL exception). Nothing is gated `#if ENABLE_NEO_MODE`.

1. **NEW `ExceptionAdaptor`** (`ILRuntime/Runtime/Adapters/ExceptionAdaptor.cs`):
   a `CrossBindingAdaptor` with a nested
   `Adapter : System.Exception, CrossBindingAdaptorType` that mirrors
   `AttributeAdapter` (`ILRuntime/Runtime/Adaptors/CLRCrossBindingAdaptors.cs`)
   byte-for-byte in structure (same overrides, same fields, same
   `CreateCLRInstance`, same cached-`IMethod` ToString forwarding). The adaptor
   forwards `ToString()` only; `Message` forwarding is intentionally NOT shipped
   because both `Message`-read paths are blocked by separate pre-existing Neo
   gaps (see Deferred [NEO-IL-EX-FIELDACCESS]). Registered as a built-in in the
   `AppDomain` constructor (~line 231, next to the existing `AttributeAdapter`).
   This fixes the `TypeLoadException("Cannot find Adaptor for:System.Exception")`
   at `ILType.cs:1418` for an IL `class X : System.Exception`.

2. **Throw-unwrap in BOTH arms.** After `o as Exception`, fall back to
   `((ILTypeInstance)o).CLRInstance as Exception` (the adaptor's `Adapter`,
   which IS-A `System.Exception`); if still null, the existing
   `NullReferenceException` guard fires (throwing a non-exception object is
   invalid, and is unreachable from C# anyway). Sites:
   - Neo `GetNeoException` (`ILIntepreter.Neo.cs` ~line 3204).
   - Legacy `Throw` arm (`ILIntepreter.Register.cs` ~line 5307).

   For any throw operand that is ALREADY a CLR `Exception` (the existing case:
   CLR-raised exceptions from arithmetic / null-deref / host bindings), the
   first `as Exception` succeeds and the IL fallback is UNREACHABLE -- so the
   existing CLR-exception throw behavior is byte-for-byte preserved on both
   engines.

### OQ1 resolution (catch-slot representation)

The Neo catch slot holds the CLR `Adapter` (the CLR view), NOT the
`ILTypeInstance`. Confirmed via a temp `Console.WriteLine` in the catch-handler
slot-store. The `CheckExceptionType` IL branch's `TypeForCLR.IsAssignableFrom`
fallback matches the IL catch type (the IL catch type's `TypeForCLR` IS the
adaptor's `Adapter` type). `catch (MyEx e)` works -- `e` holds the `Adapter`
and is usable opaquely; `catch (System.Exception e)` matches via the existing
CLRType arm. As a direct side-benefit, CATCH-COMPLETE's `CheckExceptionType` IL
branch is now exercised end-to-end for the first time (it was dead code before
this change -- no IL exception could be thrown).

## Verification

- **Neo smoke (`Debug_Neo`, filter `NeoStep`):** **108/108 green**
  (100 baseline + 8 new `NeoStep14_ILEx_*` probes, each returning the `9`
  success sentinel). Reproduced independently by the reviewer.
- **Legacy-neutral:** plain `Debug` + `useRegister=true`. The 8 `NeoStep14_ILEx_*`
  probes pass on Legacy too. Full Legacy suite: 617 tests / 9 failed -- ALL 9
  are pre-existing failures (NeoOptHardening K1, NeoStep13 ClrStruct,
  NeoStep14 TC1/TC5/TC8, NeoStep15 TC6, NeoStep6 NeoNaNR8). The 3 NeoStep14 TC
  failures are `System.ArgumentOutOfRangeException` in `List.set_Item` for
  CLR-exception-throwing tests (TC1/TC8 throw `DivideByZeroException`/NRE); the
  Throw-unwrap's IL fallback is UNREACHABLE for them (the first `as Exception`
  succeeds), so the Throw-unwrap change is exonerated.
- **Stash-toggle (load-bearing proof):** with the change stashed, ALL 8 probes
  FAIL -- but stronger than a per-test NRE: the WHOLE test session crashes at
  `TestSession.LoadTest()` with `TypeLoadException: Cannot find Adaptor
  for:System.Exception` at `ILType.cs:1418` before any test runs (the presence
  of `MyEx : System.Exception` in the assembly crashes type initialization for
  any type's method enumeration). With the fix applied, the session loads and
  all 8 probes pass. This proves the gap-a load crash is load-bearing, not a
  false green.

### Reviewer finding M1 (methodology clarification, applied here)

The reviewer's M1 noted that the stash-toggle proof is stronger than a per-test
NRE: on HEAD-with-tests the whole session hard-crashes at load (because
`MyEx : System.Exception` crashes `TestSession.LoadTest()` at type init for ANY
type's method enumeration), so the 3 NeoStep14 TC1/TC5/TC8 failures cannot be
run in isolation on pure HEAD without ALSO reverting the test additions. The
substantive exoneration holds regardless: TC1/TC8 throw CLR exceptions
(`DivideByZeroException`/NRE), so the unwrap's IL branch is unreachable for
them by construction (confirmed by reading the test bodies).

### Reviewer finding M2 (breaking-change callout -- see below)

Recording the M2 breaking-change callout explicitly here so consumers see it:
registering `ExceptionAdaptor` as a built-in is a **breaking change for any
downstream consumer that previously registered its OWN `System.Exception`
CrossBindingAdaptor.** See "Deferred -- M2 breaking-change callout" below.

## Deferred (recorded, NOT silently dropped)

### M2 breaking-change callout

Registering `ExceptionAdaptor` as a built-in (in the `AppDomain` ctor) means
that any DOWNSTREAM CONSUMER (a Unity host, a test harness, the `.neo` AOT
runtime loader, a third-party embedding) that PREVIOUSLY registered its OWN
`System.Exception` CrossBindingAdaptor will now hit the double-registration
guard in `AppDomain.RegisterCrossBindingAdaptor` (`AppDomain.cs:1981-2003`,
which throws `"... already added."` on a duplicate `System.Exception` key) at
startup. This is the CORRECT enforced-uniqueness behavior (a single canonical
adaptor per CLR base type), but it IS a behavior change: such consumers MUST
REMOVE their manual `System.Exception` adaptor registration.

**No consumer in THIS repo is affected** -- verified by grepping
`ILRuntimeTestBase/`, `TestCases/`, and the harness
(`ILRuntimeTestBase/Adapters/helper.cs:22-29` registers
`ClassInheritanceTestAdaptor`, `InterfaceTestAdaptor`, `TestClass2-4Adapter`,
`IDisposableAdapter`, `ClassInheritanceTest2Adaptor`,
`IAsyncStateMachineClassInheritanceAdaptor` -- none for `System.Exception`).
The `.neo` AOT runtime loader (Step 25) will need to be aware of this when it
ships its own adaptor set.

### F-4 / NEO-IL-EX-FIELDACCESS -- pre-existing Neo gap (surfaced here, NOT introduced)

Reading IL-declared fields/methods off a CAUGHT IL exception via the adaptor
bridge is BROKEN on Neo. This is a pre-existing gap in ExecuteNeo's
callvirt-on-CLR-interface / `appdomain.Invoke` instance-method / `ILTypeInstance`
indexer machinery -- it is NOT introduced by this change (which only registers
an adaptor and adds an unwrap fallback). The four broken read paths are:

1. **`((CrossBindingAdaptorType)e).ILInstance` bridge** -- requires `callvirt`
   on a CLR interface (`CrossBindingAdaptorType::get_ILInstance`) against the
   `Adapter` receiver; ExecuteNeo throws `InvalidCastException` ("Object does
   not match target type").
2. **`e.GetType()`** -- NIE (`callvirt.clr` on `Object.GetType`).
3. **`appdomain.Invoke(instanceMethod, e)`** -- NRE: the public `Run`/`Invoke`
   re-entry path (`ILIntepreter.cs:87-120`) ignores the `instance` argument
   under `ENABLE_NEO_MODE` (the Step-6 entry shim handles only no-arg static
   methods), so an IL `get_Message` override has no `this`.
4. **`ILTypeInstance.this[index]` indexer** -- returns `null` under
   `ENABLE_NEO_MODE` (Legacy-only `StackObject[] fields`; Neo uses
   `byte[] Primitives + AutoList ManagedObjects`).

Workaround used in the test probes: `e is MyEx` (isinst -- the same opcode the
catch matcher uses, known-good on the `Adapter`). The `MyEx` class retains its
`Msg` field + `Message` override on the throw side; forwarding will activate
once the callvirt/indexer gaps close.

Route to a cross-binding-adaptor / Step 13 Area 4 follow-up. Full detail
recorded in `.trae/documents/neo-deferred-items.md` (F-4 /
NEO-IL-EX-FIELDACCESS, master table + detail entry) and in
`openspec/changes/neo-completion-portfolio/planning-context.md` under
"Follow-ups discovered".

## Side-benefit

CATCH-COMPLETE's `CheckExceptionType` IL branch is now exercised END-TO-END
for the first time. Before this change it was dead code: no IL exception could
be thrown, so the IL-catch-clause matcher branch was unreachable. The 8
`NeoStep14_ILEx_*` probes exercise it (probe 3.1 the IL-catch path; probe 3.2
the CLRType `IsAssignableFrom` path).

## Files touched (working tree, UNCOMMITTED)

- `ILRuntime/Runtime/Adapters/ExceptionAdaptor.cs` (NEW)
- `ILRuntime/Runtime/Enviorment/AppDomain.cs` (~line 231, register built-in)
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (~line 3204,
  `GetNeoException` unwrap)
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Register.cs` (~line
  5307, Legacy `Throw` arm, IDENTICAL unwrap, NOT Neo-gated)
- `TestCases/NeoStep14Test.cs` (8 `NeoStep14_ILEx_*` probes + the `MyEx` /
  `DerivedEx` IL exception classes)

## Closeout

- **D-IL-EXCEPTION-THROW** -> RESOLVED (this change). The
  `neo-deferred-items.md` master-table row and detail entry are updated to
  RESOLVED 2026-07-05.
- **D-CHECKEX** -> fully RESOLVED. Its end-to-end piece (the part that needed
  an Exception adaptor + Throw-for-IL) is now closed; the master-table row and
  detail entry move from PARTIAL to RESOLVED.
- The `neo-exceptions` capability spec delta is merged into
  `openspec/specs/neo-exceptions/spec.md` (MODIFIED "Throw opcode executes
  under Neo" requirement + ADDED "An IL class inheriting System.Exception
  loads and is throwable" requirement); this change is archived at
  `openspec/changes/archive/2026-07-05-neo-il-exception-throw/`.

Did NOT git commit/push -- LEAD commits after ship.
