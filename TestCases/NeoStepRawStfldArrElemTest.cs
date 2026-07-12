using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // neo-raw-stfld-array-element probes: raw Stfld whose owner is a CLR-struct
    // ARRAY ELEMENT. The CIL shape `clrStructArray[i].field = x` lowers to
    // `ldelema <StructType>; stfld <field>`. The field's declaring type is a
    // CLRType (NeoArrElemIntProbe), so the Neo typed field-splitter (which only
    // rewrites stfld into the typed arms for an ILType declaring type) leaves the
    // raw OpCodeREnum.Stfld opcode in place. The ldelema handler stamps the 8-byte
    // owner byref as (arrIdx, elementIdx) where the +4 half is the ELEMENT INDEX
    // (not a byte offset) -- the same convention stind/ldind consume. ExecuteNeo's
    // raw Stfld handler previously threw a tagged NotImplementedException
    // ("array-element field write is deferred") for this owner shape; this change
    // replaces it with a box/mutate/unbox (Array.GetValue + FieldInfo.SetValue +
    // Array.SetValue), byte-identical to Legacy's ObjectTypes.ArrayReference
    // writeback.
    //
    // Assertion mechanism: same as prior NeoStep tests -- a passing test simply
    // returns without throwing; a logic failure is surfaced by a deliberate `1/0`
    // (DivideByZero fault; the Neo VM cannot yet `new Exception(...)`). Per the
    // child-1/child-2 FAULT-to-fail discipline each probe MUST fault on HEAD (the
    // tagged array-element NIE on the first element write) AND assert the value so
    // a wrong-value bug also fails. INT fields are deliberate (sidestep the
    // unrelated Neo addi-on-float / conv.i4-float bugs). The read-back is HOST-side
    // (TestCLRBinding.NeoArrElemFieldSum), so the probes do NOT depend on Neo Ldfld
    // (deferred for array-element owners) or Neo float arithmetic.

    public class NeoStepRawStfldArrElemTest
    {
        // TC1 single-element round-trip: writes two int fields to one array
        // element (arr[1].A / arr[1].B) and asserts the host-side sum. Faults on
        // HEAD (arr[1].A = 4242 throws the array-element Stfld NIE); after the fix
        // a wrong value fails the equality -> 1/0.
        public static void NeoStepRawStfldArrElem_TC1()
        {
            NeoArrElemIntProbe[] arr = new NeoArrElemIntProbe[4];
            arr[1].A = 4242;
            arr[1].B = 17;
            int s = TestCLRBinding.NeoArrElemFieldSum(arr, 1);
            if (s != 4259)
            {
                int z = 0; int _ = 1 / z;
            }
        }

        // TC2 multi-index element-index-decode proof: writes A/B at TWO distinct
        // element indices (0 and 5) and sums all four field reads. Proves the
        // (arrIdx, elementIdx) decode addresses the correct element -- a wrong
        // decode (constant-0 or arrIdx-as-elementIdx) would land writes in the
        // wrong element and the host read-back sum would mismatch. Faults on HEAD
        // (arr[0].A = 10 throws the NIE).
        public static void NeoStepRawStfldArrElem_TC2()
        {
            NeoArrElemIntProbe[] arr = new NeoArrElemIntProbe[8];
            arr[0].A = 10; arr[0].B = 20;
            arr[5].A = 30; arr[5].B = 40;
            int s = TestCLRBinding.NeoArrElemFieldSum(arr, 0) + TestCLRBinding.NeoArrElemFieldSum(arr, 5);
            if (s != 100)
            {
                int z = 0; int _ = 1 / z;
            }
        }
    }
}
