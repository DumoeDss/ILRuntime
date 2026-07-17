# Proposal: neo-byref-array-element-marshal

## Why

A `ref arr[i]` byref passed to a CLR method -- the most pervasive real-world byref
pattern of the residual Neo set (the canary is
`TestCases.CLRBindingTest.CLRBindingTest01`:
`TestClass3.setBit(ref mAllMissionData[(missionID - 1) >> 2], ...)`) -- throws a
tagged `NotImplementedException` under Neo. The shared byref-field marshal
`NeoMarshalByrefFieldToSlot` (`ILIntepreter.Neo.cs:453`) has a branch for an
`ILTypeInstance` referent, a branch for a CLR-object field referent, and a STUB
`Array` branch (`:508-517`) that throws:

```
Step 13 Area 4c: a CLR-array-element byref param is not handled
(route via the array stind/ldind path, not the field accessor)
```

`ref arr[i]` into a CLR method is pervasive (bit-set/array-flag helpers, buffer
fills, vector math, etc.). This single canary gates a whole class of production
hotfix paths.

## Re-audit verdict (REPRODUCES on HEAD `fc2baa26`)

Confirmed by running the canary under Neo (`Debug_Neo`, `-f net8.0`):

```
dotnet run ... true CLRBindingTest01
```

FAULTS with the exact tagged NIE at `ILIntepreter.Neo.cs:515`, thrown from
`ExecuteNeo`'s `Call` arm (`:3008`) via `CopyNeoCallArguments` (`:424`,
`isWrite:false`) -- the FORWARD call-arg deref. The JIT for the call site is:

```
5:ldelema r2,r0,r3      ; ref byteArr[(missionID-1)>>2] -> (arrIdx, elementIdx) in r2
9:call -, r2, r3, r4, ILRuntimeTest.TestFramework.TestClass3::setBit(Byte ByRef, ...)
```

`ldelema` (`ILIntepreter.Neo.cs:5763-5800`) on a CLR value-type-element array
encodes the 8-byte byref as `(arrIdx, elementIdx)` where the `off` half IS THE
ELEMENT INDEX (NOT a byte offset, NOT a field hash) -- the same convention the
`stind_*`/`ldind_*` arms, the raw `Stfld`/`Ldfld` array-element arms (children
19/24), and Legacy's `ObjectTypes.ArrayReference` writeback consume.

The call routes through the REFLECTION FALLBACK (`OpCodeREnum.Call` ->
`CopyNeoCallArguments` -> `CLRMethod.Invoke`), NOT the autogen `setBit_0_Neo`
redirect (the stack trace proves `CopyNeoCallArguments` runs). The forward deref
sees `objIdx = arrIdx >= 0`, `mStack[arrIdx] is Array`, and calls
`NeoMarshalByrefFieldToSlot(isWrite:false)` -> the stub throws.

The POST-CALL WRITE-BACK (`CopyNeoCallThisBack` -> `NeoMarshalByrefFieldToSlot`
with `isWrite:true`, `:653`) would hit the SAME stub on the reverse pass -- so the
single `Array` branch MUST cover both directions.

## Central question

The stub comment claims "route via the array stind/ldind path, not the field
accessor." But a byref PARAM to a CLR method is NOT a `stind`/`ldind` consumer --
it is marshalled by `CopyNeoCallArguments` (forward) / `CopyNeoCallThisBack`
(write-back), BOTH of which route an `mStack[objIdx] is Array` referent to
`NeoMarshalByrefFieldToSlot`. So the fix point IS this helper, and the branch must
mirror the existing CLR-object-field branch but swap the field accessor
(`NeoReadClrObjectField`/`NeoWriteClrObjectField`) for `Array.GetValue(off)` /
`Array.SetValue(value, off)` (child-19/24 precedent). The element index is `off`
(the ldelema convention).

## What changes

One runtime branch in `NeoMarshalByrefFieldToSlot` (`ILIntepreter.Neo.cs`,
Neo-gated by the file's `#if ENABLE_NEO_MODE`). Decode `off` as the element
index; forward read = `Array.GetValue(off)` flattened to the callee slot via
`WriteNeoValueType` (box a VT element so the reflection callee mutates the box);
write-back = box the callee slot by the element type via `ReadNeoValueType` +
`Array.SetValue(boxed, off)`. NO JIT/optimizer/object-model change -- the byref
encoding + the call map's element-type/size plumbing already exist (Step 13
Area 4c). The branch is REACHED for both `isWrite:false` and `isWrite:true`
(this is the single shared helper for both directions).

Capability home: `neo-byref` (owns Step-13 Area-4c byref-param marshalling;
sibling of children 15/17/19/24 -- all "access a CLR value-type that lives
somewhere other than a frame local").

## Out of scope

- A reference-type-element array byref is UNREACHABLE here (`ldelema` NIEs on a
  ref-type element at `:5794`); the reference arm is kept only for symmetry.
- The autogen Neo redirect path's own byref handling (a separate defect class;
  the canary routes through the reflection fallback, which this fix covers).
