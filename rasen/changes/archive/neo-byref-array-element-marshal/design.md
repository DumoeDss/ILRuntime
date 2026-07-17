# Design: neo-byref-array-element-marshal

## 1. The fix point + why it is the right one

`NeoMarshalByrefFieldToSlot` (`ILIntepreter.Neo.cs:453`) is the SHARED marshal for
a byref whose referent is a FIELD/ELEMENT of an mStack object. It is invoked in
BOTH directions of a CLR-method call that has a `ref`/`out` param:

- **Forward (call-arg deref)** -- `CopyNeoCallArguments` (`:424`,
  `isWrite:false`): dereference the caller's byref into the callee's flat-byte
  param slot BEFORE the call.
- **Write-back (post-call)** -- `CopyNeoCallThisBack` (`:653`, `isWrite:true`):
  write the (possibly-mutated) callee slot bytes BACK through the caller's byref
  AFTER the call.

The helper today has three branches:
1. `target is ILTypeInstance` -- IL heap field (`Primitives[off]` flat bytes) +
   the F-10 boxed-CLR-struct-field sub-case.
2. `target is Array` -- STUB that throws the "Area 4c" NIE (THIS change replaces
   it).
3. fall-through -- CLR-object field via `NeoReadClrObjectField`/
   `NeoWriteClrObjectField` (the field-hash accessor; child-9 IL-CLR-base + the
   raw Stfld/Ldfld handlers reuse these).

The bug: branch 2 throws instead of marshalling. The fix: give branch 2 a real
body that mirrors branch 3's per-element-category marshalling, swapping the
field accessor for `Array.GetValue(off)` / `Array.SetValue(value, off)`.

## 2. The byref encoding (confirmed at the producer)

`ldelema` on a CLR value-type-element array (`ILIntepreter.Neo.cs:5793-5798`):

```csharp
Type elemClrType = la.GetType().GetElementType();
if (elemClrType == null || !elemClrType.IsValueType)
    throw new NotImplementedException("Step 17: ldelema on a CLR array ...");
*(int*)(frameBase + ip->DstOffset + 0) = arrIdx;
*(int*)(frameBase + ip->DstOffset + 4) = elementIdx;
```

So the 8-byte byref is `(arrIdx, elementIdx)` where:
- `arrIdx` (the `objIdx` / first int) = the mStack index of the `System.Array`.
- `elementIdx` (the `off` / second int) = the ELEMENT INDEX (NOT a byte offset,
  NOT a field hash).

This is the SAME encoding the raw `Stfld` array-element arm (child-19,
`:4101-4113`), the raw `Ldfld` array-element arm (child-24, `:3906-3910`), and
the `stind_*`/`ldind_*` arms consume. The `off` parameter passed to
`NeoMarshalByrefFieldToSlot` IS the element index.

A reference-type-element array byref CANNOT reach here: `ldelema` throws at the
`:5794` guard for a non-value-type element. So branch 2 only ever sees a value-
type element (primitive / enum / struct).

## 3. The branch (forward read + write-back)

Mirror branch 3 (the CLR-object-field branch, `:518-574`) verbatim in structure,
swapping the accessor. The element type comes from the call signature
(`elemType` param); recover it from the array's `GetElementType()` when the
caller did not propagate it (defensive).

### 3a. Forward read (`isWrite == false`)

```csharp
object elemVal = arr.GetValue(off);   // box a VT element; primitive stays boxed
if (et != null && (et.IsPrimitive || et.IsEnum))
    { if (elemVal != null) WriteNeoValueType(elemVal, slot, sz);
      else InitBlock(slot, 0, sz); }          // flatten the boxed value
else if (et != null && et.IsValueType)
    { if (elemVal != null) WriteNeoValueType(elemVal, slot, sz);
      else InitBlock(slot, 0, sz); }          // flatten the boxed struct
else  // reference element (unreachable via ldelema; kept for symmetry)
    { if (elemVal == null) *(int*)slot = -1;
      else { mStack.Add(elemVal); *(int*)slot = mStack.Count - 1; } }
```

`sz` is `map.PrimitiveSize[i]` = the element type's managed size (1 for `byte`,
12 for `TestVector3`). `WriteNeoValueType` does `Unsafe.WriteUnaligned<T>` so a
boxed `byte` writes exactly 1 byte and a boxed `TestVector3` writes 12.

