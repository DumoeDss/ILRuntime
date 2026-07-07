#if ENABLE_NEO_MODE
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

using ILRuntime.CLR.Method;
using ILRuntime.CLR.TypeSystem;
using ILRuntime.Hybrid;
using ILRuntime.Mono.Cecil;
using ILRuntime.Mono.Cecil.Cil;
using ILRuntime.Runtime.Intepreter.OpCodes;
using ILRuntime.Runtime.Intepreter.RegisterVM;

namespace ILRuntime.Runtime.NeoAOT
{
    // ===== Step 23: the .neo BinaryWriter serializer =====
    //
    // Reads in-memory Neo structures (OpCodeR[] / CompiledFrame / ILType field
    // layout / GenericMethodTemplate) and writes a .neo stream. Reuses the
    // HybridPatch reference-index machinery (TypeReferencePatchInfo /
    // MethodReferencePatchInfo / FieldReferencePatchInfo) verbatim -- Neo
    // extends HybridPatch by ALSO serializing the register body + frame + type
    // metadata + templates, it does not reinvent the reference tables.
    //
    // Layering:
    //   * NeoRefTableBuilder          -- Cecil -> ref-index (dedup) for the 4
    //                                    reference tables (string/type/method/field).
    //   * static IO helpers (Write*)  -- pure record -> BinaryWriter (NO Cecil).
    //   * instance Write(...)         -- assembles records from in-memory state
    //                                    (using the builder) + writes the model.
    //
    // The IO helpers are pure (operate on already-assembled records carrying int
    // indices), so a host-side roundtrip self-check can serialize a single record
    // to a MemoryStream and back WITHOUT a full assembly -- that is the V1 gate.

    /// <summary>
    /// Builds + dedups the 4 reference tables (String / TypeRef / MethodRef /
    /// FieldRef). Dedup is structural (two refs that build an equal PatchInfo
    /// collapse to one index); the V1 roundtrip is exact regardless of dedup
    /// policy because the writer AND reader run in the same process and the
    /// OpCodeR body carries runtime token HASHES (not indices) -- the reference
    /// tables exist so Step 25's loader can rebuild hash -> object maps.
    /// </summary>
    internal sealed class NeoRefTableBuilder
    {
        readonly HashSet<MemberReference> _internalRefs;
        readonly Dictionary<FieldDefinition, int> _fieldIdxMapping;

        // String table.
        readonly List<string> _strings = new List<string>();
        readonly Dictionary<string, int> _stringIdx = new Dictionary<string, int>();

        // TypeRef table (parallel: PatchInfo + IL/CLR Kind).
        readonly List<TypeReferencePatchInfo> _typeRefs = new List<TypeReferencePatchInfo>();
        readonly List<NeoTypeRefKind> _typeRefKinds = new List<NeoTypeRefKind>();
        readonly Dictionary<string, int> _typeRefIdx = new Dictionary<string, int>();

        // MethodRef table.
        readonly List<MethodReferencePatchInfo> _methodRefs = new List<MethodReferencePatchInfo>();
        readonly Dictionary<string, int> _methodRefIdx = new Dictionary<string, int>();

        // FieldRef table.
        readonly List<FieldReferencePatchInfo> _fieldRefs = new List<FieldReferencePatchInfo>();
        readonly Dictionary<string, int> _fieldRefIdx = new Dictionary<string, int>();

        public NeoRefTableBuilder(HashSet<MemberReference> internalRefs = null,
            Dictionary<FieldDefinition, int> fieldIdxMapping = null)
        {
            _internalRefs = internalRefs ?? new HashSet<MemberReference>();
            _fieldIdxMapping = fieldIdxMapping ?? new Dictionary<FieldDefinition, int>();
        }

        public string[] Strings => _strings.ToArray();
        public TypeReferencePatchInfo[] TypeRefs => _typeRefs.ToArray();
        public NeoTypeRefKind[] TypeRefKinds => _typeRefKinds.ToArray();
        public MethodReferencePatchInfo[] MethodRefs => _methodRefs.ToArray();
        public FieldReferencePatchInfo[] FieldRefs => _fieldRefs.ToArray();

        public int IndexString(string s)
        {
            if (s == null) s = string.Empty;
            if (_stringIdx.TryGetValue(s, out int idx)) return idx;
            idx = _strings.Count;
            _stringIdx[s] = idx;
            _strings.Add(s);
            return idx;
        }

        // Route a Cecil TypeReference + its resolved IType (may be null) through
        // TypeReferencePatchInfo.Create. IL vs CLR is discriminated by the IType
        // carried alongside (a Cecil ref alone cannot tell IL-from-CLR -- the
        // AppDomain resolves that).
        public int IndexTypeRef(TypeReference tr, IType itype = null)
        {
            if (tr == null) return -1;
            var info = TypeReferencePatchInfo.Create(tr, _internalRefs);
            string key = StructuralKey(info);
            if (_typeRefIdx.TryGetValue(key, out int idx)) return idx;
            idx = _typeRefs.Count;
            _typeRefIdx[key] = idx;
            _typeRefs.Add(info);
            _typeRefKinds.Add(itype is CLRType ? NeoTypeRefKind.Clr : NeoTypeRefKind.Il);
            return idx;
        }

