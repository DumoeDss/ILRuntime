# Proposal -- neo-callvirt-this-null-residual

Wave-2 child of `neo-overhaul`. Drives the full Neo smoke down by clearing the
"Neo callvirt this is null" residual at `ResolveNeoCallvirtCLRTarget:1432`
(the C4-residual sub-cluster, 4 tests at the 86-baseline).

## Problem
At the 86-baseline full Neo smoke, 4 tests failed with
`NullReferenceException: Neo callvirt this is null` at
`ILIntepreter.Neo.cs:ResolveNeoCallvirtCLRTarget:1432` (the `ReadNeoCallThis`
guard throws when the callvirt_CLR `this` slot is the null sentinel):

- `TestCases.ReflectionTest.ReflectionTest04`
- `TestCases.ReflectionTest.ReflectionTest19`
- `TestCases.StaticTest.UnitTest_StaticTest03`
- `TestCases.DelegateTest.DelegateTest43`

All 4 PASS on Legacy (plain Debug + useRegister=true) -> Neo-specific. The 4
CONVERGE at `:1432`, but the upstream cause of the null `this` DIFFERS per test
(this is NOT a single root). C4's earlier fix (GetType VTable fallback +
`.cctor` stale-TODO) is a different shape; this child extends the dispatch
surface around `:1432`.

## Root causes (re-audited against a REAL run)
1. **ReflectionTest04 / ReflectionTest19** -- `Type.GetType(string)` (static)
   had a Legacy redirect (`CLRRedirections.GetType`) registered on `RedirectMap`
   but NO Neo redirect on `RedirectMapNeo`. Neo dispatch consults `RedirectMapNeo`
   exclusively (child-2/6/22), so the call fell through to the host
   `System.Type.GetType`, which cannot resolve an IL type name -> returned null ->
   the following `t.GetMethod(...)` callvirt_CLR threw `this is null`. Same defect
   class as child-6 (`RuntimeHelpers.InitializeArray`) and child-22
   (`Activator.CreateInstance`). FIXED.
2. **StaticTest03** -- `dict.TryGetValue(1, out var ls)` returns true but `ls`
   stays null. The checked-in autogen Neo stub
   `System_Collections_Generic_Dictionary_2_Int32_List_1_Stri.cs::TryGetValue_1_Neo`
   is STALE: it was generated BEFORE the Step-13 Area-4c generator fix, so it
   reads the `out` param + calls the real method but NEVER writes the mutated
   value back to the caller's frame. The next `ls.Add(...)` callvirt_CLR throws
   `this is null`. The generator (`MethodBindingGenerator` / `AppendNeoWriteBackCode`)
   is ALREADY correct; only the checked-in stubs are stale. FIXED at the runtime
   layer (see design): route byref-param CLR calls to the reflection path
   (`CLRMethod.Invoke`), which owns the Area-4c write-back.
3. **DelegateTest43** -- the thread-safe static-event add accessor lowers to
   `Interlocked.CompareExchange<Action`3>(ref OnIntEvent, ...)` (a `ldsflda`
   IL-static-field byref). The reflection write-back parks the result, but
   `CopyNeoCallThisBack` does NOT propagate it back to the IL static field (the
   `ldsflda` static-field byref write-back gap, deferred from child-7). DISTINCT,
   deeper root. NOT FIXED here (reported).

## Scope of change (all Neo-gated -> Legacy-neutral)
- `ILRuntime/Runtime/Enviorment/CLRRedirections.cs` -- add `GetTypeNeo` redirect
  (mirrors Legacy `GetType`; reads param 0 = fullname, resolves via
  `AppDomain.GetType`, writes `t.ReflectionType` via `WriteNeoObjectResult`).
- `ILRuntime/Runtime/Enviorment/AppDomain.cs` -- register `GetTypeNeo` on
  `RedirectMapNeo` (in the existing `Type.GetType` static loop).
- `ILRuntime/CLR/Method/CLRMethod.cs` -- add `HasByRefParameter` +
  `ReflectionCannotHandleThis` cached properties.
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`
  (`InvokeNeoClrMethod`) -- route a byref-param CLR call to the reflection path
  (skip a possibly-stale autogen _Neo stub), EXEMPT when the declaring type is a
  CLR value type with reference fields (the async builders -- reflection NIEs on
  a ref-field struct `this`; those keep their hand-written Neo redirects).

## Verify (truth = full-smoke number)
- Name-filter: ReflectionTest04/19 + StaticTest03 PASS after the fix; stash-toggle
  each to HEAD -> FAIL (`Neo callvirt this is null`).
- FULL SMOKE: 86 -> 80 (delta -6; the byref-routing fix flips 3 additional
  stale-stub byref write-back failures elsewhere in the suite).
- NeoStep: 388/0 (no regression; the async exemption holds).
- Legacy-neutral: plain Debug build 0 errors (all edits Neo-gated).

## Out of scope (reported, NOT fixed)
- DelegateTest43 (static-field byref write-back in `CopyNeoCallThisBack` for the
  `ldsflda` + `Interlocked.CompareExchange` pattern -- child-7 `Ldsflda` lineage).
- The durable fix for the stale autogen stubs is to REGENERATE them (the generator
  is already correct); that is a test-harness regen, not a runtime change.
