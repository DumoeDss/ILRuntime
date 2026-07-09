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
    /// Step 25 neo-aot-multi-hotfix capstone (host-side, DEBUG+Neo only): the
    /// Cecil-free load of a MULTI-hotfix-assembly setup where an IL type in
    /// "AssemblyA" references a TYPE in "AssemblyB" (both IL hotfix assemblies),
    /// compiled to TWO separate .neo models and Cecil-free-loaded into a FRESH
    /// AppDomain. Exercises cross-assembly IL-to-IL refs: a field of type B, a
    /// method param of type B, a call to a B method, isinst/castclass on B.
    ///
    /// The S3-2 Cecil-free load shipped a SINGLE assembly's self-contained types.
    /// The gap (lead-6 MEDIUM#6): cross-assembly IL-to-IL refs. When .neo-A (the
    /// referencing type) is loaded FIRST, its field-type / param-type resolution
    /// for the B type fails (B is not yet in mapType) -> fieldTypes stays NULL.
    /// This check probes BOTH load orders (A-first, B-first) to surface whether a
    /// deferred/lazy re-resolution is needed.
    ///
    /// Compiles NeoStep25MultiHotfixA + NeoStep25MultiHotfixB in the SESSION
    /// AppDomain A (Cecil-loaded), PARTITIONED into two .neo models (one per
    /// "assembly"), then loads BOTH Cecil-free into a FRESH AppDomain B in each
    /// order + invokes ACompute() -> asserts the result EQUALS the JIT reference.
    ///
    /// Adversarial mutation cell (M1): mutate a Ldc_I4 constant in A's ACompute
    /// body in an INDEPENDENT model2 BEFORE the Cecil-free load -> assert the
    /// MUTATED-derived value (a Cecil-fallback yields the unmutated value).
    ///
    /// Invoked host-side (CLI special mode "NeoStep25CecilFreeMultiHotfix").
    /// </summary>
    public static class NeoStep25CecilFreeMultiHotfixCheck
    {
        public class Result
        {
            public int TotalCells;
            public int Passed;
            public int Failed;
            public List<string> Failures = new List<string>();
        }

        const string AFullName = "TestCases.NeoStep25MultiHotfixA";
        const string BFullName = "TestCases.NeoStep25MultiHotfixB";
        // seed=5 + b=BValue=10 + BMagic()=1000:
        // 5 + BEcho(5)=5+10+1000=1015 -> 1020; +1 (isinst) -> 1021; +10 (cast.BValue) -> 1031
        const int Expected = 1031;

        public static Result Run(ILRuntime.Runtime.Enviorment.AppDomain appdomainA)
        {
            var res = new Result();

            // ---- locate both types in the SESSION AppDomain A (Cecil-loaded) ----
            if (!appdomainA.LoadedTypes.TryGetValue(AFullName, out var aIt) || !(aIt is ILType typeA))
            { res.Failures.Add(AFullName + " not loaded in A / not an ILType"); res.TotalCells = 1; res.Failed = 1; return res; }
            if (!appdomainA.LoadedTypes.TryGetValue(BFullName, out var bIt) || !(bIt is ILType typeB))
            { res.Failures.Add(BFullName + " not loaded in A / not an ILType"); res.TotalCells = 1; res.Failed = 1; return res; }

            // ---- (A) the known-expected value: run ACompute() via A's JIT ----
            res.TotalCells++;
            int expected;
            {
                object jitRes = null;
                string diff = null;
                try
                {
                    var instA = appdomainA.Instantiate(AFullName);
                    // Setup with a B(b=10)
                    var bCtor = typeB.GetConstructor(1);
                    var bInst = appdomainA.Instantiate(BFullName, new object[] { 10 });
                    var setupMethodA = typeA.GetMethod("Setup", 1);
                    appdomainA.Invoke(setupMethodA, instA, new object[] { bInst });
                    var computeMethodA = typeA.GetMethod("ACompute", 1);
                    jitRes = appdomainA.Invoke(computeMethodA, instA, new object[] { 5 });
                }
                catch (Exception ex) { diff = "A JIT ACompute threw " + ex.GetType().Name + ": " + ex.Message; }
                if (diff == null)
                {
                    try { expected = Convert.ToInt32(jitRes); }
                    catch { diff = "A JIT ACompute returned non-int: " + (jitRes?.GetType().Name ?? "null"); expected = -1; }
                    if (diff == null && expected != Expected)
                        diff = "A JIT ACompute=" + expected + " but hardcoded expected=" + Expected + " (probe arithmetic changed -> update Expected)";
                }
                else expected = -1;
                RecordCell(res, "A JIT ACompute (the known-expected reference)", diff);
            }

            // ---- compile the two .neo models (PARTITIONED: A alone, B alone) ----
            NeoAssemblyModel modelA, modelB;
            res.TotalCells++;
            {
                string diff = null;
                try
                {
                    using (var msA = new MemoryStream())
                    {
                        var crA = new NeoCompiler().Compile(new List<ILType> { typeA }, msA);
                        if (!crA.IsComplete) diff = "compile A skipped " + crA.Skipped.Count;
                        msA.Position = 0; modelA = NeoAssemblyReader.Read(msA);
                    }
                    using (var msB = new MemoryStream())
                    {
                        var crB = new NeoCompiler().Compile(new List<ILType> { typeB }, msB);
                        if (!crB.IsComplete) diff = (diff ?? "") + " | compile B skipped " + crB.Skipped.Count;
                        msB.Position = 0; modelB = NeoAssemblyReader.Read(msB);
                    }
                }
                catch (Exception ex) { diff = "compile/read threw " + ex.GetType().Name + ": " + ex.Message; modelA = null; modelB = null; }
                RecordCell(res, "compile .neo-A + .neo-B (partitioned, V2) in A", diff);
            }

            // ---- (B-first) load order: B then A ----
            res.TotalCells++;
            {
                string diff = RunOneCecilFreeLoad(modelB, modelA, false, expected, out _);
                RecordCell(res, "Cecil-free load order [B then A] + ACompute", diff);
            }

            // ---- (A-first) load order: A then B (the gap-exposing order) ----
            res.TotalCells++;
            {
                string diff = RunOneCecilFreeLoad(modelA, modelB, true, expected, out _);
                RecordCell(res, "Cecil-free load order [A then B] + ACompute (the gap-exposing order)", diff);
            }

            // ---- (M1) body-mutation cell: mutate the 1000 constant (BMagic's value)
            // in the .neo body BEFORE the Cecil-free load -> assert the MUTATED-
            // derived value. The optimizer may INLINE BMagic into BEcho into
            // ACompute, so the 1000 constant can live in ACompute's body (model A),
            // BEcho's body (model B), OR BMagic's body (model B). Search all three +
            // mutate the FIRST hit (the value contributes ONCE to the result either
            // way), then assert the result shifts by exactly (MUTATED - 1000). A
            // Cecil-fallback yields the unmutated value -> FAIL. ----
            res.TotalCells++;
            {
                string diff;
                try
                {
                    NeoAssemblyModel modelA2, modelB2;
                    using (var msA2 = new MemoryStream())
                    {
                        new NeoCompiler().Compile(new List<ILType> { typeA }, msA2);
                        msA2.Position = 0; modelA2 = NeoAssemblyReader.Read(msA2);
                    }
                    using (var msB2 = new MemoryStream())
                    {
                        new NeoCompiler().Compile(new List<ILType> { typeB }, msB2);
                        msB2.Position = 0; modelB2 = NeoAssemblyReader.Read(msB2);
                    }
                    // search ACompute (model A), then BEcho/BMagic (model B) for the
                    // 1000 constant. The optimizer inlines so the inlined copy is the
                    // load-bearing one at exec time.
                    OpCodeR[] body = null;
                    string mutatedModel = null;
                    int mutateAt = -1;
                    int aComputeIdx = FindMethodDefByName(modelA2, AFullName, "ACompute");
                    if (aComputeIdx >= 0)
                    {
                        body = modelA2.MethodDefs[aComputeIdx].NeoExecuteBody;
                        mutateAt = FindLdcConstant(body, 1000);
                        if (mutateAt >= 0) mutatedModel = "ACompute";
                    }
                    if (mutateAt < 0)
                    {
                        int bEchoIdx = FindMethodDefByName(modelB2, BFullName, "BEcho");
                        if (bEchoIdx >= 0)
                        {
                            body = modelB2.MethodDefs[bEchoIdx].NeoExecuteBody;
                            mutateAt = FindLdcConstant(body, 1000);
                            if (mutateAt >= 0) mutatedModel = "BEcho";
                        }
                    }
                    if (mutateAt < 0)
                    {
                        int bMagicIdx = FindMethodDefByName(modelB2, BFullName, "BMagic");
                        if (bMagicIdx >= 0)
                        {
                            body = modelB2.MethodDefs[bMagicIdx].NeoExecuteBody;
                            mutateAt = FindLdcConstant(body, 1000);
                            if (mutateAt >= 0) mutatedModel = "BMagic";
                        }
                    }
                    if (mutateAt < 0 || body == null) diff = "M1: ldc 1000 not found in ACompute/BEcho/BMagic";
                    else
                    {
                        const int MUTATED = 2000;
                        body[mutateAt].Code = OpCodeREnum.Ldc_I4;
                        body[mutateAt].Operand = MUTATED;
                        // 1000 contributes ONCE to ACompute (BMagic called once via
                        // BEcho, whether inlined or not) -> shift = (MUTATED - 1000).
                        int mutatedExpected = Expected + (MUTATED - 1000);
                        diff = RunOneCecilFreeLoad(modelA2, modelB2, true, mutatedExpected, out _);
                        if (diff == null)
                        {
                            RecordCell(res, "M1 body-mutation (Cecil-free exec runs the .neo body, mutated in " + mutatedModel + ")", null);
                            goto M1Done;
                        }
                        else
                        { res.Failed++; res.Failures.Add("M1 body-mutation: " + diff); goto M1Done; }
                    }
                }
                catch (Exception ex) { diff = "M1 threw " + ex.GetType().Name + ": " + ex.Message; }
                res.Failed++; res.Failures.Add("M1 body-mutation: " + diff);
                M1Done:;
            }

            return res;
        }

        // Load `modelFirst` then `modelSecond` Cecil-free into a FRESH AppDomain,
        // then invoke ACompute(seed=5) with a Setup(B(10)). `aFirst` flags whether
        // the referencing type A was loaded first (the gap-exposing order). Returns
        // null on success (result == expected), else a diff string.
        static string RunOneCecilFreeLoad(NeoAssemblyModel modelFirst, NeoAssemblyModel modelSecond,
            bool aFirst, int expected, out string loadDetail)
        {
            loadDetail = null;
            try
            {
                var domainB = new ILRuntime.Runtime.Enviorment.AppDomain();
                try
                {
                    var r1 = domainB.LoadNeoAssembly(modelFirst, null);
                    var r2 = domainB.LoadNeoAssembly(modelSecond, null);
                    loadDetail = "first-attach=" + r1.Attached.Count + "/skip=" + r1.Skipped.Count
                        + " second-attach=" + r2.Attached.Count + "/skip=" + r2.Skipped.Count;
                    var typeAb = domainB.GetType(AFullName) as ILType;
                    var typeBb = domainB.GetType(BFullName) as ILType;
                    if (typeAb == null || typeBb == null)
                        return "B: A/B type not found after Cecil-free load (" + (typeAb == null ? "A " : "") + (typeBb == null ? "B" : "") + ")";
                    // field type resolution check: A.BField's type should resolve to B
                    int bfIdx;
                    var ft = typeAb.GetField("BField", out bfIdx);
                    if (ft == null) return "B: A.BField field type NULL (cross-assembly field-type unresolved, aFirst=" + aFirst + ") [" + loadDetail + "]";
                    var bctor = typeBb.GetConstructor(1);
                    if (bctor == null) return "B: B(int) ctor not found after Cecil-free load";
                    var bInst = domainB.Instantiate(BFullName, new object[] { 10 });
                    var instAb = domainB.Instantiate(AFullName);
                    var setupM = typeAb.GetMethod("Setup", 1);
                    if (setupM == null) return "B: A.Setup not found after Cecil-free load";
                    domainB.Invoke(setupM, instAb, new object[] { bInst });
                    var computeM = typeAb.GetMethod("ACompute", 1);
                    if (computeM == null) return "B: A.ACompute not found after Cecil-free load";
                    var rv = domainB.Invoke(computeM, instAb, new object[] { 5 });
                    int got = -1;
                    try { got = Convert.ToInt32(rv); } catch { }
                    return (got == expected) ? null : "B ACompute=" + got + " expected=" + expected + " (aFirst=" + aFirst + ") [" + loadDetail + "]";
                }
                finally { domainB.Dispose(); }
            }
            catch (Exception ex) { return "Cecil-free load+exec threw " + ex.GetType().Name + ": " + ex.Message; }
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

        // Find the FIRST Ldc_I4 / Ldc_I4_S instruction whose Operand == value.
        // Returns the index, or -1 if not found. (The optimizer may inline a
        // constant into a caller's body, so the search is across candidate bodies.)
        static int FindLdcConstant(OpCodeR[] body, int value)
        {
            if (body == null) return -1;
            for (int j = 0; j < body.Length; j++)
            {
                var c = body[j].Code;
                if ((c == OpCodeREnum.Ldc_I4 || c == OpCodeREnum.Ldc_I4_S) && body[j].Operand == value) return j;
            }
            return -1;
        }

        static void RecordCell(Result res, string name, string diff)
        {
            if (diff == null)
            {
                res.Passed++;
                Console.WriteLine("[NeoStep25MultiHotfix] " + name + ": PASS");
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
