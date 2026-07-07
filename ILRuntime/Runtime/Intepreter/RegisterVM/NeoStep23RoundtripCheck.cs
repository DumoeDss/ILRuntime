#if ENABLE_NEO_MODE && DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

using ILRuntime.CLR.Method;
using ILRuntime.CLR.TypeSystem;
using ILRuntime.Mono.Cecil;
using ILRuntime.Runtime.Intepreter.OpCodes;
using ILRuntime.Runtime.NeoAOT;

namespace ILRuntime.Runtime.Intepreter.RegisterVM
{
    /// <summary>
    /// Step 23 V1 roundtrip-equivalence self-check (host-side, DEBUG+Neo only).
    ///
    /// For each matrix method: compiles it via the Neo JIT, serializes the
    /// resulting CompiledFrame to a MemoryStream (NeoAssemblyWriter), deserializes
    /// it back (NeoAssemblyReader), and asserts the deserialized record EQUALS the
    /// original (OpCodeR[] byte-for-byte via raw-24-byte compare; StackSlotInfo[]
    /// field-for-field; all frame scalars; LocalIsReference; NeoCatchException*;
    /// SwitchTargets; NeoCallParamMap[]; ExceptionHandlers[]). Plus a TypeDef
    /// roundtrip (type layout identical) and a GenericMethodTemplate roundtrip
    /// (template faithful). This is the load-bearing correctness proof for the
    /// .neo format (V2 deserialize -> ExecuteNeo is Step 25).
    ///
    /// Invoked host-side (CLI special mode), NOT as an interpreted NeoStep test.
    /// </summary>
    public static class NeoStep23RoundtripCheck
    {
        public class Result
        {
            public int TotalCells;
            public int Passed;
            public int Failed;
            public List<string> Failures = new List<string>();
        }

