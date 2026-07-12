# Design — neo-raw-stfld-clr-object-vt-field

## DIAGNOSIS (the crux -- pinned before the apply)

### The trigger and its CIL
`obj.Struct.value = 111` where `obj` is a CLR REFERENCE object (`TestClass3`),
`Struct` is a CLR-struct FIELD of `obj`, and `value` is a primitive field of
`Struct`. CIL: `ldflda Struct(on obj); stfld value(on the struct address)`.

JIT dump for `UnitTest_Struct2` (instr 4-6, the first statement):
```
4:ldflda r2, r2, 0xCE810475      ; ldflda Struct on obj -> r2 = byref
5:ldc.i4.s r3,111
6:stfld r2, r3, 0x200000C45F8E5EDB  ; stfld value, owner=r2 (byref), value=r3
```
`stfld` OperandLong = `0x200000C45F8E5EDB`: typeHash = `0x200000C4` (TestStruct),
fieldHash = `0x5F8E5EDB` (the `value` field). Owner = r2 = the ldflda result.

### The owner-byref encoding (PINNED)
The owner of `stfld value` is the byref produced by `ldflda Struct(on obj)`.

1. **JIT emission** (`JITCompiler.cs` `case Code.Ldflda:`, `:3151-3200`):
   - `type` = declaring type of `Struct` = `TestClass3` (CLRType).
   - `fieldType` = `TestStruct` (CLR value type).
   - `op.Operand2 = offset.PrimitiveOffset` = `FieldInfo.GetHashCode()` of the
     `Struct` field (for a CLRType, `AppDomain.GetFieldOffset` returns
     `type.GetFieldIndex(token)` = `CLRType.GetFieldIndex` =
     `fieldMapping[name]` = `FieldInfo.GetHashCode()` -- the child-15 hash
     situation).
   - Marker check: `IsClrStructFieldOfIL` -> FALSE (TestClass3 not ILType);
     `type is ILType && ref-field` -> FALSE; `type is CLRType` -> TRUE ->
     stamps `NeoLdfldaClrStructLocalFieldMarker` (0x8) into `Operand4`.

2. **Runtime ldflda** (`ILIntepreter.Neo.cs:1829-1957`):
   - `objIdx = *(int*)(frameBase + operandSlotOff)` = the mStack index of `obj`
     (TestClass3) -- **>= 0** (heap object).
   - `heapIlRefFieldMarker`? NO. `clrStructFieldMarker`? NO (not F-10). Skip the
     first two branches.
   - `objIdx == -1`? NO -> skip the frame-native branch (where the 0x8 marker
     would have been consumed to resolve the offset via `Marshal.OffsetOf`).
   - `inlineMarker` (0x1)? NO.
   - **`else` branch (`:1941-1956`) fires**: produces byref
     `*(int*)(dst+0) = objIdx; *(int*)(dst+4) = fieldPrimOff;` where
     `fieldPrimOff = ip->Operand2` = the **`Struct` field's hash**. The comment
     at `:1945-1946` explicitly states: "For a CLR object, fieldPrimOff is the
     FieldInfo hash (set by AppDomain.GetFieldOffset ... as type.GetFieldIndex
     (token))."

   **=> The byref encoding is `(objIdx_of_containing_CLR_object,
   structFieldHash)`** where structFieldHash is the `Struct` field's
   `FieldInfo.GetHashCode()` -- NOT a managed byte offset, NOT an element index.
   Confirmed by the runtime NIE: `objIdx=4`.

### The failure mode
`stfld value` consumes the byref as owner (`ILIntepreter.Neo.cs:4128+`):
- `ct` = declaring type of `value` = `TestStruct` (CLR struct, IsValueType).
- Enter the `ct.TypeForCLR.IsValueType` branch (`:4158`).
- `objIdx = *(int*)(frameBase + ownerOff)` = 4 (>= 0).
- `off = *(int*)(frameBase + ownerOff + 4)` = the `Struct` field's hash.
- `objIdx == -1`? NO. `mStack[objIdx] is Array`? NO (TestClass3 is a CLR object).
- **`else` -> NIE "unrecognized CLR value-type owner byref shape (objIdx=4)"**
  at `:4193`. Reproduced.

## THE FIX (runtime-only, Neo-gated, ~15 lines)
Add a third branch to the raw-`Stfld` CLR-value-type-owner arm, between the
Array branch and the NIE. When `objIdx >= 0` and `mStack[objIdx]` is a non-Array
object, **box/mutate/unbox ONE LEVEL UP** via the containing object's CLRType:

