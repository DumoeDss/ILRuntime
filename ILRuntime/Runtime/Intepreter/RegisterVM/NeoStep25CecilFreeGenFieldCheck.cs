#if ENABLE_NEO_MODE && DEBUG
using System;
using System.Collections.Generic;
using System.IO;

using ILRuntime.CLR.Method;
using ILRuntime.CLR.TypeSystem;
using ILRuntime.Runtime.NeoAOT;

namespace ILRuntime.Runtime.Intepreter.RegisterVM
{
    /// <summary>
    /// Step 25 generic-TYPE-instance field capstone (host-side, DEBUG+Neo only):
    /// the Cecil-free load of an IL class whose INSTANCE FIELD TYPES are generic
    /// instantiations (List&lt;int&gt;, List&lt;string&gt;, List&lt;IL-T&gt;,
    /// Dictionary&lt;int,string&gt;). The child-8 Cecil-free machinery covered a
    /// generic-METHOD instance; this is the generic-TYPE-instance FIELD surface.
    ///
    /// On HEAD a generic-instantiation field type serializes (HybridPatch
    /// TypeReferencePatchInfo) as IsGenericInstance=true + an ElementType (the
    /// generic def, e.g. List`1) + GenericArguments[] (the type args), but the
    /// Cecil-free ILType factory resolves each field type via ResolveNamedIType
    /// (reads ONLY info.Name, which is NULL for a generic instance), so the field
    /// type resolves to null -> the wrapper fails at field access.
    ///
    /// Flow (mirrors the child-8 capstone): compile a .neo for the probe + the IL
    /// generic arg type in AppDomain A (Cecil-loaded), load Cecil-free into a FRESH
    /// AppDomain B, invoke each wrapper via domainB.Invoke, assert == known-expected
    /// (independently computed in A via JIT).
    ///
    /// Invoked host-side (CLI special mode "NeoStep25CecilFreeGenField").
    /// </summary>
    public static class NeoStep25CecilFreeGenFieldCheck
    {
        public class Result
        {
            public int TotalCells;
            public int Passed;
            public int Failed;
            public List<string> Failures = new List<string>();
        }

        const string ProbeFullName = "TestCases.NeoStep25CecilFreeGenFieldProbe";
        const string ItemFullName = "TestCases.NeoStep25GenFieldItem";

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

            var wrappers = new[]
            {
                new Wrap("WrapListIntCount",  3),
                new Wrap("WrapListStrCount",  2),
                new Wrap("WrapListIlTCount",  2),
                new Wrap("WrapListIlTIndex",  7),
                new Wrap("WrapDictCount",     2),
            };
            var expected = new Dictionary<string, object>();

            // ---- (A) known-expected via A's JIT (the Cecil-loaded reference) ----
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

            // ---- compile a .neo for the probe + the IL generic arg type in A ----
            NeoAssemblyModel model;
            try
            {
                var compileSet = new List<ILType> { probeTypeA };
                if (appdomainA.LoadedTypes.TryGetValue(ItemFullName, out var itemIt) && itemIt is ILType itemIl)
                    compileSet.Add(itemIl);
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
            RecordCell(res, "compile .neo (with generic-instance field types) in A", null);

            // ---- (F) FIELD-TYPE-RESOLUTION guard: the functional wrappers above
            // pass on HEAD too (a reference-typed generic-instance field is
            // reference-SLOTTED -> the field access resolves via the reference
            // offset + the callvirt operand's OWN declaring type, NOT via the
            // resolved field type). So the functional cells alone do NOT prove the
            // field TYPE resolved. This cell probes the resolved field type
            // DIRECTLY (via ILType.GetField -> fieldTypes[i]) on a Cecil-free load.
            // On HEAD each generic-instance field type resolves to NULL (the field-
            // type serializer stores IsGenericInstance + ElementType + GenericArgs
            // but NOT Name, and the Cecil-free resolver reads ONLY Name). Expected
            // post-fix: each resolves to the matching generic instance. ----
            {
                res.TotalCells++;
                string diff;
                try
                {
                    var domainB = new ILRuntime.Runtime.Enviorment.AppDomain();
                    try
                    {
                        domainB.LoadNeoAssembly(model, null);
                        var probeB = domainB.GetType(ProbeFullName) as ILType;
                        var sb = new System.Text.StringBuilder();
                        if (probeB == null)
                        {
                            diff = "F: probe not loaded in B";
                        }
                        else
                        {
                            // (name, must-be-non-null, must-contain-substring)
                            var probes = new[]
                            {
                                ("intItems", true, "List"),
                                ("strItems", true, "List"),
                                ("ilItems", true, "List"),
                                ("map",      true, "Dictionary"),
                            };
                            int okCount = 0;
                            foreach (var (fn, mustNonNull, subs) in probes)
                            {
                                IType ft = null;
                                try { ft = probeB.GetField(fn, out _); } catch { }
                                if (ft == null) sb.Append(fn).Append("=NULL; ");
                                else if (ft.FullName == null || !ft.FullName.Contains(subs))
                                    sb.Append(fn).Append("='").Append(ft.FullName ?? "<null>").Append("' (no '").Append(subs).Append("'); ");
                                else okCount++;
                            }
                            diff = (okCount == probes.Length) ? null
                                : ("field-type resolution: " + okCount + "/" + probes.Length + " non-null + matched [" + sb + "]");
                        }
                    }
                    finally { domainB.Dispose(); }
                }
                catch (Exception ex) { diff = "F threw " + ex.GetType().Name + ": " + ex.Message; }
                RecordCell(res, "F Cecil-free generic-instance field TYPE resolution", diff);
            }

            // ---- (B) the capstone: load Cecil-free into a FRESH B per wrapper ----
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
                RecordCell(res, "B Cecil-free generic-field " + w.Name, diff);
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
                Console.WriteLine("[NeoStep25GenField] " + name + ": PASS");
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
