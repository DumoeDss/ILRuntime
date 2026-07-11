using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // neo-bare-nie probes: two Neo-reachable paths into AppDomain.GetPrimitiveSize
    // that previously hit the bare default-message NIE
    // ("The method or operation is not implemented.") -- the helper only sized the
    // primitive singletons and threw for anything else.
    //
    //   TC1 -- an enum-typed BY-VALUE call parameter. When the method is JIT-
    //          compiled under Neo, AllocateNeoCallParamSlot sizes the enum call-
    //          param via GetPrimitiveSize(enumType); pre-fix that throws the bare
    //          NIE DURING JIT (the method fails to compile -- the DelegateTest36-40
    //          reproducer shape, a CLR method taking System.Reflection.BindingFlags).
    //   TC2 -- a CLR struct copied via stobj/ldobj. The ExecuteNeo Stobj/Ldobj arms
    //          call GetPrimitiveSize(t) for a value-type token that is NOT an ILType
    //          (ilType == null, a CLR struct); pre-fix that throws the bare NIE AT
    //          RUNTIME.
    //
    // The fix broadens GetPrimitiveSize to delegate enums + CLR value types to
    // Optimizer.GetNeoValueTypeManagedSize (enum -> underlying primitive; struct ->
    // Unsafe.SizeOf). The residual throw is tagged [neo-bare-nie].
    //
    // Assertion mechanism: same as prior NeoStep tests -- a passing test returns
    // without throwing; a wrong value is surfaced by a deliberate `1/0`
    // (DivideByZero fault; the Neo VM cannot yet `new Exception(...)`). Per the
    // child-1/child-2 FAULT-to-fail discipline each probe MUST fault on the current
    // code (the bare NIE) and pass after the fix. Tests are `public static void`
    // parameterless.

    public class NeoStepBareNieTest
    {
        // TC1 enum-typed by-value call parameter: calls a CLR method
        // (System.Type.GetMethods) whose parameter is a CLR enum (BindingFlags).
        // Pre-fix: the bare NIE is thrown during JIT (AllocateNeoCallParamSlot
        // sizes the enum call-param via GetPrimitiveSize). Post-fix: the enum is
        // sized as its underlying int (4), the method compiles, and the call runs.
        // int has well-known public instance methods (ToString/Equals/GetHashCode/
        // CompareTo/...), so a non-empty result proves the call executed AND the
        // enum flag was honored (a corrupted arg would yield a different filter).
        public static void NeoStepBareNie_TC1_EnumCallArg()
        {
            System.Reflection.MethodInfo[] ms = typeof(int).GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (ms == null || ms.Length == 0)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // stobj + ldobj on a CLR struct (TestVector3). The helper stores the src
        // byref's value into the dst byref: `dst = src` lowers to ldobj TestVector3
        // (read the value type from the src managed pointer) + stobj TestVector3
        // (store the value type into the dst managed pointer). The ExecuteNeo
        // Stobj/Ldobj arms call GetPrimitiveSize(t) for a CLR struct (ilType ==
        // null); pre-fix that is the bare default-message NIE at runtime, post-fix
        // it sizes via GetNeoValueTypeManagedSize (12 bytes for 3 floats) and the
        // copy completes. Uses ref PARAMS (the Step-17 Ref-Slot call ABI), not ref
        // locals. TestVector3 is pure-primitive (X/Y/Z floats) so the arm's
        // refCount == 0 is the correct managed-ref count.
        static void CopyStructThroughRefs(ref TestVector3 dst, ref TestVector3 src)
        {
            dst = src;
        }

        // TC2 CLR-struct stobj/ldobj copy: build a src, copy it through byrefs into
        // a dst, assert all three fields survived. Faults on the current runtime
        // bare NIE (Stobj/Ldobj arm -> GetPrimitiveSize); after the fix any wrong
        // field fails the comparison -> 1/0.
        public static void NeoStepBareNie_TC2_ClrStructStobjLdobj()
        {
            TestVector3 src = default;
            src.X = 7f;
            src.Y = 8f;
            src.Z = 9f;
            TestVector3 dst = default;
            CopyStructThroughRefs(ref dst, ref src);   // ldobj + stobj TestVector3
            if (dst.X != 7f || dst.Y != 8f || dst.Z != 9f)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
