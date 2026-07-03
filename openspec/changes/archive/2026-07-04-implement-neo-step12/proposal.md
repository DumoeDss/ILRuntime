## Why

The Neo interpreter (`ExecuteNeo`) currently has NO working path for accessing
fields of an **in-frame** value type (a VT local/temp stored as flat bytes in
the frame byte region). The only typed `Ldfld_*`/`Stfld_*` arms that exist
(`ILIntepreter.Neo.cs` ~1600-1688) dereference an `ILTypeInstance` from
`mStack` — i.e. they only serve **heap-object** fields. Any IL method that
reads or writes a field of a stack-resident struct (the common case:
`Vector3 v; v.x = 1; float r = v.x + v.y;`) therefore mis-executes: the
operand register does not hold an mStack index, it holds the struct's raw
bytes. Step 12 closes this gap with dedicated `_Inline` field opcodes that do
pure pointer arithmetic on `frameBase`, plus the JIT rule that picks them,
plus a memset-0 `Initobj` for in-frame VTs.

Note on framing: contrary to a literal reading of the Step 12 title, the Neo
path **never adopted** the `ValueTypeObjectReference` / `AllocValueType`
descriptor model in the first place (confirmed: zero references in
`ILIntepreter.Neo.cs` / `JITCompiler.cs` Neo regions / `Optimizer.Neo.cs` —
they are Legacy-only, in `ILIntepreter.Register.cs` / `RuntimeStack.cs` /
`ILIntepreter.cs`). The frame allocator (`JITCompiler.AllocateLocalStackSpaces`,
~1129-1156) already reserves contiguous, sized byte space for VT locals. So
Step 12 is not "removing a descriptor" — it is **completing** the flat-byte
storage model by adding (a) natural alignment, (b) the inline field-access
opcodes, (c) the JIT discriminator that selects them, and (d) the memset
`Initobj` and its ref-field sub-case.

## What Changes

- **New opcodes** `Ldfld_<K>_Inline` and `Stfld_<K>_Inline` (one per primitive
  kind K, mirroring the existing `Ldfld_I1..U8,R4,R8,Ref` /
  `Stfld_*` set) added to `OpCodeREnum`. Each `ExecuteNeo` arm is pure pointer
  arithmetic on `frameBase` — no `mStack` lookup, no `ILTypeInstance`, zero
  branches. The `Ref` variant copies an mStack index (the in-frame VT's own
  ref-offset slot, allocated by the optimizer).
- **JIT lowering rule** in `JITCompiler` (the `Code.Ldfld` / `Code.Stfld`
  cases, ~1853-1896): when the field's owning static type is an in-frame value
  type, emit the `_Inline` variant; otherwise keep the existing heap-object
  `Ldfld_*` / `Stfld_*`. The discriminator is the **operand register's slot
  kind** (in-frame VT vs reference slot), resolved via the optimizer's
  `StackSlotInfo` / register-type tracking, NOT the field's declaring ILType
  (which is identical in both cases).
- **Optimizer frame allocation** (`Optimizer.Neo.cs` + the
  `AllocateLocalStackSpaces` / `AllocateSlotForType` paths): size VT slots by
  `TotalPrimitiveSize` + `TotalReferenceCount` (already done) AND apply natural
  alignment per the slot's largest primitive field, so pointer reads in the
  `_Inline` arms are aligned (matches the heap `ILTypeInstance.Primitives`
  layout, keeping memcpy互通 valid for Step 12b).
- **`Initobj` memset** (`ILIntepreter.Neo.cs:1509`): in-frame IL value-type
  `Initobj` becomes `Unsafe.InitBlock(frameBase + offset, 0, size)` for the
  primitive region (already present for `refCnt == 0`); the ref-field sub-case
  (currently `throw NotImplementedException ... Step 7 RefOffset lowering` at
  ~1533) is resolved by also zeroing the slot's ref-offset mStack slots to
  null. The CLR-value-type `Initobj` branch (~1539) stays Step 13.
- **New tests** `TestCases/NeoStep12Test.cs` covering: Vector3 field
  read/write/sum; nested value type (`struct Outer { Inner i; int y; }`);
  VT containing a reference field; no regression on existing value-type usage.

## Capabilities

### New Capabilities
- `neo-value-types`: In-frame value-type storage layout and inline field
  access in the Neo register VM — VT locals/temps as flat, naturally-aligned
  bytes; `Ldfld_*_Inline`/`Stfld_*_Inline` pointer-arithmetic field access;
  memset-0 `Initobj` for in-frame VTs; JIT type-based lowering that selects
  the inline variant.

### Modified Capabilities
<!-- None. `neo-dispatch` (the only existing openspec/specs/ capability) is
     unaffected: Step 12 changes value-type storage/field access, not virtual
     or interface dispatch. -->

## Impact

- **`ILRuntime/Runtime/Intepreter/OpCodes/OpCodeREnum.cs`** — append the
  `_Inline` opcode variants (one Ldfld + one Stfld per primitive kind: I1, I2,
  I4, I8, U1, U2, U4, U8, R4, R8, Ref = 11 each side).
- **`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`** — add the
  `_Inline` `case` arms (~after the existing Stfld_Ref arm at ~1688); complete
  the `Initobj` ref-field sub-case (~1532-1535).
- **`ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`** — `Code.Ldfld`
  / `Code.Stfld` cases (~1853-1896): branch on operand-slot kind to choose
  `_Inline` vs heap variant; `GetLdfldCodeForType` / `GetStfldCodeForType`
  (~1962+) gain inline counterparts or the call sites pass a flag.
- **`ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs`** — register-
  kind propagation so the JIT/optimizer knows which slots are in-frame VTs;
  alignment in the slot-offset accumulation (`AllocateNeoCallParamSlot` and
  the `StackRegisterCount` max-size loop already exist; alignment padding is
  new).
- **`TestCases/NeoStep12Test.cs`** — new (mirror `NeoStep11Test.cs` convention:
  `public static` parameterless `NeoStep12Test*` methods).
- **No Legacy impact**: all new code is `#if ENABLE_NEO_MODE`. Legacy
  `ValueTypeObjectReference`/`AllocValueType` paths are untouched.
- **Regression surface (high)**: value types are pervasive; existing NeoStep
  smoke tests use VT locals/params/fields. A storage/alignment change can
  silently corrupt them — the full `NeoStep` smoke must stay green.
- **Non-goals (explicit)**: whole value-type copy/assignment (`Move_Vt` +
  JIT `LowerMove`) = **Step 12b**; CLR value-type Box/Unbox + CLR Initobj +
  `constrained.` callvirt = **Step 13/18**. Step 12 does NOT add struct-copy.
