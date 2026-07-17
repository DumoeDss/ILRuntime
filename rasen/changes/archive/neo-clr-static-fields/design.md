## Context

The Step-25 S3-4 capstone implemented the **IL**-static `Stsfld`/`Ldsfld` arms in
`ExecuteNeo` (`ILIntepreter.Neo.cs:3821` Stsfld, `:3870` Ldsfld): the declaring
type resolves to an `ILType`, and the value is read/written from the ILType's
`StaticInstance` (`byte[] Primitives` + `AutoList ManagedObjects`) at the
per-field offset, per category (primitive / IL value-type / reference). The
`else` branch (CLR declaring type) throws a tagged `NotImplementedException`.

This change fills that `else`. The CLR static field's backing store is a real
`System.Reflection.FieldInfo` on a real CLR type -- there is no `byte[]` to index,
so the value MUST pass through a boxed `object` (`FieldInfo.GetValue` /
`SetValue`). The design therefore maps the **frame representation** (raw bytes /
mStack ref index) <-> boxed `object`, reusing the existing Step-13 Box/Unbox
marshalling helpers. Legacy `ExecuteR` (`ILIntepreter.Register.cs:3288` Stsfld,
`:3316` Ldsfld) is the reference behavior: it calls `CLRType.GetField` +
`GetFieldValue` / `SetStaticFieldValue`.

## Goals / Non-Goals

**Goals:**
- `Stsfld`/`Ldsfld` on a CLR static field execute under Neo for the three common
  categories: CLR primitive, CLR value type (simple struct/enum, no ref fields),
  CLR reference type.
- Mirror Legacy semantics (resolve via `CLRType`, unwrap `CrossBindingAdaptorType`
  on read) and the IL-static arm's frame-slot convention (`frameBase + ip->DstOffset`,
  `mStack.Add` temp-ref for references).
- Reuse existing marshalling helpers -- NO new marshalling code.
- A NeoStep probe that FAULTS without the fix (the current NIE) and asserts the
  round-trip value with the fix.

**Non-Goals:**
- CLR value types **with reference fields and no ValueTypeBinder** (the separate
  Step-13b gap, child `neo-clr-vt-reffields-binder`). The VT branch calls
  `ReadNeoValueType`/`WriteNeoValueType`; if those hit the 13b NIE for a
  ref-fielded struct, that remains a sibling concern.
- `Ldsflda` (static field address) on a CLR type -- a different opcode (5 hits,
  child `neo-misc-opcodes`).
- `CheckAndCloneValueType` / `CheckCLRTypes` value-semantics cloning that Legacy
  applies on Stsfld. Neo's `ReadNeoValueType`/`NeoBoxPrimitiveByType` already
  produce an INDEPENDENT boxed copy for value types, so the clone is not needed
  for correctness of the common cases; the `object`-typed-IL-instance edge is
  covered by the `CrossBindingAdaptorType` unwrap on read.

## Decisions

### D1. Resolve the CLR static via `CLRType` -- no new helper needed

`CLRType` already exposes everything (verified, `CLRType.cs:404-539`):
- `FieldInfo GetField(int hash)` -- walks `Fields` + `BaseType`.
- `object GetFieldValue(int hash, object target)` -- `target=null` for static ->
  `FieldInfo.GetValue(null)` (after the patch-getter cache).
- `void SetStaticFieldValue(int hash, object value)` -> `FieldInfo.SetValue(null,
  value)` (after the patch-setter cache).

The operand encoding is shared with the IL-static arm and with Legacy
(`GetStaticFieldIndex`, `AppDomain.cs:2203`: `((long)type.GetHashCode() << 32) |
(uint)idx`, SAME `else` branch for CLR types). So `sIdx = (int)ip->OperandLong` is
the field hash; `ct.GetField(sIdx)` resolves it. NO new CLRType API and NO JIT
change.

### D2. Stsfld CLR-static branch (write) -- frame -> object -> FieldInfo

`byte* srcSlot = frameBase + ip->DstOffset;` (Stsfld sets ONLY `Register1` =
`DstOffset`; the source value is the lone operand -- same as the IL-static arm).
`var ft = f.FieldType;` (a `System.Type`).

```
object value;
if (ft.IsPrimitive)
    value = NeoBoxPrimitiveByType(ft, srcSlot);          // :5950, takes System.Type
else if (ft.IsValueType)
{
    int off = ip->DstOffset;
    value = ReadNeoValueType(ft, frameBase, ref off,      // :227
                             Optimizer.GetNeoValueTypeManagedSize(ft));
}
else // reference type
{
    int srcRefIdx = *(int*)srcSlot;
    value = srcRefIdx >= 0 ? mStack[srcRefIdx] : null;
}
ct.SetStaticFieldValue(sIdx, value);                       // FieldInfo.SetValue(null, value)
```