The callee (`CLRMethod.Invoke`) then reads this slot exactly like a by-value
param of the element type: `pt` is de-byref'd at `CLRMethod.cs:447`, so a
`ref byte` param hits the `ReadNeoUInt8` arm (`:534`) and a `ref TestVector3`
param hits the CLR-struct flat-bytes arm (`:504`). The reflection `Invoke`
mutates the `param[i]` box in place; `CLRMethod.Invoke`'s own Area-4c epilogue
(`:599-631`) re-flattens the mutated box into the SAME callee slot via
`WriteNeoValueType`. So after `Invoke` returns, `targetBase + slotOff` holds the
mutated element value.

### 3b. Write-back (`isWrite == true`)

```csharp
object value;
if (et != null && (et.IsPrimitive || et.IsEnum))
    { int cur = 0; value = ReadNeoValueType(et, slot, ref cur, sz); }  // box the slot
else if (et != null && et.IsValueType)
    { int cur = 0; value = ReadNeoValueType(et, slot, ref cur, sz); }  // box the struct
else  // reference element (unreachable; symmetry)
    { int vIdx = *(int*)slot; value = vIdx >= 0 ? mStack[vIdx] : null; }
arr.SetValue(value, off);   // write the (mutated) value back to the element
```

`ReadNeoValueType` boxes the callee slot's flat bytes into the element type (a
boxed `byte` / boxed `TestVector3`). `Array.SetValue(value, elementIdx)`
unboxes/stores it back into the array element. This mirrors child-19's raw-Stfld
array-element WRITE (`((Array)target).SetValue(boxedElem, off)`) exactly.

## 4. Why ONE branch covers BOTH directions

`CopyNeoCallArguments` and `CopyNeoCallThisBack` are STRUCTURALLY IDENTICAL loops
over the same `NeoCallParamMap`: both read `(objIdx, off)` from the byref source
slot and call `NeoMarshalByrefFieldToSlot` -- differing only in `isWrite`
(`:424` false vs `:653` true). The branch discriminates on `isWrite` internally
(read vs write-back), so adding it makes BOTH the forward deref AND the write-back
work for an array-element referent. There is no separate call site to patch.

The write-back is GATED by `map.PrimitiveByRefWriteBack[i]` (a `ref`/`out` param
is flagged; an `in`-only param is not) at `CopyNeoCallThisBack:627`. A `ref byte`
param IS flagged, so the write-back fires and the mutation persists end-to-end.

## 5. The snapshot interaction (no-ops for the array case)

For a VT-`this` or `ref`/`out` call, the Call arm snapshots every write-back-
flagged byref source's `(objIdx, off)` BEFORE the call (`SnapshotNeoCallByRefSources`,
to survive a dest-register reuse that clobbers the byref bytes). For an array-
element byref, the snapshot captures `(arrIdx, elementIdx)` -- both are stable
across the call (the array does not move; the index is a literal). On the
write-back, `CopyNeoCallThisBack` reads `(arrIdx, elementIdx)` from the snapshot
and calls the helper, which decodes them exactly as the forward pass did. No
special handling is needed.

For the canary `setBit(ref byte, ...)` the call dest is void (`retDstPtr = null`),
so the byref source register is NOT reused as the dest and the snapshot is
redundant but harmless.

## 6. Correctness of the element-type/size plumbing (no JIT change)

The byref-param call map (`NeoCallParamMap`) is built by the optimizer
(`Optimizer.Neo.cs` Step-13-Area-4c). For a byref param it records:
- `PrimitiveByRefSrc[i] = true` (the slot is a byref).
- `PrimitiveByRefElemType[i]` = the element CLR `System.Type` (the `elemType`
  passed to the helper).
- `PrimitiveSize[i]` = the element type's managed size (the `sz`).
- `PrimitiveByRefWriteBack[i]` = the ref/out gate.

All four already exist and are correct for a value-type-element byref (the
forward deref + write-back machinery is generic over the element type; it already
works for frame-native byrefs and CLR-object-field byrefs). This change only adds
the Array-referent branch that CONSUMES them. NO JIT / optimizer / object-model /
binding change.

## 7. Legacy-neutrality

`ILIntepreter.Neo.cs` is entirely inside `#if ENABLE_NEO_MODE`. Legacy
(`ExecuteR`) marshals byref array params via the `ObjectTypes.ArrayReference`
case in the autogen wrapper epilogue (e.g. `setBit_0` `:133-138`) and is
untouched. The change is Legacy-neutral by construction.

