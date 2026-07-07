## Why

The Neo AOT toolchain (Steps 22-26) needs a standalone serialization layer that
persists a Neo-compiled assembly to a `.neo` file and reads it back. Step 22
shipped the in-memory generic-method template mechanism (`GenericMethodTemplate`
+ `CloneAndPatch`); Step 23 is the FORMAT + a BinaryWriter serializer + a
BinaryReader deserializer + a V1 roundtrip-equivalence test. It is the substrate
the `ilrt_neoc` CLI (Step 24) writes and the runtime `.neo` loader (Step 25)
reads. It is pure infrastructure -- the serializer only READS existing in-memory
structures (`OpCodeR[]`, `CompiledFrame`, `ILType` field layout,
`GenericMethodTemplate`); there is NO JIT / runtime / Step-22 behavior change.

## What Changes

- **Define a versioned `.neo` binary format** (Header `{Magic "ILRN", Version,
  TableOffsets[]}` + indexed tables). Format grounded in the ACTUAL structures
  (dump-locked below), not the section 8.2 draft alone.
- **REUSE HybridPatch's reference machinery** (`ILRuntime/HybridPatch/PatchInfo/
  AssemblyInfo.cs`: `TypeReferencePatchInfo`, `MethodReferencePatchInfo`,
  `FieldReferencePatchInfo` + a `StringTable`). Neo extends, does NOT reinvent,
  the type/method/field/string index tables (design doc section 8.1).
- **Serialize `OpCodeR[]` as raw 24-byte little-endian records** via
  `MemoryMarshal.AsBytes<OpCodeR>` / `MemoryMarshal.Cast<byte, OpCodeR>`.
  Justification: `OpCodeR` is `[StructLayout(LayoutKind.Explicit)]` (offsets
  0/4/6/8/10/12/16/20; see `OpCode.cs`) with aliasing fields
  (`Register1`/`DstOffset` @4, `Register2`/`SrcOffset` @6, `Register3`/
  `Operand`/`Register4`/`OperandFloat` @8-11, `Operand2`/`OperandLong`/
  `OperandDouble` @12-19, `Operand3` @16, `Operand4` @20). A raw dump captures
  ALL aliases without per-opcode field selection; field-by-field would have to
  pick a canonical alias per opcode (slow + fragile). Layout changes are guarded
  by header `Version` (the struct has been stable across Steps 1-22).
- **Serialize `CompiledFrame`** (the per-method frame metadata, `JITCompiler.cs`
  lines 74-107): load-bearing fields only -- `NeoExecuteBody` (`OpCodeR[]`,
  the lowered body `ExecuteNeo` runs), `LocalInfos` + `ParamInfos`
  (`StackSlotInfo[]` = `{Offset, RefOffset, Size, RefCount}`, 4 ints each),
  `TotalStructSize`, `TotalRefSize`, `ParamPrimitiveSize`,
  `ParamReferenceCount`, `LocalsPrimitiveSize`, `LocalsReferenceCount`,
  `ReturnPrimitiveSize`, `ReturnRefCount`, `StackRegisterCount`,
  `LocalIsReference[]`, `NeoCatchException{RegIndex,ByteOffset,RefOffset}`,
  `SwitchTargets` (`Dictionary<int,int[]>`), `NeoCallParamMap[]` (ushort arrays
  + `bool[]` flags + CLR `System.Type[]` element types by assembly-qualified
  name), and a structured `ExceptionHandler[]` table (try/handler ranges as
  BODY INDICES + catch-type ref -- the Cecil-keyed `addr[]` map is not
  serializable, so the EH table is re-represented as indices).
  NOT serialized: `CodeBody` (register-index; derivable; only needed for
  inlining/debugger), `Symbols` (`Dictionary<int, RegisterVMSymbol>` keyed by
  Cecil `Instruction` -- not serializable; used only by the Step-22 patch
  extractor and the debugger, neither of which the AOT loader exercises).
- **Serialize `ILType` type metadata**: `TotalPrimitiveSize`,
  `TotalReferenceCount`, per-field `ILTypeFieldOffset{PrimitiveOffset,
  ReferenceOffset}[]` (the computed field layout from `InitializeFields`), the
  `NeoVTable` template (`IMethod[]` slot -> method-ref), and the interface map
  (`InterfaceEntry{InterfaceType-ref, VTableOffset, MethodSlotKeys[],
  ClassSlotRemap[]}[]`). Static-field layout is captured for the static
  initializer table. (Field TYPE references live in the FieldRefTable.)
- **Serialize `GenericMethodTemplate`** (`GenericMethodTemplate.cs`) FAITHFULLY
  (the Step-22 follow-up): `TemplateBody` (`OpCodeR[]` raw), `Patches[]`
  (`PatchEntry{InstrIdx, Field, Kind, GenericParamIdx, CecilToken}` -- the
  `CecilToken` `object` is re-resolved to a TypeRef/MethodRef table index at
  serialize time, by `Kind`), the front-half scalars (`LocVarRegStart`,
  `TotalRegCnt`, `NeoCatchExRegFinal`, `StackRegisterCount`, `VarCnt`), the
  `SwitchTargets`, `InitObjPrefixLength` + `InitObjPrefixRegisters[]`,
  `VariableTypes` (`TypeReference[]` -> TypeRefIdx[]),
  `ConstrainedTypeTokens` (`TypeReference[]` -> TypeRefIdx[]),
  `ConstrainedMethodTokens` (`MethodReference[]` -> MethodRefIdx[]). The
  serializer MUST capture EVERY T-identity site the extractor records (every
  IL-source `Initobj` with `Operand2==0`, every `Box`/`Isinst`/`Castclass`/
  `Constrained` T-token, every T-qualified `Callvirt` method-token) -- NOT only
  the auto-`Initobj` prefix (the Step-22 `CallIt<Struct>` follow-up: the
  inliner can insert `Initobj` for struct temps the prefix-rebuild does not
  reproduce). NOT serialized: `Symbols`/`Addr` (Cecil-keyed), `RefBody`/
  `RefBodyAddr` (runtime cache, rebuilt on load).
- **Add a BinaryWriter serializer** (`NeoAssemblyWriter`) and a BinaryReader
  deserializer (`NeoAssemblyReader`) under a new `ILRuntime/Runtime/NeoAOT/`
  namespace, Neo-only (`#if ENABLE_NEO_MODE`).
