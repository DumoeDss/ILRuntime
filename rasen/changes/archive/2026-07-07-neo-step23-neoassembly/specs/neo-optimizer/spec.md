## ADDED Requirements

### Requirement: NeoAssembly .neo format serializes OpCodeR[] as raw 24-byte little-endian records under a versioned header

The `.neo` serializer SHALL persist each `OpCodeR` (`ILRuntime/Runtime/
Intepreter/OpCodes/OpCode.cs`, `[StructLayout(LayoutKind.Explicit)]`, 24 bytes)
as its raw 24 bytes via `MemoryMarshal.AsBytes<OpCodeR>` on write and
`MemoryMarshal.Cast<byte, OpCodeR>` on read, little-endian. The serializer
SHALL NOT use field-by-field encoding for the body, because the explicit-layout
aliases (`Register1`/`DstOffset` @4, `Register2`/`SrcOffset` @6, `Register3`/
`OperandOffset`/`Register4`/`Operand`/`OperandFloat` @8-11, `Operand2`/
`OperandLong`/`OperandDouble` @12-19, `Operand3` @16, `Operand4` @20) mean a
raw dump captures every variant without per-opcode canonical-field selection,
and field-by-field would reintroduce the OpCodeR-union aliasing defects
documented under F-8 / OPT-HARDEN-K1. The file SHALL begin with a header
`{ Magic "ILRN" (0x494C524E), Version, Endianness, TableOffsets[] }`; the
deserializer SHALL reject a file whose Magic is not "ILRN" or whose Version
exceeds the reader's supported maximum. The raw encoding is version-fragile by
construction -- the header `Version` field is the sole guard, and a future
`OpCodeR` layout change (24 -> N bytes) MUST bump `Version`.

#### Scenario: Raw 24-byte OpCodeR roundtrips byte-for-byte
- **WHEN** a compiled method's `NeoExecuteBody` (`OpCodeR[]`) is serialized to a
  `.neo` stream and deserialized back
- **THEN** the deserialized `OpCodeR[]` MUST be byte-for-byte identical to the
  original (`MemoryMarshal.AsBytes` equality over all 24 bytes per record,
  length included)
- **AND** the equality MUST hold for opcode kinds that use the aliasing fields
  (an `Ldc_I8` carrying `OperandLong` @12-19, an `Ldc_R8` carrying
  `OperandDouble` @12-19, a `Bnei_Un_R8` carrying its 8-byte immediate, a
  `Move_Vt` carrying `Register1`/`Register2` byte offsets @4/@6, a `Callvirt`
  carrying `Operand4` packed flags @20)

#### Scenario: Wrong magic / version rejected
- **WHEN** a reader is given a stream whose first 4 bytes are not "ILRN" (e.g. a
  HybridPatch stream with Magic 0x58883551)
- **THEN** the reader MUST throw a clear "not a .neo file" error without reading
  further
- **AND WHEN** a reader is given a `.neo` stream whose `Version` exceeds the
  reader's supported maximum
- **THEN** the reader MUST throw a "newer .neo version" error

### Requirement: NeoAssembly reuses HybridPatch reference tables for type/method/field/string indices

The serializer SHALL reuse HybridPatch's `TypeReferencePatchInfo`,
`MethodReferencePatchInfo`, and `FieldReferencePatchInfo` record types and their
`WriteToStream`/`FromStream` implementations from `ILRuntime/HybridPatch/PatchInfo/
AssemblyInfo.cs` for the `.neo` StringTable, TypeRefTable, MethodRefTable, and
FieldRefTable (design doc section 8.1: "Neo extends HybridPatch"). The serializer
SHALL NOT reinvent these tables. Each `TypeReferencePatchInfo`-derived record in the `.neo` file SHALL
carry an IL-vs-CLR discriminator so the Step-25 loader can resolve IL types via
`AppDomain.LoadedTypes[fullName]` and CLR types via assembly-qualified name. The
token-bearing `OpCodeR` operands (type-token hash on `Box`/`Isinst`/`Castclass`/
`Initobj`/`Newarr`/`Constrained` in `Operand`; method-token hash on `Call`/
`Callvirt`/`Ldftn` in `Operand2`; string token on `Ldstr` in `OperandLong`) are
serialized verbatim as part of the raw `OpCodeR` record (they are runtime
hashes); the reference tables exist so a future Step-25 loader can rebuild the
`hash -> IType/IMethod/string` maps. Cross-AppDomain hash re-resolution is
explicitly DEFERRED to Step 25.

#### Scenario: Reference tables reuse HybridPatch record types
- **WHEN** the `.neo` writer builds the TypeRefTable / MethodRefTable /
  FieldRefTable
- **THEN** it SHALL route Cecil `TypeReference`/`MethodReference`/`FieldReference`
  objects through the SAME `TypeReferencePatchInfo.Create` /
  `MethodReferencePatchInfo.Create` / `FieldReferencePatchInfo.Create` factories
  HybridPatch uses
