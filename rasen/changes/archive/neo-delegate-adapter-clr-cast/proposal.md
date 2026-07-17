# Proposal: neo-delegate-adapter-clr-cast

Wave-2 child C1 of the `neo-overhaul` portfolio. Targets the ~32-test
`InvalidCastException` cluster where a Neo `MethodDelegateAdapter` /
`FunctionDelegateAdapter` (ILRuntime's IL-delegate bridge, which derives from
`ILTypeInstance`) reaches a CLR cast/store site UN-converted, where a real CLR
delegate (`Action` / `Func` / a custom delegate type) is expected.

## Problem (grounded in a real Neo run)

Full Neo smoke grounding (2026-07-13): 914 ran / 189 failed. The dominant
exception is `InvalidCastException` (56). Of those, ~32 share the exact message
shape:

> Unable to cast object of type
> 'ILRuntime.Runtime.Intepreter.MethodDelegateAdapter`1[System.Int32]' to type
> 'System.Action`1[...]' (and FunctionDelegateAdapter -> Func).

Representative failing tests: `DelegateTest01/03/06/07/11/18/19/20/21/41/43/45`,
`DelegateInnerTest.TestRun`, `GenericStaticMethodTest1-8`,
`GenericExtensionMethod1Test1/2`, `GenericExtensionMethod2Test1-8`,
`GenericMethodTest14`, `Test03.Test05`, `TestValueTypeBinding.UnitTest_10046`.

The SAME tests PASS under Legacy (plain `Debug` + `useRegister=true`; the 518/519
Legacy baseline). So this is a Neo-specific regression, and Legacy is the
working reference.

## Root cause (two sites, ONE defect class -- re-audited, not trusted)

Everywhere an IL delegate crosses the IL->CLR boundary, the adapter must be
converted to a real CLR delegate via `CheckCLRTypes(fieldOrParamType)` (which
calls `IDelegateAdapter.GetConvertor` -> `DelegateManager.ConvertToDelegate`).
Legacy applies this at every boundary; Neo omits it at two sites.

### Site A -- Stsfld on a CLR delegate static field (confirmed: DelegateTest01)

Stack (Neo, `DelegateTest01`):
```
InvalidCastException at ILRuntimeTest_TestFramework_DelegateTest_Binding.set_IntDelegateTest_0(Object& o, Object v):472  -> (IntDelegate)v
  at CLRType.SetStaticFieldValue(hash, value):461
  at ILIntepreter.ExecuteNeo(...):4607   <-- Stsfld CLR-static-field arm
```
The Neo Stsfld CLR-static reference-field branch (`ILIntepreter.Neo.cs:4600-4607`)
reads the value straight from mStack and calls `ct.SetStaticFieldValue(sIdx, value)`
with the raw adapter. The autogen setter `set_IntDelegateTest_0` does
`(IntDelegate)v` directly -- no conversion -> throw.

Legacy mirrors this correctly (`ILIntepreter.Register.cs:3308`):
`t.SetStaticFieldValue(intVal, f.FieldType.CheckCLRTypes(...))`, and the autogen
Legacy setter `AssignFromStack_IntDelegateTest_0` does
`typeof(IntDelegate).CheckCLRTypes(obj, IsDelegate)`.

Fix: in the Neo Stsfld CLR-static ref branch, wrap the value with
`f.FieldType.CheckCLRTypes(value)` before `SetStaticFieldValue` (mirrors Legacy
exactly). One line.

### Site B -- autogen Neo CLR binding reads a delegate param (confirmed: GenericStaticMethodTest1)

Stack (Neo, `GenericStaticMethodTest1`):
```
InvalidCastException at StaticGenericMethods_Binding.StaticMethod_2_Neo:250  -> (System.Action)ReadNeoReference(...)
  at ILIntepreter.InvokeNeoClrMethod:1098
  at ILIntepreter.ExecuteNeo:3181   <-- Call_Redirect arm
```
The autogen Neo binding `StaticMethod_2_Neo` reads its delegate param with a raw
cast `(System.Action)ILIntepreter.ReadNeoReference(...)` -- no `CheckCLRTypes`.

CRITICAL: the binding GENERATOR is ALREADY CORRECT.
`BindingGeneratorExtensions.cs:244-246` and `MethodBindingGenerator.cs:296-298`
emit `typeof(T).CheckCLRTypes(ReadNeoReference(...), IsDelegate)` for delegate
params (the Step-19 generator fix, with a doc comment explaining why). The Legacy
binding `StaticMethod_2` also emits it. The COMMITTED Neo bindings are STALE --
they predate the generator's Step-19 delegate fix and were never regenerated
(regen is GUI-bound, per child-28). This is exactly the child-28 defect class
(stale committed autogen bindings).

Neo's reflection fallback is NOT affected: `CLRMethod.Invoke:519-524` already
converts (`if typeof(Delegate).IsAssignableFrom(t) param[i] = t.CheckCLRTypes(...)`).
So only the autogen `RedirectionNeo` path leaks the raw adapter.

Fix: hand-port the stale delegate-param reads in the committed bindings hit by
the C1 cluster to match the current generator template (wrap with
`typeof(T).CheckCLRTypes(..., IsDelegate)`). NO generator change (already
correct). NO regen (GUI-bound).

## Success metric

The full Neo smoke failure count DROPS from 189, with the C1 cluster tests
flipping green. NeoStep smoke stays 380/0 (no regression). Legacy-neutral (the
Stsfld fix is Neo-gated `#if ENABLE_NEO_MODE`; the binding hand-port touches
`#if ENABLE_NEO_MODE` regions only -- the `#else` Legacy halves are untouched).

## Out of scope

- Custom-delegate `Delegate.CreateDelegate` "Type must be a runtime Type"
  (cluster C12, separate child).
- CLR-bound delegate field INSTANCE stores (`Stfld` on a CLR object's delegate
  field) if they surface outside the C1 cluster -- note + defer.
- Regen of all autogen bindings (GUI-bound); this child hand-ports only the
  bindings the C1 cluster actually exercises.