        public int IndexMethodRef(MethodReference mr)
        {
            if (mr == null) return -1;
            var info = MethodReferencePatchInfo.Create(mr, _internalRefs);
            string key = StructuralKey(info);
            if (_methodRefIdx.TryGetValue(key, out int idx)) return idx;
            idx = _methodRefs.Count;
            _methodRefIdx[key] = idx;
            _methodRefs.Add(info);
            return idx;
        }

        public int IndexMethodRef(IMethod method, ModuleDefinition module)
        {
            if (method == null) return -1;
            var info = MethodReferencePatchInfo.Create(method, module, _internalRefs);
            string key = StructuralKey(info);
            if (_methodRefIdx.TryGetValue(key, out int idx)) return idx;
            idx = _methodRefs.Count;
            _methodRefIdx[key] = idx;
            _methodRefs.Add(info);
            return idx;
        }

        public int IndexFieldRef(FieldReference fr)
        {
            if (fr == null) return -1;
            var info = FieldReferencePatchInfo.Create(fr, _internalRefs, _fieldIdxMapping);
            string key = StructuralKey(info);
            if (_fieldRefIdx.TryGetValue(key, out int idx)) return idx;
            idx = _fieldRefs.Count;
            _fieldRefIdx[key] = idx;
            _fieldRefs.Add(info);
            return idx;
        }

