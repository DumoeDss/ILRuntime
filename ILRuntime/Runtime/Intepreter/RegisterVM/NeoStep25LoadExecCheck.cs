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
    /// Step 25 V2 load+execute self-check (host-side, DEBUG+Neo only) -- the
    /// capstone. The FIRST end-to-end "deserialize + ExecuteNeo == JIT" proof.
    ///
    /// For a small dedicated NON-GENERIC probe type (TestCases.NeoStep25LoadProbe),
    /// this: (1) compiles a .neo via the SAME public NeoCompiler driver the
    /// ilrt_neoc CLI uses -> an in-memory MemoryStream; (2) reads it back via
    /// NeoAssemblyReader -> NeoAssemblyModel; (3) runs each probe method via the
    /// JIT path and captures the result BEFORE attach; (4) NeoAssemblyLoader.Attach
    /// overwrites the matched methods' bodies with the deserialized AOT bodies; (5)
    /// runs the SAME methods again (now via ExecuteNeo on the AOT body) and
    /// captures the result; (6) asserts each result EQUALS its known-expected value
    /// (which implies JIT == AOT and rules out a "both-garbage" false pass).
    ///
    /// The matrix (non-generic only, parameterless -- the Neo Run entry is a no-arg
    /// shim by convention): arithmetic; try/catch EH (generic catch); TYPED catch
    /// (a specific type -- exercises catch-type-ref resolution + typed EH dispatch);
    /// mixed locals + an internal byref call (exercises the NeoCallParamMap
    /// rebuild); a BODY-MUTATION cell (mutate a deserialized Ldc_I4 constant before
    /// Attach -> assert the post-Attach run yields the MUTATED value, NOT the JIT
    /// value -> PROVES ExecuteNeo runs the genuine AOT body, not a JIT fallback).
    /// A divergent AOT body / EH / NeoCallParam rebuild trips the per-cell check.
    ///
    /// Invoked host-side (CLI special mode "NeoStep25LoadExec").
    /// </summary>
    public static class NeoStep25LoadExecCheck
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

        public static Result Run(ILRuntime.Runtime.Enviorment.AppDomain appdomain)
        {
            var res = new Result();

            // ---- locate the probe type ----
            if (!appdomain.LoadedTypes.TryGetValue("TestCases.NeoStep25LoadProbe", out var probeIType) ||
                !(probeIType is ILType probeType))
            {
                res.Failures.Add("TestCases.NeoStep25LoadProbe not loaded / not an ILType");
                res.TotalCells = 1; res.Failed = 1;
                return res;
            }

            // ===== Cell 1: compile a .neo via the SAME driver the CLI uses =====
            // The probe is designed to compile cleanly under Neo; a skip here is a
            // real wiring bug (an op the probe should not exercise NIE-d).
            NeoCompilerResult driverResult;
            NeoAssemblyModel model;
            try
            {
                using (var ms = new MemoryStream())
                {
                    driverResult = new NeoCompiler().Compile(new[] { probeType }, ms);
                    ms.Position = 0;
                    model = NeoAssemblyReader.Read(ms);
                }
            }
            catch (Exception ex)
            {
                res.TotalCells = 1; res.Failed = 1;
                res.Failures.Add($"compile/read threw {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"  [FAIL] compile/read threw {ex.GetType().Name}: {ex.Message}");
                return res;
            }
            res.TotalCells++;
            if (!driverResult.IsComplete)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var s in driverResult.Skipped) sb.Append(s.MethodDisplay).Append(" (").Append(s.ExceptionType).Append("); ");
                res.Failed++; res.Failures.Add($"compile skipped {driverResult.Skipped.Count}: {sb}");
                Console.WriteLine($"  [FAIL] compile skipped {driverResult.Skipped.Count}: {sb}");
            }
            else
            {
                res.Passed++;
                Console.WriteLine($"[NeoStep25] compile: PASS {driverResult.MethodsCompiled} methods, {driverResult.TemplatesCaptured} templates");
            }

            // ===== The probe matrix (non-generic, parameterless): method + expected =====
            var cells = new Cell[]
            {
                new Cell("ArithProbe",       47),
                new Cell("TryCatchProbe",    100),
                new Cell("MixedLocalsProbe", 61),
                new Cell("TypedCatchProbe",  200),   // typed catch (specific type, not Exception)
            };

            // ===== Capture JIT results BEFORE attach (each method's JIT body runs) =====
            var jitResults = new Dictionary<string, object>();
            foreach (var c in cells)
            {
                var m = probeType.GetMethod(c.Name, 0);
                if (m == null)
                {
                    res.TotalCells++; res.Failed++;
                    res.Failures.Add($"[pre] method not found: {c.Name}");
                    continue;
                }
                object r;
                try { r = appdomain.Invoke(m, null); }
                catch (Exception ex) { r = new ThrownMarker(ex); }
                jitResults[c.Name] = r;
            }

            // ===== Attach the deserialized AOT bodies (overwrites JIT bodies) =====
            NeoLoadReport report;
            try { report = NeoAssemblyLoader.Attach(appdomain, model); }
            catch (Exception ex)
            {
                res.TotalCells++; res.Failed++;
                res.Failures.Add($"Attach threw {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"  [FAIL] Attach threw {ex.GetType().Name}: {ex.Message}");
                return res;
            }
            res.AttachedCount = report.Attached.Count;
            res.SkippedCount = report.Skipped.Count;
            res.Skipped.AddRange(report.Skipped.ConvertAll(s => s.reason + ": " + s.target));
            Console.WriteLine($"[NeoStep25] Attach: {report.Attached.Count} attached, {report.Skipped.Count} skipped");

            // ===== Attach coverage: the 5 parameterless probe methods all attached =====
            // (BumpRef has a byref param + .ctor is a ctor -- both attach too but are
            // not parameterless harness tests; the 5 here are the matrix + ConstProbe.)
            res.TotalCells++;
            bool attachSawAll = true;
            foreach (var probeMethod in new[] { "ArithProbe", "TryCatchProbe", "MixedLocalsProbe", "TypedCatchProbe", "ConstProbe" })
            {
                bool found = false;
                foreach (var a in report.Attached) if (a.EndsWith("." + probeMethod)) { found = true; break; }
                if (!found) attachSawAll = false;
            }
            if (attachSawAll)
            {
                res.Passed++;
                Console.WriteLine("[NeoStep25] Attach coverage: PASS (all 5 parameterless probe methods AOT-attached)");
            }
            else
            {
                res.Failed++;
                res.Failures.Add("Attach did not cover all 5 parameterless probe methods");
            }

            // ===== Per-cell: pin JIT==expected, then AOT==expected (=> JIT==AOT) =====
            // Pinning the EXPECTED value (not just JIT==AOT) rules out a "both
            // garbage" false pass: if the Run shim or a rebuild returns garbage, the
            // expected-value check trips even when JIT==AOT.
            foreach (var c in cells)
            {
                // JIT vs expected.
                res.TotalCells++;
                {
                    var jit = jitResults[c.Name];
                    string diff = ValueEquals(jit, c.Expected)
                        ? null : $"JIT={Format(jit)} expected={c.Expected}";
                    RecordCell(res, $"{c.Name} JIT", diff);
                }
                // AOT vs expected (post-attach: isNeoAotBody set + body overwritten).
                res.TotalCells++;
                {
                    var m = probeType.GetMethod(c.Name, 0);
                    var ilm = m as ILMethod;
                    if (m == null || ilm == null || !ilm.isNeoAotBody)
                    {
                        res.Failed++; res.Failures.Add($"{c.Name} AOT: isNeoAotBody not set post-attach");
                        Console.WriteLine($"  [FAIL] {c.Name} AOT: isNeoAotBody not set post-attach");
                        continue;
                    }
                    object aot;
                    try { aot = appdomain.Invoke(m, null); }
                    catch (Exception ex) { aot = new ThrownMarker(ex); }
                    string diff = ValueEquals(aot, c.Expected)
                        ? null : $"AOT={Format(aot)} expected={c.Expected} (JIT={Format(jitResults[c.Name])})";
                    RecordCell(res, $"{c.Name} AOT", diff);
                }
            }

            // ===== Body-mutation cell -- folds the reviewer's Probe A into the
            // PERMANENT matrix: PROVE ExecuteNeo runs the genuine deserialized AOT
            // body, NOT the JIT body. The isNeoAotBody flag assertion above proves
            // the FLAG is set but NOT that the AOT body runs (the flag only short-
            // circuits BodyRegister; ExecuteNeo reads CompiledFrame.NeoExecuteBody).
            // A green JIT==AOT check is INSUFFICIENT on its own: Step 23 established
            // AOT body == JIT body byte-for-byte, so JIT==AOT holds EVEN IF the JIT
            // body still ran. The decisive proof is to MUTATE a deserialized
            // constant before Attach and observe the MUTATED value post-Attach -- if
            // the JIT body ran, the result would be the UNmutated value.
            //
            // Flow: FRESH compile -> model2 (an independent .neo, so the mutation
            // never touches the main flow's model); find ConstProbe's method-def
            // record; rewrite its Ldc_I4 <CONST> Operand to MUTATED in the
            // deserialized body array BEFORE Attach; Attach model2 (ConstProbe now
            // gets the MUTATED body -- the other methods are re-attached with their
            // correct bodies, idempotent); run ConstProbe; assert MUTATED. The
            // direct-reference assignment in InitCodeBodyFromNeo
            // (compiledFrame.NeoExecuteBody = rec.NeoExecuteBody) means the mutated
            // array IS what ExecuteNeo reads.
            {
                const int CONST = 1234567;    // ConstProbe's JIT value (unmutated)
                const int MUTATED = 7654321;  // the sentinel the AOT body is mutated to
                res.TotalCells++;
                string diff;
                try
                {
                    NeoAssemblyModel model2;
                    using (var ms2 = new MemoryStream())
                    {
                        new NeoCompiler().Compile(new[] { probeType }, ms2);
                        ms2.Position = 0;
                        model2 = NeoAssemblyReader.Read(ms2);
                    }
                    // Locate ConstProbe's method-def record by name (MethodRefIdx ->
                    // MethodRefTable -> Name), mirroring NeoAssemblyLoader.Attach.
                    int defIdx = -1;
                    for (int i = 0; i < model2.MethodDefs.Length; i++)
                    {
                        var r = model2.MethodDefs[i];
                        if (r.MethodRefIdx < 0 || r.MethodRefIdx >= model2.MethodRefs.Length) continue;
                        if (model2.MethodRefs[r.MethodRefIdx].Name == "ConstProbe") { defIdx = i; break; }
                    }
                    // Find the Ldc_I4 <CONST> in its body (CONST is outside sbyte
                    // range -> the JIT emits a real Ldc_I4 with the full Operand).
                    OpCodeR[] cbody = (defIdx >= 0) ? model2.MethodDefs[defIdx].NeoExecuteBody : null;
                    int mutateAt = -1;
                    if (cbody != null)
                    {
                        for (int j = 0; j < cbody.Length; j++)
                            if (cbody[j].Code == OpCodeREnum.Ldc_I4 && cbody[j].Operand == CONST) { mutateAt = j; break; }
                    }
                    if (mutateAt < 0)
                    {
                        // The body shape changed -> dump it so the failure is diagnosable.
                        var sb = new System.Text.StringBuilder();
                        if (cbody == null) sb.Append("null body");
                        else for (int j = 0; j < cbody.Length; j++)
                        {
                            if (j > 0) sb.Append(',');
                            sb.Append(cbody[j].Code.ToString());
                            if (cbody[j].Code == OpCodeREnum.Ldc_I4) sb.Append('=').Append(cbody[j].Operand);
                        }
                        diff = $"ConstProbe Ldc_I4={CONST} not found (defIdx={defIdx}, body=[{sb}])";
                    }
                    else
                    {
                        cbody[mutateAt].Operand = MUTATED;            // mutate the deserialized AOT body
                        NeoAssemblyLoader.Attach(appdomain, model2);  // ConstProbe -> MUTATED body
                        object m;
                        try { m = appdomain.Invoke(probeType.GetMethod("ConstProbe", 0), null); }
                        catch (Exception ex) { m = new ThrownMarker(ex); }
                        diff = ValueEquals(m, MUTATED)
                            ? null
                            : $"expected MUTATED={MUTATED} got={Format(m)} (a JIT-body run would yield CONST={CONST})";
                    }
                }
                catch (Exception ex)
                {
                    diff = $"body-mutation cell threw {ex.GetType().Name}: {ex.Message}";
                }
                RecordCell(res, "ConstProbe body-mutation (AOT body really runs)", diff);
            }

            // ===== S2 generic cells (clone+patch from the .neo template at load) =====
            //
            // The main Attach above ALSO consumed model.Templates: it reconstructed
            // the Echo/ConstGeneric GenericMethodTemplates from the .neo + bound them
            // to the generic definitions' GenericMethodTemplateCache (OVERWRITING the
            // JIT-captured template from the compile step). So a generic-instance call
            // on the AOT-loaded probe now routes through Step-22 CloneAndPatch from the
            // AOT template. These cells prove that, adversarially.
            //
            // (1) FUNCTIONAL cells: parameterless wrappers calling Echo<T> at concrete
            //     T's (int / long[8-byte prim] / string[ref] / struct) -> assert each
            //     result EQUALS its known-expected value. WrapEchoLong is ALSO the
            //     (2) FRESH-INSTANCE cell: the compile step only instantiated Echo<int>
            //     (capture-eligible), so Echo<long> was NEVER instantiated before
            //     attach -> its first call MUST route through CloneAndPatch via the AOT
            //     template (no JIT-cached instance body exists), isolating the AOT path.
            // (3) TEMPLATE BODY-MUTATION cell: mutate ConstGeneric's deserialized
            //     TemplateBody Ldc_I4 before attach -> observe the MUTATED value (NOT
            //     the JIT value) -> proves CloneAndPatch ran the genuine AOT template
            //     body, not the JIT-captured one (a green functional cell alone is
            //     insufficient: Step 23 made the bodies byte-identical).
            // (4) STRUCTURAL-EQUIVALENCE cell (DEBUG host-side): the AOT-reconstructed
            //     template's CloneAndPatch body EQUALS the per-occurrence JIT body for
            //     each concrete T (reusing the Step-22 BodiesEqual comparator).

            // ---- locate the generic definitions (open defs, GPC > 0) ----
            ILMethod echoDef = null, constDef = null;
            if (probeType.GetMethods() != null)
            {
                foreach (var mm in probeType.GetMethods())
                {
                    var ilm = mm as ILMethod;
                    if (ilm == null || ilm.IsGenericInstance || ilm.GenericParameterCount <= 0) continue;
                    if (ilm.Name == "Echo") echoDef = ilm;
                    else if (ilm.Name == "ConstGeneric") constDef = ilm;
                }
            }

            // ---- S2 attach coverage: both generic templates bound ----
            res.TotalCells++;
            {
                bool echoBound = echoDef != null && echoDef.GenericMethodTemplateCache != null;
                bool constBound = constDef != null && constDef.GenericMethodTemplateCache != null;
                string diff = (echoBound && constBound)
                    ? null
                    : $"echoDef={(echoDef != null)} echoTpl={(echoDef?.GenericMethodTemplateCache != null)} constDef={(constDef != null)} constTpl={(constDef?.GenericMethodTemplateCache != null)}";
                RecordCell(res, "S2 generic template attach coverage", diff);
            }

            // ---- (1)+(2) FUNCTIONAL + FRESH-INSTANCE cells ----
            var genCells = new GenCell[]
            {
                new GenCell("WrapEchoInt",    42),            // int (4-byte prim)
                new GenCell("WrapEchoLong",   9000000000L),   // long (8-byte prim) -- ALSO fresh-instance
                new GenCell("WrapEchoRef",    1234567),       // ref-T (string) via ConstGeneric<string> -> int
                new GenCell("WrapEchoStruct", 77),            // IL struct
            };
            foreach (var gc in genCells)
            {
                res.TotalCells++;
                var m = probeType.GetMethod(gc.Name, 0);
                object r;
                try { r = appdomain.Invoke(m, null); }
                catch (Exception ex) { r = new ThrownMarker(ex); }
                string diff = ValueEqualsObj(r, gc.Expected)
                    ? null : $"got={Format(r)} expected={gc.Expected}";
                string label = gc.Name == "WrapEchoLong"
                    ? $"{gc.Name} (AOT CloneAndPatch + fresh long-T instance)"
                    : $"generic {gc.Name} (AOT CloneAndPatch)";
                RecordCell(res, label, diff);
            }

            // ---- (4) STRUCTURAL-EQUIVALENCE cell: AOT-CloneAndPatch body == per-
            //      occurrence JIT body for each concrete T (DEBUG host-side). The AOT
            //      template is the one bound on echoDef.GenericMethodTemplateCache. ----
            if (echoDef != null)
            {
                var aotTpl = echoDef.GenericMethodTemplateCache;
                var tlist = BuildConcreteTs(appdomain);
                foreach (var tkv in tlist)
                {
                    res.TotalCells++;
                    string tname = tkv.Key;
                    IType T = tkv.Value;
                    string diff;
                    try
                    {
                        ILMethod inst;
                        try { inst = echoDef.MakeGenericMethod(new IType[] { T }) as ILMethod; }
                        catch (Exception ex) { diff = $"{tname}: MakeGenericMethod threw {ex.GetType().Name}"; RecordCell(res, $"Echo<{tname}> structural-equiv", diff); continue; }
                        if (inst == null) { diff = $"{tname}: instance null"; RecordCell(res, $"Echo<{tname}> structural-equiv", diff); continue; }
                        var perOcc = GenericMethodTemplateOps.CompilePerOccurrenceNeoBody(appdomain, probeType, inst);
                        var aotBody = GenericMethodTemplateOps.CompileViaAotTemplateNeoBody(appdomain, probeType, inst, aotTpl);
                        bool eq = GenericMethodTemplateOps.BodiesEqual(perOcc, aotBody);
                        diff = eq ? null : $"BODY MISMATCH perOccLen={perOcc?.Length ?? -1} aotLen={aotBody?.Length ?? -1}";
                    }
                    catch (Exception ex)
                    {
                        diff = $"{tname}: structural-equiv threw {ex.GetType().Name}: {ex.Message}";
                    }
                    RecordCell(res, $"Echo<{tname}> structural-equiv (AOT == JIT)", diff);
                }
            }

            // ---- (3) TEMPLATE BODY-MUTATION cell: mutate ConstGeneric's deserialized
            //      TemplateBody Ldc_I4 CONST -> MUTATED before attach, run a wrapper
            //      calling a FRESH generic instance (ConstGeneric<string>), assert
            //      MUTATED. Observing MUTATED proves CloneAndPatch ran the AOT template
            //      body (the JIT-captured template still carries CONST). ----
            {
                const int CONST = 1234567;
                const int MUTATED = 7654321;
                res.TotalCells++;
                string diff;
                try
                {
                    NeoAssemblyModel model3;
                    using (var ms3 = new MemoryStream())
                    {
                        new NeoCompiler().Compile(new[] { probeType }, ms3);
                        ms3.Position = 0;
                        model3 = NeoAssemblyReader.Read(ms3);
                    }
                    // Find ConstGeneric's template record (by MethodRef name).
                    int tplIdx = -1;
                    if (model3.Templates != null)
                    {
                        for (int i = 0; i < model3.Templates.Length; i++)
                        {
                            var trec = model3.Templates[i];
                            if (trec.DefinitionMethodRefIdx < 0 || trec.DefinitionMethodRefIdx >= model3.MethodRefs.Length) continue;
                            if (model3.MethodRefs[trec.DefinitionMethodRefIdx].Name == "ConstGeneric") { tplIdx = i; break; }
                        }
                    }
                    OpCodeR[] tbody = (tplIdx >= 0) ? model3.Templates[tplIdx].TemplateBody : null;
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
                        diff = $"ConstGeneric template Ldc_I4={CONST} not found [{sb}]";
                    }
                    else
                    {
                        tbody[mutateAt].Operand = MUTATED;            // mutate the deserialized AOT template body
                        NeoAssemblyLoader.Attach(appdomain, model3);  // binds the MUTATED template (overwrites)
                        // Drive a FRESH ConstGeneric<string> instance (ref-T) DIRECTLY --
                        // NOT via a wrapper. A wrapper's Call resolves its generic callee
                        // via AppDomain.GetMethod, which returns the instance registered
                        // during the compile step's force-compile (its bodyRegister is
                        // already cached with the unmutated template). MakeGenericMethod
                        // returns a NEW instance (bodyRegister null), so its BodyRegister
                        // getter re-runs InitCodeBody -> the Step-22 hook -> CloneAndPatch
                        // against the MUTATED AOT template. This is the fresh-instance
                        // cell (D5 cell 2) AND the mutation cell (D5 cell 1) combined.
                        IType stringT = null;
                        try { stringT = appdomain.GetType("System.String"); } catch { }
                        object r;
                        if (constDef == null || stringT == null)
                        {
                            diff = $"ConstGeneric def={(constDef != null)} stringT={(stringT != null)}";
                        }
                        else
                        {
                            var freshInst = constDef.MakeGenericMethod(new IType[] { stringT }) as ILMethod;
                            try { r = appdomain.Invoke(freshInst, null); }
                            catch (Exception ex) { r = new ThrownMarker(ex); }
                            diff = ValueEqualsObj(r, MUTATED)
                                ? null
                                : $"expected MUTATED={MUTATED} got={Format(r)} (a JIT-template run would yield CONST={CONST})";
                        }
                    }
                }
                catch (Exception ex)
                {
                    diff = $"generic body-mutation cell threw {ex.GetType().Name}: {ex.Message}";
                }
                RecordCell(res, "ConstGeneric<T> template body-mutation (AOT template really runs)", diff);
            }

            return res;
        }

        // The concrete-T matrix for the structural-equivalence cell: int (4-byte
        // prim), long (8-byte prim), string (ref), NeoStep25ProbeVal (IL struct).
        static List<KeyValuePair<string, IType>> BuildConcreteTs(ILRuntime.Runtime.Enviorment.AppDomain appdomain)
        {
            var ts = new List<KeyValuePair<string, IType>>();
            ts.Add(new KeyValuePair<string, IType>("int", appdomain.IntType));
            ts.Add(new KeyValuePair<string, IType>("long", appdomain.LongType));
            IType stringT = null;
            try { stringT = appdomain.GetType("System.String"); } catch { }
            if (stringT != null) ts.Add(new KeyValuePair<string, IType>("string", stringT));
            if (appdomain.LoadedTypes.TryGetValue("TestCases.NeoStep25ProbeVal", out var structIt) && structIt is ILType structIl)
                ts.Add(new KeyValuePair<string, IType>("struct", structIl));
            return ts;
        }

        static bool ValueEqualsObj(object o, object expected)
        {
            if (o is ThrownMarker) return false;
            if (o == null || expected == null) return Equals(o, expected);
            if (expected is string) return o is string && (string)o == (string)expected;
            try { return Convert.ToInt64(o) == Convert.ToInt64(expected); } catch { return Equals(o, expected); }
        }

        struct GenCell
        {
            public string Name;
            public object Expected;
            public GenCell(string name, object expected) { Name = name; Expected = expected; }
        }

        struct Cell
        {
            public string Name;
            public int Expected;
            public Cell(string name, int expected) { Name = name; Expected = expected; }
        }

        sealed class ThrownMarker
        {
            public readonly Exception Ex;
            public ThrownMarker(Exception ex) { Ex = ex; }
        }

        static bool ValueEquals(object o, int expected)
        {
            if (o is ThrownMarker) return false;
            if (o == null) return false;
            try { return Convert.ToInt64(o) == expected; } catch { return false; }
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
                Console.WriteLine($"[NeoStep25] {name}: PASS");
            }
            else
            {
                res.Failed++; res.Failures.Add($"{name}: {diff}");
                Console.WriteLine($"  [FAIL] {name}: {diff}");
            }
        }
    }
}
#endif
