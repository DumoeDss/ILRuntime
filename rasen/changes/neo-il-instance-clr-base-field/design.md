# Design — neo-il-instance-clr-base-field

## Context

Child-4 (`neo-raw-stfld-ldfld`) added the Neo raw `Stfld`/`Ldfld` handlers for a field whose
**declaring type is a CLRType** (the JIT leaves the raw opcode because its typed splitter only
fires for an ILType declaring type). The handlers cover CLR ref-type, CLR VT-by-value (Ldfld),
and CLR VT-byref (Stfld) owners, and DEFER two more shapes with Step-tagged NIEs:

- `ILIntepreter.Neo.cs:3750` (raw `Ldfld`): `target is ILTypeInstance || CrossBindingAdaptorType`
  -> "Neo raw Ldfld: IL-instance owner with a CLR-base field is deferred".
- `ILIntepreter.Neo.cs:3913` (raw `Stfld`): the same branch -> "... Stfld ... deferred".

This change replaces those two NIEs with a real handler. Both sites are inside the CLR
**reference-type owner** `else` (the `if (ct.TypeForCLR.IsValueType)` took the CLR-VT path; the
`else` is the ref path). The owner slot's first int is the `mStack` index of the owner object,
which for this shape is an `ILTypeInstance` (or its `CrossBindingAdaptorType` wrapper).

## How Legacy resolves a CLR-base field on an IL instance (the data path)

The IL object model stores an IL type's OWN fields in `byte[] Primitives` + `AutoList
ManagedObjects`. A field inherited from a **CLR base** is NOT in that layout. It lives on the
ILTypeInstance's **`clrInstance`** field (`ILTypeInstance.cs:285`, exposed as `.CLRInstance`):
the wrapped CLR object created at construction
(`ILTypeInstance.cs:366-368` -- when `type.FirstCLRBaseType is CrossBindingAdaptor`,
`clrInstance = adaptor.CreateCLRInstance(appdomain, this)`). For `TestCls : ClassInheritanceTest`,
that CLR object is a `ClassInheritanceTestAdaptor.Adaptor` instance, which IS-A
`ClassInheritanceTest`, so the CLR base's instance fields (`testVal`, `TestVal2`) are real CLR
fields on it.

Legacy routes through it via an INDEX gate, not a type discrimination:

- `ILIntepreter.Register.cs` raw `Stfld` (owner is a heap ref, not a VT-byref): `obj` is the
  `ILTypeInstance` -> `ilInstance.AssignFromStack((int)ip->OperandLong, ...)`.
- `ILTypeInstance.AssignFromStack(fieldIdx, ...)` (`ILTypeInstance.cs:953`):
  `if (fieldIdx < fields.Length && fieldIdx >= 0)` -> IL field; `else` -> CLR-inherited:
  `clrType = FirstCLRBaseType.BaseCLRType`; `clrType.SetFieldValue(fieldIdx, ref clrInstance, value)`.
- The Ldfld twin: `ilInstance.CopyToRegister(...)` -> `ILTypeInstance` read-indexer
  (`ILTypeInstance.cs:445-453`): out-of-range index -> `clrType.GetFieldValue(index, clrInstance)`.

Because the raw encoding puts the CLR-base field hash in the low 32 bits of `OperandLong`
(`(typeHash<<32)|fieldHash`, IDENTICAL to Neo -- `AppDomain.GetFieldOffset:2242-2258` returns the
field's declaring CLRType and `type.GetFieldIndex(token)` for the offset), the hash is large and
falls in the `else` (CLR-inherited) branch, resolving the field on `clrInstance`.

**Bottom line:** a CLR-base field on an IL instance is read/written on `ins.CLRInstance`, not on
the `ILTypeInstance` itself, and not from `Primitives`/`ManagedObjects`.

## The Neo handler branch (the apply recipe)

Both raw handlers have ALREADY decoded, before the NIE site:
- `typeHash = (int)((ulong)ip->OperandLong >> 32)`, `fieldHash = (int)ip->OperandLong`;
- `ct = AppDomain.GetType(typeHash) as CLRType` (the CLR BASE type, e.g. ClassInheritanceTest);
- `f = ct.GetField(fieldHash)` (the `FieldInfo`); `fldClrType = f.FieldType`;
- `ownerOff` (Ldfld: `ip->SrcOffset`; Stfld: `ip->DstOffset`);
- Ldfld: nothing else needed; Stfld: the source `value` is already marshalled by field category.

In the CLR ref-type owner `else`, replace the `target is ILTypeInstance || CrossBindingAdaptorType`
NIE with a redirect to `CLRInstance`. The owner object is resolved exactly as the sibling CLR-ref
branch already does: `int objIdx = *(int*)(frameBase + ownerOff); object target = objIdx >= 0 ?
mStack[objIdx] : null;` (with the existing `NullReferenceException` guard).

**Ldfld** (replaces :3750 NIE):
```csharp
if (target is ILTypeInstance ilIns || target is CrossBindingAdaptorType cbaT)
{
    ILTypeInstance il = target as ILTypeInstance ?? cbaT.ILInstance;
    fldVal = NeoReadClrObjectField(AppDomain, il.CLRInstance, fieldHash);
    if (fldVal is CrossBindingAdaptorType cba2) fldVal = cba2.ILInstance;
}
else if (target is Array)
    throw new NotImplementedException("Neo raw Ldfld: array-element field read is deferred ...");
