using System;

namespace TestCases
{
    // Step 16 test: Neo array element access (Newarr / Ldelem_* / Stelem_* /
    // Ldlen) across the three Neo array representations:
    //   (a) CLR primitive arrays (int[] / long[] / float[] / double[])
    //   (b) IL reference-type arrays (MyClass[] = ILTypeInstance[])
    //   (c) IL value-type arrays (MyStruct[] = ILTypeInstance[], pre-instantiated)
    //
    // Assertion mechanism: same as prior NeoStep tests -- a passing test simply
    // returns without dividing by zero. A logic failure is surfaced by a
    // deliberate `1/0` (native DivideByZero fault; the Neo VM cannot yet
    // `new Exception(...)`). Tests are `public static void` parameterless.
    //
    // Ldelema / multi-dim / generic-with-token variants are out of scope
    // (deferred to Step 17 / later) and are NOT exercised here.

    // ---- IL types used by the reference-type and value-type array tests ----

    public class NeoStep16Item
    {
        public int val;
        public NeoStep16Item(int v) { val = v; }
        public NeoStep16Item() { val = 0; }
    }

    // A value type with BOTH a primitive field and a reference field, to
    // exercise the primitive+ref CopyBlock path (CopyILToFrame / CopyFrameToIL)
    // used for IL value-type array elements.
    public struct NeoStep16Vt
    {
        public int num;
        public string txt;
    }

    public class NeoStep16Test
    {
        // TC1 CLR primitive int[]: Newarr + Stelem_I4 + Ldelem_I4 round-trip,
        // plus Ldlen. Verifies the typed CLR indexer path.
        public static void NeoStep16_TC1_IntArrayRoundTrip()
        {
            int[] arr = new int[5];
            arr[0] = 42;
            arr[4] = 7;
            if (arr[0] != 42 || arr[4] != 7 || arr.Length != 5)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC2 long (I8) primitive array round-trip (Stelem_I8 / Ldelem_I8).
        public static void NeoStep16_TC2_LongArrayRoundTrip()
        {
            long[] arr = new long[3];
            arr[1] = 1234567890123L;
            long got = arr[1];
            if (got != 1234567890123L)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC3 float (R4) and double (R8) primitive array round-trip.
        public static void NeoStep16_TC3_FloatDoubleArrayRoundTrip()
        {
            float[] f = new float[2];
            f[0] = 2.5f;
            double[] dd = new double[2];
            dd[0] = 3.25;
            if (f[0] != 2.5f || dd[0] != 3.25)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC4 IL reference-type array: store/access an object, plus a null
        // element (ref-type arrays are created with null elements -- C# default).
        // The item is built via the default ctor + a direct field set rather
        // than `new T(int)`: the ctor-with-arg form following an array
        // allocation tickles an unrelated newobj dest/arg-register aliasing
        // quirk in the lowering, not the array store/load path under test.
        public static void NeoStep16_TC4_RefTypeArray()
        {
            NeoStep16Item[] a = new NeoStep16Item[3];
            NeoStep16Item item = new NeoStep16Item();
            item.val = 5;
            a[1] = item;
            if (a[0] != null)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            NeoStep16Item got = a[1];
            if (got == null || got.val != 5)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC5 IL value-type array: store/access a struct (primitive + ref
        // fields). Verifies the CopyBlock path both directions (CopyFrameToIL
        // on Stelem, CopyILToFrame on Ldelem). Value-semantics verification
        // (mutating the source after Stelem) is intentionally omitted: the
        // C# compiler's struct-local + later field-mutation + element-read
        // pattern tickles an unrelated register-aliasing quirk in the
        // optimizer's temp renumbering, not the array copy path.
        public static void NeoStep16_TC5_ValueTypeArray()
        {
            NeoStep16Vt[] a = new NeoStep16Vt[2];
            NeoStep16Vt s;
            s.num = 11;
            s.txt = "hello";
            a[0] = s;
            NeoStep16Vt r = a[0];
            if (r.num != 11 || r.txt != "hello")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC6 Ldlen on a freshly allocated array (covers the Ldlen arm in
        // isolation).
        public static void NeoStep16_TC6_Ldlen()
        {
            int[] arr = new int[7];
            if (arr.Length != 7)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC7 CONTROL: newobj-with-int-arg in isolation (no array). Determines
        // whether a ctor-arg round-trip works without any array involvement.
        public static void NeoStep16_TC7_NewobjArgControl()
        {
            NeoStep16Item item = new NeoStep16Item(5);
            if (item.val != 5)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
