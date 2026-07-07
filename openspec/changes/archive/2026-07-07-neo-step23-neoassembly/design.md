## Context

Step 22 shipped the in-memory generic-method template mechanism
(`GenericMethodTemplate` + `CloneAndPatch`, `GenericMethodTemplate.cs`).
Step 23 is the next AOT layer: a standalone serialization format (`.neo`) that
persists a Neo-compiled assembly and reads it back. It is the substrate the
`ilrt_neoc` CLI (Step 24) writes and the runtime `.neo` loader (Step 25) reads.

The format is grounded in the ACTUAL in-memory structures (verified verbatim,
HEAD `21900a3a`, build green 0 errors):

- **`OpCodeR`** (`OpCode.cs`): `[StructLayout(LayoutKind.Explicit)]`, 24 bytes.
  Field offsets: `Code`@0 (OpCodeREnum, 4B), `Register1`/`DstOffset`@4 (short/
  ushort), `Register2`/`SrcOffset`@6 (short/ushort), `Register3`/
  `OperandOffset`@8 + `Register4`@10 + `Operand`@8 (int) + `OperandFloat`@8
  (float), `Operand2`@12 + `OperandLong`@12 (long) + `OperandDouble`@12
  (double), `Operand3`@16 (int), `Operand4`@20 (int). Natural size = 24.
- **`StackSlotInfo`** (`JITCompiler.cs:67`): `{Offset, RefOffset, Size,
  RefCount}` (4 ints = 16 bytes).
- **`CompiledFrame`** (`JITCompiler.cs:74-107`): the per-method frame metadata.
- **`GenericMethodTemplate`** (`GenericMethodTemplate.cs:83`): the Step-22
  template holder (15 fields).
- **`ILType` field layout** (`ILType.cs:18-88, 366-408, 700-718`):
  `ILTypeFieldOffset{PrimitiveOffset, ReferenceOffset}`, `TotalPrimitiveSize`,
  `TotalReferenceCount`, `NeoVTable` (`IMethod[]`), interface map
  (`InterfaceEntry{InterfaceType, VTableOffset, MethodSlotKeys[],
  ClassSlotRemap[]}`).
- **HybridPatch machinery to REUSE** (`HybridPatch/PatchInfo/AssemblyInfo.cs`):
  `AssemblyPatchInfo` (Magic `0x58883551`, Version, indexed tables) already
  index-izes type/method/field/string references via `TypeReferencePatchInfo`,
  `MethodReferencePatchInfo`, `FieldReferencePatchInfo`. The existing
  `MethodPatchInfo` even HAS a `CodeBodyRegister: OpCodeR[]` field -- it just
  never serializes it (only the CIL `CodeBody`). That is the natural seam: Neo
  extends HybridPatch by serializing the `CodeBodyRegister` + `CompiledFrame` +
  type metadata + templates.

## Goals / Non-Goals

**Goals:**
- A versioned `.neo` binary format (header + tables) that faithfully captures
  every field a deserialized method needs to be byte-for-byte equivalent to the
  in-memory original.
- A BinaryWriter serializer + BinaryReader deserializer, Neo-only.
- Reuse HybridPatch's reference-index machinery (do NOT reinvent the tables).
- A V1 roundtrip-equivalence test (host-side) as the load-bearing gate:
  serialize -> deserialize -> assert equal. Matrix: non-generic, generic (with
  template), EH, various locals/params/refs.

**Non-Goals:**
- The `ilrt_neoc` CLI (Step 24). Step 23's serializer is a LIBRARY the CLI calls.
- The runtime `.neo` LOADER + Cecil-decoupling (Step 25).
- Perf (Step 26).
- Cross-AppDomain token-hash stability (the OpCodeR body carries runtime token
  hashes as operands; same-process V1 roundtrip is exact; cross-AppDomain is
  Step 25's job -- the reference tables are serialized NOW so Step 25 can
  rebuild hash maps, but the re-resolution logic is deferred).
- Functional "deserialize -> ExecuteNeo" (V2). Step 23 ships V1 structural
  equivalence; V2 lands at Step 25.

## Decisions

### D1. `OpCodeR[]` serialization: RAW 24-byte little-endian records

**Choice:** serialize each `OpCodeR` as its raw 24 bytes via
`MemoryMarshal.AsBytes<OpCodeR>(body.AsSpan())` on write, and
`MemoryMarshal.Cast<byte, OpCodeR>(span)` on read. Little-endian (host-native on
x86/x64/arm64-le; documented in the header).

