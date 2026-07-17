## Context

The Neo JIT splits CIL `Stfld`/`Ldfld` into typed arms (`Stfld_I1..Stfld_Ref/Stfld_Value`,
`Ldfld_*`) ONLY when the field's declaring type is an ILType (`JITCompiler.cs` `case
Code.Ldfld` ~2789 / `case Code.Stfld` ~2860, `if (type is ILType)`). For a **CLR declaring
type** the `else` branch (~2812 / ~2882) leaves the raw `OpCodeREnum.Ldfld`/`Stfld` opcode
and stamps `OperandLong = (typeHash << 32) | fieldHash`. `ExecuteNeo` has no case for the
raw opcode, and the Neo offset-lowering pass (`Optimizer.Neo.cs` ~934/~960) does not list
it, so it reaches the Step-6 default with unset `DstOffset`/`SrcOffset`. Instrumented
full-smoke evidence (27 hits, this change's `planner-findings.md`): every escaping field is
declared on a CLR type — CLR classes (`TestCLRAttribute`, `TestCLRBinding`,
`TestVectorClass`, `BindableProperty`), CLR structs (`TestVector3`, `TestVectorStruct`,
`TestStruct`, `TestStructA`, `TestVector3NoBinding`), an IL type inheriting a CLR base
(`TestCls : ClassInheritanceTest`), and a CLR-struct array element.

The encoding the JIT emits is **identical to Legacy's raw `Stfld`/`Ldfld`** — Legacy
`ILIntepreter.Register.cs:3093-3196` (Stfld) and `3197-3253` (Ldfld) decode
`type = AppDomain.GetType((int)(OperandLong >> 32))`, `fieldToken = (int)OperandLong`,
`CLRType.GetField(fieldToken)`. So the JIT needs no change; the gap is purely the Neo
runtime handler + the lowering-pass entry.

## Goals / Non-Goals

**Goals:**
- Raw `Stfld`/`Ldfld` on a CLR declaring type execute in `ExecuteNeo` instead of hitting
  the Step-6 default.
- Cover the owner shapes observed in the smoke: CLR reference-type object (boxed in
  `mStack`), CLR value-type local (frame flat bytes / frame-native byref), and the
  IL-instance-with-CLR-base-field case.
- NeoStep regression probes that FAULT without the fix (raw opcode → Step-6 NIE) and pass
  after.
- No regression to the typed arms (NeoStep smoke stays 314/0/0).

**Non-Goals:**
- No JIT splitter change (the `else`-branch encoding is already correct).
- No Legacy (`ExecuteR`) change; all edits under `ENABLE_NEO_MODE`.
- Not a performance optimization — correctness first. A box-roundtrip for exotic
  value-type field shapes is acceptable if it is correct.
- The sibling "CLR value type with reference fields and no ValueTypeBinder" NIE
  (Step 13b inside `ReadNeoValueType`/`WriteNeoValueType`) is a separate child
  (`neo-clr-vt-reffields-binder`); this change does not fix that, only routes around it
  where the field is a plain primitive / ref / CLR-struct.

## Decisions

### D1. Fix site = ExecuteNeo runtime handler + offset-lowering entry (NOT a splitter typed-arm)
The raw `Ldfld`/`Stfld` opcode is the correct representation for a CLR-declaring-type
field — CLR fields cannot be lowered into the ILType-field typed arms (those address
`ILTypeInstance.Primitives[]`/`ManagedObjects[]`, which a CLR object does not have).
Reusing the raw opcode needs only: (a) one `case Ldfld:`/`case Stfld:` in `ExecuteNeo`,
and (b) adding the raw opcodes to the existing offset-lowering case-lists so
`DstOffset`/`SrcOffset` are stamped from `Register1`/`Register2` (same shape as the typed
arms: Ldfld `DstOffset=R1, SrcOffset=R2`; Stfld `DstOffset=R1, SrcOffset=R2`).

**Alternative considered — new dedicated opcodes** (`Ldfld_Clr`/`Stfld_Clr`) lowered in the
JIT `else` branch. Rejected: more churn (new enum members, the `OpCodeR` Operand4
spare-field map, the register-type tracker, the offset-lowering + compaction passes) for
zero information gain — the raw opcode + `OperandLong` already carries the full field
identity, and the lowering entry is two extra case labels.

### D2. Field identity from OperandLong; reuse Area 4d for the CLR-object sub-case
Decode `typeHash = (int)(OperandLong >> 32)`, `fieldHash = (int)OperandLong`. Resolve
`type = AppDomain.GetType(typeHash)` (a `CLRType`) and `field = type.GetField(fieldHash)`.
For a **boxed CLR object** owner, reuse the Step 13 Area 4d helpers verbatim
(`ILIntepreter.Neo.cs:5784-5820`): `NeoReadClrObjectField(appdomain, target, fieldHash)` /
`NeoWriteClrObjectField(appdomain, target, fieldHash, value)` (which call
`CLRType.GetFieldValue`/`SetFieldValue`). This is the path the implemented `Ldflda` arm
already documents (comment @1799-1813).

### D3. Owner resolution mirrors the `Stobj`/`Ldobj` byref-resolution structure
The `Stobj` arm (`ILIntepreter.Neo.cs:4995-5097`) is the canonical Neo byref resolver — it
reads `(objIdx, off)` and dispatches `objIdx == -1` (frame-native) / `NeoIsClrObject` (CLR
object) / else (IL instance). The new handler follows the same dispatch, specialized for a
single field. The owner-shape taxonomy (dump-confirmed):

| Declaring type | Opcode | Owner representation (owner register slot) |
|---|---|---|
| CLR ref type | Ldfld/Stfld | first int = `mStack` index of the boxed object |
| CLR value type | **Ldfld** | inline flat bytes (owner is `ldloc` by-value); field at `ownerOff + fieldNativeOffset` |
| CLR value type | **Stfld** | frame-native byref `(-1, structBaseOff)` (owner is `ldloca`); field at `structBaseOff + fieldNativeOffset` |
| CLR value-type array element | Stfld | heap byref `(arrMStackIdx, elemByteOff)` (owner is `ldelema`) |
| IL type, CLR base field | Stfld | first int = `mStack` index of the `ILTypeInstance` |

Discriminate on `type.TypeForCLR.IsValueType` + the opcode (the value-type Ldfld-by-value
vs Stfld-byref split). For the value-type flat-bytes cases, compute the field's native
offset and do a typed primitive read/write (`Unsafe.ReadUnaligned`/`WriteUnaligned` sized
to the field's CLR type), mirroring the typed `Ldfld_I4`/`Stfld_I4` arms but addressing a
CLR-struct byte region instead of `ILTypeInstance.Primitives`.

### D4. Field native offset resolution
Resolve `FieldInfo` via `CLRType.GetField(fieldHash)`, then
`System.Reflection.FieldInfo.GetFieldOffset()` for the offset within the declaring CLR
value type. (For `TestVector3`: X@0, Y@4, Z@8.) This avoids needing the ValueTypeBinder and
works for both bound and unbound CLR structs. For a ref/CLR-struct-typed field on a
flat-bytes struct owner, box-roundtrip via `ReadNeoValueType`/`WriteNeoValueType` +
`GetFieldValue`/`SetFieldValue` (correct, not fast).

## Risks / Trade-offs

- **[Owner-shape ambiguity at runtime]** The value-type Ldfld owner is inline flat bytes
  while the Stfld owner is a byref — discrimination relies on `IsValueType` + opcode, which
  is sound for the C#-emitted `ldloc`/`ldloca` patterns but must be verified for
  compiler-variations (e.g. `ldfld` after `ldflda`+`ldobj`). → Mitigation: add a tagged-NIE
  guard for an owner shape the handler cannot resolve (fail loud, like the Stobj arm's
  Step-17 deferrals); cover each shape with a NeoStep probe.
- **[Lowering-pass edit touches shared code]** Adding raw `Ldfld`/`Stfld` to the offset-
  lowering case-lists could affect the typed arms if mis-edited. → Mitigation: the new
  labels only add opcodes to the EXISTING `DstOffset/R1, SrcOffset/R2` assignment blocks
  (no logic change); NeoStep smoke (314/0/0) is the regression gate.
- **[IL-instance-with-CLR-base-field case]** `mStack[objIdx]` is an `ILTypeInstance` but
  the field is on its CLR base (`TestCls..ctor`). Area 4d's
  `appdomain.GetType(target.GetType())` may not resolve to the CLR base. → Mitigation:
  handle explicitly (resolve the CLR base field on the IL instance's CLR backing, Legacy
  parity `Register.cs:3113-3118`), or defer with a tagged NIE if it proves non-trivial
  (this case is 5 of 27 hits — all `TestCls..ctor`; acceptable to defer if isolated).
- **[CLR-struct array-element field]** `arr[i].X` (ldelema + stfld) — the heap byref into a
  CLR array. → Mitigation: reuse the array-element byref resolution if the byref consumers
  already handle CLR arrays; else defer with a tagged NIE (2 of 27 hits).

## Open Questions

- Exact owner-slot decoding for the value-type Ldfld-by-value case: is the field read from
  `ownerOff + fieldNativeOffset`, or does the register already hold a byref? The apply
  worker should confirm with a targeted probe before committing to the flat-bytes read.
- Whether the IL-instance-CLR-base-field and array-element cases can share the Area 4d path
  or need dedicated branches — resolve during apply (both are minority cases; a tagged-NIE
  defer is acceptable for the first cut, with follow-up tasks logged).
