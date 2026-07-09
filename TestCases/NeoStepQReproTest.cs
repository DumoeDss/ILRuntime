using System;

namespace TestCases
{
    // =====================================================================
    // Q-STRUCT + Q-LONG REPRODUCER GUARDS (portfolio children 18 + 19).
    //
    // These are NOT new features. They are adversarial REPRODUCERS for two
    // suspected (but long non-reproducible) optimizer / JIT quirks, kept as
    // PERMANENT regression guards per the Q-*/F-* discipline (the only
    // legitimate close of a "non-reproducible on HEAD" bug is a guard that
    // PROVES the shape stays correct).
    //
    // Assertion mechanism: same as the other NeoStep tests -- a logic failure
    // is surfaced by a deliberate `1/0` (native DivideByZero fault). Tests are
    // `public static void` parameterless; names contain `NeoStep` so the
    // `NeoStep` smoke filter catches them.
    // =====================================================================

    // ---- Q-STRUCT: struct-local + field-mutation + element-read temp-renumber ----
    // Suspect site: Optimizer.BCP.cs:97-141 (the BackwardsCopyPropagation
    // ReplaceOpcodeDest rewrite loop). The adversarial shape: a struct local,
    // TWO field mutations via stfld (each consumes an address+value pair,
    // generating Move/copy temps), then an array element READ whose dest temp
    // the BCP pass may renumber to collide with a still-live field/element
    // value, followed by reads of BOTH the mutated struct fields AND the array
    // element. If the BCP renumber corrupts a live temp, one of the three
    // assertions silently sees the wrong value.

    public struct NeoStepQStructS
    {
        public int a;
        public int b;
        public int c;
    }

    public class NeoStepQstructReproTest
    {
        // The core adversarial shape. Mutate all three fields of a struct
        // local, interleave an array element read, then assert the struct
        // fields AND the element are all correct.
        public static void NeoStepQstruct_MutateReadRenum()
        {
            int[] arr = new int[8];
            for (int i = 0; i < arr.Length; i++)
                arr[i] = (i + 1) * 100;

            NeoStepQStructS s;
            s.a = 0; s.b = 0; s.c = 0;      // initobj equivalent
            s.a = 11;
            // read an element between mutations -- the element-read temp is
            // the value BCP is suspected to renumber into a collision.
            int e3 = arr[3];
            s.b = 22;
            int e5 = arr[5];
            s.c = 33;
            int e7 = arr[7];

            if (s.a != 11 || s.b != 22 || s.c != 33)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if (e3 != 400 || e5 != 600 || e7 != 800)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // High-register-pressure variant: many live struct locals + an array,
        // forcing the register allocator / BCP into denser temp reuse. The
        // more live values, the more likely a renumber collision surfaces.
        public static void NeoStepQstruct_HighRegPressure()
        {
            int[] arr = new int[6];
            arr[0] = 7; arr[1] = 14; arr[2] = 21;
            arr[3] = 28; arr[4] = 35; arr[5] = 42;

            NeoStepQStructS s1; s1.a = 1; s1.b = 2; s1.c = 3;
            NeoStepQStructS s2; s2.a = arr[0]; s2.b = arr[1]; s2.c = arr[2];
            NeoStepQStructS s3; s3.a = arr[3]; s3.b = arr[4]; s3.c = arr[5];

            int x0 = arr[0], x1 = arr[1], x2 = arr[2], x3 = arr[3], x4 = arr[4], x5 = arr[5];

            if (s1.a != 1 || s1.b != 2 || s1.c != 3) { int z = 1; int d = 0; int _ = z / d; }
            if (s2.a != 7 || s2.b != 14 || s2.c != 21) { int z = 1; int d = 0; int _ = z / d; }
            if (s3.a != 28 || s3.b != 35 || s3.c != 42) { int z = 1; int d = 0; int _ = z / d; }
            if (x0 != 7 || x1 != 14 || x2 != 21 || x3 != 28 || x4 != 35 || x5 != 42)
            { int z = 1; int d = 0; int _ = z / d; }
        }

        // Element-write (stelem) interleaved with field mutation: the write
        // direction temp may collide symmetrically with the read direction.
        public static void NeoStepQstruct_MutateWriteRenum()
        {
            int[] arr = new int[4];
            NeoStepQStructS s;
            s.a = 100; arr[0] = s.a;
            s.b = 200; arr[1] = s.b;
            s.c = 300; arr[2] = s.c;
            arr[3] = s.a + s.b + s.c;

            if (arr[0] != 100 || arr[1] != 200 || arr[2] != 300 || arr[3] != 600)
            { int z = 1; int d = 0; int _ = z / d; }
            if (s.a != 100 || s.b != 200 || s.c != 300)
            { int z = 1; int d = 0; int _ = z / d; }
        }
    }

