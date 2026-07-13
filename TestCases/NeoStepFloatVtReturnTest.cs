using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // PROBE (neo-float-vtreturn-opaddition) -- a CLR method on a type with a registered
    // ValueTypeBinder, taking/returning a binder value type BY VALUE via the AUTOGEN Neo
    // redirect (call.redirect), must marshal the VT params/return via ReadNeoValueType/
    // WriteNeoValueType. The committed TestVector3 *_Neo stubs were STALE (pre-Step-13b):
    // they left VT args as default(...) and omitted the VT return write, so
    //   - `TestVector3.op_Addition` returned (0,0,0) (a/b never read, return never written)
    //   - `TestVector3.op_Multiply` likewise.
    // Root cause: generated binding files predate the (correct) Step-13b generator, AND the
    // generator's emission referenced the internal `Optimizer` class (never compiled in the
    // consumer) -- fixed by a public `ILIntepreter.GetNeoValueTypeManagedSize` facade. The
    // runtime reflection fallback (CLRMethod.Invoke) was always correct -- green smoke used
    // `TestVector3.One` (a host-precomputed static field read via ldsfld, never calling the
    // broken stubs), which is why the bug is context-specific (call.redirect path).
    //
    // The value check is HOST-side (TestCLRBinding.SumTestVector3Fields / SumTestVector3ArrElem
    // do the float arithmetic in CLR, sidestepping the conv.i4-float-bit-reinterpret bug). A
    // wrong value triggers a deliberate 1/0 (DivideByZero). TC1/TC2 FAULT on HEAD (stale stub
    // -> zero -> wrong sum) and PASS after the hand-port. TC3 confirms the child-26 float `+=`
    // shape (which needed the int-struct sidestep) now works with TestVector3 (op_Addition is
    // the link the int sidestep avoided).
    //
    // OUT OF SCOPE (documented follow-up): `new TestVector3(float,float,float)` still yields
    // zero -- the optimizer rewrites a CLR struct newobj to `initobj; ldloca; push(this byref);
    // call.redirect .ctor` with dest=`-` (retDst=null), so the ctor result has nowhere to land
    // (the generator emits a write-to-retDst that no-ops on null). That is a distinct
    // optimizer/redirect contract gap, not the regular-call VT-return path fixed here.
    public class NeoStepFloatVtReturnTest
    {
        // TC1 op_Addition VT-return: One+One -> (2,2,2); Sum(c, One)=(2+2+2)+3=9. (One is
        // host-precomputed; op_Addition uses the parameterless newobj, so no ctor dep --
        // isolates the VT-return + VT-arg marshalling.) On HEAD a/b default + return unwritten
        // -> c=(0,0,0) -> Sum=3 -> fault.
        public static void NeoStepFloatVtReturn_TC1_OpAdditionReturn()
        {
            TestVector3 c = TestVector3.One + TestVector3.One;
            int s = TestCLRBinding.SumTestVector3Fields(c, TestVector3.One);
            if (s != 9)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC2 op_Multiply (VT param + scalar param + VT return): One*5f -> (5,5,5);
        // Sum(c, One)=(5+5+5)+3=18. Exercises the VT-param-read-cursor-realign (a VT param
        // precedes the scalar param) + VT return write. On HEAD -> c=(0,0,0) -> Sum=3 -> fault.
        public static void NeoStepFloatVtReturn_TC2_OpMultiplyReturn()
        {
            TestVector3 c = TestVector3.One * 5f;
            int s = TestCLRBinding.SumTestVector3Fields(c, TestVector3.One);
            if (s != 18)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC3 the child-26 float-`+=` shape (ldelema; ldobj; op_Addition; stobj) with the real
        // TestVector3. Host-built arr[0]=(1,1,1) (avoids the separately-broken stelem.any path);
        // arr[0] += One -> (2,2,2); host element sum=6. This is the shape child-26 had to
        // sidestep with an int struct -- op_Addition (now fixed) was the blocker.
        public static void NeoStepFloatVtReturn_TC3_ArrPlusEqOne()
        {
            TestVector3[] arr = TestCLRBinding.BuildTestVector3OneArray();
            arr[0] += TestVector3.One;
            int s = TestCLRBinding.SumTestVector3ArrElem(arr, 0);
            if (s != 6)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
