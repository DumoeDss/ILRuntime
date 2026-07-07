#if ENABLE_NEO_MODE && DEBUG
using System;
using System.Collections.Generic;
using System.IO;

using ILRuntime.CLR.Method;
using ILRuntime.CLR.TypeSystem;
using ILRuntime.Mono.Cecil;
using ILRuntime.Runtime.Intepreter.OpCodes;
using ILRuntime.Runtime.NeoAOT;

namespace ILRuntime.Runtime.Intepreter.RegisterVM
{
    /// <summary>
    /// Step 24 V1 CLI-roundtrip self-check (host-side, DEBUG+Neo only).
    ///
    /// The load-bearing gate for Step 24. It exercises the SAME public NeoCompiler
    /// driver the ilrt_neoc CLI uses (the explicit-types overload, scoped to a
    /// small dedicated probe type set so the compile is sub-second), produces a
    /// .neo in a MemoryStream, READs it back via NeoAssemblyReader, and asserts the
    /// roundtrip EQUALS a direct in-memory compile -- reusing the Step-23
    /// comparators (OpCodeRsEqual / MethodDefsEqual / TemplatesEqual /
    /// TypeDefsEqual). It additionally asserts the driver's generic/non-generic
    /// split (a generic-method definition MUST go to the TemplateTable, NEVER the
    /// MethodDefTable) and that every emitted method body is byte-equal to an
    /// independent fresh compile. This proves the driver wiring (enumeration +
    /// force-compile + template capture + serialize + the input-set filter) is
    /// correct end-to-end. V2 functional (deserialize -> ExecuteNeo) is Step 25.
    ///
    /// Invoked host-side (CLI special mode "NeoStep24CliRoundtrip").
    /// </summary>
    public static class NeoStep24CliRoundtripCheck
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

            // ---- locate the probe types (outer + nested) ----
            ILType outer = null, nested = null;
            if (!appdomain.LoadedTypes.TryGetValue("TestCases.NeoStep24CliProbe", out var outerIType) ||
                !(outerIType is ILType))
            {
                res.Failures.Add("TestCases.NeoStep24CliProbe not loaded / not an ILType");
                res.TotalCells = 1; res.Failed = 1;
                return res;
            }
            outer = outerIType as ILType;
            // Cecil flattens nested types into LoadedTypes keyed "Outer/Nested".
            if (appdomain.LoadedTypes.TryGetValue("TestCases.NeoStep24CliProbe/NestedProbe", out var nestedIType))
                nested = nestedIType as ILType;

            var probeTypes = new List<ILType>();
            probeTypes.Add(outer);
            if (nested != null) probeTypes.Add(nested);
            else res.Failures.Add("note: TestCases.NeoStep24CliProbe/NestedProbe not found (nested-type cell skipped)");

