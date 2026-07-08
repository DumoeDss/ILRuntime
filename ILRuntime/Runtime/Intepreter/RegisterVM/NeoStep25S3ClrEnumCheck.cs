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
    /// Step 25 S3-5 host-side self-check (DEBUG+Neo only) -- the TRUE-COMPLETION
    /// gate for host-CLR-assembly registration in the standalone AOT CLI.
    ///
    /// Drives the FILE-PATH <c>NeoCompiler.Compile</c> overload (the one the
    /// <c>Assembly.LoadFrom</c> fix lives in) on the dedicated
    /// <c>NeoClrProbe.dll</c> -- a tiny IL assembly whose two methods reference the
    /// host CLR enum <c>ILRuntimeTest.TestFramework.TestCLREnum</c> (defined in
    /// ILRuntimeTestBase.dll) -- with ILRuntimeTestBase.dll as a reference. BEFORE
    /// the fix this throws <c>NeoCompilerFatal</c>
    /// "Cannot find Type:...TestCLREnum" (AppDomain.cs:1409); AFTER the fix it
    /// returns a <c>NeoCompilerResult</c> and writes a clean <c>.neo</c>.
    ///
    /// Then reads the .neo back, attaches it to the in-process AppDomain (where the
    /// probe was loaded as IL), and invokes the CLR-enum-reading methods. The enum
    /// value ROUND-TRIPS (ReadEnum -> 1, ReadEnumCmp -> 1) -- the load-bearing proof
    /// that the resolved type is the REAL CLRType, not an ILType shadow (the old
    /// LoadAssembly-the-ref path shadowed TestCLREnum as ILType, got past every
    /// enum reference at compile, but NRE'd downstream; the round-trip catches such
    /// a shadow by mis-executing the enum local / equality).
    ///
    /// The dedicated probe assembly references ONLY the CLR enum (no host CLR class
    /// base -> no cross-binding adaptor needed), so the compile is clean end-to-end
    /// -- the full TestCases.dll, by contrast, also inherits host CLR classes whose
    /// adaptors the standalone CLI does not register (a separate, already-deferred
    /// gap, proven away here by the focused input).
    ///
    /// Invoked host-side (CLI special mode "NeoStep25S3ClrEnum").
    /// </summary>
    public static class NeoStep25S3ClrEnumCheck
    {
        const string ProbeTypeFullName = "NeoClrProbe.ClrEnumProbe";

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
            public int MethodsSkipped;
            public int TemplatesCaptured;
        }

        public static Result Run(ILRuntime.Runtime.Enviorment.AppDomain appdomain,
            string probeDllPath, string ilRuntimeTestBaseDllPath)
        {
            var res = new Result();

            if (string.IsNullOrEmpty(probeDllPath) || !File.Exists(probeDllPath))
            {
                res.Failures.Add("NeoClrProbe.dll not found: " + (probeDllPath ?? "(null)") + " -- build NeoClrProbe first");
                res.TotalCells = 1; res.Failed = 1;
                return res;
            }

            // ---- load the probe assembly into the in-process AppDomain (as IL) so
            // its type is present for the post-attach invoke. The probe is a
            // legitimate IL assembly; loading it as IL here is correct (this is NOT
            // the shadowing bug -- that was loading ILRuntimeTestBase, a CLR assembly,
            // as IL). ILRuntimeTestBase is already loaded in-process (project ref),
            // so the probe's TestCLREnum references resolve cleanly.
            //
            // The FileStream is intentionally NOT disposed here: Cecil's
            // ModuleDefinition.ReadModule(stream) (called inside LoadAssembly) keeps
            // the stream for LAZY metadata access, so the probe type's methods are
            // resolved lazily at Attach. This mirrors TestSession.Load, which holds
            // its FileStream as a field for the AppDomain's lifetime. The stream is
            // reclaimed when the CLI process exits (this is a short-lived debug self-
            // check). ----
            res.TotalCells++;
            try
            {
                var probeFs = File.OpenRead(probeDllPath);
                appdomain.LoadAssembly(probeFs);
            }
            catch (Exception ex)
            {
                res.Failed++;
                res.Failures.Add($"LoadAssembly(probe) threw {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"  [FAIL] LoadAssembly(probe) threw {ex.GetType().Name}: {ex.Message}");
                return res;
            }
            res.Passed++;
            Console.WriteLine("[NeoStep25S3] LoadAssembly(probe): PASS");

            // ---- locate the probe type ----
            ILType probeType = null;
            if (appdomain.LoadedTypes.TryGetValue(ProbeTypeFullName, out var pit) && pit is ILType pil)
                probeType = pil;
            if (probeType == null)
            {
                res.TotalCells++; res.Failed++;
                res.Failures.Add(ProbeTypeFullName + " not loaded / not an ILType after LoadAssembly");
                Console.WriteLine($"  [FAIL] {ProbeTypeFullName} not loaded / not an ILType after LoadAssembly");
                return res;
            }

            // ===== Cell: FILE-PATH compile (drives the Assembly.LoadFrom fix) =====
            // NeoClrProbe.dll + ILRuntimeTestBase.dll ref, via the file-path overload
            // (the path the Assembly.LoadFrom fix lives in). The fresh AppDomain
            // NeoCompiler creates resolves TestCLREnum as CLRType via the host CLR
            // scan (ILRuntimeTestBase is Assembly.LoadFrom-ed into the host
            // System.AppDomain, shared by every ILRuntime AppDomain's GetType(string)
            // CLR fallback). BEFORE the fix: NeoCompilerFatal
            // "Cannot find Type:...TestCLREnum" (AppDomain.cs:1409). AFTER: a result.
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
                    model = NeoAssemblyReader.Read(ms);
                }
                res.MethodsCompiled = driverResult.MethodsCompiled;
                res.MethodsSkipped = driverResult.Skipped.Count;
                res.TemplatesCaptured = driverResult.TemplatesCaptured;
            }
            catch (Exception ex)
            {
                res.Failed++;
                res.Failures.Add($"file-path Compile threw {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"  [FAIL] file-path Compile threw {ex.GetType().Name}: {ex.Message}");
                return res;     // the fix regressed -- no point continuing
            }
            res.Passed++;
            Console.WriteLine($"[NeoStep25S3] file-path Compile: PASS ({driverResult.MethodsCompiled} methods, {driverResult.TemplatesCaptured} templates, {driverResult.Skipped.Count} skipped)");

            // ===== Cell: NO TestCLREnum / "Cannot find Type" skip =====
            res.TotalCells++;
            {
                var sb = new System.Text.StringBuilder();
                foreach (var s in driverResult.Skipped)
                {
                    string hay = (s.MethodDisplay ?? "") + " " + (s.Message ?? "") + " " + (s.ExceptionType ?? "");
                    if (hay.IndexOf("TestCLREnum", StringComparison.Ordinal) >= 0 ||
                        hay.IndexOf("Cannot find Type", StringComparison.Ordinal) >= 0)
                        sb.Append(s.MethodDisplay).Append(" (").Append(s.ExceptionType).Append(": ").Append(s.Message).Append("); ");
                }
                if (sb.Length == 0)
                {
                    res.Passed++;
                    Console.WriteLine("[NeoStep25S3] no TestCLREnum / Cannot-find-Type skip: PASS");
                }
                else
                {
                    res.Failed++;
                    res.Failures.Add($"TestCLREnum/CLR-resolution skips: {sb}");
                    Console.WriteLine($"  [FAIL] TestCLREnum/CLR-resolution skips: {sb}");
                }
            }

            // ===== Cell: Attach the deserialized .neo (binds the probe methods) =====
            NeoLoadReport report = null;
            res.TotalCells++;
            try
            {
                report = NeoAssemblyLoader.Attach(appdomain, model);
            }
            catch (Exception ex)
            {
                res.Failed++;
                res.Failures.Add($"Attach threw {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"  [FAIL] Attach threw {ex.GetType().Name}: {ex.Message}");
                return res;
            }
            res.AttachedCount = report.Attached.Count;
            res.SkippedCount = report.Skipped.Count;
            res.Skipped.AddRange(report.Skipped.ConvertAll(s => s.reason + ": " + s.target));
            res.Passed++;
            Console.WriteLine($"[NeoStep25S3] Attach: PASS -- {report.Attached.Count} attached, {report.Skipped.Count} skipped");

            // ===== Cell: the probe methods attached (their AOT bodies loaded) =====
            res.TotalCells++;
            {
                bool readEnum = false, readEnumCmp = false;
                foreach (var a in report.Attached)
                {
                    if (a.EndsWith(".ReadEnum")) readEnum = true;
                    if (a.EndsWith(".ReadEnumCmp")) readEnumCmp = true;
                }
                if (readEnum && readEnumCmp)
                {
                    res.Passed++;
                    Console.WriteLine("[NeoStep25S3] probe methods attached (ReadEnum, ReadEnumCmp): PASS");
                }
                else
                {
                    res.Failed++;
                    res.Failures.Add($"probe attach: ReadEnum={readEnum} ReadEnumCmp={readEnumCmp}");
                    Console.WriteLine($"  [FAIL] probe attach: ReadEnum={readEnum} ReadEnumCmp={readEnumCmp}");
                }
            }

            // ===== Cells: invoke the probe -> enum value round-trips =====
            // THE load-bearing CLRType-not-shadow proof. The probe body was compiled
            // in NeoCompiler's FRESH AppDomain (where the fix resolves TestCLREnum as
            // CLRType), serialized to .neo by NAME, attached here, and re-resolved via
            // appdomain.GetType(fullName) (the same CLR fallback). A shadow ILType
            // wrap (the old LoadAssembly-the-ref bug) would mis-execute the enum local
            // / equality here. Returning the underlying int uses the Run shim's
            // primitive-return path (NeoBoxReturnValue, the typeof(int) arm).
            foreach (var cell in new[] { new InvokeCell("ReadEnum", 1), new InvokeCell("ReadEnumCmp", 1) })
            {
                res.TotalCells++;
                var m = probeType.GetMethod(cell.Name, 0);
                if (!(m is ILMethod ilm) || !ilm.isNeoAotBody)
                {
                    res.Failed++;
                    res.Failures.Add($"{cell.Name}: method missing or AOT body not attached");
                    Console.WriteLine($"  [FAIL] {cell.Name}: method missing or AOT body not attached");
                    continue;
                }
                object r;
                try { r = appdomain.Invoke(m, null); }
                catch (Exception ex) { r = new ThrownMarker(ex); }
                string diff = ValueEquals(r, cell.Expected)
                    ? null : $"got={Format(r)} expected={cell.Expected}";
                RecordCell(res, $"{cell.Name} round-trip (CLRType, not shadow)", diff);
            }

            return res;
        }

        struct InvokeCell
        {
            public string Name;
            public int Expected;
            public InvokeCell(string name, int expected) { Name = name; Expected = expected; }
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
            try { return Convert.ToInt32(o) == expected; } catch { return false; }
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
                Console.WriteLine($"[NeoStep25S3] {name}: PASS");
            }
            else
            {
                res.Failed++;
                res.Failures.Add($"{name}: {diff}");
                Console.WriteLine($"  [FAIL] {name}: {diff}");
            }
        }
    }
}
#endif
