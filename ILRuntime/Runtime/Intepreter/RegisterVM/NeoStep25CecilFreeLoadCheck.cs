#if ENABLE_NEO_MODE && DEBUG
using System;
using System.Collections.Generic;
using System.IO;

using ILRuntime.CLR.Method;
using ILRuntime.CLR.TypeSystem;
using ILRuntime.Runtime.NeoAOT;
using ILRuntime.Runtime.Intepreter.OpCodes;

namespace ILRuntime.Runtime.Intepreter.RegisterVM
{
    /// <summary>
    /// Step 25 S3-2 capstone (host-side, DEBUG+Neo only): the Cecil-free load.
    /// Compiles a .neo for TestCases.NeoStep25S3Probe in the SESSION AppDomain
    /// (AppDomain A, Cecil-loaded), then loads it Cecil-free into a FRESH
    /// ILRuntime AppDomain B (a `new AppDomain()` with NO Cecil module for the
    /// probe), invokes the probe's Compute() method via domainB.Invoke, and
    /// asserts the result EQUALS the independently-computed expected value
    /// (A's JIT result). This exercises field write/read (Stfld/Ldfld on the
    /// Cecil-free ILType's installed fieldOffsets), virtual dispatch (the
    /// BaseVirtual override via the installed Neo VTable), and interface
    /// dispatch (IfaceMethod via the installed interface map) -- all on a
    /// Cecil-free ILType, with NO Cecil TypeDefinition on the load side.
    ///
    /// Adversarial mutation cells (a green capstone is INSUFFICIENT -- the
    /// record is built from Cecil's values at serialize; a Cecil-fallback would
    /// pass trivially):
    ///  - M1 (body-mutation): mutate a Ldc_I4 constant in an INDEPENDENT
    ///    model2's NeoExecuteBody BEFORE LoadNeoAssembly -> assert the Cecil-free
    ///    execution yields the MUTATED value (proves ExecuteNeo runs the genuine
    ///    .neo body, not a Cecil/JIT fallback).
    ///  - M2 (layout-mutation): mutate a PrimitiveOffset in model2's Fields[]
    ///    BEFORE LoadNeoAssembly -> assert the Cecil-free ILType's fieldOffsets
    ///    reflects the mutation (proves the factory builds from the .neo record,
    ///    not Cecil).
    /// A Cecil-free load that secretly re-reads Cecil or used A's maps fails
    /// BOTH mutation cells. Mirrors the S3-partial + S1/S2 mutation discipline.
    ///
    /// Invoked host-side (CLI special mode "NeoStep25CecilFreeLoad").
    /// </summary>
    public static class NeoStep25CecilFreeLoadCheck
    {
        public class Result
        {
            public int TotalCells;
            public int Passed;
            public int Failed;
            public List<string> Failures = new List<string>();
            public int AttachedCount;
            public int SkippedCount;
            public List<string> Skipped = new List<string>();
        }

        const string ProbeFullName = "TestCases.NeoStep25S3Probe";
        const int ComputeExpected = 155;   // 27 (BaseVirtual override) + 21 (IfaceMethod) + 100 (FLong) + 7 (FInt)

