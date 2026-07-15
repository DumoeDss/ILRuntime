using System;

namespace TestCases
{
    /// <summary>
    /// neo-recluster-19 regression probes.
    /// TC1 guards the CLR-enum-RETURN sizing fix (DelegateTest19 class): a CLR
    /// enum returned from an IL method must round-trip its underlying int. On HEAD
    /// AllocateSlotForType mis-sized a CLR enum as a boxed reference (RefCount=1)
    /// while the JIT emits it as a flat int, so the Ret handler's vt-with-ref-
    /// fields branch OOB-read a non-existent ref slot.
    /// (TestCLREnum is fully qualified -- a TestCases.TestCLREnum shadow exists.)
    /// </summary>
    public class NeoStepRecluster19Test
    {
        // Force a REAL (non-inlined) call so the Ret handler runs: a method
        // carrying try/catch is never inlined by the Neo JIT (hasExceptionHandler
        // -> canInline is false). An inlined return would bypass the Ret handler
        // and not exercise the bug.
        static ILRuntimeTest.TestFramework.TestCLREnum ReturnClrEnumNonInlined()
        {
            try { } catch (Exception) { }
            return ILRuntimeTest.TestFramework.TestCLREnum.Test2;
        }

        public static void NeoStepRecluster19_TC1_ClrEnumReturn()
        {
            // Direct (non-inlined) call -> exercises the ExecuteNeo Ret handler.
            var v = ReturnClrEnumNonInlined();
            // TestCLREnum.Test2 == 1. On HEAD this probe FAULTS (the Ret OOBs in
            // the vt-with-ref-fields branch). After the AllocateSlotForType fix
            // the enum is sized RefCount=0 -> Ret takes the primitive CopyBlock
            // path -> the underlying int (1) round-trips.
            if ((int)v != 1)
                throw new Exception("CLR enum return wrong: got " + (int)v);
        }
    }
}
