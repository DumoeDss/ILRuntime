# Design -- neo-activator-createinstance-neo-redirect

## The Neo calling convention to mirror (child-6 / Step-19 templates)

A Neo CLR redirect has this signature (established by `InitializeArrayNeo` and
`DelegateCombineNeo` in `CLRRedirections.cs`):

```csharp
public unsafe static void XNeo(
    ILIntepreter intp, byte* frameBase, AutoList mStack,
    CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
```

- **Reading params**: `int curPrim = 0; object p = ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);`
  Each call reads a 4-byte mStack index at `frameBase + curPrim` and advances
  `curPrim += 4`. Params are laid out in declaration order. The Neo null
  sentinel is index `-1` (`ReadNeoReference` returns `idx >= 0 ? mStack[idx] : null`).
- **Writing a reference result**: the autogen stubs and `InvokeNeoClrMethod`
  both use: non-null -> `if (retRefBase >= mStack.Count) mStack.Add(result); else mStack[retRefBase] = result; *(int*)retDst = retRefBase;`;
  null -> `*(int*)retDst = -1;` (the null sentinel; see `InvokeNeoClrMethod`
  `ILIntepreter.Neo.cs:1082-1084`).
- **Void method / no dest**: `if (retDst == null) return;` (InitializeArrayNeo
  simply does not write because it is void).

The existing `WriteNeoDelegateResult` helper writes a non-null-aware ref
(delegate Combine can yield null-as-valid-index). Activator needs null-aware
writes (the Type overloads can PushNull), so a new helper `WriteNeoObjectResult`
is added (it is `WriteNeoDelegateResult` + the null sentinel).

## The three redirects and their Legacy semantics to reproduce

### CreateInstanceNeo -- generic `Activator.CreateInstance<T>()`
Legacy body (`CLRRedirections.cs:30-59`):
- `t = method.GenericArguments[0]` (NO frame param -- the type is the generic arg).
- `if (t is ILType)`: `Instantiate()` (reference type / enum) OR `AllocValueType`
  (value type, non-enum).
- `else` (CLR type): `if (ValueTypeBinders.ContainsKey(t.TypeForCLR))`
  `AllocValueType` ELSE `((CLRType)t).CreateDefaultInstance()`.

Neo body:
- `t = method.GenericArguments[0]`. If null/empty -> `EntryPointNotFoundException`
  (Legacy parity).
- IL type -> `result = ((ILType)t).Instantiate()`.
- CLR type -> `result = ((CLRType)t).CreateDefaultInstance()`.
  (The VT `AllocValueType` branches are unreachable in the smoke -- see
  proposal Scope; producing the box and writing it as a reference is the
  Neo-faithful best-effort and matches the autogen `CreateInstance_3_Neo`
  Adaptor convention. The Legacy VT-vs-ref distinction only mattered for the
  `StackObject` stack representation, which Neo does not have.)
- `WriteNeoObjectResult(mStack, retDst, retRefBase, result)`.

### CreateInstance2Neo -- `Activator.CreateInstance(Type)`
Legacy body (`CLRRedirections.cs:76-92`):
- `t = mStack[p->Value] as Type` (1 param: the Type).
- `if (t is ILRuntimeType)` -> `((ILRuntimeType)t).ILType.Instantiate()`.
- `else Activator.CreateInstance(t)`.
- `t == null` -> `PushNull`.

Neo body:
- `curPrim = 0; Type t = (Type)ReadNeoReference(frameBase, ref curPrim, mStack);`
- `result = (t is ILRuntimeType ilrt) ? ilrt.ILType.Instantiate() : (t != null ? Activator.CreateInstance(t) : null);`
- `WriteNeoObjectResult(...)` (null -> `-1`, reproducing PushNull).

### CreateInstance3Neo -- `Activator.CreateInstance(Type, object[])`
Legacy body (`CLRRedirections.cs:110-136`):
- `t = mStack[(esp-2)->Value] as Type` (param 0), `t2 = mStack[(esp-1)->Value] as object[]` (param 1).
- null-check each `t2[i]` -> `ArgumentNullException`.
- `if (t is ILRuntimeType)` -> `((ILRuntimeType)t).ILType.Instantiate(t2)`.
- `else Activator.CreateInstance(t, t2)`.
- `t == null` -> `PushNull`.

Neo body:
- `curPrim = 0; Type t = (Type)ReadNeoReference(...); object[] t2 = (object[])ReadNeoReference(...);`
  (Two `ReadNeoReference` calls in declaration order -- param 0 = Type, param 1 = object[].
  The `object[]` is itself a reference (an Array), so `ReadNeoReference` recovers it
  correctly -- the autogen `CreateInstance_1_Neo` reads it the same way.)
- null-check each `t2[i]` -> `throw new ArgumentNullException();` (Legacy parity).
- `result = (t is ILRuntimeType ilrt) ? ilrt.ILType.Instantiate(t2) : (t != null ? Activator.CreateInstance(t, t2) : null);`
- `WriteNeoObjectResult(...)`.

