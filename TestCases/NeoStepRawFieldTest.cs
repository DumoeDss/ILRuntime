using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // neo-raw-stfld-ldfld probes: raw Stfld/Ldfld whose field is declared on a CLR
    // type. The Neo JIT's typed field-splitter rewrites CIL stfld/ldfld into the
    // typed arms (Stfld_*/Ldfld_*/ldfld.value/stfld.value) ONLY when the field's
    // declaring type is an ILType; for a CLR declaring type it leaves the raw
    // OpCodeREnum.Stfld/Ldfld opcode with OperandLong = (typeHash<<32)|fieldHash
    // (identical to Legacy's raw encoding). ExecuteNeo previously had no case for
    // these, so they hit the Step-6 NotImplementedException default. This change
    // adds the raw Stfld/Ldfld handlers (the owner shape splits by opcode + type
    // kind: a CLR ref-type owner is a boxed mStack object; a CLR value-type owner
    // is inline flat bytes for Ldfld / a frame-native byref for Stfld).
    //
    // Assertion mechanism: same as prior NeoStep tests -- a passing test simply
    // returns without throwing; a logic failure is surfaced by a deliberate
    // `1/0` (DivideByZero fault; the Neo VM cannot yet `new Exception(...)`).
    // Per the child-1/child-2 FAULT-to-fail discipline each probe MUST fault on
    // the current code (the raw Stfld/Ldfld Step-6 NIE) AND assert the value so a
    // wrong-value bug also fails. Tests are `public static void` parameterless.
    // TestClass3.NeoClrInstProbe is a writable host CLR instance int added for the
    // primitive round-trip; TestVector3 (X/Y/Z floats) is the canonical CLR struct.

    public class NeoStepRawFieldTest
    {
        // TC1 CLR reference-type owner, primitive field round-trip: writes a known
        // int to a CLR class instance field (stfld CLR-ref-owner, declaring type =
        // TestClass3, a CLRType -> raw opcode -> NeoWriteClrObjectField) and reads
        // it back (ldfld CLR-ref-owner -> NeoReadClrObjectField). Faults on the
        // current raw-Ldfld/Stfld Step-6 NIE; after the fix any wrong value fails
        // the equality -> 1/0.
        public static void NeoStepRawFld_TC1_ClrRefPrimitiveRoundTrip()
        {
            TestClass3 obj = new TestClass3();
            obj.NeoClrInstProbe = 7777;
            int v = obj.NeoClrInstProbe;
            if (v != 7777)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC2 CLR value-type owner, primitive struct fields: writes X/Y/Z on a CLR
        // struct local (stfld CLR-VT-owner -- the owner is a frame-native byref
        // produced by ldloca; declaring type = TestVector3, a CLRType struct ->
        // raw opcode -> box/mutate/unbox at the byref target) and reads them back
        // (ldfld CLR-VT-owner -- the owner is inline flat bytes from ldloc; box +
        // reflection GetValue). Faults on the current Step-6 NIE; after the fix a
        // wrong field value fails the comparisons -> 1/0.
        public static void NeoStepRawFld_TC2_ClrVtStructFields()
        {
            TestVector3 v = default;
            v.X = 11f;
            v.Y = 22f;
            v.Z = 33f;
            float x = v.X;
            float y = v.Y;
            float z = v.Z;
            if (x != 11f || y != 22f || z != 33f)
            {
                int zz = 1; int d = 0; int _ = zz / d;
            }
        }
    }
}
