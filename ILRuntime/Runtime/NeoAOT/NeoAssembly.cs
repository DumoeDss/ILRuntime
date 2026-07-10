#if ENABLE_NEO_MODE
using System;
using System.Collections.Generic;

using ILRuntime.Runtime.Intepreter.OpCodes;

namespace ILRuntime.Runtime.NeoAOT
{
    // ===== Step 23: the .neo binary FORMAT MODEL =====
    //
    // A standalone serialization layer over the Neo in-memory structures
    // (OpCodeR[] / CompiledFrame / ILType field layout / GenericMethodTemplate).
    // This file holds ONLY the format model: the header constants + the table
    // record structs + the deserialized-model holder. The BinaryWriter serializer
    // lives in NeoAssemblyWriter.cs; the BinaryReader deserializer in
    // NeoAssemblyReader.cs.
    //
    // Additive + Neo-only: the whole namespace is #if ENABLE_NEO_MODE, so Legacy
    // ExecuteR is byte-identical (this file compiles out of plain Debug). No
    // JIT / runtime / Step-22 behavior change -- the serializer only READS the
    // public/internal fields those structures already expose.

    /// <summary>
    /// Format-level constants. The .neo Magic is DISTINCT from HybridPatch's
    /// 0x58883551 (a .neo is never a .patch). Version guards the OpCodeR raw-24-
    /// byte layout + the table record shapes; a future layout change bumps
    /// Version and old readers reject the file.
    /// </summary>
    internal static class NeoAssemblyFormat
    {
        // "ILRN" little-endian = 0x49 0x4C 0x52 0x4E.
        public const int Magic = 0x494C524E;
        // Step 25 S3-2: Version bumped 1 -> 2. V2 added the APPROACH-1 recorded
        // compile-time identity-hash arrays (TypeRefHashes + MethodRefHashes)
        // parallel to the TypeRef / MethodRef tables, so a Cecil-free load into
        // a FRESH AppDomain can re-register each resolved ref under the recorded
        // hash (the baked token operands then resolve in B's maps). The arrays
        // are ADDITIVE: the same-AppDomain S1/S2/S3-partial path IGNORES them
        // (the live maps resolve the bodies naturally).
        //
        // Step 25 S3-4: Version bumped 2 -> 3. V3 adds the per-static-field
        // layout array (NeoTypeDefRecord.StaticFields[]) parallel to the
        // instance Fields[], so a Cecil-free ILType can locate each STATIC field
        // in its static byte[] + AutoList storage (the instance Fields[] only
        // covers instance fields). This is ADDITIVE + backward-compatible: the
        // same-AppDomain S1/S2/S3 path IGNORES StaticFields[] (the live Cecil
        // InitializeFields provides the static offsets naturally); the Cecil-free
        // loader's Version guard rejects a prior-Version `.neo` for the static-
        // field path. Each type writes StaticFields[] (EMPTY for a type with no
        // static fields), so a V3 reader reads every V3 `.neo` cleanly.
        //
        // neo-debugger-aot-body: Version bumped 3 -> 4. V4 adds the per-method
        // LOCAL variable metadata (NeoMethodDefRecord.LocalVariables[] -- one
        // NeoLocalVarRecord per declared local: TypeRefIdx + Name). A Cecil-free
        // ILMethod shell (def == null, the S3-2 LoadNeoAssembly path) has NO
        // Definition.Body.Variables, so the Neo debugger frame read needs the
        // local type/name in the .neo to inspect AOT-body locals. ADDITIVE +
        // backward-incompatible only via the Version guard: the same-AppDomain
        // S1/S2/S3 path IGNORES LocalVariables[] (Definition.Body.Variables is
        // present + authoritative); the Cecil-free loader resolves each local's
        // TypeRef to a runtime IType at InitCodeBodyFromNeo. Each method writes
        // LocalVariables[] (EMPTY for a method with no locals), so a V4 reader
        // reads every V4 `.neo` cleanly. (The PARKED neo-aot-generic-cecilfree
        // child originally planned the V4 bump for GenericParamNames -- it is
        // re-routed to neo-aot-generic-cecilfree-backhalf + will use V5.)
        public const short Version = 4;
        public const byte EndiannessLittle = 1;
        // 7 indexed tables (the static-ctor InitializerTable is folded into the
        // TypeDefTable via StaticCtorMethodRefIdx -- see design.md D4/D6).
        public const int TableCount = 7;
    }

