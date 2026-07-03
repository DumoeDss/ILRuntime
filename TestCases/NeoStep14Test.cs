using System;

namespace TestCases
{
    // Step 14 test: Neo structured exception handling (throw / catch / finally /
    // leave / rethrow) with correct nesting and cross-frame propagation.
    //
    // Assertion mechanism: a test PASSES when its IL method returns without
    // throwing. The Neo VM does not yet support `new Exception(...)` for CLR
    // types (CLR newobj is Step 9), so every throw source is a no-newobj native
    // fault: `int _ = 1/0` raises DivideByZeroException, and a null IL-instance
    // field access raises NullReferenceException. A catch that swallows such a
    // fault lets the method return normally => PASS. A negative check (an
    // expected fault did NOT happen) deliberately performs an uncaught `1/0` to
    // turn a logic failure into a visible test failure. catch-type matching is
    // resolved by the shared handler engine (GetCorrespondingExceptionHandler),
    // so specific `catch (DivideByZeroException)` clauses are used instead of
    // `is`-checks (isinst/castclass land in Step 15).

    public class NeoStep14Test
    {
        // TC1 basic try-catch: the catch is reached and the divide result is
        // never returned.
        public static int NeoStep14_TC1_BasicTryCatch()
        {
            try
            {
                int z = 1; int d = 0; int _ = z / d;
                return -1; // unreachable on success
            }
            catch (DivideByZeroException)
            {
                return 7;
            }
        }

        // TC2 catch-object access: the caught object is non-null and reachable
        // inside the catch body, proving it was stored into the catch handler's
        // ref slot.
        public static int NeoStep14_TC2_CatchObjectAccess()
        {
            try
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            catch (DivideByZeroException e)
            {
                if (e != null)
                    return 7;
            }
            return -1; // reachable only if catch was skipped or object was null
        }

        // TC3 try-finally runs on the exception path: the finally executes even
        // though the catch swallowed the fault.
        public static int NeoStep14_TC3_FinallyRunsOnException()
        {
            int r = 1;
            try
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            catch (DivideByZeroException)
            {
                r = 2;
            }
            finally
            {
                r += 10;
            }
            if (r != 12)
            {
                int z = 1; int d = 0; int _ = z / d; // fail signal
            }
            return r;
        }

        // TC4 finally runs on a normal Leave (no exception).
        public static int NeoStep14_TC4_FinallyOnNormalLeave()
        {
            int r = 0;
            try
            {
                r = 1;
            }
            finally
            {
                r += 10;
            }
            if (r != 11)
            {
                int z = 1; int d = 0; int _ = z / d; // fail signal
            }
            return r;
        }

        // TC5 nested try-catch innermost wins: the inner catch handles the
        // fault; the outer catch does not run.
        public static int NeoStep14_TC5_NestedInnermostWins()
        {
            try
            {
                try
                {
                    int z = 1; int d = 0; int _ = z / d;
                    return -1;
                }
                catch (DivideByZeroException)
                {
                    return 5;
                }
            }
            catch (Exception)
            {
                return -1; // reachable only if outer catch wrongly won
            }
        }

        // TC6 cross-frame propagation: a callee throws via 1/0; the caller
        // catches it (requires the cross-frame fix).
        public static int NeoStep14_TC6_CrossFrameCatch()
        {
            try
            {
                NeoStep14_CalleeDivZero();
                return -1; // unreachable on success
            }
            catch (DivideByZeroException)
            {
                return 9;
            }
        }

        // TC7 cross-frame caller finally: when the callee throws and the caller
        // catches, the caller's finally still runs (caller frame state intact).
        public static int NeoStep14_TC7_CrossFrameCallerFinally()
        {
            int r = 0;
            try
            {
                NeoStep14_CalleeDivZero();
                r = -1;
            }
            catch (DivideByZeroException)
            {
                r = 3;
            }
            finally
            {
                r += 10;
            }
            if (r != 13)
            {
                int z = 1; int d = 0; int _ = z / d; // fail signal
            }
            return r;
        }

        // TC8 NullReferenceException caught: a null IL-instance field access
        // raises NRE which is caught.
        public static int NeoStep14_TC8_NullRefCatch()
        {
            NeoStep14Holder h = null;
            try
            {
                int v = h.value;
                return -1; // unreachable on success
            }
            catch (NullReferenceException)
            {
                return 8;
            }
        }

        // TC9 rethrow: a caught exception is rethrown and observed by an outer
        // handler in the same frame.
        public static int NeoStep14_TC9_Rethrow()
        {
            int seen = 0;
            try
            {
                try
                {
                    int z = 1; int d = 0; int _ = z / d;
                }
                catch (DivideByZeroException)
                {
                    seen = 1;
                    throw;
                }
            }
            catch (DivideByZeroException)
            {
                seen += 10;
            }
            if (seen != 11)
            {
                int z = 1; int d = 0; int _ = z / d; // fail signal
            }
            return seen;
        }

        // ---- helpers ----

        static void NeoStep14_CalleeDivZero()
        {
            int z = 1; int d = 0; int _ = z / d;
        }

        class NeoStep14Holder
        {
            public int value;
        }
    }
}
