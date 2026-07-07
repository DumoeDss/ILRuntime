#if ENABLE_NEO_MODE
using System;
using System.Collections.Generic;
using System.IO;

using ILRuntime.Runtime.NeoAOT;

namespace ILRuntimeNeoCompiler
{
    // ===== ilrt_neoc: the Step-24 standalone precompile CLI =====
    //
    // A THIN wrapper over the in-assembly public NeoCompiler driver. It does ONLY:
    // arg parse -> open streams -> call NeoCompiler.Compile -> print the report ->
    // map the outcome to an exit code. NO runtime / JIT / serializer / template
    // logic lives here -- all compile logic is in ILRuntime.Runtime.NeoAOT.NeoCompiler
    // (in-assembly, where the internal NeoAOT types are reachable).
    //
    // Usage:
    //   ilrt_neoc <input.dll> <output.neo> [reference-assembly paths...]
    //
    // Exit codes:
    //   0 -- every method + template compiled (NeoCompilerResult.IsComplete).
    //   2 -- one or more methods skipped (partial .neo still written).
    //   1 -- fatal error (bad args / input not found / load / serializer threw).
    //
    // Neo-only: the whole file (and the driver) is #if ENABLE_NEO_MODE. The tool
    // project builds only with Debug_Neo / Release_Neo.
    internal static class Program
    {
        static int Main(string[] args)
        {
            // ---- arg parse: positional <input> <output> [refs...] ----
            if (args.Length < 2)
            {
                PrintUsage();
                return 1;
            }
            string input = args[0];
            string output = args[1];

            if (string.IsNullOrEmpty(input) || !File.Exists(input))
            {
                Console.Error.WriteLine("ilrt_neoc: input assembly not found: " + (input ?? "(null)"));
                PrintUsage();
                return 1;
            }
            if (string.IsNullOrEmpty(output))
            {
                Console.Error.WriteLine("ilrt_neoc: output path is empty");
                PrintUsage();
                return 1;
            }

            var refs = new List<string>();
            for (int i = 2; i < args.Length; i++)
            {
                if (!string.IsNullOrEmpty(args[i])) refs.Add(args[i]);
            }

            // ---- compile ----
            var driver = new NeoCompiler();
            NeoCompilerResult result;
            try
            {
                using (var outputStream = File.Create(output))
                {
                    result = driver.Compile(input, refs, outputStream);
                }
            }
            catch (NeoCompilerFatal fatal)
            {
                Console.Error.WriteLine("ilrt_neoc: FATAL: " + fatal.Message);
                if (fatal.InnerException != null)
                    Console.Error.WriteLine("  inner: " + fatal.InnerException.GetType().Name + ": " + fatal.InnerException.Message);
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ilrt_neoc: FATAL: " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }

            // ---- report + exit code ----
            int skipped = result.Skipped != null ? result.Skipped.Count : 0;
            Console.WriteLine(
                "ilrt_neoc: compiled {0} methods, {1} templates, {2} types" + (skipped > 0 ? " ({3} skipped)" : ""),
                result.MethodsCompiled, result.TemplatesCaptured, result.TypesCompiled, skipped);
            if (result.Skipped != null)
            {
                foreach (var s in result.Skipped)
                    Console.Error.WriteLine("  SKIP " + s.MethodDisplay + ": " + s.ExceptionType + ": " + s.Message);
            }
            return result.IsComplete ? 0 : 2;
        }

        static void PrintUsage()
        {
            Console.Error.WriteLine("Usage: ilrt_neoc <input.dll> <output.neo> [reference-assembly paths...]");
            Console.Error.WriteLine("  Precompiles an IL assembly to a .neo (Step-24 Neo AOT CLI).");
            Console.Error.WriteLine("  Exit codes: 0 = complete, 2 = partial (some methods skipped), 1 = fatal.");
        }
    }
}
#else
// The tool is Neo-only. Plain Debug/Release compile this stub so the project
// still builds (with a clear error if invoked) -- but the real entry is the
// Debug_Neo / Release_Neo build above.
using System;

namespace ILRuntimeNeoCompiler
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            Console.Error.WriteLine("ilrt_neoc: this build was compiled WITHOUT ENABLE_NEO_MODE (Legacy). Rebuild with Debug_Neo or Release_Neo.");
            return 1;
        }
    }
}
#endif