        public static Result Run(ILRuntime.Runtime.Enviorment.AppDomain appdomain)
        {
            var res = new Result();
            if (!appdomain.LoadedTypes.TryGetValue("TestCases.NeoStep23RoundtripProbes", out var probesIType))
            {
                res.Failures.Add("TestCases.NeoStep23RoundtripProbes not loaded");
                res.TotalCells = 1; res.Failed = 1;
                return res;
            }
            var probesType = probesIType as ILType;
            if (probesType == null)
            {
                res.Failures.Add("NeoStep23RoundtripProbes is not an ILType");
                res.TotalCells = 1; res.Failed = 1;
                return res;
            }
            ModuleDefinition module = appdomain.LoadedModules.Count > 0 ? appdomain.LoadedModules[0] : null;

            // ===== Method cells: compile -> serialize -> deserialize -> compare =====
            // Minor-3: the matrix grew from 6 -> 10 methods (ClrByrefCall,
            // NestedEH, MultiConstrainedGeneric, ZeroLocals added). These also
            // feed the full-model Write/Read cell below.
            string[] methodNames = { "ProbeBasic", "GenericProbe", "TryCatchProbe", "MixedLocals", "ByrefParams", "SwitchProbe", "ClrByrefCall", "NestedEH", "MultiConstrainedGeneric", "ZeroLocals" };
            foreach (var mname in methodNames)
            {
                res.TotalCells++;
                var method = probesType.GetMethod(mname) as ILMethod;
                if (method == null) { res.Failures.Add($"method {mname} not found"); res.Failed++; continue; }
                try
                {
                    RoundtripMethod(appdomain, method, module, out int bodyLen, out string diff);
                    if (diff == null)
                    {
                        res.Passed++;
                        Console.WriteLine($"[NeoStep23] {mname}: roundtrip PASS len={bodyLen}");
                    }
                    else
                    {
                        res.Failed++;
                        res.Failures.Add($"{mname}: {diff}");
                        Console.WriteLine($"  [FAIL] {mname}: {diff}");
                    }
                }
                catch (Exception ex)
                {
                    res.Failed++;
                    res.Failures.Add($"{mname}: threw {ex.GetType().Name}: {ex.Message}");
                    Console.WriteLine($"  [FAIL] {mname}: threw {ex.GetType().Name}: {ex.Message}");
                }
            }

            // ===== TypeDef cells: serialize each type's metadata -> deserialize -> compare =====
            // Minor-3: a second TypeDef (NeoStep23MultiIfaceProbe, ifaces=2) joins
            // the shipped NeoStep23TypeDefProbe (ifaces=1) to cover the multi-entry
            // NeoInterfaceEntryRecord[] path.
            string[] typeDefNames = { "TestCases.NeoStep23TypeDefProbe", "TestCases.NeoStep23MultiIfaceProbe" };
            foreach (var tdName in typeDefNames)
            {
                var shortTdName = tdName.Substring(tdName.LastIndexOf('.') + 1);
                if (!appdomain.LoadedTypes.TryGetValue(tdName, out var tdIType) || !(tdIType is ILType tdType))
                {
                    res.TotalCells++;
                    res.Failed++; res.Failures.Add($"TypeDef {shortTdName}: not loaded / not an ILType");
                    Console.WriteLine($"  [FAIL] TypeDef {shortTdName}: not loaded / not an ILType");
                    continue;
                }
                res.TotalCells++;
                try
                {
                    var builder = new NeoRefTableBuilder();
                    var td = NeoAssemblyWriter.BuildTypeDef(tdType, builder, module);
                    byte[] blob;
                    using (var ms = new MemoryStream())
                    using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
                    {
                        NeoAssemblyWriter.WriteTypeDef(bw, td);
                        blob = ms.ToArray();
                    }
                    NeoTypeDefRecord td2;
                    using (var ms2 = new MemoryStream(blob))
                    using (var br = new BinaryReader(ms2, System.Text.Encoding.UTF8, leaveOpen: true))
                        td2 = NeoAssemblyReader.ReadTypeDef(br);
                    string diff = TypeDefsEqual(td, td2);
                    if (diff == null)
                    {
                        res.Passed++;
                        Console.WriteLine($"[NeoStep23] TypeDef {shortTdName}: roundtrip PASS fields={td.Fields?.Length ?? 0} vtable={td.VTableMethodRefIdxs?.Length ?? 0} ifaces={td.Interfaces?.Length ?? 0}");
                    }
                    else
                    {
                        res.Failed++; res.Failures.Add($"TypeDef {shortTdName}: {diff}");
                        Console.WriteLine($"  [FAIL] TypeDef {shortTdName}: {diff}");
                    }
                }
                catch (Exception ex)
                {
                    res.Failed++; res.Failures.Add($"TypeDef {shortTdName}: threw {ex.GetType().Name}: {ex.Message}");
                    Console.WriteLine($"  [FAIL] TypeDef {shortTdName}: threw {ex.GetType().Name}: {ex.Message}");
                }
            }

            // ===== Template cells: force-build each generic template -> serialize -> deserialize -> compare =====
            // Minor-3: a second template (MultiConstrainedGeneric, the Step-22
            // BLOCKER-1 constrained. T shape DOUBLED -> constrainedT=2,
            // constrainedM=2) joins the shipped GenericProbe template (1
            // constrained pair) to cover the multi-Constrained-pair template path.
            string[] templateMethodNames = { "GenericProbe", "MultiConstrainedGeneric" };
            foreach (var tname in templateMethodNames)
            {
                res.TotalCells++;
                try
                {
                    var genMethod = probesType.GetMethod(tname) as ILMethod;
                    var defForTemplate = genMethod != null && genMethod.GenericDefinition != null
                        ? genMethod.GenericDefinition : genMethod;
                    var template = defForTemplate != null
                        ? GenericMethodTemplateOps.ForceBuildTemplate(appdomain, probesType, defForTemplate) : null;
                    if (template == null)
                    {
                        res.Failed++; res.Failures.Add($"Template {tname}: ForceBuildTemplate returned null");
                        Console.WriteLine($"  [FAIL] Template {tname}: ForceBuildTemplate returned null");
                        continue;
                    }
                    var builder = new NeoRefTableBuilder();
                    var tr = NeoAssemblyWriter.BuildTemplate(template, builder, module);
                    NeoTemplateRecord tr2;
                    using (var ms = new MemoryStream())
                    using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
                    {
                        NeoAssemblyWriter.WriteTemplate(bw, tr);
                        using (var br = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true))
                        {
                            ms.Position = 0;
                            tr2 = NeoAssemblyReader.ReadTemplate(br);
                        }
                    }
                    string diff = TemplatesEqual(tr, tr2);
                    if (diff == null)
                    {
                        res.Passed++;
                        Console.WriteLine($"[NeoStep23] Template {tname}: roundtrip PASS patches={tr.Patches?.Length ?? 0} bodyLen={tr.TemplateBody?.Length ?? 0} constrainedT={tr.ConstrainedTypeRefIdxs?.Length ?? 0} constrainedM={tr.ConstrainedMethodRefIdxs?.Length ?? 0}");
                    }
                    else
                    {
                        res.Failed++; res.Failures.Add($"Template {tname}: {diff}");
                        Console.WriteLine($"  [FAIL] Template {tname}: {diff}");
                    }
                }
                catch (Exception ex)
                {
                    res.Failed++; res.Failures.Add($"Template {tname}: threw {ex.GetType().Name}: {ex.Message}");
                    Console.WriteLine($"  [FAIL] Template {tname}: threw {ex.GetType().Name}: {ex.Message}");
                }
            }