        public static Result Run(ILRuntime.Runtime.Enviorment.AppDomain appdomainA)
        {
            var res = new Result();

            // ---- locate the probe in the SESSION AppDomain A (Cecil-loaded) ----
            if (!appdomainA.LoadedTypes.TryGetValue(ProbeFullName, out var probeIt) || !(probeIt is ILType probeTypeA))
            {
                res.Failures.Add(ProbeFullName + " not loaded in A / not an ILType");
                res.TotalCells = 1; res.Failed = 1;
                return res;
            }

            // ---- (A) the known-expected value: run Compute() via A's JIT (the
            // Cecil-loaded reference). Independent of the Cecil-free path; pins
            // the expected value so a "both-garbage" false pass is ruled out. ----
            res.TotalCells++;
            int expected;
            {
                object jitRes = null;
                string diff = null;
                try
                {
                    var inst = appdomainA.Instantiate(ProbeFullName);
                    var computeMethodA = probeTypeA.GetMethod("Compute", 0);
                    jitRes = appdomainA.Invoke(computeMethodA, inst);
                }
                catch (Exception ex) { diff = "A JIT Compute threw " + ex.GetType().Name + ": " + ex.Message; }
                if (diff == null)
                {
                    try { expected = Convert.ToInt32(jitRes); }
                    catch { diff = "A JIT Compute returned non-int: " + (jitRes?.GetType().Name ?? "null"); expected = -1; }
                    if (diff == null && expected != ComputeExpected)
                        diff = "A JIT Compute=" + expected + " but the hardcoded expected=" + ComputeExpected + " (probe arithmetic changed -> update ComputeExpected)";
                }
                else expected = -1;
                RecordCell(res, "A JIT Compute (the known-expected reference)", diff);
            }

            // ---- compile a .neo for the probe (CLOSURE: probe + base + interface)
            // in A (V2, with recorded hashes). The Cecil-free load needs EVERY IL
            // type the probe references transitively (base + interface) IN the
            // .neo -- otherwise they cannot resolve by name in B. ----
            NeoAssemblyModel model;
            try
            {
                var compileSet = new List<ILType> { probeTypeA };
                if (appdomainA.LoadedTypes.TryGetValue("TestCases.NeoStep25S3Base", out var baseIt) && baseIt is ILType baseIl)
                    compileSet.Add(baseIl);
                if (appdomainA.LoadedTypes.TryGetValue("TestCases.INeoStep25S3Iface", out var ifaceIt) && ifaceIt is ILType ifaceIl)
                    compileSet.Add(ifaceIl);
                using (var ms = new MemoryStream())
                {
                    var cres = new NeoCompiler().Compile(compileSet, ms);
                    if (!cres.IsComplete)
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (var s in cres.Skipped) sb.Append(s.MethodDisplay).Append(" (").Append(s.ExceptionType).Append("); ");
                        res.TotalCells++; res.Failed++;
                        res.Failures.Add("compile skipped " + cres.Skipped.Count + ": " + sb);
                        return res;
                    }
                    ms.Position = 0;
                    model = NeoAssemblyReader.Read(ms);
                }
            }
            catch (Exception ex)
            {
                res.TotalCells++; res.Failed++;
                res.Failures.Add("compile/read threw " + ex.GetType().Name + ": " + ex.Message);
                return res;
            }
            res.TotalCells++;
            RecordCell(res, "compile .neo (V2) in A", null);

            // ---- (B) the capstone: load Cecil-free into a FRESH AppDomain B ----
            // B has NO Cecil module + NO mapType/mapTypeToken entries for the probe
            // until LoadNeoAssembly populates them PURELY from the .neo tables.
            res.TotalCells++;
            int bResult = -1;
            {
                string diff;
                try
                {
                    var domainB = new ILRuntime.Runtime.Enviorment.AppDomain();
                    try
                    {
                        var loadReport = domainB.LoadNeoAssembly(model, null);
                        res.AttachedCount = loadReport.Attached.Count;
                        res.SkippedCount = loadReport.Skipped.Count;
                        foreach (var s in loadReport.Skipped) res.Skipped.Add(s.reason + ": " + s.target);
                        // Invoke Compute() on the Cecil-free probe in B. Compute is
                        // an instance method -> instantiate first (the Cecil-free
                        // ILType's Instantiate reads the installed field layout).
                        var probeB = domainB.GetType(ProbeFullName);
                        var computeMethodB = probeB?.GetMethod("Compute", 0);
                        if (computeMethodB == null) diff = "B: probe/Compute not found after Cecil-free load";
                        else
                        {
                            var instB = domainB.Instantiate(ProbeFullName);
                            var r = domainB.Invoke(computeMethodB, instB);
                            try { bResult = Convert.ToInt32(r); }
                            catch { bResult = -1; }
                            diff = (bResult == expected)
                                ? null
                                : "B Compute=" + bResult + " expected=" + expected;
                        }
                    }
                    finally { domainB.Dispose(); }
                }
                catch (Exception ex) { diff = "B Cecil-free load+exec threw " + ex.GetType().Name + ": " + ex.Message; }
                RecordCell(res, "B Cecil-free load + Compute (the capstone)", diff);
            }

