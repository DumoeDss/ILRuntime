using System;

namespace TestCases
{
    // ===== Step 25 neo-aot-multi-hotfix: cross-assembly IL-to-IL type refs =====
    //
    // Simulates a MULTI-hotfix-assembly setup: an IL type in "AssemblyA"
    // references a TYPE in "AssemblyB" (both IL hotfix assemblies). The AOT
    // compile partitions them into TWO .neo models (one per "assembly"); the
    // Cecil-free load loads BOTH into a fresh AppDomain, and the references
    // between them must resolve by NAME across the two loads.
    //
    // NeoStep25MultiHotfixA.ClassA (the referencing type):
    //   - a FIELD of type NeoStep25MultiHotfixB.ClassB (field-type cross-ref)
    //   - a METHOD PARAM of type ClassB (param-type cross-ref)
    //   - a CALL to a ClassB method (method-ref cross-ref)
    //   - isinst / castclass on ClassB (type-test cross-ref)
    //
    // Both types are top-level + non-generic + BCL-refs-only + instance methods
    // only (no statics, no .cctor) so they stay within the shipped Cecil-free
    // scope (S1-S4 + clrbase-iface). The arithmetic results are known constants
    // so the capstone asserts exactly.

    // "AssemblyB" -- the REFERENCED type. Compiled to its own .neo model.
    public class NeoStep25MultiHotfixB
    {
        public int BValue;

        public NeoStep25MultiHotfixB(int v) { BValue = v; }

        public int BEcho(int x) { return x + BValue + BMagic(); }

        // A constant in B's body (the M1 mutation target -- proves the Cecil-free
        // execution runs the genuine .neo-B body, not a Cecil fallback).
        public int BMagic() { return 1000; }
    }

    // "AssemblyA" -- the REFERENCING type. Compiled to its own .neo model.
    // Every member references the B type (field, param, call, type-test).
    public class NeoStep25MultiHotfixA
    {
        public NeoStep25MultiHotfixB BField;   // field of type B (cross-assembly)

        public void Setup(NeoStep25MultiHotfixB b)  // param of type B
        {
            BField = b;
        }

        // field read (BField) + CALL into B (BEcho) + isinst + castclass, all
        // cross-assembly. Result is a known constant so the capstone asserts
        // exactly. Marks whether each cross-ref resolved.
        public int ACompute(int seed)
        {
            int sum = seed;
            // field read of the cross-assembly B field + a CALL into B
            if (BField != null) sum += BField.BEcho(seed);
            // isinst on the cross-assembly type
            object o = BField;
            if (o is NeoStep25MultiHotfixB) sum += 1;
            // castclass on the cross-assembly type
            var cast = (NeoStep25MultiHotfixB)o;
            sum += cast.BValue;
            return sum;
            // with seed=5 + b=BValue=10: 5 + (5+10)=15 +1 +10 = 41
        }
    }
}
