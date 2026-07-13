using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // PROBE (neo-raw-ldfld-clr-object-vt-field) -- the READ counterpart of
    // child-27 (which fixed the raw Stfld WRITE on a CLR-struct field of a CLR
    // reference object). The CIL `x = owner.S.field` (owner is a CLR object, S is
    // a CLR-struct field of owner, field is a primitive field of S) lowers to
    // `ldflda S(on owner); ldfld field(on the struct address)`. The ldflda's
    // heap-CLR-object else branch (ILIntepreter.Neo.cs:~1955) produces a byref
    // (objIdx, structFieldHash) -- objIdx parks the containing CLR object,
    // structFieldHash is the S field's FieldInfo hash (NOT a byte offset). The
    // raw Ldfld's DECLARING type is the STRUCT (a CLR value type), so on HEAD it
    // enters the IsValueType arm and takes the flat-bytes else branch:
    // ReadNeoValueType reinterprets the 8-byte byref (objIdx, structFieldHash) as
    // the struct's first two int fields -> SILENT WRONG VALUE (x = objIdx, not the
    // real field). No crash, no NIE -- the same silent-corruption shape as
    // child-24's array-element read.
    //
    // THE FIX (mirrors child-24's marker idiom + child-27's Stfld READ-direction):
    // a JIT marker `NeoRawLdfldClrObjectFieldByRefMarker = 0x2` (bit 0x2 of the
    // raw Ldfld Operand4 -- DISJOINT from child-24's 0x1 array-element marker in
    // the same spare; the two byref-owner shapes are mutually exclusive by CIL
    // predecessor: a raw Ldfld's Previous is EITHER Ldelema OR Ldflda, never
    // both). Stamped in the case Code.Ldfld CLRType else-branch when
    // ins.Previous.OpCode.Code == Code.Ldflda. At runtime, when the marker is set,
    // decode (objIdx, structFieldHash), box the WHOLE struct field via the
    // containing object's CLRType (NeoReadClrObjectField(target, structFieldHash)
    // -> boxedStruct), reflection-read the leaf field (f.GetValue(boxedStruct)),
    // marshal to dest by field category (unchanged). The flat-bytes path moves
    // under a new else; child-24's array-element marker branch stays first.
    //
    // The probe uses NeoClrObjVtFieldProbe (3 int fields) hosted as field S on
    // NeoClrObjVtFieldOwner (a CLR class). Host-setup via BuildNeoClrObjVtFieldOwner
    // writes the struct field VALUES on the CLR side, so the probe exercises ONLY
    // the raw Ldfld READ (no dependency on the sibling Stfld WRITE or Neo float
    // arithmetic). Read-back + assert happen in IL int arithmetic (no float bug).
    // Both probes FAULT on HEAD (silent wrong value -> deliberate 1/0
    // DivideByZero); PASS after. TC2 reads all THREE fields and sums them, proving
    // each field's byref offset resolves to the right FieldInfo (a hash collision
    // or a wrong field would yield a different sum).

    public class NeoStepRawLdfldClrObjVtFieldTest
    {
        // TC1 single-field read. owner.S.a host-set to 111 -> IL reads o.S.a via
        // raw Ldfld -> must be 111. On HEAD it reads objIdx (small int) -> != 111
        // -> 1/0. Hand-checked: a=111.
        public static void NeoStepRawLdfldClrObjVtField_TC1()
        {
            var o = TestCLRBinding.BuildNeoClrObjVtFieldOwner(111, 222, 333);
            int x = o.S.a;
            if (x != 111)
            {
                int z = 0; int _ = 1 / z;
            }
        }

        // TC2 three-field PER-FIELD read. Reads o.S.a, o.S.b, o.S.c via three
        // separate raw Ldfld ops (each `ldflda S; ldfld X`, each with its own leaf
        // field hash) and asserts EACH field's exact value. This proves per-field
        // byref-offset resolution (a hash collision that read the wrong field, or a
        // fix that always read `a`, would fail here -- not just change a sum).
        // On HEAD x=objIdx, y=structFieldHash (a large int), z=frame residue -> at
        // least one field misses -> 1/0. Hand-checked: a=111, b=222, c=333.
        public static void NeoStepRawLdfldClrObjVtField_TC2()
        {
            var o = TestCLRBinding.BuildNeoClrObjVtFieldOwner(111, 222, 333);
            int x = o.S.a;
            int y = o.S.b;
            int z = o.S.c;
            if (x != 111 || y != 222 || z != 333)
            {
                int z0 = 0; int _ = 1 / z0;
            }
        }
    }
}
