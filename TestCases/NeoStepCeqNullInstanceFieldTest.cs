using System;

namespace TestCases
{
    // neo-ceq-null-instance-field PLANNER RE-AUDIT PROBE (child 23 of neo-overhaul).
    // An INSTANCE reference field that is null, compared via `== null`, reads FALSE
    // on HEAD. The framed root cause: an instance ref field load (ldfld.ref ->
    // Ldfld_Ref) does NOT seed registerTypes[dest] as a reference, so neither
    // Brtrue_Ref (direct form) nor Ceq_Ref (ceq form) fires -- the raw mStack index
    // (a VALID index pointing to null) is compared/tested as an integer. These
    // probes FAULT on HEAD via a deliberate 1/0 (DivideByZero) when the null test
    // returns the wrong answer. Names embed "NeoStep" for the smoke filter.
    //
    // TEMPORARY planner probe -- will be removed before ship (the apply worker will
    // author the permanent probe).

    public class NeoStepCeqNullInstanceFieldData
    {
        public string RefField; // defaults to null
        public int Marker;

        public NeoStepCeqNullInstanceFieldData()
        {
            RefField = null;
            Marker = 42;
        }
    }

    public class NeoStepCeqNullInstanceFieldTest
    {
        // TC1: ternary forces the CEQ materialization.
        //   field is null -> (field == null) must be TRUE -> r = 1.
        //   On HEAD (bug): Ceq/Brtrue mis-reads -> r = 0 -> 1/0 DivideByZero FAULT.
        public static int NeoStepCeqNullInstanceField_TC1_Ternary()
        {
            var d = new NeoStepCeqNullInstanceFieldData();
            int r = (d.RefField == null) ? 1 : 0;
            if (r != 1) { int z = 1; int dd = 0; int _ = z / dd; }
            return r;
        }

        // TC2: materialized bool local, then brtrue on the bool (bool is int 0/1).
        //   `bool b = field == null;` forces ceq -> b. Then `if (b)` is a plain
        //   int brtrue on the 0/1 (NOT a ref brtrue). So this isolates the ceq.
        public static int NeoStepCeqNullInstanceField_TC2_MaterializedBool()
        {
            var d = new NeoStepCeqNullInstanceFieldData();
            bool b = d.RefField == null;
            if (!b) { int z = 1; int dd = 0; int _ = z / dd; }
            return b ? 1 : 0;
        }

        // TC3: direct `if (field == null)` -- Roslyn may lower this to the direct
        //   brtrue form (ldfld.ref; brfalse/brtrue) with NO ceq. This isolates the
        //   Brtrue_Ref path for the instance field. body runs when field IS null.
        public static int NeoStepCeqNullInstanceField_TC3_DirectIf()
        {
            var d = new NeoStepCeqNullInstanceFieldData();
            int r = 0;
            if (d.RefField == null) { r = 1; }
            if (r != 1) { int z = 1; int dd = 0; int _ = z / dd; }
            return r;
        }

        // TC4 (control, non-null): field set to a real string -> == null is FALSE.
        //   Guards against a fix that makes EVERY field read as null.
        public static int NeoStepCeqNullInstanceField_TC4_NonNullControl()
        {
            var d = new NeoStepCeqNullInstanceFieldData();
            d.RefField = "hello";
            int r = (d.RefField == null) ? 1 : 0;
            if (r != 0) { int z = 1; int dd = 0; int _ = z / dd; }
            return r;
        }
    }
}
