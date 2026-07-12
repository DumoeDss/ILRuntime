using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // PROBE (neo-byref-array-element-marshal) -- a `ref arr[i]` byref passed to a
    // CLR method. The CIL shape `TestClass3.M(ref arr[i])` lowers to
    // `ldelema <ElementType>; call M(... <ElementType>& ...)`. The ldelema handler
    // stamps the 8-byte byref as (arrIdx, elementIdx) (ILIntepreter.Neo.cs:5797-
    // 5798) where elementIdx is the ELEMENT INDEX. The call routes the byref to
    // NeoMarshalByrefFieldToSlot for BOTH the forward call-arg deref
    // (CopyNeoCallArguments, :424, isWrite=false) AND the post-call write-back
    // (CopyNeoCallThisBack, :653, isWrite=true). On HEAD the helper's `target is
    // Array` branch throws a tagged "Step 13 Area 4c" NotImplementedException, so
    // every `ref arr[i]` CLR call faults. This probe asserts the callee's mutation
    // PERSISTS end-to-end (forward materialize + reflection mutate + write-back),
    // so a wrong/lost value fails via a deliberate 1/0 (DivideByZero).
    //
    // Covers (a) a primitive-element array (ref byteArr[i] / ref intArr[i]) and
    // (b) a VT-element array (ref vectorArr[i] -- box/mutate/unbox). The VT probe
    // calls the helper TWICE on the same element: the second call's returned
    // incoming-sum proves the first call's write-back landed in the array element
    // (reading a struct array element IL-side would need ldelema+ldobj, a separate
    // unimplemented path, so the twice-call pattern isolates the byref marshal).
    //
    // Regression probes for the neo-byref-array-element-marshal fix (the Array
    // branch in NeoMarshalByrefFieldToSlot). All 3 FAULT on HEAD (the Area-4c NIE
    // on the forward deref); PASS after.

    public class NeoStepByrefArrayElementTest
    {
        // TC1 primitive byte: pass ref byteArr[1] to a CLR helper that increments
        // it by 5. Forward deref materializes byteArr[1] (20) into the callee slot;
        // the helper mutates 20 -> 25; the write-back stores 25 via
        // Array.SetValue. A subsequent ldelem.u1 reads 25. On HEAD the forward
        // deref NIEs -> fault. Expected arr[1] == 25.
        public static void NeoStepByrefArrayElement_TC1()
        {
            byte[] arr = new byte[4];
            arr[0] = 10;
            arr[1] = 20;
            arr[2] = 30;
            TestCLRBinding.NeoByrefArrElemIncrementByte(ref arr[1]);
            byte v = arr[1];
            if (v != 25)
            {
                int z = 0; int _ = 1 / z;
            }
        }

        // TC2 primitive int, multi-index: pass ref arr[0] and ref arr[3] to a CLR
        // helper that adds 100. Proves the element-index decode addresses the
        // CORRECT element on both the forward and write-back passes (a wrong decode
        // mutates the wrong element). arr[0]: 1 -> 101; arr[3]: 4 -> 104. Sum = 205.
        public static void NeoStepByrefArrayElement_TC2()
        {
            int[] arr = new int[4];
            arr[0] = 1;
            arr[1] = 2;
            arr[2] = 3;
            arr[3] = 4;
            TestCLRBinding.NeoByrefArrElemIncrementInt(ref arr[0]);
            TestCLRBinding.NeoByrefArrElemIncrementInt(ref arr[3]);
            int s = arr[0] + arr[3];
            if (s != 205)
            {
                int z = 0; int _ = 1 / z;
            }
        }

        // TC3 VT TestVector3: pass ref vectorArr[0] to a CLR helper TWICE. The
        // helper returns the INCOMING X+Y+Z sum, then adds (1000,2000,3000). The
        // array is host-built with (7,70,700). ret1 = 7+70+700 = 777; after the
        // first call the element is (1007,2070,3700) IF the write-back persisted.
        // ret2 = 1007+2070+3700 = 6777 IF the write-back persisted (else 777). The
        // twice-call pattern proves the struct mutation persisted without reading
        // the struct element IL-side (ldelema+ldobj is a separate unimplemented
        // path). Hand-checked: 7+70+700=777; (7+1000)+(70+2000)+(700+3000)=6777.
        public static void NeoStepByrefArrayElement_TC3()
        {
            TestVector3[] arr = TestCLRBinding.BuildNeoByrefVectorArray();
            int ret1 = TestCLRBinding.NeoByrefArrElemMutateVectorAndReturnIncomingSum(ref arr[0]);
            int ret2 = TestCLRBinding.NeoByrefArrElemMutateVectorAndReturnIncomingSum(ref arr[0]);
            if (ret1 != 777 || ret2 != 6777)
            {
                int z = 0; int _ = 1 / z;
            }
        }
    }
}
