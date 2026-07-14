using System;
using System.Collections.Generic;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // PROBE (neo-delegate-vt-float-return) -- a CLR host invokes an IL delegate
    // Func<TestVector3,float> backing `v => v.X`. The CLR->IL delegate callback
    // (DelegateAdapter.NeoInvokeSub) marshals the TestVector3 param IN and reads
    // the float return OUT. On HEAD the path corrupts the result (DelegateTest24:
    // res = 4E-45 = *(float*)&<int>, a bit-reinterpret).
    //
    // ROOT CAUSE: under the Neo calling convention a CLR value-type PARAMETER is
    // laid out as a BOXED REFERENCE (AllocateSlotForType: Size=4/RefCount=1, an
    // mStack index of the boxed struct -- NOT flat managed bytes, which is how a
    // CLR value-type LOCAL is stored). The `v => v.X` lambda lowers to
    // `ldarg; ldfld X`. The raw-Ldfld IsValueType arm assumed the owner slot held
    // the struct's flat bytes and read them via ReadNeoValueType -- reinterpreting
    // the boxed-ref mStack INDEX as field X (silent corruption). The fix (a JIT
    // marker on `ldarg; ldfld` + a runtime branch that dereferences the boxed
    // struct and reflection-reads the field) mirrors child-24/29's marker pattern.
    //
    // Value checks are HOST-side (the host helper invokes the selector and does
    // all arithmetic / comparison in CLR, sidestepping IL-side ldfld / conv.i4
    // read bugs). A wrong value triggers a deliberate 1/0 (DivideByZero).
    public class NeoStepDelegateVtFloatReturnTest
    {
        // TC1: control -- Func<int,int>. Primitive param + primitive return. MUST
        // PASS on HEAD (isolates the primitive delegate-callback path is sound;
        // the bug is specific to the value-type-param field read).
        public static void NeoStepDvtfr_TC1_ControlPrimPrim()
        {
            int r = TestCLRBinding.HostInvokeIntInt(v => v + 1, 41);
            if (r != 42)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC2: VT param + float return -- THE BUG. A CLR host invokes the IL
        // selector on a single TestVector3; `v => v.X` on (5,6,7) -> 5.0f =
        // 0x40A00000. On HEAD the boxed-ref param's mStack index was read as the
        // float field (bit-reinterpret). MUST FAULT on HEAD.
        public static void NeoStepDvtfr_TC2_VtParamFloatReturn()
        {
            int bits = TestCLRBinding.HostInvokeVTFloatBits(v => v.X, new TestVector3(5f, 6f, 7f));
            if (bits != 0x40A00000)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC3: the list.Sum mirror of DelegateTest24. Host sums `v => v.X` over a
        // 3-element list (1,2,3)/(2,3,4)/(3,4,5) -> 6. MUST FAULT on HEAD.
        public static void NeoStepDvtfr_TC3_ListSumMirror()
        {
            List<TestVector3> list = new List<TestVector3>();
            list.Add(new TestVector3(1f, 2f, 3f));
            list.Add(new TestVector3(2f, 3f, 4f));
            list.Add(new TestVector3(3f, 4f, 5f));
            int ok = TestCLRBinding.HostCheckSelectorSum6(list, v => v.X);
            if (ok == 0)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
