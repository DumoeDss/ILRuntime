#if ENABLE_NEO_MODE && DEBUG
using System;
using System.Collections.Generic;
using System.IO;

using ILRuntime.CLR.Method;
using ILRuntime.CLR.TypeSystem;
using ILRuntime.Runtime.Intepreter.OpCodes;
using ILRuntime.Runtime.NeoAOT;
using ILRuntimeException = ILRuntime.Runtime.Intepreter.ILRuntimeException;

namespace ILRuntime.Runtime.Debugger
{
    /// <summary>
    /// neo-debugger-aot-body capstone (host-side, DEBUG+Neo only) -- the
    /// correctness gate for Neo debugger LOCAL-variable inspection on an
    /// AOT-loaded body (a .neo deserialized + bound via NeoAssemblyLoader.Attach,
    /// the S1 same-AppDomain path -> isNeoAotBody = true).
    ///
    /// Extends the shipped neo-debugger-neo-frame / neo-debugger-ilvt-local
    /// (JIT-path) frame reads to the AOT-body path. The exercise point is the
    /// SAME unhandled-exception path: an AOT-body method throws unhandled ->
    /// ExecuteNeo's unwind builds an ILRuntimeException whose ctor calls
    /// DebugService.GetLocalVariableInfo (DebugService.cs:GetLocalVariableInfo
    /// Neo arm). This check compiles the NeoDebuggerFrameProbe to a .neo,
    /// Attach'es it (overwriting the probe methods' bodies with the AOT bodies),
    /// then drives each probe method and asserts the stashed .LocalInfo carries
    /// the CORRECT live local values -- the SAME shapes the JIT-path gate asserts
    /// (primitive / reference / long / IL-VT + an adversarial mutated local).
    ///
    /// The load-bearing distinction from the JIT-path gate: the methods now run
    /// an AOT body (isNeoAotBody), so the slot layout is the .neo-deserialized
    /// LocalInfos + the type/name resolution is the .neo LocalVariables[] table
    /// (V4) when Definition is null. A body-mutation cell (mutate a deserialized
    /// Ldc constant before Attach -> observe the MUTATED local value post-Attach)
    /// PROVES the inspection reads the genuine AOT body, not a JIT fallback (the
    /// isNeoAotBody flag alone is insufficient -- it only short-circuits
    /// BodyRegister; ExecuteNeo reads NeoExecuteBody).
    ///
    /// Invoked host-side (CLI special mode "NeoDebuggerAotBody"). Neo-only.
    /// </summary>
    public static class NeoDebuggerAotBodyCheck
    {
        public class Result
        {
            public int TotalCells;
            public int Passed;
            public int Failed;
            public List<string> Failures = new List<string>();
        }

        const string ProbeFullName = "TestCases.NeoDebuggerFrameProbe";

        public static Result Run(ILRuntime.Runtime.Enviorment.AppDomain appdomain)
        {
            var res = new Result();

            ILType probeType = null;
            if (!appdomain.LoadedTypes.TryGetValue(ProbeFullName, out var probeIType) || !(probeIType is ILType pt))
            {
                res.Failures.Add(ProbeFullName + " not loaded / not an ILType");
                res.TotalCells = 1; res.Failed = 1;
                return res;
            }
            probeType = pt;

            // ===== compile the probe to a .neo + Attach (S1 same-AppDomain) =====
            // The probe methods get AOT bodies (isNeoAotBody = true). A clean
            // compile is the precondition -- a skip here is a wiring bug.
            NeoAssemblyModel model;
            try
            {
                using (var ms = new MemoryStream())
                {
                    var drv = new NeoCompiler().Compile(new[] { probeType }, ms);
                    if (!drv.IsComplete)
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (var s in drv.Skipped) sb.Append(s.MethodDisplay).Append(" (").Append(s.ExceptionType).Append("); ");
                        res.TotalCells = 1; res.Failed = 1;
                        res.Failures.Add("compile skipped " + drv.Skipped.Count + ": " + sb);
                        return res;
                    }
                    ms.Position = 0;
                    model = NeoAssemblyReader.Read(ms);
                }
            }
            catch (Exception ex)
            {
                res.TotalCells = 1; res.Failed = 1;
                res.Failures.Add("compile/read threw " + ex.GetType().Name + ": " + ex.Message);
                return res;
            }
            res.TotalCells++;
            RecordCell(res, "compile + read .neo (V4)",
                model.Header.Version == NeoAssemblyFormat.Version ? null : "version " + model.Header.Version);