**Rationale:**
- The `[StructLayout(LayoutKind.Explicit)]` aliases mean a raw dump captures
  EVERY variant (`Register1`/`DstOffset`, `Operand`/`OperandFloat`/
  `Register3`/`Register4`, `Operand2`/`OperandLong`/`OperandDouble`) without
  per-opcode canonical-field selection. Field-by-field would have to pick the
  right alias per `OpCodeREnum` (hundreds of cases) -- slow, fragile, and a
  silent-bug source (the F-8 / OPT-HARDEN-K1 lessons were exactly OpCodeR-union
  aliasing mistakes).
- Fastest: one `MemoryMarshal.AsBytes` + one `BinaryWriter.Write(byte[])` per
  method body. No per-field dispatch.
- `OpCodeR` is unmanaged (no object refs); `MemoryMarshal.AsBytes` is sound.

**Versioning:** the header `Version` field guards layout changes. If `OpCodeR`
ever gains/loses a field (bumping 24 -> N bytes), `Version` bumps and old
readers reject the file with a clear error. The struct has been stable across
Steps 1-22.

**Alignment check:** explicit layout = exactly the offsets specified, no
padding. `Marshal.SizeOf<OpCodeR>()` = 24 (max offset+size = `Operand4`@20+4).
Confirmed by the field-offset table in `OpCode.cs`.

**Alternative considered (rejected):** field-by-field `(Code, Register1,
Register2, Register3, Operand, Operand2, Operand3, Operand4)` -- the 8 canonical
fields `BodiesEqual` compares. This is version-safer in principle but (a)
duplicates the aliasing bookkeeping the runtime already owns, (b) is an order of
magnitude slower, (c) the raw dump is ALSO byte-identical for these 8 fields
(they cover the full 24 bytes: `Register1`@4-5, `Register2`@6-7, `Register3`@8-9,
`Register4`@10-11 is within `Operand`@8-11, `Operand`@8-11, `Operand2`@12-15,
`Operand3`@16-19, `Operand4`@20-23, `Code`@0-3). Raw wins on every axis. If
version-safety later demands it, a Version bump can switch the body encoding
without changing the table structure.

### D2. Reference tables: REUSE HybridPatch's `*PatchInfo` types

**Choice:** the `.neo` reference tables ARE HybridPatch's tables, serialized
with their existing `WriteToStream`/`FromStream`:
- `StringTable` -> `string[]` (length-prefixed UTF-8 via `BinaryWriter.Write(string)`).
- `TypeRefTable` -> `TypeReferencePatchInfo[]` (IL type by `GetSafeFullNames`
  full name; CLR type by assembly-qualified name; arrays/byrefs/generic-instances
  decomposed structurally -- already handled).
- `MethodRefTable` -> `MethodReferencePatchInfo[]` (declaring-type ref + name +
  parameter-type refs + generic-instance decomposition).
- `FieldRefTable` -> `FieldReferencePatchInfo[]` (declaring-type ref + field-type
  ref + name + IsStatic).

**Rationale:** HybridPatch already solves "index + structurally resolve a
type/method/field reference" (design doc section 8.1: "Neo extends HybridPatch"). The
existing `TypeReferencePatchInfo.Create(TypeReference, internalRefs)` /
`MethodReferencePatchInfo.Create(MethodReference, internalRefs)` /
`FieldReferencePatchInfo.Create(...)` factories build the records from Cecil
objects; their `WriteToStream`/`FromStream` are battle-tested. Reinventing them
would duplicate ~400 lines for zero gain.

**IL vs CLR discrimination:** add a 1-byte `Kind` to each `TypeReferencePatchInfo`
record on the Neo side (`Il=0`, `Clr=1`) OR carry it via the existing `IsInternal`
flag (an IL type referenced cross-assembly is `IsInternal=false`; the loader
resolves IL types via `AppDomain.LoadedTypes[fullName]` and CLR types via
`AppDomain.GetType(aqname)`). Decision: add an explicit `Kind` byte in the
NeoAOT record wrapper (cleaner than overloading `IsInternal`; the wrapper lives
in `NeoAOT/`, the HybridPatch types are reused as-is inside it).

**Resolution (Step 25, deferred):** on load, the reader builds
`Dictionary<int hash, IType/IMethod>` maps the way the runtime's
`AppDomain.GetType(int)` / `GetMethod(int)` / `GetString(long)` expect, by
resolving each ref-table entry to its `IType`/`IMethod`/string and registering
it under the hash the OpCodeR operands carry. Step 23 builds the tables; Step 25
wires the resolution.

### D3. `CompiledFrame` serialization: load-bearing fields only

