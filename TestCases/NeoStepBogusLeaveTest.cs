using System;

namespace TestCases
{
    // rasen neo-jit-bogus-opcode regression probe.
    //
    // Root cause (confirmed via an instrumented ExecuteNeo dump on the full Neo
    // smoke): Optimizer.LowerNeoOffsets deletes synthetic Push instructions for
    // Call/Newobj with MORE than 3 register parameters and re-maps control-flow
    // targets via FixBranchTargetsAfterRemove. That helper remapped IsBranching
    // (Operand), IsIntermediateBranching (Operand4) and Switch jump tables, but
    // NOT Leave / Leave_S (whose EH target lives in Operand). After a deletion
    // shifted indices below a Leave target, the Leave kept pointing at its
    // pre-deletion index. When that target was the method-exit position (past the
    // final Ret, as in the async state-machine MoveNext) the un-bounded
    // ExecuteNeo loop read a garbage Code (0x23F70C) and threw a misleading
    // "opcode 2359324 not yet implemented (Step 6)". With the permanent ExecuteNeo
    // dispatch guard, an un-remapped Leave that would run past the body end
    // instead throws "Neo: ip ran past body end in <method> ...".
    //
    // The probes below put a >3-parameter call (forcing the Push-deletion path)
    // inside try/catch (emitting Leave). Whether an un-remapped Leave then lands
    // past the body end depends on the exact lowered layout (instruction count /
    // operand shape). Stash-toggle verified (task 5.2): TC_RET5 deterministically
    // overruns on HEAD ("Neo: ip ran past body end ... at index 12/11") and passes
    // with the fix -> it is the load-bearing detector. The other TCs exercise the
    // same lowering path and verify the fix does not regress those shapes (they
    // pass with the fix applied). (Pass = runs without throwing.)
    public class NeoStepBogusLeaveTest
    {
        static int Sum4(int a, int b, int c, int d) => a + b + c + d;
        static int Sum6(int a, int b, int c, int d, int e, int f) => a + b + c + d + e + f;
        static string Cat5(string a, string b, string c, string d, string e) => a + b + c + d + e;

        // TC_VOID6: void try/catch + a 6-arg call (3 Pushes deleted). The try/catch
        // is the whole body, so the Leave targets the final Ret; the deletion shifts
        // the Ret below the un-remapped Leave target -> overrun -> guard throw.
        public static void NeoStepBogusLeave_TC_VOID6_TryCatch6Arg()
        {
            try { Sum6(1, 2, 3, 4, 5, 6); }
            catch (Exception) { }
        }

        // TC_VOID4: same shape with a 4-arg call (1 Push deleted).
        public static void NeoStepBogusLeave_TC_VOID4_TryCatch4Arg()
        {
            try { Sum4(10, 20, 30, 40); }
            catch (Exception) { }
        }

        // TC_RET6: direct-return of the multi-arg call result inside try/catch.
        public static int NeoStepBogusLeave_TC_RET6_Return6Arg()
        {
            try { return Sum6(1, 2, 3, 4, 5, 6); }
            catch (Exception) { return -1; }
        }

        // TC_RET5: direct-return of a 5-arg ref-type call's length. THE detector:
        // on HEAD (Leave remap missing) the guard throws "ip ran past body end ...
        // at index 12/11"; with the fix it returns 5.
        public static int NeoStepBogusLeave_TC_RET5_Return5ArgRef()
        {
            try { return Cat5("a", "b", "c", "d", "e").Length; }
            catch (Exception) { return -1; }
        }

        // TC_ASN6: result assigned then returned (the in-range mis-target shape;
        // included for coverage -- may pass on both HEAD and fixed).
        public static int NeoStepBogusLeave_TC_ASN6_Assign6Arg()
        {
            int r;
            try { r = Sum6(1, 2, 3, 4, 5, 6); }
            catch (Exception) { r = -1; }
            return r;
        }
    }
}
