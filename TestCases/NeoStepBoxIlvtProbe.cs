using System;

namespace TestCases
{
    // PROBE: IL value-type boxing round-trip INSIDE A GENERIC METHOD context.
    // `T BoxUnbox<T>(T v){ object o = v; return (T)o; }` where T is an IL struct.
    //
    // Context: the non-generic IL-VT box/unbox (NeoStep13's NeoTestIlVtOneRefBoxUnbox
    // / NeoTestIlVtManyRefsBoxUnbox) is GREEN. The generic `BoxUnbox<int>` is GREEN.
    // But `BoxUnbox<IL-VT>` was reported as failing even in a Cecil-loaded JIT run
    // (an engine gap surfaced by the T-identity work). This probe isolates it on the
    // plain JIT path (no Cecil-free machinery) to confirm the engine-level gap and
    // pin the exact failure signature (NRE / wrong-value / NIE).
    //
    // Test entries are `public static` parameterless (the harness convention; only
    // such methods are collected by TestSession.LoadTest). They construct the probe
    // instance + call the generic method so the generic-arg T is an IL struct.
    //
    // Assertion: intentional divide-by-zero on failure (the harness convention; the
    // Neo VM cannot `throw new Exception(...)`). A passing test returns normally.

    // IL value type with one primitive + one reference field (the richest minimal
    // round-trip -- exercises both the byte copy and the ref-slot copy).
    public struct NeoStepBoxIlvtVal
    {
        public int n;
        public string s;
    }

    public class NeoStepBoxIlvtProbe
    {
        // The generic body under test. `object o = v;` is `Box T`; `return (T)o;`
        // is `Unbox.Any T`. Both carry a T-identity type token (T is a method
        // generic param).
        public T BoxUnbox<T>(T v)
        {
            object o = v;
            return (T)o;
        }

        // int T: known-good reference (this passes on HEAD). Guards against a
        // total breakage of BoxUnbox<T>. Returns 4242.
        public static int NeoStepBoxIlvt_Int()
        {
            var p = new NeoStepBoxIlvtProbe();
            return p.BoxUnbox<int>(4242);
        }

        // IL-VT T: the gap. Both n and s must round-trip (s must be non-null).
        // Returns 77 on success; faults (DivideByZero) on any mismatch.
        public static int NeoStepBoxIlvt_Val()
        {
            var p = new NeoStepBoxIlvtProbe();
            NeoStepBoxIlvtVal v = default(NeoStepBoxIlvtVal);
            v.n = 77;
            v.s = "ok";
            NeoStepBoxIlvtVal r = p.BoxUnbox<NeoStepBoxIlvtVal>(v);
            int ok = 1;
            if (r.n != 77)
                ok = 0;
            if (r.s != "ok")
                ok = 0;
            if (ok == 0)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            return r.n;
        }
    }
}
