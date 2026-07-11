using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // neo-clr-static-fields probes: Stsfld/Ldsfld on a CLR (host) declaring
    // type. The Step-25 S3-4 capstone implemented only the IL-static arms; the
    // CLR-declaring-type `else` branches threw "Neo Stsfld/Ldsfld: CLR static
    // field not implemented". This change fills them: the field is resolved via
    // CLRType.GetField(hash) and read/written through FieldInfo.GetValue(null)/
    // SetValue(null, value), marshalling the frame slot <-> boxed object by the
    // field's CLR System.Type category (primitive / value-type / reference).
    //
    // Assertion mechanism: same as prior NeoStep tests -- a passing test simply
    // returns without throwing; a logic failure is surfaced by a deliberate
    // `1/0` (DivideByZero fault; the Neo VM cannot yet `new Exception(...)`).
    // Per the child-1/child-2 FAULT-to-fail discipline each probe MUST fault on
    // the current code (the Stsfld/Ldsfld CLR-static NIE) AND assert the value
    // so a wrong-value bug also fails. Tests are `public static void`
    // parameterless. TestClass3.NeoClrStaticProbe is a writable host CLR static
    // added for the primitive round-trip; string.Empty / IntPtr.Zero are
    // mscorlib CLR statics (no infra).

    public class NeoStepClrStaticFieldTest
    {
        // TC1 Primitive round-trip: writes a known int to a CLR static
        // (stsfld CLR-primitive -> NeoBoxPrimitiveByType + SetStaticFieldValue)
        // and reads it back (ldsfld CLR-primitive -> GetFieldValue +
        // NeoWritePrimitiveToFrame). `h` is a HOST read (a plain CLR call, no
        // stsfld/ldsfld) so it isolates a broken WRITE from a broken READ: if the
        // field is not 12345 the write (Stsfld) is broken; if it is 12345 but `v`
        // differs the read (Ldsfld) is broken. Faults on the current NIE at the
        // stsfld; after the fix any wrong value fails the equality -> 1/0.
        public static void NeoStepClrStatic_TC1_PrimitiveRoundTrip()
        {
            TestClass3.NeoClrStaticProbe = 12345;
            int h = TestCLRBinding.HostReadNeoClrStaticProbe();
            int v = TestClass3.NeoClrStaticProbe;
            if (h != 12345 || v != 12345)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC2 CLR reference read: reads System.String.Empty (ldsfld on a CLR
        // declaring type -> GetFieldValue + mStack.Add temp-ref). Empty must be
        // a non-null, zero-length string. Faults on the current NIE; after the
        // fix a null (broken) read NREs on .Length and a non-empty read fails
        // the length check -> 1/0.
        public static void NeoStepClrStatic_TC2_StringEmptyRefRead()
        {
            string s = string.Empty;
            if (s == null || s.Length != 0)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC3 CLR value-type read: reads System.IntPtr.Zero (ldsfld on a CLR
        // declaring type -> GetFieldValue + WriteNeoValueType; IntPtr is a
        // simple blittable struct, IsValueType && !IsPrimitive). The result
        // must equal IntPtr.Zero. Faults on the current NIE; after the fix a
        // non-zero (garbage) read fails the comparison -> 1/0.
        public static void NeoStepClrStatic_TC3_IntPtrZeroVtRead()
        {
            IntPtr p = IntPtr.Zero;
            if (p != IntPtr.Zero)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
