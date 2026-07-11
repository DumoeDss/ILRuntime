using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // neo-clr-static-vt-field probes: Stsfld/Ldsfld on a CLR (host) value-type
    // static field whose type has a registered ValueTypeBinder (TestVector3 --
    // 3 floats, blittable). Child-3's NeoClrVtStaticFieldIsUnsafe guard rejected
    // ANY binder VT static with a tagged NIE ("Neo Ldsfld: CLR static value-type
    // field One ... not supported under Neo ..."), because the worker observed a
    // binder-struct AccessViolation and (over-conservatively) refused all binder
    // structs. That rejection is unsound: the Neo flat-byte value-type path
    // (ReadNeoValueType/WriteNeoValueType) does NOT consult the binder at all
    // (it is pure Unsafe.ReadUnaligned/WriteUnaligned; the binder only exposes
    // Legacy StackObject* marshalling -- there is no Neo byte* binder API), so a
    // blittable binder struct has the same flat-byte representation with or
    // without a binder. The real AV guard is the slot-overflow clause (kept). The
    // binder clause is removed; TestVector3.One now reads/writes via the existing
    // flat-byte box-roundtrip behind the guard.
    //
    // Assertion mechanism: same as prior NeoStep tests -- a passing test simply
    // returns without throwing; a logic failure is surfaced by a deliberate
    // `1/0` (DivideByZero fault; the Neo VM cannot yet `new Exception(...)`).
    // Per the child-1/child-2/child-3 FAULT-to-fail discipline each probe MUST
    // fault on the pre-fix guard NIE AND assert the value so a wrong-value bug
    // also fails. Tests are `public static void` parameterless. The class/method
    // names embed "NeoStep" so the NeoStep smoke filter picks them up.

    public class NeoStepClrVtStaticFieldTest
    {
        // TC1 Ldsfld read of a binder CLR value-type static: reads
        // TestVector3.One (ldsfld on a CLR declaring type whose type has a
        // registered ValueTypeBinder -> GetFieldValue(sIdx,null) +
        // WriteNeoValueType). X=Y=Z=1, so SumTestVector3Fields(v, v) =
        // (1+1+1)+(1+1+1) = 6. SumTestVector3Fields is an existing host helper
        // taking TestVector3 BY VALUE (exercises the already-working by-value
        // param path); the load-bearing step under test is the
        // `ldsfld TestVector3.One`. Faults on the pre-fix ldsfld guard NIE; after
        // the fix any wrong value fails the equality -> 1/0.
        public static void NeoStepClrVtStatic_TC1_LdsfldBinderVtRead()
        {
            TestVector3 v = TestVector3.One;
            int s = TestCLRBinding.SumTestVector3Fields(v, v);
            if (s != 6)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC2 Stsfld write + Ldsfld read-back of a binder CLR value-type static:
        // sources src from TestVector3.One (ldsfld), writes it to
        // TestClass3.NeoClrVtStaticProbe (stsfld -- the write step under test),
        // reads it back from the HOST (a plain CLR call isolating a broken WRITE
        // from a broken READ; h==3 proves the stsfld write landed), then reads
        // it back via ldsfld and re-sums with the source via SumTestVector3Fields
        // (s==6 proves BOTH the stsfld and ldsfld binder-struct paths execute).
        // Faults on the pre-fix guard NIE (the ldsfld of TestVector3.One faults
        // first on HEAD); after the fix any wrong value fails the equality -> 1/0.
        public static void NeoStepClrVtStatic_TC2_StsfldLdsfldBinderVtRoundTrip()
        {
            TestVector3 src = TestVector3.One;
            TestClass3.NeoClrVtStaticProbe = src;
            int h = TestCLRBinding.HostReadNeoClrVtStaticProbe();
            TestVector3 rd = TestClass3.NeoClrVtStaticProbe;
            int s = TestCLRBinding.SumTestVector3Fields(src, rd);
            if (h != 3 || s != 6)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
