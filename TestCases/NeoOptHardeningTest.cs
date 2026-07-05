using System;

namespace TestCases
{
    // Neo optimizer hardening regression tests (change: implement-neo-opt-hardening).
    //
    // K1: Forward Copy Propagation (FCP) must NOT propagate a field read of a
    // value-type-copy destination across a write to a field of the propagation
    // source. After `b = a` (a whole value-type Move), an intervening `a.n = v`
    // reaches the source's storage INDIRECTLY through a `ldloca.s` address
    // handle (`ldloca rAddr, rSrc` then `stfld.*.inline rAddr, ...`). A correct
    // FCP kill must fire when a Ldloca takes the address of the propagation
    // source (or dest), because the local can then be mutated through that
    // address. The prior, mis-diagnosed fix (kill only on a stfld whose
    // Register1 == xSrc/xDst) was a no-op, because the stfld's Register1 is the
    // ldloca temp, not the base local.
    //
    // Assertion mechanism (same as the NeoStep tests): a passing test returns
    // without dividing by zero; a logic failure is surfaced by a deliberate
    // `1/0` (native DivideByZero fault; the Neo VM cannot yet `new Exception`).
    // Tests are `public static void` parameterless.
    //
    // Q-STRUCT and Q-LONG are deferred (not reproducible on HEAD); no probes are
    // shipped here per the change scope.

    // The value type under test: one primitive field. (A reference-field
    // variant would exercise the same ldloca indirection via Stfld_Ref_Inline.)
    public struct OptHardK1Struct
    {
        public int n;
    }

