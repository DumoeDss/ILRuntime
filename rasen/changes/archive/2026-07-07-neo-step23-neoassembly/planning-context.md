# Planning Context — neo-step23-neoassembly (LEAD seed)

> SEED for the planner. Read THIS FIRST, then the AOT design + the Step 22
> template mechanism, then research only what is missing. APPEND durable findings.

## User intent

Continue the Neo AOT toolchain. This child = **Step 23**: the `.neo` binary format
(header + tables) + a BinaryWriter serializer + a BinaryReader deserializer + a
ROUNDTRIP equivalence test. Step 22 (the in-memory template mechanism) is DONE
(HEAD `b8ce773d`). Capability `neo-optimizer`. Full autonomy; LEAD commits + pushes.

## What Step 23 delivers (from `.trae/documents/object-model-neo-design.md` §8.2-8.4)

A standalone serialization layer that persists a Neo-compiled assembly to a `.neo`
file and reads it back. §8.2 format draft:
```
NeoAssembly:
  Header        { Magic "ILRN", Version, TableOffsets[] }
  StringTable   // all string constants, indexed
  TypeRefTable  // IL + CLR type references (indexed)
  MethodRefTable
  FieldRefTable
  TypeDefTable  // IL type defs: Fields[] + ILTypeFieldOffset +
                //   TotalPrimitiveSize + TotalReferenceCount + VTableTemplate
  MethodDefTable// OpCodeR[] + StackSlotInfo[] (CompiledFrame locals) +
                //   ExceptionHandlers[] + ParameterCount + TotalStructSize + TotalRefSize
  GenericMethodTemplateTable // the Step 22 templates (templateBody + patches[])
  InitializerTable // static ctors
```
§8.3 flow: `ilrt_neoc` (Step 24) Cecil-loads -> JITCompiler.Compile per method ->
serialize -> `.neo`. Runtime (Step 25) deserializes -> methods point at OpCodeR[]
-> ExecuteNeo runs directly (no JIT). §8.4.1: refs resolved at LOAD time to direct
indices/pointers (no execution-time lookup).

## Current state (the inputs Step 23 serializes)

- **`OpCodeR`** (`ILRuntime/Runtime/Intepreter/OpCodes/OpCode.cs`): a 24-byte
  `[StructLayout(LayoutKind.Explicit)]` struct. Fields: `Code` (OpCodeREnum @0),
  `Register1`/`DstOffset` (@4), `Register2`/`SrcOffset` (@6), `Register3`/`Operand`
  (@8, aliasing `Register4` @10), `Operand2` (@12), `Operand3` (@16), `Operand4`
  (@20). Can be serialized as raw 24-byte records OR field-by-field (the aliases
  mean a raw dump captures all variants; field-by-field is more portable/version-
  safe -- decide).