## 8. Probes

`TestCases/NeoStepByrefArrayElementTest.cs` (names embed "NeoStep" so the smoke
filter picks them up):

- **TC1 (primitive `byte`):** allocate `byte[] {10,20,30}`, call a host CLR
  helper `NeoByrefArrElemIncrementByte(ref byte b)` that does `b = (byte)(b + 5)`
  on `arr[1]`, assert `arr[1] == 25` via a deliberate `1 / (arr[1] - 25)`
  DivideByZero guard (FAULTS on HEAD: the NIE; PASS after).
- **TC2 (primitive `int`, write-back persistence at multiple indices):** allocate
  `int[] {1,2,3,4}`, call `NeoByrefArrElemIncrementInt(ref int v)` (`v += 100`)
  on `arr[0]` and `arr[3]`, assert `arr[0] == 101 && arr[3] == 104` via
  `1 / ((arr[0]-101) | (arr[3]-104))`.
- **TC3 (VT element `TestVector3`, twice-call persistence proof):** host-build
  `TestVector3[]{(7,70,700)}` via `BuildNeoByrefVectorArray()`, call
  `NeoByrefArrElemMutateVectorAndReturnIncomingSum(ref arr[0])` TWICE. The helper
  returns the INCOMING `X+Y+Z` then adds `(1000,2000,3000)`. `ret1 = 777`; after
  call 1 the element is `(1007,2070,3700)` IF the write-back persisted. `ret2 =
  6777` IF persisted (else 777). Assert `(ret1==777 && ret2==6777)` via a `1/0`
  guard. The twice-call pattern proves the struct write-back persisted WITHOUT
  reading the struct element IL-side (`ldelema; ldobj` on a struct array is
  R1-Shape-B from triage batch-2, still unimplemented -- the twice-call isolates
  the byref marshal from that separate gap). This proves a STRUCT element
  round-trips (box/mutate/unbox) -- the highest-value coverage.

Host helpers added to `TestClass3.cs` in the **`TestCLRBinding`** class (NOT
`TestClass3` -- see gotcha; the file `TestClass3.cs` defines multiple classes and
`TestClass3` spans only lines 12-39; child-19/24's array-element helpers are also
in `TestCLRBinding`).

## 9. Hand-check of TC3's expected constants (child-24 lesson)

`arr[0] = (7, 70, 700)` (host-built). Helper returns `(int)(v.X+v.Y+v.Z)` then
adds. `ret1 = 7+70+70 = 777`. After call 1: `X=1007, Y=2070, Z=3700` (IF
write-back persisted). `ret2 = 1007+2070+3700 = 6777` (IF persisted; else 777).
Probe asserts `ret1==777 && ret2==6777`. Hand-checked: 7+70+700=777;
(7+1000)+(70+2000)+(700+3000)=1007+2070+3700=6777.

Wait -- recompute Y: 70 + 2000 = 2070. Z: 700 + 3000 = 3700. X: 7 + 1000 = 1007.
Sum = 1007 + 2070 + 3700 = 6777. Confirmed **6777**.

## 10. Rejected alternatives

- **Route via the stind/ldind consumer arms (the stub's suggested fix).** Wrong:
  a byref PARAM is marshalled by `CopyNeoCallArguments`/`CopyNeoCallThisBack`,
  which call THIS helper, not the stind/ldind arms. The stind/ldind arms are for
  a `stind.*`/`ldind.*` OPCODE consuming a byref; a `call` with a byref arg is a
  different opcode and a different marshal path.
- **Fix the autogen `setBit_0_Neo` instead.** The canary routes through the
  REFLECTION FALLBACK (the JIT emits a plain `Call`, not `Call_Redirect`), so the
  autogen redirect is not on this call's path. Even if it were, the autogen path
  is a SEPARATE defect class (it reads the byref bytes as a flat value and has no
  write-back epilogue). The reflection fallback is the correct, general fix point
  (it covers EVERY CLR method with a `ref arr[i]` arg, not just the autogen
  subset). The autogen path is out of scope (documented in the proposal).
- **Runtime `mStack[objIdx] is Array` detection in a NEW helper.** Unnecessary --
  the existing branch 2 ALREADY detects `target is Array` (it just throws). The
  fix is to give that detection a real body.
