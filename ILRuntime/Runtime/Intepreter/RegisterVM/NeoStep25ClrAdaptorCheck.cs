#if ENABLE_NEO_MODE && DEBUG
using System;
using System.Collections.Generic;
using System.IO;

using ILRuntime.CLR.TypeSystem;
using ILRuntime.Runtime.NeoAOT;

namespace ILRuntime.Runtime.Intepreter.RegisterVM
{
    /// <summary>
    /// Step 25 CLR-adaptor host-side self-check (DEBUG+Neo only) -- the TRUE-
    /// COMPLETION gate for the standalone AOT CLI's robustness to IL types whose
    /// CLR base/interface needs a <c>CrossBindingAdaptor</c> the generic compile
    /// AppDomain does NOT register.
    ///
    /// Drives the FILE-PATH <c>NeoCompiler.Compile</c> overload (the one the
    /// standalone <c>ilrt_neoc</c> CLI uses) on the dedicated <c>NeoClrProbe.dll</c>,
    /// which now carries TWO probe types:
    /// <list type="bullet">
    /// <item><c>NeoClrProbe.ExceptionProbe : System.Exception</c> -- a CLR base
    /// resolved by a BUILT-IN adaptor (<c>ExceptionAdaptor</c>, registered by the
    /// AppDomain ctor). The compile's per-type pre-filter resolves it -> EMITTED.</item>
    /// <item><c>NeoClrProbe.AdaptorProbe : ILRuntimeTest.TestFramework.TestClass2</c>
    /// -- a CLR base whose adaptor (<c>TestClass2Adapter</c>) is registered ONLY by
    /// the test harness, never in the generic CLI. BEFORE the fix this threw
    /// <c>NeoCompilerFatal("serializer failure: Cannot find Adaptor for:...TestClass2")</c>
    /// (exit 1, no .neo); AFTER the fix the pre-filter catches the
    /// <c>TypeLoadException</c> and records a TYPE-level skip -> a valid .neo is
    /// written (exit-2 equivalent, no fatal).</item>
    /// </list>
    ///
    /// The probe assembly references <c>ILRuntimeTestBase.dll</c> (passed as the
    /// ref), so <c>TestClass2</c> resolves as a CLRType via the host CLR scan
    /// (the S3-5 <c>Assembly.LoadFrom</c> path) -- hitting the adaptor lookup at
    /// <c>ILType.cs:1593</c>, which is exactly the throw site closed by the
    /// CompileCore pre-filter. The generic CLI registers NO harness adaptor.
    ///
    /// Invoked host-side (CLI special mode "NeoStep25ClrAdaptor").
    /// </summary>
    public static class NeoStep25ClrAdaptorCheck
    {
        const string ExceptionProbeFullName = "NeoClrProbe.ExceptionProbe";
        const string AdaptorProbeFullName = "NeoClrProbe.AdaptorProbe";

        public class Result
        {
            public int TotalCells;
            public int Passed;
            public int Failed;
            public List<string> Failures = new List<string>();
            public int TypesCompiled;
            public int MethodsCompiled;
            public int TemplatesCaptured;
            public int SkippedCount;
            public List<string> Skipped = new List<string>();
        }

