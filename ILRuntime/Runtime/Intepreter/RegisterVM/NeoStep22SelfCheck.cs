using System;
using System.Collections.Generic;

using ILRuntime.CLR.Method;
using ILRuntime.CLR.TypeSystem;
using ILRuntime.Runtime.Intepreter.OpCodes;

namespace ILRuntime.Runtime.Intepreter.RegisterVM
{
#if ENABLE_NEO_MODE
    /// <summary>
    /// Step 22 V1 structural-equivalence self-check (host-side, DEBUG+Neo only).
    ///
    /// For each matrix generic method + each concrete T, compiles the SAME generic
    /// instance via BOTH paths -- the per-occurrence JIT (the reference) and
    /// CloneAndPatch (the template path) -- and asserts the two NeoExecuteBody
    /// arrays are structurally equal (same length, Code, Register1/2/3, Operand,
    /// Operand2/3/4 per index). This is the load-bearing correctness proof for the
    /// template mechanism (there is no FAIL-on-HEAD functional probe -- the JIT
    /// path already runs every generic correctly; structural body equivalence IS
    /// the correctness statement).
    ///
    /// Invoked host-side (CLI special mode), NOT as an interpreted NeoStep test.
    /// </summary>
    public static class NeoStep22SelfCheck
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
            if (!appdomain.LoadedTypes.TryGetValue("TestCases.NeoStep22GenericProbes", out var probesIType))
            {
                res.Failures.Add("TestCases.NeoStep22GenericProbes not loaded");
                res.TotalCells = 1; res.Failed = 1;
                return res;
            }
            var probesType = probesIType as ILType;
            if (probesType == null)
            {
                res.Failures.Add("NeoStep22GenericProbes is not an ILType");
                res.TotalCells = 1; res.Failed = 1;
                return res;
            }
            // T values: int (prim 4), long (prim 8), object (ref CLR),
            // NeoStep22RefClass (ref IL), NeoStep22Struct (value IL).
            var ts = new List<KeyValuePair<string, IType>>();
            ts.Add(new KeyValuePair<string, IType>("int", appdomain.IntType));
            ts.Add(new KeyValuePair<string, IType>("long", appdomain.LongType));
            ts.Add(new KeyValuePair<string, IType>("object", appdomain.ObjectType));
            if (appdomain.LoadedTypes.TryGetValue("TestCases.NeoStep22RefClass", out var refIl))
                ts.Add(new KeyValuePair<string, IType>("RefClass", refIl));
            if (appdomain.LoadedTypes.TryGetValue("TestCases.NeoStep22Struct", out var structIl))
                ts.Add(new KeyValuePair<string, IType>("Struct", structIl));

            // Matrix methods (single generic arg T).
            string[] methodNames = { "ProbeBasic", "MakeArray", "StoreRef", "LoadRef", "BoxIt", "HashIt", "EqualsIt", "CompareThem", "BranchIt", "SwitchIt", "TryCatch" };
            foreach (var mname in methodNames)
            {
                var methodDef = probesType.GetMethod(mname) as ILMethod;
                if (methodDef == null)
                {
                    res.Failures.Add($"method {mname} not found");
                    res.TotalCells++; res.Failed++;
                    continue;
                }
                // Force-build the template (capture from T=int, capture-eligible).
                var template = GenericMethodTemplateOps.ForceBuildTemplate(appdomain, probesType, methodDef);
                int patchCnt = template != null && template.Patches != null ? template.Patches.Length : -1;
                Console.WriteLine($"[NeoStep22] {mname}: template built, patches={patchCnt}");
                if (template == null)
                {
                    res.Failures.Add($"{mname}: template build failed");
                    res.TotalCells++; res.Failed++;
                    continue;
                }
                foreach (var tkv in ts)
                {
                    res.TotalCells++;
                    string tname = tkv.Key;
                    IType T = tkv.Value;
                    ILMethod instance;
                    try { instance = methodDef.MakeGenericMethod(new IType[] { T }) as ILMethod; }
                    catch (Exception ex) { res.Failures.Add($"{mname}<{tname}>: MakeGenericMethod threw {ex.GetType().Name}"); res.Failed++; continue; }
                    if (instance == null) { res.Failures.Add($"{mname}<{tname}>: instance null"); res.Failed++; continue; }
                    OpCodeR[] perOcc, templ;
                    try { perOcc = GenericMethodTemplateOps.CompilePerOccurrenceNeoBody(appdomain, probesType, instance); }
                    catch (Exception ex) { res.Failures.Add($"{mname}<{tname}>: per-occ threw {ex.GetType().Name}: {ex.Message}"); res.Failed++; continue; }
                    try { templ = GenericMethodTemplateOps.CompileViaTemplateNeoBody(appdomain, probesType, instance); }
                    catch (Exception ex) { res.Failures.Add($"{mname}<{tname}>: CloneAndPatch threw {ex.GetType().Name}: {ex.Message}"); res.Failed++; continue; }
                    bool eq = GenericMethodTemplateOps.BodiesEqual(perOcc, templ);
                    if (eq) { res.Passed++; Console.WriteLine($"  [PASS] {mname}<{tname}>: len={perOcc.Length}"); }
                    else
                    {
                        res.Failed++;
                        string msg = DescribeDiff(perOcc, templ);
                        res.Failures.Add($"{mname}<{tname}>: BODY MISMATCH. per-occ len={perOcc?.Length ?? -1}, template len={templ?.Length ?? -1}. {msg}");
                        Console.WriteLine($"  [FAIL] {mname}<{tname}>: {msg}");
                    }
                }
            }
            return res;
        }

        static string DescribeDiff(OpCodeR[] a, OpCodeR[] b)
        {
            if (a == null || b == null) return $"null: perOcc={a != null} templ={b != null}";
            if (a.Length != b.Length) return $"length {a.Length} vs {b.Length}";
            for (int i = 0; i < a.Length; i++)
            {
                ref var x = ref a[i];
                ref var y = ref b[i];
                if (x.Code != y.Code) return $"idx {i}: Code {x.Code} vs {y.Code}";
                if (x.Register1 != y.Register1 || x.Register2 != y.Register2 || x.Register3 != y.Register3)
                    return $"idx {i}: Reg ({x.Register1},{x.Register2},{x.Register3}) vs ({y.Register1},{y.Register2},{y.Register3}) [Code={x.Code}]";
                if (x.Operand != y.Operand || x.Operand2 != y.Operand2 || x.Operand3 != y.Operand3 || x.Operand4 != y.Operand4)
                    return $"idx {i}: Operand ({x.Operand},{x.Operand2},{x.Operand3},{x.Operand4}) vs ({y.Operand},{y.Operand2},{y.Operand3},{y.Operand4}) [Code={x.Code}]";
            }
            return "equal (unexpected)";
        }
    }
#endif
}
