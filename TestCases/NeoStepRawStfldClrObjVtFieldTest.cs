using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // PROBE (neo-raw-stfld-clr-object-vt-field) -- a raw Stfld whose owner is a
    // byref into a CLR-struct FIELD of a CLR REFERENCE object. The CIL
    // `owner.S.field = x` (owner is a CLR object, S is a CLR-struct field of
    // owner, field is a primitive field of S) lowers to `ldflda S(on owner);
    // stfld field(on the struct address)`. The ldflda produces a byref
    // (objIdx, structFieldHash) -- objIdx parks the containing CLR object,
    // structFieldHash is the S field's FieldInfo hash (NOT a byte offset; the
    // ldflda heap-CLR-object else branch ILIntepreter.Neo.cs:~1955, where for a
    // CLR object fieldPrimOff IS the FieldInfo hash). On HEAD the raw Stfld
    // VT-owner arm hits the "unrecognized CLR value-type owner byref shape" NIE
    // (only objIdx==-1 frame-local and Array element were handled).
    //
    // The fix adds a third branch: box/mutate/unbox ONE LEVEL UP via the
    // containing object's CLRType -- read the struct field (NeoReadClrObjectField
    // with the structFieldHash), reflection-write the leaf field, write the
    // mutated struct back (NeoWriteClrObjectField). Mirrors child-19's array-
    // element box/mutate/unbox, replacing "array element" with "CLR-object
    // field." No JIT marker (a value-type-owner Stfld's owner is ALWAYS a byref,
    // never flat bytes -- runtime content detection is safe; contrast child-24
    // which needed a marker for the READ-side flat-bytes-vs-byref ambiguity).
    //
    // The probe uses NeoClrObjVtFieldProbe (a 3-int-field CLR struct) hosted as
    // field S on NeoClrObjVtFieldOwner (a CLR class). Read-back is on the HOST
    // side via NeoClrObjVtFieldProbeSum (CLR reflection), sidestepping the
    // deferred Neo raw-Ldfld read sibling. TC2 sets all THREE fields separately,
    // proving the box/mutate/unbox preserves the other fields (a fix that
    // recreated the struct from default each time would lose the earlier writes
    // and yield only the last). Both probes FAULT on HEAD (Stfld NIE at the
    // first assignment); PASS after. A wrong value yields a different sum ->
    // deliberate 1/0 (DivideByZero), matching the child-24/25/26 probe idiom.

    public class NeoStepRawStfldClrObjVtFieldTest
    {
        // TC1 single-field write persists. owner.S.a = 111 -> host sum = 111
        // (a=111, b=0, c=0). Hand-checked: 111+0+0 = 111.
        public static void NeoStepRawStfldClrObjVtField_TC1()
        {
            var o = new NeoClrObjVtFieldOwner();
            o.S.a = 111;
            int s = TestCLRBinding.NeoClrObjVtFieldProbeSum(o);
            if (s != 111)
            {
                int z = 0; int _ = 1 / z;
            }
        }

        // TC2 multi-field preservation. Three separate raw Stfld writes, each a
        // box/mutate/unbox on the containing object's struct field. After all
        // three: a=111, b=222, c=333 -> sum = 666. A fix that recreated the
        // struct from default each time (instead of reading the current struct)
        // would yield only the LAST write (c=333, a=b=0) -> sum 333. Hand-checked:
        // 111+222+333 = 666.
        public static void NeoStepRawStfldClrObjVtField_TC2()
        {
            var o = new NeoClrObjVtFieldOwner();
            o.S.a = 111;
            o.S.b = 222;
            o.S.c = 333;
            int s = TestCLRBinding.NeoClrObjVtFieldProbeSum(o);
            if (s != 666)
            {
                int z = 0; int _ = 1 / z;
            }
        }
    }
}
