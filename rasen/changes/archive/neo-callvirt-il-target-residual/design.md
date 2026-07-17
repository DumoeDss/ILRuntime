# Design -- neo-callvirt-il-target-residual (Wave-2 child)

## Scope
The `ResolveNeoCallvirtILTarget:1366` NRE sub-cluster reported by the NRE-residual
and C16 audits (3 tests). The audit's line number (1366) is slightly stale vs HEAD
`bf76ad62`; the real throw lives in `ReadNeoCallThis` and is reached via
`ResolveNeoCallvirtILTarget`. This child re-audits against the REAL current full smoke
(baseline 63), pins the exact root cause, fixes it, and verifies with a full-smoke delta.

## Phase 1 -- RE-AUDIT (verified at the 63 baseline)

Fresh `Debug_Neo` CLI + `Debug` TestCases builds, full Neo smoke baseline:
`Ran 928 tests, 63 failed` (exit 127 = the known pre-existing mid-stream crash).

The 3 tests failing with a raw `NullReferenceException` whose top ILRuntime frame is
`ResolveNeoCallvirtILTarget` (each appears twice in the log: original + "Rethrown as"):

1. `TestCases.DelegateTest.DelegateTest16` (DelegateTest.cs:194) -- `a.GetType()` on an
   `Action a = () => {};`. Failing instruction:
   `callvirt System.Type System.Object::GetType()`, `vslot=65535, thisArg=0`.
2. `TestCases.DelegateTest.DelegateTest17` (DelegateTest.cs:201) -- `a.GetHashCode()` on
   an `Action a = () => {};`. Failing instruction:
   `callvirt System.Int32 System.Object::GetHashCode()`, `vslot=65535, thisArg=0`.
3. `TestCases.SimpleTest.EqualsTest` (SimpleTest.cs:185) -- `act1.Equals(obj)` where
   `act1` is an `Action`. Failing instruction:
   `callvirt System.Boolean System.Object::Equals(System.Object)`, `vslot=65535, thisArg=0`.

All three are an inherited `System.Object` virtual/non-virtual method called via `callvirt`
on an `Action` delegate `this`, dispatched through the plain `Callvirt` opcode ->
`ResolveNeoGenericCallvirtTarget` -> `ResolveNeoCallvirtILTarget`.

### Why it is NOT the C4 "callvirt this is null" residual
C4 (child `neo-callvirt-gettype-vtable`) added a fallback in `ResolveNeoCallvirtILTarget`
for an inherited non-virtual CLR method on an IL instance: when `TryGetNeoVTableSlot` misses
and `declaredMethod is CLRMethod`, return the CLRMethod (CLR dispatch serves
`ObjectGetTypeNeo` for GetType). That fallback is still present and correct, but it sits
AFTER the `TryGetNeoVTableSlot` call. Here the NRE fires INSIDE the
`instance.Type.TryGetNeoVTableSlot(declaredMethod, out slot)` evaluation (Neo.cs:1382)
before the miss-fallback can run, because `instance.Type` is null.

### Instrumented diagnostic (temporary throw at Neo.cs:1382, stash-toggle confirmed)
Dumped `thisObj`/`instance`/`instance.Type`/`declaredMethod` for all 3 tests. IDENTICAL
shape:
```
thisObjType = ILRuntime.Runtime.Intepreter.MethodDelegateAdapter
instType    = ILRuntime.Runtime.Intepreter.MethodDelegateAdapter
inst.Type   = <NULL TYPE>
declaredMethod = ILRuntime.CLR.Method.CLRMethod sig=GetType|0()->System.Type  decl=System.Object
                (GetHashCode|0()->System.Int32 / Equals|0(System.Object)->System.Boolean)
```

## Root cause (pinned, Neo-vs-Legacy + stack evidence)

`DelegateAdapter` (DelegateAdapter.cs:932) is declared `abstract class DelegateAdapter :
ILTypeInstance, IDelegateAdapter`. Every delegate adapter (`MethodDelegateAdapter`,
`FunctionDelegateAdapter<...>`, `DummyDelegateAdapter`) therefore IS an `ILTypeInstance`.
The `DelegateAdapter(Enviorment.AppDomain, ILTypeInstance, ILMethod)` constructor
(DelegateAdapter.cs:953-959) sets `appdomain`/`instance`/`method`/`CLRInstance` but NEVER
assigns the inherited `ILTypeInstance.type` field -> `instance.Type` is null for every
delegate adapter.

Dispatch path for `adapter.GetType()` (and GetHashCode/Equals):
- Plain `Callvirt` opcode (Neo.cs:3974) -> `ResolveNeoGenericCallvirtTarget` (Neo.cs:1455).
- `ResolveNeoGenericCallvirtTarget` reads `thisObj = ReadNeoCallThis(...)` then tests
  `if (thisObj is ILTypeInstance)`. A delegate adapter satisfies this (it subclasses
  ILTypeInstance) -> routes to `ResolveNeoCallvirtILTarget` (Neo.cs:1459).
- `ResolveNeoCallvirtILTarget` (Neo.cs:1374) pattern-matches
  `if (thisObj is ILTypeInstance instance)` (true), computes `slot = 0xffff`, then at
  Neo.cs:1382 evaluates `instance.Type.TryGetNeoVTableSlot(declaredMethod, out slot)`.
  `instance.Type` is null -> **raw NRE** ("Object reference not set").

Note `ReadNeoCallThis` carries `[MethodImpl(AggressiveInlining)]` and the throw frame is
reported at the inlined call site inside `ResolveNeoCallvirtILTarget`; the audit's ":1366"
label maps to this site (line drifted slightly since the audit was written).

