#if ENABLE_NEO_MODE
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

using ILRuntime.CLR.Method;
using ILRuntime.CLR.TypeSystem;
using ILRuntime.Hybrid;
using ILRuntime.Mono.Cecil;
using ILRuntime.Runtime.Intepreter.OpCodes;
using ILRuntime.Runtime.Intepreter.RegisterVM;

namespace ILRuntime.Runtime.NeoAOT
{
    // ===== Step 23: the .neo BinaryReader deserializer =====
    //
    // Reads a .neo stream back into a NeoAssemblyModel. Pure-record IO is
    // exposed as static helpers mirroring NeoAssemblyWriter; the instance Read
    // validates the header + decodes all 7 tables. V1 (Step 23) builds the
    // model; V2 (Step 25) wires the model into ExecuteNeo (the runtime loader
    // re-resolves the reference tables to hash -> object maps then).

    internal static class NeoAssemblyReader
    {
        const int OpCodeRSize = 24;

        // ===== OpCodeR[] : RAW 24-byte little-endian (design D1) =====

        public static OpCodeR[] ReadOpCodeRArray(BinaryReader br)
        {
            int len = br.ReadInt32();
            if (len < 0) return null;
            if (len == 0) return Array.Empty<OpCodeR>();
            byte[] raw = br.ReadBytes(len * OpCodeRSize);
            OpCodeR[] arr = new OpCodeR[len];
            MemoryMarshal.Cast<byte, OpCodeR>(raw).CopyTo(arr);
            return arr;
        }

        // ===== StackSlotInfo[] =====

        public static StackSlotInfo[] ReadStackSlotInfoArray(BinaryReader br)
        {
            int len = br.ReadInt32();
            if (len < 0) return null;
            var arr = new StackSlotInfo[len];
            for (int i = 0; i < len; i++)
            {
                arr[i] = new StackSlotInfo
                {
                    Offset = br.ReadInt32(),
                    RefOffset = br.ReadInt32(),
                    Size = br.ReadInt32(),
                    RefCount = br.ReadInt32(),
                };
            }
            return arr;
        }

        // ===== primitive array helpers (null = -1) =====

        public static int[] ReadIntArray(BinaryReader br)
        {
            int len = br.ReadInt32();
            if (len < 0) return null;
            var arr = new int[len];
            for (int i = 0; i < len; i++) arr[i] = br.ReadInt32();
            return arr;
        }

        public static ushort[] ReadUShortArray(BinaryReader br)
        {
            int len = br.ReadInt32();
            if (len < 0) return null;
            var arr = new ushort[len];
            for (int i = 0; i < len; i++) arr[i] = br.ReadUInt16();
            return arr;
        }

        public static bool[] ReadBoolArray(BinaryReader br)
        {
            int len = br.ReadInt32();
            if (len < 0) return null;
            var arr = new bool[len];
            for (int i = 0; i < len; i++) arr[i] = br.ReadBoolean();
            return arr;
        }

        public static string[] ReadStringArray(BinaryReader br)
        {
            int len = br.ReadInt32();
            if (len < 0) return null;
            var arr = new string[len];
            for (int i = 0; i < len; i++) arr[i] = br.ReadString();
            return arr;
        }

        public static KeyValuePair<int, int[]>[] ReadSwitchTargetPairs(BinaryReader br)
        {
            int count = br.ReadInt32();
            if (count <= 0) return null;
            var arr = new KeyValuePair<int, int[]>[count];
            for (int i = 0; i < count; i++)
            {
                int key = br.ReadInt32();
                int arrLen = br.ReadInt32();
                int[] vals = arrLen > 0 ? new int[arrLen] : null;
                for (int j = 0; j < arrLen; j++) vals[j] = br.ReadInt32();
                arr[i] = new KeyValuePair<int, int[]>(key, vals);
            }
            return arr;
        }

        // ===== NeoCallParamMapRecord =====

        public static NeoCallParamMapRecord ReadNeoCallParamMap(BinaryReader br)
        {
            var r = new NeoCallParamMapRecord();
            r.PrimitiveSrc = ReadUShortArray(br);
            r.PrimitiveDst = ReadUShortArray(br);
            r.PrimitiveSize = ReadUShortArray(br);
            r.RefSrc = ReadUShortArray(br);
            r.RefDst = ReadUShortArray(br);
            r.PrimitiveByRefSrc = ReadBoolArray(br);
            r.PrimitiveByRefWriteBack = ReadBoolArray(br);
            int len = br.ReadInt32();
            if (len < 0) r.PrimitiveByRefElemTypeAqName = null;
            else
            {
                r.PrimitiveByRefElemTypeAqName = new string[len];
                for (int i = 0; i < len; i++) r.PrimitiveByRefElemTypeAqName[i] = br.ReadString();
            }
            return r;
        }

        public static NeoCallParamMapRecord[] ReadNeoCallParamMaps(BinaryReader br)
        {
            int len = br.ReadInt32();
            if (len < 0) return null;
            var arr = new NeoCallParamMapRecord[len];
            for (int i = 0; i < len; i++) arr[i] = ReadNeoCallParamMap(br);
            return arr;
        }