            // ===== Full-model cell: Write the whole probe type -> Read -> verify =====
            {
                res.TotalCells++;
                try
                {
                    var methodList = new List<ILMethod>();
                    foreach (var mn in methodNames)
                    {
                        var m = probesType.GetMethod(mn) as ILMethod;
                        if (m != null) methodList.Add(m);
                    }
                    // Minor-3: write BOTH typedefs (single-iface NeoStep23TypeDefProbe
                    // + multi-iface NeoStep23MultiIfaceProbe) so the whole-pipeline
                    // Write/Read also exercises the multi-TypeDef path (header
                    // offsets + TypeDef table for >1 type).
                    var typesList = new List<ILType>();
                    foreach (var tdn in typeDefNames)
                    {
                        if (appdomain.LoadedTypes.TryGetValue(tdn, out var tt) && tt is ILType tilt)
                            typesList.Add(tilt);
                    }
                    var writer = new NeoAssemblyWriter();
                    NeoAssemblyModel model;
                    using (var ms = new MemoryStream())
                    {
                        writer.Write(typesList.ToArray(), methodList.ToArray(), Array.Empty<GenericMethodTemplate>(), ms);
                        ms.Position = 0;
                        model = NeoAssemblyReader.Read(ms);
                    }
                    // Verify header + counts.
                    string diff = null;
                    if (model.Header.Magic != NeoAssemblyFormat.Magic) diff = $"magic 0x{model.Header.Magic:X8}";
                    else if (model.Header.Version != NeoAssemblyFormat.Version) diff = $"version {model.Header.Version}";
                    else if (model.MethodDefs.Length != methodList.Count) diff = $"methoddef count {model.MethodDefs.Length} vs {methodList.Count}";
                    if (diff == null)
                    {
                        // Cross-check: each NON-generic method body in the model is
                        // byte-equal to a fresh compile (deterministic). Open
                        // generic definitions are skipped here -- compiling an open
                        // definition is non-deterministic (the Step-22 documented
                        // quirk: it corrupts shared caches), and their body-level
                        // faithfulness is already proven by the per-record cell.
                        for (int i = 0; i < methodList.Count && diff == null; i++)
                        {
                            if (methodList[i].GenericParameterCount > 0) continue;
                            var fresh = NeoAssemblyWriter.CompileFresh(methodList[i], out _);
                            if (!OpCodeRsEqual(fresh.NeoExecuteBody, model.MethodDefs[i].NeoExecuteBody))
                                diff = $"body mismatch for {methodList[i].Name}";
                        }
                    }
                    if (diff == null)
                    {
                        res.Passed++;
                        Console.WriteLine($"[NeoStep23] Full-model Write/Read: PASS typedefs={model.TypeDefs.Length} methods={model.MethodDefs.Length} typerefs={model.TypeRefs.Length} methodrefs={model.MethodRefs.Length} strings={model.StringTable.Length}");
                    }
                    else
                    {
                        res.Failed++; res.Failures.Add($"FullModel: {diff}");
                        Console.WriteLine($"  [FAIL] FullModel: {diff}");
                    }
                }
                catch (Exception ex)
                {
                    res.Failed++; res.Failures.Add($"FullModel: threw {ex.GetType().Name}: {ex.Message}");
                    Console.WriteLine($"  [FAIL] FullModel: threw {ex.GetType().Name}: {ex.Message}");
                }
            }

