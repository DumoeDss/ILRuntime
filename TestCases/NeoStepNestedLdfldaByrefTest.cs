using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // neo-nested-ldflda-byref: regression probes for the INNER ldflda-on-byref
    // gap (`outer.Struct.field += N` -> `ldflda Struct; ldflda field; ldind;
    // add; stind`). On HEAD the inner ldflda treated the outer byref's objIdx as
    // a direct heap index and the following ldind mis-resolved the inner field
    // on the wrong (containing) object -> NRE. After the fix the inner ldflda
    // materializes a self-describing NeoNestedFieldAddr and the ldind/stind arms
    // do box/read + box/mutate/unbox with origin write-back.
    //
    // TC1 = F-10 owner (IL instance, CLR-struct field stored boxed at
    //       ManagedObjects[refOff]); write-back persists to ManagedObjects
    //       (matches Legacy, which prints the persisted value).
    // TC2 = CLR-object owner (TestClass3.Struct); write-back persists via
    //       NeoWriteClrObjectField (EXCEEDS Legacy, which does not back-
    //       propagate a CLR-class struct-field boxed-copy mutation).
    // Both FAULT on HEAD (ldind NRE); PASS after with the asserted values.

    public class NeoNestedLdfldaHolder
    {
        public ILRuntimeTest.TestFramework.TestStruct Struct;
    }

    public class NeoStepNestedLdfldaByrefTest
    {
        public static void NeoStepNestedLdflda_TC1_F10IlInstanceOwner()
        {
            NeoNestedLdfldaHolder h = new NeoNestedLdfldaHolder();
            // Plain Stfld (F-10 path) sets the baseline; the nested += then
            // reads 100, adds 50, writes 150 back through the descriptor.
            h.Struct.value = 100;
            h.Struct.value += 50;
            if (h.Struct.value != 150)
                throw new Exception("NeoStepNestedLdflda TC1 (F-10): expected 150, got " + h.Struct.value);
        }

        public static void NeoStepNestedLdflda_TC2_ClrObjectOwner()
        {
            TestClass3 obj = new TestClass3();
            obj.Struct.value = 100;
            obj.Struct.value += 50;
            if (obj.Struct.value != 150)
                throw new Exception("NeoStepNestedLdflda TC2 (CLR-object): expected 150, got " + obj.Struct.value);
        }

        // TC3: two separate nested += on the same owner to prove field
        // preservation (a zeroing/recreate bug would lose the earlier write).
        public static void NeoStepNestedLdflda_TC3_FieldPreservation()
        {
            NeoNestedLdfldaHolder h = new NeoNestedLdfldaHolder();
            h.Struct.value = 10;
            h.Struct.value += 5;   // 15
            h.Struct.value += 20;  // 35
            if (h.Struct.value != 35)
                throw new Exception("NeoStepNestedLdflda TC3 (preservation): expected 35, got " + h.Struct.value);
        }
    }
}