            NeoLoadReport report;
            try { report = NeoAssemblyLoader.Attach(appdomain, model); }
            catch (Exception ex)
            {
                res.TotalCells++; res.Failed++;
                res.Failures.Add("Attach threw " + ex.GetType().Name + ": " + ex.Message);
                return res;
            }
            res.TotalCells++;
            RecordCell(res, "Attach (probe methods -> AOT bodies)",
                report.Attached.Count >= 5 ? null : "only " + report.Attached.Count + " attached");

            // ===== AOT-body flag check: the probe methods are now isNeoAotBody =====
            res.TotalCells++;
            {
                var m = probeType.GetMethod("ProbeThrow", 0) as ILMethod;
                string diff = (m != null && m.isNeoAotBody)
                    ? null
                    : "ProbeThrow isNeoAotBody=" + (m?.isNeoAotBody ?? false);
                RecordCell(res, "ProbeThrow is an AOT body (isNeoAotBody)", diff);
            }

            // ===== Cells: each probe's .LocalInfo carries the CORRECT live values =====
            // The SAME value the JIT-path gate (NeoDebuggerFrameCheck) asserts, now
            // read off the AOT body. Drives every local shape: primitive, reference,
            // long, IL-VT + an adversarial mutated local.
            RunAotCell(res, appdomain, probeType, "ProbeThrow",
                new Expected[] {
                    new Expected("prim local", "12345"),
                    new Expected("reference local msg", "local-string-A"),
                });
            RunAotCell(res, appdomain, probeType, "ProbeMutateThenThrow",
                new Expected[] {
                    new Expected("MUTATED prim local (live AOT read)", "99999"),
                    new Expected("reference local msg", "mutated-string-B"),
                });
            RunAotCell(res, appdomain, probeType, "ProbeMixedWidths",
                new Expected[] {
                    new Expected("long (8-byte prim) local", "1311768467463790320"),
                    new Expected("reference local tag", "wide-probe-C"),
                });
            RunAotCell(res, appdomain, probeType, "ProbeVtLocal",
                new Expected[] {
                    new Expected("IL-VT local primitive field X", "4242"),
                    new Expected("IL-VT local reference field S", "vt-field-A"),
                });
            RunAotCell(res, appdomain, probeType, "ProbeVtLocalMutate",
                new Expected[] {
                    new Expected("MUTATED IL-VT local primitive field X (live AOT read)", "8888"),
                    new Expected("IL-VT local reference field S", "vt-field-B"),
                });