**Choice:** serialize exactly the fields a deserialized method needs to be
runnable + structurally equal to the original. Record layout
(`NeoMethodDefRecord`, one per IL method):

```
NeoMethodDefRecord {
  // Identity
  int    MethodRefIdx;          // -> MethodRefTable (declaring type + name + sig)
  // The lowered body ExecuteNeo runs
  int    BodyLength; byte[BodyLength*24]  NeoExecuteBody;   // raw OpCodeR[]
  // Frame layout (StackSlotInfo = 4 ints)
  int    LocalInfosCount;  StackSlotInfo[LocalInfosCount]  LocalInfos;
  int    ParamInfosCount;  StackSlotInfo[ParamInfosCount]  ParamInfos;
  int    TotalStructSize, TotalRefSize;
  int    ParamPrimitiveSize, ParamReferenceCount;
  int    LocalsPrimitiveSize, LocalsReferenceCount;
  int    ReturnPrimitiveSize, ReturnRefCount;
  int    StackRegisterCount;
  int    LocalIsRefCount; bool[LocalIsRefCount] LocalIsReference;
  int    NeoCatchExceptionRegIndex, NeoCatchExceptionByteOffset,
         NeoCatchExceptionRefOffset;
  // SwitchTargets: Dictionary<int,int[]>
  int    SwitchTargetCount; (int key, int arrLen, int[arrLen])[] SwitchTargets;
  // NeoCallParamMap[] (ushort arrays + bool[] flags + CLR System.Type[] by aqname)
  int    NeoCallParamCount; NeoCallParamMapRecord[] NeoCallParams;
  // Exception handlers as BODY INDICES (the Cecil-keyed addr[] is not serializable)
  int    EHCount; NeoExceptionHandlerRecord[] ExceptionHandlers;
}
NeoCallParamMapRecord {
  ushort[] PrimitiveSrc, PrimitiveDst, PrimitiveSize;  // (len, vals)
  ushort[] RefSrc, RefDst;
  bool[]  PrimitiveByRefSrc, PrimitiveByRefWriteBack;
  int     ElemTypeCount; string[] PrimitiveByRefElemTypeAqName;  // System.Type -> aqname
}
NeoExceptionHandlerRecord {
  int    TryStartIdx, TryEndIdx;       // body indices (resolved via addr[] at serialize)
  int    HandlerStartIdx, HandlerEndIdx;
  int    FilterIdx;                    // -1 if none
  int    HandlerType;                  // Mono.Cecil.Cil.ExceptionHandlerType (int)
  int    CatchTypeRefIdx;              // -> TypeRefTable (-1 for finally/fault)
}
```

**NOT serialized** (with rationale):
- `CodeBody` (register-index body) -- derivable from `NeoExecuteBody` is NOT
  possible (lowering is one-way), BUT `CodeBody` is only consumed by the
  inliner and the debugger. The AOT loader (Step 25) does not inline and the
  debugger path is a separate deferred item. If a future step needs `CodeBody`
  in AOT, serialize it as a second raw `OpCodeR[]`; for Step 23 it is out of
  scope. (Step 23 V1 roundtrip asserts `NeoExecuteBody`, not `CodeBody`.)
- `Symbols` (`Dictionary<int, RegisterVMSymbol>` where `RegisterVMSymbol` holds
  a Cecil `Instruction`) -- Cecil-keyed, not serializable. Used only by the
  Step-22 patch extractor (which runs at serialize time, producing the
  `PatchEntry[]` that IS serialized) and the debugger (deferred). Skip.
- `SwitchTargets` keys are stable Cecil array hashes -- serialized as-is (they
  are int keys; the runtime reads them verbatim).

**EH representation:** the Neo runtime resolves EH via the Cecil-keyed `addr[]`
map at JIT time (`JITCompiler.cs:595`). `addr` is `Dictionary<Instruction,int>`
-- not serializable. The `.neo` format re-represents the EH table as
`NeoExceptionHandlerRecord[]` with try/handler ranges as BODY INDICES (resolved
via `addr` at serialize time) + catch type as a TypeRef. Step 25's loader
rebuilds the runtime EH lookup directly from these indices (no Cecil). For Step
23 V1, the roundtrip asserts the structured EH table equals.

### D4. `ILType` metadata serialization (TypeDefTable)