**Why `NeoBoxPrimitiveByType` (not `NeoBoxReturnValue`):** the former takes a
`System.Type` directly (the field's `FieldType`); the latter takes an `IType` and
would need an extra `AppDomain.GetType(ft)` lookup. `NeoBoxPrimitiveByType`
already exists (`:5950`, currently 0 call sites, retained intentionally) -- this
change gives it a live caller.

### D3. Ldsfld CLR-static branch (read) -- FieldInfo -> object -> frame

`byte* dstSlot = frameBase + ip->DstOffset;`

```
object obj = ct.GetFieldValue(sIdx, null);                 // FieldInfo.GetValue(null)
if (obj is CrossBindingAdaptorType cba) obj = cba.ILInstance;   // Legacy parity :3334-3335
var ft = f.FieldType;
if (ft.IsPrimitive)
    NeoWritePrimitiveToFrame(obj, dstSlot);                // :5986
else if (ft.IsValueType)
    WriteNeoValueType(obj, dstSlot,                        // :243
                      Optimizer.GetNeoValueTypeManagedSize(ft));
else // reference type -- mStack.Add temp-ref convention (mirrors IL-static Ldsfld)
{
    mStack.Add(obj);
    *(int*)dstSlot = obj != null ? mStack.Count - 1 : -1;
}
```

**Why `mStack.Add` (not `frameRefBase + dstRefOffset`):** Stsfld/Ldsfld have NO
`dstRefOffset` operand (the JIT sets only `Register1`). The IL-static Ldsfld ref
branch already uses the `mStack.Add` temp-ref convention; the CLR branch MUST
match it (the JIT emits the same frame layout for IL- and CLR-static ldsfld).
Box/Unbox use `frameRefBase + dstRefOffset` instead because THOSE opcodes carry a
`dstRefOffset` (`ip->Operand3`) -- a different opcode contract, not applicable
here.

### D4. Category discriminator is the field's CLR `System.Type`, not an `IType`

The IL-static arm discriminates on `ilt.StaticFieldTypes[sIdx]` (an `IType`). The
CLR-static arm discriminates on `f.FieldType.IsPrimitive`/`.IsValueType` (a
`System.Type`). This is correct because a CLR static field's type is always a CLR
type, and `ReadNeoValueType`/`WriteNeoValueType`/`NeoBoxPrimitiveByType` all take
a `System.Type`. No `IType` round-trip is needed.

### D5. Probe must FAULT to fail (child-1/child-2 durable finding)

The harness counts "ran without throwing" as Pass. The current code throws a
tagged NIE -> without the fix the probe faults (good). But a wrong-value bug
would NOT fault, so the primitive probe MUST assert the round-trip equality (throw
on mismatch), covering both failure modes. `string.Empty`/`IntPtr.Zero` read
probes assert a known value (empty / zero).

### D6. Probe infra: a public static `int` on a host CLR helper

`Stsfld` needs a WRITABLE CLR static; mscorlib's static fields are mostly
`readonly`. Add `public static int NeoClrStaticProbe;` to the existing `TestClass3`
CLR helper in `ILRuntimeTestBase` (rebuild ILRuntimeTestBase). The probe writes
12345, reads back, asserts equality. `string.Empty` (reference read) and
`IntPtr.Zero` (VT read) need NO infra (mscorlib CLR statics).

## Risks / Trade-offs

- **[Field-hash mismatch]** `ct.GetField(sIdx)` returns null if the hash encoding
  differs from what `GetStaticFieldIndex` recorded. -> Mitigation: the encoding is
  the SAME `GetStaticFieldIndex` call Legacy uses (verified `AppDomain.cs:2223`);
  Legacy resolves these fields green, so the hash is correct. Guard with a null
  check that throws a tagged exception (clearer than the NRE `FieldInfo.SetValue`
  would give) only if observed.
- **[CLR VT with ref fields]** a struct static field with reference fields and no
  `ValueTypeBinder` hits the Step-13b NIE inside `ReadNeoValueType`/
  `WriteNeoValueType`. -> Out of scope (sibling child); simple structs/enums
  (`IntPtr`, enums) work. The probe uses `IntPtr.Zero` (a simple one-int struct)
  to exercise the VT category without the binder.
- **[initonly fields]** `string.Empty` is `static readonly initonly`; IL `ldsfld`
  reads it fine (the probe only READS it). The write probe uses a non-initonly
  field, so no verifier issue.
- **[Value-semantics cloning]** Legacy clones value types on Stsfld
  (`CheckAndCloneValueType`). Neo's box/read already yields an independent copy,
  so skipping the clone does not alias the frame. -> Acceptable for the common
  cases; revisit only if a test shows aliasing.
