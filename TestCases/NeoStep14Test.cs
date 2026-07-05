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
                if (e is DivideByZeroException && e.Message != null)
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

        // ====================================================================
        // D-IL-EXCEPTION-THROW probes (NeoStep14_ILEx_*).
        //
        // An IL `class X : System.Exception` loads (built-in ExceptionAdaptor),
        // can be thrown, and caught end-to-end. The Throw operand is an
        // ILTypeInstance; the shared Throw arm unwraps ILTypeInstance.CLRInstance
        // (the adaptor's Adapter, a real CLR Exception) and throws THAT. So the
        // catch slot holds the Adapter (CLR view). To read IL-declared fields
        // off the caught object, use the CrossBindingAdaptorType.ILInstance
        // bridge. Each test catches internally and returns 9 on success / -1 on
        // logic failure (the harness treats an UNCAUGHT exception as failure).
        // ====================================================================

        public class MyEx : System.Exception
        {
            public MyEx() { }
            public MyEx(string msg) { Msg = msg; }
            public string Msg;
            public override string Message
            {
                get { return Msg; }
            }
        }

        public class DerivedEx : MyEx
        {
            public DerivedEx() { }
            public DerivedEx(string msg) : base(msg) { }
        }

        // 3.1 IL exception thrown + caught by exact type, flag set.
        public static int NeoStep14_ILEx_ThrowAndCatch()
        {
            try
            {
                throw new MyEx();
            }
            catch (MyEx)
            {
                return 9;
            }
            return -1;
        }

        // 3.2 IL exception caught by CLR base type.
        public static int NeoStep14_ILEx_CatchByBaseType()
        {
            try
            {
                throw new MyEx();
            }
            catch (Exception)
            {
                return 9;
            }
            return -1;
        }

        // 3.3 derived IL exception caught by exact derived type.
        public static int NeoStep14_ILEx_CatchByExactType()
        {
            try
            {
                throw new DerivedEx();
            }
            catch (DerivedEx)
            {
                return 9;
            }
            return -1;
        }

        // 3.4 catch ordering: derived clause before base clause wins.
        public static int NeoStep14_ILEx_CatchOrderingDerivedBeforeBase()
        {
            try
            {
                throw new DerivedEx();
            }
            catch (DerivedEx)
            {
                return 9;
            }
            catch (MyEx)
            {
                return -1; // wrong clause ran
            }
            return -1;
        }

        // 3.5 rethrow: inner catch rethrows, outer catch sees it, code after
        // the inner throw; does NOT run.
        public static int NeoStep14_ILEx_Rethrow()
        {
            try
            {
                try
                {
                    throw new MyEx();
                }
                catch (MyEx)
                {
                    throw;
                }
                // unreachable: the throw; must not fall through here
                return -100;
            }
            catch (MyEx)
            {
                return 9;
            }
            return -1;
        }

        // 3.6 cross-frame: callee throws an IL exception, caller catches.
        static void NeoStep14_ILEx_ThrowingCallee()
        {
            throw new MyEx();
        }

        public static int NeoStep14_ILEx_CrossFramePropagation()
        {
            try
            {
                NeoStep14_ILEx_ThrowingCallee();
                return -1; // unreachable on success
            }
            catch (MyEx)
            {
                return 9;
            }
        }

        // 3.7 message field: throw MyEx carrying a Msg, recover the IL view of
        // the caught object via the CrossBindingAdaptorType bridge, and assert
        // the IL type identity. The catch slot holds the CLR Adapter view (OQ1:
        // confirmed -- e is NOT an ILTypeInstance directly), so the IL type is
        // recovered via ILInstance. (Reading the IL-declared Msg field VALUE
        // through ILTypeInstance's indexer is blocked on Neo -- the indexer
        // returns null under ENABLE_NEO_MODE, a separate pre-existing Neo
        // object-model limitation; OQ2 resolved: the minimal adaptor forwards
        // ToString only, NOT Message, to avoid the Neo appdomain.Invoke
        // re-entry gap that does not push `this` for instance methods.)
        public static int NeoStep14_ILEx_MessageField()
        {
            try
            {
                throw new MyEx("hello-il-ex");
            }
            catch (MyEx e)
            {
                // e is the adaptor's Adapter (CLR Exception view) -- OQ1
                // confirmed via runtime diagnostic (ex.GetType ==
                // ExceptionAdaptor+Adapter). Prove the caught object is non-null
                // and assignable to the IL catch type's CLR projection: use the
                // isinst/is path (the same opcode the catch matcher uses, known
                // to work on the Adapter). The caught `e` IS the IL exception
                // (its IL type is MyEx); verifying `e is MyEx` confirms the
                // caught object matches the IL catch type end-to-end.
                if (e == null)
                    return -2;
                bool isMyEx = e is MyEx;
                return isMyEx ? 9 : -5;
            }
            catch (Exception)
            {
                return -4;
            }
        }

        // 3.8 mixed: a method with both an IL-exception catch and a CLR-
        // exception catch -- each thrown exception lands in the RIGHT clause.
        public static int NeoStep14_ILEx_MixedWithCLR()
        {
            int ilHit = 0;
            int clrHit = 0;

            // IL exception branch
            try
            {
                throw new MyEx();
            }
            catch (MyEx)
            {
                ilHit = 1;
            }
            catch (DivideByZeroException)
            {
                return -1; // cross-contamination
            }

            // CLR exception branch
            try
            {
                int z = 1; int d = 0; int _ = z / d;
                return -2; // unreachable on success
            }
            catch (DivideByZeroException)
            {
                clrHit = 1;
            }
            catch (MyEx)
            {
                return -3; // cross-contamination
            }

            return (ilHit == 1 && clrHit == 1) ? 9 : -4;
        }
    }
}
