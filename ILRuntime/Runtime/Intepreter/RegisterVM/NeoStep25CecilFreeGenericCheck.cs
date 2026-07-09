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
    /// Step 25 child-8 capstone (host-side, DEBUG+Neo only): the Cecil-free
    /// GENERIC load. The cross-section of the S3-2 Cecil-free load + the S2
    /// generic-instantiation-at-load machinery.
    ///
    /// Compiles a .neo for TestCases.NeoStep25CecilFreeGenericProbe in the SESSION
    /// AppDomain A (Cecil-loaded), then loads it Cecil-free into a FRESH
    /// ILRuntime AppDomain B (a `new AppDomain()` with NO Cecil module for the
    /// probe), invokes the probe's generic-method wrappers via domainB.Invoke, and
    /// asserts each result EQUALS its known-expected value (independently computed
    /// in A via JIT). The generic arg T is re-resolved Cecil-free (no Cecil
    /// TypeReference / MethodReference on the load side).
    ///
    /// Matrix: int T (4-byte prim), long T (8-byte prim), string T (ref), struct T
    /// (IL value type). Each wrapper instantiates the Cecil-free ILType + calls a
    /// generic method at a concrete T, routed through Step-22 CloneAndPatch against
    /// the AOT template re-resolved Cecil-free.
    ///
    /// Adversarial guards:
    ///  - G1 (template-attach coverage): the Cecil-free generic DEFINITION shell
    ///    has its GenericMethodTemplateCache bound post-load (the S2 hook re-resolved
    ///    + bound the template Cecil-free). FAILs on HEAD (the shell returns
    ///    GenericParameterCount=0 -> MatchGenericDefinition never matches -> the
    ///    template is skipped -> no cache).
    ///  - G2 (template body-mutation): mutate ConstGeneric's deserialized
    ///    TemplateBody Ldc_I4 before the Cecil-free load -> assert the MUTATED
    ///    value on the Cecil-free exec. Observing MUTATED proves CloneAndPatch ran
    ///    the genuine AOT template (NOT a JIT/Cecil fallback -- a green functional
    ///    cell alone is insufficient: the bodies are byte-identical under Step 23).
    ///
    /// Invoked host-side (CLI special mode "NeoStep25CecilFreeGeneric").
    /// </summary>
    public static class NeoStep25CecilFreeGenericCheck
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

        const string ProbeFullName = "TestCases.NeoStep25CecilFreeGenericProbe";

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

            // ---- (A) the known-expected values: run each wrapper via A's JIT ----
            // (the Cecil-loaded reference). Independent of the Cecil-free path;
            // pins the expected values so a "both-garbage" false pass is ruled out.
            var wrappers = new[]
            {
                new Wrap("WrapEchoInt",    42),
                new Wrap("WrapEchoLong",   9000000000L),
                new Wrap("WrapEchoRef",    1234567),
                new Wrap("WrapEchoStruct", 77),
            };
            var expected = new Dictionary<string, object>();
            foreach (var w in wrappers)
            {
                res.TotalCells++;
                object jitRes = null;
                string diff = null;
                try
                {
                    var inst = appdomainA.Instantiate(ProbeFullName);
                    var mA = probeTypeA.GetMethod(w.Name, 0);
                    jitRes = appdomainA.Invoke(mA, inst);
                }
                catch (Exception ex) { diff = "A JIT " + w.Name + " threw " + ex.GetType().Name + ": " + ex.Message; }
                if (diff == null)
                {
                    if (!ValueEqualsObj(jitRes, w.Expected))
                        diff = "A JIT " + w.Name + "=" + Format(jitRes) + " but the hardcoded expected=" + w.Expected + " (probe changed -> update expected)";
                    else
                        expected[w.Name] = jitRes;
                }
                RecordCell(res, "A JIT " + w.Name + " (the known-expected reference)", diff);
            }

            // ---- compile a .neo for the probe (+ the struct arg type) in A. The
            // Cecil-free load needs every IL type the probe references transitively
            // (the struct generic arg) IN the .neo -- otherwise it cannot resolve by
            // name in B. ----
            NeoAssemblyModel model;
            try
            {
                var compileSet = new List<ILType> { probeTypeA };
                if (appdomainA.LoadedTypes.TryGetValue("TestCases.NeoStep25CegVal", out var structIt) && structIt is ILType structIl)
                    compileSet.Add(structIl);
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
            RecordCell(res, "compile .neo (with generic template) in A", null);

            // ---- (B) the capstone: load Cecil-free into a FRESH AppDomain B ----
            // B has NO Cecil module + NO mapType/mapTypeToken entries for the probe
            // until LoadNeoAssembly populates them PURELY from the .neo tables.
            res.TotalCells++;
            int bLoadOk = 0;
            NeoLoadReport loadReport = null;
            {
                string diff;
                try
                {
                    var domainB = new ILRuntime.Runtime.Enviorment.AppDomain();
                    try
                    {
                        loadReport = domainB.LoadNeoAssembly(model, null);
                        res.AttachedCount = loadReport.Attached.Count;
                        res.SkippedCount = loadReport.Skipped.Count;
                        foreach (var s in loadReport.Skipped) res.Skipped.Add(s.reason + ": " + s.target);
                        // Sanity: the probe type is present in B.
                        var probeB = domainB.GetType(ProbeFullName);
                        diff = (probeB != null) ? null : "B: probe not found after Cecil-free load";
                        bLoadOk = (probeB != null) ? 1 : 0;
                    }
                    finally { domainB.Dispose(); }
                }
                catch (Exception ex) { diff = "B Cecil-free load threw " + ex.GetType().Name + ": " + ex.Message; }
                RecordCell(res, "B Cecil-free load (the probe loads)", diff);
            }

            // ---- (G1) template-attach coverage: the Cecil-free generic DEFINITION
            // shell has its GenericMethodTemplateCache bound post-load. On HEAD the
            // shell returns GenericParameterCount=0 -> the S2 MatchGenericDefinition
            // loop skips it -> the template is never bound -> the cache stays null.
            // ----
            res.TotalCells++;
            {
                string diff = null;
                try
                {
                    var domainB = new ILRuntime.Runtime.Enviorment.AppDomain();
                    try
                    {
                        var lr = domainB.LoadNeoAssembly(model, null);
                        bool echoBound = false, constBound = false;
                        var probeB = domainB.GetType(ProbeFullName) as ILType;
                        if (probeB != null && probeB.GetMethods() != null)
                        {
                            foreach (var mm in probeB.GetMethods())
                            {
                                var ilm = mm as ILMethod;
                                if (ilm == null || ilm.IsGenericInstance) continue;
                                if (ilm.GenericMethodTemplateCache != null)
                                {
                                    if (ilm.Name == "Echo") echoBound = true;
                                    else if (ilm.Name == "ConstGeneric") constBound = true;
                                }
                            }
                        }
                        diff = (echoBound && constBound)
                            ? null
                            : "echoBound=" + echoBound + " constBound=" + constBound + " (skipped entries mentioning template: "
                              + string.Join("; ", lr.Skipped.FindAll(s => s.reason != null && s.reason.IndexOf("template", StringComparison.OrdinalIgnoreCase) >= 0).ConvertAll(s => s.reason + ": " + s.target).ToArray()) + ")";
                    }
                    finally { domainB.Dispose(); }
                }
                catch (Exception ex) { diff = "G1 threw " + ex.GetType().Name + ": " + ex.Message; }
                RecordCell(res, "G1 Cecil-free generic template bind coverage", diff);
            }

            // ---- (1) FUNCTIONAL cells: load Cecil-free into a FRESH B per wrapper
            // (a shared B would do, but isolating per cell gives a cleaner signal),
            // invoke each wrapper, assert == expected. ----
            foreach (var w in wrappers)
            {
                res.TotalCells++;
                string diff;
                try
                {
                    var domainB = new ILRuntime.Runtime.Enviorment.AppDomain();
                    try
                    {
                        domainB.LoadNeoAssembly(model, null);
                        var probeB = domainB.GetType(ProbeFullName);
                        var mB = probeB?.GetMethod(w.Name, 0);
                        object r = null;
                        if (mB == null) diff = "B: " + w.Name + " not found after Cecil-free load";
                        else
                        {
                            var instB = domainB.Instantiate(ProbeFullName);
                            try { r = domainB.Invoke(mB, instB); }
                            catch (Exception ex) { r = new ThrownMarker(ex); }
                            diff = ValueEqualsObj(r, w.Expected)
                                ? null
                                : "B " + w.Name + "=" + Format(r) + " expected=" + w.Expected;
                        }
                    }
                    finally { domainB.Dispose(); }
                }
                catch (Exception ex) { diff = "B " + w.Name + " threw " + ex.GetType().Name + ": " + ex.Message; }
                RecordCell(res, "B Cecil-free generic " + w.Name, diff);
            }

            // ---- (G2) TEMPLATE BODY-MUTATION guard: mutate ConstGeneric's
            // deserialized TemplateBody Ldc_I4 CONST -> MUTATED before the Cecil-free
            // load -> invoke WrapConstRef (a FRESH ConstGeneric<string> instance) ->
            // assert MUTATED. Observing MUTATED on the Cecil-free exec proves
            // CloneAndPatch ran the genuine AOT template body (a green functional
            // cell alone is insufficient: Step 23 made the bodies byte-identical, so
            // a JIT/Cecil fallback would also yield CONST and pass; the mutation
            // distinguishes AOT-template from fallback).
            //
            // Drive a FRESH instance directly (MakeGenericMethod -> a NEW instance
            // whose BodyRegister is null -> InitCodeBody -> the Step-22 hook ->
            // CloneAndPatch against the MUTATED AOT template), NOT via the wrapper
            // (the wrapper's callee resolves via AppDomain.GetMethod -> the instance
            // registered at compile, whose body is already cached).
            {
                const int CONST = 1234567;
                const int MUTATED = 7654321;
                res.TotalCells++;
                string diff;
                try
                {
                    NeoAssemblyModel model2;
                    using (var ms2 = new MemoryStream())
                    {
                        var cset = new List<ILType> { probeTypeA };
                        if (appdomainA.LoadedTypes.TryGetValue("TestCases.NeoStep25CegVal", out var sit) && sit is ILType sil) cset.Add(sil);
                        new NeoCompiler().Compile(cset, ms2);
                        ms2.Position = 0;
                        model2 = NeoAssemblyReader.Read(ms2);
                    }
                    // Find ConstGeneric's template record (by MethodRef name).
                    int tplIdx = -1;
                    if (model2.Templates != null)
                    {
                        for (int i = 0; i < model2.Templates.Length; i++)
                        {
                            var trec = model2.Templates[i];
                            if (trec.DefinitionMethodRefIdx < 0 || trec.DefinitionMethodRefIdx >= model2.MethodRefs.Length) continue;
                            if (model2.MethodRefs[trec.DefinitionMethodRefIdx].Name == "ConstGeneric") { tplIdx = i; break; }
                        }
                    }
                    OpCodeR[] tbody = (tplIdx >= 0) ? model2.Templates[tplIdx].TemplateBody : null;
                    int mutateAt = -1;
                    if (tbody != null)
                    {
                        for (int j = 0; j < tbody.Length; j++)
                            if (tbody[j].Code == OpCodeREnum.Ldc_I4 && tbody[j].Operand == CONST) { mutateAt = j; break; }
                    }
                    if (mutateAt < 0)
                    {
                        var sb = new System.Text.StringBuilder();
                        if (tbody == null) sb.Append("null template body (tplIdx=").Append(tplIdx).Append(')');
                        else for (int j = 0; j < tbody.Length; j++)
                        {
                            if (j > 0) sb.Append(',');
                            sb.Append(tbody[j].Code);
                            if (tbody[j].Code == OpCodeREnum.Ldc_I4) { sb.Append('='); sb.Append(tbody[j].Operand); }
                        }
                        diff = "ConstGeneric template Ldc_I4=" + CONST + " not found [" + sb + "]";
                    }
                    else
                    {
                        tbody[mutateAt].Operand = MUTATED;            // mutate the deserialized AOT template body
                        var domainB2 = new ILRuntime.Runtime.Enviorment.AppDomain();
                        try
                        {
                            domainB2.LoadNeoAssembly(model2, null);
                            var probeB2 = domainB2.GetType(ProbeFullName) as ILType;
                            // Find the open ConstGeneric definition on the Cecil-free type.
                            ILMethod constDef = null;
                            if (probeB2 != null && probeB2.GetMethods() != null)
                            {
                                foreach (var mm in probeB2.GetMethods())
                                {
                                    var ilm = mm as ILMethod;
                                    if (ilm == null || ilm.IsGenericInstance) continue;
                                    if (ilm.Name == "ConstGeneric") { constDef = ilm; break; }
                                }
                            }
                            IType stringT = null;
                            try { stringT = domainB2.GetType("System.String"); } catch { }
                            object r;
                            if (constDef == null || stringT == null)
                            {
                                diff = "G2: constDef=" + (constDef != null) + " stringT=" + (stringT != null);
                            }
                            else
                            {
                                var freshInst = constDef.MakeGenericMethod(new IType[] { stringT }) as ILMethod;
                                try { r = domainB2.Invoke(freshInst, null); }
                                catch (Exception ex) { r = new ThrownMarker(ex); }
                                diff = ValueEqualsObj(r, MUTATED)
                                    ? null
                                    : "G2 body-mutation: expected MUTATED=" + MUTATED + " got=" + Format(r) + " (a JIT/Cecil fallback would yield CONST=" + CONST + ")";
                            }
                        }
                        finally { domainB2.Dispose(); }
                    }
                }
                catch (Exception ex) { diff = "G2 threw " + ex.GetType().Name + ": " + ex.Message; }
                RecordCell(res, "G2 ConstGeneric<T> template body-mutation (AOT template really runs)", diff);
            }

            return res;
        }

        struct Wrap
        {
            public string Name;
            public object Expected;
            public Wrap(string name, object expected) { Name = name; Expected = expected; }
        }

        static bool ValueEqualsObj(object o, object expected)
        {
            if (o is ThrownMarker) return false;
            if (o == null || expected == null) return Equals(o, expected);
            if (expected is string) return o is string && (string)o == (string)expected;
            try { return Convert.ToInt64(o) == Convert.ToInt64(expected); } catch { return Equals(o, expected); }
        }

        sealed class ThrownMarker
        {
            public readonly Exception Ex;
            public ThrownMarker(Exception ex) { Ex = ex; }
        }

        static string Format(object o)
        {
            if (o is ThrownMarker tm) return "threw " + (tm.Ex?.GetType().Name ?? "?") + ": " + (tm.Ex?.Message ?? "");
            return o == null ? "null" : o.ToString();
        }

        static void RecordCell(Result res, string name, string diff)
        {
            if (diff == null)
            {
                res.Passed++;
                Console.WriteLine("[NeoStep25Ceg] " + name + ": PASS");
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