    // ---- Q-LONG: long default-zero compare / conv.i8 quirk ----
    // Suspect site: JIT branch type-specialization + AllocateLocalStackSpaces
    // 4-vs-8-byte overlap (a long local is 8 bytes; a mis-sized slot could
    // overlap its neighbour and a conv.i8 / compare reads stale upper bytes).
    // The adversarial shape: a default-zero long, a conv.i8 of an int to long,
    // and a compare (== 0L and != someLong) that would mis-fire if the long
    // slot is only 4 bytes or the conv.i8 / branch type-spec is wrong.

    public class NeoStepQlongReproTest
    {
        // Core: default-zero long == 0L, plus conv.i8 of a small int then ==.
        public static void NeoStepQlong_DefaultZeroCompare()
        {
            long L = default;            // zero
            if (L != 0L)                 // must be false
            { int z = 1; int d = 0; int _ = z / d; }
            if (!(L == 0L))              // must be true
            { int z = 1; int d = 0; int _ = z / d; }

            int i = 5;
            long m = (long)i;            // conv.i8
            if (m != 5L)
            { int z = 1; int d = 0; int _ = z / d; }
            if (m == 0L)                 // conv.i8 of non-zero must NOT equal zero
            { int z = 1; int d = 0; int _ = z / d; }
        }

        // conv.i8 of a NEGATIVE int must sign-extend, and a zero long must
        // compare unequal to it. (Catches a 4-byte-slot upper-half-stale bug:
        // if the upper 4 bytes are garbage, (long)(-1) could read as a huge
        // positive and (long)0 could read as non-zero.)
        public static void NeoStepQlong_ConvI8SignExtend()
        {
            int neg = -1;
            long le = (long)neg;         // conv.i8 -> 0xFFFFFFFFFFFFFFFF
            long L = default;            // 0
            if (le != -1L)
            { int z = 1; int d = 0; int _ = z / d; }
            if (L == le)                 // 0 != -1L
            { int z = 1; int d = 0; int _ = z / d; }
            if (!(L != le))
            { int z = 1; int d = 0; int _ = z / d; }

            int big = 0x7FFFFFFF;        // max int
            long lb = (long)big;         // conv.i8 -> 0x000000007FFFFFFF
            if (lb != 0x7FFFFFFFL)
            { int z = 1; int d = 0; int _ = z / d; }
        }

        // Array-long + scalar-long with register pressure: many live longs so
        // the 4-vs-8-byte overlap (if present) is most likely to surface.
        public static void NeoStepQlong_ArrayScalarPressure()
        {
            long[] arr = new long[4];
            arr[0] = 0L; arr[1] = 1L; arr[2] = -2L; arr[3] = 0x123456789AL;
            long a0 = arr[0], a1 = arr[1], a2 = arr[2], a3 = arr[3];

            long z1 = default, z2 = default, z3 = default;
            long nz = (long)42;

            if (a0 != 0L) { int z = 1; int d = 0; int _ = z / d; }
            if (a1 != 1L) { int z = 1; int d = 0; int _ = z / d; }
            if (a2 != -2L) { int z = 1; int d = 0; int _ = z / d; }
            if (a3 != 0x123456789AL) { int z = 1; int d = 0; int _ = z / d; }
            if (!(z1 == 0L && z2 == 0L && z3 == 0L)) { int z = 1; int d = 0; int _ = z / d; }
            if (nz != 42L) { int z = 1; int d = 0; int _ = z / d; }
            if (nz == 0L) { int z = 1; int d = 0; int _ = z / d; }
        }
    }
}