- **AND** the serialized records SHALL be readable by the corresponding
  `FromStream` methods (structural fidelity)

#### Scenario: Reference tables roundtrip structurally
- **WHEN** a `.neo` stream's reference tables are written and read back
- **THEN** the deserialized `TypeReferencePatchInfo[]` / `MethodReferencePatchInfo[]`
  / `FieldReferencePatchInfo[]` / `string[]` MUST equal the originals
  field-for-field (including arrays, byrefs, generic-instances, generic-parameter
  decomposition)

### Requirement: CompiledFrame serialization persists every load-bearing field for a runnable frame

The `.neo` MethodDefTable SHALL serialize, per IL method, the load-bearing
fields of `CompiledFrame` (`JITCompiler.cs:74-107`): `NeoExecuteBody` (`OpCodeR[]`,
the lowered body `ExecuteNeo` runs), `LocalInfos` and `ParamInfos`
(`StackSlotInfo[]` = `{Offset, RefOffset, Size, RefCount}`), `TotalStructSize`,
`TotalRefSize`, `ParamPrimitiveSize`, `ParamReferenceCount`,
`LocalsPrimitiveSize`, `LocalsReferenceCount`, `ReturnPrimitiveSize`,
`ReturnRefCount`, `StackRegisterCount`, `LocalIsReference[]`,
`NeoCatchException{RegIndex,ByteOffset,RefOffset}`, `SwitchTargets`
(`Dictionary<int,int[]>`), `NeoCallParamMap[]` (ushort arrays + `bool[]` flags
+ CLR `System.Type[]` element types serialized by assembly-qualified name), and a
structured `ExceptionHandler[]` table with try/handler ranges as BODY INDICES
plus a catch-type TypeRef index. The serializer SHALL NOT serialize `CodeBody`
(the register-index body; only consumed by the inliner/debugger, out of AOT
scope) NOR `Symbols` (keyed by Cecil `Instruction`, not serializable; consumed
only by the Step-22 extractor at serialize-time and the debugger). The EH table
SHALL re-represent the Cecil-keyed `addr[]` map as body-index ranges, because
`addr[]` is not serializable and Step 25's loader must rebuild the runtime EH
lookup from indices without Cecil.

#### Scenario: CompiledFrame roundtrips field-for-field
- **WHEN** a compiled method's `CompiledFrame` is serialized and deserialized
- **THEN** every load-bearing field listed above MUST equal the original
  (`StackSlotInfo[]` field-for-field on `{Offset, RefOffset, Size, RefCount}`;
  scalar sizes/counts equal; `LocalIsReference[]` equal; `NeoCatchException*`
  equal; `SwitchTargets` dictionary equal key-for-key and value-for-value;
  `NeoCallParamMap[]` equal including the `PrimitiveByRefElemType` CLR types
  resolved back from assembly-qualified names)
- **AND** `NeoExecuteBody` MUST be byte-for-byte identical (per the raw-OpCodeR
  requirement)

#### Scenario: Exception handler table roundtrips as body indices
- **WHEN** a method with a try/catch handler is serialized
- **THEN** the EH record SHALL carry `TryStartIdx`/`TryEndIdx`/
  `HandlerStartIdx`/`HandlerEndIdx`/`FilterIdx` as BODY INDICES (resolved via
  the JIT-time `addr[]` map at serialize time), `HandlerType` as the Cecil
  `ExceptionHandlerType` int, and `CatchTypeRefIdx` as a TypeRefTable index
  (-1 for finally/fault)
- **AND** the deserialized EH table MUST equal the original structurally
  (ranges, type, catch-type ref)

#### Scenario: Methods with diverse frame shapes roundtrip
- **WHEN** the roundtrip matrix covers a non-generic method, a generic method
  (with a template), a method with an exception handler, and methods with
  various locals/params/refs (primitive locals, IL value-type locals, CLR
  value-type locals, byref params, ref-returning methods)
- **THEN** every cell MUST roundtrip field-for-field (the matrix exercises the
  `StackSlotInfo` Size/RefCount variation, the `LocalIsReference` flag, the
  `NeoCallParamMap` byref paths, and the EH table)

### Requirement: GenericMethodTemplate serialization is faithful (every T-identity site, not just the Initobj prefix)