            return res;
        }

        // ===== Method roundtrip: compile -> build record -> serialize -> read -> compare =====
        static void RoundtripMethod(ILRuntime.Runtime.Enviorment.AppDomain appdomain,
            ILMethod method, ModuleDefinition module, out int bodyLen, out string diff)
        {
            bodyLen = 0;
            var frame = NeoAssemblyWriter.CompileFresh(method, out var addr);
            var builder = new NeoRefTableBuilder();
            var md = NeoAssemblyWriter.BuildMethodDef(method, frame, addr, builder, module);
            NeoMethodDefRecord md2;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                NeoAssemblyWriter.WriteMethodDef(bw, md);
                ms.Position = 0;
                using (var br = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true))
                    md2 = NeoAssemblyReader.ReadMethodDef(br);
            }
            bodyLen = md.NeoExecuteBody?.Length ?? 0;
            diff = MethodDefsEqual(md, md2);
        }

        // ===== Comparators =====

        // OpCodeR[] byte-equality (raw 24-byte compare -- stronger than the
        // canonical 8-field BodiesEqual; catches any union-alias corruption).
        internal static bool OpCodeRsEqual(OpCodeR[] a, OpCodeR[] b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            if (a.Length == 0) return true;
            int bytes = a.Length * 24;
            ReadOnlySpan<byte> sa = MemoryMarshal.AsBytes<OpCodeR>(a.AsSpan());
            ReadOnlySpan<byte> sb = MemoryMarshal.AsBytes<OpCodeR>(b.AsSpan());
            return sa.SequenceEqual(sb);
        }

        // Step 24 (NeoStep24CliRoundtripCheck) reuses these comparators; they are
        // internal (additive visibility widening within this DEBUG+Neo self-check
        // file) so the Step-24 V1-A gate does not duplicate them.
        internal static string MethodDefsEqual(NeoMethodDefRecord a, NeoMethodDefRecord b)
        {
            // Minor-1: the identity ref-idx IS serialized (first int written)
            // + read (first int read), so it roundtrips positionally -- but the V1
            // gate must ASSERT it so a future write/read-order bug in the identity
            // idx cannot slip the gate. This is how the Step-25 loader maps a
            // MethodDef to its declaring MethodRef.
            if (a.MethodRefIdx != b.MethodRefIdx)
                return $"MethodRefIdx {a.MethodRefIdx} vs {b.MethodRefIdx}";
            if (!OpCodeRsEqual(a.NeoExecuteBody, b.NeoExecuteBody))
                return $"NeoExecuteBody mismatch (a={a.NeoExecuteBody?.Length ?? -1}, b={b.NeoExecuteBody?.Length ?? -1})";
            if (!SlotInfosEqual(a.LocalInfos, b.LocalInfos)) return "LocalInfos mismatch";
            if (!SlotInfosEqual(a.ParamInfos, b.ParamInfos)) return "ParamInfos mismatch";
            if (a.TotalStructSize != b.TotalStructSize) return $"TotalStructSize {a.TotalStructSize} vs {b.TotalStructSize}";
            if (a.TotalRefSize != b.TotalRefSize) return $"TotalRefSize {a.TotalRefSize} vs {b.TotalRefSize}";
            if (a.ParamPrimitiveSize != b.ParamPrimitiveSize) return $"ParamPrimitiveSize {a.ParamPrimitiveSize} vs {b.ParamPrimitiveSize}";
            if (a.ParamReferenceCount != b.ParamReferenceCount) return $"ParamReferenceCount {a.ParamReferenceCount} vs {b.ParamReferenceCount}";
            if (a.LocalsPrimitiveSize != b.LocalsPrimitiveSize) return $"LocalsPrimitiveSize {a.LocalsPrimitiveSize} vs {b.LocalsPrimitiveSize}";
            if (a.LocalsReferenceCount != b.LocalsReferenceCount) return $"LocalsReferenceCount {a.LocalsReferenceCount} vs {b.LocalsReferenceCount}";
            if (a.ReturnPrimitiveSize != b.ReturnPrimitiveSize) return $"ReturnPrimitiveSize {a.ReturnPrimitiveSize} vs {b.ReturnPrimitiveSize}";
            if (a.ReturnRefCount != b.ReturnRefCount) return $"ReturnRefCount {a.ReturnRefCount} vs {b.ReturnRefCount}";
            if (a.StackRegisterCount != b.StackRegisterCount) return $"StackRegisterCount {a.StackRegisterCount} vs {b.StackRegisterCount}";
            if (!BoolArraysEqual(a.LocalIsReference, b.LocalIsReference)) return "LocalIsReference mismatch";
            if (a.NeoCatchExceptionRegIndex != b.NeoCatchExceptionRegIndex) return $"NeoCatchExceptionRegIndex {a.NeoCatchExceptionRegIndex} vs {b.NeoCatchExceptionRegIndex}";
            if (a.NeoCatchExceptionByteOffset != b.NeoCatchExceptionByteOffset) return $"NeoCatchExceptionByteOffset {a.NeoCatchExceptionByteOffset} vs {b.NeoCatchExceptionByteOffset}";
            if (a.NeoCatchExceptionRefOffset != b.NeoCatchExceptionRefOffset) return $"NeoCatchExceptionRefOffset {a.NeoCatchExceptionRefOffset} vs {b.NeoCatchExceptionRefOffset}";
            if (!SwitchTargetsEqual(a.SwitchTargets, b.SwitchTargets)) return "SwitchTargets mismatch";
            if (!CallParamMapsEqual(a.NeoCallParams, b.NeoCallParams)) return "NeoCallParams mismatch";
            if (!ExceptionHandlersEqual(a.ExceptionHandlers, b.ExceptionHandlers)) return "ExceptionHandlers mismatch";
            return null;
        }

        static bool SlotInfosEqual(StackSlotInfo[] a, StackSlotInfo[] b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].Offset != b[i].Offset) return false;
                if (a[i].RefOffset != b[i].RefOffset) return false;
                if (a[i].Size != b[i].Size) return false;
                if (a[i].RefCount != b[i].RefCount) return false;
            }
            return true;
        }

        static bool BoolArraysEqual(bool[] a, bool[] b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static bool IntArraysEqual(int[] a, int[] b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static bool UShortArraysEqual(ushort[] a, ushort[] b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static bool StringArraysEqual(string[] a, string[] b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static bool SwitchTargetsEqual(KeyValuePair<int, int[]>[] a, KeyValuePair<int, int[]>[] b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            // Order-independent compare (Dictionary iteration order is not
            // guaranteed to match across two captures): build key->vals maps.
            var da = new Dictionary<int, int[]>();
            foreach (var kv in a) da[kv.Key] = kv.Value;
            foreach (var kv in b)
            {
                if (!da.TryGetValue(kv.Key, out var va)) return false;
                if (!IntArraysEqual(va, kv.Value)) return false;
            }
            return true;
        }

        static bool CallParamMapsEqual(NeoCallParamMapRecord[] a, NeoCallParamMapRecord[] b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (!UShortArraysEqual(a[i].PrimitiveSrc, b[i].PrimitiveSrc)) return false;
                if (!UShortArraysEqual(a[i].PrimitiveDst, b[i].PrimitiveDst)) return false;
                if (!UShortArraysEqual(a[i].PrimitiveSize, b[i].PrimitiveSize)) return false;
                if (!UShortArraysEqual(a[i].RefSrc, b[i].RefSrc)) return false;
                if (!UShortArraysEqual(a[i].RefDst, b[i].RefDst)) return false;
                if (!BoolArraysEqual(a[i].PrimitiveByRefSrc, b[i].PrimitiveByRefSrc)) return false;
                if (!BoolArraysEqual(a[i].PrimitiveByRefWriteBack, b[i].PrimitiveByRefWriteBack)) return false;
                if (!StringArraysEqual(a[i].PrimitiveByRefElemTypeAqName, b[i].PrimitiveByRefElemTypeAqName)) return false;
            }
            return true;
        }

        static bool ExceptionHandlersEqual(NeoExceptionHandlerRecord[] a, NeoExceptionHandlerRecord[] b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].TryStartIdx != b[i].TryStartIdx) return false;
                if (a[i].TryEndIdx != b[i].TryEndIdx) return false;
                if (a[i].HandlerStartIdx != b[i].HandlerStartIdx) return false;
                if (a[i].HandlerEndIdx != b[i].HandlerEndIdx) return false;
                if (a[i].FilterIdx != b[i].FilterIdx) return false;
                if (a[i].HandlerType != b[i].HandlerType) return false;
                if (a[i].CatchTypeRefIdx != b[i].CatchTypeRefIdx) return false;
            }
            return true;
        }

        internal static string TypeDefsEqual(NeoTypeDefRecord a, NeoTypeDefRecord b)
        {
            // Minor-1: the type's own identity ref-idx (-> TypeRefTable full name).
            // Asserted, not just read, so a future identity-idx write/read-order
            // bug cannot slip the gate.
            if (a.TypeRefIdx != b.TypeRefIdx) return $"TypeRefIdx {a.TypeRefIdx} vs {b.TypeRefIdx}";
            if (a.TotalPrimitiveSize != b.TotalPrimitiveSize) return $"TotalPrimitiveSize {a.TotalPrimitiveSize} vs {b.TotalPrimitiveSize}";
            if (a.TotalReferenceCount != b.TotalReferenceCount) return $"TotalReferenceCount {a.TotalReferenceCount} vs {b.TotalReferenceCount}";
            if (a.StaticTotalPrimitiveSize != b.StaticTotalPrimitiveSize) return $"StaticTotalPrimitiveSize {a.StaticTotalPrimitiveSize} vs {b.StaticTotalPrimitiveSize}";
            if (a.StaticTotalReferenceCount != b.StaticTotalReferenceCount) return $"StaticTotalReferenceCount {a.StaticTotalReferenceCount} vs {b.StaticTotalReferenceCount}";
            if (a.BaseTypeRefIdx != b.BaseTypeRefIdx) return $"BaseTypeRefIdx {a.BaseTypeRefIdx} vs {b.BaseTypeRefIdx}";
            // Field layout: compare PrimitiveOffset/ReferenceOffset per field (the
            // FieldRefIdx is builder-dependent but must match roundtrip).
            int af = a.Fields?.Length ?? 0, bf = b.Fields?.Length ?? 0;
            if (af != bf) return $"Fields count {af} vs {bf}";
            for (int i = 0; i < af; i++)
            {
                if (a.Fields[i].PrimitiveOffset != b.Fields[i].PrimitiveOffset) return $"field {i} PrimitiveOffset {a.Fields[i].PrimitiveOffset} vs {b.Fields[i].PrimitiveOffset}";
                if (a.Fields[i].ReferenceOffset != b.Fields[i].ReferenceOffset) return $"field {i} ReferenceOffset {a.Fields[i].ReferenceOffset} vs {b.Fields[i].ReferenceOffset}";
                if (a.Fields[i].FieldRefIdx != b.Fields[i].FieldRefIdx) return $"field {i} FieldRefIdx {a.Fields[i].FieldRefIdx} vs {b.Fields[i].FieldRefIdx}";
            }
            if (!IntArraysEqual(a.VTableMethodRefIdxs, b.VTableMethodRefIdxs)) return "VTableMethodRefIdxs mismatch";
            if ((a.Interfaces?.Length ?? 0) != (b.Interfaces?.Length ?? 0)) return $"Interfaces count {a.Interfaces?.Length ?? 0} vs {b.Interfaces?.Length ?? 0}";
            int ai = a.Interfaces?.Length ?? 0;
            for (int i = 0; i < ai; i++)
            {
                if (a.Interfaces[i].VTableOffset != b.Interfaces[i].VTableOffset) return $"iface {i} VTableOffset";
                if (!StringArraysEqual(a.Interfaces[i].MethodSlotKeys, b.Interfaces[i].MethodSlotKeys)) return $"iface {i} MethodSlotKeys";
                if (!IntArraysEqual(a.Interfaces[i].ClassSlotRemap, b.Interfaces[i].ClassSlotRemap)) return $"iface {i} ClassSlotRemap";
                if (a.Interfaces[i].InterfaceTypeRefIdx != b.Interfaces[i].InterfaceTypeRefIdx) return $"iface {i} InterfaceTypeRefIdx";
            }
            if (a.StaticCtorMethodRefIdx != b.StaticCtorMethodRefIdx) return $"StaticCtorMethodRefIdx {a.StaticCtorMethodRefIdx} vs {b.StaticCtorMethodRefIdx}";
            return null;
        }

        internal static string TemplatesEqual(NeoTemplateRecord a, NeoTemplateRecord b)
        {
            // Minor-1: the open generic method def's identity ref-idx
            // (-> MethodRefTable). Asserted, not just read.
            if (a.DefinitionMethodRefIdx != b.DefinitionMethodRefIdx)
                return $"DefinitionMethodRefIdx {a.DefinitionMethodRefIdx} vs {b.DefinitionMethodRefIdx}";
            if (!OpCodeRsEqual(a.TemplateBody, b.TemplateBody)) return "TemplateBody mismatch";
            int ap = a.Patches?.Length ?? 0, bp = b.Patches?.Length ?? 0;
            if (ap != bp) return $"Patches count {ap} vs {bp}";
            for (int i = 0; i < ap; i++)
            {
                var pa = a.Patches[i]; var pb = b.Patches[i];
                if (pa.InstrIdx != pb.InstrIdx) return $"patch {i} InstrIdx {pa.InstrIdx} vs {pb.InstrIdx}";
                if (pa.Field != pb.Field) return $"patch {i} Field {pa.Field} vs {pb.Field}";
                if (pa.Kind != pb.Kind) return $"patch {i} Kind {pa.Kind} vs {pb.Kind}";
                if (pa.GenericParamIdx != pb.GenericParamIdx) return $"patch {i} GenericParamIdx";
                if (pa.TokenRefIdx != pb.TokenRefIdx) return $"patch {i} TokenRefIdx {pa.TokenRefIdx} vs {pb.TokenRefIdx}";
                if (pa.CecilTokenKind != pb.CecilTokenKind) return $"patch {i} CecilTokenKind";
            }
            if (a.LocVarRegStart != b.LocVarRegStart) return "LocVarRegStart";
            if (a.TotalRegCnt != b.TotalRegCnt) return "TotalRegCnt";
            if (a.NeoCatchExRegFinal != b.NeoCatchExRegFinal) return "NeoCatchExRegFinal";
            if (a.StackRegisterCount != b.StackRegisterCount) return "StackRegisterCount";
            if (a.VarCnt != b.VarCnt) return "VarCnt";
            if (a.InitObjPrefixLength != b.InitObjPrefixLength) return "InitObjPrefixLength";
            if (!IntArraysEqual(a.InitObjPrefixRegisters, b.InitObjPrefixRegisters)) return "InitObjPrefixRegisters";
            if (!IntArraysEqual(a.VariableTypeRefIdxs, b.VariableTypeRefIdxs)) return "VariableTypeRefIdxs";
            if (!IntArraysEqual(a.ConstrainedTypeRefIdxs, b.ConstrainedTypeRefIdxs)) return "ConstrainedTypeRefIdxs";
            if (!IntArraysEqual(a.ConstrainedMethodRefIdxs, b.ConstrainedMethodRefIdxs)) return "ConstrainedMethodRefIdxs";
            if (!SwitchTargetsEqual(a.SwitchTargets, b.SwitchTargets)) return "SwitchTargets";
            return null;
        }
    }
}
#endif
