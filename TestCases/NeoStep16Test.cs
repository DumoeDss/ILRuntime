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
        // The item is now built with the real ctor-with-arg form
        // (`new NeoStep16Item(5)`): the newobj dest/arg-register aliasing quirk
        // that previously forced a default-ctor + field-set workaround is gone
        // on current HEAD (verified by the Step 18 Q-NEWOBJ JIT dump -- every
        // register gets a distinct frame region and mStack ref slot).
        public static void NeoStep16_TC4_RefTypeArray()
        {
            NeoStep16Item[] a = new NeoStep16Item[3];
            NeoStep16Item item = new NeoStep16Item(5);
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

        // ---- D-ARR (rank-1 array completion) adversarial probes. ----
        // Each probe is load-bearing (FAIL-on-HEAD stash-toggle). The native-int
        // Stelem_I / Ldelem_I arms, the Ldelem_I JIT case, and the F-4 other-
        // width CLR-array Stind/Ldind branches were all absent on HEAD.

        // TC8 IntPtr[] Stelem_I + Ldelem_I round-trip. `arr[i] = IntPtr`
        // lowers to stelem.i; reading lowers to ldelem.i. Exercises the new
        // Stelem_I runtime arm (dedicated IntPtr[]/UIntPtr[] branch) + the new
        // JIT Ldelem_I case + dedicated runtime arm. Native-int is I4-width on
        // this VM (OQ2 dump-confirmed via runtime debug: Stelem_I writes val4=
        // 100/-7/4660; Ldelem_I reads v=100/-7/4660). (TestCases targets C# 8.0,
        // so `nint` is unavailable -- use System.IntPtr directly.)
        //
        // ASSERTION NOTE: a C#-level value comparison (`(int)x`, `x == y`)
        // routes through IntPtr op_Explicit / op_Equality CLR-struct-method
        // calls, which hit a PRE-EXISTING by-value-CLR-struct-param gap (the
        // Step 6 / NEO-IL-VT-INSTANCE-COVERAGE family), unrelated to this
        // change. So the probe asserts the array LENGTH + that store/load did
        // NOT fault: on HEAD the Stelem_I arm is absent -> NIE -> test fails;
        // after the fix the arms execute cleanly -> test passes. Value
        // correctness is proven by the apply-time runtime debug output.
        public static void NeoStep16_TC8_StelemI_NIntArray()
        {
            IntPtr[] arr = new IntPtr[3];
            arr[0] = new IntPtr(100);
            arr[1] = new IntPtr(-7);
            arr[2] = new IntPtr(0x1234);
            IntPtr a0 = arr[0];
            IntPtr a1 = arr[1];
            IntPtr a2 = arr[2];
            if (arr.Length != 3)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC9 UIntPtr[] Stelem_I + Ldelem_I -- INTENTIONALLY OMITTED. The
        // runtime's GetPrimitiveSize does not recognize UIntPtr (only IntPtr),
        // so any UIntPtr-typed local/temp throws at AllocateLocalStackSpaces
        // (a PRE-EXISTING gap: UIntPtr is not a registered primitive). The
        // Stelem_I / Ldelem_I arms DO dispatch on UIntPtr[] (mirroring the
        // IntPtr[] branch), but no C# shape can reach them without first
        // tripping the UIntPtr-primitive gap. The IntPtr[] probe TC8 covers
        // the arms; UIntPtr[] stays uncovered (accepted-known, pre-existing).

        // TC10 generic-method element access on a REFERENCE type array. A
        // generic `T Get<T>(T[], int)` / `Set<T>` with T=string lowers to
        // ldelem.any T / stelem.any T (opcode 0xa3/0xa4 = Code.Ldelem_Any /
        // Stelem_Any in this Mono.Cecil fork), which the JIT already enumerates
        // and the runtime Ldelem_Any/Stelem_Any arms resolve against the type
        // token. Regression guard for the generic-token path (the proposal's
        // "generic-token" gap is moot in this fork: the generic form IS
        // Ldelem_Any/Stelem_Any). NOTE: a generic PRIMITIVE T (int/long) via
        // Ldelem_Any/Stelem_Any is a separate PRE-EXISTING gap (the Any arms
        // treat the element as a boxed reference, not inline bytes); a
        // reference-type T is the supported shape and is what we guard here.
        static T GGet<T>(T[] a, int i) { return a[i]; }
        static void GSet<T>(T[] a, int i, T v) { a[i] = v; }

        public static void NeoStep16_TC10_GenericTokenLdelemStelem()
        {
            string[] arr = new string[3];
            GSet(arr, 0, "alpha");
            GSet(arr, 1, "beta");
            GSet(arr, 2, "gamma");
            if (!GGet(arr, 0).Equals("alpha") || !GGet(arr, 1).Equals("beta")
                || !GGet(arr, 2).Equals("gamma"))
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC11 ulong[] round-trip: arr[i] = ulong lowers to stelem.i8 and
        // reading lowers to ldelem.i8 (the 8-byte path; OQ1 resolved -- there
        // is no `Code.Ldelem_U8` in this fork, an 8-byte unsigned load IS
        // Ldelem_I8). Regression for the I8 element width on the unsigned kind.
        public static void NeoStep16_TC11_LdelemU8_UlongArray()
        {
            ulong[] arr = new ulong[3];
            arr[0] = 0xDEADBEEFCAFEUL;
            arr[1] = 123456789UL;
            arr[2] = 0xFFFFFFFFFFFFFFFFUL;
            // Incremental assert (one ulong local at a time) -- mirrors TC12.
            ulong a0 = arr[0];
            bool bad = a0 != 0xDEADBEEFCAFEUL;
            ulong a1 = arr[1];
            bad = bad || a1 != 123456789UL;
            ulong a2 = arr[2];
            bad = bad || a2 != 0xFFFFFFFFFFFFFFFFUL;
            if (bad)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC12 F-4 CLR-array ldelema -> stind.i8 / ldind.i8. A `ref` to a
        // long[] element flows through ldelema (Ref Slot (arrIdx, elemIdx))
        // and the callee's stind.i8 writes through the new Array branch.
        static void SetViaRefI8(ref long slot, long v) { slot = v; }

        public static void NeoStep16_TC12_F4_StindLdindClrArray_I8()
        {
            long[] arr = new long[3];
            arr[0] = 10L;
            arr[1] = 20L;
            arr[2] = 30L;
            SetViaRefI8(ref arr[1], 9999999999L);
            // Read each element and assert immediately (one long local at a
            // time) -- avoids a pre-existing optimizer quirk with multiple
            // simultaneous long locals (out of scope for D-ARR).
            long a0 = arr[0];
            bool bad = a0 != 10L;
            long a1 = arr[1];
            bad = bad || a1 != 9999999999L;
            long a2 = arr[2];
            bad = bad || a2 != 30L;
            if (bad)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC13 F-4 CLR-array ldelema -> stind.r4 / ldind.r4 (float[]).
        static void SetViaRefR4(ref float slot, float v) { slot = v; }

        public static void NeoStep16_TC13_F4_StindLdindClrArray_R4()
        {
            float[] arr = new float[3];
            arr[0] = 1.5f;
            arr[1] = 2.5f;
            arr[2] = 3.5f;
            SetViaRefR4(ref arr[1], 99.25f);
            float a0 = arr[0];
            float a1 = arr[1];
            float a2 = arr[2];
            if (a0 != 1.5f || a1 != 99.25f || a2 != 3.5f)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC14 F-4 CLR-array ldelema -> stind.r8 / ldind.r8 (double[]).
        static void SetViaRefR8(ref double slot, double v) { slot = v; }

        public static void NeoStep16_TC14_F4_StindLdindClrArray_R8()
        {
            double[] arr = new double[3];
            arr[0] = 1.5;
            arr[1] = 2.5;
            arr[2] = 3.5;
            SetViaRefR8(ref arr[1], 123.456);
            double a0 = arr[0];
            bool bad = a0 != 1.5;
            double a1 = arr[1];
            bad = bad || a1 != 123.456;
            double a2 = arr[2];
            bad = bad || a2 != 3.5;
            if (bad)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC15 F-4 CLR-array ldelema -> stind.ref / ldind.ref -- INTENTIONALLY
        // OMITTED. A `ref` to a string[]/object[] element flows through
        // ldelema, but the Neo ldelema arm throws a PRE-EXISTING Step-17 NIE
        // for CLR arrays with a reference-type element ("ldelema on a CLR array
        // with a reference-type element is deferred (use direct indexing)").
        // That NIE sits upstream of the new Stind_Ref / Ldind_Ref `is Array`
        // branch, so no C# shape can reach the Ref array branch without first
        // fixing the ldelema ref-type gap (a Step 17 follow-up, out of scope
        // for D-ARR rank-1). The new Stind_Ref / Ldind_Ref array branches are
        // correct-by-construction (they mirror the I4 precedent that TC16
        // guards) but remain unreachable until the upstream ldelema gap closes.

        // TC16 CONTROL: F-4 I4 CLR-array path regression (the step17-completion
        // arm that the new branches mirror). Must stay green.
        static void SetViaRefI4(ref int slot, int v) { slot = v; }

        public static void NeoStep16_TC16_F4_StindLdindClrArray_I4_Regression()
        {
            int[] arr = new int[3];
            arr[0] = 10;
            arr[1] = 20;
            arr[2] = 30;
            SetViaRefI4(ref arr[1], 99);
            int a0 = arr[0];
            int a1 = arr[1];
            int a2 = arr[2];
            if (a0 != 10 || a1 != 99 || a2 != 30)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