This is Neo-specific: Legacy (`ExecuteR`, ILIntepreter.Register.cs) resolves callvirt via
the CLR binding map keyed on the runtime object -- a delegate adapter calling an inherited
Object method is served directly by the registered CLR method on the adapter (no IL VTable
lookup, no `Type` deref). All 3 tests PASS under plain `Debug` + `useRegister=true`.

## The fix (Neo-gated, ~18 lines, single file)

Add a guard at the top of the `if (thisObj is ILTypeInstance instance)` body in
`ResolveNeoCallvirtILTarget` (before the `slot` computation): if `instance.Type == null`,
the instance is a delegate adapter (a genuine IL instance always has a non-null `Type`, set
in every ILTypeInstance constructor -- so `Type == null` uniquely identifies the
delegate-adapter shape). When the declared method is a CLRMethod (the inherited Object
method), return it for CLR dispatch -- the SAME fallback C4 uses at the
`TryGetNeoVTableSlot` miss. The caller (`ResolveNeoGenericCallvirtTarget` or the
`Callvirt_IL` arm at Neo.cs:3806) passes the CLRMethod to `InvokeNeoCallTarget` ->
`InvokeNeoClrMethod`, which serves the registered Neo redirect (`ObjectGetTypeNeo` for
GetType, returning the runtime type) or the reflection fallback (GetHashCode/Equals run the
real Object method on the adapter object). If `declaredMethod` is NOT a CLRMethod (a genuine
IL method on a typeless adapter -- unreachable, since the delegate-Invoke path is handled
earlier by `IsDelegateInvoke`), throw a clear `MissingMethodException` instead of NRE'ing.

The guard covers BOTH call sites of `ResolveNeoCallvirtILTarget` (the plain-Callvirt path
via `ResolveNeoGenericCallvirtTarget:1459` AND the direct `Callvirt_IL` arm at Neo.cs:3806).

No JIT / optimizer / object-model / binding change. The change is entirely inside
`ILIntepreter.Neo.cs` (file-gated `#if ENABLE_NEO_MODE`) -> Legacy compiles none of it.

## Scope of code change
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`
  `ResolveNeoCallvirtILTarget`: +1 guard block (~18 lines incl. comment), 0 removed.

## Verify (truth = full-smoke number)
- **Full smoke: 63 -> 60 (delta -3, 0 real regressions).**
  Command: `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build --
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true`
  (exit 127 = known pre-existing mid-stream crash, unrelated).
- **Flipped green (3):** `DelegateTest16`, `DelegateTest17`, `SimpleTest.EqualsTest`.
- **Newly failing (regressions): 0.** A first postfix run showed 61 (a flaky
  `InheritanceTest07` "VMethod3 is not bound!" in ExecuteR -- a C9-cluster binding-config
  artifact, NOT in ExecuteNeo, NOT my code path; passes when run alone). A second postfix
  run reproduced 60 with InheritanceTest07 absent and postfix2's fail set a strict subset
  of postfix1's -> the InheritanceTest07 blip is crash-order non-determinism, not a
  regression.
- **Stash-toggle (airtight):** disable the guard (`if (false && instance.Type == null)`) ->
  rebuild -> 3/3 FAIL (NRE returns) -> restore -> rebuild -> 3/3 PASS. Proves causation.
- **NeoStep: 394/0** (no regression).
- **Legacy-neutral:** plain `Debug` build 0 errors (the edit compiles out under Legacy);
  all 3 tests PASS on Legacy (`Debug` + `useRegister=true`).

## Durable findings
- **Delegate adapters are ILTypeInstance subclasses with `Type == null`.**
  `DelegateAdapter : ILTypeInstance` (DelegateAdapter.cs:932); its ctor (953-959) never sets
  the inherited `type` field. So `someDelegateAdapter is ILTypeInstance` is TRUE, but
  `.Type` is null. ANY code that pattern-matches `is ILTypeInstance` and then derefs
  `.Type` will NRE on a delegate-adapter `this`. The two existing Neo callvirt resolvers
  that do this are `ResolveNeoCallvirtILTarget` (fixed here) and the discriminator in
  `ResolveNeoGenericCallvirtTarget` (Neo.cs:1458). A future, more principled fix could make
  `ResolveNeoGenericCallvirtTarget` treat a `DelegateAdapter` as a CLR object (fall through
  to `if (declaredMethod is CLRMethod) return declaredMethod;`) BEFORE the
  `is ILTypeInstance` test, but the in-place `Type == null` guard is the minimal, defensive
  fix that covers both call sites without perturbing the discriminator's ordering.
- **Inherited System.Object methods on a delegate adapter are the canonical trigger.**
  `del.GetType()` / `del.GetHashCode()` / `del.Equals(x)` / `del.ToString()` on an
  IL-method-backed delegate (`Action`/`Func`) lower to plain `callvirt Object::X` with
  `vslot=65535`. The delegate-Invoke case (`del(args)`) is intercepted earlier by the
  `IsDelegateInvoke` check, so only the inherited-Object-method shape reaches the resolver.
- **Full-smoke crash-order non-determinism (exit 127):** the pre-existing mid-stream crash
  makes the exact fail set vary by +/-1 between runs near the crash boundary. A single
  full-smoke run is NOT authoritative for a +/-1 delta -- run it twice and take the stable
  set. A "regression" that (a) throws in `ExecuteR` (not `ExecuteNeo`), (b) is a binding-
  config "is not bound" message, and (c) PASSES when run alone is crash-order noise, not a
  real regression.
