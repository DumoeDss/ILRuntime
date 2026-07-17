# Design -- neo-callvirt-this-null-residual

## Phase 1 -- RE-AUDIT (the 4 tests at the 86-baseline, verified against a REAL run)

Build: `dotnet build ILRuntimeTestCLI -c Debug_Neo --no-incremental` (0 errors) +
`dotnet build TestCases -c Debug` (0 errors). Full smoke at HEAD:
`Ran 922 tests, 86 failed`. The 4 target tests all fail with
`NullReferenceException: Neo callvirt this is null` at
`ILIntepreter.Neo.cs:ResolveNeoCallvirtCLRTarget:1432` (the `ReadNeoCallThis`
guard). All 4 PASS on Legacy (plain Debug + useRegister=true) -> Neo-specific.

`ReadNeoCallThis` (ILIntepreter.Neo.cs:1344) reads the callvirt_CLR `this` slot:
`thisIdx = *(int*)(targetBase + thisArgOff)`; throws "Neo callvirt this is null"
when `thisIdx < 0 || thisIdx >= mStack.Count` OR `mStack[thisIdx] == null`. So the
guard is honest -- the `this` slot genuinely holds the null sentinel. The 4 tests
CONVERGE here but the upstream producer of the null `this` DIFFERS per test.

### Test 1+2: ReflectionTest04 (ReflectionTest.cs:45) / ReflectionTest19 (:538)
- ReflectionTest04: `var t = Type.GetType("..."); var method = t.GetMethod("foo");`
- ReflectionTest19: `var mi = t.GetMethod(nameof(ReflectionTest19)); if (!mi.IsStatic)...`
- Local-var dump: `Type t = null`. The callvirt that throws is `t.GetMethod(...)`.
- PINNED ROOT: `Type.GetType(string)` (STATIC) is registered ONLY on Legacy's
  `RedirectMap` (AppDomain.cs:201 `RegisterCLRMethodRedirection(i, CLRRedirections.GetType)`),
  NOT on `RedirectMapNeo`. Neo dispatch consults `RedirectMapNeo` exclusively
  (child-2/6/22). With no Neo entry the call fell through to the host
  `System.Type.GetType(string)`, which cannot resolve an IL type name -> returned
  null -> `t` is null -> `t.GetMethod(...)` callvirt_CLR -> `this is null`.
- SAME defect class as child-6 (RuntimeHelpers.InitializeArray) and child-22
  (Activator.CreateInstance): a hand-written Legacy redirect missing a Neo twin.

### Test 3: StaticTest.UnitTest_StaticTest03 (StaticTest.cs:69)
- `if (dict.TryGetValue(1, out var ls)) { ls.Add("ggg"); ... }`
- Local-var dump: `List`1 ls = null, Boolean v1 = True`. TryGetValue returned
  TRUE (key found) but `ls` is null -> `ls.Add(...)` callvirt_CLR -> `this is null`.
- Instrumented diagnostic (temp stderr in CLRMethod.cs Area-4c reference write-back
  + ILIntepreter.Neo.cs CopyNeoCallThisBack): the Area-4c write-back NEVER ran for
  this call; CopyNeoCallThisBack copied `copiedInt=-1` (the null sentinel) from a
  slot nobody wrote. => `clrMethod.Invoke` (the reflection path that owns Area-4c)
  was NOT entered.
- PINNED ROOT: the checked-in autogen Neo stub
  `ILRuntimeTestBase/AutoGenerate/System_Collections_Generic_Dictionary_2_Int32_List_1_Stri.cs::TryGetValue_1_Neo`
  is STALE. It reads the `out` param via `ReadNeoReference` + calls the real
  `TryGetValue` + writes ONLY the `bool` return to `__retDst`, but NEVER writes the
  mutated `@value` back to the caller's frame. The CURRENT generator
  (`MethodBindingGenerator.GenerateMethodWraperCode_Neo` -> `AppendNeoWriteBackCode`
  in BindingGeneratorExtensions.cs:265, plus the `int __off_<idx> = __curPrim;`
  capture in `AppendArgumentCodeNeo` :158) EMITS the write-back correctly. The
  checked-in stubs were generated BEFORE that generator fix -> stale. The generator
  is correct; only the checked-in stubs lag. (Regenerating the stubs is a
  test-harness concern, not a runtime change; this child fixes it at the runtime
  layer instead.)

### Test 4: DelegateTest43 (DelegateTest.cs:681)
- `OnIntEvent += DelegateTest43Sub; OnIntEvent(1, 2, 3); ...` (`OnIntEvent` is an
  IL `static event Action<float,double,int>`).
- The `+=` lowers to the thread-safe event accessor `add_OnIntEvent`, whose body is
  `OnIntEvent = Delegate.Combine(...)` inside an `Interlocked.CompareExchange<Action`3>
  (ref OnIntEvent, value, comparand)` CAS loop (compiler-generated `ldsflda`
  IL-static-field byref).
