using System;
using System.Diagnostics;

namespace TestCases
{
    // ===== Step 26: the Neo-vs-Legacy performance benchmark probe =====
    //
    // A dedicated probe type for the Step-26 host-side benchmark self-check
    // (ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep26BenchCheck.cs). The
    // self-check invokes each Bench* method below via appdomain.Invoke and
    // host-times it with a REAL System.Diagnostics.Stopwatch (the generic
    // test harness IGNORES [ILRuntimeTest(IsPerformanceTest)] methods and emits
    // no clean timing line, so a host-side self-check is the robust path -- see
    // design.md section 3.1).
    //
    // EACH Bench* method is a tight N-iteration loop that returns the ACCUMULATED
    // work as a primitive (long). The self-check pins each method's known-expected
    // return value and asserts it via the divide-assert pattern -- this is the
    // correctness-of-measurement gate: a bench that returns the wrong value did
    // the WRONG WORK, not just some work, and is worthless as a timing sample
    // (the "green smoke does not prove a gate correct" lesson).
    //
    // NAMING CONTRACT (load-bearing for the regression gate): the type name
    // `NeoBenchProbe` and the method names `Bench*` deliberately do NOT contain
    // the substring `NeoStep`. The test harness builds TestName as
    // `<TypeFullName>.<MethodName>` (TestSession.cs:94,100 -> BaseTestUnit.cs:29),
    // and the regression smoke runs under the Contains filter `NeoStep`. If the
    // probe type/method names contained `NeoStep`, the 5 bench methods would be
    // discovered as test units and picked up by the `NeoStep` regression filter,
    // changing the 215/215 baseline. The bench self-check is driven by the
    // EXACT-MATCH CLI hook `if (nameFilter == "NeoStep26Bench")` (a string ==,
    // NOT a TestName Contains), so the probe type name is irrelevant to hook
    // activation. The file name keeps the `NeoStep26` provenance; the CLASS name
    // avoids the `NeoStep` substring so the regression filter cannot match.
    //
    // PARAMETERLESS + primitive-return by design: (1) avoids the Step-6
    // parameterless-Run shim's no-arg limitation (F-11/F-12 family); (2) avoids
    // the F-12 reference-return limitation (the Run entry returns primitives
    // only). NO [ILRuntimeTest] tag (avoids the ignored-perf-test path -- the
    // generic loop BaseTestUnit.Invoke only IGNORES methods tagged
    // IsPerformanceTest, so untagged bench methods RUN under the generic loop,
    // which is what lets the runner script drive them under BOTH engines). NO
    // Neo dependency -- the methods are plain C#, runnable by BOTH ExecuteNeo
    // and ExecuteR, which is what makes the Neo-vs-Legacy ratio apples-to-apples
    // on the SAME TestCases.dll.
    //
    // The 5 workload shapes (design.md section 3.2):
    //   BenchFieldAccess    -- instance field read+write on an IL class
    //                          (Ldfld_*_Inline / Stfld_*_Inline + the heap path)
    //   BenchMethodCall     -- a static IL method call
    //                          (the Neo call ABI + InvokeNeoCallTarget)
    //   BenchValueType      -- an IL struct field read + arithmetic
    //                          (Move_Vt + the in-frame VT path)
    //   BenchVirtualDispatch-- a virtual / interface call
    //                          (the Neo VTable + Callvirt dispatch)
    //   BenchArray          -- a rank-1 array element read+write
    //                          (Ldelem_* / Stelem_*)
    //
    // N is chosen so each bench takes a measurable-but-bounded wall-clock (the
    // whole suite is sub-second per config; the handoff's >10s = infinite-loop
    // heuristic). N is duplicated in the self-check's iteration table -- the two
    // MUST stay in sync (the BENCH:<name>:<iters>:<ticks> line reports N).
    //
    // SELF-EMIT (OQ1 resolution, design.md section 3.5): each Bench* method
    // wraps ONLY the N-iteration loop in a System.Diagnostics.Stopwatch and
    // emits one structured line `BENCH:<friendlyName>:<N>:<sw.ElapsedTicks>`
    // AFTER the loop. This makes the bench result parsable by the runner script
    // (scripts/run-neo-bench.{ps1,sh}) under BOTH engines: the runner drives the
    // 5 methods via the generic test loop with the `NeoBenchProbe` filter under
    // Debug_Neo AND plain Debug + useRegister=true, and parses the `BENCH:` lines
    // from both configs into a `name | neo_ms | legacy_ms | ratio` table (the
    // spec runner scenario). The Stopwatch.StartNew/Stop/ElapsedTicks calls are
    // OUTSIDE the timed region (StartNew before the loop, Stop after), so the
    // measurement is exactly the loop wall-clock; the once-per-invocation
    // Stopwatch + Console.WriteLine overhead is NOT inside the timed region and
    // is negligible against N=200000 iterations either way. The probe self-emit
    // uses the SAME host Stopwatch tick unit as the NeoStep26BenchCheck
    // host-side self-check (frequency emitted as BENCHFREQ:), so the runner
    // converts ticks -> ms identically for both configs. The friendly names
    // (FieldAccess / MethodCall / ValueType / VirtualDispatch / Array) MATCH the
    // NeoStep26BenchCheck cell table names so a reader can cross-reference the
    // runner's ratio table against the self-check's host-timed BENCH lines.

