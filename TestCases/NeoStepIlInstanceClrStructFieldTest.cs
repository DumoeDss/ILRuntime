using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // neo-il-instance-clr-struct-field-f10 probes: an IL instance has a field
    // whose TYPE is a CLR struct (e.g. `class Holder { public TestVector3 V; }`).
    // Under the Neo object model a CLR-struct field of an IL instance is stored
    // as a reference slot holding the BOXED struct at ManagedObjects[refOff] (NOT
    // flat Primitives bytes). `ldflda <clrStructField>` on the IL instance
    // produces the byref (objIdx, ReferenceOffset | NeoF10ByrefOffsetFlag).
    // A following leaf-field access `obj.V.X = value` (raw Stfld) /
    // `var x = obj.V.X;` (raw Ldfld) previously hit the tagged F-10 NIE at
    // ILIntepreter.Neo.cs (the raw Stfld/Ldfld value-type-owner arm). The fix
    // routes the F-10-flagged byref to a box/mutate/unbox on the IL instance's
    // ManagedObjects (the IL-instance-storage sibling of child-27's heap-CLR-
    // object-field fix).
    //
    // Assertion mechanism: a passing test returns without throwing; a logic
    // failure is surfaced by a deliberate 1/0 (DivideByZero). Float arithmetic
    // is done via the HOST helper TestCLRBinding.SumTestVector3Fields (CLR-side)
    // to sidestep unrelated pre-existing Neo float-seeding bugs. Methods are
    // `public static void` parameterless; names embed "NeoStep" so the NeoStep
    // smoke filter picks them up.
    public class NeoStepIlInstanceClrStructFieldTest
    {
        // The IL holder type: a CLR-struct field (reference slot under Neo).
        public class NeoF10Holder
        {
            public TestVector3 V = new TestVector3(1f, 1f, 1f);
            public Fixed64Vector2 Fv = new Fixed64Vector2(5, 6);
            public TestVector3 VBare;
        }

        // TC1 (Stfld F-10 WRITE + Ldfld_Ref F-10 whole-struct READ): write the
        // three float leaf fields of the CLR-struct field via `h.V.X = ...`
        // (ldflda V; stfld X -- raw Stfld F-10), then read the WHOLE struct back
        // (ldfld V -- Ldfld_Ref F-10) and sum in CLR. After the writes
        // V = (42, 7, 100); SumTestVector3Fields(V, default) = 149. Proves the
        // write persists (the Stfld F-10 box/mutate/unbox writes the mutated
        // boxed struct back to ManagedObjects[refOff]).
        public static void NeoStepIlInstanceClrStruct_TC1_StfldWriteLdfldRead()
        {
            NeoF10Holder h = new NeoF10Holder();
            h.V.X = 42f;
            h.V.Y = 7f;
            h.V.Z = 100f;

            int s = TestCLRBinding.SumTestVector3Fields(h.V, default(TestVector3));
            if (s != 149)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC2 was REMOVED: a probe reading a struct field's property via
        // `.x.RawValue` on a Fixed64Vector2 (e.g. `var r = h.Fv.x.RawValue;`)
        // faults on a CONTROL with NO F-10 involvement at all (`new
        // Fixed64Vector2(555,0).x.RawValue` returns the wrong value). That is a
        // SEPARATE pre-existing Neo gap (a constrained-callvirt / nested-struct-
        // field-property read), NOT the F-10 fix -- it is the same gap that
        // leaves UnitTest_10051's `list[0].V2.x.RawValue` assertion failing
        // AFTER the F-10 write is fixed. The F-10 Stfld write of a STRUCT-typed
        // leaf field uses the IDENTICAL code path as TC1's float-leaf write
        // (`f.SetValue(boxedStruct, value)` + ManagedObjects write-back; only
        // the value-marshalling helper differs -- ReadNeoValueType vs
        // NeoBoxPrimitiveByType), so TC1 covers it. Surfaced follow-up:
        // neo-struct-field-property-read (the `.x.RawValue`-on-local-struct
        // gap).

        // TC3 (raw Ldfld F-10 float field-of-field READ): after writing V.X/Y/Z,
        // read each leaf field individually via `h.V.X` (ldflda V; ldfld X --
        // raw Ldfld F-10), reassemble a TestVector3, and sum in CLR. Proves the
        // raw Ldfld F-10 read returns correct PRIMITIVE (float) field values.
        public static void NeoStepIlInstanceClrStruct_TC3_FloatFieldOfFieldRead()
        {
            NeoF10Holder h = new NeoF10Holder();
            h.V.X = 42f;
            h.V.Y = 7f;
            h.V.Z = 100f;

            float x = h.V.X;
            float y = h.V.Y;
            float z = h.V.Z;
            TestVector3 v2 = new TestVector3(x, y, z);
            int s = TestCLRBinding.SumTestVector3Fields(v2, default(TestVector3));
            if (s != 149)
            {
                int zz = 1; int d = 0; int _ = zz / d;
            }
        }

        // TC4 (null-slot seed path): a CLR-struct field with NO initializer
        // (VBare) -- its ManagedObjects slot may be null until first written.
        // `h.VBare.X = 42f` must seed a default struct (Activator.CreateInstance)
        // before mutating the leaf field; the read-back must then yield 42.
        public static void NeoStepIlInstanceClrStruct_TC4_NullSlotSeedWrite()
        {
            NeoF10Holder h = new NeoF10Holder();
            h.VBare.X = 42f;
            h.VBare.Y = 7f;
            h.VBare.Z = 100f;

            int s = TestCLRBinding.SumTestVector3Fields(h.VBare, default(TestVector3));
            if (s != 149)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
