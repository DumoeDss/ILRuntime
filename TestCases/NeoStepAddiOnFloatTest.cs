using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // neo-addi-on-float (child 16 of neo-overhaul) probes: a float/double/long
    // operand loaded by an indirect load (`ldind.r4`, the byref/CLR-struct-field
    // producer reached via `ldloca; ldflda; ldind.r4`) or an element load
    // (`ldelem.r4`) and then combined with a constant (`+=`, `-=`, `*=`, ...).
    //
    // The defect: the front-half constant-fold (ELDC, shared with Legacy and
    // CORRECT) folds `add(floatReg, ldc.r4 100)` into the integer immediate form
    // `addi r, r, 0x42C80000` (the IEEE bits of 100.0f). Neo's frame is UNTYPED,
    // so Neo's plain `Addi` runtime arm is hardcoded integer; Neo relies on the
    // back-half `TypeSpecializeNeoOpcodes` to rewrite `Addi -> Addi_R4` (whose arm
    // reads `OperandFloat`). That specialization keys on
    // `registerTypes[op.Register2]`, but the producing `Ldind_R4`/`Ldelem_R4` was
    // NEVER seeded (only `Ldc_*` and `Ldfld_*` were), so the operand stays the
    // default I4, the typed specialization silently no-ops, and a plain integer
    // `Addi` integer-adds the float bits -> garbage.
    //
    // The fix (this change): `TypeSpecializeNeoOpcodes` now seeds
    // `registerTypes[op.Register1]` for `Ldind_R4/R8/I8/I4` and
    // `Ldelem_R4/R8/I8/I4`, so the typed-immediate (`Addi/Subi/Muli/Divi/Remi ->
    // *_R4/*_R8/*_I8`) AND typed-binary (`Add/Sub/Mul -> *_R4/...`) specializations
    // fire for an operand loaded via a byref/CLR-struct-field indirection or an
    // array element.
    //
    // Assertion mechanism: a passing test returns without throwing; a logic
    // failure is surfaced by a deliberate `1/0` (DivideByZero fault). The value
    // check is done via the HOST helper `TestCLRBinding.SumTestVector3Fields(a,b)`
    // = (int)(a.X+a.Y+a.Z+b.X+b.Y+b.Z) -- the float arithmetic runs in CLR (not
    // the Neo VM), so it sidesteps the separate, out-of-scope `conv.i4` float-
    // bit-reinterpret bug. Per the child-1/child-2 FAULT-to-fail discipline each
    // probe MUST fault on HEAD (stash-toggle-confirmed DivideByZero on the wrong
    // value). Tests are `public static void` parameterless; the class/method names
    // embed "NeoStep" so the NeoStep smoke filter picks them up.
    public class NeoStepAddiOnFloatTest
    {
        // TC1 exercises the `addi` (immediate add) path. `a.X += 100` on a float
        // field of a CLR struct local lowers to `ldloca a; ldflda X; ldind.r4`
        // (read a.X) + the constant-folded `addi r, r, <bits of 100.0f>` (write
        // back via stind.r4). a starts at TestVector3.One = (1,1,1); after the
        // increment a = (101,1,1), so SumTestVector3Fields(a, a) =
        // (101+1+1)+(101+1+1) = 206. On HEAD the integer `addi` integer-adds the
        // float bits -> garbage -> sum != 206 -> DivideByZero.
        public static void NeoStepAddiOnFloat_TC1_FloatPlusEqConst()
        {
            TestVector3 a = TestVector3.One;
            a.X += 100;

            int s = TestCLRBinding.SumTestVector3Fields(a, a);
            if (s != 206)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC2 exercises the `subi` (immediate subtract) path. `a.X -= 100` lowers
        // the same `ldind.r4` + constant-folded `subi r, r, <bits of 100.0f>` way.
        // a starts at One = (1,1,1); after the decrement a = (-99,1,1), so
        // SumTestVector3Fields(a, a) = (-99+1+1)+(-99+1+1) = -194. On HEAD the
        // integer `subi` corrupts the float bits -> sum != -194 -> DivideByZero.
        public static void NeoStepAddiOnFloat_TC2_FloatMinusEqConst()
        {
            TestVector3 a = TestVector3.One;
            a.X -= 100;

            int s = TestCLRBinding.SumTestVector3Fields(a, a);
            if (s != -194)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC3 exercises BOTH the `muli` and the `addi` paths. We use two compound
        // assignments (`*=` then `+=`) so EACH read of a.X lowers through the
        // `ldloca; ldflda; ldind.r4` byref producer that this change seeds. (A
        // single expression `a.X = a.X * 2 + 1` instead lowers the READ through a
        // raw `ldfld` of the CLR-struct field -- a DIFFERENT, out-of-scope
        // producer whose registerTypes seeding is NOT covered by this change -- so
        // it would not exercise the Ldind_R4 seeding under test. The compound
        // `*=`/`+=` forms reliably use the byref/ldind shape, identical to TC1/TC2.)
        // a starts at One = (1,1,1); a.X *= 2 -> 2, then a.X += 1 -> 3, so
        // a = (3,1,1) and SumTestVector3Fields(a, a) = (3+1+1)+(3+1+1) = 10.
        // On HEAD both the muli and the addi integer-corrupt the float bits
        // -> sum != 10 -> DivideByZero.
        public static void NeoStepAddiOnFloat_TC3_CompoundMulAdd()
        {
            TestVector3 a = TestVector3.One;
            a.X *= 2;
            a.X += 1;

            int s = TestCLRBinding.SumTestVector3Fields(a, a);
            if (s != 10)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
