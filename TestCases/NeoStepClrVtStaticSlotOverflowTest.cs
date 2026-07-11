using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // neo-clr-static-vt-slot-overflow probes: the TEMP-dest shape of a CLR
    // value-type static load. The slot-overflow guard (NeoClrVtStaticFieldIsUnsafe,
    // the REAL AccessViolation protection kept byte-for-byte here) fires on a
    // genuine 12-into-8 OOB: a CLR struct like TestVector3 (12 bytes) is gathered
    // by GatherValueTypes, but AllocateLocalStackSpaces grew the uniform temp-file
    // `maxSize` (default 8) ONLY for ILType value types -- the gathered CLRType was
    // skipped by the `is ILType` consumer loop, so every eval temp stayed 8 bytes.
    // When `ldsfld TestVector3.One` lowers into a TEMP register (not a named local)
    // the 12-byte write overflows the 8-byte temp and the guard correctly refuses
    // it with the tagged NIE. child-8's TC1/TC2 are direct-to-LOCAL
    // (`TestVector3 v = TestVector3.One` -- a named local IS sized to 12 by the
    // F-MAJ-1 CLR-VT local block) and never covered the temp shape; child-8's
    // hasBinder-clause removal unmasked these ~14 full-smoke hits. The fix
    // (JITCompiler.AllocateLocalStackSpaces) adds an `else if (i is CLRType ct)`
    // arm to the maxSize/maxAlignment loop, sizing the temp to fit the struct --
    // the guard stays as the defense for dests the gather misses.
    //
    // Assertion mechanism: same as prior NeoStep tests -- a passing test simply
    // returns without throwing; a logic failure is surfaced by a deliberate
    // `1/0` (DivideByZero fault; the Neo VM cannot yet `new Exception(...)`).
    // Per the child-1/2/3/8 FAULT-to-fail discipline each probe MUST fault on the
    // pre-fix temp-dest guard NIE AND assert the value so a wrong-value bug also
    // fails. The load-bearing step under test is the `ldsfld TestVector3.One`
    // into a TEMP (fed directly BY VALUE as a call arg with NO intervening named
    // local on the eval path). Tests are `public static void` parameterless; the
    // class/method names embed "NeoStep" so the NeoStep smoke filter picks them up.
    // Assert via the host helper `SumTestVector3Fields` (CLR-side float arithmetic)
    // to sidestep the open `conv.i4`-float-bit-reinterpret gap.

    public class NeoStepClrVtStaticSlotOverflowTest
    {
        // TC1 ldsfld-into-TEMP, by-value call arg (no named local): feeds
        // TestVector3.One directly as BOTH call args. The ldsfld dest is a temp
        // (the value flows straight onto the eval stack into the call frame),
        // NOT a named local -- this is the shape the 14 full-smoke hits take.
        // X=Y=Z=1, so SumTestVector3Fields(One, One) = (1+1+1)+(1+1+1) = 6.
        // Faults on HEAD (the ldsfeld-into-temp slot-overflow guard NIE: temp was
        // sized 8, struct is 12); after the fix the temp is sized to 12 and any
        // wrong value fails the equality -> 1/0.
        public static void NeoStepClrVtStaticSlot_TC1_LdsfeldIntoTempByValueArg()
        {
            int s = TestCLRBinding.SumTestVector3Fields(TestVector3.One, TestVector3.One);
            if (s != 6)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC2 ldsfeld-into-TEMP, mixed with default(TestVector3): the first arg
        // is TestVector3.One (ldsfld into a temp), the second is
        // default(TestVector3) = (0,0,0). SumTestVector3Fields(One, default) =
        // (1+1+1)+(0+0+0) = 3. Distinct expected value from TC1 so a wrong-value
        // fix (e.g. always-6) is caught. Faults on HEAD (same temp-dest guard
        // NIE); after the fix passes with s == 3.
        public static void NeoStepClrVtStaticSlot_TC2_LdsfeldIntoTempByValueArgDefault()
        {
            int s = TestCLRBinding.SumTestVector3Fields(TestVector3.One, default(TestVector3));
            if (s != 3)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