            // ===== Body-mutation cell (load-bearing): mutate the DESERIALIZED
            // ProbeThrow body's prim Ldc_I4 (12345, the SOLE assignment to prim --
            // no later overwrite) to BODY_MUT before Attach -> the AOT body's local
            // value the inspector reads is BODY_MUT, NOT 12345. PROVES the inspector
            // reads the genuine AOT body's frame bytes (a JIT-fallback read would
            // surface the unmutated 12345). ProbeThrow is the right target: prim is
            // assigned once + never overwritten, so the mutation is observable.
            // (ProbeMutateThenThrow re-assigns prim -> the mutation is clobbered.) =====
            {
                const int ORIG = 12345;        // ProbeThrow's prim Ldc_I4 (sole assignment)
                const int BODY_MUT = 70707;    // the deserialized-body mutation sentinel
                res.TotalCells++;
                string diff = null;
                try
                {
                    NeoAssemblyModel model2;
                    using (var ms2 = new MemoryStream())
                    {
                        new NeoCompiler().Compile(new[] { probeType }, ms2);
                        ms2.Position = 0;
                        model2 = NeoAssemblyReader.Read(ms2);
                    }
                    // locate ProbeThrow's method-def record by name.
                    int defIdx = -1;
                    for (int i = 0; i < model2.MethodDefs.Length; i++)
                    {
                        var r = model2.MethodDefs[i];
                        if (r.MethodRefIdx < 0 || r.MethodRefIdx >= model2.MethodRefs.Length) continue;
                        if (model2.MethodRefs[r.MethodRefIdx].Name == "ProbeThrow") { defIdx = i; break; }
                    }
                    OpCodeR[] cbody = (defIdx >= 0) ? model2.MethodDefs[defIdx].NeoExecuteBody : null;
                    int mutateAt = -1;
                    if (cbody != null)
                    {
                        for (int j = 0; j < cbody.Length; j++)
                            if (cbody[j].Code == OpCodeREnum.Ldc_I4 && cbody[j].Operand == ORIG) { mutateAt = j; break; }
                    }
                    if (mutateAt < 0)
                    {
                        diff = "ProbeThrow Ldc_I4=" + ORIG + " not found in AOT body (defIdx=" + defIdx + ")";
                    }
                    else
                    {
                        cbody[mutateAt].Operand = BODY_MUT;
                        NeoAssemblyLoader.Attach(appdomain, model2);
                        var m = probeType.GetMethod("ProbeThrow", 0);
                        var inst = appdomain.Instantiate(ProbeFullName);
                        string li = null;
                        bool driveFailed = false;
                        try { appdomain.Invoke(m, inst); }
                        catch (ILRuntimeException ex) { li = ex.LocalInfo ?? ""; }
                        catch (Exception ex) { diff = "body-mutation: got " + ex.GetType().Name + ": " + ex.Message; driveFailed = true; }
                        if (!driveFailed)
                        {
                            diff = (li != null && li.Contains(BODY_MUT.ToString()) && !li.Contains(ORIG.ToString()))
                                ? null
                                : "expected BODY_MUT=" + BODY_MUT + " (not " + ORIG + ") in .LocalInfo (a JIT-body read would yield " + ORIG + "); li=[" + li + "]";
                        }
                    }
                }
                catch (Exception ex) { diff = "body-mutation cell threw " + ex.GetType().Name + ": " + ex.Message; }
                RecordCell(res, "AOT body-mutation (inspector reads genuine AOT body)", diff);
            }

            // ===== S3-2 Cecil-free shell cell (the V4 metadata pipeline gate). The
            // core of this child is making the LOCAL type/name inspectable on a
            // Cecil-free shell (def == null). Compile the probe + Cecil-free-load
            // it into a FRESH AppDomain (NO Cecil module for the probe), then verify
            // the shell methods HAVE the deserialized local meta (HasNeoAotLocalMeta
            // + GetNeoAotLocalName matches the probe's declared local names + the
            // primitive type resolves). This is the serialize -> deserialize ->
            // resolve pipeline on the genuine Cecil-free path. (The reference local
            // VALUE on the Cecil-free path is a SEPARATE Cecil-free-execution gap --
            // string-literal / reference-local persistence -- tracked as a follow-up;
            // this cell asserts the META pipeline, which is this child's core.) =====
            res.TotalCells++;
            {
                string diff;
                try
                {
                    NeoAssemblyModel cfModel;
                    using (var ms = new MemoryStream())
                    {
                        new NeoCompiler().Compile(new[] { probeType }, ms);
                        ms.Position = 0;
                        cfModel = NeoAssemblyReader.Read(ms);
                    }
                    var domainB = new ILRuntime.Runtime.Enviorment.AppDomain();
                    try
                    {
                        domainB.LoadNeoAssembly(cfModel, null);
                        var probeB = domainB.GetType(ProbeFullName) as ILType;
                        var mB = probeB?.GetMethod("ProbeThrow", 0) as ILMethod;
                        if (mB == null) diff = "B: ProbeThrow shell not found";
                        else if (mB.Definition != null) diff = "B: shell has a Cecil Definition (not Cecil-free)";
                        else if (!mB.HasNeoAotLocalMeta) diff = "B: shell HasNeoAotLocalMeta=false (V4 meta not deserialized)";
                        else
                        {
                            // ProbeThrow declares `int prim` + `string msg` (2 locals).
                            var sb = new System.Text.StringBuilder();
                            if (mB.LocalVariableCount != 2) sb.Append("LocalVariableCount=").Append(mB.LocalVariableCount).Append(" (expected 2); ");
                            var t0 = mB.GetNeoAotLocalType(0);   // prim -> Int32
                            if (t0 == null || t0.Name != "Int32") sb.Append("local0 type=").Append(t0?.Name ?? "<null>").Append(" (expected Int32); ");
                            var n0 = mB.GetNeoAotLocalName(0);   // "prim"
                            if (n0 != "prim") sb.Append("local0 name=").Append(n0 ?? "<null>").Append(" (expected prim); ");
                            var t1 = mB.GetNeoAotLocalType(1);   // msg -> String
                            if (t1 == null || t1.Name != "String") sb.Append("local1 type=").Append(t1?.Name ?? "<null>").Append(" (expected String); ");
                            var n1 = mB.GetNeoAotLocalName(1);   // "msg"
                            if (n1 != "msg") sb.Append("local1 name=").Append(n1 ?? "<null>").Append(" (expected msg); ");
                            diff = sb.Length == 0 ? null : sb.ToString();
                        }
                    }
                    finally { domainB.Dispose(); }
                }
                catch (Exception ex) { diff = "CF meta cell threw " + ex.GetType().Name + ": " + ex.Message; }
                RecordCell(res, "S3-2 Cecil-free shell local-meta (V4 serialize->deserialize->resolve)", diff);
            }

