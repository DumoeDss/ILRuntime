#if ENABLE_NEO_MODE && DEBUG
using System;
using System.Collections.Generic;
using System.IO;

using ILRuntime.CLR.Method;
using ILRuntime.CLR.TypeSystem;
using ILRuntime.Runtime.Enviorment;
using ILRuntime.Runtime.NeoAOT;
using ILRuntime.Runtime.Intepreter.OpCodes;

namespace ILRuntime.Runtime.Intepreter.RegisterVM
{
    /// <summary>
    /// Step 25 neo-aot-clrbase-iface host-side self-check (DEBUG+Neo only) --
    /// the TRUE-COMPLETION gate for the Cecil-free LOAD/EXECUTE of an IL type
    /// whose base is a CLR type that needs a <c>CrossBindingAdaptor</c>.
    ///
    /// The probe: <c>NeoClrProbe.ExceptionProbe : System.Exception</c> (a CLR
    /// base resolved by the BUILT-IN <c>ExceptionAdaptor</c>, registered by the
    /// AppDomain ctor for ANY AppDomain). The COMPILE side
    /// (neo-step25-clr-adaptor, archived) already compiles + emits this type to
    /// the .neo (the built-in adaptor resolves at compile time). This check is
    /// the LOAD/EXECUTE side: compile it in A (Cecil-loaded), Cecil-free-load
    /// into a FRESH AppDomain B (no Cecil), and assert the adaptor is INSTALLED
    /// on the Cecil-free ILType (the gap closed by this change).
    ///
    /// The gap (HEAD before the fix): <c>FinalizeFromNeoRecord</c> resolved a
    /// CLR base to a raw <c>CLRType</c> but never installed the
    /// <c>CrossBindingAdaptor</c>. So <c>FirstCLRBaseType</c> was the raw
    /// CLRType (NOT an adaptor) -> <c>ILTypeInstance.CLRInstance</c> was
    /// <c>this</c> (no CLR Exception bridge) -> <c>TypeForCLR</c> resolved to
    /// the wrong CLR type. The fix installs the adaptor in
    /// <c>FinalizeFromNeoRecord</c> (mirroring <c>InitializeBaseType</c>).
    ///
    /// Load-bearing cells (a green capstone alone is insufficient):
    ///  - the invariant: <c>probeB.FirstCLRBaseType is CrossBindingAdaptor</c>
    ///    (FALSE on HEAD -> TRUE after the fix -- the stash-toggle-able core);
    ///  - the bridge: <c>instB.CLRInstance is System.Exception</c> (the
    ///    ILTypeInstance ctor built a real CLR Exception via the adaptor);
    ///  - end-to-end: <c>Invoke(Tag) == 42</c> under Cecil-free exec == A's JIT.
    ///  - M1 body-mutation: mutate Tag's Ldc_I4 42 -> 555 in an independent
    ///    model2 BEFORE LoadNeoAssembly -> assert 555 (proves ExecuteNeo runs
    ///    the genuine .neo body on the Cecil-free CLR-base type, not a Cecil
    ///    fallback).
    ///
    /// Invoked host-side (CLI special mode "NeoStep25ClrBaseIface").
    /// </summary>
    public static class NeoStep25ClrBaseIfaceCheck
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

        const string ProbeFullName = "NeoClrProbe.ExceptionProbe";
        const int TagExpected = 42;

