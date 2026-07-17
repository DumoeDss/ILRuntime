# Design: neo-delegate-adapter-clr-cast

## Root cause (pinned with code evidence)

An IL delegate is represented in ILRuntime by a `DelegateAdapter` subclass
(`MethodDelegateAdapter<...>` / `FunctionDelegateAdapter<...>`), which derives
from `ILTypeInstance`. Each adapter holds a real CLR delegate field (`action` /
`func`) exposed via `IDelegateAdapter.Delegate`, and a `GetConvertor(Type)`
that produces a real CLR delegate of a requested type (short-circuit when
`type.IsAssignableFrom(NativeDelegateType)`, else `DelegateManager.ConvertToDelegate`
which looks up a registered converter).

Whenever an IL delegate crosses to CLR, the adapter must be CONVERTED to the real
CLR delegate. The conversion lives in `Extensions.CheckCLRTypes(this Type pt, object obj)`
(`Extensions.cs:281-288`):
```
else if ((typeFlags & TypeFlags.IsDelegate) != 0) {
    if (obj is Delegate) return obj;
    if (pt == typeof(Delegate)) return ((IDelegateAdapter)obj).Delegate;
    return ((IDelegateAdapter)obj).GetConvertor(pt);
}
```
Legacy applies `CheckCLRTypes` at every IL->CLR boundary. Neo omits it at two
sites, so the raw adapter hits a CLR `(ConcreteDelegate)v` cast and throws.

### Site A -- Neo Stsfld, CLR delegate static field

`ILIntepreter.Neo.cs:4600-4607` (the Stsfld CLR-static reference-field branch,
landed by child-3):
```
else {
    // Reference static field: the source register is a ref-slot mStack index.
    int srcRefIdx = *(int*)srcSlot;
    value = srcRefIdx >= 0 ? mStack[srcRefIdx] : null;   // <-- raw adapter
}
ct.SetStaticFieldValue(sIdx, value);                     // <-- (IntDelegate)value throws
```
Legacy parity (`ILIntepreter.Register.cs:3301-3308`):
```
CLRType t = type as CLRType;
var f = t.GetField(intVal);
...
t.SetStaticFieldValue(intVal, f.FieldType.CheckCLRTypes(CheckAndCloneValueType(...)));
```
The autogen Legacy setter `AssignFromStack_IntDelegateTest_0` ALSO converts:
`typeof(IntDelegate).CheckCLRTypes(StackObject.ToObject(...), IsDelegate)`.

### Site B -- autogen Neo CLR binding, delegate param

`StaticMethod_2_Neo` (committed): `(System.Action)ILIntepreter.ReadNeoReference(...)`.

Generator NOW emits (Step-19 fix, `BindingGeneratorExtensions.cs:238-251`):
```
if (typeof(Delegate).IsAssignableFrom(pt))
    "... ({realClsName})typeof({realClsName}).CheckCLRTypes(ReadNeoReference(...), (TypeFlags)8);"
else
    "... ({realClsName})ReadNeoReference(...);"
```
`MethodBindingGenerator.cs:296-302` mirrors this for the instance `this` arg.
Legacy bindings already emit the same. The committed Neo bindings are STALE
(child-28 class).

## The fix

### Fix C (engine, Site C -- INSTANCE field/property; surfaced by DelegateTest41)

DelegateTest41 stores an IL delegate into a CLR INSTANCE field/property
(`BindableProperty<long>.OnChangeWithOldVal`, a custom `onChangeWithOldVal<long>`
delegate). The raw Stfld CLR-ref-owner branch (`ILIntepreter.Neo.cs:4324`) routes
through `NeoWriteClrObjectField(appdomain, target, fieldHash, value)`, which
called `ct.SetFieldValue(fieldHash, ref tmp, value)` with the raw adapter ->
`set_OnChangeWithOldVal_0` `(onChangeWithOldVal)v` throws. Fix: in
`NeoWriteClrObjectField`, resolve the field via `ct.GetField(fieldHash)` and
convert `value = f.FieldType.CheckCLRTypes(value);` before `SetFieldValue`
(mirrors Fix A; CheckCLRTypes is total, null-safe via the `f != null` guard).

### Fix A (engine, Site A) -- Neo-gated, one line