            // ---- (M1) body-mutation cell: mutate a Ldc constant in model2 BEFORE
            // load. Compute() bakes FLong = 100 via Ldc_I4_S 100. Mutate it to
            // MUTATED -> assert B yields the MUTATED-derived value (a Cecil-
            // fallback would yield the unmutated value). The result shifts by
            // exactly (MUTATED - 100) (FLong feeds ONLY the final Ldfld_I8 +
            // Conv_I4 + Add -- a single contribution). ----
            res.TotalCells++;
            {
                const int ORIG = 100;
                const int MUTATED = 555;
                int mutatedExpected = expected + (MUTATED - ORIG);   // FLong contributes once to the sum
                string diff;
                try
                {
                    NeoAssemblyModel model2;
                    using (var ms2 = new MemoryStream())
                    {
                        var cset = new List<ILType> { probeTypeA };
                        if (appdomainA.LoadedTypes.TryGetValue("TestCases.NeoStep25S3Base", out var bit) && bit is ILType bil) cset.Add(bil);
                        if (appdomainA.LoadedTypes.TryGetValue("TestCases.INeoStep25S3Iface", out var iit) && iit is ILType iil) cset.Add(iil);
                        new NeoCompiler().Compile(cset, ms2);
                        ms2.Position = 0;
                        model2 = NeoAssemblyReader.Read(ms2);
                    }
                    int defIdx = FindMethodDefByName(model2, ProbeFullName, "Compute");
                    OpCodeR[] body = defIdx >= 0 ? model2.MethodDefs[defIdx].NeoExecuteBody : null;
                    int mutateAt = -1;
                    if (body != null)
                    {
                        for (int j = 0; j < body.Length; j++)
                            if (body[j].Code == OpCodeREnum.Ldc_I4_S && body[j].Operand == ORIG) { mutateAt = j; break; }
                    }
                    if (mutateAt < 0)
                    {
                        var sb = new System.Text.StringBuilder();
                        if (body == null) sb.Append("null body");
                        else for (int j = 0; j < body.Length; j++)
                        {
                            if (j > 0) sb.Append(',');
                            sb.Append(body[j].Code);
                            if (body[j].Code == OpCodeREnum.Ldc_I4 || body[j].Code == OpCodeREnum.Ldc_I4_S) sb.Append('=').Append(body[j].Operand);
                        }
                        diff = "Compute Ldc_I4_S=" + ORIG + " not found [" + sb + "]";
                    }
                    else
                    {
                        body[mutateAt].Operand = MUTATED;
                        var domainB2 = new ILRuntime.Runtime.Enviorment.AppDomain();
                        try
                        {
                            domainB2.LoadNeoAssembly(model2, null);
                            var m = domainB2.GetType(ProbeFullName)?.GetMethod("Compute", 0);
                            var instB2 = domainB2.Instantiate(ProbeFullName);
                            var r = m != null ? domainB2.Invoke(m, instB2) : null;
                            int got = -1;
                            try { got = Convert.ToInt32(r); } catch { }
                            diff = (got == mutatedExpected)
                                ? null
                                : "M1 body-mutation: expected " + mutatedExpected + " got " + got + " (a Cecil-fallback would yield " + expected + ")";
                        }
                        finally { domainB2.Dispose(); }
                    }
                }
                catch (Exception ex) { diff = "M1 threw " + ex.GetType().Name + ": " + ex.Message; }
                RecordCell(res, "M1 body-mutation (Cecil-free exec runs the .neo body)", diff);
            }