    public class NeoBenchProbe
    {
        public const int N = 200000;

        // (a) instance field read+write on an IL class. obj.Value starts 0,
        //     +1 per iteration -> obj.Value == N. Expected: 200000.
        public static long BenchFieldAccess()
        {
            NeoBenchHolder obj = new NeoBenchHolder();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++)
            {
                obj.Value = obj.Value + 1;
            }
            sw.Stop();
            EmitBench("FieldAccess", N, sw.ElapsedTicks);
            return (long)obj.Value;
        }

        // (b) static IL method call. InvokeTarget(i) = i + 1.
        //     acc = sum_{i=0}^{N-1}(i+1) = N(N+1)/2. Expected: 20000100000.
        public static long BenchMethodCall()
        {
            long acc = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++)
            {
                acc += InvokeTarget(i);
            }
            sw.Stop();
            EmitBench("MethodCall", N, sw.ElapsedTicks);
            return acc;
        }

        // (c) IL struct field read + arithmetic. v.X = i, v.Y = 2 per iter;
        //     acc += i + 2. acc = sum_{i=0}^{N-1}(i+2) = N(N+3)/2.
        //     Expected: 20000300000.
        public static long BenchValueType()
        {
            NeoBenchVec v = new NeoBenchVec();
            long acc = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++)
            {
                v.X = i;
                v.Y = 2;
                acc += v.X + v.Y;
            }
            sw.Stop();
            EmitBench("ValueType", N, sw.ElapsedTicks);
            return acc;
        }

        // (d) virtual / interface dispatch. b.Compute(i) = i * 2.
        //     acc = sum_{i=0}^{N-1}(2i) = N(N-1). Expected: 39999800000.
        public static long BenchVirtualDispatch()
        {
            INeoBench b = new NeoBenchImplA();
            long acc = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++)
            {
                acc += b.Compute(i);
            }
            sw.Stop();
            EmitBench("VirtualDispatch", N, sw.ElapsedTicks);
            return acc;
        }

        // (e) rank-1 array element read+write. arr[i & 3] += 2 per iter;
        //     N is divisible by 4 so each of the 4 slots gets N/4 writes of +2
        //     -> each slot = 2*N/4 = N/2 = 100000; sum = 4 * 100000 = 2N.
        //     Expected: 400000.
        public static long BenchArray()
        {
            int[] arr = new int[4];
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++)
            {
                int idx = i & 3;
                arr[idx] = arr[idx] + 2;
            }
            sw.Stop();
            EmitBench("Array", N, sw.ElapsedTicks);
            return (long)(arr[0] + arr[1] + arr[2] + arr[3]);
        }

        // Static helper (has a parameter -> NOT discovered as a test unit; the
        // discovery gate requires ParameterCount == 0). Exercises the Neo call
        // ABI for a static IL method.
        static int InvokeTarget(int x)
        {
            return x + 1;
        }

        // Emits `BENCH:<name>:<iters>:<ticks>` via individual Console.Write
        // calls of primitives (NOT string concatenation). Under Neo the
        // interpreter-side string concat of primitives (`"s" + int + ":" + long`)
        // hits a primitive-to-string gap (the int and long render as 0); routing
        // each value through Console.Write(T) / Console.WriteLine(T) calls the
        // REAL CLR Console overload which ToString's the genuine value, so the
        // line is correct under BOTH ExecuteNeo and ExecuteR. The runner script
        // (scripts/run-neo-bench.{ps1,sh}) parses these BENCH: lines from both
        // configs. The string fragments are emitted via Console.Write(string)
        // (no concat); the friendly name, the iteration count, and the tick
        // count are emitted as primitive Console.Write/WriteLine calls.
        static void EmitBench(string name, int iters, long ticks)
        {
            Console.Write("BENCH:");
            Console.Write(name);
            Console.Write(":");
            Console.Write(iters);
            Console.Write(":");
            Console.WriteLine(ticks);
        }
    }

    // ---- helper types (top-level so their FullName is unambiguous; none have a
    //      public static parameterless method, so none are discovered as test
    //      units by TestSession.LoadTest). None of these names contain `NeoStep`. -

    public class NeoBenchHolder
    {
        public int Value;
    }

    public struct NeoBenchVec
    {
        public int X;
        public int Y;
    }

    public interface INeoBench
    {
        int Compute(int x);
    }

    public class NeoBenchImplA : INeoBench
    {
        public int Compute(int x)
        {
            return x * 2;
        }
    }
}