    /// <summary>
    /// The .neo file header. Written at offset 0; TableOffsets[] is patched in
    /// after each table's start position is known (the writer buffers table data
    /// then seeks back, OR -- simpler for V1 -- the writer records offsets as it
    /// writes and re-emits the header at the head of a second pass into a fresh
    /// stream). V1 writes tables sequentially into a MemoryStream and captures
    /// each table's start offset for the header.
    /// </summary>
    internal struct NeoHeader
    {
        public int Magic;
        public short Version;
        public byte Endianness;
        public byte Reserved;
        // One per table, in NeoTableId order.
        public int[] TableOffsets;

        public static NeoHeader Create()
        {
            return new NeoHeader
            {
                Magic = NeoAssemblyFormat.Magic,
                Version = NeoAssemblyFormat.Version,
                Endianness = NeoAssemblyFormat.EndiannessLittle,
                Reserved = 0,
                TableOffsets = new int[NeoAssemblyFormat.TableCount],
            };
        }
    }

    /// <summary>
    /// Table identifiers (also the index into NeoHeader.TableOffsets[] and the
    /// serialization order).
    /// </summary>
    internal enum NeoTableId
    {
        String = 0,
        TypeRef = 1,
        MethodRef = 2,
        FieldRef = 3,
        TypeDef = 4,
        MethodDef = 5,
        Template = 6,
    }

    // ===== OpCodeR raw-24-byte serialization =====
    //
    // OpCodeR is [StructLayout(LayoutKind.Explicit)] with aliasing fields (see
    // OpCode.cs). A raw MemoryMarshal.AsBytes<OpCodeR> dump captures EVERY alias
    // without per-opcode canonical-field selection (the F-8 / OPT-HARDEN-K1
    // lessons were OpCodeR-union aliasing mistakes). The header Version field is
    // the sole guard against future layout changes (the struct has been stable
    // across Steps 1-22). Little-endian (host-native on x86/x64/arm64-le).

    // ===== CompiledFrame load-bearing fields (design D3) =====
    //
    // One NeoMethodDefRecord per IL method. NOT serialized: CodeBody (register-
    // index; only the inliner / debugger consume it -- ExecuteNeo runs against
    // NeoExecuteBody) and Symbols (Dictionary<int,RegisterVMSymbol> keyed by a
    // Cecil Instruction -- not serializable; only the Step-22 patch extractor
    // and the debugger use it, both serialize-time / out of scope).