            // ---- (M2) layout-mutation cell: mutate a PrimitiveOffset in model2's
            // Fields[] BEFORE load -> assert the Cecil-free ILType's fieldOffsets
            // reflects the mutation (NOT Cecil's). Proves the factory builds from
            // the record. ----
            res.TotalCells++;
            {
                string diff;
                try
                {
                    NeoAssemblyModel model3;
                    using (var ms3 = new MemoryStream())
                    {
                        var cset = new List<ILType> { probeTypeA };
                        if (appdomainA.LoadedTypes.TryGetValue("TestCases.NeoStep25S3Base", out var bit) && bit is ILType bil) cset.Add(bil);
                        if (appdomainA.LoadedTypes.TryGetValue("TestCases.INeoStep25S3Iface", out var iit) && iit is ILType iil) cset.Add(iil);
                        new NeoCompiler().Compile(cset, ms3);
                        ms3.Position = 0;
                        model3 = NeoAssemblyReader.Read(ms3);
                    }
                    int tdIdx = FindTypeDefByName(model3, ProbeFullName);
                    if (tdIdx < 0) diff = "M2: probe TypeDef not found";
                    else
                    {
                        var fields = model3.TypeDefs[tdIdx].Fields;
                        if (fields == null || fields.Length == 0) diff = "M2: probe has no fields";
                        else
                        {
                            // Mutate Fields[0].PrimitiveOffset.
                            var f0 = fields[0];
                            int origPO = f0.PrimitiveOffset;
                            int mutatedPO = origPO + 9999;
                            f0.PrimitiveOffset = mutatedPO;
                            fields[0] = f0;
                            var domainB3 = new ILRuntime.Runtime.Enviorment.AppDomain();
                            try
                            {
                                domainB3.LoadNeoAssembly(model3, null);
                                var probeB = domainB3.GetType(ProbeFullName) as ILType;
                                if (probeB == null) diff = "M2: probe not found in B3";
                                else
                                {
                                    int start = probeB.FieldStartIndex;
                                    int got = probeB.GetFieldOffset(start + 0).PrimitiveOffset;
                                    diff = (got == mutatedPO)
                                        ? null
                                        : "M2 layout-mutation: expected " + mutatedPO + " got " + got + " (a Cecil-built factory would yield " + origPO + ")";
                                }
                            }
                            finally { domainB3.Dispose(); }
                        }
                    }
                }
                catch (Exception ex) { diff = "M2 threw " + ex.GetType().Name + ": " + ex.Message; }
                RecordCell(res, "M2 layout-mutation (factory builds from the .neo record)", diff);
            }

            return res;
        }

        static int FindMethodDefByName(NeoAssemblyModel model, string declaringFullName, string name)
        {
            if (model.MethodDefs == null) return -1;
            for (int i = 0; i < model.MethodDefs.Length; i++)
            {
                var r = model.MethodDefs[i];
                if (r.MethodRefIdx < 0 || r.MethodRefIdx >= model.MethodRefs.Length) continue;
                var mr = model.MethodRefs[r.MethodRefIdx];
                if (mr == null || mr.DeclaringType == null || mr.DeclaringType.Name != declaringFullName) continue;
                if (mr.Name == name) return i;
            }
            return -1;
        }

        static int FindTypeDefByName(NeoAssemblyModel model, string fullName)
        {
            if (model.TypeDefs == null) return -1;
            for (int i = 0; i < model.TypeDefs.Length; i++)
            {
                int trIdx = model.TypeDefs[i].TypeRefIdx;
                if (trIdx < 0 || trIdx >= model.TypeRefs.Length) continue;
                if (model.TypeRefs[trIdx].Name == fullName) return i;
            }
            return -1;
        }

        static void RecordCell(Result res, string name, string diff)
        {
            if (diff == null)
            {
                res.Passed++;
                Console.WriteLine("[NeoStep25S3-2] " + name + ": PASS");
            }
            else
            {
                res.Failed++; res.Failures.Add(name + ": " + diff);
                Console.WriteLine("  [FAIL] " + name + ": " + diff);
            }
        }
    }
}
#endif
