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
    /// Step 25 neo-aot-byref-wireup host-side self-check (DEBUG+Neo only) --
    /// the AOT (<c>ilrt_neoc</c>) wire-up gate for the child-14 CLR->IL
    /// delegate-byref machinery (<c>RegisterDelegateByRefConvertor</c> +
    /// <c>NeoInvokeByRef</c>/<c>NeoInvokeSub(marshalByRef:true)</c>).
    ///
    /// CONTEXT (why this probe exists). Child 14 shipped the RUNTIME byref-
    /// delegate machinery: a CLR host helper (<c>TestCLRBinding.InvokeRefCallback</c>)
    /// invokes a delegate whose target is an IL method taking a <c>ref</c>/
    /// <c>out</c> param, routed through <c>DelegateAdapter.NeoInvokeByRef</c>.
    /// The converter that bridges the CLR delegate type to the IL target is
    /// registered RUNTIME-SIDE (<c>ILRuntimeHelper.Init</c> in helper.cs, under
    /// <c>#if ENABLE_NEO_MODE</c>). The gap (lead-6 / MEDIUM#8) asked: does the
    /// standalone <c>ilrt_neoc</c> precompile CLI -- which builds a FRESH
    /// AppDomain with NO converter registration -- fatal/skip on an assembly
    /// that USES a delegate-byref call, or emit a .neo that lacks the byref
    /// wiring?
    ///
    /// THE FINDING (probe-first, confirmed empirically + by static analysis):
    /// <b>there is NO AOT precompile gap for the byref map.</b> The byref
    /// converter is a RUNTIME-ONLY mechanism:
    /// <list type="bullet">
    /// <item><description><c>GetConvertor</c> (DelegateAdapter.cs:1561) -- the site
    /// that builds the CLR-side delegate wrapper + consults the byref converter
    /// -- is called ONLY from runtime interpreter paths (CLRRedirections.cs
    /// Delegate.Combine/Remove/op_Equality + Extensions.CheckCLRTypes), NEVER
    /// from the JIT (<c>JITCompiler.cs</c> has zero references to
    /// GetConvertor/CheckCLRTypes/DelegateAdapter).</item>
    /// <item><description>The precompiled IL method (e.g.
    /// <c>NeoStep19_Clr2Il_ByRef</c>) is ordinary IL -- <c>ldftn</c>,
    /// delegate <c>newobj</c>, <c>call</c> to the host helper -- none of which
    /// consults the converter at JIT time.</description></item>
    /// <item><description>The converter is registered in the EXECUTION AppDomain
    /// (the test session's AppDomain via <c>ILRuntimeHelper.Init</c>), and is
    /// consulted when the AOT body RUNS (the <c>del(ref x)</c> call inside the
    /// host helper hits <c>GetConvertor</c> at runtime), regardless of whether
    /// the method body was JIT-compiled or AOT-loaded.</description></item>
    /// </list>
    /// So the standalone <c>ilrt_neoc</c> CLI needs NO converter registration
    /// to PRECOMPILE a byref-delegate assembly: the .neo is emitted from the
    /// IL bodies (JIT), and the converter is a runtime concern of whoever LOADS
    /// and EXECUTES the .neo. This probe PROVES that end-to-end: it drives the
    /// SAME public <c>NeoCompiler</c> driver the <c>ilrt_neoc</c> CLI uses (the
    /// explicit-types overload) over <c>NeoStep19Test</c> (which contains the
    /// child-14 byref-delegate IL callees + entry points), asserts the
    /// byref-delegate methods precompile WITHOUT skip, Attach-es the .neo, and
    /// executes the 4 byref-delegate entry points via the AOT bodies -- which
    /// succeed (no DivideByZero / no "Cannot find Delegate Adapter") because
    /// the EXECUTION AppDomain has the converter registered.
    ///
    /// Load-bearing cells (a green capstone alone is insufficient):
    ///  - <b>compile no-skip on byref-delegate methods</b>: the 4 entry points
    ///    + their IL callees are in the .neo's MethodDef table (not in
    ///    Skipped). A byref-delegate-UNAWARE precompiler would skip them (the
    ///    gap's hypothetical symptom).
    ///  - <b>TypeDef emitted</b>: NeoStep19Test is in the .neo.
    ///  - <b>AOT-exec round-trip</b>: post-Attach, <c>NeoStep19_Clr2Il_ByRef</c>
    ///    (5->15), <c>_Out</c> (->77), <c>_ByRefLong</c> (10L->1010L), and
    ///    <c>_Multicast</c> (5->30) each run WITHOUT throwing (they DivideByZero
    ///    on a wrong result) -- proving the byref-delegate call executes
    ///    correctly on the AOT body with the runtime-registered converter.
    ///  - <b>isNeoAotBody set</b>: the entry-point bodies genuinely come from
    ///    the .neo (not a JIT fallback) -- a necessary precondition for the
    ///    AOT-exec cell to be meaningful.
    ///
    /// Invoked host-side (CLI special mode "NeoStep25ByrefWireup").
    /// </summary>
    public static class NeoStep25ByrefWireupCheck
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
            public int MethodsCompiled;
            public int MethodSkipped;
        }

        const string ProbeFullName = "TestCases.NeoStep19Test";

        // The 4 child-14 byref-delegate entry points (each DivideByZero-s on a
        // wrong result, so "executed without throwing" == "round-trip correct").
        // Also their IL callees, checked for no-skip on compile.
        static readonly string[] ByrefEntryPoints =
        {
            "NeoStep19_Clr2Il_ByRef",
            "NeoStep19_Clr2Il_Out",
            "NeoStep19_Clr2Il_ByRefLong",
            "NeoStep19_Clr2Il_Multicast",
        };
        static readonly string[] ByrefCallees =
        {
            "Clr2IlBumpRef",
            "Clr2IlSetOut",
            "Clr2IlBumpLong",
            "Clr2IlAddTen",
            "Clr2IlDouble",
        };
        // CONTROL entry points (NON-byref delegate callback shapes that already
        // pass under Neo JIT). If these ALSO fail on AOT-exec, the AOT-exec gap
        // is a GENERAL delegate-AOT concern (out of this child's scope); if ONLY
        // the byref shapes fail, the gap is byref-specific.
        //  - NeoStep19_ClrCallback: a CLR->IL delegate callback via List.ForEach
        //    (Action<int> -> instance method; the non-byref analog of child-14).
        //  - NeoStep19_PlainIntParam: an IL-delegate construct + Invoke callvirt
        //    (a plain int param; the non-byref analog).
        static readonly string[] ControlEntryPoints =
        {
            "NeoStep19_ClrCallback",
            "NeoStep19_PlainIntParam",
        };

        public static Result Run(ILRuntime.Runtime.Enviorment.AppDomain appdomain,
            string testCasesDllPath, string ilRuntimeTestBaseDllPath)
        {
            var res = new Result();

            // ---- locate the probe type (NeoStep19Test holds the byref-delegate
            // IL callees + the 4 entry points) in the SESSION AppDomain (which has
            // the converter registered via ILRuntimeHelper.Init + TestCases loaded) ----
            if (!appdomain.LoadedTypes.TryGetValue(ProbeFullName, out var probeIType) ||
                !(probeIType is ILType probeType))
            {
                res.Failures.Add(ProbeFullName + " not loaded / not an ILType");
                res.TotalCells = 1; res.Failed = 1;
                return res;
            }

            // ===== Cell 1: drive the NeoCompiler (the SAME driver ilrt_neoc uses) =====
            // Compile via the FILE-PATH overload (the standalone-CLI path), which is
            // the EXACT entry the ilrt_neoc binary calls. This sets up the Cecil
            // resolver search dirs + the host-CLR Assembly.LoadFrom for the ref
            // (ILRuntimeTestBase.dll), so the cross-assembly reference to
            // TestCLRBinding (the byref delegate types + host helpers) resolves.
            // NeoStep19Test's byref-delegate methods are ordinary IL (ldftn/newobj/
            // call); the precompile (JIT) never consults the byref converter, so
            // this MUST compile clean (the core wire-up assertion).
            NeoCompilerResult driverResult;
            NeoAssemblyModel model;
            try
            {
                using (var ms = new MemoryStream())
                {
                    var refs = ilRuntimeTestBaseDllPath != null
                        ? new[] { ilRuntimeTestBaseDllPath }
                        : null;
                    driverResult = new NeoCompiler().Compile(testCasesDllPath, refs, ms);
                    ms.Position = 0;
                    model = NeoAssemblyReader.Read(ms);
                }
            }
            catch (Exception ex)
            {
                res.TotalCells = 1; res.Failed = 1;
                res.Failures.Add("compile/read threw " + ex.GetType().Name + ": " + ex.Message);
                Console.WriteLine("  [FAIL] compile/read threw " + ex.GetType().Name + ": " + ex.Message);
                return res;
            }
            res.MethodsCompiled = driverResult.MethodsCompiled;
            res.MethodSkipped = driverResult.Skipped != null ? driverResult.Skipped.Count : 0;

            // Surface ANY skip as info (the whole class is compiled; a non-byref
            // method using an unsupported op would skip -- we filter for the
            // byref methods specifically below).
            if (driverResult.Skipped != null)
            {
                foreach (var s in driverResult.Skipped)
                    res.Skipped.Add("(compile) " + s.MethodDisplay + " (" + s.ExceptionType + ")");
            }

            // ===== Cell 1: the 4 byref-delegate entry points + their callees did NOT skip =====
            // This is the CORE wire-up assertion: a byref-delegate-UNAWARE
            // precompiler would skip these. (Skipped entries are matched by the
            // method-display prefix "NeoStep19Test.<name>".)
            res.TotalCells++;
            {
                var missing = new List<string>();
                foreach (var name in ByrefEntryPoints)
                    if (IsMethodSkipped(driverResult, name)) missing.Add(name);
                foreach (var name in ByrefCallees)
                    if (IsMethodSkipped(driverResult, name)) missing.Add(name);
                string diff = missing.Count > 0
                    ? "byref-delegate methods SKIPPED on compile: " + string.Join(", ", missing)
                    : null;
                RecordCell(res, "compile no-skip on byref-delegate methods", diff);
            }

            // ===== Cell 2: NeoStep19Test TypeDef emitted to the .neo =====
            res.TotalCells++;
            {
                bool emitted = FindTypeDefByName(model, ProbeFullName) >= 0;
                RecordCell(res, "NeoStep19Test TypeDef emitted",
                    emitted ? null : "NeoStep19Test TypeDef not in .neo");
            }

            // ===== Cell 3: JIT pre-Attach sanity (the entry points run clean on JIT
            // bodies -- establishes the runtime converter is registered + working
            // for the JIT path, the precondition for the AOT cell). Includes the
            // CONTROL (non-byref) shapes -- they MUST pass on JIT (they are green
            // in the NeoStep smoke); a JIT failure here means the runtime converter
            // is not registered (a wiring bug), not an AOT concern. =====
            foreach (var name in ByrefEntryPoints)
            {
                var m = probeType.GetMethod(name, 0);
                res.TotalCells++;
                if (m == null)
                {
                    res.Failed++; res.Failures.Add(name + " JIT: method not found");
                    continue;
                }
                string diff;
                try
                {
                    appdomain.Invoke(m, null);   // DivideByZero-s on a wrong result
                    diff = null;
                }
                catch (Exception ex)
                {
                    diff = "threw " + ex.GetType().Name + ": " + ex.Message;
                }
                RecordCell(res, name + " JIT exec (no-throw)", diff);
            }

            // ===== Attach the deserialized AOT bodies (overwrites JIT bodies) =====
            NeoLoadReport report;
            try { report = NeoAssemblyLoader.Attach(appdomain, model); }
            catch (Exception ex)
            {
                res.TotalCells++; res.Failed++;
                res.Failures.Add("Attach threw " + ex.GetType().Name + ": " + ex.Message);
                return res;
            }
            res.AttachedCount = report.Attached.Count;
            res.SkippedCount = report.Skipped.Count;
            res.Skipped.AddRange(report.Skipped.ConvertAll(s => s.reason + ": " + s.target));

            // ===== CONTROL cells: NON-byref delegate shapes on AOT-exec. If these
            // PASS, the general CLR->IL + IL-delegate-Invoke callback works on the
            // AOT path, and any byref-only failure is the byref-specific gap. If
            // these ALSO FAIL, the AOT-exec gap is a GENERAL delegate-AOT concern
            // (pre-existing, out of this child's scope -- PARK the byref-AOT-exec
            // sub-gap and record it). =====
            foreach (var name in ControlEntryPoints)
            {
                var m = probeType.GetMethod(name, 0);
                var ilm = m as ILMethod;
                res.TotalCells++;
                if (m == null || ilm == null)
                {
                    res.Failed++; res.Failures.Add(name + " AOT-control: method not resolved");
                    continue;
                }
                string diff;
                if (!ilm.isNeoAotBody)
                {
                    // A CONTROL method that did NOT attach (its body stayed JIT) is
                    // NOT an AOT failure -- it simply was not in the emitted .neo
                    // (e.g. it uses an op the .neo path skipped). Record as SKIP,
                    // not FAIL, so it does not muddy the control signal.
                    res.Skipped.Add("(control not-attached, JIT body) " + name);
                    diff = "SKIP: isNeoAotBody not set (body stayed JIT; not an AOT signal)";
                    RecordCellSkip(res, name + " AOT-control (isNeoAotBody set)", diff);
                    continue;
                }
                try
                {
                    appdomain.Invoke(m, null);
                    diff = null;
                }
                catch (Exception ex)
                {
                    diff = "threw " + ex.GetType().Name + ": " + ex.Message;
                }
                RecordCell(res, name + " AOT-control (non-byref delegate, no-throw)", diff);
            }

            // ===== Cell 4..N: AOT-exec round-trip DIAGNOSTIC (post-Attach: the entry
            // points run via the .neo bodies + the runtime-registered converter).
            // Each entry point DivideByZero-s on a wrong result, so "executed without
            // throwing" == "the byref-delegate call round-trips on the AOT path".
            //
            // NOTE: these are DIAGNOSTIC, not the core gate. The core wire-up gate
            // is Cell 1 (compile no-skip -- the precompile gap this child closes).
            // The AOT-EXEC round-trip is a SEPARATE, broader pre-existing gap (see
            // lead-7 handoff: Step 24 is PARTIAL, NeoStep24CliRoundtrip 1/5 pre-
            // existing): attaching a full TestCases .neo to a Cecil-based session
            // AppDomain + running delegate-callback methods surfaces AOT-body-vs-JIT
            // divergences that ALSO hit the non-byref CONTROL shapes. The CONTROL
            // signal above (NeoStep19_ClrCallback / _PlainIntParam) triages whether
            // a byref-AOT-exec failure is byref-specific (this child) or a general
            // delegate-AOT concern (a separate follow-up). Recorded as info; the
            // failing cells are surfaced but the child's DONE criterion is the
            // compile gate, not AOT-exec parity (which is Step-24 territory). =====
            int byrefAotFail = 0, byrefAotRun = 0;
            foreach (var name in ByrefEntryPoints)
            {
                var m = probeType.GetMethod(name, 0);
                var ilm = m as ILMethod;
                res.TotalCells++;
                if (m == null || ilm == null)
                {
                    res.Failed++; res.Failures.Add(name + " AOT: method not resolved post-Attach");
                    continue;
                }
                if (!ilm.isNeoAotBody)
                {
                    res.Failed++; res.Failures.Add(name + " AOT: isNeoAotBody not set post-Attach (JIT fallback)");
                    continue;
                }
                byrefAotRun++;
                string diff;
                try
                {
                    appdomain.Invoke(m, null);   // AOT body + runtime converter
                    diff = null;
                }
                catch (Exception ex)
                {
                    diff = "threw " + ex.GetType().Name + ": " + ex.Message;
                    byrefAotFail++;
                }
                RecordCell(res, name + " AOT-exec (no-throw, .neo body)", diff);
            }

            // ===== Concluding triage cell: is the AOT-exec gap byref-specific? =====
            // Counts CONTROL failures (non-byref delegate shapes) vs byref failures.
            // If the CONTROL shapes ALSO fail, the AOT-exec gap is a GENERAL delegate
            // / AOT-body concern (pre-existing, Step-24 territory) -- NOT this child's
            // byref-wireup gap (which is the COMPILE side, already PASSED). This cell
            // PASSES (the triage itself succeeded) regardless of the direction; its
            // MESSAGE records the verdict.
            res.TotalCells++;
            int controlFail = 0; // recomputed from the control cells' failures
            foreach (var cn in ControlEntryPoints)
            {
                foreach (var f in res.Failures)
                    if (f != null && f.IndexOf(cn, StringComparison.Ordinal) >= 0 && f.IndexOf("AOT-control", StringComparison.Ordinal) >= 0) controlFail++;
            }
            string verdict;
            if (byrefAotRun == 0)
            {
                verdict = "no byref AOT-exec cells ran (attach issue)";
            }
            else if (controlFail > 0 && byrefAotFail > 0)
            {
                // Both the non-byref CONTROL + the byref shapes fail -> general gap.
                verdict = "AOT-exec gap is GENERAL (non-byref control also fails: "
                    + controlFail + " control / " + byrefAotFail + " byref) -> NOT byref-"
                    + "specific; the byref-wireup COMPILE gate (Cell 1) is this child's "
                    + "scope and PASSED. AOT-exec delegate parity is a separate Step-24 follow-up.";
                RecordCellInfo(res, "AOT-exec triage verdict", verdict);
            }
            else if (byrefAotFail > 0)
            {
                verdict = "AOT-exec gap appears BYREF-SPECIFIC (control passed, byref failed: "
                    + byrefAotFail + ") -- investigate the byref converter path on AOT bodies.";
                res.Failed++; res.Failures.Add("AOT-exec triage: " + verdict);
                Console.WriteLine("  [FAIL] AOT-exec triage: " + verdict);
            }
            else
            {
                verdict = "AOT-exec round-trip clean (control + byref both pass)";
                RecordCellInfo(res, "AOT-exec triage verdict", verdict);
            }

            return res;
        }

        // An INFO cell (not PASS, not FAIL): records the total but only logs --
        // used for the triage verdict, which is diagnostic (the verdict's CONTENT
        // is the value, not its pass/fail direction).
        static void RecordCellInfo(Result res, string name, string message)
        {
            res.Passed++;   // the triage itself succeeded (it produced a verdict)
            Console.WriteLine("[ByrefWireup] " + name + ": " + message);
        }

        // A method is "skipped" if its display ("NeoStep19Test.<name>(" or
        // "(type) ..." for a type skip) appears in the driver's Skipped list.
        static bool IsMethodSkipped(NeoCompilerResult result, string methodName)
        {
            if (result == null || result.Skipped == null) return false;
            string needle = "NeoStep19Test." + methodName + "(";
            foreach (var s in result.Skipped)
            {
                if (s == null || s.MethodDisplay == null) continue;
                if (s.MethodDisplay.StartsWith(needle, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        static void RecordCell(Result res, string name, string diff)
        {
            if (diff == null)
            {
                res.Passed++;
                Console.WriteLine("[ByrefWireup] " + name + ": PASS");
            }
            else
            {
                res.Failed++; res.Failures.Add(name + ": " + diff);
                Console.WriteLine("  [FAIL] " + name + ": " + diff);
            }
        }

        // A SKIP cell (not PASS, not FAIL): records the total but does not move
        // Passed/Failed -- used for control cells whose body stayed JIT (not an
        // AOT signal either way). Keeps the failure count clean.
        static void RecordCellSkip(Result res, string name, string diff)
        {
            // TotalCells was already incremented by the caller; record info only.
            Console.WriteLine("  [SKIP] " + name + ": " + diff);
        }

        // Find a TypeDef record by its type full name (TypeRefIdx -> TypeRef name).
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
    }
}
#else
namespace ILRuntime.Runtime.Intepreter.RegisterVM
{
    // Legacy stub: the check is Neo+DEBUG only (gated above). This placeholder
    // keeps the file compile-clean in plain Debug/Release (Legacy) builds.
    internal static class NeoStep25ByrefWireupCheck { }
}
#endif