## The `WriteNeoObjectResult` helper
```csharp
static unsafe void WriteNeoObjectResult(AutoList mStack, byte* retDst, int retRefBase, object result)
{
    if (retDst == null) return;          // void method / no dest
    if (result == null) { *(int*)retDst = -1; return; }  // Neo null sentinel
    if (retRefBase >= mStack.Count) mStack.Add(result);
    else mStack[retRefBase] = result;
    *(int*)retDst = retRefBase;
}
```
This is `WriteNeoDelegateResult` (`CLRRedirections.cs:642-648`) plus the
null-sentinel branch. The non-null store + index write is identical to the
autogen stubs and `InvokeNeoClrMethod` (`ILIntepreter.Neo.cs:1082-1089`).

## Registration (AppDomain ctor)
After the existing Legacy `RegisterCLRMethodRedirection` block for Activator
(`AppDomain.cs:162-176`), add under `#if ENABLE_NEO_MODE`:
```csharp
foreach (var i in typeof(System.Activator).GetMethods())
{
    if (i.Name == "CreateInstance" && i.IsGenericMethodDefinition)
        RegisterCLRMethodRedirectionNeo(i, CLRRedirections.CreateInstanceNeo);
    else if (i.Name == "CreateInstance" && i.GetParameters().Length == 1)
        RegisterCLRMethodRedirectionNeo(i, CLRRedirections.CreateInstance2Neo);
    else if (i.Name == "CreateInstance" && i.GetParameters().Length == 2)
        RegisterCLRMethodRedirectionNeo(i, CLRRedirections.CreateInstance3Neo);
}
```
(Or fold into the existing loop with `#if ENABLE_NEO_MODE` sibling calls, as
the `Delegate.Combine` / `DelegateRemove` block already does at lines 196-206.
Either is Legacy-neutral; folding into the existing loop is the lower-diff
choice and mirrors the Delegate precedent exactly.)

## Precedence over the autogen stubs (the key correctness argument)
`CLRMethod.TryGetRedirection` (`CLRMethod.cs:111-131`):
```csharp
if (def.IsGenericMethod && !def.IsGenericMethodDefinition)
{
    if (!map.TryGetValue(def.GetGenericMethodDefinition(), out redirect))
        map.TryGetValue(def, out redirect);
}
else
    map.TryGetValue(def, out redirect);
```
- Generic instantiation (`Activator.CreateInstance<ILType>()`): the lookup
  tries the generic DEFINITION first. The hand-written `CreateInstanceNeo` is
  registered for the generic definition -> it preempts every autogen
  per-instantiation stub (`CreateInstance_0/3/4/5_Neo`). ONE redirect serves
  all generic instantiations, exactly as the Legacy hand-written redirect does.
- Non-generic Type overloads: `RegisterCLRMethodRedirectionNeo` is
  first-registered-wins. The AppDomain ctor runs before the test-harness
  autogen `CLRBindings.Register()` -> the hand-written Neo redirect is
  registered first -> the autogen `CreateInstance_1/2_Neo` registration is
  skipped (`!ContainsKey`). (Same first-registered-wins guarantee the Step-20
  async builder redirects rely on.)

So the hand-written Neo redirects win on BOTH overload families.

## Verification
- Probe `NeoStepActivatorCreateInstanceTest.cs`: call all three overloads on an
  IL type, assert default values + ctor-arg round-trip. MUST FAULT
  (`MissingMethodException`) on HEAD; PASS after. The "faults on HEAD" guard
  uses the assert-throws / deliberate-1/0 pattern (a wrong value alone will not
  fail because the redirect throws before any value is produced).
- NeoStep smoke green (target 358+N/0; baseline 358/0 post-child-21).
- Stash-toggle: stash the engine files (keep the probe) -> rebuild -> probe
  FAULTS; pop -> rebuild -> PASS.
- The 2 triage-flagged Activator tests pass under Neo by name filter.
- Legacy-neutral: the change is 100% `#if ENABLE_NEO_MODE`.

## Non-trivial Neo signature note (eliminated hypothesis)
The `Activator.CreateInstance(Type, object[])` overload has a `params object[]`
argument. A priori this could have been a non-byref-array Neo edge case (an
`object[]` is an Array reference, not a value type). Confirmed NOT a problem:
`ReadNeoReference` recovers any reference-typed param as a 4-byte mStack index,
and an `object[]` is a reference. The autogen `CreateInstance_1_Neo` reads it
the identical way (`ReadNeoReference` for both Type and object[]). So all three
overloads use the plain `ReadNeoReference` cursor discipline -- no value-type
marshal is involved. (If it had been a VT param, the Step-13b `WriteNeoValueType`
/ `ReadNeoValueType` path would have been required -- it is not.)