else
    fldVal = NeoReadClrObjectField(AppDomain, target, fieldHash);
```
(The post-read dest marshalling -- primitive -> `NeoWritePrimitiveToFrame`, VT ->
`WriteNeoValueType`, ref -> `mStack.Add` + index -- is unchanged and already follows this block.)

**Stfld** (replaces :3913 NIE):
```csharp
if (target is ILTypeInstance ilIns || target is CrossBindingAdaptorType cbaT)
{
    ILTypeInstance il = target as ILTypeInstance ?? cbaT.ILInstance;
    NeoWriteClrObjectField(AppDomain, il.CLRInstance, fieldHash, value);
}
else if (target is Array)
    throw new NotImplementedException("Neo raw Stfld: array-element field write is deferred ...");
else
    NeoWriteClrObjectField(AppDomain, target, fieldHash, value);
```

### Why `NeoReadClrObjectField`/`NeoWriteClrObjectField` work for `il.CLRInstance`

These helpers (`ILIntepreter.Neo.cs:6079/6091`) resolve `ct = appdomain.GetType(target.GetType())
as CLRType` and call `ct.GetFieldValue(fieldHash, target)` / `ct.SetFieldValue(fieldHash, ref
target, value)`. `il.CLRInstance` for a CLR-base IL type is the Adaptor object (IS-A the CLR
base). `GetFieldValue`/`GetField` walk the base-type chain (child-3 finding), so the field
declared on `ClassInheritanceTest` resolves correctly from the Adaptor's CLRType. This is the
SAME reflection path the sibling CLR-ref-owner branch uses (`:3753`/`:3916`).

**Alternative (equally valid, byte-identical to Legacy):** skip the helper and call the
already-decoded declaring type directly -- `ct.GetFieldValue(fieldHash, clrTarget)` /
`ct.SetFieldValue(fieldHash, ref clrTarget, value)` -- where `clrTarget = il.CLRInstance`. This
mirrors `ILTypeInstance.cs:966` exactly and avoids re-resolving the CLRType from the Adaptor's
runtime type. Either is acceptable; the helper form is preferred for minimal diff and consistency
with the sibling branch.

### No writeback / no `ref` hazard

`il.CLRInstance` is a reference-type Adaptor object (`ClassInheritanceTest` is a class), so
`SetFieldValue(hash, ref obj, value)` does NOT replace the instance -- the `ref` is the defensive
ILRuntime convention for value-type targets and is a no-op here. Legacy does not write
`clrInstance` back either. No mutation of the `ILTypeInstance` is needed.

## Probe design

A TestCases IL type deriving from the CLR `ClassInheritanceTest` (which has `public int TestVal2`
and `protected int testVal`), plus a static NeoStep test that constructs it and round-trips the
CLR-base field. The access compiles to raw `stfld`/`ldfld ClassInheritanceTest::TestVal2` with an
IL-instance owner -- exactly the deferred shape. Probe convention (child-1/child-2/child-4): pass
= "ran without throwing"; signal a wrong value with a deliberate `1/0` (the Neo VM cannot yet
`new Exception(...)`); each probe MUST fault on the current tagged NIE.

```csharp
public class NeoStepIlClrBaseHolder : ClassInheritanceTest   // IL type, CLR base
{
    public int ReadBase() { return TestVal2; }      // ldfld CLR-base field (IL this)
    public void WriteBase(int v) { TestVal2 = v; }  // stfld CLR-base field (IL this)
}
public class NeoStepIlClrBaseFieldTest
{
    public static void NeoStepIlClrBase_TC1_RoundTrip()
    {
        var h = new NeoStepIlClrBaseHolder();
        h.WriteBase(4242);
        int v = h.ReadBase();
        if (v != 4242) { int z = 1; int d = 0; int _ = z / d; }
    }
}
```

- Without the fix, `WriteBase` (or `ReadBase`) throws the :3913/:3750 tagged NIE -> probe FAULTs.
- With the fix, the round-trip asserts the value -> a wrong-value bug also fails (`1/0`).
- Optional TC2: read the default (`TestVal2 == 200`) to exercise Ldfld independently of Stfld.

`ClassInheritanceTest` lives in `ILRuntimeTestBase` (a CLR host type registered via
`ClassInheritanceTestAdaptor`), so `NeoStepIlClrBaseHolder : ClassInheritanceTest` is a legal
IL-inherits-CLR declaration (same shape as the existing `InheritanceTest.TestCls`).

### Probe risk: newobj of an IL-type-with-CLR-base

`new NeoStepIlClrBaseHolder()` runs the IL default ctor -> the CLR base ctor on `clrInstance`.
This is the Step-18 newobj + ctor-dispatch surface and is exercised by the existing
`InheritanceTest.TestCls` in the full smoke, so it is expected to work. If newobj of this shape
turns out to throw a DIFFERENT (non-tagged) NIE, the apply worker must stop and surface that as a
distinct gap (it is NOT this change's defect); the probe is robust to the base field initializers
not running because the round-trip writes its own value.

## Capability / spec-delta choice

**Capability = `neo-value-types`** (the SAME capability child-4 used for the raw Stfld/Ldfld
owner-shape requirement). The delta ADDs a new requirement: "IL-instance owner with a
CLR-base-declared instance field (raw Stfld/Ldfld)" -- a sibling owner shape to child-4's CLR
ref / CLR VT owners. It is Neo-gated and therefore Legacy-neutral by construction.

## Out of scope

- The array-element raw Stfld/Ldfld shape (`target is Array`, `:3752`/`:3915`) -- left deferred
  with its tagged NIE (a distinct ~2-hit shape; route through the element byref in a future child).
- CLR value type with reference fields and no binder (the Step-13b sibling inside
  `ReadNeoValueType`/`WriteNeoValueType`) -- already covered by child-6/child-8.
- Generic CLR base (`ClassInheritanceTest2<T>`) -- `TestCls4 : ClassInheritanceTest2<TestCls4>`
  hits the same `clrInstance` path once the base resolves; not separately probed here.

## Decisions

- **D1: route through `CLRInstance`, not the IL field layout.** A CLR-base field is not in
  `Primitives`/`ManagedObjects`; it lives on the Adaptor. Confirmed by the Legacy indexer +
  `AssignFromStack` `else` branch (`ILTypeInstance.cs:445-453`, `:958-968`).
- **D2: reuse `NeoReadClrObjectField`/`NeoWriteClrObjectField`.** No new helper. The helpers
  resolve the field by hash via the target's CLRType, walking the base chain. (Direct
  `ct.GetFieldValue/SetFieldValue` is the equivalent explicit form.)
- **D3: unwrap both `ILTypeInstance` and `CrossBindingAdaptorType`.** The owner slot may hold
  either (the Adaptor wrapper or the IL instance directly). `target as ILTypeInstance ??
  ((CrossBindingAdaptorType)target).ILInstance` covers both; `.CLRInstance` then yields the
  Adaptor object either way.
- **D4: no JIT / optimizer change.** Child-4 already lowered the raw `Ldfld`/`Stfld`
  `Register1`/`Register2` into `DstOffset`/`SrcOffset` and stamped `OperandLong`. This change is
  interpreter-only.
