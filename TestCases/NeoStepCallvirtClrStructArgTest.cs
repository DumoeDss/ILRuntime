using System;
using System.Collections.Generic;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // PROBE (neo-callvirt-clr-struct-arg) -- a `callvirt.clr` to a CLR-generic
    // INSTANCE method taking a value-type arg (List<TestVector3>.Add(item),
    // List<int>.Add) must marshal the struct's flat bytes into the CLR invoke.
    // On HEAD the host CLR method receives a ZERO struct (the struct arg's flat
    // bytes are not correctly projected into the callee param region). The value
    // check is HOST-side (TestCLRBinding.HostSumListTestVector3First / HostListIntFirst
    // do the field reads + arithmetic in CLR, sidestepping any IL-side ldfld /
    // conv.i4 read bug). A wrong value triggers a deliberate 1/0 (DivideByZero).
    //
    // The struct construction (`new TestVector3(1,2,3)` inline in Add) is the
    // as-value ctor shape, which is SEPARATELY working (neo-clr-struct-newobj-
    // retdest-null). This probe isolates the ARG-marshalling path: even with a
    // correctly-constructed struct, List<TestVector3>.Add delivers zero on HEAD.
    public class NeoStepCallvirtClrStructArgTest
    {
        // TC1: list.Add(new TestVector3(1,2,3)); host reads list[0] sum == 6.
        // MUST FAULT on HEAD (host receives zero struct -> sum 0 -> 1/0).
        public static void NeoStepCvClrStructArg_TC1_ListAddVector3()
        {
            List<TestVector3> list = new List<TestVector3>();
            list.Add(new TestVector3(1f, 2f, 3f));
            int s = TestCLRBinding.HostSumListTestVector3First(list);
            if (s != 6)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC2: the List<int>.Add shape (CLR-generic with a primitive arg). A
        // control for the param-map layout: if TC1 faults but TC2 passes, the
        // gap is specific to the value-type-arg layout (not the generic-instance
        // call machinery itself).
        public static void NeoStepCvClrStructArg_TC2_ListAddInt()
        {
            List<int> list = new List<int>();
            list.Add(42);
            int s = TestCLRBinding.HostListIntFirst(list);
            if (s != 42)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC3: multi-add + Sum(v=>v.X) mirror of DelegateTest24 (all-in-host).
        // list.Add((1,2,3)); list.Add((2,3,4)); list.Add((3,4,5)); sum X = 6.
        public static void NeoStepCvClrStructArg_TC3_ListAddThreeSumX()
        {
            List<TestVector3> list = new List<TestVector3>();
            list.Add(new TestVector3(1f, 2f, 3f));
            list.Add(new TestVector3(2f, 3f, 4f));
            list.Add(new TestVector3(3f, 4f, 5f));
            int s = TestCLRBinding.HostSumListTestVector3AllX(list);
            if (s != 6)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
