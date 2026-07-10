using System;

namespace TestCases
{
    // =====================================================================
    // OR-CHAIN IL-VT STRING-COMPARE REPRODUCER GUARD.
    //
    // This is NOT a new feature. It is an adversarial REPRODUCER for a
    // PRE-EXISTING string-comparison bug surfaced by child 17 (the IL-VT
    // multi-dim array work): a combined `||`-chained multi-string-`!=` on
    // IL value-type struct fields evaluates WRONG (the `||` chain mis-fires
    // to true), even though each INDIVIDUAL `s.field != "x"` is correct.
    //
    // Root cause (see openspec/changes/neo-vt-field-orchain-compare/design.md):
    // the Neo JIT register-allocator assigns the SAME frame region to the
    // `op_Inequality` Call dest (the bool compare result) as it uses for the
    // next field's `ldfld` read in the `||` chain, so the next field read
    // CLOBBERS the compare result the `brtrue` short-circuit branch then
    // consumes -- a compare-result-live-range vs register-reuse bug.
    //
    // Assertion mechanism: same as the other NeoStep tests -- a logic failure
    // is surfaced by a deliberate `1/0` (native DivideByZero fault). Tests are
    // `public static void` parameterless; names contain `NeoStep` so the
    // `NeoStep` smoke filter catches them.
    // =====================================================================

    // A small IL value type with reference (string) fields -- the minimal
    // shape that exposes the ||-chain + IL-VT-field-read interaction.
    public struct NeoStepOrChainVt2
    {
        public string a;
        public string b;
    }

    public struct NeoStepOrChainVt3
    {
        public string a;
        public string b;
        public string c;
    }