    internal struct NeoMethodDefRecord
    {
        public int MethodRefIdx;          // -> MethodRefTable (declaring type + name + sig)
        // Step 25 S3-2: the method's return type (-> TypeRefTable). The
        // MethodRef table does NOT carry a return type (HybridPatch's
        // MethodReferencePatchInfo omits it); a Cecil-free ILMethod shell needs
        // it for Run's type-discriminated return-read, so it rides here. -1 if
        // void / unavailable (the Cecil-free loader resolves void from the
        // AppDomain).
        public int ReturnTypeRefIdx;
        // The lowered body ExecuteNeo runs (raw 24-byte OpCodeR[]).
        public OpCodeR[] NeoExecuteBody;
        // Frame layout. StackSlotInfo = {Offset, RefOffset, Size, RefCount} (4 ints).
        public ILRuntime.Runtime.Intepreter.RegisterVM.StackSlotInfo[] LocalInfos;
        public ILRuntime.Runtime.Intepreter.RegisterVM.StackSlotInfo[] ParamInfos;
        public int TotalStructSize;
        public int TotalRefSize;
        public int ParamPrimitiveSize;
        public int ParamReferenceCount;
        public int LocalsPrimitiveSize;
        public int LocalsReferenceCount;
        public int ReturnPrimitiveSize;
        public int ReturnRefCount;
        public int StackRegisterCount;
        public bool[] LocalIsReference;
        // Step 14 catch-handler exception variable slot (-1 / 0/0/0 when no catch).
        public int NeoCatchExceptionRegIndex;
        public int NeoCatchExceptionByteOffset;
        public int NeoCatchExceptionRefOffset;
        // SwitchTargets: Dictionary<int,int[]> (keys are stable Cecil array hashes;
        // values are body indices -- serialized verbatim).
        public KeyValuePair<int, int[]>[] SwitchTargets;
        // NeoCallParamMap[] (ushort arrays + bool[] flags + CLR System.Type[] by
        // assembly-qualified name -- the System.Type[] is not otherwise serializable).
        public NeoCallParamMapRecord[] NeoCallParams;
        // Exception handlers as BODY INDICES (the Cecil-keyed addr[] map used at
        // JIT time is not serializable -- the EH table is re-represented here).
        public NeoExceptionHandlerRecord[] ExceptionHandlers;
        // V4 (neo-debugger-aot-body): the method's LOCAL variable metadata, one
        // entry per declared local (varCnt -- the SAME count as Cecil Body.
        // Variables.Count, NOT LocalInfos.Length which also holds params + temp
        // registers). Carries the local's declared TYPE (-> TypeRefTable, so a
        // Cecil-free shell can resolve it to a runtime IType) + the local's NAME
        // (from Cecil DebugInformation, or "v" + index when no debug name). This
        // is what the Neo debugger frame read (DebugService.GetLocalVariableInfo)
        // needs to inspect an AOT-loaded body's locals: a Cecil-free ILMethod
        // shell (def == null) has NO Definition.Body.Variables, so the type/name
        // must ride the .neo. The slot LAYOUT (offsets/sizes) is already in
        // LocalInfos[paramCnt..paramCnt+varCnt). -1 TypeRefIdx = unavailable
        // (the local renders as "<unknown local type>", mirroring the JIT-path
        // null-type guard). ADDITIVE: V3 .neo readers reject this at the Version
        // guard; same-AppDomain S1 Attach IGNORES it (Definition.Body.Variables
        // is present + authoritative on the S1 path).
        public NeoLocalVarRecord[] LocalVariables;
    }

    // V4 (neo-debugger-aot-body): one LOCAL variable's declared metadata. The
    // declared type is a TypeRefTable index (resolved to a runtime IType at load
    // via the same closure InitCodeBodyFromNeo uses for catch types); -1 if the
    // type could not be indexed (renders as "<unknown local type>"). The name is
    // the Cecil debug name (or "v" + index). Parallel to LocalVariables[]:
    // local index i -> LocalInfos[ParameterCount + (HasThis?1:0) + i] for the
    // slot layout, and LocalVariables[i] for the type/name.
    internal struct NeoLocalVarRecord
    {
        public int TypeRefIdx;   // -> TypeRefTable (-1 if unavailable)
        public string Name;      // Cecil debug name or "v" + index
    }

    internal struct NeoCallParamMapRecord
    {
        public ushort[] PrimitiveSrc;
        public ushort[] PrimitiveDst;
        public ushort[] PrimitiveSize;
        public ushort[] RefSrc;
        public ushort[] RefDst;
        public bool[] PrimitiveByRefSrc;
        public bool[] PrimitiveByRefWriteBack;
        // One assembly-qualified name per primitive slot (null/empty for a slot
        // with no element type -- a frame-native byref or a non-byref slot).
        public string[] PrimitiveByRefElemTypeAqName;
    }

