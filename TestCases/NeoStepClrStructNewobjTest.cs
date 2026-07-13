using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // PROBE (neo-clr-struct-newobj-retdest-null) -- a CLR struct newobj whose ctor has a
    // ValueTypeBinder redirect (`new TestVector3(float,float,float)` -> JIT rewrites Newobj to
    // Call_Redirect with Operand4 bit 0x2) must land the ctor-constructed struct into the caller's
    // dest slot. On HEAD the newobj-originated Call_Redirect path passes retDstPtr to the redirect
    // but the constructed result has nowhere to land correctly -> the dest reads zero -> the host
    // sum is wrong -> fault. The value check is HOST-side (TestCLRBinding.SumTestVector3Fields does
    // the float arithmetic in CLR, sidestepping the conv.i4-float-bit-reinterpret bug).
    public class NeoStepClrStructNewobjTest
    {
        // TC1 direct struct newobj into a local, then host-sum with One.
        // new TestVector3(1,2,3) + One(1,1,1) => (1+2+3)+(1+1+1) = 9. On HEAD (struct zero) => 3 -> fault.
        public static void NeoStepClrStructNewobj_TC1_DirectCtor()
        {
            TestVector3 v = new TestVector3(1f, 2f, 3f);
            int s = TestCLRBinding.SumTestVector3Fields(v, TestVector3.One);
            if (s != 9)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC2 two struct newobj, host-sum (no static One dependency).
        // new(1,2,3) + new(4,5,6) => 1+2+3+4+5+6 = 21. On HEAD => 0 -> fault.
        public static void NeoStepClrStructNewobj_TC2_TwoNewobj()
        {
            TestVector3 a = new TestVector3(1f, 2f, 3f);
            TestVector3 b = new TestVector3(4f, 5f, 6f);
            int s = TestCLRBinding.SumTestVector3Fields(a, b);
            if (s != 21)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC3 newobj-as-value passed directly as a by-value arg to a host method (the
        // `f(new VT(...))` shape -- Roslyn lowers to `call.redirect r4, r4, r5, r6, ctor`
        // with dest=r4, distinct from TC1's into-local `initobj; ldloca; call.ctor`
        // shape). (1+2+3)+(1+1+1) = 9.
        public static void NeoStepClrStructNewobj_TC3_NewobjAsArg()
        {
            int s = TestCLRBinding.SumTestVector3Fields(new TestVector3(1f, 2f, 3f), TestVector3.One);
            if (s != 9)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