    public class NeoStepOrChainTest
    {
        // The core reproducer (isolated from arrays): a 2-field struct, both
        // fields set so the whole `||` chain SHOULD be false. On buggy HEAD
        // the chain mis-fires to true (the second compare result is clobbered
        // by the second field read, or vice versa).
        public static void NeoStepOrChain_TwoFieldsBothFalse()
        {
            NeoStepOrChainVt2 s;
            s.a = "x";
            s.b = "y";
            bool wrong = (s.a != "x" || s.b != "y");
            if (wrong)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // The 3-field variant -- reproduces identically with three compares.
        public static void NeoStepOrChain_ThreeFieldsBothFalse()
        {
            NeoStepOrChainVt3 s;
            s.a = "x";
            s.b = "y";
            s.c = "z";
            bool wrong = (s.a != "x" || s.b != "y" || s.c != "z");
            if (wrong)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // CONTROL: a SINGLE string != on an IL-VT field must be correct.
        // (This guards that the per-field read is right -- the bug is only in
        // the || chain, not the individual compare.)
        public static void NeoStepOrChain_SingleFieldControl()
        {
            NeoStepOrChainVt2 s;
            s.a = "x";
            s.b = "y";
            if (s.a != "x") { int z = 1; int d = 0; int _ = z / d; }
            if (s.b != "y") { int z = 1; int d = 0; int _ = z / d; }
            if (!(s.a == "x")) { int z = 1; int d = 0; int _ = z / d; }
            if (!(s.b == "y")) { int z = 1; int d = 0; int _ = z / d; }
        }

        // CONTROL: the `||` chain with the FIRST compare TRUE must short-
        // circuit to true (the first field is mismatched). Guards that the
        // `||` short-circuit itself still fires when it should.
        public static void NeoStepOrChain_FirstCompareTrue()
        {
            NeoStepOrChainVt2 s;
            s.a = "WRONG";
            s.b = "y";
            bool anyWrong = (s.a != "x" || s.b != "y");
            if (!anyWrong)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // CONTROL: the `||` chain with the SECOND compare TRUE (first matches,
        // second mismatched) must also fire to true. This is the case most
        // likely to expose a clobbered-first-compare bug: if the first
        // (correct) compare result is clobbered before its brtrue, the chain
        // could still reach the second compare -- but the result must be true.
        public static void NeoStepOrChain_SecondCompareTrue()
        {
            NeoStepOrChainVt2 s;
            s.a = "x";
            s.b = "WRONG";
            bool anyWrong = (s.a != "x" || s.b != "y");
            if (!anyWrong)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // The DESIGN-NOTE shape: a `||` chain spanning TWO DISTINCT struct
        // instances (`r0.txt != "a" || r1.txt != "b"`), both SHOULD be false.
        // This mirrors the rank-1 IL-VT multi-cell probe that first exposed
        // the bug (two array-element structs r0, r1). Each struct has a
        // single string field; the chain reads a field from EACH struct.
        public static void NeoStepOrChain_TwoStructsBothFalse()
        {
            NeoStepOrChainVt2 r0;
            r0.a = "a";
            r0.b = "b";
            NeoStepOrChainVt2 r1;
            r1.a = "c";
            r1.b = "d";
            bool wrong = (r0.a != "a" || r1.b != "d");
            if (wrong)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // The design-note shape using TWO structs each with a single string
        // field compared in the chain, three-way (three distinct struct
        // instances) -- the multi-cell rank-1 reproducer's exact shape.
        public static void NeoStepOrChain_ThreeStructsBothFalse()
        {
            NeoStepOrChainVt2 r0; r0.a = "a"; r0.b = "b";
            NeoStepOrChainVt2 r1; r1.a = "c"; r1.b = "d";
            NeoStepOrChainVt2 r2; r2.a = "e"; r2.b = "f";
            bool wrong = (r0.a != "a" || r1.b != "d" || r2.a != "e");
            if (wrong)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // The ORIGINAL failing shape: `r0.txt != "a" || r1.txt != "b"` where
        // r0 and r1 are ARRAY ELEMENTS (a rank-1 IL-VT array). Both SHOULD be
        // false. This is the exact form that first exposed the bug on HEAD
        // (the child-17 TC5b diagnostic). The array-element read path
        // (Ldelem_Ref -> CopyILToFrame into the frame) combined with the
        // || chain is what tickles the register-reuse.
        public static void NeoStepOrChain_Rank1ArrayMultiCell()
        {
            NeoStepOrChainVt3[] a = new NeoStepOrChainVt3[2];
            NeoStepOrChainVt3 s0; s0.a = "a"; s0.b = "b"; s0.c = "c";
            NeoStepOrChainVt3 s1; s1.a = "d"; s1.b = "e"; s1.c = "f";
            a[0] = s0;
            a[1] = s1;
            NeoStepOrChainVt3 r0 = a[0];
            NeoStepOrChainVt3 r1 = a[1];
            bool wrong = (r0.a != "a" || r1.b != "e");
            if (wrong)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // Three-way array multi-cell (the full original shape).
        public static void NeoStepOrChain_Rank1ArrayThreeCell()
        {
            NeoStepOrChainVt3[] a = new NeoStepOrChainVt3[3];
            NeoStepOrChainVt3 s0; s0.a = "a"; s0.b = "b"; s0.c = "c";
            NeoStepOrChainVt3 s1; s1.a = "d"; s1.b = "e"; s1.c = "f";
            NeoStepOrChainVt3 s2; s2.a = "g"; s2.b = "h"; s2.c = "i";
            a[0] = s0; a[1] = s1; a[2] = s2;
            NeoStepOrChainVt3 r0 = a[0];
            NeoStepOrChainVt3 r1 = a[1];
            NeoStepOrChainVt3 r2 = a[2];
            bool wrong = (r0.a != "a" || r1.b != "e" || r2.c != "i");
            if (wrong)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // CONTROL for the array case: a SINGLE r0.a != "a" (the first compare
        // in the chain) on the SAME array-element struct MUST be correct.
        // (Proves the per-field read is right -- the bug is only the chain.)
        public static void NeoStepOrChain_Rank1ArraySingleControl()
        {
            NeoStepOrChainVt3[] a = new NeoStepOrChainVt3[2];
            NeoStepOrChainVt3 s0; s0.a = "a"; s0.b = "b"; s0.c = "c";
            a[0] = s0;
            NeoStepOrChainVt3 r0 = a[0];
            bool w1 = (r0.a != "a");
            if (w1) { int z = 1; int d = 0; int _ = z / d; }
            NeoStepOrChainVt3 s1; s1.a = "d"; s1.b = "e"; s1.c = "f";
            a[1] = s1;
            NeoStepOrChainVt3 r1 = a[1];
            bool w2 = (r1.b != "e");
            if (w2) { int z = 1; int d = 0; int _ = z / d; }
        }
    }
}
