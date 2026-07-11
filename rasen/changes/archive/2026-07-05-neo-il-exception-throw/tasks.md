## 1. Exception CrossBindingAdaptor (load-time fix, gap a)

- [x] 1.1 Create `ILRuntime/Runtime/Adapters/ExceptionAdaptor.cs`: a
  `CrossBindingAdaptor` whose `BaseCLRType` is `typeof(System.Exception)` and
  whose nested `Adapter : System.Exception, CrossBindingAdaptorType` mirrors
  `AttributeAdapter.Adapter` (holds `ILTypeInstance instance` + `AppDomain
  appdomain`; caches `IMethod` for `ToString()`; forwards `ToString()` to the
  IL override if present, else returns `instance.Type.FullName`). Forward
  `Message` only if OQ2 decides the bridge read is awkward in the harness
  (prefer minimal; see design D4/OQ2).
- [x] 1.2 Register the adaptor in the `AppDomain` constructor at
  `ILRuntime/Runtime/Enviorment/AppDomain.cs:231` (alongside the existing
  `RegisterCrossBindingAdaptor(new Adapters.AttributeAdapter());` line):
  `RegisterCrossBindingAdaptor(new Adapters.ExceptionAdaptor());`.
- [x] 1.3 Build the CLI (`dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj
  -c Debug_Neo`) and TestCases (`dotnet build TestCases/TestCases.csproj -c
  Debug`); confirm 0 errors. (Adaptor registration is engine-agnostic; both
  configs must compile.)
- [x] 1.4 Add a throwaway IL class `class _LoadProbe : System.Exception {}` in
  a temporary test method; confirm it LOADS (no `TypeLoadException`) before
  writing any throw/catch logic. Remove the throwaway once confirmed.

## 2. Throw-unwrap (run-time fix, gap b) -- SHARED engine

- [x] 2.1 Neo arm: extend `GetNeoException`
  (`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs:3204-3212`):
  after `mStack[objIndex] as Exception` returns null, if the object is an
  `ILTypeInstance`, fall back to `((ILTypeInstance)o).CLRInstance as Exception`;
  if still null, throw `NullReferenceException` (preserve the existing guard).
- [x] 2.2 Legacy arm: apply the IDENTICAL unwrap to the `Throw` arm in
  `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Register.cs:5307-3312`:
  read `mStack[objRef->Value]`, try `as Exception`, fall back to
  `ILTypeInstance.CLRInstance as Exception`, else the existing throw-null ->
  NullReferenceException behavior. NOT gated `#if ENABLE_NEO_MODE` (shared-
  engine fix; see design D1).
- [x] 2.3 Confirm via a temporary `Console.WriteLine` inside the catch arm
  (OQ1) which object the Neo catch slot holds for an IL exception -- the
  `Adapter` (CLR view) or the `ILTypeInstance`. Document the finding in the
  review report; remove the diagnostic before shipping.

## 3. Adversarial probe tests (TestCases/NeoStep14Test.cs)

Add 8 probe methods (the `NeoStep` filter catches them). Each test CATCHES
internally (try/catch sets a flag) and asserts -- it does NOT let the exception
escape (the harness treats an uncaught exception as failure). Each must run
<10s (infinite-loop guard). All 8 must pass on BOTH `Debug_Neo` (Neo) and plain
`Debug` + `useRegister=true` (Legacy).

- [x] 3.1 `NeoStep14_ILEx_ThrowAndCatch`: `class MyEx : System.Exception {}`,
  `throw new MyEx()`, `catch (MyEx)`, set flag, assert flag set.
- [x] 3.2 `NeoStep14_ILEx_CatchByBaseType`: throw `MyEx`, `catch
  (System.Exception)`, assert it matched.
- [x] 3.3 `NeoStep14_ILEx_CatchByExactType`: throw a derived `class DerivedEx
  : MyEx {}`, `catch (DerivedEx)`, assert exact-type match.
- [x] 3.4 `NeoStep14_ILEx_CatchOrderingDerivedBeforeBase`: throw `DerivedEx`,
  with BOTH `catch (DerivedEx)` and `catch (MyEx)` clauses in source order;
  assert the derived clause ran (nearest-match two-pass).
- [x] 3.5 `NeoStep14_ILEx_Rethrow`: catch `MyEx`, `throw;` (rethrow), outer
  `catch` sees the same exception, assert outer flag set + inner post-rethrow
  code did NOT run.
- [x] 3.6 `NeoStep14_ILEx_CrossFramePropagation`: `Caller { try { Callee();
  return -1; } catch (MyEx) { return 9; } }`, `Callee { throw new MyEx(); }`
  -> assert returns 9.
- [x] 3.7 `NeoStep14_ILEx_MessageField`: `class MyEx : System.Exception {
  public string Msg; }`, throw with `Msg` set, catch, read the field via the
  `ILInstance` bridge (`((CrossBindingAdaptorType)e).ILInstance`) or via
  forwarded `Message` (per OQ2), assert the value round-trips.
- [x] 3.8 `NeoStep14_ILEx_MixedWithCLR`: a method with a `catch (MyEx)` AND a
  `catch (DivideByZeroException)`; throw each in turn, assert each lands in the
  RIGHT clause (no cross-contamination).

## 4. Verify + regression gates

- [x] 4.1 Build + run full `NeoStep` smoke (Neo, `Debug_Neo`, filter
  `NeoStep`): confirm baseline preserved (100/100 at HEAD) + the 8 new probes
  green. A test >10s = infinite loop -- kill + investigate the throw/unwind.
- [x] 4.2 Legacy-neutral gate: build plain `Debug`, run `useRegister=true`
  with a catch filter (e.g. filter `NeoStep14`) + the full 519-test Legacy
  baseline; confirm 518/519 holds AND the new probes pass on Legacy too.
- [x] 4.3 Stash-toggle sanity: with the change stashed, the 8 new probes FAIL
  (NRE on throw); with the change applied, they PASS (proves the fix is
  load-bearing, not a false-green).
- [x] 4.4 Confirm the `CheckExceptionType` IL branch (CATCH-COMPLETE) is now
  reachable end-to-end (probe 3.1 exercises it for the IL catch clause;
  probe 3.2 exercises the CLRType arm for the CLR-base catch).

## 5. Closeout

- [ ] 5.1 Move `D-IL-EXCEPTION-THROW` to the Resolved section of
  `.trae/documents/neo-deferred-items.md` (搂4) + update the 搂2 master-table
  row + the 搂3 detail entry; note the shared-engine decision + the
  ExceptionAdaptor-as-built-in decision.
- [ ] 5.2 Update `.trae/documents/neo-handoff.md` 搂4 (test harness
  limitations: `throw new ILExceptionType()` is now green-testable) + 搂5
  (D-IL-EXCEPTION-THROW closed).
- [ ] 5.3 Write `review-report.md` (verify stage: the OQ1/OQ2 resolutions,
  the dump/probe evidence, the Legacy-neutrality proof) and `ship-log.md`
  (delivered scope, deferred scope, baseline numbers).
- [ ] 5.4 Archive: sync the `neo-exceptions` delta into
  `openspec/specs/neo-exceptions/spec.md` (MODIFIED Throw requirement +
  ADDED loadability requirement) and move the change to
  `openspec/changes/archive/`.