    internal struct NeoExceptionHandlerRecord
    {
        public int TryStartIdx;       // body index (resolved via addr[] at serialize)
        public int TryEndIdx;
        public int HandlerStartIdx;
        public int HandlerEndIdx;
        public int FilterIdx;         // -1 if none
        public int HandlerType;       // Mono.Cecil.Cil.ExceptionHandlerType (int)
        public int CatchTypeRefIdx;   // -> TypeRefTable (-1 for finally/fault/filter-all)
    }

    // ===== ILType metadata (design D4) =====

    internal struct NeoTypeDefRecord
    {
        public int TypeRefIdx;           // -> TypeRefTable (the type's own full name)
        public int BaseTypeRefIdx;       // -1 if none
        public int TotalPrimitiveSize;
        public int TotalReferenceCount;
        public int StaticTotalPrimitiveSize;
        public int StaticTotalReferenceCount;
        public NeoFieldLayoutRecord[] Fields;        // instance fields
        // Step 25 S3-4: the per-STATIC-field layout, parallel to the instance
        // Fields[]. Each entry = {FieldRefIdx (name + type + IsStatic),
        // PrimitiveOffset, ReferenceOffset} -- the SAME NeoFieldLayoutRecord
        // shape used for instance fields, but for the type's STATIC fields
        // (offsets within the static byte[] Primitives + static AutoList
        // ManagedObjects). The Cecil-free ILType factory installs staticFieldOffsets
        // / staticFieldTypes / staticFieldMapping from this array so the .cctor
        // (seeded at Cecil-free load) + Stsfld/Ldsfld tokens resolve. EMPTY for a
        // type with no static fields (never null on the wire -- length-prefixed).
        // ADDITIVE: the same-AppDomain S1/S2/S3 path IGNORES it (the Cecil
        // InitializeFields provides the static offsets naturally).
        public NeoFieldLayoutRecord[] StaticFields;   // static fields (S3-4)
        // NeoVTable (IMethod[] slot -> method-ref). Each slot -> MethodRef index
        // (open question Q2 default NO: serialize only the slot -> method-ref
        // array; the reverse slot-key map is rebuilt by Step 25's loader).
        public int[] VTableMethodRefIdxs;
        public NeoInterfaceEntryRecord[] Interfaces;
        public int StaticCtorMethodRefIdx;   // -1 if none (Q3: initial values mirror
                                             // HybridPatch FieldPatchInfo.InitialValues
                                             // -- scoped to the FieldRefTable for V1).
    }

    internal struct NeoFieldLayoutRecord
    {
        public int FieldRefIdx;          // -> FieldRefTable (name + type + IsStatic)
        public int PrimitiveOffset;
        public int ReferenceOffset;
    }

    internal struct NeoInterfaceEntryRecord
    {
        public int InterfaceTypeRefIdx;
        public int VTableOffset;
        public string[] MethodSlotKeys;
        public int[] ClassSlotRemap;     // -1 sentinel = empty (null in the live map)
    }

    // ===== GenericMethodTemplate (design D5 -- FAITHFUL, every T-identity site) =====

    internal struct NeoTemplateRecord
    {
        public int DefinitionMethodRefIdx;        // the open generic method def
        public OpCodeR[] TemplateBody;            // raw 24-byte OpCodeR[]
        public NeoPatchEntryRecord[] Patches;
        public short LocVarRegStart;
        public int TotalRegCnt;
        public short NeoCatchExRegFinal;
        public int StackRegisterCount;
        public int VarCnt;
        public int InitObjPrefixLength;
        public int[] InitObjPrefixRegisters;
        public int[] VariableTypeRefIdxs;
        public int[] ConstrainedTypeRefIdxs;
        public int[] ConstrainedMethodRefIdxs;
        public KeyValuePair<int, int[]>[] SwitchTargets;
    }

