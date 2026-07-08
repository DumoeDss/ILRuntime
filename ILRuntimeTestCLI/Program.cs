using ILRuntimeTest.TestBase;
using System;
using System.Collections.Generic;

namespace ILRuntimeTestCLI
{
    class Program
    {
        static int Main(string[] args)
        {
            TestSession session = new TestSession();
            try
            {
                return Run(args, session);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("=== UNHANDLED EXCEPTION ===");
                Console.Error.WriteLine(ex.ToString());
                var inner = ex.InnerException;
                while (inner != null)
                {
                    Console.Error.WriteLine("--- Inner ---");
                    Console.Error.WriteLine(inner.ToString());
                    inner = inner.InnerException;
                }
                return -2;
            }
            finally
            {
                session.Dispose();
            }
        }

        static int Run(string[] args, TestSession session)
        {
            if(args.Length <3)
            {
                Console.WriteLine("Usage: ILRuntimeTestCLI path patchPath useRegister[true|false] [nameFilter]");
                return -1;
            }

            string path = args[0];
            string patchPath = args[1];
            bool useRegister = args[2].ToLower() == "true";
            string nameFilter = args.Length >= 4 ? args[3] : null;
            session.Load(path, patchPath, useRegister);
#if ENABLE_NEO_MODE
            // Step 22 host-side V1 structural-equivalence self-check.
            if (nameFilter == "NeoStep22SelfCheck")
            {
                int failed;
                try
                {
                    var r = ILRuntime.Runtime.Intepreter.RegisterVM.NeoStep22SelfCheck.Run(session.Appdomain);
                    failed = r.Failed;
                    Console.WriteLine("===============================");
                    Console.WriteLine($"NeoStep22 V1 self-check: {r.Passed}/{r.TotalCells} cells passed, {r.Failed} failed.");
                    foreach (var f in r.Failures)
                        Console.WriteLine($"  FAIL: {f}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("=== NeoStep22SelfCheck threw ===");
                    Console.Error.WriteLine(ex.ToString());
                    failed = -1;
                }
                session.Dispose();
                return failed <= 0 ? 0 : -1;
            }
            // Step 23 host-side V1 roundtrip-equivalence self-check.
            if (nameFilter == "NeoStep23Roundtrip")
            {
                int failed;
                try
                {
                    var r = ILRuntime.Runtime.Intepreter.RegisterVM.NeoStep23RoundtripCheck.Run(session.Appdomain);
                    failed = r.Failed;
                    Console.WriteLine("===============================");
                    Console.WriteLine($"NeoStep23 V1 roundtrip: {r.Passed}/{r.TotalCells} cells passed, {r.Failed} failed.");
                    foreach (var f in r.Failures)
                        Console.WriteLine($"  FAIL: {f}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("=== NeoStep23Roundtrip threw ===");
                    Console.Error.WriteLine(ex.ToString());
                    failed = -1;
                }
                session.Dispose();
                return failed <= 0 ? 0 : -1;
            }
            // Step 24 host-side V1 CLI-roundtrip self-check (drives the SAME
            // NeoCompiler the ilrt_neoc CLI uses).
            if (nameFilter == "NeoStep24CliRoundtrip")
            {
                int failed;
                try
                {
                    var r = ILRuntime.Runtime.Intepreter.RegisterVM.NeoStep24CliRoundtripCheck.Run(session.Appdomain);
                    failed = r.Failed;
                    Console.WriteLine("===============================");
                    Console.WriteLine($"NeoStep24 V1 CLI roundtrip: {r.Passed}/{r.TotalCells} cells passed, {r.Failed} failed.");
                    foreach (var f in r.Failures)
                        Console.WriteLine($"  FAIL: {f}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("=== NeoStep24CliRoundtrip threw ===");
                    Console.Error.WriteLine(ex.ToString());
                    failed = -1;
                }
                session.Dispose();
                return failed <= 0 ? 0 : -1;
            }
            // Step 25 host-side V2 load+execute self-check (deserialize .neo ->
            // NeoAssemblyLoader.Attach -> ExecuteNeo == JIT for a non-generic probe).
            if (nameFilter == "NeoStep25LoadExec")
            {
                int failed;
                try
                {
                    var r = ILRuntime.Runtime.Intepreter.RegisterVM.NeoStep25LoadExecCheck.Run(session.Appdomain);
                    failed = r.Failed;
                    Console.WriteLine("===============================");
                    Console.WriteLine($"NeoStep25 V2 load+exec: {r.Passed}/{r.TotalCells} cells passed, {r.Failed} failed. Attach: {r.AttachedCount} attached, {r.SkippedCount} skipped.");
                    foreach (var f in r.Failures)
                        Console.WriteLine($"  FAIL: {f}");
                    foreach (var s in r.Skipped)
                        Console.WriteLine($"  SKIP: {s}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("=== NeoStep25LoadExec threw ===");
                    Console.Error.WriteLine(ex.ToString());
                    failed = -1;
                }
                session.Dispose();
                return failed <= 0 ? 0 : -1;
            }
            // Step 25 S3-5 host-side self-check: the standalone-AOT-CLI host-CLR-
            // registration gate. Drives the FILE-PATH NeoCompiler.Compile (the
            // Assembly.LoadFrom fix) on TestCases.dll + ILRuntimeTestBase.dll ref,
            // then loads + invokes the CLR-enum probe -> the enum round-trips
            // (proves CLRType, not an ILType shadow).
            if (nameFilter == "NeoStep25S3ClrEnum")
            {
                int failed;
                try
                {
                    // The dedicated probe assembly (NeoClrProbe.dll) is the FOCUSED
                    // input for the host-CLR-registration proof. Locate it relative to
                    // the TestCases.dll path: TestCases.dll lives at
                    // <repo>/TestCases/bin/Debug/netstandard2.1/TestCases.dll, so the
                    // repo root is 4 dirs above its directory; NeoClrProbe.dll is at
                    // <repo>/NeoClrProbe/bin/Debug/netstandard2.1/NeoClrProbe.dll.
                    // ILRuntimeTestBase.dll is a project-ref of TestCases -> copied
                    // next to TestCases.dll in its bin output dir (the host CLR ref).
                    string testCasesDir = System.IO.Path.GetDirectoryName(path);
                    string repoRoot = testCasesDir;
                    for (int i = 0; i < 4 && repoRoot != null; i++)
                        repoRoot = System.IO.Path.GetDirectoryName(repoRoot);
                    string probeDll = System.IO.Path.Combine(
                        repoRoot ?? "", "NeoClrProbe", "bin", "Debug", "netstandard2.1", "NeoClrProbe.dll");
                    string testBaseDll = System.IO.Path.Combine(testCasesDir, "ILRuntimeTestBase.dll");
                    var r = ILRuntime.Runtime.Intepreter.RegisterVM.NeoStep25S3ClrEnumCheck.Run(
                        session.Appdomain, probeDll, testBaseDll);
                    failed = r.Failed;
                    Console.WriteLine("===============================");
                    Console.WriteLine($"NeoStep25 S3-5 CLR-enum: {r.Passed}/{r.TotalCells} cells passed, {r.Failed} failed. Compile: {r.MethodsCompiled} methods / {r.MethodsSkipped} skipped / {r.TemplatesCaptured} templates. Attach: {r.AttachedCount} attached, {r.SkippedCount} skipped.");
                    foreach (var f in r.Failures)
                        Console.WriteLine($"  FAIL: {f}");
                    foreach (var s in r.Skipped)
                        Console.WriteLine($"  SKIP: {s}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("=== NeoStep25S3ClrEnum threw ===");
                    Console.Error.WriteLine(ex.ToString());
                    failed = -1;
                }
                session.Dispose();
                return failed <= 0 ? 0 : -1;
            }
            // Step 26 host-side Neo-vs-Legacy benchmark self-check (the
            // perf-validation capstone; the LAST numbered AOT-chain step).
            // Drives 5 bench workloads via appdomain.Invoke, host-times each
            // with a real Stopwatch, asserts each returned its expected
            // primitive (the correctness-of-measurement divide-assert gate),
            // and emits BENCH:<name>:<iters>:<ticks> lines. The Neo-vs-Legacy
            // RATIO is computed by the separate runner script
            // (scripts/run-neo-bench.{ps1,sh}); the self-check does NOT assert
            // a ratio (this host is not the perf baseline host).
            if (nameFilter == "NeoStep26Bench")
            {
                int failed;
                try
                {
                    var r = ILRuntime.Runtime.Intepreter.RegisterVM.NeoStep26BenchCheck.Run(session.Appdomain);
                    failed = r.Failed;
                    Console.WriteLine("===============================");
                    Console.WriteLine($"NeoStep26 bench: {r.Passed}/{r.TotalCells} cells passed, {r.Failed} failed.");
                    foreach (var f in r.Failures)
                        Console.WriteLine($"  FAIL: {f}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("=== NeoStep26Bench threw ===");
                    Console.Error.WriteLine(ex.ToString());
                    failed = -1;
                }
                session.Dispose();
                return failed <= 0 ? 0 : -1;
            }
#endif
            int ignoreCnt = 0;
            int todoCnt = 0;
            List<TestResultInfo> failedTests = new List<TestResultInfo>();
            int ranCnt = 0;
            foreach(var i in session.TestList)
            {
                if (!string.IsNullOrEmpty(nameFilter) && !i.TestName.Contains(nameFilter))
                    continue;
                ranCnt++;
                try
                {
                    i.Run(true);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"=== Test {i.TestName} threw during Run ===");
                    Console.Error.WriteLine(ex.ToString());
                    var inner = ex.InnerException;
                    while (inner != null)
                    {
                        Console.Error.WriteLine("--- Inner ---");
                        Console.Error.WriteLine(inner.ToString());
                        inner = inner.InnerException;
                    }
                }
                var res = i.CheckResult();
                if (res.Result == ILRuntimeTest.Test.TestResults.Failed)
                {
                    if (res.HasTodo)
                        todoCnt++;
                    else
                        failedTests.Add(res);
                }
                else if (res.Result == ILRuntimeTest.Test.TestResults.Ignored)
                    ignoreCnt++;

                Console.WriteLine(res.Message);
                Console.WriteLine("===============================");
            }
            Console.WriteLine("===============================");
            Console.WriteLine($"{failedTests.Count} tests failed");
            foreach(var i in failedTests)
            {
                Console.WriteLine($"Test name:{i.TestName}, Message:{i.Message}");
                Console.WriteLine("===============================");
            }
            Console.WriteLine($"Ran {ranCnt} tests, {failedTests.Count} failded, {ignoreCnt} ignored, {todoCnt} todos");
            session.Dispose();
            return failedTests.Count <= 0 ? 0 : -1;
        }
    }
}
