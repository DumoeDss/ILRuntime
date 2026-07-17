# Design — neo-raw-stfld-array-element

## Context

Raw `Stfld` reaches the Neo interpreter when the field's DECLARING type is a CLRType — the Neo
typed-splitter only rewrites CIL `stfld` into typed arms (`Stfld_I4`/`Stfld_Ref`/...) for an ILType
declaring type, so a CLR-declaring-type field stays as the raw `OpCodeREnum.Stfld` with
`OperandLong = (typeHash << 32) | fieldHash` (identical to Legacy's raw encoding). child-4 added the
raw `Stfld`/`Ldfld` handlers and resolved three CLR owner shapes; the array-element owner was deferred
with a tagged NIE. This change closes that deferred shape.

## The owner byref shape (verified)

`clrStructArray[i].field = x` lowers to `ldelema <StructType>; stfld <field>`. The Neo `ldelema`
handler for a CLR (non-IL) value-type-element array (`ILIntepreter.Neo.cs:5729-5749`, the `else`
branch after the `ILTypeInstance[]` case) stamps the 8-byte byref as:

```
*(int*)(frameBase + ip->DstOffset + 0) = arrIdx;       // mStack index of the System.Array
*(int*)(frameBase + ip->DstOffset + 4) = elementIdx;   // the ELEMENT INDEX (NOT a byte offset)
```

This is the SAME `(objIdx, off)` byref convention the `stind_*`/`ldind_*` consumer arms already
decode (`ILIntepreter.Neo.cs:5170-5340`): `objIdx == -1` -> frame-native; `mStack[objIdx] is Array`
-> `cArr.SetValue(v, off)` / `cArr.GetValue(off)` where `off` is the element index. So for the raw
`Stfld` handler, the owner slot at `ownerOff = ip->DstOffset` holds this byref, and `off` (the +4
int) is the element index.

Legacy confirms the semantics: `ILIntepreter.Register.cs` raw `Stfld` writeback
(`ObjectTypes.ArrayReference`, :3154-3158) is `arr.SetValue(obj, idx)` — box/mutate/writeback by
element index.

## Decision D1 — fix mechanism: `Array.GetValue`/`SetValue` box/mutate/unbox (NOT Marshal.OffsetOf)

**Chosen:** mirror Legacy's `ObjectTypes.ArrayReference` writeback exactly:

```csharp
// In the raw Stfld handler, CLR value-type declaring branch, replacing the :4076-4077 NIE:
else if (objIdx >= 0 && mStack[objIdx] is Array cArr)
{
    int elementIdx = off;                                  // off == *(int*)(ownerOff+4)
    object boxedElem = cArr.GetValue(elementIdx);          // box the struct element (a copy)
    f.SetValue(boxedElem, value);                          // reflection-write the field
    cArr.SetValue(boxedElem, elementIdx);                  // unbox the mutated struct back
}
```

`value` is already a boxed `object` marshalled by field category earlier in the handler
(`ILIntepreter.Neo.cs:4044-4057`: primitive -> `NeoBoxPrimitiveByType`, VT -> `ReadNeoValueType`, ref
-> `mStack[idx]`). `f` is the already-resolved `FieldInfo` (`ct.GetField(fieldHash)`). No new helper,
no offset math.

**Rejected alt (the LEAD's suggested mechanism): resolve the array-element byref to the element's
base + the field's `Marshal.OffsetOf` + write the field value directly.** This was considered and
rejected: it would require pinning the array element and computing a raw byte pointer into the array
backing store (`Marshal.UnsafeAddrOfPinnedArrayElement(arr, elementIdx) + fieldByteOffset`), which is
more fragile than reflection and gains nothing. `Array.GetValue`/`SetValue` + `FieldInfo.SetValue`
already does the box/mutate/unbox correctly and is byte-identical to Legacy. `Marshal.OffsetOf`
(child-15's `ResolveClrStructFieldByteOffset` pattern) is the right tool only when you must form a
raw frame byte address — which the array-element case does NOT need (the element lives in the Array's
CLR-managed backing store, not in the Neo frame). **The LEAD's suggested `Marshal.OffsetOf` path is
superseded by the simpler reflection round-trip.**

This is the SAME pattern as child-4's CLR-VT-OWNER Stfld frame-byref case (`:4068-4075`:
`ReadNeoValueType` the whole struct from the byref target -> `f.SetValue` -> `WriteNeoValueType`
back), with `Array.GetValue`/`SetValue` substituted for the frame byte read/write (because the
element lives in the array, not the frame).

## Decision D2 — both Stfld declaring-type branches; ref-type branch is defensive

There are TWO array-element NIE branches in the raw Stfld handler:

1. **`:4076-4077` (CLR value-type declaring branch)** — the field's declaring type is a CLR struct.
   This is the REACHABLE case (`clrStructArray[i].field = x`, the ~4 full-smoke hits). Apply D1.
2. **`:4102-4103` (CLR ref-type declaring branch)** — the field's declaring type is a CLR ref type
   and the owner `target is Array`. UNREACHABLE in practice: `ldelema` on a ref-type-element array
   throws (`:5744-5746`), and a ref-type-declaring field's owner can't be an Array via any other
   path. Nevertheless, route it through the SAME `Array.GetValue`/`f.SetValue`/`Array.SetValue`
   pattern (decode `elementIdx = *(int*)(ownerOff+4)`) for symmetry and to remove the dead NIE the
   LEAD flagged. Harmless whether or not it is ever hit.

## Decision D3 — Ldfld array-element is DEFERRED (not symmetric; deeper)

The LEAD's prompt allowed handling `Ldfld` "if symmetric." It is NOT cleanly symmetric, so this change
defers it. Rationale:

- A raw `Stfld` value-type owner is **always a byref** (`ldloca` for a local, `ldelema` for an array
  element) — you cannot write a field without the address. So the `objIdx == -1` (frame) vs
  `mStack[objIdx] is Array` (array) discriminator child-4 used is unambiguous.
- A raw `Ldfld` value-type owner can be EITHER **flat managed bytes** (`ldloc` by-value of a CLR
  struct local — child-4's established representation; the existing `:3890-3902` branch boxes the
  whole struct via `ReadNeoValueType`) OR a **byref** (`ldelema` for an array element). The Neo frame
  is UNTYPED (no per-slot `ObjectType` tag, unlike Legacy's `reg->ObjectType`), so the handler cannot
  tag-distinguish the two shapes.
- A naive discriminator (`int objIdx = *(int*)(ownerOff); if (objIdx >= 0 && mStack[objIdx] is Array)
  ...`) is UNSAFE: a by-value struct local whose first int field happens to coincide with a valid
  mStack index holding an Array (e.g. a method with an array argument at `mStack[0]` and a struct
  field value of `0`) would false-positive into the array path and mis-read. Legacy distinguishes via
  `GetObjectAndResolveReference(...)->ObjectType` (`Register.cs:3200`); Neo has no equivalent tag.
- A robust Ldfld fix needs a JIT-time marker stamped on the raw `Ldfld` (child-15
  `NeoLdfldaClrStructLocalFieldMarker` style) to tag the owner as an array-element byref, then a
  runtime branch on that marker. That is a JIT + runtime change — deeper than this child's scope.

The raw `Ldfld` ref-type-branch array NIE (`:3928-3929`) is also unreachable (same `ldelema`
ref-type-element guard) and is left as a defensive tagged NIE. The raw `Ldfld` value-type array-element
READ (`:3890-3902`) is a latent silent-corruption (no NIE; not in the documented hit surface) if ever
exercised; it is NOT touched here. Both are noted as follow-ups.

## Probe design (must FAULT on HEAD)

The probe must hit the reachable value-type-declaring branch (`:4077`) and fault on the tagged NIE on
HEAD, then pass after the fix. To AVOID the pre-existing unrelated Neo float bugs (`addi`-on-float /
`conv.i4`-float-bit-reinterpret, child-16 candidate), the probe uses a host CLR struct with INT
fields:

- Host struct `NeoArrElemIntProbe { public int A; public int B; }` (blittable, default
  `LayoutKind.Sequential`, no ValueTypeBinder needed — the fix uses reflection, not the flat-byte
  binder path).
- Host helper `TestCLRBinding.NeoArrElemFieldSum(NeoArrElemIntProbe[] arr, int i)` returns
  `arr[i].A + arr[i].B` — the READ-BACK happens on the CLR (host) side, so the probe verifies the
  Neo `Stfld` WRITE landed in the array without depending on Neo `Ldfld` (deferred) or Neo float
  arithmetic (broken).
- **TC1** `NeoStepRawStfldArrElem_TC1`: `new NeoArrElemIntProbe[4]; arr[1].A = 4242; arr[1].B = 17;`
  assert `NeoArrElemFieldSum(arr, 1) == 4259` else `1/0`. Faults on HEAD (`arr[1].A = 4242` throws
  the `:4077` NIE).
- **TC2** `NeoStepRawStfldArrElem_TC2`: writes to TWO different element indices
  (`arr[0].A=10/B=20`, `arr[5].A=30/B=40`), asserts the four-way sum `== 100` else `1/0`. Proves the
  element-index decode is correct (not a constant-0 or arrIdx-as-index confusion) across multiple
  indices. Faults on HEAD (the first `arr[0].A = 10` throws the NIE).

Both probe method names embed "NeoStep" so the smoke filter picks them up. Build `TestCases` with
plain `Debug` (NEVER `Debug_Neo`).

## Open questions (for the apply worker)

- **Stash-toggle:** confirm both TC1 and TC2 FAULT on HEAD by temporarily reverting the two handler
  edits (expect the `:4077` NIE to propagate), then PASS after restoring.
- **NeoStep smoke:** expect `352/0` -> `~354/0` (352 + 2 probes; TC names may match the filter twice
  if they share a prefix — verify the exact delta).
- **Full smoke (optional, pre-crash):** the ~4 "array-element field write is deferred" tagged-NIE
  occurrences should drop to 0 (the full run still NRE-crashes mid-stream on the known unrelated
  `GenericMethodTest` NRE).
