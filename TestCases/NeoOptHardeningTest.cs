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

        // ====================================================================
        // Review-loop round-1 fixes (neo-opt-harden-2). The F-MAJ-1 declare-side
        // fix (Option B) re-typed a CLR-VT LOCAL as flat bytes (RefCount=0,
        // isRef=false). Three runtime arms -- Initobj / Box / Isinst+Castclass --
        // still assumed the OLD boxed-ref representation and read/wrote a stale
        // mStack index. These probes reproduce each broken arm BEFORE the
        // review-fix and pass AFTER. See review-report.md M1/M2/M3.
        // ====================================================================

        // M1 -- Initobj (default(ClrStruct)) on a CLR struct local. The arm used
        // to write a boxed default into mStack[frameRefBase+RefOffset]; with
        // RefCount=0 the stamped RefOffset is STALE (belongs to a neighbour).
        // To make the corruption OBSERVABLE (not masked by ordering), the probe
        // establishes the canary ref slot FIRST, then re-initializes the struct
        // local via `default(T)` (emits ldloca;initobj AFTER the canary is set),
        // then reads the canary back. Before the fix: initobj's stale-RefOffset
        // write clobbers the canary -> wrong length. After: zero-init of the
        // flat bytes; the canary is untouched.
        public static void NeoOptHardTest_Fmaj1_InitobjClrStruct()
        {
            // Establish the canary neighbour FIRST (mStack ref slot).
            string canary = ILRuntimeTest.TestFramework.TestCLRBinding.MakeCanary();
            // CLR struct local, then RE-init via default(T) -> ldloca;initobj
            // runs AFTER the canary exists, so a stale-RefOffset write would
            // clobber it.
            ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(1f, 2f, 3f);
            v = default(ILRuntimeTest.TestFramework.TestVector3NoBinding);
            int len = ILRuntimeTest.TestFramework.TestCLRBinding.StringLength(canary); // expect 7
            int sum = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(v); // expect 0
            if (len != 7 || sum != 0)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // M2 -- Box of a CLR struct local (`object o = clrStruct;`). The arm
        // used to read a 4-byte mStack index from the flat bytes (garbage) and
        // index mStack with it -> wrong object / OOB. After the fix: box by
        // reading the flat managed bytes.
        public static void NeoOptHardTest_Fmaj1_BoxClrStructLocal()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(100f, 200f, 300f); // 600
            object o = v; // Box of a CLR struct local (flat bytes source).
            int r = ILRuntimeTest.TestFramework.TestCLRBinding.UnboxAndSumVector3NoBinding(o); // expect 600
            if (r != 600)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // M3 -- Isinst / Castclass with a CLR-struct type token. Two source
        // representations must both work: (a) a boxed-ref source (`object` local
        // holding a boxed struct) -- the existing path; (b) a flat-bytes source
        // (a CLR struct local re-typed as object via Box then immediately
        // isinst/cast). Before the fix the flat-bytes source was read as an
        // mStack index -> wrong/OOB. After: the boxed-ref path is preserved and
        // a flat-bytes-clr-vt source is read+boxed for the check.
        public static void NeoOptHardTest_Fmaj1_IsinstClrStructLocal()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(10f, 20f, 30f); // 60
            // Box the struct local into an object-typed local (boxed ref).
            object boxed = (object)v;
            // isinst + castclass on the boxed-ref source.
            int isIt = ILRuntimeTest.TestFramework.TestCLRBinding.IsVector3NoBinding(boxed); // 1
            ILRuntimeTest.TestFramework.TestVector3NoBinding back =
                (ILRuntimeTest.TestFramework.TestVector3NoBinding)boxed; // castclass
            int sum = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(back); // 60
            if (isIt != 1 || sum != 60)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // M-extra -- mixed frame: a CLR struct local + an int local + a string
        // local in the SAME frame. Guards that the Initobj/Box fixes do not
        // cross-corrupt neighbouring flat-bytes / primitive / ref slots.
        public static void NeoOptHardTest_Fmaj1_MixedFrameNoCrossCorruption()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(100f, 200f, 300f); // 600
            int n = 7;
            string s = ILRuntimeTest.TestFramework.TestCLRBinding.MakeCanary(); // len 7
            // 600 + 7 + 7 = 614
            int r = ILRuntimeTest.TestFramework.TestCLRBinding.MixedFrameSum(v, n, s);
            if (r != 614)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // ====================================================================
        // F-8 / NEO-DOUBLE-COMBINE probes (change: neo-double-combine-quirk,
        // [OPT-HARDEN-3]). 2+ `double` locals combined in one boolean expr
        // silently misfire. The dump-CONFIRMED root cause is D4 (NOT the
        // propose-time D2 type-spec candidate -- that was REFUTED by the dump;
        // the Ldelem_R8 dest registers ARE correctly seeded `System.Double`
        // and the emitted opcodes ARE the `*_R8` forms). The defect is a DEAD
        // but DESTRUCTIVE field write in `Optimizer.Neo.cs LowerNeoOffsets`:
        // the immediate-branch case stamped `op.Operand3 =
        // localInfos[r1].RefOffset` for EVERY immediate branch (I4/I8/R4/R8).
        // The `OpCodeR` struct is `[StructLayout(LayoutKind.Explicit)]` --
        // `Operand3` (@16) overlaps the HIGH 4 bytes of `OperandLong`/
        // `OperandDouble` (@12-19); `Operand3` is NEVER READ by any
        // immediate-branch runtime arm (each reads only `DstOffset` + the
        // immediate field + `Operand4`), so the write is dead -- but it
        // clobbers the 8-byte immediate constant for the I8/R8 forms. The
        // `long`-works / `double`-fails split: copy-prop folds a `double`
        // `Ldc_R8` INTO the immediate form (`Bnei_Un_R8`, constant in the
        // opcode, corruption reachable) but keeps `Ldc_I8` in a register
        // (`Bne_Un_I8`, register-register, I8 immediate form never produced ->
        // corruption unreachable). The FIX gates the dead `Operand3` write
        // OFF for the I8/R4/R8 immediate-branch forms (single `immLarge`
        // boolean in `LowerNeoOffsets`); I4 byte-identical (its immediate
        // `Operand` @8 is disjoint from @16). NO JITCompiler.cs change.
        // (Refuted: AllocateLocalStackSpaces slot sizing -- double and long
        // get byte-identical 8-byte slots; long works, only double fails, so
        // the discriminator is the constant-folding, NOT the frame layout.)
        // Prefix `NeoOptHardTest_Dbl_` keeps these OUT of the `NeoStep` smoke
        // filter (run under the `NeoOptHardTest_Dbl_` / `Dbl` filter).
        // ====================================================================

        // Helper: build a double[] element-by-element (array initializer emits
        // Ldtoken which is a Step-6 NIE; use new + indexed store).
        static double[] MakeDoubles(double a, double b, double c)
        {
            double[] arr = new double[3];
            arr[0] = a;
            arr[1] = b;
            arr[2] = c;
            return arr;
        }

        // (1) The exact F-8 reproducer: 2 double locals from a double[],
        // combined in one boolean expr. Expected: both hold their values, so
        // the combined || is FALSE -> no divide-by-zero (PASS). On HEAD the
        // combine reads 4 bytes of each 8-byte slot -> wrong comparison -> the
        // || trips -> DivideByZero (FAIL).
        public static void NeoOptHardTest_Dbl_TwoDoubleCombine()
        {
            double[] arr = MakeDoubles(1.5, 2.5, 0.0);
            double a0 = arr[0]; // 1.5 via Ldelem_R8
            double a1 = arr[1]; // 2.5 via Ldelem_R8
            if (a0 != 1.5 || a1 != 2.5)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // (2) 3 double locals combined.
        public static void NeoOptHardTest_Dbl_ThreeDoubleCombine()
        {
            double[] arr = MakeDoubles(1.5, 2.5, 3.5);
            double a0 = arr[0];
            double a1 = arr[1];
            double a2 = arr[2];
            if (a0 != 1.5 || a1 != 2.5 || a2 != 3.5)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // (3) Boundary control: 3 LONG locals combined. PASSES on HEAD -- the
        // I8 type-spec / compare path is correct (Ldelem_I8 dest IS seeded).
        // Proves the quirk is double-specific.
        public static void NeoOptHardTest_Dbl_ThreeLongCombine()
        {
            long[] arr = new long[3];
            arr[0] = 10L; arr[1] = 20L; arr[2] = 30L;
            long a0 = arr[0];
            long a1 = arr[1];
            long a2 = arr[2];
            if (a0 != 10L || a1 != 20L || a2 != 30L)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // (4) double + long mix in one boolean expr (the boundary).
        public static void NeoOptHardTest_Dbl_DoubleLongMix()
        {
            double[] darr = MakeDoubles(7.25, 0.0, 0.0);
            long[] larr = new long[2];
            larr[0] = 99L; larr[1] = 0L;
            double d0 = darr[0];
            long l0 = larr[0];
            if (d0 != 7.25 || l0 != 99L)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // (5) double + int mix in one boolean expr.
        public static void NeoOptHardTest_Dbl_DoubleIntMix()
        {
            double[] darr = MakeDoubles(4.5, 0.0, 0.0);
            int[] iarr = new int[2];
            iarr[0] = 42; iarr[1] = 0;
            double d0 = darr[0];
            int i0 = iarr[0];
            if (d0 != 4.5 || i0 != 42)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // (6) double locals NOT combined (each in its own if). PASSES on HEAD --
        // a single double read is correct (no combine -> no mis-typed compare).
        // Verify still works after the fix.
        public static void NeoOptHardTest_Dbl_SingleDoubleEach()
        {
            double[] arr = MakeDoubles(1.5, 2.5, 0.0);
            double a0 = arr[0];
            double a1 = arr[1];
            if (a0 != 1.5)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if (a1 != 2.5)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // (7) double local live across an intervening method call, then
        // combined (live range). The call is a host helper returning a known
        // int (does not consume the double locals).
        public static void NeoOptHardTest_Dbl_LiveRangeAcrossCall()
        {
            double[] arr = MakeDoubles(8.75, 9.75, 0.0);
            double a0 = arr[0];
            // Intervening call (host static helper).
            int n = ILRuntimeTest.TestFramework.TestCLRBinding.MixedFrameSum(
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(0f, 0f, 0f), 0, "");
            double a1 = arr[1];
            // n is unused; combine the double locals.
            if (a0 != 8.75 || a1 != 9.75 || n < 0)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // (8) Single double local regression (read + check, no combine).
        public static void NeoOptHardTest_Dbl_SingleDoubleRegression()
        {
            double[] arr = MakeDoubles(3.14, 0.0, 0.0);
            double a0 = arr[0];
            if (a0 != 3.14)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