The `.neo` TemplateTable SHALL serialize each cached `GenericMethodTemplate`
(`GenericMethodTemplate.cs:83`) faithfully: `TemplateBody` (raw `OpCodeR[]`),
`Patches[]` (every `PatchEntry` the Step-22 extractor recorded, with the
`CecilToken` `object` re-resolved to a TypeRef/MethodRef table index by
`PatchKind`), the front-half scalars (`LocVarRegStart`, `TotalRegCnt`,
`NeoCatchExRegFinal`, `StackRegisterCount`, `VarCnt`), the `SwitchTargets`, the
`InitObjPrefixLength` + `InitObjPrefixRegisters[]` (the prefix, rebuilt from
these at instantiate time), `VariableTypes` (as TypeRef indices),
`ConstrainedTypeTokens` (as TypeRef indices), and `ConstrainedMethodTokens` (as
MethodRef indices). The serializer MUST capture EVERY T-identity site the
extractor records -- every IL-source `Initobj` (`Operand2==0`), every `Box`/
`Unbox`/`Unbox_Any`/`Isinst`/`Castclass`/`Newarr`/`Stobj`/`Ldobj` T-token, and
every `Constrained` T-type-token + its trailing T-qualified callvirt
method-token. The serializer MUST NOT assume the auto-`Initobj` prefix is the
only `Initobj` site (the Step-22 `CallIt<Struct>` follow-up: the inliner can
insert an `Initobj` for struct temps that the prefix-rebuild does not
reproduce; any such non-prefix `Initobj` T-token the extractor records as a
patch MUST be serialized as a patch). `Symbols`, `Addr`, `RefBody`, and
`RefBodyAddr` SHALL NOT be serialized (Cecil-keyed or runtime cache; the
ref-share is rebuilt by Step 25's loader via the same all-ref-and-no-token
discrimination).

#### Scenario: GenericMethodTemplate roundtrips structurally
- **WHEN** a cached template (built via the Step-22 capture path) is serialized
  and deserialized
- **THEN** `TemplateBody` MUST be byte-for-byte identical (raw OpCodeR)
- **AND** the `Patches[]` array MUST equal the original element-for-element on
  `{InstrIdx, Field, Kind, GenericParamIdx}` and each `TokenRefIdx` MUST resolve
  to the same TypeRef/MethodRef the original `CecilToken` routes to (by `Kind`)
- **AND** the front-half scalars, `SwitchTargets`, `InitObjPrefixRegisters`,
  `VariableTypeRefIdxs`, `ConstrainedTypeRefIdxs`, `ConstrainedMethodRefIdxs`
  MUST equal the originals

#### Scenario: Every T-identity patch site is captured
- **WHEN** a generic method body contains T-identity sites beyond the Initobj
  prefix (e.g. an IL-source `Initobj T` not in the prefix, a `Box T`, a
  `constrained.callvirt T.GetHashCode`, a `constrained.callvirt
  IComparable<T>::CompareTo` with a T-qualified method token)
- **THEN** the serialized `Patches[]` MUST contain a `PatchEntry` for EACH such
  site (the extractor's full output), with the correct `Field`/`Kind`/
  `GenericParamIdx`/`TokenRefIdx`
- **AND** the prefix-only `InitObjPrefixRegisters` MUST be disjoint from the
  IL-source `Initobj` patch sites (they are different mechanisms -- prefix vs
  patch)

### Requirement: NeoAssembly serialization is additive, Neo-only, and Legacy-neutral

The `.neo` serializer/deserializer SHALL be entirely additive new code under a
new `ILRuntime/Runtime/NeoAOT/` namespace, gated `#if ENABLE_NEO_MODE`. It
SHALL NOT modify the JIT compiler, `ExecuteNeo`, the optimizer, the Step-22
template mechanism, or any `ILType` field-layout logic; it only READS the
public/internal fields these already expose. Any new accessor needed on
`ILType`/`CompiledFrame`/`GenericMethodTemplate` for the serializer SHALL be
gated `#if ENABLE_NEO_MODE` (additive, no behavior change). Legacy `ExecuteR`
(`ILIntepreter.Register.cs`) SHALL be byte-identical to before this change: the
whole `NeoAOT/` namespace SHALL compile out under plain `Debug`, and a
stash-toggle plain-`Debug` + `useRegister=true` NeoStep-filter run MUST show the
SAME pre-existing Legacy failure set with and without the change. The full
NeoStep smoke (205/205) + NeoOptHardening (24/24) + NeoStep20 (9/9) MUST stay
green (the serializer does not change any existing JIT/runtime behavior).

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and runs the Legacy
  register VM
- **THEN** the Legacy NeoStep-filter smoke MUST show the SAME pre-existing
  failures with and without this change (stash-toggle proof), because the entire
  `NeoAOT/` namespace and any new accessors are gated `#if ENABLE_NEO_MODE` and
  compile out

#### Scenario: NeoStep regression smoke stays green
- **WHEN** the serializer is enabled (`Debug_Neo`) and the full NeoStep smoke is
  run
- **THEN** NeoStep MUST stay 205/205, NeoOptHardening 24/24, NeoStep20 9/9
  (ZERO regressions; the serializer is additive and reads existing structures)

#### Scenario: V1 roundtrip self-check passes for the full matrix
- **WHEN** the host-side V1 roundtrip self-check (DEBUG+Neo, mirroring
  `NeoStep22SelfCheck`) compiles each matrix method, serializes to an in-memory
  stream, deserializes, and compares
- **THEN** every matrix cell MUST pass: `OpCodeR[]` byte-for-byte, `CompiledFrame`
  field-for-field, type layout identical, `GenericMethodTemplate` faithful
- **AND** any cell that fails MUST be reported with a structural diff (per-index
  opcode / per-field mismatch), not a silent skip