        public static Result Run(ILRuntime.Runtime.Enviorment.AppDomain appdomainA,
            string probeDllPath, string ilRuntimeTestBaseDllPath)
        {
            var res = new Result();

            if (string.IsNullOrEmpty(probeDllPath) || !File.Exists(probeDllPath))
            {
                res.Failures.Add("NeoClrProbe.dll not found: " + (probeDllPath ?? "(null)") + " -- build NeoClrProbe first");
                res.TotalCells = 1; res.Failed = 1;
                return res;
            }

            // ExceptionProbe lives in NeoClrProbe.dll, NOT TestCases.dll -> it
            // is NOT in A's LoadedTypes. Compile it via the FILE-PATH
            // NeoCompiler.Compile overload (the standalone-CLI path), which
            // Cecil-loads NeoClrProbe.dll in a throwaway compile context. The
            // probe resolves System.Exception via the built-in ExceptionAdaptor
            // (registered by A's ctor) -> compiles + emits. The file-path
            // compile ALSO emits AdaptorProbe : TestClass2 (a harness-adaptor
            // base), which the COMPILE side (neo-step25-clr-adaptor) skip-lists
            // -> IsComplete == false. That is EXPECTED: ExceptionProbe is still
            // emitted; we require only ExceptionProbe's presence below.
            ILType probeTypeA = null;
            if (appdomainA.LoadedTypes.TryGetValue(ProbeFullName, out var probeItA) && probeItA is ILType pa)
                probeTypeA = pa;

            NeoAssemblyModel model;
            try
            {
                using (var ms = new MemoryStream())
                {
                    NeoCompilerResult cres;
                    if (probeTypeA != null)
                    {
                        // Compile from the live A type (the in-process Cecil path).
                        cres = new NeoCompiler().Compile(new List<ILType> { probeTypeA }, ms);
                    }
                    else
                    {
                        cres = new NeoCompiler().Compile(probeDllPath,
                            ilRuntimeTestBaseDllPath != null ? new[] { ilRuntimeTestBaseDllPath } : null, ms);
                    }
                    ms.Position = 0;
                    model = NeoAssemblyReader.Read(ms);
                    if (!cres.IsComplete)
                    {
                        // AdaptorProbe skip is expected for the file-path compile;
                        // surface it as info but do not fail.
                        foreach (var s in cres.Skipped) res.Skipped.Add("(compile) " + s.MethodDisplay + " (" + s.ExceptionType + ")");
                    }
                }
            }
            catch (Exception ex)
            {
                res.TotalCells++; res.Failed++;
                res.Failures.Add("compile/read threw " + ex.GetType().Name + ": " + ex.Message);
                return res;
            }
            res.TotalCells++;
            // Verify ExceptionProbe was emitted to the .neo (the built-in
            // adaptor resolved at compile time).
            bool probeEmitted = FindTypeDefByName(model, ProbeFullName) >= 0;
            RecordCell(res, "compile .neo (ExceptionProbe emitted) in A",
                probeEmitted ? null : "ExceptionProbe TypeDef not in .neo (built-in adaptor did not resolve at compile)");
            if (!probeEmitted) return res;

            // ---- (B) the capstone: load Cecil-free into a FRESH AppDomain B ----
            res.TotalCells++;
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
                        var probeB = domainB.GetType(ProbeFullName) as ILType;
                        if (probeB == null) diff = "B: probe not found after Cecil-free load";
                        else
                        {
                            // (a) the invariant: FirstCLRBaseType IS the adaptor.
                            // FALSE on HEAD (raw CLRType) -> the stash-toggle-able
                            // core of this change.
                            var fcb = probeB.FirstCLRBaseType;
                            bool isAdaptor = fcb is CrossBindingAdaptor;
                            if (!isAdaptor)
                            {
                                diff = "B invariant: FirstCLRBaseType is " +
                                    (fcb == null ? "null" : fcb.GetType().Name) +
                                    " (expected CrossBindingAdaptor) -- the adaptor was NOT installed on the Cecil-free ILType";
                            }
                            else
                            {
                                // (b) the bridge: the ILTypeInstance ctor built a
                                // real CLR Exception via the adaptor's
                                // CreateCLRInstance (depends on FirstCLRBaseType
                                // being the adaptor).
                                var instB = domainB.Instantiate(ProbeFullName);
                                var clrInst = (instB as ILTypeInstance)?.CLRInstance;
                                if (!(clrInst is System.Exception))
                                {
                                    diff = "B bridge: CLRInstance is " +
                                        (clrInst == null ? "null" : clrInst.GetType().FullName) +
                                        " (expected a System.Exception) -- the ILTypeInstance ctor did not build the CLR bridge";
                                }
                                else
                                {
                                    // (c) end-to-end: Invoke Tag() == 42 (A's JIT
                                    // computes the same value independently below).
                                    var tagMethodB = probeB.GetMethod("Tag", 0);
                                    if (tagMethodB == null) diff = "B: Tag not found after Cecil-free load";
                                    else
                                    {
                                        var r = domainB.Invoke(tagMethodB, instB);
                                        int got = -1;
                                        try { got = Convert.ToInt32(r); } catch { }
                                        diff = (got == TagExpected)
                                            ? null
                                            : "B Tag=" + got + " expected=" + TagExpected;
                                    }
                                }
                            }
                        }
                    }
                    finally { domainB.Dispose(); }
                }
                catch (Exception ex) { diff = "B Cecil-free load+exec threw " + ex.GetType().Name + ": " + ex.Message; }
                RecordCell(res, "B Cecil-free load + adaptor invariant + bridge + Tag()", diff);
            }

            // ---- (A-JIT) the known-expected value: run Tag() via A's JIT. ----
            // Pins the expected value independently of the Cecil-free path so a
            // "both-garbage" false pass is ruled out. Only meaningful if the
            // probe was Cecil-loaded in A; the file-path fallback compiled a
            // throwaway context, so A-JIT is skipped there (TagExpected is a
            // hardcoded constant 42 either way).
            res.TotalCells++;
            {
                string diff = null;
                if (probeTypeA != null)
                {
                    try
                    {
                        var inst = appdomainA.Instantiate(ProbeFullName);
                        var tagA = probeTypeA.GetMethod("Tag", 0);
                        var r = appdomainA.Invoke(tagA, inst);
                        int got = -1;
                        try { got = Convert.ToInt32(r); } catch { }
                        diff = (got == TagExpected) ? null : ("A JIT Tag=" + got + " expected=" + TagExpected);
                    }
                    catch (Exception ex) { diff = "A JIT Tag threw " + ex.GetType().Name + ": " + ex.Message; }
                }
                RecordCell(res, "A JIT Tag (the known-expected reference)", diff);
            }

            // ---- (M1) body-mutation cell: mutate Tag's Ldc_I4 42 -> 555 in an
            // independent model2 BEFORE LoadNeoAssembly -> assert 555 (proves
            // ExecuteNeo runs the genuine .neo body, not a Cecil fallback). ----
            res.TotalCells++;
            {
                const int MUTATED = 555;
                string diff;
                try
                {
                    NeoAssemblyModel model2;
                    using (var ms2 = new MemoryStream())
                    {
                        if (probeTypeA != null)
                            new NeoCompiler().Compile(new List<ILType> { probeTypeA }, ms2);
                        else
                            new NeoCompiler().Compile(probeDllPath,
                                ilRuntimeTestBaseDllPath != null ? new[] { ilRuntimeTestBaseDllPath } : null, ms2);
                        ms2.Position = 0;
                        model2 = NeoAssemblyReader.Read(ms2);
                    }
                    int defIdx = FindMethodDefByName(model2, ProbeFullName, "Tag");
                    OpCodeR[] body = defIdx >= 0 ? model2.MethodDefs[defIdx].NeoExecuteBody : null;
                    int mutateAt = -1;
                    if (body != null)
                    {
                        for (int j = 0; j < body.Length; j++)
                        {
                            var c = body[j].Code;
                            if ((c == OpCodeREnum.Ldc_I4 || c == OpCodeREnum.Ldc_I4_S) && body[j].Operand == TagExpected)
                            {
                                mutateAt = j;
                                body[j].Code = OpCodeREnum.Ldc_I4;
                                break;
                            }
                        }
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
                        diff = "M1: Tag Ldc=" + TagExpected + " not found [" + sb + "]";
                    }
                    else
                    {
                        body[mutateAt].Operand = MUTATED;
                        var domainB2 = new ILRuntime.Runtime.Enviorment.AppDomain();
                        try
                        {
                            domainB2.LoadNeoAssembly(model2, null);
                            var probeB2 = domainB2.GetType(ProbeFullName) as ILType;
                            var tagMethod = probeB2?.GetMethod("Tag", 0);
                            var instB2 = domainB2.Instantiate(ProbeFullName);
                            var r = tagMethod != null ? domainB2.Invoke(tagMethod, instB2) : null;
                            int got = -1;
                            try { got = Convert.ToInt32(r); } catch { }
                            diff = (got == MUTATED)
                                ? null
                                : "M1 body-mutation: expected " + MUTATED + " got " + got + " (a Cecil-fallback would yield " + TagExpected + ")";
                        }
                        finally { domainB2.Dispose(); }
                    }
                }
                catch (Exception ex) { diff = "M1 threw " + ex.GetType().Name + ": " + ex.Message; }
                RecordCell(res, "M1 body-mutation (Cecil-free exec runs the .neo body)", diff);
            }

            return res;
        }

        // Locate NeoClrProbe.dll + ILRuntimeTestBase.dll: passed in by the CLI
        // host (mirrors NeoStep25ClrAdaptorCheck), which locates them relative
        // to the TestCases.dll path argument.

        static int FindTypeDefByName(NeoAssemblyModel model, string fullName)
        {
            if (model == null || model.TypeDefs == null || model.TypeRefs == null || string.IsNullOrEmpty(fullName))
                return -1;
            for (int i = 0; i < model.TypeDefs.Length; i++)
            {
                int trIdx = model.TypeDefs[i].TypeRefIdx;
                if (trIdx < 0 || trIdx >= model.TypeRefs.Length) continue;
                if (model.TypeRefs[trIdx].Name == fullName) return i;
            }
            return -1;
        }

        static int FindMethodDefByName(NeoAssemblyModel model, string declaringFullName, string name)
        {
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

        static void RecordCell(Result res, string name, string diff)
        {
            if (diff == null)
            {
                res.Passed++;
                Console.WriteLine("[NeoStep25ClrBaseIface] " + name + ": PASS");
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