    public class NeoOptHardeningTest
    {
        // K1 regression: copy then mutate source primitive field, read dest field.
        // Expected: b.n == 11 (the value at copy time), NOT 999.
        //
        // On HEAD (before the fix): FCP rewrites the `b.n` read source from b
        // (xDst) to a (xSrc), so it observes the post-mutation 999 -> the test
        // divides by zero -> FAIL.
        // After the fix: the `ldloca.s &a` after the Move kills the propagation,
        // so `b.n` reads b's own snapshot -> returns 11 -> PASS.
        public static void NeoOptHardTest_K1_FcpVtPropagation()
        {
            OptHardK1Struct a = default;
            a.n = 11;
            OptHardK1Struct b = a;   // whole-VT Move; FCP records b <= a
            a.n = 999;               // stfld a.n via ldloca &a
            int got = b.n;           // MUST be 11 (b's independent snapshot)
            if (got != 11)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // K1 variant: mutate the DEST field after the copy, read dest field.
        // Expected: b.n == 42 (the value written through b's own address).
        // Exercises the xDst side of the ldloca kill.
        public static void NeoOptHardTest_K1_MutateDestAfterCopy()
        {
            OptHardK1Struct a = default;
            a.n = 11;
            OptHardK1Struct b = a;   // b <= a (b.n == 11)
            b.n = 42;                // stfld b.n via ldloca &b
            int got = b.n;           // MUST be 42
            if (got != 42)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // K1 variant: copy, mutate source field, read BOTH source and dest.
        // Source read MUST reflect the mutation (999); dest read MUST NOT (11).
        // Guards that the ldloca kill does not also corrupt the source's own
        // field reads (which are NOT FCP-propagated from a Move).
        public static void NeoOptHardTest_K1_SourceAndDestAfterMutation()
        {
            OptHardK1Struct a = default;
            a.n = 11;
            OptHardK1Struct b = a;   // b <= a (b.n == 11, a.n == 11)
            a.n = 999;               // a.n now 999
            int gotA = a.n;          // MUST be 999
            int gotB = b.n;          // MUST be 11
            if (gotA != 999 || gotB != 11)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // ====================================================================
        // F-MAJ-1 probes (change: neo-opt-harden-2). Two+ simultaneously-live
        // CLR struct locals -> silent wrong result. The D6 return-write path
        // writes a struct return's flat bytes (12 for Vector3) into a dest slot
        // AllocateLocalStackSpaces declares as a 4-byte boxed-ref -> 8-byte
        // overflow corrupts the neighbouring local. See change design.md.
        // Prefix `NeoOptHardTest_Fmaj1_` keeps these OUT of the `NeoStep` smoke
        // filter (run under the `NeoOptHardTest_` filter, mirroring K1).
        // ====================================================================

        // (1) Exact reproducer: two CLR struct locals v, w; combined check.
        //     FAILS on HEAD (DivideByZero on the combined check). PASSES after.
        public static void NeoOptHardTest_Fmaj1_TwoClrStructLocals()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(100f, 200f, 300f);
            ILRuntimeTest.TestFramework.TestVector3NoBinding w =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(1f, 1f, 1f);
            int r1 = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(v); // expect 600
            int r2 = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(w); // expect 3
            if (r1 != 600 || r2 != 3)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // (2a) Isolation control: only r1 != 600 check. FAILS on HEAD alone
        //      (proves the corruption is NOT an r1<->r2 cross-clobber).
        public static void NeoOptHardTest_Fmaj1_IsolationR1()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(100f, 200f, 300f);
            ILRuntimeTest.TestFramework.TestVector3NoBinding w =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(1f, 1f, 1f);
            int r1 = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(v); // expect 600
            int r2 = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(w); // expect 3
            if (r1 != 600)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // (2b) Isolation control: only r2 != 3 check. FAILS on HEAD alone.
        public static void NeoOptHardTest_Fmaj1_IsolationR2()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(100f, 200f, 300f);
            ILRuntimeTest.TestFramework.TestVector3NoBinding w =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(1f, 1f, 1f);
            int r1 = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(v); // expect 600
            int r2 = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(w); // expect 3
            if (r2 != 3)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // (3) Three simultaneous CLR struct locals. Stresses the overflow
        //     direction. FAILS on HEAD where the overflow is real.
        public static void NeoOptHardTest_Fmaj1_ThreeStructLocals()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(10f, 20f, 30f);  // 60
            ILRuntimeTest.TestFramework.TestVector3NoBinding w =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(1f, 2f, 3f);     // 6
            ILRuntimeTest.TestFramework.TestVector3NoBinding x =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(100f, 0f, 0f);   // 100
            int r1 = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(v);
            int r2 = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(w);
            int r3 = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(x);
            if (r1 != 60 || r2 != 6 || r3 != 100)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // (4) Live-range overlap across a method call. Construct v, an
        //     intervening method call that touches the frame, construct w, then
        //     Sum(v). The fix must not be order-dependent. FAILS on HEAD.
        public static void NeoOptHardTest_Fmaj1_LiveRangeOverlapAcrossCall()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(100f, 200f, 300f); // 600
            int touch = ILRuntimeTest.TestFramework.TestCLRBinding.TouchFrame(41);  // 42
            ILRuntimeTest.TestFramework.TestVector3NoBinding w =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(1f, 1f, 1f);      // 3
            int r1 = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(v); // 600
            int r2 = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(w); // 3
            if (r1 != 600 || r2 != 3 || touch != 42)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // (5) Scoped reuse / no-frame-bloat guard. Disjoint-scope reuse: v goes
        //     out of scope (last use passed) before w is declared. The fix MUST
        //     NOT balloon the frame (the monotonic allocator never reused v's
        //     slot; this change must not disable that). PASSES throughout.
        public static void NeoOptHardTest_Fmaj1_ScopedReuseNoFrameBloat()
        {
            int r1;
            {
                ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                    ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(100f, 200f, 300f);
                r1 = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(v); // 600
            }
            int r2;
            {
                ILRuntimeTest.TestFramework.TestVector3NoBinding w =
                    ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(1f, 1f, 1f);
                r2 = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(w); // 3
            }
            if (r1 != 600 || r2 != 3)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // (6a) Boundary: 4-byte struct. A 4-byte struct MUST pass on HEAD (no
        //      overflow; the slot is 4 bytes). Regression guard: the fix must
        //      not over-correct a size-4 struct. PASSES throughout.
        public static void NeoOptHardTest_Fmaj1_StructSize4()
        {
            ILRuntimeTest.TestFramework.TestStruct4 v =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestStruct4(600);
            ILRuntimeTest.TestFramework.TestStruct4 w =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestStruct4(3);
            int r1 = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestStruct4(v); // 600
            int r2 = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestStruct4(w); // 3
            if (r1 != 600 || r2 != 3)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // (6b) Boundary: 8-byte struct. Overflows a 4-byte slot by 4 bytes.
        //      FAILS on HEAD where the overflow is real.
        public static void NeoOptHardTest_Fmaj1_StructSize8()
        {
            ILRuntimeTest.TestFramework.TestStruct8 v =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestStruct8(400, 200);  // 600
            ILRuntimeTest.TestFramework.TestStruct8 w =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestStruct8(1, 2);      // 3
            int r1 = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestStruct8(v); // 600
            int r2 = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestStruct8(w); // 3
            if (r1 != 600 || r2 != 3)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // (8) Two CLR int returns control. The documented control: a primitive
        //     return writes 4 bytes into a 4-byte slot -> no overflow -> passes.
        //     MUST pass on HEAD and after (the fix must not regress the primitive
        //     path). PASSES throughout.
        public static void NeoOptHardTest_Fmaj1_TwoClrIntReturns()
        {
            int r1 = ILRuntimeTest.TestFramework.TestCLRBinding.MakeIntA(); // 600
            int r2 = ILRuntimeTest.TestFramework.TestCLRBinding.MakeIntB(); // 3
            if (r1 != 600 || r2 != 3)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
