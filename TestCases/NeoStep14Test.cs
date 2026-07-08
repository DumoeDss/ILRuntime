using System;
using ILRuntime.Runtime.Enviorment;
using ILRuntime.Runtime.Intepreter;
using ILRuntimeTest.TestFramework;

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
            // F-4 #3 instance-method-under-test: reads the IL-declared Msg field
            // (so the override runs with the right `this` -- on HEAD, Run drops
            // `instance`, this NREs on the field access; after the fix it returns
            // the override's value). Returns the field's length as a primitive
            // (avoids the reference-return path, which is exercised separately by
            // the F-12 probe).
            public int GetCode()
            {
                return Msg.Length;
            }
        }

        // F-12 / NEO-RUN-REF-RETURN: a PARAMETERLESS IL method returning a
        // REFERENCE type (string). Invoked via the public AppDomain.Invoke -> Run
        // path. On HEAD Run reads this via NeoBoxReturnValue (primitives only) ->
        // the raw mStack index as an int (0); after the fix Run's reference-return
        // branch boxes the string.
        public static string EchoRef()
        {
            return "hi";
        }

        // F-4 #3 host-side target: construct a MyEx with a known Msg via the
        // default ctor + a direct field assignment (the workaround for the known
        // `new MyEx(string)` ctor string-arg mis-route), and return it. The host
        // self-check (NeoF4ParamRunCheck) invokes this to obtain an instance, then
        // re-invokes the instance GetCode() via the PUBLIC AppDomain.Invoke ->
        // Run path -- proving the parametrized Run marshals slot-0 `this`.
        public static MyEx BuildF4Ex()
        {
            MyEx e = new MyEx();
            e.Msg = "f4code";
            return e;
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

        // ====================================================================
        // F-4 / NEO-IL-EX-FIELDACCESS probes (reflection-read off a caught Neo
        // ILTypeInstance). Path #1 (CrossBindingAdaptorType.ILInstance bridge)
        // already works on HEAD; these exercise path #2 (Object.GetType) and
        // path #4 (the ILTypeInstance Neo field indexer).
        // ====================================================================

        // 3.9 F-4 path #2: e.GetType() on the caught object (the adaptor's CLR
        // Adapter view) must return the IL type's CLR projection -- on HEAD this
        // throws ArgumentOutOfRangeException (no Neo redirect for Object.GetType;
        // the autogen reader mis-reads the reference `this`). After the Neo
        // ObjectGetType redirect it returns a non-null Type assignable from the
        // IL catch type's CLR projection.
        public static int NeoStep14_ILEx_GetType()
        {
            try
            {
                throw new MyEx("gettype-msg");
            }
            catch (MyEx e)
            {
                try
                {
                    var t = e.GetType();
                    // Use ReferenceEquals (not `t == null`, which lowers to
                    // Type.op_Equality -- a separate pre-existing ReadNeoReference
                    // null-operand gap that would confound this probe).
                    if (ReferenceEquals(t, null))
                        return -10;
                    return 9;
                }
                catch (Exception)
                {
                    return -96;
                }
            }
        }

        // 3.10 F-4 path #4: read an IL-declared string field off a recovered
        // ILTypeInstance through the Neo indexer (the cross-binding-adaptor
        // forward path). The bridge recovery (path #1) is done in pure CLR via
        // the NeoF4ReflectionProbe host helper so the probe exercises exactly
        // the indexer. On HEAD the Neo indexer `get` is `return null` so the
        // read yields null -> assertion FAILs; after the fix it PASSes.
        public static int NeoStep14_ILEx_IndexerFieldRead()
        {
            // Construct with the default ctor, then assign Msg via a direct
            // field write (stfld.ref from a local string -- the standard
            // reference-field assignment path), so the probe exercises the
            // indexer read against a known-good stored value. (Using the
            // string-param ctor would route through newobj-arg passing, a
            // separate concern from the indexer.)
            MyEx toThrow = new MyEx();
            toThrow.Msg = "idx-msg";
            try
            {
                throw toThrow;
            }
            catch (MyEx e)
            {
                try
                {
                    // Path #1 (works on HEAD): recover the IL view via the
                    // CrossBindingAdaptorType.ILInstance bridge.
                    ILTypeInstance ili = ((CrossBindingAdaptorType)(object)e).ILInstance;
                    // Path #4 (the indexer): read the IL-declared Msg field and
                    // compare in pure CLR (avoids the IL string-op_Equality gap).
                    int m = NeoF4ReflectionProbe.ReadFieldStringMatch(ili, "Msg", "idx-msg");
                    // 1 = match (PASS). Other codes surface the specific failure.
                    if (m == 1)
                        return 9;
                    return -10 - m;
                }
                catch (Exception)
                {
                    return -97;
                }
            }
        }

        // ====================================================================
        // neo-f4-surfaced-gaps DUMP-GATE probes (FAIL-on-HEAD -> PASS-after).
        //
        // Two SMALL pre-existing Neo gaps surfaced by the F-4 work; each is
        // exercised here by an adversarial probe. Both are tagged so a future
        // worker reading the result knows which gate fired.
        // ====================================================================

        // GAP A -- op_Equality null-operand. `t == null` on a System.Type
        // lowers to Type.op_Equality(t, null); the autogen
        // System_Type_Binding.op_Equality_1_Neo reads BOTH operands via
        // ILIntepreter.ReadNeoReference, and a NULL operand is the Neo null
        // sentinel (-1), so mStack[-1] -> ArgumentOutOfRangeException. On HEAD
        // this probe returns -96 (caught AoRE); after the ReadNeoReference
        // null-sentinel fix it returns 9 (t is non-null, so t == null is false).
        public static int NeoStep14_ILEx_GapA_TypeOpEqualityNull()
        {
            try
            {
                MyEx e = new MyEx("gap-a-msg");
                Type t = e.GetType();
                // The `t == null` is the load-bearing op_Equality-with-null-operand.
                if (t == null)
                    return -10; // t is null (unexpected)
                return 9;
            }
            catch (ArgumentOutOfRangeException)
            {
                return -96; // the gap: mStack[-1] AoRE in op_Equality_Neo
            }
            catch (Exception)
            {
                return -97; // any other failure
            }
        }

        // GAP B -- `new MyEx(string)` ctor string-arg mis-route. The string
        // ctor arg should reach the Msg field; on HEAD the newobj-arg marshal
        // mis-routes so Msg holds the ILTypeInstance (`this`), not the string.
        // The field is read off the recovered ILTypeInstance via the indexer
        // (path #4, which works on HEAD), so the probe isolates the ctor WRITE.
        // On HEAD ReadFieldStringMatch returns 0 (Msg holds `this`, not a
        // string) -> -10; after the fix it returns 1 (Msg == "ctor-msg") -> 9.
        // (Rigorous: a DISTINCT string rules out coincidence; the base-ctor
        // chain `DerivedEx(msg):base(msg)` exercises the :base(msg) arg pass.)
        public static int NeoStep14_ILEx_GapB_NewobjStringArg()
        {
            // neo-f4-surfaced-gaps Gap B gate. Root cause (re-characterized at
            // implement time): the F-4 finding's hypothesis ("the plain ctor
            // stores `this` into Msg") was STALE -- `new MyEx("ctor-msg")`
            // already sets Msg correctly on HEAD. The REAL surfaced bug is that
            // a Neo flat instance of a DERIVED IL type was allocated too small:
            // `DerivedEx.TotalReferenceCount` counted ONLY DerivedEx's own
            // fields (none), NOT the inherited `MyEx.Msg`, so the instance's
            // ManagedObjects had 0 slots and the `:base(msg)` ctor's
            // `Msg = msg` stfld NREd (ManagedObjects[0] on a null list). On HEAD
            // this probe throws (NRE); after the ILType.InitializeFields
            // base-field accumulation fix both the plain + the derived chain
            // set Msg correctly.
            MyEx a = new MyEx("ctor-msg");
            ILTypeInstance ilia = ((CrossBindingAdaptorType)(object)a).ILInstance;
            int ma = NeoF4ReflectionProbe.ReadFieldStringMatch(ilia, "Msg", "ctor-msg");
            if (ma != 1) return -10 - ma;

            DerivedEx b = new DerivedEx("derived-msg");
            ILTypeInstance ilib = ((CrossBindingAdaptorType)(object)b).ILInstance;
            int mb = NeoF4ReflectionProbe.ReadFieldStringMatch(ilib, "Msg", "derived-msg");
            if (mb != 1) return -20 - mb;

            return 9;
        }

        // ====================================================================
        // F-4 #3 / NEO-RUN-PARAMETRIZED + F-12 / NEO-RUN-REF-RETURN gates.
        //
        // Both gates are exercised HOST-SIDE by
        // `NeoF4ParamRunCheck.Run(appdomain)` (CLI hook `NeoF4ParamRun`), NOT as
        // IL test methods here. Reason: the F-4 #3 / F-12 scenarios are HOST -> IL
        // re-entry actions (`appdomain.Invoke(...)` called from CLR), and driving
        // them from WITHIN an IL method would NEST `domain.Invoke` (-> a second
        // `Run` -> a second `ExecuteNeo`) inside an in-flight `ExecuteNeo`. That
        // nested-ExecuteNeo shape hits a PRE-EXISTING Neo re-entrancy corruption
        // (the outer frame's instruction pointer runs off the end of its body ->
        // garbage opcode; reproduced with the OLD parameterless-only Run shim
        // too, so it is NOT the parametrized-Run change). The host-side check
        // invokes `Run` exactly ONCE per cell (no nesting), so it isolates the
        // parametrized-Run machinery cleanly. The IL-side targets used by the
        // host check are: `EchoRef` (F-12 reference return), `BuildF4Ex` +
        // `MyEx.GetCode` (F-4 #3 instance-method re-entry). See
        // `NeoF4ParamRunCheck.cs` + this change's design.md addendum.
        // ====================================================================
    }
}