**Choice:** one `NeoTypeDefRecord` per IL type:
```
NeoTypeDefRecord {
  int    TypeRefIdx;           // -> TypeRefTable (the type's own full name)
  int    BaseTypeRefIdx;       // -1 if none
  int    TotalPrimitiveSize, TotalReferenceCount;
  int    StaticTotalPrimitiveSize, StaticTotalReferenceCount;
  int    FieldCount; NeoFieldLayoutRecord[] Fields;   // instance fields
  // NeoVTable (IMethod[] slot -> method-ref)
  int    VTableSlotCount; int[VTableSlotCount] VTableMethodRefIdxs;
  // Interface map
  int    InterfaceCount; NeoInterfaceEntryRecord[] Interfaces;
  // Initializer (static ctor) MethodRefIdx, -1 if none
  int    StaticCtorMethodRefIdx;
}
NeoFieldLayoutRecord {
  int    FieldRefIdx;          // -> FieldRefTable (name + type + IsStatic)
  int    PrimitiveOffset, ReferenceOffset;   // ILTypeFieldOffset
}
NeoInterfaceEntryRecord {
  int    InterfaceTypeRefIdx;
  int    VTableOffset;
  int    MethodSlotKeyCount; string[MethodSlotKeyCount] MethodSlotKeys;
  int    ClassSlotRemapCount; int[ClassSlotRemapCount] ClassSlotRemap;  // -1 sentinel = empty
}
```
The computed results (`TotalPrimitiveSize`, per-field `ILTypeFieldOffset`) are
persisted so Step 25 does not recompute them. Field TYPE references live in the
FieldRefTable (reused from HybridPatch).

### D5. `GenericMethodTemplate` serialization: FAITHFUL (every T-identity site)

**Choice:** one `NeoTemplateRecord` per cached template:
```
NeoTemplateRecord {
  int    DefinitionMethodRefIdx;        // the open generic method def
  int    TemplateBodyLength; byte[TemplateBodyLength*24] TemplateBody;  // raw OpCodeR[]
  int    PatchCount; NeoPatchEntryRecord[] Patches;
  short  LocVarRegStart; int TotalRegCnt; short NeoCatchExRegFinal;
  int    StackRegisterCount; int VarCnt;
  int    InitObjPrefixLength; int[InitObjPrefixLength] InitObjPrefixRegisters;
  int    VariableTypeRefCount;    int[VariableTypeRefCount] VariableTypeRefIdxs;
  int    ConstrainedTypeRefCount; int[ConstrainedTypeRefCount] ConstrainedTypeRefIdxs;
  int    ConstrainedMethodRefCount; int[ConstrainedMethodRefCount] ConstrainedMethodRefIdxs;
  int    SwitchTargetCount; (int key, int arrLen, int[arrLen])[] SwitchTargets;
}
NeoPatchEntryRecord {
  int    InstrIdx;
  int    Field;     // PatchField enum (Operand=0/Operand2=1/Operand4=2)
  int    Kind;      // PatchKind enum (TypeToken=0/MethodToken=1/IsRefMoveFlag=2)
  int    GenericParamIdx;
  int    TokenRefIdx;   // TypeRefTable (Kind=TypeToken) or MethodRefTable (Kind=MethodToken); -1 for IsRefMoveFlag
}
```

**Faithfulness (the Step-22 follow-up):** the serializer captures EVERY
`PatchEntry` the extractor recorded -- every IL-source `Initobj` (`Operand2==0`),
every `Box`/`Unbox`/`Unbox_Any`/`Isinst`/`Castclass`/`Newarr`/`Stobj`/`Ldobj`
T-token, every `Constrained` T-type-token + its trailing T-qualified callvirt
method-token. The auto-`Initobj` prefix is captured SEPARATELY via
`InitObjPrefixRegisters` (the prefix is rebuilt from these at instantiate time;
the `CecilToken` for a prefix op is NOT a patch site). This split mirrors
`ExtractPatches` exactly, so the Step-22 `CallIt<Struct>` follow-up (inliner
inserts an `Initobj` for struct temps the prefix-rebuild does not reproduce) is
handled: any non-prefix `Initobj` T-token the extractor records as a patch IS
serialized as a patch, and the loader's CloneAndPatch re-resolves it. The
serializer MUST NOT assume the prefix is the only `Initobj` site.

**CecilToken re-resolution:** the in-memory `PatchEntry.CecilToken` is `object`
(a Cecil `TypeReference`/`MethodReference`). At serialize time, the writer
routes it through the SAME `TypeReferencePatchInfo.Create` /
`MethodReferencePatchInfo.Create` factories (D2) to get a table index; the
`Kind` discriminator tells the reader which table. `VariableTypes`,
`ConstrainedTypeTokens`, `ConstrainedMethodTokens` are likewise converted to
ref-index arrays.

