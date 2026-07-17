# Design: neo-autogen-binding-invoke

## The fix point
`ILRuntime/CLR/Method/CLRMethod.cs` -- the Neo `Invoke(byte* targetBase, ...)`
reflection fallback. This is the path that handles ANY byref-param CLR method
under Neo, because `InvokeNeoClrMethod` (`ILIntepreter.Neo.cs:1264`) routes byref
calls to reflection (the autogen redirect may be a stale stub that never writes
the mutated byref back; reflection owns the Area-4c write-back). So even though
`TestCLREnumRef` HAS an autogen `TestCLREnumRef_0_Neo`, the byref call bypasses
it and reaches `CLRMethod.Invoke(byte*)`.

## The bug
In the param-read loop, an enum param was read as:
```
if (t == typeof(int) || t.IsEnum) { param[i] = ReadNeoInt32(...); }
```
`ReadNeoInt32` returns a boxed `Int32`. For a BYREF enum param, `param[i]` (the
boxed Int32) is handed to `MethodBase.Invoke` -> `System.RuntimeType.CheckValue`,
which requires the value to BE the enum type against the `EnumType&` parameter.
`Int32` is not assignable to `EnumType&` -> `ArgumentException`.

A by-value enum param is unaffected in practice (the autogen redirect path owns
by-value enums; no by-value-enum reflection call exists in the smoke), but to be
minimal and byte-identical-to-HEAD for the un-proven case, the fix scopes the
enum-type boxing to byref params only (`byRefSlotOff[i] >= 0`).

## The fix
Split the arm:
```
if (t == typeof(int)) { param[i] = ReadNeoInt32(...); }
else if (t.IsEnum)
{
    int enumUnderlying = ReadNeoInt32(...);
    param[i] = (byRefSlotOff != null && i < byRefSlotOff.Length && byRefSlotOff[i] >= 0)
        ? Enum.ToObject(t, enumUnderlying)   // byref: box as the enum type
        : (object)enumUnderlying;            // by-value: unchanged (Int32 box)
}
```
`Enum.ToObject(enumType, intValue)` returns a boxed `EnumType` -- assignable to
`EnumType&`, so `CheckValue` accepts it. Mirrors Legacy's `StackObject.ToObject`
which routes enums through `CheckCLRTypes`/`Enum.ToObject`.

## Round-trip soundness
The post-call write-back (`CLRMethod.cs:675`, `et.IsEnum` arm) flattens the
(possibly-mutated) boxed enum back to its Int32 flat bytes via
`WriteNeoValueType(boxedEnum, slot, sz=4)`. A boxed enum's flat bytes ARE its
underlying Int32 value, so the write-back is correct for both the forward
`Enum.ToObject` box and a method-mutated box. Verified end-to-end:
`UnitTest_RefCLREnum` asserts `key==2 && tag==TestCLREnum.Test2` after the call.

## Neo-gating / Legacy-neutrality
`CLRMethod.Invoke(byte* targetBase, ...)` is the NEO reflection overload (Legacy
uses `Invoke(StackObject* esp, ...)`). Legacy never calls the byte* overload, so
the change is structurally Neo-only. Empirically: plain Debug + useRegister=true
NeoStep = 398 ran / 18 failed (the documented pre-existing Legacy set); the new
probe passes under Legacy.

## Why only 1 test (honest scoping)
The other 7 cluster-D tests are distinct deep roots (see proposal.md table):
JIT generic-method specialization (CLRBindingTest07/08), IL-struct boxing on a
CLR-generic crossing (StructTest11), boxed-struct interface dispatch (MyTest.Test),
reflection SetValue arg corruption (ReflectionTest06), ILRuntimeType rejected by
framework reflection (ReflectionTest10, TestGenericMethod2). Each warrants its own
child; bundling them would be high-risk. The byref-enum gap is the one clean,
isolated, mechanical fix in the cluster.
