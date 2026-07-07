#if ENABLE_NEO_MODE && DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;

using ILRuntime.CLR.TypeSystem;

namespace ILRuntime.Runtime.Intepreter.RegisterVM
{
    /// <summary>
    /// Step 26 Neo-vs-Legacy benchmark self-check (host-side, DEBUG+Neo only) --
    /// the perf-validation capstone. The LAST numbered AOT-chain step.
    ///
    /// Drives a fixed set of parameterless static bench methods on a dedicated
    /// probe type (TestCases.NeoBenchProbe, in TestCases/NeoStep26BenchProbe.cs)
    /// through `appdomain.Invoke` and host-times each invocation with a REAL
    /// `System.Diagnostics.Stopwatch`. The generic test harness IGNORES
    /// `[ILRuntimeTest(IsPerformanceTest)]` methods and emits no clean timing
    /// line, so a host-side self-check that re-enters via `appdomain.Invoke` is
    /// the robust timing path (design.md section 3.1). The host times the
    /// interpreted workload; an interpreted `Stopwatch` is NOT used.
    ///
    /// For each bench the self-check asserts the returned primitive EQUALS a
    /// known-expected value via the DIVIDE-ASSERT pattern
    /// (`if (!Equals(result, expected)) int x = 1 / 0;`). This is the
    /// CORRECTNESS-OF-MEASUREMENT gate: a bench that returns the wrong value
    /// did the WRONG WORK (not just some work), and a timing sample off a bench
    /// that did the wrong work is WORTHLESS. The adversarial probe (tasks.md
    /// 5.4) mutates an expected value -> the divide-assert MUST trip, proving
    /// the gate is load-bearing (a green smoke does not prove a gate correct).
    ///
    /// The self-check emits one structured line per bench:
    ///   BENCH:<benchName>:<iterations>:<elapsedTicks>
    /// The Neo-vs-Legacy RATIO is computed by the SEPARATE runner script
    /// (scripts/run-neo-bench.{ps1,sh}) across two CLI invocations (Debug_Neo
    /// and plain Debug + useRegister=true). The self-check does NOT assert a
    /// ratio threshold: the development host is NOT the performance-baseline
    /// host, and absolute timings are noise across machines and load. A green
    /// self-check proves measurement works AND each bench computed its expected
    /// value; it does NOT prove Neo is fast.
    ///
    /// A cell FAILS only if the bench threw, returned a wrong value (the
    /// divide-assert tripped), or produced a non-positive timing.
    ///
    /// Invoked host-side (CLI special mode "NeoStep26Bench", an EXACT-MATCH
    /// hook -- NOT a TestName Contains filter, so the probe type name is
    /// irrelevant to hook activation).
    /// </summary>
    public static class NeoStep26BenchCheck
    {
        public class Result
        {
            public int TotalCells;
            public int Passed;
            public int Failed;
            public List<string> Failures = new List<string>();
            public List<string> BenchLines = new List<string>();
        }

        public static Result Run(ILRuntime.Runtime.Enviorment.AppDomain appdomain)
        {
            var res = new Result();

            // ---- locate the probe type ----
            if (!appdomain.LoadedTypes.TryGetValue("TestCases.NeoBenchProbe", out var probeIType) ||
                !(probeIType is ILType probeType))
            {
                res.Failures.Add("TestCases.NeoBenchProbe not loaded / not an ILType");
                res.TotalCells = 1; res.Failed = 1;
                return res;
            }

            // ===== The bench matrix: (name, methodName, iterations, expected) =====
            // `Iterations` MUST match the probe's `NeoBenchProbe.N` (the BENCH line
            // reports it). Expected values are the deterministic primitives each
            // bench accumulates (see TestCases/NeoStep26BenchProbe.cs for the
            // per-bench derivation).
            var cells = new Cell[]
            {
                new Cell("FieldAccess",    "BenchFieldAccess",    200000, 200000L),
                new Cell("MethodCall",     "BenchMethodCall",     200000, 20000100000L),
                new Cell("ValueType",      "BenchValueType",      200000, 20000300000L),
                new Cell("VirtualDispatch","BenchVirtualDispatch",200000, 39999800000L),
                new Cell("Array",          "BenchArray",          200000, 400000L),
            };

            Console.WriteLine($"[NeoStep26] host-side bench self-check: {cells.Length} workloads, {cells[0].Iterations} iterations each");
            // Emit the host Stopwatch frequency so the runner script can convert
            // ElapsedTicks -> ms EXACTLY (ms = ticks * 1000 / frequency) on any
            // host, rather than assuming the standard 10^7 Windows frequency.
            Console.WriteLine($"BENCHFREQ:{Stopwatch.Frequency}");

            foreach (var c in cells)
            {
                res.TotalCells++;
                var m = probeType.GetMethod(c.MethodName, 0);
                if (m == null)
                {
                    res.Failed++;
                    res.Failures.Add($"{c.Name}: method {c.MethodName} not found on TestCases.NeoBenchProbe");
                    Console.WriteLine($"  [FAIL] {c.Name}: method {c.MethodName} not found");
                    continue;
                }

                // ---- host-time a single appdomain.Invoke of the bench ----
                object r;
                var sw = Stopwatch.StartNew();
                try { r = appdomain.Invoke(m, null); }
                catch (Exception ex) { r = new ThrownMarker(ex); }
                sw.Stop();
                long ticks = sw.ElapsedTicks;

                // ---- correctness-of-measurement gate (divide-assert) ----
                // A bench returning the wrong value did the WRONG WORK. The 1/0
                // makes the trip loud; we catch it to RECORD the cell as failed
                // (rather than abort the whole self-check on the first wrong bench).
                bool correct = ValueEquals(r, c.Expected);
                if (!correct)
                {
                    try
                    {
                        int zero = 0;
                        int _ = 1 / zero;   // throws DivideByZeroException -- the gate tripping
                    }
                    catch (DivideByZeroException) { /* recorded below */ }
                }

                // ---- timing sanity: a completed bench must produce a positive timing ----
                bool timingOk = ticks > 0;

                if (correct && timingOk)
                {
                    res.Passed++;
                    string bench = $"BENCH:{c.Name}:{c.Iterations}:{ticks}";
                    res.BenchLines.Add(bench);
                    Console.WriteLine($"{bench}   (PASS, returned {Format(r)} == expected {c.Expected})");
                }
                else
                {
                    res.Failed++;
                    string diff = !correct
                        ? $"correctness gate tripped: got={Format(r)} expected={c.Expected}"
                        : $"non-positive timing ticks={ticks}";
                    res.Failures.Add($"{c.Name}: {diff}");
                    Console.WriteLine($"  [FAIL] {c.Name}: {diff}");
                }
            }

            return res;
        }

        static bool ValueEquals(object o, long expected)
        {
            if (o is ThrownMarker) return false;
            if (o == null) return false;
            try { return Convert.ToInt64(o) == expected; }
            catch { return false; }
        }

        static string Format(object o)
        {
            if (o is ThrownMarker tm) return "threw " + (tm.Ex?.GetType().Name ?? "?") + ": " + (tm.Ex?.Message ?? "");
            return o == null ? "null" : o.ToString();
        }

        struct Cell
        {
            public string Name;
            public string MethodName;
            public int Iterations;
            public long Expected;
            public Cell(string name, string methodName, int iterations, long expected)
            {
                Name = name; MethodName = methodName; Iterations = iterations; Expected = expected;
            }
        }

        sealed class ThrownMarker
        {
            public readonly Exception Ex;
            public ThrownMarker(Exception ex) { Ex = ex; }
        }
    }
}
#endif
