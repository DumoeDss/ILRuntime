using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // neo-ceq-null-sentinel regression probes (child 12; sibling of child-11
    // NeoStepBrtrueRefTest).
    //
    // The Neo Ceq/Beq/Bne_Un arms compared operands as raw int32s. Under the Neo
    // object model a reference is an mStack INDEX in the slot's primitive bytes,
    // and null is the -1 sentinel (Ldnull / CLR-static Ldsfeld) or a NON-ZERO
    // index to a null mStack entry (an IL-static reference). The raw-int compare
    // therefore mis-handles the null sentinel: for a null IL-static reference
    // (index N) compared to ldnull (-1), N != -1 so Ceq yields 0 -> `x == null`
    // is FALSE even when x IS null -> the `if (x == null) { init }` lazy-init is
    // SKIPPED in its CEQ lowering -> x stays null -> downstream NRE. Actively
    // failing on HEAD: TestStaticFieldInstance, RegisterVMTest04.
    //
    // The fix type-specializes the compare at JIT time: a Ceq/Beq/Bne_Un whose
    // operand register is a reference slot is rewritten to Ceq_Ref/Beq_Ref/
    // Bne_Un_Ref, whose runtime arm resolves each operand to its referenced
    // object (R(v) = v >= 0 ? mStack[v] : null) and compares by C# reference
    // identity (Legacy parity).
    //
    // To force the CEQ lowering (rather than a direct brtrue/beq that Roslyn
    // folds a branch condition into), each comparison is MATERIALIZED as a value
    // -- returned from a helper, or captured into a bool that is consumed as a
    // value -- so Roslyn emits `ld...; ldnull/ld...; ceq; stloc/ret`.
    //
    // Pass = returns normally. TC1 fails on HEAD as a NullReferenceException
    // (init skipped -> field null -> deref NRE); TC2/TC3 fail on HEAD as a
    // deliberate 1/0 div-by-zero (the raw-index compare gives the wrong
    // identity/equality answer).

    public class NeoStepCeqHolder
    {
        public int Value;
        public NeoStepCeqHolder(int v) { Value = v; }
    }

    public class NeoStepCeqNullSentinelTest
    {
        // IL-static reference field, initially null. The lazy-init compares it to
        // null via the CEQ form (materialized as a returned bool). On HEAD the
        // raw-index Ceq mis-compares (index N vs ldnull -1 -> "not equal"), the
        // init is skipped, _inst stays null, and the trailing deref NREs.
        private static NeoStepCeqHolder _inst;

        // Returning the comparison forces the CEQ value lowering
        // (`ldsfld _inst; ldnull; ceq; ret`) -- Roslyn cannot fold a returned
        // value into a branch.
        private static bool IsInstNull()
        {
            return _inst == null;
        }

        // TC1 (the confirmed-live ceq-form null-detection failure shape). On HEAD
        // the raw-index Ceq mis-compares the null IL-static reference's mStack
        // index (N) against ldnull's -1 sentinel -> "not equal" -> `x == null` is
        // FALSE even though x IS null. With the fix, Ceq_Ref resolves R(N)=null /
        // R(-1)=null -> equal -> IsInstNull returns TRUE.
        //
        // This is the ceq form of the canonical lazy-init condition
        // `if (x == null) { init }` (Roslyn emits `ldsfld x; ldnull; ceq; ...`
        // when the comparison is materialized into a value). It checks the null
        // DETECTION directly via the returned bool (the load-bearing ceq.ref
        // result), rather than the full init+deref round-trip: a separate,
        // unrelated static-field-read issue (surfaced downstream in
        // TestStaticFieldInstance past this NRE) makes an IL-static field VALUE
        // read-back unreliable, but that is orthogonal to the null-sentinel
        // compare this child fixes.
        public static void NeoStepCeqNull_TC1_NullStaticRefCeq()
        {
            bool wasNull = IsInstNull();   // _inst is null -> MUST be true
            if (!wasNull) { int x = 1; int y = 0; int _ = x / y; }
            _inst = new NeoStepCeqHolder(1);
            bool stillNull = IsInstNull(); // _inst now non-null -> MUST be false
            if (stillNull) { int x = 1; int y = 0; int _ = x / y; }
        }

        // Returning the two-reference comparison forces the CEQ value lowering
        // (`ldarg x; ldarg y; ceq; ret`).
        private static bool RefSame(NeoStepCeqHolder x, NeoStepCeqHolder y)
        {
            return x == y;
        }

        // TC2 (two-reference identity via ceq). The SAME instance at two locals
        // (a, b=a) MUST compare EQUAL; two DISTINCT instances (a, c) MUST compare
        // NOT EQUAL. Roslyn cannot fold a returned value, so ceq is emitted. On
        // HEAD the raw-index compare gives the wrong identity answer -> 1/0.
        public static void NeoStepCeqNull_TC2_TwoRefIdentity()
        {
            NeoStepCeqHolder a = new NeoStepCeqHolder(5);
            NeoStepCeqHolder b = a;               // SAME instance
            NeoStepCeqHolder c = new NeoStepCeqHolder(5); // DISTINCT instance
            bool sameAB = RefSame(a, b);          // expect true (identity)
            bool sameAC = RefSame(a, c);          // expect false (distinct)
            if (!sameAB) { int x = 1; int y = 0; int _ = x / y; }
            if (sameAC) { int x = 1; int y = 0; int _ = x / y; }
        }

        // TC3 (beq/bne.un branch form on references -- completeness). The direct
        // `if (a == b)` / `if (a != b)` lowering may emit beq/bne.un (exercising
        // Beq_Ref/Bne_Un_Ref) or ceq/cgt.un (exercising Ceq_Ref / the existing
        // Cgt_Un arm); either way the reference-equality result MUST be correct.
        // On HEAD a mis-compare trips the deliberate 1/0.
        public static void NeoStepCeqNull_TC3_BranchFormRefs()
        {
            NeoStepCeqHolder a = new NeoStepCeqHolder(9);
            NeoStepCeqHolder b = a;
            NeoStepCeqHolder c = new NeoStepCeqHolder(9);
            int ok = 0;
            if (a == b) { ok++; }                // same instance -> branch taken
            if (a != c) { ok++; }                // distinct instance -> branch taken
            if (a == c) { ok--; }                // distinct -> branch NOT taken
            if (ok != 2) { int x = 1; int y = 0; int _ = x / y; }
        }
    }
}
