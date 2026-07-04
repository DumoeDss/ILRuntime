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
    }
}
