#define F13_PROBE
#if F13_PROBE
using System;
using System.Collections.Generic;
using System.Reflection;

using ILRuntime.CLR.Method;
using ILRuntime.CLR.TypeSystem;
using ILRuntime.Runtime.Enviorment;
using ILRuntime.Runtime.Intepreter;
using ILRuntimeTest.TestFramework;
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
using AutoList = System.Collections.Generic.List<object>;
#else
using AutoList = ILRuntime.Other.UncheckedList<object>;
#endif

namespace ILRuntimeTest.Test
{
    // TEMP F-13 reproduction probe (CLI mode `NeoF13Nested`). Registers a Neo
    // redirect for ILRuntimeTest.TestFramework.NeoF13Bridge.NestedInvoke that
    // NESTS appdomain.Invoke -> Run -> ExecuteNeo while the outer IL method's
    // ExecuteNeo is in flight. Then drives the outer IL method
    // NeoStep14_F13_NestedInvokeProbe via appdomain.Invoke. Forces the F-13
    // re-entrancy corruption on HEAD. Removed before ship.
    public static class NeoF13NestedProbe
    {
        public class Result
        {
            public int TotalCells;
            public int Passed;
            public int Failed;
            public List<string> Failures = new List<string>();
        }

        public static unsafe Result Run(ILRuntime.Runtime.Enviorment.AppDomain appdomain)
        {
            var res = new Result();

            var bridgeType = typeof(NeoF13Bridge);
            const BindingFlags flag = BindingFlags.Public | BindingFlags.Static;
            var mi = bridgeType.GetMethod("NestedInvoke", flag);
            if (mi == null)
            {
                res.TotalCells = 1; res.Failed = 1;
                res.Failures.Add("NeoF13Bridge.NestedInvoke not found via reflection");
                return res;
            }
            appdomain.RegisterCLRMethodRedirectionNeo(mi, NestedInvoke_Neo);

            ILType probeType = null;
            if (!appdomain.LoadedTypes.TryGetValue("TestCases.NeoStep14Test", out var t) || (probeType = t as ILType) == null)
            {
                res.TotalCells = 1; res.Failed = 1;
                res.Failures.Add("TestCases.NeoStep14Test not loaded / not an ILType");
                return res;
            }

            var outer = probeType.GetMethod("NeoStep14_F13_NestedInvokeProbe", 0) as ILMethod;
            var outerCatch = probeType.GetMethod("NeoStep14_F13_NestedInCatchProbe", 0) as ILMethod;
            if (outer == null || outerCatch == null)
            {
                res.TotalCells = 1; res.Failed = 1;
                res.Failures.Add("F13 probe methods not found");
                return res;
            }

            // Cell 1: plain mid-body nested Invoke.
            res.TotalCells++;
            {
                object ret = null;
                try { ret = appdomain.Invoke(outer, null); }
                catch (Exception ex)
                {
                    res.Failed++;
                    res.Failures.Add("Plain Outer Invoke threw " + ex.GetType().Name + ": " + ex.Message);
                    Console.WriteLine("  [FAIL] Plain Outer Invoke threw " + ex.GetType().Name + ": " + ex.Message);
                    Console.WriteLine(ex.ToString());
                    goto cell2;
                }
                if (ret is int v && v == 13)
                {
                    res.Passed++;
                    Console.WriteLine("[NeoF13Nested] Cell1 plain-nested: PASS (13)");
                }
                else
                {
                    res.Failed++;
                    string got = ret == null ? "null" : (ret.GetType().Name + ":" + ret);
                    res.Failures.Add("Cell1 expected 13 got " + got);
                    Console.WriteLine("  [FAIL] Cell1 expected 13 got " + got);
                }
            }

            cell2:
            // Cell 2: the F-4 #3 shape -- nested Invoke INSIDE a catch handler.
            res.TotalCells++;
            {
                object ret = null;
                try { ret = appdomain.Invoke(outerCatch, null); }
                catch (Exception ex)
                {
                    res.Failed++;
                    res.Failures.Add("Catch-Outer Invoke threw " + ex.GetType().Name + ": " + ex.Message);
                    Console.WriteLine("  [FAIL] Catch-Outer Invoke threw " + ex.GetType().Name + ": " + ex.Message);
                    Console.WriteLine(ex.ToString());
                    goto done;
                }
                if (ret is int v && v == 13)
                {
                    res.Passed++;
                    Console.WriteLine("[NeoF13Nested] Cell2 catch-nested: PASS (13; the F-4 #3 shape)");
                }
                else
                {
                    res.Failed++;
                    string got = ret == null ? "null" : (ret.GetType().Name + ":" + ret);
                    res.Failures.Add("Cell2 expected 13 got " + got);
                    Console.WriteLine("  [FAIL] Cell2 expected 13 got " + got);
                }
            }

            done:
            return res;
        }

        static unsafe void NestedInvoke_Neo(ILIntepreter __intp, byte* __frameBase, AutoList __mStack,
            CLRMethod __method, bool isNewObj, byte* __retDst, int __retRefBase)
        {
            var appdomain = __intp.AppDomain;
            // Stress the GC-pin hypothesis: force a full GC + many allocations
            // BEFORE the nested Invoke (which itself allocates a new interpreter +
            // JITs). If the outer's `fixed (OpCodeR* ptr = body)` pin were
            // invalidatable by a GC, this would surface it.
            for (int i = 0; i < 2000; i++) { var _ = new byte[64]; }
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
            int inner = 0;
            if (appdomain.LoadedTypes.TryGetValue("TestCases.NeoStep14Test", out var t) && t is ILType pt)
            {
                var m = pt.GetMethod("F13_Inner", 0) as ILMethod;
                if (m != null)
                {
                    var r = appdomain.Invoke(m, null);
                    if (r is int iv) inner = iv;
                    // 3-LEVEL nesting: the inner F13_Inner is itself driven by
                    // appdomain.Invoke (level 2). Have it re-enter ONE more level
                    // by re-invoking the OUTER probe method recursively? No --
                    // recurse on a DISTINCT trivial method to avoid infinite
                    // recursion; just prove depth-2 nesting survives.
                    var m2 = pt.GetMethod("EchoRef", 0) as ILMethod;
                    if (m2 != null)
                    {
                        var r2 = appdomain.Invoke(m2, null); // depth-2 nested Invoke
                    }
                }
            }
            if (__retDst != null)
                *(int*)__retDst = inner;
        }
    }
}
#endif