In the Neo Stsfld CLR-static reference-field branch, convert the value before
the store (mirrors Legacy `Register.cs:3308`):
```
else {
    int srcRefIdx = *(int*)srcSlot;
    value = srcRefIdx >= 0 ? mStack[srcRefIdx] : null;
    // C1: an IL delegate arrives as a MethodDelegateAdapter/FunctionDelegateAdapter
    // (an ILTypeInstance subclass); a CLR delegate FIELD expects the real CLR
    // delegate. Convert via CheckCLRTypes(fieldType) -- mirrors Legacy
    // ExecuteR Stsfld (Register.cs:3308) f.FieldType.CheckCLRTypes(...) and the
    // autogen Legacy AssignFromStack_* setter. Also unwraps an ILTypeInstance to
    // its CLRInstance for a CLR-base-typed field (parity, no behavior change for
    // matching types). Neo-only path.
    value = f.FieldType.CheckCLRTypes(value);
}
ct.SetStaticFieldValue(sIdx, value);
```
`CheckCLRTypes` is total: a real delegate passes through (`obj is Delegate`);
an adapter is converted; an `ILTypeInstance` for a CLR-base field is unwrapped
to `CLRInstance`; a plain matching object is returned as-is. Safe for every
reference static field category.

### Fix B (bindings, Site B) -- hand-port stale committed bindings

Hand-port the delegate-param reads in the committed Neo bindings the C1 cluster
exercises, from `(T)ILIntepreter.ReadNeoReference(...)` to
`(T)typeof(T).CheckCLRTypes(ILIntepreter.ReadNeoReference(...), (ILRuntime.CLR.Utils.Extensions.TypeFlags)8)`
-- byte-identical to what the current generator emits. The `#else` Legacy halves
and the `#if ENABLE_NEO_MODE` framing are left intact, so Legacy is byte-for-byte
unchanged.

Files (the bindings the C1 call-arg tests hit, verified by stack trace):
- `ILRuntimeTest_TestBase_StaticGenericMethods_Binding.cs` -- delegate params of
  `StaticMethod` / `FunctionMethod` overloads (GenericStaticMethodTest1-8).
- `ILRuntimeTest_TestBase_GenericExtensions_Binding.cs` -- delegate params of the
  `ExtendMethod` overloads (GenericExtensionMethod1/2Test*).
- Any further binding surfaced by a residual C1 test (GenericMethodTest14 /
  Test03.Test05 / UnitTest_10046) -- patched on evidence.

No generator change (already correct). No full regen (GUI-bound); latent stale
bindings not exercised by C1 are noted as a follow-up, not forced here.

## Why this is Legacy-neutral

- Fix A is entirely inside the Neo Stsfld arm (`ILIntepreter.Neo.cs`, the
  file-gated Neo interpreter). Legacy compiles none of it.
- Fix B edits only the `#if ENABLE_NEO_MODE` bodies of the autogen bindings; the
  `#else` Legacy halves (which already call `CheckCLRTypes`) are untouched.
- The shared conversion helper (`Extensions.CheckCLRTypes`) is unchanged.

## Verification plan

1. Build CLI (`Debug_Neo`, `--no-incremental` after touching ILRuntimeTestBase)
   + TestCases (`Debug`).
2. Name-filtered fast iteration: `DelegateTest01`, `GenericStaticMethodTest1`,
   `GenericExtensionMethod1Test1` under Neo -> PASS after the fix.
3. Stash-toggle: stash the engine + binding edits, rebuild -> FAIL; pop -> PASS.
4. NeoStep smoke (380/0 baseline) -> unchanged (no regression).
5. Legacy-neutral: `DelegateTest01` + a C1 call-arg test under plain
   `Debug` + `useRegister=true` -> still PASS.
6. FULL smoke (no filter) -> record the `Ran N tests, M failded` delta.
   Expect 189 -> lower, with the C1 cluster flipping green.

## Eliminated hypotheses (mandatory)

- NOT a `DelegateManager` registration gap: the converters ARE registered
  (`RegisterMethodDelegate`/`RegisterFunctionDelegate` register
  `RegisterDelegateConvertor<Action/Func>(defaultConverter)`; custom delegates
  like `IntDelegate` are registered by the test harness -- Legacy passes, proving
  the converter exists). `GetConvertor` resolves them.
- NOT a `castclass`/`unbox` arm bug: the Neo castclass arm (`Neo.cs:4998`) keeps
  the adapter on success (it never throws for a matching delegate); the throws
  are in the autogen setter / binding, not castclass. (Tested: the C1 stacks
  show `set_*` / `*_Neo`, never the castclass arm.)
- NOT the reflection fallback: `CLRMethod.Invoke:519-524` already converts
  delegates. Only the autogen `RedirectionNeo` path leaks the adapter.
- NOT a generator bug (Site B): the generator is correct; the bindings are stale.
  Confirmed by reading `BindingGeneratorExtensions.cs:244-246`.
