using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // neo-il-instance-clr-base-field probes: raw Stfld/Ldfld whose field is declared
    // on a CLR base type AND whose owner is an ILTypeInstance (an IL type that inherits
    // the CLR base via a CrossBindingAdaptor). The Neo JIT's typed field-splitter
    // rewrites CIL stfld/ldfld into the typed arms ONLY when the field's declaring type
    // is an ILType; for a CLR declaring type it leaves the raw OpCodeREnum.Stfld/Ldfld
    // opcode with OperandLong = (typeHash<<32)|fieldHash (identical to Legacy's raw
    // encoding). The raw handlers added by child-4 (neo-raw-stfld-ldfld) covered a CLR
    // ref-type owner, a CLR VT-by-value owner (Ldfld), and a CLR VT-byref owner (Stfld),
    // but DEFERRED the IL-instance-owner shape with a tagged NIE. This change closes
    // that gap: the CLR-base field is NOT in the IL instance's Primitives/ManagedObjects
    // layout -- it lives on ILTypeInstance.CLRInstance (the wrapped Adaptor object,
    // IS-A the CLR base), so the read/write routes through the field-hash accessor on
    // CLRInstance (byte-identical to Legacy's ILTypeInstance indexer/AssignFromStack
    // CLR-inherited else branch).
    //
    // Assertion mechanism: same as prior NeoStep tests -- a passing test simply returns
    // without throwing; a logic failure is surfaced by a deliberate `1/0`
    // (DivideByZero fault; the Neo VM cannot yet `new Exception(...)`). Per the
    // child-1/child-2 FAULT-to-fail discipline each probe MUST fault on the current code
    // (the tagged "IL-instance owner with a CLR-base field is deferred" NIE) AND assert
    // the value so a wrong-value bug also fails. Tests are `public static void`
    // parameterless. ClassInheritanceTest is a CLR host type (in ILRuntimeTestBase,
    // registered via ClassInheritanceTestAdaptor) with `public int TestVal2 = 200` and
    // `protected int testVal = 100`, so NeoStepIlClrBaseHolder : ClassInheritanceTest is
    // a legal IL-inherits-CLR declaration (same shape as InheritanceTest.TestCls).

    // An IL type inheriting a CLR base. ReadBase/WriteBase compile to raw
    // ldfld/stfld ClassInheritanceTest::TestVal2 with an IL-instance (this) owner.
    public class NeoStepIlClrBaseHolder : ClassInheritanceTest
    {
        public int ReadBase() { return TestVal2; }      // ldfld CLR-base field (IL this)
        public void WriteBase(int v) { TestVal2 = v; }  // stfld CLR-base field (IL this)
    }

    public class NeoStepIlClrBaseFieldTest
    {
        // TC1 round-trip: newobj the IL-type-with-CLR-base, write a known int to the
        // CLR-base field (stfld IL-owner -> NeoWriteClrObjectField on CLRInstance),
        // read it back (ldfld IL-owner -> NeoReadClrObjectField on CLRInstance), assert.
        // Faults on the current tagged NIE (WriteBase or ReadBase throws); after the fix
        // any wrong value fails the equality -> 1/0.
        public static void NeoStepIlClrBase_TC1_RoundTrip()
        {
            NeoStepIlClrBaseHolder h = new NeoStepIlClrBaseHolder();
            h.WriteBase(4242);
            int v = h.ReadBase();
            if (v != 4242)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC2 read-default: exercise Ldfld independently of Stfld by reading the CLR
        // base's field initializer (TestVal2 == 200). Faults on the current tagged NIE
        // (ReadBase throws); after the fix a wrong default fails the equality -> 1/0.
        public static void NeoStepIlClrBase_TC2_ReadDefault()
        {
            NeoStepIlClrBaseHolder h = new NeoStepIlClrBaseHolder();
            int v = h.ReadBase();
            if (v != 200)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