        // Structural dedup key: serialize the PatchInfo to a little MemoryStream
        // and use the resulting byte string. Exact (HybridPatch WriteToStream is
        // deterministic) and allocation-bounded for test-scale tables.
        static string StructuralKey(TypeReferencePatchInfo info)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                info.WriteToStream(bw);
                return Convert.ToBase64String(ms.ToArray());
            }
        }
        static string StructuralKey(MethodReferencePatchInfo info)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                info.WriteToStream(bw);
                return Convert.ToBase64String(ms.ToArray());
            }
        }
        static string StructuralKey(FieldReferencePatchInfo info)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                info.WriteToStream(bw);
                return Convert.ToBase64String(ms.ToArray());
            }
        }
    }

    /// <summary>
    /// The BinaryWriter serializer. Pure-record IO is exposed as static helpers
    /// (WriteOpCodeRArray etc.); the instance Write(...) assembles a full model.
    /// </summary>
    internal sealed class NeoAssemblyWriter
    {
        const int OpCodeRSize = 24;   // OpCodeR is [StructLayout(Explicit)], 24 bytes.

        // ===== OpCodeR[] : RAW 24-byte little-endian (design D1) =====

        public static void WriteOpCodeRArray(BinaryWriter bw, OpCodeR[] body)
        {
            if (body == null) { bw.Write(-1); return; }
            bw.Write(body.Length);
            // MemoryMarshal.AsBytes captures EVERY [StructLayout(Explicit)] alias
            // (Register1/DstOffset, Operand/OperandFloat/Register3/Register4,
            // Operand2/OperandLong/OperandDouble) without per-opcode canonical-
            // field selection -- the F-8 / OPT-HARDEN-K1 lessons.
            byte[] raw = new byte[body.Length * OpCodeRSize];
            MemoryMarshal.AsBytes<OpCodeR>(body.AsSpan()).CopyTo(raw);
            bw.Write(raw);
        }

        // ===== StackSlotInfo[] : 4 ints per record (design D3) =====

        public static void WriteStackSlotInfoArray(BinaryWriter bw, StackSlotInfo[] arr)
        {
            if (arr == null) { bw.Write(-1); return; }
            bw.Write(arr.Length);
            for (int i = 0; i < arr.Length; i++)
            {
                bw.Write(arr[i].Offset);
                bw.Write(arr[i].RefOffset);
                bw.Write(arr[i].Size);
                bw.Write(arr[i].RefCount);
            }
        }

        // ===== int[] / bool[] / ushort[] / string[] helpers (null = -1) =====

        public static void WriteIntArray(BinaryWriter bw, int[] arr)
        {
            if (arr == null) { bw.Write(-1); return; }
            bw.Write(arr.Length);
            for (int i = 0; i < arr.Length; i++) bw.Write(arr[i]);
        }

        public static void WriteUShortArray(BinaryWriter bw, ushort[] arr)
        {
            if (arr == null) { bw.Write(-1); return; }
            bw.Write(arr.Length);
            for (int i = 0; i < arr.Length; i++) bw.Write(arr[i]);
        }

        public static void WriteBoolArray(BinaryWriter bw, bool[] arr)
        {
            if (arr == null) { bw.Write(-1); return; }
            bw.Write(arr.Length);
            for (int i = 0; i < arr.Length; i++) bw.Write(arr[i]);
        }

        public static void WriteStringArray(BinaryWriter bw, string[] arr)
        {
            if (arr == null) { bw.Write(-1); return; }
            bw.Write(arr.Length);
            for (int i = 0; i < arr.Length; i++) bw.Write(arr[i] ?? string.Empty);
        }

        // SwitchTargets: Dictionary<int,int[]> as (count, (key, arrLen, arr)[]).
        public static void WriteSwitchTargets(BinaryWriter bw, Dictionary<int, int[]> st)
        {
            if (st == null || st.Count == 0) { bw.Write(0); return; }
            bw.Write(st.Count);
            foreach (var kv in st)
            {
                bw.Write(kv.Key);
                var arr = kv.Value;
                bw.Write(arr == null ? 0 : arr.Length);
                if (arr != null) for (int i = 0; i < arr.Length; i++) bw.Write(arr[i]);
            }
        }

        // ===== NeoCallParamMapRecord (ushort[]s + bool[]s + System.Type[] aqnames) =====

        public static void WriteNeoCallParamMap(BinaryWriter bw, NeoCallParamMapRecord m)
        {
            WriteUShortArray(bw, m.PrimitiveSrc);
            WriteUShortArray(bw, m.PrimitiveDst);
            WriteUShortArray(bw, m.PrimitiveSize);
            WriteUShortArray(bw, m.RefSrc);
            WriteUShortArray(bw, m.RefDst);
            WriteBoolArray(bw, m.PrimitiveByRefSrc);
            WriteBoolArray(bw, m.PrimitiveByRefWriteBack);
            // System.Type[] -> assembly-qualified names (null/empty for no type).
            if (m.PrimitiveByRefElemTypeAqName == null) { bw.Write(-1); return; }
            bw.Write(m.PrimitiveByRefElemTypeAqName.Length);
            for (int i = 0; i < m.PrimitiveByRefElemTypeAqName.Length; i++)
                bw.Write(m.PrimitiveByRefElemTypeAqName[i] ?? string.Empty);
        }

        // ===== NeoExceptionHandlerRecord =====

        public static void WriteNeoExceptionHandler(BinaryWriter bw, NeoExceptionHandlerRecord eh)
        {
            bw.Write(eh.TryStartIdx);
            bw.Write(eh.TryEndIdx);
            bw.Write(eh.HandlerStartIdx);
            bw.Write(eh.HandlerEndIdx);
            bw.Write(eh.FilterIdx);
            bw.Write(eh.HandlerType);
            bw.Write(eh.CatchTypeRefIdx);
        }

        // ===== NeoMethodDefRecord (assemble already-built record -> bytes) =====

        public static void WriteMethodDef(BinaryWriter bw, NeoMethodDefRecord md)
        {
            bw.Write(md.MethodRefIdx);
            WriteOpCodeRArray(bw, md.NeoExecuteBody);
            WriteStackSlotInfoArray(bw, md.LocalInfos);
            WriteStackSlotInfoArray(bw, md.ParamInfos);
            bw.Write(md.TotalStructSize);
            bw.Write(md.TotalRefSize);
            bw.Write(md.ParamPrimitiveSize);
            bw.Write(md.ParamReferenceCount);
            bw.Write(md.LocalsPrimitiveSize);
            bw.Write(md.LocalsReferenceCount);
            bw.Write(md.ReturnPrimitiveSize);
            bw.Write(md.ReturnRefCount);
            bw.Write(md.StackRegisterCount);
            WriteBoolArray(bw, md.LocalIsReference);
            bw.Write(md.NeoCatchExceptionRegIndex);
            bw.Write(md.NeoCatchExceptionByteOffset);
            bw.Write(md.NeoCatchExceptionRefOffset);
            WriteSwitchTargetPairs(bw, md.SwitchTargets);
            WriteNeoCallParamMaps(bw, md.NeoCallParams);
            WriteNeoExceptionHandlers(bw, md.ExceptionHandlers);
            // NOTE: CodeBody (register-index; inliner/debugger only) and Symbols
            // (Cecil-Instruction-keyed; not serializable) are intentionally NOT
            // serialized here -- ExecuteNeo runs against NeoExecuteBody. See D3.
        }

        static void WriteSwitchTargetPairs(BinaryWriter bw, KeyValuePair<int, int[]>[] pairs)
        {
            if (pairs == null) { bw.Write(0); return; }
            bw.Write(pairs.Length);
            for (int i = 0; i < pairs.Length; i++)
            {
                bw.Write(pairs[i].Key);
                var arr = pairs[i].Value;
                bw.Write(arr == null ? 0 : arr.Length);
                if (arr != null) for (int j = 0; j < arr.Length; j++) bw.Write(arr[j]);
            }
        }

        public static void WriteNeoCallParamMaps(BinaryWriter bw, NeoCallParamMapRecord[] maps)
        {
            if (maps == null) { bw.Write(-1); return; }
            bw.Write(maps.Length);
            for (int i = 0; i < maps.Length; i++) WriteNeoCallParamMap(bw, maps[i]);
        }

        public static void WriteNeoExceptionHandlers(BinaryWriter bw, NeoExceptionHandlerRecord[] ehs)
        {
            if (ehs == null) { bw.Write(-1); return; }
            bw.Write(ehs.Length);
            for (int i = 0; i < ehs.Length; i++) WriteNeoExceptionHandler(bw, ehs[i]);
        }

        // ===== NeoFieldLayoutRecord / NeoInterfaceEntryRecord =====

        public static void WriteFieldLayout(BinaryWriter bw, NeoFieldLayoutRecord f)
        {
            bw.Write(f.FieldRefIdx);
            bw.Write(f.PrimitiveOffset);
            bw.Write(f.ReferenceOffset);
        }

        public static void WriteInterfaceEntry(BinaryWriter bw, NeoInterfaceEntryRecord e)
        {
            bw.Write(e.InterfaceTypeRefIdx);
            bw.Write(e.VTableOffset);
            WriteStringArray(bw, e.MethodSlotKeys);
            WriteIntArray(bw, e.ClassSlotRemap);
        }

        // ===== NeoTypeDefRecord =====

        public static void WriteTypeDef(BinaryWriter bw, NeoTypeDefRecord td)
        {
            bw.Write(td.TypeRefIdx);
            bw.Write(td.BaseTypeRefIdx);
            bw.Write(td.TotalPrimitiveSize);
            bw.Write(td.TotalReferenceCount);
            bw.Write(td.StaticTotalPrimitiveSize);
            bw.Write(td.StaticTotalReferenceCount);
            if (td.Fields == null) bw.Write(-1);
            else
            {
                bw.Write(td.Fields.Length);
                for (int i = 0; i < td.Fields.Length; i++) WriteFieldLayout(bw, td.Fields[i]);
            }
            WriteIntArray(bw, td.VTableMethodRefIdxs);
            if (td.Interfaces == null) bw.Write(-1);
            else
            {
                bw.Write(td.Interfaces.Length);
                for (int i = 0; i < td.Interfaces.Length; i++) WriteInterfaceEntry(bw, td.Interfaces[i]);
            }
            bw.Write(td.StaticCtorMethodRefIdx);
        }

        // ===== NeoPatchEntryRecord / NeoTemplateRecord =====

        public static void WritePatchEntry(BinaryWriter bw, NeoPatchEntryRecord pe)
        {
            bw.Write(pe.InstrIdx);
            bw.Write(pe.Field);
            bw.Write(pe.Kind);
            bw.Write(pe.GenericParamIdx);
            bw.Write(pe.TokenRefIdx);
            bw.Write(pe.CecilTokenKind);
        }

        public static void WriteTemplate(BinaryWriter bw, NeoTemplateRecord t)
        {
            bw.Write(t.DefinitionMethodRefIdx);
            WriteOpCodeRArray(bw, t.TemplateBody);
            if (t.Patches == null) bw.Write(-1);
            else
            {
                bw.Write(t.Patches.Length);
                for (int i = 0; i < t.Patches.Length; i++) WritePatchEntry(bw, t.Patches[i]);
            }
            bw.Write(t.LocVarRegStart);
            bw.Write(t.TotalRegCnt);
            bw.Write(t.NeoCatchExRegFinal);
            bw.Write(t.StackRegisterCount);
            bw.Write(t.VarCnt);
            bw.Write(t.InitObjPrefixLength);
            WriteIntArray(bw, t.InitObjPrefixRegisters);
            WriteIntArray(bw, t.VariableTypeRefIdxs);
            WriteIntArray(bw, t.ConstrainedTypeRefIdxs);
            WriteIntArray(bw, t.ConstrainedMethodRefIdxs);
            WriteSwitchTargetPairs(bw, t.SwitchTargets);
            // NOTE: Symbols / Addr (Cecil-Instruction-keyed; not serializable) and
            // RefBody / RefBodyAddr (runtime cache, rebuilt by Step 25's loader)
            // are intentionally NOT serialized here. See D5.
        }

        // ===== Reference tables (reused HybridPatch *PatchInfo) =====

        public static void WriteStringTable(BinaryWriter bw, string[] strings)
        {
            bw.Write(strings == null ? 0 : strings.Length);
            for (int i = 0; i < (strings?.Length ?? 0); i++) bw.Write(strings[i]);
        }

        public static void WriteTypeRefTable(BinaryWriter bw, TypeReferencePatchInfo[] refs, NeoTypeRefKind[] kinds)
        {
            int n = refs == null ? 0 : refs.Length;
            bw.Write(n);
            for (int i = 0; i < n; i++)
            {
                bw.Write((byte)(kinds != null && i < kinds.Length ? kinds[i] : NeoTypeRefKind.Il));
                refs[i].WriteToStream(bw);
            }
        }

        public static void WriteMethodRefTable(BinaryWriter bw, MethodReferencePatchInfo[] refs)
        {
            int n = refs == null ? 0 : refs.Length;
            bw.Write(n);
            for (int i = 0; i < n; i++) refs[i].WriteToStream(bw);
        }

        public static void WriteFieldRefTable(BinaryWriter bw, FieldReferencePatchInfo[] refs)
        {
            int n = refs == null ? 0 : refs.Length;
            bw.Write(n);
            for (int i = 0; i < n; i++) refs[i].WriteToStream(bw);
        }

        // ===== Top-level Write: header + 7 tables =====
        //
        // V1 writes tables sequentially into the stream and records each table's
        // start offset for the header. The header is written FIRST with zero
        // offsets into a prefix buffer, tables follow, then the header offsets
        // are patched by re-emitting the header at the head of a second buffer.
        // For V1 (host-side MemoryStream) we buffer the table bytes, capture the
        // running offset, and emit header + tables in one pass.

        public void Write(ILType[] types, ILMethod[] methods, GenericMethodTemplate[] templates, Stream stream)
        {
            if (types == null) types = Array.Empty<ILType>();
            if (methods == null) methods = Array.Empty<ILMethod>();
            if (templates == null) templates = Array.Empty<GenericMethodTemplate>();

            // Build internalRefs (every type/method/field def in the serialized
            // set is internal). The policy does not affect V1 roundtrip (same
            // process) but mirrors HybridPatch for Step 24/25.
            var internalRefs = new HashSet<MemberReference>();
            foreach (var t in types)
            {
                internalRefs.Add(t.TypeDefinition);
                foreach (var f in t.TypeDefinition.Fields) internalRefs.Add(f);
                foreach (var m in t.TypeDefinition.Methods) internalRefs.Add(m);
            }
            var builder = new NeoRefTableBuilder(internalRefs);
            var module = types.Length > 0 ? types[0].AppDomain.LoadedModules[0] : null;

            // Assemble TypeDefs + MethodDefs + Templates (this also populates the
            // reference tables via the builder).
            var typeDefs = new NeoTypeDefRecord[types.Length];
            for (int i = 0; i < types.Length; i++)
                typeDefs[i] = BuildTypeDef(types[i], builder, module);

            var methodDefs = new NeoMethodDefRecord[methods.Length];
            var addrList = new Dictionary<Instruction, int>[methods.Length];
            for (int i = 0; i < methods.Length; i++)
            {
                var frame = CompileFresh(methods[i], out var addr);
                addrList[i] = addr;
                methodDefs[i] = BuildMethodDef(methods[i], frame, addr, builder, module);
            }

            var templateRecs = new NeoTemplateRecord[templates.Length];
            for (int i = 0; i < templates.Length; i++)
                templateRecs[i] = BuildTemplate(templates[i], builder, module);

            WriteModel(stream, builder, typeDefs, methodDefs, templateRecs);
        }

        void WriteModel(Stream stream, NeoRefTableBuilder b,
            NeoTypeDefRecord[] typeDefs, NeoMethodDefRecord[] methodDefs, NeoTemplateRecord[] templates)
        {
            // Build every table blob first (in memory), then compute offsets and
            // emit header + tables in one clean pass. This avoids any seek/patch
            // collision between the header and the first table's bytes.
            byte[] stringBlob = Buf(WriteStringTable, b.Strings);
            byte[] typeRefBlob = BufTypeRefs(b.TypeRefs, b.TypeRefKinds);
            byte[] methodRefBlob = BufMethodRefs(b.MethodRefs);
            byte[] fieldRefBlob = BufFieldRefs(b.FieldRefs);
            byte[] typeDefBlob = BufTypeDefs(typeDefs);
            byte[] methodDefBlob = BufMethodDefs(methodDefs);
            byte[] templateBlob = BufTemplates(templates);

            // Header fixed size: int + short + byte + byte + 7 ints = 4+2+1+1+28 = 36.
            const int HeaderSize = 4 + 2 + 1 + 1 + sizeof(int) * NeoAssemblyFormat.TableCount;
            var header = NeoHeader.Create();
            int cursor = HeaderSize;
            header.TableOffsets[(int)NeoTableId.String] = cursor; cursor += stringBlob.Length;
            header.TableOffsets[(int)NeoTableId.TypeRef] = cursor; cursor += typeRefBlob.Length;
            header.TableOffsets[(int)NeoTableId.MethodRef] = cursor; cursor += methodRefBlob.Length;
            header.TableOffsets[(int)NeoTableId.FieldRef] = cursor; cursor += fieldRefBlob.Length;
            header.TableOffsets[(int)NeoTableId.TypeDef] = cursor; cursor += typeDefBlob.Length;
            header.TableOffsets[(int)NeoTableId.MethodDef] = cursor; cursor += methodDefBlob.Length;
            header.TableOffsets[(int)NeoTableId.Template] = cursor;

            using (var bw = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(header.Magic);
                bw.Write(header.Version);
                bw.Write(header.Endianness);
                bw.Write(header.Reserved);
                for (int i = 0; i < NeoAssemblyFormat.TableCount; i++) bw.Write(header.TableOffsets[i]);
                bw.Write(stringBlob);
                bw.Write(typeRefBlob);
                bw.Write(methodRefBlob);
                bw.Write(fieldRefBlob);
                bw.Write(typeDefBlob);
                bw.Write(methodDefBlob);
                bw.Write(templateBlob);
            }
        }

        static byte[] Buf(Action<BinaryWriter, string[]> write, string[] arg)
        {
            using (var ms = new MemoryStream())
            using (var tw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                write(tw, arg);
                return ms.ToArray();
            }
        }

        static byte[] BufTypeRefs(TypeReferencePatchInfo[] refs, NeoTypeRefKind[] kinds)
        {
            using (var ms = new MemoryStream())
            using (var tw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                WriteTypeRefTable(tw, refs, kinds);
                return ms.ToArray();
            }
        }

        static byte[] BufMethodRefs(MethodReferencePatchInfo[] refs)
        {
            using (var ms = new MemoryStream())
            using (var tw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                WriteMethodRefTable(tw, refs);
                return ms.ToArray();
            }
        }

        static byte[] BufFieldRefs(FieldReferencePatchInfo[] refs)
        {
            using (var ms = new MemoryStream())
            using (var tw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                WriteFieldRefTable(tw, refs);
                return ms.ToArray();
            }
        }

        static byte[] BufTypeDefs(NeoTypeDefRecord[] typeDefs)
        {
            using (var ms = new MemoryStream())
            using (var tw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                tw.Write(typeDefs.Length);
                for (int i = 0; i < typeDefs.Length; i++) WriteTypeDef(tw, typeDefs[i]);
                return ms.ToArray();
            }
        }

        static byte[] BufMethodDefs(NeoMethodDefRecord[] methodDefs)
        {
            using (var ms = new MemoryStream())
            using (var tw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                tw.Write(methodDefs.Length);
                for (int i = 0; i < methodDefs.Length; i++) WriteMethodDef(tw, methodDefs[i]);
                return ms.ToArray();
            }
        }

        static byte[] BufTemplates(NeoTemplateRecord[] templates)
        {
            using (var ms = new MemoryStream())
            using (var tw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                tw.Write(templates.Length);
                for (int i = 0; i < templates.Length; i++) WriteTemplate(tw, templates[i]);
                return ms.ToArray();
            }
        }

        // ===== record assembly (in-memory -> record, using the builder) =====

        public static NeoMethodDefRecord BuildMethodDef(ILMethod method, CompiledFrame frame,
            Dictionary<Instruction, int> addr, NeoRefTableBuilder b, ModuleDefinition module)
        {
            var rec = new NeoMethodDefRecord();
            rec.MethodRefIdx = b.IndexMethodRef(method, module);
            rec.NeoExecuteBody = frame.NeoExecuteBody;
            rec.LocalInfos = frame.LocalInfos;
            rec.ParamInfos = frame.ParamInfos;
            rec.TotalStructSize = frame.TotalStructSize;
            rec.TotalRefSize = frame.TotalRefSize;
            rec.ParamPrimitiveSize = frame.ParamPrimitiveSize;
            rec.ParamReferenceCount = frame.ParamReferenceCount;
            rec.LocalsPrimitiveSize = frame.LocalsPrimitiveSize;
            rec.LocalsReferenceCount = frame.LocalsReferenceCount;
            rec.ReturnPrimitiveSize = frame.ReturnPrimitiveSize;
            rec.ReturnRefCount = frame.ReturnRefCount;
            rec.StackRegisterCount = frame.StackRegisterCount;
            rec.LocalIsReference = frame.LocalIsReference;
            rec.NeoCatchExceptionRegIndex = frame.NeoCatchExceptionRegIndex;
            rec.NeoCatchExceptionByteOffset = frame.NeoCatchExceptionByteOffset;
            rec.NeoCatchExceptionRefOffset = frame.NeoCatchExceptionRefOffset;
            rec.SwitchTargets = ToPairs(frame.SwitchTargets);
            rec.NeoCallParams = BuildCallParamMaps(frame.NeoCallParams);
            rec.ExceptionHandlers = BuildExceptionHandlers(method, addr, b);
            return rec;
        }

        static KeyValuePair<int, int[]>[] ToPairs(Dictionary<int, int[]> st)
        {
            if (st == null || st.Count == 0) return null;
            var arr = new KeyValuePair<int, int[]>[st.Count];
            int i = 0;
            foreach (var kv in st) { arr[i++] = new KeyValuePair<int, int[]>(kv.Key, kv.Value); }
            return arr;
        }

        static NeoCallParamMapRecord[] BuildCallParamMaps(NeoCallParamMap[] maps)
        {
            if (maps == null) return null;
            var res = new NeoCallParamMapRecord[maps.Length];
            for (int i = 0; i < maps.Length; i++)
            {
                var m = maps[i];
                var r = new NeoCallParamMapRecord();
                r.PrimitiveSrc = m.PrimitiveSrc;
                r.PrimitiveDst = m.PrimitiveDst;
                r.PrimitiveSize = m.PrimitiveSize;
                r.RefSrc = m.RefSrc;
                r.RefDst = m.RefDst;
                r.PrimitiveByRefSrc = m.PrimitiveByRefSrc;
                r.PrimitiveByRefWriteBack = m.PrimitiveByRefWriteBack;
                // System.Type[] -> assembly-qualified names. null/empty is the
                // canonical "no element type" marker (a frame-native byref or a
                // non-byref slot), so normalize null -> "" for an exact roundtrip
                // (the reader treats "" as null via ResolveAqName).
                if (m.PrimitiveByRefElemType == null) r.PrimitiveByRefElemTypeAqName = null;
                else
                {
                    r.PrimitiveByRefElemTypeAqName = new string[m.PrimitiveByRefElemType.Length];
                    for (int j = 0; j < m.PrimitiveByRefElemType.Length; j++)
                    {
                        var t = m.PrimitiveByRefElemType[j];
                        r.PrimitiveByRefElemTypeAqName[j] = t == null ? string.Empty : (t.AssemblyQualifiedName ?? string.Empty);
                    }
                }
                res[i] = r;
            }
            return res;
        }

        // Resolve the Cecil ExceptionHandler collection to body-index records
        // using the JIT-time addr[] map (Cecil Instruction -> body index). The
        // Cecil-keyed addr[] itself is not serializable; the resolved indices
        // are. Catch type -> TypeRef index (a CLR catch type routes through the
        // CLR IType -> still a TypeReferencePatchInfo in the TypeRef table).
        static NeoExceptionHandlerRecord[] BuildExceptionHandlers(ILMethod method,
            Dictionary<Instruction, int> addr, NeoRefTableBuilder b)
        {
            var def = method.Definition;
            if (def == null || !def.HasBody || !def.Body.HasExceptionHandlers) return null;
            var ehs = def.Body.ExceptionHandlers;
            var res = new NeoExceptionHandlerRecord[ehs.Count];
            for (int i = 0; i < ehs.Count; i++)
            {
                var eh = ehs[i];
                var r = new NeoExceptionHandlerRecord();
                r.TryStartIdx = AddrOf(addr, eh.TryStart);
                r.TryEndIdx = AddrOf(addr, eh.TryEnd);
                r.HandlerStartIdx = AddrOf(addr, eh.HandlerStart);
                r.HandlerEndIdx = AddrOf(addr, eh.HandlerEnd);
                r.FilterIdx = eh.FilterStart != null ? AddrOf(addr, eh.FilterStart) : -1;
                r.HandlerType = (int)eh.HandlerType;
                r.CatchTypeRefIdx = eh.CatchType != null
                    ? b.IndexTypeRef(eh.CatchType, ResolveCatchType(method, eh.CatchType))
                    : -1;
                res[i] = r;
            }
            return res;
        }

        static int AddrOf(Dictionary<Instruction, int> addr, Instruction ins)
        {
            if (ins == null) return -1;
            if (addr != null && addr.TryGetValue(ins, out int idx)) return idx;
            return -1;
        }

        static IType ResolveCatchType(ILMethod method, TypeReference tr)
        {
            // Best-effort IL/CLR discrimination for the TypeRef Kind byte. There
            // is no AppDomain.GetType(TypeReference) overload; resolve by full
            // name through LoadedTypes (an IL catch type is loaded; a CLR catch
            // type e.g. System.Exception is not). The Kind byte roundtrips
            // verbatim regardless (V1 exactness does not depend on it); Step 25
            // re-resolves the catch type for runtime dispatch.
            if (tr == null) return null;
            try
            {
                if (method.AppDomain.LoadedTypes.TryGetValue(tr.FullName, out var it))
                    return it;
            }
            catch { }
            return null;
        }

        public static NeoTypeDefRecord BuildTypeDef(ILType type, NeoRefTableBuilder b, ModuleDefinition module)
        {
            var rec = new NeoTypeDefRecord();
            rec.TypeRefIdx = b.IndexTypeRef(type.TypeReference, type);
            rec.BaseTypeRefIdx = type.BaseType is ILType bt ? b.IndexTypeRef(bt.TypeReference, bt) : -1;
            rec.TotalPrimitiveSize = type.TotalPrimitiveSize;
            rec.TotalReferenceCount = type.TotalReferenceCount;
            rec.StaticTotalPrimitiveSize = type.StaticTotalPrimitiveSize;
            rec.StaticTotalReferenceCount = type.StaticTotalReferenceCount;
            rec.Fields = BuildFieldLayouts(type, b);
            rec.VTableMethodRefIdxs = BuildVTable(type, b, module);
            rec.Interfaces = BuildInterfaces(type, b);
            rec.StaticCtorMethodRefIdx = BuildStaticCtorRef(type, b, module);
            return rec;
        }

        static NeoFieldLayoutRecord[] BuildFieldLayouts(ILType type, NeoRefTableBuilder b)
        {
            int start = type.FieldStartIndex;
            int total = type.TotalFieldCount;
            int own = total - start;
            if (own <= 0) return null;
            var res = new NeoFieldLayoutRecord[own];
            for (int i = 0; i < own; i++)
            {
                int fieldIdx = start + i;
                var fieldType = type.GetField(fieldIdx, out FieldReference fr);
                var off = type.GetFieldOffset(fieldIdx);
                res[i] = new NeoFieldLayoutRecord
                {
                    FieldRefIdx = b.IndexFieldRef(fr),
                    PrimitiveOffset = off.PrimitiveOffset,
                    ReferenceOffset = off.ReferenceOffset,
                };
            }
            return res;
        }

        static int[] BuildVTable(ILType type, NeoRefTableBuilder b, ModuleDefinition module)
        {
            var vt = type.NeoVTable;
            if (vt == null || vt.Length == 0) return null;
            var res = new int[vt.Length];
            for (int i = 0; i < vt.Length; i++) res[i] = b.IndexMethodRef(vt[i], module);
            return res;
        }

        static NeoInterfaceEntryRecord[] BuildInterfaces(ILType type, NeoRefTableBuilder b)
        {
            var map = type.NeoInterfaceMapForAOT;
            if (map == null || map.Length == 0) return null;
            var res = new NeoInterfaceEntryRecord[map.Length];
            for (int i = 0; i < map.Length; i++)
            {
                ref var e = ref map[i];
                // IL interface type -> its Cecil TypeReference -> TypeRef index.
                // CLR interface type -> -1 for V1 (CLR types index by assembly-
                // qualified name at Step 25; the slot layout is still recorded).
                var ilIface = e.InterfaceType as ILType;
                res[i] = new NeoInterfaceEntryRecord
                {
                    InterfaceTypeRefIdx = ilIface != null ? b.IndexTypeRef(ilIface.TypeReference, ilIface) : -1,
                    VTableOffset = e.VTableOffset,
                    MethodSlotKeys = e.MethodSlotKeys,
                    ClassSlotRemap = e.ClassSlotRemap,
                };
            }
            return res;
        }

        static int BuildStaticCtorRef(ILType type, NeoRefTableBuilder b, ModuleDefinition module)
        {
            var ctors = type.GetConstructors();
            if (ctors == null) return -1;
            foreach (var c in ctors)
            {
                if (c != null && c.IsStatic) return b.IndexMethodRef(c, module);
            }
            return -1;
        }

        public static NeoTemplateRecord BuildTemplate(GenericMethodTemplate tpl,
            NeoRefTableBuilder b, ModuleDefinition module)
        {
            var rec = new NeoTemplateRecord();
            rec.DefinitionMethodRefIdx = b.IndexMethodRef(tpl.Definition, module);
            rec.TemplateBody = tpl.TemplateBody;
            rec.Patches = BuildPatches(tpl.Patches, b);
            rec.LocVarRegStart = tpl.LocVarRegStart;
            rec.TotalRegCnt = tpl.TotalRegCnt;
            rec.NeoCatchExRegFinal = tpl.NeoCatchExRegFinal;
            rec.StackRegisterCount = tpl.StackRegisterCount;
            rec.VarCnt = tpl.VarCnt;
            rec.InitObjPrefixLength = tpl.InitObjPrefixLength;
            rec.InitObjPrefixRegisters = tpl.InitObjPrefixRegisters;
            rec.VariableTypeRefIdxs = IndexTypeRefs(tpl.VariableTypes, b);
            rec.ConstrainedTypeRefIdxs = IndexTypeRefs(tpl.ConstrainedTypeTokens, b);
            rec.ConstrainedMethodRefIdxs = IndexMethodRefs(tpl.ConstrainedMethodTokens, b);
            rec.SwitchTargets = ToPairs(tpl.SwitchTargets);
            return rec;
        }

        static NeoPatchEntryRecord[] BuildPatches(PatchEntry[] patches, NeoRefTableBuilder b)
        {
            if (patches == null) return null;
            var res = new NeoPatchEntryRecord[patches.Length];
            for (int i = 0; i < patches.Length; i++)
            {
                var p = patches[i];
                byte cecilKind;
                int tokenRefIdx;
                if (p.Kind == PatchKind.MethodToken)
                {
                    cecilKind = 1;
                    tokenRefIdx = p.CecilToken is MethodReference cmr ? b.IndexMethodRef(cmr) : -1;
                }
                else if (p.Kind == PatchKind.TypeToken)
                {
                    cecilKind = 0;
                    tokenRefIdx = p.CecilToken is TypeReference ctr ? b.IndexTypeRef(ctr) : -1;
                }
                else
                {
                    cecilKind = 2;
                    tokenRefIdx = -1;
                }
                res[i] = new NeoPatchEntryRecord
                {
                    InstrIdx = p.InstrIdx,
                    Field = (int)p.Field,
                    Kind = (int)p.Kind,
                    GenericParamIdx = p.GenericParamIdx,
                    TokenRefIdx = tokenRefIdx,
                    CecilTokenKind = cecilKind,
                };
            }
            return res;
        }

        static int[] IndexTypeRefs(TypeReference[] trs, NeoRefTableBuilder b)
        {
            if (trs == null) return null;
            var res = new int[trs.Length];
            for (int i = 0; i < trs.Length; i++) res[i] = trs[i] != null ? b.IndexTypeRef(trs[i]) : -1;
            return res;
        }

        static int[] IndexMethodRefs(MethodReference[] mrs, NeoRefTableBuilder b)
        {
            if (mrs == null) return null;
            var res = new int[mrs.Length];
            for (int i = 0; i < mrs.Length; i++) res[i] = mrs[i] != null ? b.IndexMethodRef(mrs[i]) : -1;
            return res;
        }

        // Compile a method fresh (per-occurrence JIT path -- the reference). The
        // addr[] map is returned for EH index resolution. Mirrors the Step-22
        // CompilePerOccurrenceNeoBody helper but returns the full frame + addr.
        public static CompiledFrame CompileFresh(ILMethod method, out Dictionary<Instruction, int> addr)
        {
            var jit = new JITCompiler(method.AppDomain, method.DeclearingType as ILType, method);
            addr = new Dictionary<Instruction, int>();
            var frame = new CompiledFrame();
            jit.Compile(addr, ref frame);
            return frame;
        }
    }
}
#endif