    internal struct NeoPatchEntryRecord
    {
        public int InstrIdx;
        public int Field;        // PatchField enum (Operand=0 / Operand2=1 / Operand4=2)
        public int Kind;         // PatchKind enum (TypeToken=0 / MethodToken=1 / IsRefMoveFlag=2)
        public int GenericParamIdx;
        // TypeRefTable index if Kind=TypeToken; MethodRefTable index if Kind=MethodToken;
        // -1 if Kind=IsRefMoveFlag. The Kind discriminator tells the reader which table.
        public int TokenRefIdx;
        // The Cecil-token Kind captured at serialize time (TypeReference vs
        // MethodReference) so the Step-25 loader can re-resolve without re-running
        // the extractor. Mirrors PatchKind for V1; recorded explicitly for forward
        // compatibility (a future PatchKind split between Box-T and Initobj-T).
        public byte CecilTokenKind;   // 0 = TypeReference, 1 = MethodReference, 2 = none
    }

    /// <summary>
    /// The deserialized model holder -- what NeoAssemblyReader.Read returns and
    /// what Step 24's ilrt_neoc CLI / Step 25's runtime loader will consume. V1
    /// (Step 23) populates it host-side for the roundtrip self-check; V2
    /// (Step 25) wires it into ExecuteNeo.
    /// </summary>
    internal sealed class NeoAssemblyModel
    {
        public NeoHeader Header;
        public string[] StringTable;
        public ILRuntime.Hybrid.TypeReferencePatchInfo[] TypeRefs;
        public NeoTypeRefKind[] TypeRefKinds;        // parallel to TypeRefs (Il=0 / Clr=1)
        public ILRuntime.Hybrid.MethodReferencePatchInfo[] MethodRefs;
        public ILRuntime.Hybrid.FieldReferencePatchInfo[] FieldRefs;
        public NeoTypeDefRecord[] TypeDefs;
        public NeoMethodDefRecord[] MethodDefs;
        public NeoTemplateRecord[] Templates;
        // Step 25 S3-2 (APPROACH 1): the recorded compile-time identity-hash ->
        // NAME bindings, snapshotted from the COMPILING AppDomain's mapTypeToken
        // / mapMethod after the bodies are JIT-compiled. Each baked token operand
        // in the deserialized bodies carries one of these hashes (ILType/ILMethod
        // identity for IL refs; Cecil TypeReference / MethodReference identity
        // for CLR-type + method-call tokens -- ALL identity-based against
        // process-global counters, NONE reproducible in a fresh AppDomain). A ref
        // may appear under MULTIPLE hashes (different Cecil token instances of
        // the same logical ref baked in different bodies), so this is a FLAT
        // binding list. The binding carries the NAME directly (NOT a ref-table
        // index) because body-INTERNAL call-site tokens (e.g. an interface
        // method a body calls via Callvirt_Interface) are NOT in the .neo ref
        // tables -- matching by name-only reaches them all. The Cecil-free
        // loader re-resolves by name + re-registers under the recorded hash.
        // Same-AppDomain loads IGNORE these.
        public NeoTokenBinding[] TypeTokenBindings;
        public NeoTokenBinding[] MethodTokenBindings;
    }

    /// <summary>
    /// Step 25 S3-2 (APPROACH 1): one recorded compile-time identity-hash ->
    /// name binding. For a type: FullName. For a method: DeclaringFullName +
    /// Name + ParamCount. The Cecil-free loader re-resolves by name, then
    /// registers the resolved object in the fresh AppDomain's mapTypeToken /
    /// mapMethod under <see cref="Hash"/> (an ALIAS key alongside the fresh
    /// identity hash) so the baked token operands resolve.
    /// </summary>
    internal struct NeoTokenBinding
    {
        public int Hash;                    // the compile-time identity hash baked in a body
        public string FullName;             // type: the type full name; method: the declaring type full name
        public string MethodName;           // type: null; method: the method name
        public int ParamCount;              // type: 0; method: the parameter count
    }

    /// <summary>1-byte IL/CLR discriminator carried alongside each TypeRef.</summary>
    internal enum NeoTypeRefKind : byte
    {
        Il = 0,
        Clr = 1,
    }
}
#endif