        public static NeoExceptionHandlerRecord ReadNeoExceptionHandler(BinaryReader br)
        {
            return new NeoExceptionHandlerRecord
            {
                TryStartIdx = br.ReadInt32(),
                TryEndIdx = br.ReadInt32(),
                HandlerStartIdx = br.ReadInt32(),
                HandlerEndIdx = br.ReadInt32(),
                FilterIdx = br.ReadInt32(),
                HandlerType = br.ReadInt32(),
                CatchTypeRefIdx = br.ReadInt32(),
            };
        }

        public static NeoExceptionHandlerRecord[] ReadNeoExceptionHandlers(BinaryReader br)
        {
            int len = br.ReadInt32();
            if (len < 0) return null;
            var arr = new NeoExceptionHandlerRecord[len];
            for (int i = 0; i < len; i++) arr[i] = ReadNeoExceptionHandler(br);
            return arr;
        }

        // ===== NeoMethodDefRecord =====

        public static NeoMethodDefRecord ReadMethodDef(BinaryReader br)
        {
            var md = new NeoMethodDefRecord();
            md.MethodRefIdx = br.ReadInt32();
            md.NeoExecuteBody = ReadOpCodeRArray(br);
            md.LocalInfos = ReadStackSlotInfoArray(br);
            md.ParamInfos = ReadStackSlotInfoArray(br);
            md.TotalStructSize = br.ReadInt32();
            md.TotalRefSize = br.ReadInt32();
            md.ParamPrimitiveSize = br.ReadInt32();
            md.ParamReferenceCount = br.ReadInt32();
            md.LocalsPrimitiveSize = br.ReadInt32();
            md.LocalsReferenceCount = br.ReadInt32();
            md.ReturnPrimitiveSize = br.ReadInt32();
            md.ReturnRefCount = br.ReadInt32();
            md.StackRegisterCount = br.ReadInt32();
            md.LocalIsReference = ReadBoolArray(br);
            md.NeoCatchExceptionRegIndex = br.ReadInt32();
            md.NeoCatchExceptionByteOffset = br.ReadInt32();
            md.NeoCatchExceptionRefOffset = br.ReadInt32();
            md.SwitchTargets = ReadSwitchTargetPairs(br);
            md.NeoCallParams = ReadNeoCallParamMaps(br);
            md.ExceptionHandlers = ReadNeoExceptionHandlers(br);
            return md;
        }

        // ===== FieldLayout / InterfaceEntry =====

        public static NeoFieldLayoutRecord ReadFieldLayout(BinaryReader br)
        {
            return new NeoFieldLayoutRecord
            {
                FieldRefIdx = br.ReadInt32(),
                PrimitiveOffset = br.ReadInt32(),
                ReferenceOffset = br.ReadInt32(),
            };
        }

        public static NeoInterfaceEntryRecord ReadInterfaceEntry(BinaryReader br)
        {
            var e = new NeoInterfaceEntryRecord
            {
                InterfaceTypeRefIdx = br.ReadInt32(),
                VTableOffset = br.ReadInt32(),
                MethodSlotKeys = ReadStringArray(br),
                ClassSlotRemap = ReadIntArray(br),
            };
            return e;
        }

        // ===== NeoTypeDefRecord =====

        public static NeoTypeDefRecord ReadTypeDef(BinaryReader br)
        {
            var td = new NeoTypeDefRecord();
            td.TypeRefIdx = br.ReadInt32();
            td.BaseTypeRefIdx = br.ReadInt32();
            td.TotalPrimitiveSize = br.ReadInt32();
            td.TotalReferenceCount = br.ReadInt32();
            td.StaticTotalPrimitiveSize = br.ReadInt32();
            td.StaticTotalReferenceCount = br.ReadInt32();
            int flen = br.ReadInt32();
            if (flen < 0) td.Fields = null;
            else
            {
                td.Fields = new NeoFieldLayoutRecord[flen];
                for (int i = 0; i < flen; i++) td.Fields[i] = ReadFieldLayout(br);
            }
            td.VTableMethodRefIdxs = ReadIntArray(br);
            int icnt = br.ReadInt32();
            if (icnt < 0) td.Interfaces = null;
            else
            {
                td.Interfaces = new NeoInterfaceEntryRecord[icnt];
                for (int i = 0; i < icnt; i++) td.Interfaces[i] = ReadInterfaceEntry(br);
            }
            td.StaticCtorMethodRefIdx = br.ReadInt32();
            return td;
        }

        // ===== PatchEntry / Template =====

        public static NeoPatchEntryRecord ReadPatchEntry(BinaryReader br)
        {
            return new NeoPatchEntryRecord
            {
                InstrIdx = br.ReadInt32(),
                Field = br.ReadInt32(),
                Kind = br.ReadInt32(),
                GenericParamIdx = br.ReadInt32(),
                TokenRefIdx = br.ReadInt32(),
                CecilTokenKind = br.ReadByte(),
            };
        }