            return res;
        }

        // Drive one AOT-body probe method, catch the ILRuntimeException, assert
        // .LocalInfo contains each expected local value. localExpects: the values
        // the frame locals hold at the throw point.
        static void RunAotCell(Result res, ILRuntime.Runtime.Enviorment.AppDomain appdomain,
            ILType probeType, string methodName, Expected[] localExpects)
        {
            res.TotalCells++;
            string diff = null;
            try
            {
                var inst = appdomain.Instantiate(ProbeFullName);
                var m = probeType.GetMethod(methodName, 0);
                if (m == null) { diff = "method " + methodName + " not found"; RecordCell(res, methodName + " AOT-body locals", diff); return; }
                ILRuntimeException caught = null;
                try { appdomain.Invoke(m, inst); }
                catch (ILRuntimeException ex) { caught = ex; }
                catch (Exception ex) { diff = methodName + ": expected ILRuntimeException, got " + ex.GetType().Name + ": " + ex.Message; RecordCell(res, methodName + " AOT-body locals", diff); return; }
                if (caught == null) { diff = methodName + ": probe did not throw"; RecordCell(res, methodName + " AOT-body locals", diff); return; }
                string li = caught.LocalInfo ?? "";
                if (li.Contains("not supported")) diff = "LocalInfo=refusal; ";
                var sb = diff == null ? new System.Text.StringBuilder() : new System.Text.StringBuilder(diff);
                foreach (var e in localExpects)
                {
                    if (!li.Contains(e.Value))
                        sb.Append("LocalInfo missing ").Append(e.Label).Append("='").Append(e.Value).Append("' (li=[").Append(li).Append("]); ");
                }
                diff = sb.Length == 0 ? null : sb.ToString();
            }
            catch (Exception ex) { diff = methodName + " cell threw " + ex.GetType().Name + ": " + ex.Message; }
            RecordCell(res, methodName + " AOT-body locals", diff);
        }

        static void RecordCell(Result res, string name, string diff)
        {
            if (diff == null)
            {
                res.Passed++;
                Console.WriteLine("[NeoDebuggerAotBody] " + name + ": PASS");
            }
            else
            {
                res.Failed++; res.Failures.Add(name + ": " + diff);
                Console.WriteLine("  [FAIL] " + name + ": " + diff);
            }
        }

        struct Expected
        {
            public string Label;
            public string Value;
            public Expected(string label, string value) { Label = label; Value = value; }
        }
    }
}
#endif
