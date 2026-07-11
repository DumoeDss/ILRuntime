using System;

namespace TestCases
{
    // rasen neo-overhaul-eh-table-remap regression probe (child 14).
    //
    // Defect: Optimizer.Neo.cs LowerNeoOffsets deletes the synthetic Push
    // instructions the JIT emits for Call/Newobj with MORE than 3 register
    // parameters, and FixBranchTargetsAfterRemove re-maps the body-indexed
    // control-flow targets that the shift invalidates (branch / intermediate /
    // Switch / Leave / symbols). It did NOT re-map the exception-handler table
    // (method.ExceptionHandlerRegister: TryStart/TryEnd/HandlerStart/HandlerEnd,
    // all body-indexed). ExecuteNeo indexes into the POST-deletion body, so after
    // a deletion the stale EH boundaries (CodeBody/front-half order) point too
    // high -> a throw whose runtime addr falls outside the stale [TryStart,
    // TryEnd] MISSES its handler -> the exception escapes unhandled.
    //
    // Trigger shape (load-bearing, two non-obvious details):
    //  (a) The >3-arg call must be placed BEFORE the try block AND must be a REAL
    //      Call (not inlined). The Neo inliner (JITCompiler.InitializeFunctionParam)
    //      inlines small IL methods (body <= MaximalInlineInstructionCount/2 == 10
    //      register instructions) and NEVER inlines a method that has an exception
    //      handler. An inlined call emits no Push -> no deletion -> no staleness ->
    //      no fault. So Warmup4/Warmup6 are deliberately padded past the inline
    //      threshold, and ThrowUnconditionally carries a try/finally (=> has EH =>
    //      not inlined).
    //  (b) The throw must come from a NON-INLINED NO-ARG call that is the FIRST
    //      statement of the try. That Call opcode sits exactly at TryStart, so the
    //      runtime throw addr (== the Call site index, post-deletion) has ZERO
    //      in-try instructions before it and falls STRICTLY below the stale
    //      (pre-deletion) TryStart -> the catch is MISSED on HEAD -> the exception
    //      escapes unhandled -> FAULT. (An inlined `throw new X` adds ldstr/newobj
    //      before the throw, which can offset the deletion and leave the throw addr
    //      inside the stale window by luck -- so it is NOT a reliable trigger.)
    //
    // With the fix (FixBranchTargetsAfterRemove decrements the 4 EH fields in
    // lockstep with branch targets on every deletion, per-deletion inside the
    // loop) the EH table is kept in the post-deletion frame, the throw routes to
    // the catch, and the probe returns its sentinel. Stash-toggle confirmed: both
    // TCs FAULT on HEAD (throw escapes the catch) and PASS with the fix.
    public class NeoStepEhTableRemapTest
    {
        static int g_throwSentinel;

        // Padded well past the inline threshold (>10 register instructions) so the
        // Neo inliner emits a REAL Call. Warmup4 -> 1 overflow Push; Warmup6 -> 3.
        // Neither throws for first-arg == 0; they exist only to drive the
        // Push-deletion path at body indices BEFORE the caller's try-start.
        static int Warmup4(int a, int b, int c, int d)
        {
            int r = a + b + c + d;
            r = r * 3 + 7;
            r = r - a + b - c + d;
            r ^= r >> 1;
            r += r; r -= a; r += b; r ^= c; r += d;
            return r;
        }
        static int Warmup6(int a, int b, int c, int d, int e, int f)
        {
            int r = a + b + c + d + e + f;
            r = r * 3 + 7;
            r = r - a + b - c + d - e + f;
            r ^= r >> 1;
            r += r; r -= a; r += b; r ^= c; r += d; r -= e; r += f;
            return r;
        }

        // A throwing helper the inliner will NOT inline (it has an exception
        // handler => canInline is false). No-arg + called as the FIRST try
        // statement => its Call opcode is exactly at TryStart, so the runtime
        // throw addr is minimal. The finally only exists to keep the EH alive
        // (defeats empty-finally elision) and incidentally exercises a finally
        // EH entry; it never swallows the exception.
        static void ThrowUnconditionally()
        {
            try
            {
                throw new InvalidOperationException("boom");
            }
            finally
            {
                g_throwSentinel++;
            }
        }

        // TC1: 6-arg warmup (3 overflow Pushes deleted before try-start) before
        // the try. On HEAD the stale TryStart sits 3 above the runtime throw addr
        // -> catch missed -> unhandled -> FAULT. With the fix, returns 42.
        public static int NeoStepEhTableRemap_TC1_6ArgBeforeTry()
        {
            Warmup6(0, 2, 3, 4, 5, 6);
            try
            {
                ThrowUnconditionally();
                return 0;
            }
            catch (Exception)
            {
                return 42;
            }
        }

        // TC2: 4-arg warmup (1 overflow Push deleted before try-start). A different
        // deletion count (1 vs 3) confirms the per-deletion decrement is applied
        // correctly (not a fixed offset). Same reliable miss on HEAD.
        public static int NeoStepEhTableRemap_TC2_4ArgBeforeTry()
        {
            Warmup4(0, 2, 3, 4);
            try
            {
                ThrowUnconditionally();
                return 0;
            }
            catch (Exception)
            {
                return 43;
            }
        }
    }
}