**NOT serialized:** `Symbols`/`Addr` (Cecil-keyed), `RefBody`/`RefBodyAddr`
(runtime cache; the ref-share is rebuilt by Step 25's loader via the same
all-ref-and-no-token discrimination).

### D6. Header + table-of-offsets layout

```
NeoHeader {
  byte[4]  Magic = "ILRN" (0x49 0x4C 0x52 0x4E);
  short    Version = 1;
  byte     Endianness = 1;   // 1 = little-endian
  byte     Reserved = 0;
  int      StringTableOffset,  TypeRefTableOffset,  MethodRefTableOffset,
           FieldRefTableOffset, TypeDefTableOffset,  MethodDefTableOffset,
           TemplateTableOffset;
}
```
Tables follow in order; each table is `int Count` + `Count` records. The
offset array lets a reader seek directly to a table (random access for lazy
loading in Step 25). Magic `0x494C524E` is distinct from HybridPatch's
`0x58883551` (a `.neo` file is never a HybridPatch).

### D7. Gating: Neo-only, Legacy-neutral, additive

The whole `NeoAOT/` namespace is `#if ENABLE_NEO_MODE`. It compiles out of
plain `Debug`, so Legacy `ExecuteR` is byte-identical. The serializer only
READS existing public/internal fields; if it needs a new internal accessor on
`ILType`/`CompiledFrame`, that accessor is `#if ENABLE_NEO_MODE` too. No
JIT/runtime/optimizer/Step-22 code path is changed.

## Risks / Trade-offs

- **[Raw OpCodeR is version-fragile]** -> header `Version` guards it; a future
  layout change bumps Version and old readers reject. The struct has been
  stable across Steps 1-22. Acceptable.
- **[Cecil-keyed `addr[]`/`Symbols` not serializable]** -> the EH table is
  re-represented as body-index records (D3); `Symbols` is skipped (only the
  debugger + the Step-22 extractor use it, and both are serialize-time / out of
  scope). Step 25's loader rebuilds EH lookup from indices. Recorded as a
  deferred item so it is not lost.
- **[Cross-AppDomain token-hash stability]** -> the OpCodeR body carries runtime
  token hashes; same-process V1 roundtrip is exact, but a `.neo` written by one
  AppDomain and loaded by another needs the loader to re-resolve hashes via the
  reference tables. Step 23 builds the tables; Step 25 wires the resolution.
  Recorded as a deferred item.
- **[`NeoCallParamMap.PrimitiveByRefElemType` is `System.Type[]`]** -> serialize
  each as assembly-qualified name; reader re-resolves via
  `Type.GetType(aqname)` / the AppDomain. CLR-internal types (e.g. byref element
  types) are name-stable.
- **[V1 roundtrip does not exercise ExecuteNeo]** -> V1 is STRUCTURAL
  equivalence only; V2 functional (deserialize -> run) is Step 25. The
  regression smoke (NeoStep 205/205) gates "the additive serializer did not
  break the build / a shared type"; the V1 self-check gates "the format is
  faithful". Both are required to ship.
- **[Reference-table duplication with HybridPatch]** -> the `.neo` file carries
  its OWN reference tables (a `.neo` is standalone, not grafted onto a
  `.patch`). This duplicates the table-build code paths but reuses the record
  types + factories. Acceptable (a `.neo` and a `.patch` are different
  artifacts for different tools).

## Migration Plan

N/A -- additive, no existing artifact changes. Rollback = delete the
`NeoAOT/` namespace + the test files; no shared code depends on them.

## Open Questions

- **Q1: Does Step 23 need to serialize `CodeBody` (register-index) too?**
  Default: NO (only `NeoExecuteBody`; `CodeBody` is for inlining/debugger, out
  of AOT scope). If Step 25's loader or a future debugger needs it, add a
  second raw `OpCodeR[]` under a Version bump. Tracked as a deferred item.
- **Q2: Should the TypeDefTable VTable serialize the slot-key strings too?**
  The NeoVTable is `IMethod[]`; the slot-key map (`neoVTableSlots`) is a runtime
  cache for `TryGetNeoVTableSlot`. Step 25 can rebuild it from the method
  signatures. Default: serialize only the `IMethod[]` slot -> method-ref array;
  let the loader rebuild the reverse map. Confirm at apply.
- **Q3: Static-instance initial values.** HybridPatch's `FieldPatchInfo` carries
  `InitialValues` (byte[]) for static field initializers. The Neo TypeDefTable
  should mirror this for the static-instance seed. Confirm the ILType static
  layout + initial values are accessible at serialize time (they are, via
  `StaticInstance` + `staticFieldOffsets`).