```csharp
else if (objIdx >= 0)
{
    object target = mStack[objIdx];
    if (target == null)
        throw new NullReferenceException();
    if (target is ILTypeInstance || target is CrossBindingAdaptorType)
        throw new NotImplementedException("Neo raw Stfld: ... IL-instance CLR-struct-field owner (F-10 ManagedObjects storage) is deferred ...");
    // CLR-OBJECT-FIELD owner: the byref is (objIdx, structFieldHash).
    // `off` == structFieldHash (the byref's +4 half). Read the struct field,
    // reflection-write the leaf field, write the mutated struct back.
    object boxedStruct = NeoReadClrObjectField(AppDomain, target, off);
    f.SetValue(boxedStruct, value);
    NeoWriteClrObjectField(AppDomain, target, off, boxedStruct);
}
else
    throw new NotImplementedException("Neo raw Stfld: unrecognized ...");
```

### Why this is correct
- **`off` is resolvable.** `NeoReadClrObjectField(appdomain, target, fieldHash)`
  re-resolves `ct = appdomain.GetType(target.GetType()) as CLRType` (the
  containing object's CLRType, e.g. TestClass3) and calls
  `ct.GetFieldValue(fieldHash, target)` -> `GetField(fieldHash)` (the `Fields`/
  `fieldInfoCache` dictionary is keyed by `FieldInfo.GetHashCode()` in
  `InitializeFields`, `CLRType.cs:610-615`) -> `fieldInfo.GetValue(target)` ->
  the boxed current struct. So `off` = structFieldHash resolves the `Struct`
  FieldInfo on the containing type. (This is the SAME Area-4d mechanism the
  existing `NeoReadClrObjectField`/`NeoWriteClrObjectField` callers use at
  `:4026/:4031/:4216/:4232` -- proven by child-4/9.)
- **`f.SetValue(boxedStruct, value)` mutates in place.** `f` = the leaf field's
  FieldInfo (already resolved as `ct.GetField(fieldHash)` where `ct` = the
  STRUCT's CLRType, e.g. TestStruct); `boxedStruct` = the boxed struct;
  `value` = the boxed source value (already marshalled by field category at
  `:4145-4157`). `FieldInfo.SetValue` on a boxed value type mutates the boxed
  instance in place. Precedent: child-19's array-element Stfld
  (`object boxedElem = cArr.GetValue(off); f.SetValue(boxedElem, value);
  cArr.SetValue(boxedElem, off);` at `:4188-4190`).
- **Write-back persists.** `NeoWriteClrObjectField(appdomain, target, off,
  boxedStruct)` -> `ct.SetFieldValue(off, ref target, boxedStruct)` ->
  `fieldInfo.SetValue(target, boxedStruct)` writes the mutated struct back to
  the containing object's `Struct` field. The containing object is a class
  (ref type) living on mStack, so the mutation is visible to subsequent reads
  (host-side or Neo-side).
- **Field preservation.** The box/mutate/unbox READS the current struct before
  mutating, so other fields are preserved (the TC2 multi-field probe proves
  this: three separate single-field writes yield a struct with all three set).
- **No JIT marker needed (contrast child-24).** A value-type-owner `Stfld`'s
  owner is ALWAYS a byref (a value-type field WRITE always goes through the
  struct's address), never flat bytes -- so runtime content detection
  (`mStack[objIdx]` kind) is unambiguous, exactly as child-19's Stfld-array
  fix used runtime detection safely. The flat-bytes-vs-byref ambiguity only
  exists for the READ side (raw `Ldfld`), which child-24 fixed with a marker;
  this child does not touch the read side.

### Null + IL-instance handling
- `target == null` -> `NullReferenceException` (a null containing object).
- `target is ILTypeInstance || CrossBindingAdaptorType` -> tagged deferred NIE.
  This is the `obj2.Struct.value` sub-shape in `UnitTest_Struct2` (obj2 is an
  IL class; the struct field uses F-10 `ManagedObjects[refOff]` storage, a
  different mechanism). Deferred as a sibling (see "Out of scope").

## Out of scope (separate siblings -- noted, NOT fixed here)
1. **The raw `Ldfld` READ sibling** (`ILIntepreter.Neo.cs:3967-4002`): a plain
   read `x = obj.Struct.value` (or `Console.WriteLine(obj.Struct.value)`, instr
   17 in the dump) lowers to raw `Ldfld` with the same ldflda-produced byref
   owner; the existing flat-bytes `ReadNeoValueType` path reinterprets the
   byref ints as the struct's fields -> silent wrong value (the read-direction
   counterpart of this child's write). Needs a JIT marker (the flat-bytes-vs-
   byref ambiguity, per child-24). Candidate follow-up child.
2. **The `+=` / nested-ldflda path** (`obj.Struct.value += 111`): Roslyn lowers
   this to `ldflda Struct; ldflda value; ldind.i4; add; stind.i4` (instr 8-14
   in the dump) -- NOT raw Stfld. The nested `ldflda value` on a byref-to-CLR-
   struct-field is a distinct, deeper gap (the inner ldflda treats the byref's
   objIdx as the struct). Candidate follow-up.
3. **The IL-instance CLR-struct-field owner** (`obj2.Struct.value`, F-10
   ManagedObjects storage) -- the deferred NIE branch above.
4. **Pre-existing Neo float bugs** (`addi`-on-float, `conv.i4`-float-bit-
   reinterpret) -- the probe uses INT fields only to stay clean (child-15/16/21
   gotcha).

## Probe design
- **Host types** (`TestVector3.cs`): `NeoClrObjVtFieldProbe` (a 3-int-field CLR
  struct: `a`, `b`, `c`) + `NeoClrObjVtFieldOwner` (a CLR class with
  `public NeoClrObjVtFieldProbe S;`). Multiple int fields let TC2 prove field
  preservation. Pure int avoids the float bugs.
- **Host helper** (`TestClass3.cs` `TestCLRBinding`): `NeoClrObjVtFieldProbeSum
  (NeoClrObjVtFieldOwner o) => o.S.a + o.S.b + o.S.c` -- CLR reflection
  read-back (sidesteps the deferred Neo Ldfld read sibling).
- **Probe** (`TestCases/NeoStepRawStfldClrObjVtFieldTest.cs`):
  - TC1: `o.S.a = 111;` -> host sum = 111 (a=111,b=0,c=0). Single write persists.
  - TC2: `o.S.a = 111; o.S.b = 222; o.S.c = 333;` -> host sum = 666. Three
    separate raw-Stfld writes; box/mutate/unbox must preserve the other fields
    (a zeroing/recreate bug would yield only the last write -> 333).
  - Both FAULT on HEAD (Stfld NIE at the first assignment); PASS after. A wrong
    value yields a different sum -> deliberate `1/0` (DivideByZero), matching
    the child-24/25/26 probe convention.
- Hand-checked constants: TC1 111+0+0 = 111; TC2 111+222+333 = 666.

## Verify plan
- Build CLI (`Debug_Neo --no-incremental`) + TestCases (`Debug`).
- NeoStep smoke: target 373+2 = 375/0 (373 baseline + TC1 + TC2); no regressions.
- `UnitTest_Struct2` PROGRESSES (no NIE at the direct assignment; residual =
  the `+=` / read siblings, out of scope).
- Stash-toggle: stash the engine file (keep probe + host types/helper) -> TC1/TC2
  FAULT (Stfld NIE) -> pop -> PASS.
- Write-back persistence: empirically proven by TC2 (the host reads back all
  three written values through the containing object).
- Legacy-neutral: the fix is inside `#if ENABLE_NEO_MODE` (file-gated
  `ILIntepreter.Neo.cs`).
- child-25 build-server-cache gotcha: kill `dotnet` build-server +
  `-p:UseSharedCompilation=false` after touching TestClass3.cs / TestVector3.cs.

## Open questions resolved
- **(triage OPEN) Is the byref `off` a byte offset or a field hash?** PINNED: it
  is the struct field's `FieldInfo.GetHashCode()` (a hash), per the runtime
  ldflda `else` branch comment + the `AppDomain.GetFieldOffset` -> `GetFieldIndex`
  -> `fieldMapping[name]` -> `FieldInfo.GetHashCode()` chain (child-15's finding
  generalized to the heap-object case). The fix does NOT chase a byte offset --
  it resolves the hash to a FieldInfo via the containing CLRType (the Area-4d
  mechanism), so the hash-vs-offset distinction is moot for the fix.
- **Marker or runtime detection?** Runtime detection (mirror child-19, NOT
  child-24): a VT-owner Stfld owner is always a byref, so no flat-bytes
  ambiguity.
