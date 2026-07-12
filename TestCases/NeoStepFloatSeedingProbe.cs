using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // RE-AUDIT PROBE for neo-raw-ldfld-stfld-clr-struct-seeding (child 21 of
    // neo-overhaul). Two sibling shapes to the child-16 seeding gap that were
    // NOT covered by child-16's Ldind/Ldelem seeding:
    //
    //   SHAPE 1 -- a Call returning a primitive float/double/long writes its
    //     result to a dest slot whose registerTypes[dest] is NEVER seeded.
    //     TypeSpecializeNeoOpcodes's Call case (JITCompiler.cs) only CLEARS a
    //     stale in-frame-VT / reference type off a reused dest; it never SEEDS
    //     a fresh dest with the resolved ReturnType. So a subsequent add/addi/
    //     sub/mul/... keyed on registerTypes[Register2] falls back to I4, the
    //     typed *_R4/*_R8 specialization no-ops, and a plain integer op runs on
    //     the raw IEEE bits -> garbage.
    //
    //   SHAPE 2 -- a raw Ldfld of a CLR-struct field (declaring type is
    //     CLRType, so the typed splitter leaves the raw OpCodeREnum.Ldfld).
    //     There is NO seeding case for raw Ldfld in TypeSpecializeNeoOpcodes
    //     (only the typed Ldfld_R4/R8/I8 arms are seeded). The single-
    //     expression form `a.X = a.X * 2 + 1` lowers the READ of a.X through
    //     raw ldfld (per child-16 TC3 note), so the loaded float temp is
    //     mis-typed I4 and the mul/add integer-corrupts the bits.
    //
    // Assertion: a passing probe returns without throwing; a logic failure is
    // surfaced by a deliberate 1/0 (DivideByZero). The value check is done via
    // the HOST helper TestCLRBinding.SumTestVector3Fields(a,b) so the float
    // arithmetic runs in CLR (sidesteps the separate out-of-scope conv.i4-
    // float-bit-reinterpret bug). Per the child-1/child-2 FAULT discipline each
    // probe MUST fault on HEAD if the bug is real. Probes are public static void
    // parameterless; names embed "NeoStep" so the NeoStep smoke filter matches.
    public class NeoStepFloatSeedingProbe
    {
        // Non-inlinable IL method (hasExceptionHandler -> canInline=false per
        // JITCompiler.InitializeFunctionParam). Returns a primitive float, so
        // the Call dest is a primitive-float producer that this change targets.
        static float NeoStepGetOneF()
        {
            try { return 1.0f; }
            catch (Exception) { return 1.0f; }
        }

        // Non-inlinable IL method returning a primitive double.
        static double NeoStepGetOneD()
        {
            try { return 1.0; }
            catch (Exception) { return 1.0; }
        }

        // SHAPE 1 (float): the call result is used DIRECTLY in arithmetic (not
        // stored to a typed local first), so the call dest is a fresh temp whose
        // registerTypes entry is null. add/addi keys on it -> I4 -> integer-add
        // of the float bits -> garbage. Correct: 1.0f + 100f = 101 ->
        // v=(101,1,1) -> Sum(v,v) = (101+1+1)*2 = 206. On HEAD the integer add
        // corrupts the bits -> sum != 206 -> DivideByZero.
        public static void NeoStepFloatSeeding_TC1_CallRetFloatAddConst()
        {
            TestVector3 v = TestVector3.One;        // (1,1,1)
            v.X = NeoStepGetOneF() + 100f;
            int s = TestCLRBinding.SumTestVector3Fields(v, v);
            if (s != 206)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // SHAPE 1 (double): same shape, double producer. Correct: 1.0 + 100.0 =
        // 101.0 -> v=(101,1,1) -> Sum = 206. On HEAD integer-add corrupts ->
        // DivideByZero.
        public static void NeoStepFloatSeeding_TC2_CallRetDoubleAddConst()
        {
            TestVector3 v = TestVector3.One;        // (1,1,1)
            v.X = (float)(NeoStepGetOneD() + 100.0);
            int s = TestCLRBinding.SumTestVector3Fields(v, v);
            if (s != 206)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // SHAPE 2 (raw Ldfld of a CLR-struct float field): the single-expression
        // form `a.X = a.X * 2 + 1` lowers the READ of a.X through a raw ldfld
        // (child-16 TC3 note). The loaded float temp is mis-typed I4 -> the mul
        // and add integer-corrupt the bits. Correct: 1*2+1 = 3 -> a=(3,1,1) ->
        // Sum(a,a) = (3+1+1)*2 = 10. On HEAD integer-corruption -> sum != 10 ->
        // DivideByZero. (Contrast child-16 TC3 which used the compound `*=`/`+=`
        // form that lowers through ldflda;ldind.r4 -- now seeded -- and so PASSES;
        // this single-expression form uses the still-unseeded raw ldfld.)
        public static void NeoStepFloatSeeding_TC3_RawLdfldClrStructMulAdd()
        {
            TestVector3 a = TestVector3.One;        // (1,1,1)
            a.X = a.X * 2 + 1;
            int s = TestCLRBinding.SumTestVector3Fields(a, a);
            if (s != 10)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // SHAPE 2 variant: pure register-register form `a.X = a.X + a.Y` (both
        // operands raw-ldfld-loaded CLR-struct floats). add keys on
        // registerTypes[Register2] (the first a.X load temp) -> I4 -> integer-add
        // of float bits. Correct: 1+1=2 -> a=(2,1,1) -> Sum = (2+1+1)*2 = 8.
        public static void NeoStepFloatSeeding_TC4_RawLdfldClrStructRegRegAdd()
        {
            TestVector3 a = TestVector3.One;        // (1,1,1)
            a.X = a.X + a.Y;
            int s = TestCLRBinding.SumTestVector3Fields(a, a);
            if (s != 8)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
