using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // PROBE (child-24 re-audit) -- raw Ldfld whose owner is a CLR-struct ARRAY
    // ELEMENT. The CIL shape `x = clrStructArray[i].field` lowers to
    // `ldelema <StructType>; ldfld <field>`. The field's declaring type is a
    // CLRType (NeoArrElemIntProbe), so the Neo typed field-splitter (which only
    // rewrites ldfld into the typed arms for an ILType declaring type) leaves the
    // raw OpCodeREnum.Ldfld opcode in place. The ldelema handler stamps the 8-byte
    // owner byref as (arrIdx, elementIdx) (ILIntepreter.Neo.cs:5774-5775). The raw
    // Ldfld value-type-owner branch (ILIntepreter.Neo.cs:3890-3902) reads the
    // owner slot as FLAT MANAGED BYTES (ReadNeoValueType) -- for an array element
    // that reinterprets the byref ints (arrIdx, elementIdx) as the struct's fields
    // -> SILENT WRONG VALUE (no crash). This probe asserts the read value so a
    // wrong value fails via a deliberate 1/0 (DivideByZero).
    //
    // The WRITE side (arr[i].A = x) is the green child-19 raw Stfld array-element
    // path, so a round-trip (Stfld write -> Ldfld read) isolates the Ldfld gap: a
    // mismatch proves the READ is wrong (the write is host-confirmed by child-19).
    // Fields are INT (sidestep the unrelated addi-on-float / conv.i4-float bugs).
    //
    // Regression probes for the neo-raw-ldfld-array-element fix (the JIT marker
    // NeoRawLdfldArrayElementByRefMarker + the runtime array-element READ branch
    // in the raw-Ldfld value-type-owner handler). All 3 FAULT on HEAD (pre-fix
    // DivideByZero from the byref-as-struct-bytes reinterpretation); PASS after.

    public class NeoStepRawLdfldArrayElementTest
    {
        // TC1 single-element round-trip: writes A/B to arr[1] via the (green)
        // Stfld array path, then READS arr[1].A / arr[1].B via the raw Ldfld
        // array path (the gap). Asserts the IL-side int sum. On HEAD the read
        // reinterprets the byref (arrIdx, elementIdx) as the struct -> wrong sum
        // -> 1/0 DivideByZero. After the fix the read returns 4242/17 -> 4259.
        public static void NeoStepRawLdfldArrayElement_TC1()
        {
            NeoArrElemIntProbe[] arr = new NeoArrElemIntProbe[4];
            arr[1].A = 4242;
            arr[1].B = 17;
            int a = arr[1].A;
            int b = arr[1].B;
            int s = a + b;
            if (s != 4259)
            {
                int z = 0; int _ = 1 / z;
            }
        }

        // TC2 multi-index element-index-decode proof: writes A/B at TWO distinct
        // element indices (0 and 5) via Stfld, then READS all four fields via
        // Ldfld and sums IL-side. Proves the (arrIdx, elementIdx) decode addresses
        // the correct element on the READ too -- a wrong decode (constant-0 or
        // arrIdx-as-elementIdx) lands reads in the wrong element -> sum mismatch
        // -> 1/0. Expected 10+20+30+40 = 100.
        public static void NeoStepRawLdfldArrayElement_TC2()
        {
            NeoArrElemIntProbe[] arr = new NeoArrElemIntProbe[8];
            arr[0].A = 10; arr[0].B = 20;
            arr[5].A = 30; arr[5].B = 40;
            int s = arr[0].A + arr[0].B + arr[5].A + arr[5].B;
            if (s != 100)
            {
                int z = 0; int _ = 1 / z;
            }
        }

        // TC3 host-built-array read isolation: the array is built and populated
        // on the HOST side (no IL Stfld write), so the probe depends ONLY on the
        // raw Ldfld array-element READ. The four escalating values (7/70/700/7000)
        // are distinct magnitudes -> any wrong element/field decode yields a
        // distinguishable wrong sum -> 1/0. Correct sum = 7+70+700+7000 = 7777.
        public static void NeoStepRawLdfldArrayElement_TC3()
        {
            NeoArrElemIntProbe[] arr = TestCLRBinding.BuildNeoArrElemProbeArray(7, 70, 700, 7000);
            int s = arr[0].A + arr[0].B + arr[1].A + arr[1].B;
            if (s != 7777)
            {
                int z = 0; int _ = 1 / z;
            }
        }
    }
}