        public static NeoTemplateRecord ReadTemplate(BinaryReader br)
        {
            var t = new NeoTemplateRecord();
            t.DefinitionMethodRefIdx = br.ReadInt32();
            t.TemplateBody = ReadOpCodeRArray(br);
            int plen = br.ReadInt32();
            if (plen < 0) t.Patches = null;
            else
            {
                t.Patches = new NeoPatchEntryRecord[plen];
                for (int i = 0; i < plen; i++) t.Patches[i] = ReadPatchEntry(br);
            }
            t.LocVarRegStart = br.ReadInt16();
            t.TotalRegCnt = br.ReadInt32();
            t.NeoCatchExRegFinal = br.ReadInt16();
            t.StackRegisterCount = br.ReadInt32();
            t.VarCnt = br.ReadInt32();
            t.InitObjPrefixLength = br.ReadInt32();
            t.InitObjPrefixRegisters = ReadIntArray(br);
            t.VariableTypeRefIdxs = ReadIntArray(br);
            t.ConstrainedTypeRefIdxs = ReadIntArray(br);
            t.ConstrainedMethodRefIdxs = ReadIntArray(br);
            t.SwitchTargets = ReadSwitchTargetPairs(br);
            return t;
        }

        // ===== Reference tables (reuse HybridPatch *PatchInfo.FromStream) =====

        public static string[] ReadStringTable(BinaryReader br)
        {
            int n = br.ReadInt32();
            var arr = new string[n];
            for (int i = 0; i < n; i++) arr[i] = br.ReadString();
            return arr;
        }

        public static TypeReferencePatchInfo[] ReadTypeRefTable(BinaryReader br, out NeoTypeRefKind[] kinds)
        {
            int n = br.ReadInt32();
            var arr = new TypeReferencePatchInfo[n];
            kinds = new NeoTypeRefKind[n];
            for (int i = 0; i < n; i++)
            {
                kinds[i] = (NeoTypeRefKind)br.ReadByte();
                arr[i] = TypeReferencePatchInfo.FromStream(br);
            }
            return arr;
        }

        public static MethodReferencePatchInfo[] ReadMethodRefTable(BinaryReader br)
        {
            int n = br.ReadInt32();
            var arr = new MethodReferencePatchInfo[n];
            for (int i = 0; i < n; i++) arr[i] = MethodReferencePatchInfo.FromStream(br);
            return arr;
        }

        public static FieldReferencePatchInfo[] ReadFieldRefTable(BinaryReader br)
        {
            int n = br.ReadInt32();
            var arr = new FieldReferencePatchInfo[n];
            for (int i = 0; i < n; i++) arr[i] = FieldReferencePatchInfo.FromStream(br);
            return arr;
        }

        // ===== Top-level Read -> NeoAssemblyModel =====

        public static NeoAssemblyModel Read(Stream stream)
        {
            var br = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            var model = new NeoAssemblyModel();
            // Header.
            int magic = br.ReadInt32();
            if (magic != NeoAssemblyFormat.Magic)
                throw new NotSupportedException($"Wrong .neo Magic: 0x{magic:X8} (expected 0x{NeoAssemblyFormat.Magic:X8})");
            short version = br.ReadInt16();
            if (version != NeoAssemblyFormat.Version)
                throw new NotSupportedException($"Unsupported .neo Version: {version} (expected {NeoAssemblyFormat.Version})");
            var header = NeoHeader.Create();
            header.Magic = magic;
            header.Version = version;
            header.Endianness = br.ReadByte();
            header.Reserved = br.ReadByte();
            for (int i = 0; i < NeoAssemblyFormat.TableCount; i++) header.TableOffsets[i] = br.ReadInt32();
            model.Header = header;

            // Tables follow in NeoTableId order. The offsets enable lazy access
            // (Step 25); V1 reads them sequentially.
            model.StringTable = ReadStringTable(br);
            model.TypeRefs = ReadTypeRefTable(br, out var kinds);
            model.TypeRefKinds = kinds;
            model.MethodRefs = ReadMethodRefTable(br);
            model.FieldRefs = ReadFieldRefTable(br);

            int tdCount = br.ReadInt32();
            model.TypeDefs = new NeoTypeDefRecord[tdCount];
            for (int i = 0; i < tdCount; i++) model.TypeDefs[i] = ReadTypeDef(br);

            int mdCount = br.ReadInt32();
            model.MethodDefs = new NeoMethodDefRecord[mdCount];
            for (int i = 0; i < mdCount; i++) model.MethodDefs[i] = ReadMethodDef(br);

            int tplCount = br.ReadInt32();
            model.Templates = new NeoTemplateRecord[tplCount];
            for (int i = 0; i < tplCount; i++) model.Templates[i] = ReadTemplate(br);

            return model;
        }

        // ===== V1 roundtrip helper: System.Type re-resolution =====
        //
        // PrimitiveByRefElemTypeAqName -> System.Type via Type.GetType(aqname).
        // CLR byref element types are name-stable; an unresolvable name yields
        // null (Step 25 re-resolves via the AppDomain). Used by the V1 comparator
        // + Step 25's loader.
        public static System.Type ResolveAqName(string aqName)
        {
            if (string.IsNullOrEmpty(aqName)) return null;
            try { return System.Type.GetType(aqName); } catch { return null; }
        }
    }
}
#endif