- **Add a V1 roundtrip-equivalence test** (host-side, `DEBUG`+Neo, mirroring
  `NeoStep22SelfCheck`): compile a test assembly's methods (Neo) -> serialize to
  an in-memory `MemoryStream` -> deserialize -> assert deserialized ==
  originals (`OpCodeR[]` byte-for-byte; `StackSlotInfo[]` field-for-field; type
  layout identical; `GenericMethodTemplate` faithful). Matrix: non-generic
  method, generic method (with template), method with EH, method with various
  locals/params/refs.

Non-goals (deferred): the `ilrt_neoc` standalone CLI (Step 24); the runtime
`.neo` LOADER + Cecil-decoupling (Step 25); perf benchmarks (Step 26); the
cross-AppDomain token-hash-stability question (the OpCodeR body carries
runtime token HASHES as operands; same-process V1 roundtrip is exact, but a
`.neo` produced by one AppDomain and loaded by another needs the loader to
re-resolve hashes -- Step 25's job, recorded as a deferred item). Step 23 ships
the FORMAT + serializer/deserializer + V1 roundtrip, NOT the CLI or the loader.

## Capabilities

### New Capabilities

(None -- the `.neo` format is a serialization layer over structures whose
correctness invariants live under existing capabilities. The roundtrip-
equivalence requirement is recorded under `neo-optimizer` alongside the Step-22
template requirements, because the template is one of the structures Step 23
serializes and the Step-22 serializer-faithfulness follow-up is the same
correctness axis.)

### Modified Capabilities

- `neo-optimizer`: add Requirements for the `.neo` binary format + roundtrip
  fidelity -- (a) the OpCodeR raw-24-byte serialization + header versioning
  invariant; (b) the CompiledFrame load-bearing-fields serialization invariant
  (what MUST persist so a deserialized method's frame is runnable); (c) the
  GenericMethodTemplate faithful-serialization invariant (every T-identity site,
  the Step-22 follow-up); (d) the reference-table reuse-HybridPatch invariant;
  (e) the additive + Neo-only + Legacy-neutral gating invariant.

## Impact

- **New namespace `ILRuntime/Runtime/NeoAOT/`** (Neo-only, `#if ENABLE_NEO_MODE`):
  - `NeoAssembly.cs` -- the format model (the table record structs:
    `NeoMethodDefRecord`, `NeoTypeDefRecord`, `NeoTemplateRecord`,
    `NeoHeader`).
  - `NeoAssemblyWriter.cs` -- the BinaryWriter serializer (reads in-memory
    `CompiledFrame` / `ILType` / `GenericMethodTemplate`, writes a `.neo`
    stream). Reuses `TypeReferencePatchInfo`/`MethodReferencePatchInfo`/
    `FieldReferencePatchInfo` from `HybridPatch/PatchInfo/AssemblyInfo.cs`.
  - `NeoAssemblyReader.cs` -- the BinaryReader deserializer (reads a `.neo`
    stream, rebuilds in-memory structures).
- **`TestCases/NeoStep23NeoAssemblyTest.cs`** (new) -- the matrix of methods
  (non-generic, generic w/ template, EH, various locals/params/refs) used as
  serialization inputs (the V1 roundtrip runs host-side).
- **`ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep23RoundtripCheck.cs`** (new,
  `#if ENABLE_NEO_MODE && DEBUG`, host-side) -- mirrors `NeoStep22SelfCheck`:
  compiles each matrix method, serialize -> deserialize -> comparator. Invoked
  via the CLI `NeoStep23Roundtrip` filter (small `Program.cs` hook).
- **No changes to `JITCompiler.cs`, `ILIntepreter.Neo.cs`, `Optimizer.Neo.cs`,
  `GenericMethodTemplate.cs`, or `ILType.cs`.** The serializer only READS the
  public/internal fields these already expose (Neo frame fields are already
  `public` on the `CompiledFrame` struct; `GenericMethodTemplate` fields are
  already `public`; `ILType.NeoVTable` / `TotalPrimitiveSize` /
  `TotalReferenceCount` / field-offset accessors are already `public`/`internal`).
  If any field needs exposing for the serializer, it is an additive internal
  accessor, not a behavior change.
- **No breaking changes.** Legacy `ExecuteR` is untouched (the whole NeoAOT
  namespace compiles out under plain `Debug`). Regression risk is LOW: the
  change is additive new code reading existing structures; the only shared-code
  risk is a new internal accessor on `ILType`/`CompiledFrame`, gated
  `#if ENABLE_NEO_MODE`. Gate: NeoStep smoke 205/205 + NeoOptHardening 24/24 +
  NeoStep20 9/9 (unchanged) + the V1 roundtrip self-check (new, all-PASS).