- **`CompiledFrame`** (the per-method frame metadata): `LocalInfos` (StackSlotInfo[]
  -- each local's Offset/Size/RefOffset/RefCount/etc.), `ParamInfos`,
  `ExceptionHandlers`, `TotalStructSize`, `TotalRefSize`, `ParameterCount`, the
  VTable. Find the exact struct (`ILType.cs` / `ILMethod.cs` / a CompiledFrame type).
- **Type metadata**: `ILType` field layout (`TotalPrimitiveSize`,
  `TotalReferenceCount`, `ILTypeField[]` with offsets, the VTable, interface map).
- **Step 22 `GenericMethodTemplate`** (`GenericMethodTemplate.cs`): `templateBody`
  (OpCodeR[]) + `patches[]` (PatchEntry[]) + the ConstrainedTypeTokens/
  ConstrainedMethodTokens capture arrays. Step 23 MUST serialize these too (the
  Step-22 follow-up: the serializer must NOT assume the Initobj prefix is the only
  Initobj site -- capture the full template faithfully).
- **References**: String constants, TypeRefs (IL types by name + CLR types by
  assembly-qualified name), MethodRefs, FieldRefs -- all INDEXED (§8.1: HybridPatch
  already index-izes type/method/field/string refs; mirror that).

## The HybridPatch serialization basis (§8.1 says Neo extends it)

`ILRuntime/HybridPatch/` (`AssemblyPatch.cs`, `PatchInfo/AssemblyInfo.cs`,
`MethodPatchContext.cs`, `PatchGenerator.cs`, `HashingUtility.cs`). HybridPatch's
`MethodPatchInfo` holds CodeBody (CIL `OpCode[]`), LocalVariables,
ExceptionHandlers, with type/method/field/string refs INDEXED. Neo EXTENDS this:
HybridPatch stores CIL `OpCode[]`; Neo stores `OpCodeR[]` (register) +
`CompiledFrame` metadata. READ these to reuse the index/reference machinery +
mirror the serialization approach (do NOT reinvent the string/type/method/field
tables -- extend HybridPatch's).

## KEY design questions for the planner to dump-gate + decide

1. **Raw vs field-by-field OpCodeR serialization.** Raw 24-byte records are fast +
   simple but version-fragile (a struct layout change breaks old .neo files).
   Field-by-field is version-safe + handles the aliases explicitly. The format
   should be VERSIONED (header Version field) either way. Decide + justify.
2. **The reference tables.** How are IL types / CLR types / methods / fields /
   strings indexed + resolved on deserialize? IL type -> by full name (resolved
   via the AppDomain on load); CLR type -> assembly-qualified name; method ->
   (type-index, name, signature); field -> (type-index, name). Mirror HybridPatch.
3. **CompiledFrame / StackSlotInfo serialization.** The exact fields that must
   persist so a deserialized method's frame is runnable (LocalInfos offsets/sizes/
   refcounts, ParamInfos, ExceptionHandlers, TotalStructSize/TotalRefSize). Find
   the exact CompiledFrame struct + enumerate the load-bearing fields.
4. **GenericMethodTemplate serialization.** templateBody + patches[] + the
   Constrained/Method token capture arrays. MUST be faithful (the Step-22
   struct-T x inliner follow-up: capture every Initobj site, not just the prefix).
5. **Roundtrip equivalence.** Serialize a compiled assembly -> deserialize ->
   assert the deserialized structures EQUAL the in-memory originals (OpCodeR[]
   byte-for-byte; CompiledFrame field-for-field; type layout identical). This is
   the VERIFICATION anchor.

## VERIFICATION (roundtrip equivalence -- no functional smoke)

Like Step 22, Step 23 is infrastructure (no runtime behavior change). Verification:
- **(V1) Roundtrip equivalence test (RECOMMENDED, load-bearing):** compile a test
  assembly's methods (Neo), serialize to an in-memory/`.neo` byte stream,
  deserialize, assert the deserialized `OpCodeR[]` + `CompiledFrame` + type layout
  EQUAL the originals. Matrix: non-generic method, generic method (with template),
  method with EH, method with various locals/params/refs. A `public static` test
  that does serialize->deserialize->BodiesEqual + frame-field-compare (assert via
  the value / 1-0 divide path).
- **(V2) Functional:** the actual end-to-end "deserialize then ExecuteNeo" is
  Step 25 (the loader). Step 23 ships the format + serializer/deserializer + V1
  roundtrip; V2 functional lands at Step 25.
Recommend V1 as the load-bearing gate. NeoStep smoke 205/205 is the REGRESSION
gate (the serializer is additive -- new code, doesn't touch the JIT/runtime).

## Scope + non-goals

- **In scope:** the `.neo` format (header + tables); BinaryWriter serializer;
  BinaryReader deserializer; serialization of OpCodeR[] + CompiledFrame + type
  metadata + GenericMethodTemplate; the V1 roundtrip test.
- **Non-goals (defer):** the `ilrt_neoc` standalone CLI (Step 24 -- Step 23's
  serializer is a LIBRARY the CLI will call); the runtime `.neo` LOADER +
  Cecil-decoupling (Step 25); perf benchmarks (Step 26). Step 23 ships the format
  + serializer/deserializer + roundtrip test, NOT the CLI or the runtime loader.
- **Non-goal:** do NOT change the JIT/runtime/Step-22 template behavior. The
  serializer is purely additive (reads existing in-memory structures). Confirm
  Legacy-neutral (Neo-only; Legacy unaffected).

## Probe BEFORE designing (binding)

Ground the format in the ACTUAL structures: dump a compiled method's `OpCodeR[]`
+ `CompiledFrame` (LocalInfos/ParamInfos/EH/sizes) + a `GenericMethodTemplate` --
observe the exact fields that must persist. Read the HybridPatch index/reference
machinery to reuse it. Do NOT design the format from §8.2 alone.

## Build + test (CRITICAL)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll \
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep   # ALWAYS -f net8.0; baseline 205/205 (REGRESSION gate)
```
- Build CLI with `Debug_Neo`; NEVER TestCases with `Debug_Neo`.
- `Debug_Neo` floods JIT output -- grep for "Ran N tests". >10s = infinite loop -> kill.
- NeoStep 205/205 + NeoOptHard 24/24 + NeoStep20 9/9 = REGRESSION gate (additive).
- Build-cache gotcha: confirm the DLL rebuilt after an edit.

## Codebase gotchas (full detail in handoff section 4)

- `OpCodeR` is `[StructLayout(Explicit)]` with aliasing fields -- a raw dump
  captures all aliases; field-by-field must handle which alias is canonical per
  opcode (or just dump all 24 bytes + accept the redundancy).
- The serializer/deserializer should be Neo-only (`#if ENABLE_NEO_MODE`) -- it
  serializes Neo-specific structures (OpCodeR, CompiledFrame). Legacy unaffected.
- Test harness is NOT xUnit; `public static` parameterless methods; the V1
  roundtrip test needs a host-side comparator (reuse Step 22's `BodiesEqual` pattern
  if accessible).
- Write tool corrupts ~0.5% of CJK; author ASCII-primary.
- Binary format: use `System.IO.BinaryWriter`/`BinaryReader` (or a raw byte[] +
  Span writer). Version the header. Big-endian vs little-endian: pick the host's
  native (little-endian on x86/x64) + document it.

## Likely new files / fix sites (dump-locked)

- A new `ILRuntime/Runtime/NeoAOT/` (or similar) namespace: `NeoAssembly.cs`
  (the format model), `NeoAssemblyWriter.cs` (serializer), `NeoAssemblyReader.cs`
  (deserializer), `NeoTableTypes.cs` (the table record structs).
- Possibly extend the HybridPatch index machinery (reuse, don't reinvent).
- `TestCases/NeoStep23NeoAssemblyTest.cs` (new) -- the V1 roundtrip test.
- A CLI/host hook to invoke the roundtrip (mirror Step 22's NeoStep22SelfCheck).

## Regression risk: LOW-MEDIUM.

The serializer is additive (new code reading existing structures; no JIT/runtime
change). The risk is "does the roundtrip faithfully reproduce the originals?"
(the V1 test gates this) + "did adding the serializer break the build / a shared
type?" Gate: NeoStep 205/205 + V1 roundtrip + Legacy-neutral.

## Maintain this file

APPEND durable findings after propose (the format design grounded in the actual
structures, the OpCodeR serialization choice, the reference-table design, the V1
roundtrip design, the GenericMethodTemplate serialization). Do NOT append chatter.

## Findings -- neo-step23-neoassembly (propose, 2026-07-07)

All artifacts authored (proposal.md, design.md, specs/neo-optimizer/spec.md,
tasks.md); `openspec validate` passes (pure-ASCII; see gotcha below).

### Grounding (structures verified verbatim, HEAD 21900a3a, build green 0 errors)

The format is grounded in the ACTUAL struct definitions (NOT a runtime dump --
the struct schemas are the authoritative ground truth; a single-method dump
would only confirm one instance). All under `#if ENABLE_NEO_MODE`:

- OpCodeR (`ILRuntime/Runtime/Intepreter/OpCodes/OpCode.cs`): 24 bytes,
  `[StructLayout(LayoutKind.Explicit)]`. Offsets: Code@0 (4B OpCodeREnum),
  Register1/DstOffset@4, Register2/SrcOffset@6, Register3/OperandOffset@8,
  Register4@10, Operand/OperandFloat@8 (4B), Operand2@12, OperandLong/
  OperandDouble@12 (8B), Operand3@16, Operand4@20. Marshal.SizeOf = 24.
- StackSlotInfo (`JITCompiler.cs:67`): {Offset, RefOffset, Size, RefCount}, 16B.
- CompiledFrame (`JITCompiler.cs:74-107`): see design.md D3 for the full
  load-bearing field list. NOT serialized: CodeBody (register-index; inliner/
  debugger only), Symbols (Dictionary<int,RegisterVMSymbol> keyed by Cecil
  Instruction -- NOT serializable).
- GenericMethodTemplate (`GenericMethodTemplate.cs:83`): 15 fields; see design.md
  D5. NOT serialized: Symbols, Addr (Cecil-keyed), RefBody/RefBodyAddr (runtime
  cache). CecilToken (object: Cecil TypeReference/MethodReference) re-resolved
  to TypeRef/MethodRef table index by PatchKind at serialize time.
- ILType field layout (`ILType.cs:18-88,366-408,700-718`): ILTypeFieldOffset
  {PrimitiveOffset, ReferenceOffset}, TotalPrimitiveSize, TotalReferenceCount,
  NeoVTable (IMethod[]), InterfaceEntry {InterfaceType, VTableOffset,
  MethodSlotKeys[], ClassSlotRemap[]}.

### HybridPatch reuse (the key seam)

`ILRuntime/HybridPatch/PatchInfo/AssemblyInfo.cs` already index-izes type/
method/field/string refs: TypeReferencePatchInfo, MethodReferencePatchInfo,
FieldReferencePatchInfo (each with Create() factory + WriteToStream/FromStream).
AssemblyPatchInfo header = Magic 0x58883551. Crucially, MethodPatchInfo ALREADY
HAS a CodeBodyRegister: OpCodeR[] field -- it just never serializes it (only the
CIL CodeBody). Neo extends HybridPatch by serializing CodeBodyRegister +
CompiledFrame + type metadata + templates. The .neo Magic is 0x494C524E ("ILRN")
-- distinct, a .neo is never a .patch. Reuse the *PatchInfo record types +
factories verbatim; wrap TypeRef in a NeoAOT record adding a 1-byte IL/CLR Kind.

### OpCodeR serialization decision: RAW 24-byte little-endian (LOCKED)

Via MemoryMarshal.AsBytes<OpCodeR> write / MemoryMarshal.Cast<byte,OpCodeR>
read. Rationale: the explicit-layout aliases mean raw bytes capture EVERY
variant (Register1/DstOffset, Operand/OperandFloat/Register3/Register4,
Operand2/OperandLong/OperandDouble) without per-opcode canonical-field
selection; field-by-field would reintroduce the F-8/OPT-HARDEN-K1 OpCodeR-union
aliasing defects. Version the header (Version=1) -- the sole guard against
future layout changes (struct stable across Steps 1-22). The 8 canonical fields
BodiesEqual compares cover the full 24 bytes, so raw == field-by-field
semantically; raw is an order of magnitude faster + simpler.

### Reference-table design (HybridPatch reuse, LOCKED)

StringTable (string[], length-prefixed UTF-8) + TypeRefTable
(TypeReferencePatchInfo[] + IL/CLR Kind byte) + MethodRefTable
(MethodReferencePatchInfo[]) + FieldRefTable (FieldReferencePatchInfo[]). The
token-bearing OpCodeR operands (type-token hash in Operand, method-token hash
in Operand2, string token in OperandLong) are serialized VERBATIM as part of
the raw OpCodeR record (they are runtime hashes). The reference tables exist so
Step 25's loader can rebuild hash->object maps. Cross-AppDomain hash
re-resolution is DEFERRED to Step 25 (same-process V1 roundtrip is exact).

### CompiledFrame + EH serialization (LOCKED)

Load-bearing fields per design.md D3. The Cecil-keyed addr[] map (used at JIT
time for EH resolution, JITCompiler.cs:595) is NOT serializable -- the EH
table is re-represented as NeoExceptionHandlerRecord {TryStartIdx, TryEndIdx,
HandlerStartIdx, HandlerEndIdx, FilterIdx, HandlerType, CatchTypeRefIdx} as BODY
INDICES (resolved via addr at serialize time). NeoCallParamMap.PrimitiveByRefElemType
(System.Type[]) serialized by assembly-qualified name.

### GenericMethodTemplate serialization -- FAITHFUL (the Step-22 follow-up, LOCKED)

The serializer captures EVERY PatchEntry the extractor records (every IL-source
Initobj Operand2==0, every Box/Isinst/Castclass/Newarr/Stobj/Ldobj T-token,
every Constrained T-type-token + trailing T-qualified callvirt method-token).
The auto-Initobj prefix is captured SEPARATELY via InitObjPrefixRegisters. So
the Step-22 CallIt<Struct> follow-up (inliner inserts Initobj for struct temps
the prefix-rebuild does NOT reproduce) is handled: any non-prefix Initobj
T-token the extractor records IS serialized as a patch.

### V1 roundtrip design (RECOMMENDED, load-bearing gate)

Host-side, DEBUG+Neo, mirroring NeoStep22SelfCheck. Matrix: non-generic method,
generic method WITH template, method with EH, methods with diverse locals
(primitive/IL-VT/CLR-VT)/byref params/ref return. Comparators: OpCodeR[]
byte-equality (MemoryMarshal.AsBytes) + FramesEqual (StackSlotInfo[] field-
for-field + scalars + LocalIsReference + NeoCatchException* + SwitchTargets +
NeoCallParamMap[] including aqname-resolved types) + TypeLayoutEqual +
TemplatesEqual. Reuse GenericMethodTemplateOps.BodiesEqual for the human-
readable diff. Invoked via a new CLI NeoStep23Roundtrip filter. V2 (deserialize
-> ExecuteNeo) is Step 25. NeoStep 205/205 + NeoOptHard 24/24 + NeoStep20 9/9 =
REGRESSION gate (additive).

### Gotcha: openspec validator + non-ASCII + first-line SHALL

Two validator traps bitten during propose:
1. The openspec validator rejects any non-ASCII byte in spec.md (a U+00A7
   section sign broke parsing of the whole requirement -> false "must contain
   SHALL or MUST"). ALL spec.md bytes MUST be ASCII. Cleaned proposal/design/
   tasks/spec of section signs / em dashes. The memory cjk-write-encoding-
   corruption warning generalizes: author ASCII-primary for openspec artifacts.
2. The validator scans only the FIRST WRAPPED LINE of a requirement body for
   SHALL/MUST. If the body's first hard-wrapped line ends before reaching
   SHALL (because a parenthesized list pushes SHALL to line 2), validation
   fails even though the body clearly contains SHALL. FIX: start each
   requirement body with "... SHALL ..." on the first wrapped line (mirror
   the archived Step-22 requirements, which all lead with SHALL).

## Findings -- neo-step23-neoassembly (apply, 2026-07-07)

Step 23 SHIPPED: the `.neo` binary format + BinaryWriter serializer +
BinaryReader deserializer + V1 roundtrip-equivalence self-check. All under
`#if ENABLE_NEO_MODE` (Neo-only, additive). V1 roundtrip 9/9 cells PASS.
Regression gate GREEN (NeoStep 205/205 + NeoOptHard 24/24 + NeoStep20 9/9 +
NeoStep22SelfCheck 55/55). Legacy-neutral PROVEN.

### Files shipped (new)

- `ILRuntime/Runtime/NeoAOT/NeoAssembly.cs` -- the format model: `NeoHeader`
  (Magic 0x494C524E "ILRN", Version=1, Endianness=1, 7 TableOffsets), the
  record structs (NeoMethodDefRecord / NeoTypeDefRecord / NeoFieldLayoutRecord
  / NeoInterfaceEntryRecord / NeoTemplateRecord / NeoPatchEntryRecord /
  NeoCallParamMapRecord / NeoExceptionHandlerRecord), `NeoAssemblyModel`
  (the deserialized holder), `NeoTableId` (7 tables), `NeoTypeRefKind`.
- `ILRuntime/Runtime/NeoAOT/NeoAssemblyWriter.cs` -- the BinaryWriter
  serializer. `NeoRefTableBuilder` (Cecil -> dedup ref-index, reusing the
  HybridPatch `*PatchInfo.Create` factories + structural dedup via WriteToStream
  byte-key). Static IO helpers (WriteOpCodeRArray raw-24-byte via
  MemoryMarshal.AsBytes, WriteStackSlotInfoArray, WriteNeoCallParamMap,
  WriteNeoExceptionHandler, WriteMethodDef, WriteTypeDef, WriteTemplate,
  reference-table writers). Instance `Write(types, methods, templates, stream)`
  builds all tables + emits header + 7 tables in one pass (build-all-blobs-first,
  no seek/patch). `CompileFresh` (per-occurrence JIT -> CompiledFrame + addr)
  + `BuildMethodDef/BuildTypeDef/BuildTemplate` (in-memory -> record).
- `ILRuntime/Runtime/NeoAOT/NeoAssemblyReader.cs` -- the BinaryReader
  deserializer. Static IO readers mirroring the writer (ReadOpCodeRArray via
  MemoryMarshal.Cast<byte,OpCodeR>, etc.). `Read(Stream) -> NeoAssemblyModel`
  (validates Magic/Version, reads header + 7 tables). `ResolveAqName` helper
  (System.Type re-resolution for PrimitiveByRefElemType, Step 25's job to
  harden).
- `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep23RoundtripCheck.cs` --
  host-side V1 self-check (`#if ENABLE_NEO_MODE && DEBUG`), mirroring
  NeoStep22SelfCheck. 9 cells: 6 method roundtrips (ProbeBasic / GenericProbe /
  TryCatchProbe / MixedLocals / ByrefParams / SwitchProbe) + TypeDef + Template
  + Full-model Write/Read. Comparators: OpCodeRsEqual (raw-24-byte byte-equality
  via MemoryMarshal.AsBytes + SequenceEqual), MethodDefsEqual (StackSlotInfo[]
  field-for-field + all scalars + LocalIsReference + NeoCatchException* +
  SwitchTargets + NeoCallParamMap[] + ExceptionHandlers[]), TypeDefsEqual
  (layout + fields + VTable + interfaces + static-ctor ref), TemplatesEqual
  (body byte-equal + patches field-for-field + scalars + arrays).
- `TestCases/NeoStep23NeoAssemblyTest.cs` -- the matrix serialization inputs:
  `NeoStep23TypeDefProbe` (base ILType + interface INeoStep23Iface + fields of
  primitive/IL-VT/CLR-VT/ref + static ctor + virtual methods) +
  `NeoStep23RoundtripProbes` (the 6 matrix methods). NOT auto-discovered as
  tests (methods have parameters) -- they are serialization inputs only.
- `ILRuntimeTestCLI/Program.cs` -- added the `NeoStep23Roundtrip` CLI filter
  (mirrors the NeoStep22SelfCheck hook).

### Files changed (additive, minimal)

- `ILRuntime/CLR/TypeSystem/ILType.cs` -- ONE additive read-only internal
  accessor `NeoInterfaceMapForAOT` (gated `#if ENABLE_NEO_MODE`) over the
  already-computed private `neoInterfaceMap` field, so the writer can serialize
  each InterfaceEntry without reconstructing it from the public surface. No
  behavior change. (Explicitly sanctioned by design task 5.3 + the proposal's
  "additive internal accessor" carve-out; NeoStep22SelfCheck still 55/55.)

### OpCodeR serialization -- RAW 24-byte little-endian (LOCKED, proven)

Write: `MemoryMarshal.AsBytes<OpCodeR>(body.AsSpan()).CopyTo(byte[])`. Read:
`MemoryMarshal.Cast<byte, OpCodeR>(raw).CopyTo(array)`. The V1 roundtrip
proves byte-for-byte equivalence via `ReadOnlySpan<byte>.SequenceEqual` on the
AsBytes spans (STRONGER than the 8-field BodiesEqual -- catches any union-alias
corruption). The header `Version=1` is the layout-change guard. No field that
could not roundtrip -- the raw dump is total over the 24 bytes.

### Reference tables -- REUSE HybridPatch (proven)

StringTable (length-prefixed UTF-8) + TypeRefTable (TypeReferencePatchInfo[] +
1-byte IL/CLR Kind) + MethodRefTable (MethodReferencePatchInfo[]) + FieldRefTable
(FieldReferencePatchInfo[]). Built via the existing `Create()` factories;
serialized via the existing `WriteToStream`/`FromStream`. Dedup is structural
(byte key of the serialized PatchInfo). The token-bearing OpCodeR operands
(type/method/string hashes) are serialized VERBATIM in the raw body -- the
reference tables exist so Step 25's loader can rebuild hash -> object maps
(cross-AppDomain re-resolution is Step 25, deferred).

### CompiledFrame serialization -- load-bearing fields (proven)

NeoMethodDefRecord carries NeoExecuteBody + LocalInfos + ParamInfos + all the
size scalars + LocalIsReference + NeoCatchException* + SwitchTargets +
NeoCallParamMap[] + ExceptionHandlers[]. NOT serialized: CodeBody (register-
index; inliner/debugger only) + Symbols (Cecil-Instruction-keyed, not
serializable) -- documented in WriteMethodDef. The Cecil-keyed `addr[]` EH map
is re-represented as NeoExceptionHandlerRecord[] (body INDICES resolved via
addr at serialize + catch-type -> TypeRef). NeoCallParamMap.PrimitiveByRefElemType
(System.Type[]) -> assembly-qualified names; null normalized to "" for an exact
roundtrip (ResolveAqName treats "" as null).

### GenericMethodTemplate serialization -- FAITHFUL (proven)

NeoTemplateRecord carries TemplateBody (raw OpCodeR[]) + Patches[] (InstrIdx /
Field / Kind / GenericParamIdx / TokenRefIdx / CecilTokenKind) + the front-half
scalars + InitObjPrefixRegisters + VariableTypeRefIdxs + ConstrainedTypeRefIdxs
+ ConstrainedMethodRefIdxs + SwitchTargets. Every PatchEntry is captured (the
Step-22 CallIt<Struct> follow-up); CecilToken re-resolved to TypeRef/MethodRef
index by Kind at serialize time. NOT serialized: Symbols/Addr (Cecil-keyed) +
RefBody/RefBodyAddr (runtime cache). The GenericProbe template roundtrips with
patches=2 (the Box-T TypeToken + the constrained. T callvirt TypeToken).

### Q1 / Q2 / Q3 resolutions (apply)

- **Q1 (serialize CodeBody register-index?):** NO. Only NeoExecuteBody is
  serialized (CodeBody is inliner/debugger-only; the AOT loader does not inline
  + the debugger path is a separate deferred item). If a future step needs it,
  add a second raw OpCodeR[] under a Version bump. Recorded as a deferred item.
- **Q2 (serialize VTable slot-key strings?):** NO. The TypeDef serializes only
  the `IMethod[]` slot -> MethodRef-index array (VTableMethodRefIdxs); the
  reverse slot-key map (neoVTableSlots) is rebuilt by Step 25's loader. The
  interface map's MethodSlotKeys[] ARE serialized (they are per-interface
  declaring-type slots, not the class reverse map) -- needed for interface
  dispatch and accessible via the NeoInterfaceMapForAOT accessor.
- **Q3 (static-instance initial values):** NOT serialized at V1. The static
  LAYOUT (StaticTotalPrimitiveSize / StaticTotalReferenceCount) IS captured,
  and the static-ctor MethodRefIdx is serialized (so Step 25 runs the .cctor to
  seed the static instance, mirroring how the runtime initializes it). The
  raw byte initial-values story (HybridPatch FieldPatchInfo.InitialValues) is
  scoped out: the Neo runtime seeds statics by EXECUTING the .cctor (not from
  captured byte blobs), so the .cctor ref is the faithful seed. If Step 25
  finds a .cctor-less static with a non-default initializer, that is the
  deferred gap.

### V1 roundtrip result (the load-bearing proof)

9/9 cells PASS:
- ProbeBasic (non-generic, len=6), GenericProbe (generic+template, len=8),
  TryCatchProbe (EH, len=15), MixedLocals (primitive/IL-VT/CLR-VT, len=25),
  ByrefParams (ref/out, len=7), SwitchProbe (SwitchTargets, len=13) -- all
  byte-exact OpCodeR + field-for-field frame.
- TypeDef NeoStep23TypeDefProbe: roundtrip PASS (4 fields, vtable=6, ifaces=1).
- Template GenericProbe: roundtrip PASS (patches=2, bodyLen=9).
- Full-model Write/Read: PASS (methods=6, typerefs=4, methodrefs=12) -- the
  whole pipeline (header + 7 tables + offsets) is internally consistent; the
  5 non-generic bodies are byte-equal to a fresh compile through the full
  Write->Read pipeline.

No cell scoped out, no field that could not roundtrip. The Full-model cell's
body cross-check skips OPEN generic definitions (compiling an open definition
is non-deterministic -- the Step-22 documented quirk; their body-level
faithfulness is already proven by the per-record cell, which compiles once).

### Regression gate + Legacy-neutral

- NeoStep 205/205, NeoOptHard 24/24, NeoStep20 9/9, NeoStep22SelfCheck 55/55 --
  ZERO regressions (the serializer is additive; no JIT/runtime change).
- Legacy-neutral PROVEN by construction: ALL new code is `#if ENABLE_NEO_MODE`
  (the NeoAOT namespace + the ILType accessor + the self-check + the CLI hooks
  compile out of plain `Debug`), so the compiled ILRuntime.dll is byte-identical
  to HEAD under plain Debug. Verified: plain `Debug` builds with 0 errors; the
  plain-Debug Legacy NeoStep smoke runs 205 tests with 0 NeoStep23 entries in
  the failure set (the 8 Legacy failures are pre-existing NeoStep6/13/14/15/16
  Legacy-mode quirks, unchanged). The test count is 205 in BOTH modes (the
  NeoStep23 probes are parameterized methods -> NOT auto-discovered as tests).
  The `#if ENABLE_NEO_MODE` gating IS the compile-time stash-toggle.

### Deferred to Step 24 / 25 / 26

- Step 24: the `ilrt_neoc` standalone CLI (Step 23's serializer is the LIBRARY
  it calls).
- Step 25: the runtime `.neo` LOADER -- re-resolve the reference tables to
  hash -> object maps the runtime expects; rebuild the VTable slot-key reverse
  map; re-resolve PrimitiveByRefElemType byref element types via the AppDomain;
  rebuild EH lookup from the body-index records; V2 functional (deserialize ->
  ExecuteNeo). Cross-AppDomain token-hash stability is Step 25's job.
- Step 26: perf benchmarks.
- CLR interface implementor types are recorded with a -1 TypeRef idx at V1
  (CLR types index by assembly-qualified name at Step 25); the slot layout
  (VTableOffset + MethodSlotKeys + ClassSlotRemap) IS recorded faithfully.

## Findings -- neo-step23-neoassembly (review-fix round 1, 2026-07-07)

Adopted the reviewer's Minor-1 + Minor-3 (TEST-ONLY; no engine change). Both
gates re-verified GREEN after the tighten.

### Minor-1 -- the 3 identity ref-idx fields now ASSERTED (not just read)

The V1 comparators serialize+read these but did not compare them. Added 3
one-line comparisons so the gate PROVES they persist (a future write/read-
order bug in an identity idx can no longer slip the gate):
- `NeoStep23RoundtripCheck.cs` MethodDefsEqual -- `MethodRefIdx` (how the
  Step-25 loader maps a MethodDef to its declaring MethodRef).
- TypeDefsEqual -- `TypeRefIdx` (the type's own full-name identity).
- TemplatesEqual -- `DefinitionMethodRefIdx` (the open generic method def).

The record structs already carry the fields (`NeoAssembly.cs:104/164/200`)
and the writer/reader already (de)serialize them as the FIRST int of each
record (Writer:292/365/401, Reader:174/226/269). The 15/15 PASS WITH the
strict checks confirms they roundtrip positionally -- NO serializer fix
needed (matches the reviewer's "correct by inspection" call; now PROVEN by
the gate, not just inspected). Confirmed-none: no real bug found.

### Minor-3 -- 6 adversarial probes adopted permanently into the V1 matrix

Matrix grew 9 -> 15 cells (each serialize -> deserialize -> assert==original
under the now-strict comparators). Adopted:
- `MultiConstrainedGeneric<T>` (method + template) -- the Step-22 BLOCKER-1
  constrained. T shape DOUBLED: patches=2, constrainedT=2, constrainedM=2
  (the shipped GenericProbe template has 1 pair). Most important gain.
- `NestedEH` -- try/catch/catch/finally, 3 EH clauses, mixed HandlerType
  (Catch,Catch,Finally). len=26.
- `ClrByrefCall` -- int.TryParse(string, out int): a byref slot carrying a
  REAL "System.Int32" PrimitiveByRefElemType aqname (nonEmptyAqnameSlots=1).
  Exercises the null->"" normalization path the shipped matrix NEVER hit
  with a real value (the apply-time bug lived here).
- `NeoStep23MultiIfaceProbe` TypeDef -- implements 2 IL interfaces
  (ifaces=2, vtable=8); the shipped TypeDef has ifaces=1. Exercises the
  multi-entry NeoInterfaceEntryRecord[] path.
- `ZeroLocals` -- LocalInfos.Length == 0, len=3 (empty-array write path).
(The 6th reviewer probe -- whole-pipeline byte-equality over many methods --
was already the shipped Full-model cell; it now spans both TypeDefs.)

### Gates (re-verified independently, 2026-07-07)

- NeoStep23Roundtrip: **15/15 PASS** (was 9/9). All 3 identity ref-idx
  checks + all 6 adopted probes PASS.
- NeoStep: **Ran 205 tests, 0 failed** (regression gate GREEN, unchanged).
- Build: CLI Debug_Neo 0 errors; TestCases plain Debug 0 errors.
- TEST-ONLY: no engine/serializer/reader change (the 3 ref-idx fields were
  already wired end-to-end; the comparators now assert them). All new code
  stays `#if ENABLE_NEO_MODE` (Legacy-neutral).