            // ===== Cell 1: drive the NeoCompiler (the SAME driver the CLI uses) =====
            // Compile the probe type set to a MemoryStream, then read it back.
            res.TotalCells++;
            NeoCompilerResult driverResult;
            NeoAssemblyModel model1;
            try
            {
                using (var ms = new MemoryStream())
                {
                    driverResult = new NeoCompiler().Compile(probeTypes, ms);
                    ms.Position = 0;
                    model1 = NeoAssemblyReader.Read(ms);
                }
            }
            catch (Exception ex)
            {
                res.Failed++; res.Failures.Add($"Cell1 driver: threw {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"  [FAIL] Cell1 driver: threw {ex.GetType().Name}: {ex.Message}");
                return res;
            }
            // The probe is designed to compile cleanly under Neo; a skip here is a
            // real wiring bug (an op the probe should not exercise NIE-d).
            if (!driverResult.IsComplete)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var s in driverResult.Skipped) sb.Append(s.MethodDisplay).Append(" (").Append(s.ExceptionType).Append("); ");
                res.Failed++; res.Failures.Add($"Cell1 driver: {driverResult.Skipped.Count} skip(s): {sb}");
            }
            else
            {
                res.Passed++;
                Console.WriteLine($"[NeoStep24] Cell1 driver: PASS compiled {driverResult.MethodsCompiled} methods, {driverResult.TemplatesCaptured} templates, {driverResult.TypesCompiled} types");
            }

            // ===== Replicate the driver's partition (for the expected model + counts) =====
            // SAME iteration as NeoCompiler.CompileCore: per type, GetMethods() then
            // GetConstructors(); route generic DEFINITIONS to templates[], non-generic
            // to methods[]. This yields the survivor arrays in the SAME order the
            // driver hands them to NeoAssemblyWriter.Write, so the deserialized
            // MethodDefs/Templates are positional with these lists.
            var ngen = new List<ILMethod>();
            var gdefs = new List<ILMethod>();
            var templates = new List<GenericMethodTemplate>();
            foreach (var t in probeTypes)
            {
                var tm = new List<ILMethod>();
                if (t.GetMethods() != null)
                    foreach (var m in t.GetMethods()) { var ilm = m as ILMethod; if (ilm != null) tm.Add(ilm); }
                if (t.GetConstructors() != null) tm.AddRange(t.GetConstructors());
                foreach (var ilm in tm)
                {
                    if (ilm.IsGenericInstance) continue;
                    if (ilm.GenericParameterCount > 0)
                    {
                        gdefs.Add(ilm);
                        var tpl = GenericMethodTemplateOps.ForceBuildTemplate(appdomain, t, ilm);
                        if (tpl != null) templates.Add(tpl);
                    }
                    else ngen.Add(ilm);
                }
            }

            // ===== Cell 2: header =====
            res.TotalCells++;
            {
                string diff = null;
                if (model1.Header.Magic != NeoAssemblyFormat.Magic) diff = $"magic 0x{model1.Header.Magic:X8}";
                else if (model1.Header.Version != NeoAssemblyFormat.Version) diff = $"version {model1.Header.Version}";
                RecordCell(res, "Cell2 header", diff);
            }

            // ===== Cell 3: counts + the generic/non-generic split =====
            res.TotalCells++;
            {
                string diff = null;
                if (model1.TypeDefs.Length != probeTypes.Count) diff = $"TypeDefs {model1.TypeDefs.Length} vs {probeTypes.Count}";
                else if (model1.MethodDefs.Length != ngen.Count) diff = $"MethodDefs {model1.MethodDefs.Length} vs ngen {ngen.Count}";
                else if (model1.Templates.Length != templates.Count) diff = $"Templates {model1.Templates.Length} vs {templates.Count}";
                // The split: every entry in ngen MUST be non-generic; every gdef MUST
                // be a generic definition (the binding correctness rule).
                for (int i = 0; i < ngen.Count && diff == null; i++)
                    if (ngen[i].GenericParameterCount > 0) diff = $"ngen[{i}] ({ngen[i].Name}) is a generic definition -- split violated";
                for (int i = 0; i < gdefs.Count && diff == null; i++)
                    if (gdefs[i].GenericParameterCount <= 0) diff = $"gdefs[{i}] ({gdefs[i].Name}) is NOT a generic definition";
                RecordCell(res, "Cell3 counts+split", diff,
                    $"typedefs={model1.TypeDefs.Length} methoddefs={model1.MethodDefs.Length} templates={model1.Templates.Length} ngen={ngen.Count} gdefs={gdefs.Count}");
            }

            // ===== Cell 4: model1 == model2 element-wise (reuses Step-23 comparators) =====
            // Build the EXPECTED model by calling NeoAssemblyWriter.Write directly on
            // the same partitioned arrays (same order). Both models use the same Write
            // + builder path on identical inputs, so the ref-idx scheme matches and a
            // field-for-field comparison is exact. This proves the driver enumerates,
            // partitions, captures, and serializes IDENTICALLY to a correct direct
            // Write -- i.e. the wiring is sound.
            res.TotalCells++;
            {
                NeoAssemblyModel model2 = null;
                string diff = null;
                try
                {
                    using (var ms2 = new MemoryStream())
                    {
                        new NeoAssemblyWriter().Write(probeTypes.ToArray(), ngen.ToArray(), templates.ToArray(), ms2);
                        ms2.Position = 0;
                        model2 = NeoAssemblyReader.Read(ms2);
                    }
                    if (diff == null && model1.TypeDefs.Length == model2.TypeDefs.Length)
                        for (int i = 0; i < model1.TypeDefs.Length && diff == null; i++)
                            diff = NeoStep23RoundtripCheck.TypeDefsEqual(model1.TypeDefs[i], model2.TypeDefs[i]);
                    if (diff == null && model1.MethodDefs.Length == model2.MethodDefs.Length)
                        for (int i = 0; i < model1.MethodDefs.Length && diff == null; i++)
                            diff = NeoStep23RoundtripCheck.MethodDefsEqual(model1.MethodDefs[i], model2.MethodDefs[i]);
                    if (diff == null && model1.Templates.Length == model2.Templates.Length)
                        for (int i = 0; i < model1.Templates.Length && diff == null; i++)
                            diff = NeoStep23RoundtripCheck.TemplatesEqual(model1.Templates[i], model2.Templates[i]);
                }
                catch (Exception ex)
                {
                    diff = $"threw {ex.GetType().Name}: {ex.Message}";
                }
                RecordCell(res, "Cell4 model1==model2", diff);
            }

            // ===== Cell 5: independent body check -- each non-gen MethodDef body
            //       byte-equals a FRESH CompileFresh (not another Write). Proves the
            //       emitted bodies are the genuine correct compiles, method-by-method.
            //       model1.MethodDefs is positional with ngen (same Write order). =====
            res.TotalCells++;
            {
                string diff = null;
                try
                {
                    for (int i = 0; i < ngen.Count && diff == null; i++)
                    {
                        var fresh = NeoAssemblyWriter.CompileFresh(ngen[i], out _);
                        if (!NeoStep23RoundtripCheck.OpCodeRsEqual(fresh.NeoExecuteBody, model1.MethodDefs[i].NeoExecuteBody))
                            diff = $"body mismatch for {ngen[i].DeclearingType?.FullName}.{ngen[i].Name}";
                    }
                    // Each template body byte-equals the in-memory ForceBuildTemplate body.
                    for (int i = 0; i < templates.Count && diff == null; i++)
                    {
                        if (!NeoStep23RoundtripCheck.OpCodeRsEqual(templates[i].TemplateBody, model1.Templates[i].TemplateBody))
                            diff = $"template body mismatch for template #{i}";
                    }
                }
                catch (Exception ex)
                {
                    diff = $"threw {ex.GetType().Name}: {ex.Message}";
                }
                RecordCell(res, "Cell5 fresh-body", diff);
            }

            return res;
        }

        static void RecordCell(Result res, string name, string diff, string extra = null)
        {
            if (diff == null)
            {
                res.Passed++;
                Console.WriteLine($"[NeoStep24] {name}: PASS" + (extra != null ? " " + extra : ""));
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
