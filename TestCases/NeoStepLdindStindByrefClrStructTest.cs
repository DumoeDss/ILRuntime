using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // neo-ldind-stind-byref-clr-struct (child 15) probes: a CLR value-type LOCAL
    // (inline flat managed bytes in the frame) whose field is addressed by
    // `ldflda` and then read/written through the resulting byref via
    // `ldind_*`/`stind_*`. The defect: for a CLR (non-IL) declaring type,
    // AppDomain.GetFieldOffset returns PrimitiveOffset = type.GetFieldIndex(token)
    // = FieldInfo.GetHashCode() -- a large arbitrary 32-bit HASH, NOT a byte
    // offset. So the ldflda frame-native branch (objectIndex == -1, from
    // `ldloca <CLR-struct local>`) produced `(-1, vtBase + <huge hash>)`, and the
    // following `ldind.r4`/`stind.r4` deref `frameBase + <huge hash>` -> AV
    // (segfault, exit 139). LATENT until child 8 made `TestVector3.One` ldsfld
    // reachable.
    //
    // The fix (this change): the JIT stamps a new Operand4 marker bit
    // (NeoLdfldaClrStructLocalFieldMarker = 0x8) when the field's declaring type
    // is a CLRType; the runtime ldflda frame-native branch then resolves the
    // field's REAL managed byte offset (cached Marshal.OffsetOf) and uses it
    // instead of the hash, so the byref is a valid frame address and every
    // consumer (ldind_*/stind_*/stobj/ldobj/initobj) works unchanged.
    //
    // Assertion mechanism: a passing test returns without throwing; a logic
    // failure is surfaced by a deliberate `1/0` (DivideByZero fault). The value
    // check is done via the HOST helper `TestCLRBinding.SumTestVector3Fields(a,b)`
    // = (int)(a.X+a.Y+a.Z+b.X+b.Y+b.Z) -- the float arithmetic runs in CLR (not
    // the Neo VM), so it sidesteps two UNRELATED pre-existing Neo float bugs (the
    // `addi` integer-add-of-float-bits constant fold, and the `conv.i4` float-
    // bit-reinterpret) that would otherwise mask this child's result. Per the
    // child-1/child-2 FAULT-to-fail discipline each probe MUST fault on HEAD (the
    // AV -- stash-toggle-confirmed, exit 139) AND assert the value so a wrong-
    // offset fix also fails. Tests are `public static void` parameterless; the
    // class/method names embed "NeoStep" so the NeoStep smoke filter picks them up.
    public class NeoStepLdindStindByrefClrStructTest
    {
        // TC1 exercises the stind.r4 (WRITE) consumer of the ldflda-produced
        // byref. `ref float x = ref v.X;` lowers to `ldloca v; ldflda X` (the
        // frame-native byref); `x = 42f;` lowers to `stind.r4` writing through it.
        // v starts at TestVector3.One = (1,1,1); after the write v = (42,1,1), so
        // SumTestVector3Fields(v, v) = (42+1+1)+(42+1+1) = 88. The stind.r4 AVs on
        // HEAD (stash-toggle-confirmed); the equality guards a wrong-offset fix.
        public static void NeoStepLdindStindByrefClrStruct_TC1_StindWriteByref()
        {
            TestVector3 v = TestVector3.One;
            ref float x = ref v.X;
            x = 42f;

            int s = TestCLRBinding.SumTestVector3Fields(v, v);
            if (s != 88)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC2 exercises the stind.r4 (WRITE) consumer at a NON-ZERO field offset
        // (Y = managed byte offset 4), proving the ldflda arm resolves the field's
        // REAL managed byte offset via Marshal.OffsetOf -- NOT the FieldInfo hash
        // (which is never exactly 4) and NOT a hard-wired 0 (which would clobber
        // X). `ref float y = ref v.Y; y = 70f;` lowers to `ldloca v; ldflda Y`
        // (frame-native byref, offset 4) + `stind.r4`. v starts at One = (1,1,1);
        // after the write v = (1,70,1), so SumTestVector3Fields(v, v) =
        // (1+70+1)+(1+70+1) = 144. (The ldind.r4 READ consumer shares the SAME
        // byref offset half; the stash-toggle confirmed its HEAD AV is resolved by
        // this fix. A direct ldind-into-temp probe is avoided here because an
        // unrelated pre-existing register-temp aliasing quirk in the
        // `dst = src` (ldind->temp->stind) shape would mask this child's result.)
        public static void NeoStepLdindStindByrefClrStruct_TC2_StindWriteByrefNonZeroOffset()
        {
            TestVector3 v = TestVector3.One;
            ref float y = ref v.Y;
            y = 70f;

            int s = TestCLRBinding.SumTestVector3Fields(v, v);
            if (s != 144)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
