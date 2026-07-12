using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // PROBE (neo-ldobj-array-element) -- ldobj / stobj of a CLR value-type ARRAY
    // ELEMENT through a ldelema-produced byref. The CIL compound read-modify-write
    // `arr[i] += <struct>` lowers to `ldelema <T>; ldobj <T>; op_Addition; stobj
    // <T>` (Roslyn emits ldelema+ldobj for the address-taking read-modify-write; a
    // PLAIN `a = arr[i]` instead lowers to ldelem.any, a separate path NOT covered
    // here). The ldobj (load the WHOLE struct element by address) and stobj (store
    // the result back) decode the byref as (arrIdx, elementIdx)
    // (ILIntepreter.Neo.cs:5874-5875) where elementIdx IS the element index. On HEAD
    // both arms' `else` fallback calls GetNeoILInstance, which throws the "Step
    // 17/13b ... Owner type: <T>[]" NotImplementedException.
    //
    // The fix adds an `Array` branch to BOTH the Ldobj (READ) and Stobj (WRITE-back)
    // arms: ldobj does `Array.GetValue(elementIdx)` + WriteNeoValueType; stobj does
    // ReadNeoValueType + `Array.SetValue(boxed, elementIdx)`. Runtime detection
    // (mStack[objIdx] is Array) is safe -- ldobj/stobj's pointer operand is ALWAYS a
    // byref, so there is no flat-bytes-vs-byref ambiguity (unlike child-24 raw-Ldfld).
    //
    // The probe uses NeoArrElemIntProbe (an INT-field CLR struct with an int
    // operator+) -- PURE int arithmetic, avoiding the pre-existing float-constructor
    // / float-arithmetic contamination that makes TestVector3 unsuitable for an
    // isolated ldobj/stobj probe. Read-back is on the HOST side via
    // NeoArrElemFieldSum (passes the ARRAY + index, NOT the element by value, so it
    // does NOT depend on the broken ldelem.any path). Both probes FAULT on HEAD
    // (ldobj NIE); PASS after. A wrong element-index decode or unfaithful flat-byte
    // copy yields a different sum -> deliberate 1/0 (DivideByZero).

    public class NeoStepLdobjArrayElementTest
    {
        // TC1 the faithful read-modify-write trigger (ldobj READ + op_Addition +
        // stobj WRITE-back). Host-built arr[0]=(100,200). arr[0] += One -> (101,201),
        // element sum = 302. Read back on the HOST via NeoArrElemFieldSum(arr, 0).
        // Hand-checked: 101+201 = 302.
        public static void NeoStepLdobjArrayElement_TC1()
        {
            NeoArrElemIntProbe[] arr = TestCLRBinding.BuildNeoArrElemProbeArray(100, 200, 1, 2);
            arr[0] += NeoArrElemIntProbe.One;
            int s = TestCLRBinding.NeoArrElemFieldSum(arr, 0);
            if (s != 302)
            {
                int z = 0; int _ = 1 / z;
            }
        }

        // TC2 element-index decode (ldobj reads + stobj writes the CORRECT element
        // at two distinct indices). arr[0]=(100,200) -> += One -> (101,201) sum 302.
        // arr[1]=(1,2) -> += One -> (2,3) sum 5. Combined = 307. A wrong element-index
        // decode yields a different combined sum. Hand-checked: 101+201=302; 2+3=5;
        // 302+5 = 307.
        public static void NeoStepLdobjArrayElement_TC2()
        {
            NeoArrElemIntProbe[] arr = TestCLRBinding.BuildNeoArrElemProbeArray(100, 200, 1, 2);
            arr[0] += NeoArrElemIntProbe.One;
            arr[1] += NeoArrElemIntProbe.One;
            int s = TestCLRBinding.NeoArrElemFieldSum(arr, 0) + TestCLRBinding.NeoArrElemFieldSum(arr, 1);
            if (s != 307)
            {
                int z = 0; int _ = 1 / z;
            }
        }
    }
}
