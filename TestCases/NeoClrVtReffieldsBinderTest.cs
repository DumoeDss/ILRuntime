using System;

namespace TestCases
{
    // neo-clr-vt-reffields-binder probes (child-6 of neo-overhaul). The 18 full-
    // smoke "CLR value type with reference fields and no ValueTypeBinder (Step
    // 13b): ... System.RuntimeFieldHandle" hits are ALL the C# array-initializer
    // lowering
    //   newarr; dup; ldtoken <PrivateImplementationDetails blob>;
    //   call RuntimeHelpers.InitializeArray
    // InitializeArray had a Legacy redirect only; Neo dispatch consults
    // RedirectMapNeo exclusively, which had no entry -> the call fell through to
    // the reflection fallback, marshalled the RuntimeFieldHandle param, and hit
    // the Step-13b NIE. A Neo redirect (CLRRedirections.InitializeArrayNeo) now
    // intercepts the call and bulk-copies the initializer blob (surfaced as a
    // byte[] reference by the Neo ldtoken field path) into the array.
    //
    // Assertion discipline (child-1/child-2): a probe must FAIL when the fix is
    // absent. These probes have NO try/catch, so on HEAD the uncaught Step-13b
    // NIE from InitializeArray propagates and the test FAILs. With the fix the
    // array is populated and the per-element contents assertion runs; a wrong
    // copy surfaces as the deliberate 1/0 (DivideByZero) fault -- so the probe is
    // a real correctness check, not a ran-without-throwing check. Tests are
    // `public static void` parameterless; names embed "NeoStep" so they run in
    // the NeoStep smoke (and "ClrVtReffieldsBinder" for a dedicated filter).

    public class NeoStepClrVtReffieldsBinderTest
    {
        // TC1: a large int[] initializer (32 distinct ints = 128 bytes, forcing
        // the compiler to the InitializeArray + ldtoken <field> form rather than
        // 32 individual Stelem stores). Every element must equal its compile-time
        // constant. On HEAD the InitializeArray NIE is uncaught -> FAIL.
        public static void NeoStepClrVtReffieldsBinder_TC1_ArrayInitializer()
        {
            int[] arr = new int[] {
                100, 101, 102, 103, 104, 105, 106, 107,
                108, 109, 110, 111, 112, 113, 114, 115,
                116, 117, 118, 119, 120, 121, 122, 123,
                124, 125, 126, 127, 128, 129, 130, 131 };
            if (arr.Length != 32)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            for (int i = 0; i < 32; i++)
            {
                if (arr[i] != 100 + i)
                {
                    int z = 1; int d = 0; int _ = z / d;
                }
            }
        }

        // TC2: a non-int element type (double[]). The redirect's Marshal.Copy
        // bulk copy must be element-type-agnostic (it copies raw bytes into the
        // pinned CLR array), not limited to int[]. Fractional values prove the
        // bytes were not int-truncated. On HEAD the InitializeArray NIE is
        // uncaught -> FAIL.
        public static void NeoStepClrVtReffieldsBinder_TC2_NonIntArrayInitializer()
        {
            double[] arr = new double[] {
                0.5,  1.5,  2.5,  3.5,  4.5,  5.5,  6.5,  7.5,
                8.5,  9.5, 10.5, 11.5, 12.5, 13.5, 14.5, 15.5,
               16.5, 17.5, 18.5, 19.5, 20.5, 21.5, 22.5, 23.5,
               24.5, 25.5, 26.5, 27.5, 28.5, 29.5, 30.5, 31.5 };
            if (arr.Length != 32)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            for (int i = 0; i < 32; i++)
            {
                if (arr[i] != i + 0.5)
                {
                    int z = 1; int d = 0; int _ = z / d;
                }
            }
        }
    }
}