- Instrumented diagnostics: `DelegateCombineNeo(null, MethodDelegateAdapter`3)`
  correctly returns the adapter; `castclass Action`3` converts it to a REAL
  `System.Action`3`; the CompareExchange reflection write-back (Area-4c) RUNS and
  parks the Action at `mStack[15]` (slotOff=0). But `OnIntEvent` stays null ->
  `ldsfld OnIntEvent` for the Invoke reads null -> `this is null`.
- PINNED ROOT: `CopyNeoCallThisBack` does NOT propagate the CompareExchange
  byref write-back to the IL static field -- the `ldsflda` IL-static-field byref
  write-back gap (the static-field form of the Ldsflda case deferred from child-7).
  DISTINCT, deeper root. NOT fixed here.

## Phase 2 -- the fixes (all Neo-gated -> Legacy-neutral)

### Fix A: GetTypeNeo (CLRRedirections.cs + AppDomain.cs) -- for tests 1+2
Mirror the Legacy `CLRRedirections.GetType` (CLRRedirections.cs:138): read param 0
(the fullname string, in declaration order via the Neo cursor), resolve via
`AppDomain.GetType(fullname)`, write `t.ReflectionType` via `WriteNeoObjectResult`
(null -> -1 sentinel). Registered on `RedirectMapNeo` in the existing
`typeof(System.Type).GetMethods()` loop, `if (i.Name == "GetType" && i.IsStatic)`.
Param 0 is the fullname for every static GetType overload; the extra bool params
(throwOnError / ignoreCase) are ignored (we return the null sentinel when the type
is not found = throwOnError=false behavior). Same pattern as ObjectGetTypeNeo (C4).

### Fix B: route byref-param CLR calls to reflection (CLRMethod.cs + ILIntepreter.Neo.cs) -- for test 3
In `InvokeNeoClrMethod`, when `clrMethod.HasByRefParameter` is true, skip the
autogen Neo redirect and fall through to `clrMethod.Invoke` (the reflection path),
which owns the Area-4c byref write-back. The reflection path is the AUTHORITATIVE
write-back path (it is the existing fallback when no redirect is registered), so
routing byref calls there is correct regardless of stub staleness.

EXEMPTION (load-bearing -- without it, 22 NeoStep20 async tests regress): when the
declaring type is a CLR value type WITH reference fields
(`clrMethod.ReflectionCannotHandleThis`), KEEP the redirect. The async builder
methods (`AsyncTaskMethodBuilder`1/AsyncVoidMethodBuilder/AsyncValueTaskMethodBuilder`1`
-- ref-field CLR structs, invoked byref) have HAND-WRITTEN Neo redirects
(`CLRRedirections.AsyncNeo.cs`) that the reflection path cannot replace
(`CLRMethod.Invoke` NIEs on a ref-field struct `this` -- the Step-13 Area-4b NIE).
`ReflectionCannotHandleThis` reuses the existing `NeoClrStructHasReferenceField`
guard. For a static method on a ref-field struct this is over-conservative (keeps
the redirect) but that is no worse than HEAD and avoids the async regression.

Two cached CLRMethod properties (lazily computed, one-time scan):
- `HasByRefParameter` -- any formal param `ParameterType.IsByRef`.
- `ReflectionCannotHandleThis` -- declaring type is CLR struct with ref fields.

The earlier `redirectNeo != null` fast path becomes
`redirectNeo != null && (!HasByRefParameter || ReflectionCannotHandleThis)`.

## Scope of code change
- `ILRuntime/Runtime/Enviorment/CLRRedirections.cs` -- +24 lines (`GetTypeNeo`).
- `ILRuntime/Runtime/Enviorment/AppDomain.cs` -- +6 lines (Neo registration).
- `ILRuntime/CLR/Method/CLRMethod.cs` -- +2 cached properties (~60 lines w/ comments).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- ~10-line guard
  rewrite in `InvokeNeoClrMethod` (no new opcode, no JIT/optimizer change).
All edits Neo-only (`#if ENABLE_NEO_MODE` or Neo-only call sites).

## Verify (truth = full-smoke number; rigorously diffed vs a REAL HEAD run)
- Stash-toggle: at HEAD, ReflectionTest04/ReflectionTest19/UnitTest_StaticTest03
  each FAIL with `Neo callvirt this is null`; with the fix, 0/0 each. DelegateTest43
  fails at BOTH HEAD and with the fix (distinct root).
- **FULL SMOKE: 86 -> 80 (delta -6, 0 regressions).** Diff vs a real HEAD full
  smoke (922/86). 6 flipped green:
  `ReflectionTest04`, `ReflectionTest19`, `UnitTest_StaticTest03` (Fix A + B
  direct targets), plus `ArrayTest.ArrayBindTest`, `RefOutTest.UnitTest_OutTest`,
  `Test05.UnitTest_Out2` (Fix B -- 3 additional stale-stub byref write-back
  failures elsewhere in the suite). Regressions (in 80 but not 86): NONE (empty
  diff).
- NeoStep: **388/0** (no regression; the async exemption holds -- without it,
  routing the async builder methods to reflection regresses 22 NeoStep20 tests
  with the Area-4b ref-field-struct NIE).
- Legacy-neutral: plain Debug build **0 errors** (all edits Neo-gated).

## NOT fixed (reported, distinct roots)
- DelegateTest43 -- the `ldsflda` IL-static-field byref write-back gap in
  `CopyNeoCallThisBack` (CompareExchange/CAS on a static field). Lineage: child-7
  Ldsflda (deferred). Evidence: the reflection Area-4c write-back runs and parks
  the result, but the static field is never updated.
- The stale autogen stubs broadly -- the durable fix is to REGENERATE them (the
  generator is already correct). This child's runtime routing makes the staleness
  irrelevant for byref-param calls (the reflection path is authoritative).