        public static Result Run(string probeDllPath, string ilRuntimeTestBaseDllPath)
        {
            var res = new Result();

            if (string.IsNullOrEmpty(probeDllPath) || !File.Exists(probeDllPath))
            {
                res.Failures.Add("NeoClrProbe.dll not found: " + (probeDllPath ?? "(null)") + " -- build NeoClrProbe first");
                res.TotalCells = 1; res.Failed = 1;
                return res;
            }

            // ===== Cell: FILE-PATH compile does NOT throw (the fix flips the
            // adaptor fatal to a graceful skip). BEFORE the fix this throws
            // NeoCompilerFatal("serializer failure: Cannot find Adaptor for:...TestClass2");
            // AFTER the fix it returns a NeoCompilerResult. =====
            NeoCompilerResult driverResult = null;
            NeoAssemblyModel model = null;
            res.TotalCells++;
            try
            {
                using (var ms = new MemoryStream())
                {
                    driverResult = new NeoCompiler().Compile(probeDllPath,
                        new[] { ilRuntimeTestBaseDllPath }, ms);
                    ms.Position = 0;
                    // Read the .neo back. NeoAssemblyReader.Read THROWS
                    // NotSupportedException on a wrong magic -> a non-null model
                    // here PROVES a valid .neo was written (stronger than a byte
                    // check). This is the standalone-process no-fatal + valid-.neo
                    // proof in one cell.
                    model = NeoAssemblyReader.Read(ms);
                }
                res.TypesCompiled = driverResult.TypesCompiled;
                res.MethodsCompiled = driverResult.MethodsCompiled;
                res.TemplatesCaptured = driverResult.TemplatesCaptured;
                res.SkippedCount = driverResult.Skipped.Count;
                foreach (var s in driverResult.Skipped)
                    res.Skipped.Add(s.MethodDisplay + " (" + s.ExceptionType + ": " + s.Message + ")");
            }
            catch (Exception ex)
            {
                res.Failed++;
                res.Failures.Add($"file-path Compile / Read threw {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"  [FAIL] file-path Compile / Read threw {ex.GetType().Name}: {ex.Message}");
                return res;     // the fix regressed -- no point continuing
            }
            res.Passed++;
            Console.WriteLine($"[NeoStep25ClrAdaptor] file-path Compile (no fatal): PASS ({driverResult.TypesCompiled} types, {driverResult.MethodsCompiled} methods, {driverResult.TemplatesCaptured} templates, {driverResult.Skipped.Count} skipped)");

            // ===== Cell: the .neo is valid (read back to a non-null model with a
            // TypeDef table). Read already enforced the magic; this asserts the
            // model is populated. =====
            res.TotalCells++;
            if (model != null && model.TypeDefs != null)
            {
                res.Passed++;
                Console.WriteLine($"[NeoStep25ClrAdaptor] valid .neo read back: PASS ({model.TypeDefs.Length} TypeDefs)");
            }
            else
            {
                res.Failed++;
                res.Failures.Add("NeoAssemblyReader.Read returned a null / empty model (invalid .neo)");
                Console.WriteLine("  [FAIL] NeoAssemblyReader.Read returned a null / empty model (invalid .neo)");
            }

            // ===== Cell: IsComplete == false (AdaptorProbe was skipped -> partial).
            // A complete compile here would mean the skip did not fire (regression). =====
            res.TotalCells++;
            if (!driverResult.IsComplete)
            {
                res.Passed++;
                Console.WriteLine("[NeoStep25ClrAdaptor] IsComplete == false (partial, as expected): PASS");
            }
            else
            {
                res.Failed++;
                res.Failures.Add("IsComplete == true: expected false (AdaptorProbe should have been skipped)");
                Console.WriteLine("  [FAIL] IsComplete == true: expected false (AdaptorProbe should have been skipped)");
            }

            // ===== Cell: AdaptorProbe is in the skip report with the right shape
            // -- a (type) marker, TypeLoadException, "Cannot find Adaptor for:".
            // THE decisive cell: BEFORE the fix the adaptor absence was a FATAL;
            // AFTER the fix it is a typed, reported TYPE-level skip. =====
            res.TotalCells++;
            {
                var match = FindTypeSkip(driverResult, AdaptorProbeFullName);
                string diff = null;
                if (match == null)
                    diff = $"no (type) skip entry for {AdaptorProbeFullName}";
                else if (match.ExceptionType != nameof(TypeLoadException))
                    diff = $"wrong ExceptionType for {AdaptorProbeFullName}: {match.ExceptionType} (expected TypeLoadException)";
                else if ((match.Message ?? "").IndexOf("Cannot find Adaptor for:", StringComparison.Ordinal) < 0)
                    diff = $"wrong Message for {AdaptorProbeFullName}: {match.Message}";
                if (diff == null)
                {
                    res.Passed++;
                    Console.WriteLine($"[NeoStep25ClrAdaptor] AdaptorProbe (type) skip reported: PASS ({match.ExceptionType}: {match.Message})");
                }
                else
                {
                    res.Failed++; res.Failures.Add(diff);
                    Console.WriteLine("  [FAIL] " + diff);
                }
            }

            // ===== Cell: ExceptionProbe (built-in adaptor) is NOT in the skip
            // report -- it resolved via ExceptionAdaptor and was compiled. Proves
            // the built-in-adaptor path is preserved (Fix A is a no-op). =====
            res.TotalCells++;
            {
                var match = FindTypeSkip(driverResult, ExceptionProbeFullName);
                if (match == null)
                {
                    res.Passed++;
                    Console.WriteLine($"[NeoStep25ClrAdaptor] ExceptionProbe not skipped (built-in adaptor resolved): PASS");
                }
                else
                {
                    res.Failed++;
                    res.Failures.Add($"ExceptionProbe was skipped (should resolve via ExceptionAdaptor): {match.ExceptionType}: {match.Message}");
                    Console.WriteLine($"  [FAIL] ExceptionProbe was skipped (should resolve via ExceptionAdaptor): {match.ExceptionType}: {match.Message}");
                }
            }

            // ===== Cell: ExceptionProbe TypeDef IS in the .neo (emitted, proving
            // the survivor was actually compiled + serialized -- not just
            // skipped). =====
            res.TotalCells++;
            {
                int tdIdx = FindTypeDefByName(model, ExceptionProbeFullName);
                if (tdIdx >= 0)
                {
                    res.Passed++;
                    Console.WriteLine($"[NeoStep25ClrAdaptor] ExceptionProbe TypeDef emitted to .neo: PASS (idx {tdIdx})");
                }
                else
                {
                    res.Failed++;
                    res.Failures.Add($"ExceptionProbe TypeDef not found in .neo (should be emitted as a survivor)");
                    Console.WriteLine("  [FAIL] ExceptionProbe TypeDef not found in .neo (should be emitted as a survivor)");
                }
            }

            // ===== Cell: AdaptorProbe TypeDef is NOT in the .neo (a skipped type
            // is omitted entirely -- its methods/templates/type-def are all gone).
            // This is the "skip is not contagious" + "omitted cleanly" proof. =====
            res.TotalCells++;
            {
                int tdIdx = FindTypeDefByName(model, AdaptorProbeFullName);
                if (tdIdx < 0)
                {
                    res.Passed++;
                    Console.WriteLine("[NeoStep25ClrAdaptor] AdaptorProbe TypeDef omitted from .neo: PASS");
                }
                else
                {
                    res.Failed++;
                    res.Failures.Add($"AdaptorProbe TypeDef unexpectedly present in .neo at idx {tdIdx} (a skipped type must be omitted)");
                    Console.WriteLine($"  [FAIL] AdaptorProbe TypeDef unexpectedly present in .neo at idx {tdIdx} (a skipped type must be omitted)");
                }
            }

            return res;
        }

        // Find a TYPE-level skip entry (MethodDisplay == "(type) <fullName>") by
        // the type's full name. Returns null if not found.
        static MethodSkip FindTypeSkip(NeoCompilerResult result, string typeFullName)
        {
            if (result == null || result.Skipped == null || string.IsNullOrEmpty(typeFullName))
                return null;
            string marker = "(type) " + typeFullName;
            foreach (var s in result.Skipped)
            {
                if (s != null && (s.MethodDisplay ?? "") == marker)
                    return s;
            }
            return null;
        }

        // Find a TypeDef record by its type full name (TypeRefIdx -> TypeRef name).
        // Mirrors NeoAssemblyLoader.Attach's MethodRefIdx -> MethodRef name lookup
        // and the helper in NeoStep25LoadExecCheck.
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
#endif
