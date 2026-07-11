using System;

namespace TestCases
{
    // neo-misc-opcodes probes (child-7 of neo-overhaul): four independent
    // ExecuteNeo opcode gaps that each surface in the full Neo smoke as a
    // Step-6 / tagged NIE -- Conv_R_Un, Switch, Unbox of a boxed IL enum, and
    // Ldsflda. Each arm mirrors a Legacy ExecuteR arm 1:1 (Register.cs:1258 /
    // 2754 / 4129 / 3348). All edits are Neo-gated, so Legacy is untouched.
    //
    // Assertion mechanism (child-1..6 discipline): a passing test simply returns
    // without throwing; a logic failure is surfaced by a deliberate `1/0`
    // (DivideByZero fault; the Neo VM cannot yet `new Exception(...)`). Each
    // probe MUST fault on HEAD (the uncaught Step-6 / tagged NIE) AND assert the
    // exact value so a wrong-value bug also fails. Tests are `public static void`
    // parameterless; names embed "NeoStep" so they run in the NeoStep smoke.

    public class NeoStepMiscOpTest
    {
        // ---- Shared state for the Ldsflda probe (IL static field + ref helper) ----
        private static int S_Sf;

        private static void SetRef(ref int x, int v) { x = v; }

        private static int GetRef(ref int x) { return x; }

        // An IL enum for the Unbox-of-enum probe (underlying type int).
        public enum E { A = 1, B = 2, C = 7 }

        // TC1 Conv_R_Un: (double)(uint) of a sign-bit-set value must yield the
        // UNSIGNED (positive) interpretation, not the signed (negative) one.
        // Uses 0x80000000 (2^31): signed-int reading = -2147483648 (negative),
        // unsigned = +2147483648 (positive) -- so the sign of `d` proves the
        // unsigned interpretation. 2^31 is EXACTLY representable as float32, so
        // Legacy (conv.r.un -> float32 intermediate for 32-bit sources, per the
        // CIL spec / design D1) and Neo (-> double, 8-byte dest) AGREE on the
        // value -> the probe is Legacy-neutral. (0xFFFFFFFF would round in the
        // Legacy float32 intermediate, so it is Neo-only.) The U8 line builds a
        // sign-bit-set ulong at runtime (avoids literal-loading peepholes);
        // unsigned -> large positive, signed -> negative, truncated -> 0. Faults
        // on the Step-6 NIE (Conv_R_Un not implemented) on HEAD; after the fix a
        // signed/truncated read fails the checks -> 1/0.
        public static void NeoStepMiscOp_ConvRUn()
        {
            uint u = 0x80000000;
            double d = (double)u;
            ulong ul = ((ulong)u << 32);
            double dl = (double)ul;
            if (d != 2147483648.0 || !(dl > 1.0e18))
            {
                int z = 1; int dd = 0; int _ = z / dd;
            }
        }

        // TC2 Switch: a 3-case switch(int) + default. x==1 must select case 1
        // (-> 20); x==99 (out of range) must fall through to default (-> -1).
        // Faults on the Step-6 NIE (Switch not implemented) on HEAD; after the
        // fix a wrong-case jump (e.g. an index mis-read) fails the equality ->
        // 1/0.
        public static void NeoStepMiscOp_Switch()
        {
            int r1 = SwitchSelect(1);
            int r99 = SwitchSelect(99);
            if (r1 != 20 || r99 != -1)
            {
                int z = 1; int dd = 0; int _ = z / dd;
            }
        }

        private static int SwitchSelect(int x)
        {
            switch (x)
            {
                case 0: return 10;
                case 1: return 20;
                case 2: return 30;
                default: return -1;
            }
        }

        // TC3 Unbox-of-enum: box IL enum E.B, then unbox to its underlying int
        // via (int)(object)E.B. The result must be 2 (the enum's underlying
        // constant). On HEAD the Neo Unbox CLR-primitive branch hands the
        // ILEnumTypeInstance straight to NeoWritePrimitiveToFrame, which throws
        // "unsupported CLR primitive for Unbox: ILEnumTypeInstance"; after the
        // fix the enum guard extracts the underlying value, and a wrong/zero
        // extraction fails the equality -> 1/0.
        public static void NeoStepMiscOp_UnboxEnum()
        {
            E e = E.B;
            object o = e;
            int i = (int)o;
            if (i != 2)
            {
                int z = 1; int dd = 0; int _ = z / dd;
            }
        }

        // TC4 Ldsflda: take the address of an IL static field via `ref int`
        // helpers. The optimizer inlines these to `ldsflda S_Sf` + a byref
        // consumer: SetRef -> ldsflda; stind.i4 (write through the address),
        // GetRef -> ldsflda; ldind.i4 (read through the address). Both stind
        // and ldind are LowerNeoOffsets-lowered byref consumers that dispatch
        // `mStack[objIdx] is ILTypeInstance` -> Primitives[off], so the
        // materialized StaticInstance byref resolves with zero consumer change.
        // (Read-back is via ldind, not ldsfld, to stay within the byref path the
        // Ldsflda arm produces.) Faults on the Step-6 NIE (Ldsflda not
        // implemented) on HEAD; after the fix a byref the consumer did not
        // recognize yields a wrong read-back value -> fails the equality -> 1/0.
        public static void NeoStepMiscOp_Ldsflda()
        {
            SetRef(ref S_Sf, 7);
            int v = GetRef(ref S_Sf);
            if (v != 7)
            {
                int z = 1; int dd = 0; int _ = z / dd;
            }
        }
    }
}
