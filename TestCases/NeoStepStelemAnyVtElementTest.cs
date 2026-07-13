using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // PROBE (wave2 C10 / neo-array-vt-index-oob) -- the Stelem_Any / Stelem_Ref
    // "CLR object array" arm of ExecuteNeo (ILIntepreter.Neo.cs, case
    // OpCodeREnum.Stelem_Any) wrongly assumed EVERY non-ILTypeInstance[] array is a
    // REFERENCE-element array: it read the value slot's first int as an mStack
    // index (mStack[vIdx]) and Array.SetValue'd that object. But for a CLR
    // VALUE-TYPE-element array (TestVector3[], or float[] reached via
    // `stelem.any !T` on a generic T[]) the value slot holds the struct's FLAT
    // MANAGED BYTES, not an mStack index. The first int is the first field's bit
    // pattern (e.g. 1.0f == 0x3F800000 == 1065353216) -> mStack[1065353216] ->
    // ArgumentOutOfRangeException ("Index was out of range ... collection").
    //
    // The fix discriminates by the runtime element type: a value-type element is
    // read via ReadNeoValueType (flat bytes -> boxed struct) and Array.SetValue'd;
    // a reference element keeps the mStack-index path. This mirrors the Stobj
    // CLR-value-type-array-element WRITE arm (child-26).
    //
    // These probes verify the WRITE via HOST read-back (SumTestVector3ArrayElems
    // reads arr[i].X/Y/Z on the CLR side), so they do NOT depend on Neo Ldelem or
    // Neo float arithmetic. On HEAD both TC1/TC2 FAULT (ArgumentOutOfRange at the
    // stelem.any store); after the fix the host reads back the exact values.
    //
    // Failing full-smoke tests this guards: ArrayTest05, UnitTest_10035,
    // TestValueTypeBinding.Test03, Test03.TestUsingNested (the generic-float
    // `stelem.any !T` shape, covered by TestUsingNested itself).

    public class NeoStepStelemAnyVtElementTest
    {
        // TC1 array-initializer store: `new TestVector3[] { One, One }` lowers to
        // newarr; dup; ldc i; ldsfld One; stelem.any TestVector3 (per element).
        // Two One elements => host sum (1+1+1)+(1+1+1) = 6.
        public static void NeoStepStelemAnyVtElement_TC1()
        {
            TestVector3[] arr = new TestVector3[] { TestVector3.One, TestVector3.One };
            int s = TestCLRBinding.SumTestVector3ArrayElems(arr, 0, 1);
            if (s != 6)
            {
                int z = 0; int _ = 1 / z;
            }
        }

        // TC2 explicit-size + index-assignment store: `arr[i] = One` also lowers to
        // stelem.any TestVector3 for a concrete struct-element array. Writes One at
        // indices 0 and 2 of a 3-element array, host sums elements 0 and 2 => 6.
        public static void NeoStepStelemAnyVtElement_TC2()
        {
            TestVector3[] arr = new TestVector3[3];
            arr[0] = TestVector3.One;
            arr[2] = TestVector3.One;
            int s = TestCLRBinding.SumTestVector3ArrayElems(arr, 0, 2);
            if (s != 6)
            {
                int z = 0; int _ = 1 / z;
            }
        }
    }
}
