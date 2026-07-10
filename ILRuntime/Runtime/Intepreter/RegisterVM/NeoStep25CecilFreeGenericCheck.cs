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
                // T-identity-token body (Box T + Unbox.Any T). Expected round-trip.
                // string-T wrapper omitted: the Run shim's NeoBoxReturnValue handles
                // primitive returns only (a string return is a separate dimension,
                // out of scope). int-T covers the Box/Unbox.Any T path (the
                // authoritative T-identity Cecil-free dispatch is the G3 fresh-
                // instance cell below). struct-T wrapper omitted: Box<IL-value-type>
                // then Unbox.Any<IL-value-type> is an engine-level Box/Unbox-of-IL-VT
                // gap (it FAILS the "A JIT" reference -- a Cecil-loaded run with no
                // T-identity machinery in play -- so it is NOT a T-identity Cecil-free
                // regression; out of scope for this change).
                new Wrap("WrapBoxUnboxInt",    4242),
                // MethodToken T-identity body (constrained. T callvirt on
                // IComparable<T>). int T: 7.CompareTo(5) > 0 (sign-normalized to 1
                // by the wrapper -- CompareTo's magnitude is not documented). The
                // authoritative Cecil-free MethodToken T-identity dispatch is the
                // G4 fresh-instance cell below. string T (the ref-T path) is
                // omitted: the constrained. ref-type arm throws on HEAD (an engine
                // gap, fails the A JIT reference too -> out of scope).
                new Wrap("WrapCompareElemsInt", 1),
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
                                // ConstGeneric<T> is an INSTANCE method (HasThis); the
                                // fresh generic-instance needs a `this`. Instantiate the
                                // Cecil-free probe type + pass it as the instance arg.
                                object probeInstance = null;
                                try { probeInstance = domainB2.Instantiate(ProbeFullName); }
                                catch { }
                                try { r = domainB2.Invoke(freshInst, probeInstance); }
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

            // ---- (G4) METHOD-TOKEN T-IDENTITY Cecil-free generic dispatch: drive a
            // FRESH CompareElems<int> instance via MakeGenericMethod (never inlined)
            // on a Cecil-free load. CompareElems<T> (where T: IComparable<T>) calls
            // a.CompareTo(b) -- a `constrained. T`-qualified callvirt whose METHOD
            // token is T-qualified (the declaring type is the generic instance
            // IComparable<T>, which contains the method generic param T). On HEAD the
            // Cecil-free S3 RebuildPatchesNoCecil REJECTS a MethodToken T-identity
            // patch (hasMethodIdentityToken -> BuildFromNeoRecord returns null -> the
            // template is skipped -> the method falls back to JIT, which a Cecil-free
            // AppDomain B cannot run), and the call throws. The follow-up fix re-
            // resolves the T-qualified method token Cecil-free (the declaring type
            // re-resolved via the synthetic GenericParameter + the method name/sig via
            // the loaded interface) + the template binds. Expected: 7.CompareTo(5) > 0
            // (sign-correct on the concrete int T).
            {
                const int CE_A = 7;
                const int CE_B = 5;
                res.TotalCells++;
                string diff;
                try
                {
                    var domainB4 = new ILRuntime.Runtime.Enviorment.AppDomain();
                    try
                    {
                        var lr4 = domainB4.LoadNeoAssembly(model, null);
                        var probeB4 = domainB4.GetType(ProbeFullName) as ILType;
                        // Find the CompareElems open definition with the S2-bound AOT
                        // template (same GetMethod-accumulation caveat as G3: pick the
                        // entry whose GenericMethodTemplateCache is non-null).
                        ILMethod ceDef = null;
                        int ceSeen = 0;
                        if (probeB4 != null && probeB4.GetMethods() != null)
                        {
                            foreach (var mm in probeB4.GetMethods())
                            {
                                var ilm = mm as ILMethod;
                                if (ilm == null || ilm.IsGenericInstance) continue;
                                if (ilm.Name == "CompareElems")
                                {
                                    ceSeen++;
                                    if (ilm.GenericMethodTemplateCache != null) { ceDef = ilm; break; }
                                    if (ceDef == null) ceDef = ilm;
                                }
                            }
                        }
                        IType intT4 = null;
                        try { intT4 = domainB4.GetType("System.Int32"); } catch { }
                        object r;
                        if (ceDef == null || intT4 == null)
                        {
                            diff = "G4: ceDef=" + (ceDef != null) + " intT=" + (intT4 != null) + " ceSeen=" + ceSeen;
                        }
                        else
                        {
                            var freshInst = ceDef.MakeGenericMethod(new IType[] { intT4 }) as ILMethod;
                            object probeInstance = null;
                            try { probeInstance = domainB4.Instantiate(ProbeFullName); }
                            catch { }
                            try { r = domainB4.Invoke(freshInst, probeInstance, CE_A, CE_B); }
                            catch (Exception ex) { r = new ThrownMarker(ex); }
                            // 7.CompareTo(5) == 2 (positive). Sign-correct check
                            // (CompareTo is documented to return a value whose sign is
                            // correct, not necessarily the exact magnitude).
                            bool ok = false;
                            if (!(r is ThrownMarker))
                            {
                                try { ok = Convert.ToInt64(r) > 0; } catch { }
                            }
                            diff = ok
                                ? null
                                : "G4 MethodToken T-identity CompareElems<int>: expected >0 (7.CompareTo(5)) got=" + Format(r)
                                  + " (ceSeen=" + ceSeen + " ceDefCache=" + (ceDef.GenericMethodTemplateCache != null) + ")";
                        }
                    }
                    finally { domainB4.Dispose(); }
                }
                catch (Exception ex) { diff = "G4 threw " + ex.GetType().Name + ": " + ex.Message; }
                RecordCell(res, "G4 CompareElems<T> MethodToken T-identity Cecil-free dispatch", diff);
            }

            // ---- (G3) T-IDENTITY-TOKEN Cecil-free generic dispatch: drive a FRESH
            // BoxUnbox<int> instance via MakeGenericMethod (never inlined) on a
            // Cecil-free load. BoxUnbox<T> carries a `Box T` + `Unbox.Any T` -- a
            // T-identity TypeToken patch site. On HEAD the Cecil-free S3
            // BuildFromNeoRecord REJECTS the template (T-identity token -> S3), so
            // BoxUnbox<T> falls back to JIT (which a Cecil-free AppDomain B cannot
            // run -- no Cecil module), and the call throws. The follow-up fix adds
            // a Cecil-free GenericParamIdx-keyed T-substitution so the concrete T
            // hash is re-derived Cecil-free + the template binds. Driving a FRESH
            // instance (never inlined) proves the AOT-template T-substitution path,
            // not the compile-side specialization. Expected: 4242 round-trips.
            {
                const int BU_INPUT = 4242;
                res.TotalCells++;
                string diff;
                try
                {
                    var domainB3 = new ILRuntime.Runtime.Enviorment.AppDomain();
                    try
                    {
                        var lr3 = domainB3.LoadNeoAssembly(model, null);
                        var probeB3 = domainB3.GetType(ProbeFullName) as ILType;
                        // Find the BoxUnbox open definition with the S2-bound AOT template.
                        // NOTE: the runtime's GetMethod appends a generic INSTANCE to the
                        // type's methods list on each generic-call resolution (ILType.cs
                        // :2465, a pre-existing accumulation pattern), so GetMethods()
                        // may surface multiple BoxUnbox entries (a non-cached instance
                        // WITHOUT a definition + the cached open definition). Pick the
                        // one whose GenericMethodTemplateCache is non-null (the S2-bound
                        // open def); it is the authoritative generic definition the
                        // fresh instance's genericDefinition must point at.
                        ILMethod buDef = null;
                        int buSeen = 0;
                        if (probeB3 != null && probeB3.GetMethods() != null)
                        {
                            foreach (var mm in probeB3.GetMethods())
                            {
                                var ilm = mm as ILMethod;
                                if (ilm == null || ilm.IsGenericInstance) continue;
                                if (ilm.Name == "BoxUnbox")
                                {
                                    buSeen++;
                                    if (ilm.GenericMethodTemplateCache != null) { buDef = ilm; break; }
                                    if (buDef == null) buDef = ilm;  // fallback to first seen
                                }
                            }
                        }
                        IType intT = null;
                        try { intT = domainB3.GetType("System.Int32"); } catch { }
                        object r;
                        if (buDef == null || intT == null)
                        {
                            diff = "G3: buDef=" + (buDef != null) + " intT=" + (intT != null) + " buSeen=" + buSeen;
                        }
                        else
                        {
                            var freshInst = buDef.MakeGenericMethod(new IType[] { intT }) as ILMethod;
                            object probeInstance = null;
                            try { probeInstance = domainB3.Instantiate(ProbeFullName); }
                            catch { }
                            try { r = domainB3.Invoke(freshInst, probeInstance, BU_INPUT); }
                            catch (Exception ex) { r = new ThrownMarker(ex); }
                            diff = ValueEqualsObj(r, BU_INPUT)
                                ? null
                                : "G3 T-identity BoxUnbox<int>: expected=" + BU_INPUT + " got=" + Format(r)
                                  + " (buSeen=" + buSeen + " buDefCache=" + (buDef.GenericMethodTemplateCache != null) + ")";
                        }
                    }
                    finally { domainB3.Dispose(); }
                }
                catch (Exception ex) { diff = "G3 threw " + ex.GetType().Name + ": " + ex.Message; }
                RecordCell(res, "G3 BoxUnbox<T> T-identity-token Cecil-free dispatch", diff);
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
