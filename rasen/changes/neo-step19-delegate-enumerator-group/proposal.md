# Proposal: neo-step19-delegate-enumerator-group

Wave-2 child of `neo-overhaul`. Re-audit the 4 full-smoke failures the ground-13
child flagged as "all touch Step-19 delegate/enumerator dispatch -- worth a re-audit
for a shared root that could multi-flip": DelegateTest42, UnitTest_10046,
UnitTest_10051, MyTest.Test.

## Mandate / scope

The ground-13 handoff listed these 4 as possibly sharing a Step-19 root. This child
VERIFIES that claim against a real run and fixes whatever is tractable.

## Re-audit finding: the 4 are DISTINCT roots (no shared multi-flip root)

Each was reproduced by name-filter under Neo (Debug_Neo) + confirmed PASS on Legacy
(plain Debug + useRegister=true). Exact failure sites:

1. **DelegateTest42** -- `throw` at DelegateTest.cs:662
   (`testDele.Target != cls2`). Locals `v2=F,v3=F,v4=T,v5=F`: the 4 `get_Target`
   reads; only v4 (an IL-delegate wrapping an IL *instance* method on
   `DelegateTestCls`) is wrong. v2/v3 use real CLR Delegates and pass. ROOT =
   `System.Delegate.get_Target` has a Legacy redirect
   (`CLRRedirections.DelegateGetTarget`, AppDomain.cs:354) but NO Neo twin ->
   the JIT emits a raw `callvirt.clr get_Target` (no RedirectMapNeo entry ->
   no Call_Redirect) and the reflection fallback returns the wrong object for an
   IDelegateAdapter-held IL delegate. The classic child-2/6/22 "missing Neo
   redirect twin" defect class. **TRACTABLE.**

2. **UnitTest_10046** -- `throw` at TestValueTypeBinding.cs:470 (`a.X != 2`) inside
   the delegate body `UnitTest_10046Sub(TestVector3 a)` invoked via
   `TestVector3.DoTest2()` -> `TestDelegate2(TestVector3.One)`. A CLR delegate
   wrapping an IL *static* method, invoked with a by-value struct arg. ROOT = the
   delegate-INVOKE struct-arg marshalling path (Step-19), DISTINCT from #1 (which
   is get_Target read, not invoke). Needs its own JIT-dump triage.

3. **UnitTest_10051** -- `throw` at TestValueTypeBinding.cs:619
   (`list[0].V2.x.RawValue != 999`). ROOT = constrained-callvirt property read on a
   NESTED struct field (`Fixed64Vector2.x.RawValue` -- `.x` is a Fixed64 struct
   field, `.RawValue` a property on it). The F-10 child ALREADY pinned this as a
   SEPARATE pre-existing Neo gap (not F-10, not delegate): "reading a struct
   field's PROPERTY via `.x.RawValue`". The Sort lambda is a delegate but the
   delegate dispatch itself is fine; the property-read inside it is the gap.

4. **MyTest.Test** -- `InvalidCastException String->IEnumerator<KeyValuePair<int,int>>`
   at autogen `get_Current_0_Neo:48` <- InvokeNeoClrMethod Neo.cs:1331. ROOT =
   the enumerator `this` register is corrupted to a String on a later loop
   iteration (register/frame aliasing in the `while(e.MoveNext()){ e.Current... }`
   loop, where `e.Current.Key + "" + e.Current.Value` produces String temps).
   DISTINCT (a liveness/aliasing bug, not delegate/enumerator dispatch).

**Verdict:** no shared root. #1 is the most tractable (well-known defect class,
~15-line mirror of Legacy). This child FIXES #1 and reports #2/#3/#4 as distinct
deep roots for future children.

## What changes (the fix for #1)

- `ILRuntime/Runtime/Enviorment/CLRRedirections.cs`: add `DelegateGetTargetNeo`
  (reads the `this` ref, returns `((IDelegateAdapter)dele).Instance` for an adapter
  else `((Delegate)dele).Target`, null-aware write via `WriteNeoObjectResult`).
- `ILRuntime/Runtime/Enviorment/AppDomain.cs`: register it on `RedirectMapNeo`
  right after the Legacy get_Target registration (under `#if ENABLE_NEO_MODE`).

Neo-gated -> Legacy-neutral by construction. Mirrors Legacy semantics exactly.

## Out of scope (distinct roots, future children)

- #2 delegate-invoke struct-arg marshalling (Step-19 invoke path).
- #3 nested-struct-field constrained-callvirt property read (`.x.RawValue`).
- #4 enumerator-loop `this`-register aliasing to a String.
- Surfaced: `Delegate.op_Equality` / `op_Inequality` ALSO have no Neo twin
  (AppDomain.cs:235-242 register only Legacy) -- a latent sibling; not in the 13.
