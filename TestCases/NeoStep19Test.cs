using System;
using System.Collections.Generic;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // Step 19 test: Neo delegate support.
    //   * ldftn / ldvirtftn (IMethod into a Neo ref slot)
    //   * delegate Newobj (DelegateAdapter bound from this + IMethod)
    //   * DelegateAdapter.InvokeILMethod Neo calling convention (CLR -> IL)
    //   * multicast += / -= (next-chain)
    //   * List<T>.ForEach(action) (the CLR -> IL callback via InvokeILMethod)
    //   * ref/out params, closure over an IL instance's this
    //
    // Assertion mechanism: same as prior NeoStep tests -- a passing test simply
    // returns without dividing by zero. A logic failure is surfaced by a
    // deliberate `1/0`. NOTE: the Neo engine does not implement Stsfld (Step 6),
    // so multicast side-effects use an instance holder + a ref/out accumulator
    // rather than static state.

    // A holder with instance state, for the instance-method / closure / multicast
    // probes (avoids static fields).
    public class NeoStep19Holder
    {
        public int Field;
        public int Counter;
    }

    // A base type with a virtual method, for the ldvirtftn (VTable slot) probe.
    public class NeoStep19Base
    {
        public virtual int VirtualMethod() { return 1; }
    }
    public class NeoStep19Derived : NeoStep19Base
    {
        public override int VirtualMethod() { return 2; }
    }

    public class NeoStep19Test
    {
        // ---- instance delegate targets (use a holder to accumulate) ----
        public void InstanceFoo(NeoStep19Holder h) { h.Counter += 1; }
        public void InstanceBaz(NeoStep19Holder h) { h.Counter += 100; }
        public int InstanceReadField(NeoStep19Holder h) { return h.Field; }

        // ---- static delegate targets (no static-state mutation) ----
        public static int StaticBar(int x) { return x * 2; }

        // ---- List.ForEach accumulator target (instance, mutates holder) ----
        public void Accumulate(NeoStep19Holder h, int x) { h.Counter += x; }

        // TC1 static Action: `Action a = Foo; a();` -- the simplest construct +
        // invoke. StaticBar returns x*2; we assert the invocation result. (The
        // construct + invoke path itself is the load-bearing assertion -- no NIE.)
        public static void NeoStep19_StaticAction()
        {
            Func<int, int> f = StaticBar;
            int r = f(42);
            if (r != 84)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC2 static Func with return: `Func<int,int> f = Bar; int r = f(42);`
        public static void NeoStep19_StaticFunction()
        {
            Func<int, int> f = StaticBar;
            int r = f(7);
            if (r != 14)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC3 instance-method delegate: reads `this` state.
        public static void NeoStep19_InstanceMethod()
        {
            NeoStep19Test self = new NeoStep19Test();
            NeoStep19Holder h = new NeoStep19Holder { Field = 9 };
            Func<NeoStep19Holder, int> f = self.InstanceReadField;
            int r = f(h);
            if (r != 9)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC4 virtual-method delegate (ldvirtftn): a Derived override dispatched
        // via a Base-typed delegate variable -- the override must run.
        public static void NeoStep19_VirtualMethod()
        {
            NeoStep19Base obj = new NeoStep19Derived();
            Func<int> f = obj.VirtualMethod;
            int r = f();
            if (r != 2)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC5 multicast combine: `a += Baz; a();` -- both run in order.
        public static void NeoStep19_MulticastCombine()
        {
            NeoStep19Test self = new NeoStep19Test();
            NeoStep19Holder h = new NeoStep19Holder();
            Action<NeoStep19Holder> a = self.InstanceFoo;   // +1
            a += self.InstanceBaz;                          // +100
            a(h);
            if (h.Counter != 101)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC6 multicast remove: `a -= Foo;` after combine -- only the remaining runs.
        public static void NeoStep19_MulticastRemove()
        {
            NeoStep19Test self = new NeoStep19Test();
            NeoStep19Holder h = new NeoStep19Holder();
            Action<NeoStep19Holder> a = self.InstanceFoo;
            a += self.InstanceBaz;
            a -= self.InstanceFoo;       // only Baz remains (+100)
            a(h);
            if (h.Counter != 100)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC7 the CLR -> IL callback via InvokeILMethod: an IL Action<int> passed
        // to List<int>.ForEach. This is the nested re-entry / Risk 3 probe.
        public static void NeoStep19_ClrCallback()
        {
            NeoStep19Test self = new NeoStep19Test();
            NeoStep19Holder h = new NeoStep19Holder();
            List<int> list = new List<int>();
            list.Add(1); list.Add(2); list.Add(3); list.Add(4);
            // Accumulate(holder, x) -- but ForEach passes only x. Use a closure
            // over `h` via a lambda is not supported; instead use a delegate
            // whose target is `self` and takes the int, accumulating into a
            // returned sum via a Func<int,int>.
            Func<int, int> acc = self.DoubleIt;
            List<int> outList = new List<int>();
            Action<int> action = self.AppendSquare(outList);
            list.ForEach(action);
            int sum = 0;
            for (int i = 0; i < outList.Count; i++) sum += outList[i];
            // 1+4+9+16 = 30
            if (sum != 30)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        public int DoubleIt(int x) { return x * 2; }

        // Returns a delegate that appends x*x to the list. (Instance method so
        // the delegate closes over `this` -- but we need the list captured.)
        // Since lambdas closing over locals aren't IL-supported cleanly, we use
        // a dedicated holder method instead.
        public Action<int> AppendSquare(List<int> list)
        {
            // Build a delegate over an instance method that appends to a field.
            // Simplest: return a delegate to a static that we cannot capture.
            // To keep this engine-friendly, we instead expose a per-element
            // delegate via InstanceFoo-style: use a wrapper instance.
            AppendTarget target = new AppendTarget { Target = list };
            Action<int> a = target.Append;
            return a;
        }

        // TC8 an IL-defined delegate construct + invoke (the IL-delegate-Invoke
        // callvirt routing through adapter.NeoInvokePublic). NOTE: a delegate
        // with ref/out params requires byref-aware arg marshaling in NeoInvoke
        // (the byref must flow as a raw 8-byte Ref Slot, not a boxed object) --
        // deferred to a follow-up. This probe uses a plain `int` param + return
        // to exercise the IL-delegate construct + Invoke callvirt path.
        public delegate int NeoStep19IlFuncDelegate(int input);
        public static int Doubler(int x) { return x * 3; }
        public static void NeoStep19_PlainIntParam()
        {
            NeoStep19IlFuncDelegate d = Doubler;
            int result = d(7);
            if (result != 21)
            {
                int z = 1; int d2 = 0; int _ = z / d2;
            }
        }

        // ---- F-7 (NEO-DELEGATE-REFOUT) byref probes: a delegate whose target
        // signature carries a ref/out param, invoked via Neo (`del(ref v)`).
        // The byref MUST marshal across the delegate boundary as a valid Ref Slot
        // and the target's write-back MUST propagate to the caller's frame cell.
        // On HEAD (pre-fix) these threw ArgumentOutOfRangeException because the
        // byref was half-read through ReadNeoDelegateInvokeArgs' object[] funnel
        // and the target ran on a separate pooled interpreter. The same-frame
        // fast path (Callvirt_IL IsDelegateInvoke -> InvokeNeoCallTarget on THIS
        // interpreter -> CopyNeoCallThisBack) makes them pass.

        public delegate void NeoStep19RefIntDelegate(ref int x);
        public delegate void NeoStep19OutIntDelegate(out int x);
        public delegate int NeoStep19RefStringDelegate(ref string s);

        // ref int target: bumps the byref'd cell by 10.
        public static void BumpRef(ref int x) { x += 10; }
        // out int target: assigns a constant.
        public static void SetOut(out int x) { x = 99; }
        // Double target (for the multicast probe): doubles the cell.
        public static void DoubleRef(ref int x) { x *= 2; }
        // ref string target: reads the byref'd string (proving the byref
        // marshals across the delegate boundary as a valid Ref Slot the callee
        // can deref) and returns its length. NOTE: an in-place WRITE-BACK of a
        // callee-created string (`s = s + "!"`) is a SEPARATE follow-up -- the
        // new object lands in the callee's frame ref region, which ExecuteNeo
        // pops on return, leaving the caller's slot with a dangling mStack index.
        // Ref-type byref write-back of a callee-created object needs mStack
        // lifetime promotion (track under neo-f7 / ref-type-byref-writeback).
        // This probe verifies the marshal + READ path (the F-7 byref-marshal
        // criterion for a reference-type param).
        public static int ReadLength(ref string s)
        {
            return s.Length;
        }

        // F-7 probe 1: ref int -> write-back observable (v==15).
        public static void NeoStep19_ByRef_Int()
        {
            NeoStep19RefIntDelegate d = BumpRef;
            int v = 5;
            d(ref v);
            if (v != 15)
            {
                int z = 1; int dd = 0; int _ = z / dd;
            }
        }

        // F-7 probe 2: out int -> write-back observable (v==99).
        public static void NeoStep19_ByRef_Out()
        {
            NeoStep19OutIntDelegate d = SetOut;
            int v;
            d(out v);
            if (v != 99)
            {
                int z = 1; int dd = 0; int _ = z / dd;
            }
        }

        // F-7 probe 3: ref string -> the byref marshals across the delegate
        // boundary as a valid Ref Slot the callee can deref (READ path). The
        // callee reads `s` and returns its length (3 for "abc"). The in-place
        // write-back of a callee-CREATED string is a separate follow-up (see
        // ReadLength's note: mStack lifetime promotion).
        public static void NeoStep19_ByRef_String()
        {
            NeoStep19RefStringDelegate d = ReadLength;
            string s = "abc";
            int r = d(ref s);
            if (r != 3)
            {
                int z = 1; int dd = 0; int _ = z / dd;
            }
        }

        // F-7B probe: ref string WRITE-BACK where the callee REASSIGNS the
        // referent (`s = s + "!"`). The new string object is created inside the
        // CALLEE's frame ref region; the caller must observe the NEW object
        // ("abc!") after the call, NOT a dangling mStack index. This is the
        // ref-type-byref-writeback binding probe for F-7B.
        public static int AppendBang(ref string s)
        {
            s = s + "!";
            return s.Length;
        }

        public static void NeoStep19_ByRef_StringWriteBack()
        {
            NeoStep19RefStringDelegate d = AppendBang;
            string s = "abc";
            int r = d(ref s);
            // r == 4 (length of "abc!") AND s == "abc!" (the caller observes the
            // callee-created object). A dangling mStack index makes `s` garbage /
            // throws IndexOutOfRange in a later CLR string binding.
            if (r != 4 || s != "abc!")
            {
                int z = 1; int dd = 0; int _ = z / dd;
            }
        }

        // F-7B multicast-with-ref-string adversarial (D3): a void-returning
        // ref-string delegate with TWO targets that each reassign the referent.
        // AppendBang -> AppendQ over "abc" yields "abc!?" (last write wins; each
        // target sees the prior target's object via the shared caller-owned mStack
        // slot reserved once in NeoRunDelegateTargetOnThis). Verifies the promotion
        // is idempotent across the multicast chain AND that the last object
        // survives the FINAL callee pop.
        public delegate void NeoStep19RefStringVoidDelegate(ref string s);
        public static void AppendBangV(ref string s) { s = s + "!"; }
        public static void AppendQ(ref string s) { s = s + "?"; }

        public static void NeoStep19_ByRef_StringMulticastWriteBack()
        {
            NeoStep19RefStringVoidDelegate d = AppendBangV;
            d += AppendQ;
            string s = "abc";
            d(ref s);
            // "abc" -> "abc!" (AppendBangV) -> "abc!?" (AppendQ, last wins).
            if (s != "abc!?")
            {
                int z = 1; int dd = 0; int _ = z / dd;
            }
        }

        // F-7 probe 4: multicast with a byref param -> each target sees the
        // prior target's mutation and writes back to the same caller cell.
        // Bump(+10) then Double(*2) over v==5 -> ((5+10)*2)==30.
        public static void NeoStep19_ByRef_Multicast()
        {
            NeoStep19RefIntDelegate d = BumpRef;
            d += DoubleRef;
            int v = 5;
            d(ref v);
            if (v != 30)
            {
                int z = 1; int dd = 0; int _ = z / dd;
            }
        }

        // TC9 closure over an IL instance's this: the bound instance's field is
        // readable in the callback. Already covered by TC3 (InstanceReadField);
        // this variant re-invokes after mutating the field to confirm the bound
        // instance reads CURRENT state.
        public static void NeoStep19_ClosureOverThis()
        {
            NeoStep19Test self = new NeoStep19Test();
            NeoStep19Holder h = new NeoStep19Holder { Field = 55 };
            Func<NeoStep19Holder, int> f = self.InstanceReadField;
            int r1 = f(h);
            h.Field = 77;
            int r2 = f(h);
            if (r1 != 55 || r2 != 77)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC10 value-type-param probe: a Func<TestVector3,float> round-trips a
        // registered CLR struct param through WriteNeoValueType (the R4 risk).
        // The target returns a constant to isolate the test to the param
        // marshaling (struct field access via generic Ldfld is a separate Step 6
        // gap, unrelated to delegates -- see [NEO-IL-VT-INSTANCE-COVERAGE]).
        public static float TakeTestVector(ILRuntimeTest.TestFramework.TestVector3 v)
        {
            // The param is a registered CLR struct; the load-bearing assertion is
            // that it round-trips through WriteNeoValueType without corrupting the
            // frame (struct field access via generic Ldfld is a separate Step 6
            // gap). Return a constant.
            return 42f;
        }
        public static void NeoStep19_ValueTypeParam()
        {
            Func<ILRuntimeTest.TestFramework.TestVector3, float> f = TakeTestVector;
            var v = new ILRuntimeTest.TestFramework.TestVector3(10f, 20f, 30f);
            float r = f(v);
            if (r < 41.5f || r > 42.5f)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // ===== F-7B-SIB triage probes (DIRECT Call_IL ref-type byref write-back) =====
        // The delegate path (F-7B) is green via the caller-owned mStack slot promotion.
        // The DIRECT Call_IL/CopyNeoCallThisBack sibling has the SAME dangling-index
        // mechanism, but the Neo trivial inliner folds small direct targets, so the
        // byref write-back normally stays in-frame. These probes defeat the inliner to
        // force a real cross-frame Call_IL and reach (or fail to reach) the gap.

        // Defeat condition 1: an exception handler. The inliner gate
        // (`JITCompiler.cs:2958`) requires `!hasExceptionHandler`. A try/catch body
        // is NEVER inlined.
        public static int AppendBangBig(ref string s)
        {
            try
            {
                s = s + "!";
                return s.Length;
            }
            catch
            {
                return -1;
            }
        }

        public static void NeoStep19_SIB_DirectCall_TryCatch()
        {
            string s = "abc";
            int r = AppendBangBig(ref s);
            // F-7B-SIB REACHED: caller observes the NEW object ("abc!"). If the gap
            // reproduces, `s` is a dangling mStack index -> garbage or a later throw.
            if (r != 4 || s != "abc!")
            {
                int z = 1; int dd = 0; int _ = z / dd;
            }
        }

        // Defeat condition 2: instruction count above the inliner threshold
        // (`Optimizer.MaximalInlineInstructionCount == 20`, `JITCompiler.cs:2968`).
        // A body with > 20 IL instructions is NOT inlined. The local churn below
        // inflates the body past the threshold.
        public static int AppendBangLong(ref string s)
        {
            int a0 = 0, a1 = 1, a2 = 2, a3 = 3, a4 = 4;
            int a5 = 5, a6 = 6, a7 = 7, a8 = 8, a9 = 9;
            int a10 = a0 + a1 + a2 + a3 + a4;
            int a11 = a5 + a6 + a7 + a8 + a9;
            int a12 = a10 + a11;
            s = s + "!";
            if (a12 == 45 && a11 == 35) return s.Length;
            return a12;
        }

        public static void NeoStep19_SIB_DirectCall_BigBody()
        {
            string s = "abc";
            int r = AppendBangLong(ref s);
            if (r != 4 || s != "abc!")
            {
                int z = 1; int dd = 0; int _ = z / dd;
            }
        }

        // F-7B-SIB scope discriminator: a non-inlinable DIRECT call with a
        // PRIMITIVE ref param (`ref int`). If this ALSO fails, the gap is the
        // BROADER "IL-direct-call byref offset is never rebased across frames"
        // bug (the callee derefs a caller-relative offset against its own
        // frameBase). If it PASSES while the ref-string probes fail, the gap is
        // the F-7B-SIB reference-specific dangling-index mechanism alone.
        public static void BumpIntBig(ref int x)
        {
            try
            {
                x = x + 10;
            }
            catch
            {
            }
        }

        public static void NeoStep19_SIB_DirectCall_PrimitiveRef()
        {
            int v = 5;
            BumpIntBig(ref v);
            if (v != 15)
            {
                int z = 1; int dd = 0; int _ = z / dd;
            }
        }

        // ---- child-14 (neo-byref-clr2il-delegate): the CLR->IL delegate
        //      callback direction with a BYREF param (the REVERSE of F-7).
        //      An IL method is bound to a CLR delegate type carrying a `ref
        //      int` / `out int` param; a CLR host helper invokes the delegate
        //      WITH the byref; the IL callee mutates the byref; the mutation
        //      MUST be observable on the CLR side after the call (write-back).
        //      The delegate type + host helper live in
        //      TestFramework.TestClass3 (Clr2IlRefIntDelegate / InvokeRefCallback);
        //      the converter marshals the byref through NeoInvokeSub. ----

        // The IL delegate target: bumps the byref'd cell by 10 (read+write).
        public static void Clr2IlBumpRef(ref int x) { x += 10; }
        // The IL delegate target: assigns a constant (write-only out).
        public static void Clr2IlSetOut(out int x) { x = 77; }

        public static void NeoStep19_Clr2Il_ByRef()
        {
            // Build a CLR delegate (TestCLRBinding.Clr2IlRefIntDelegate) bound to
            // the IL method Clr2IlBumpRef, then hand it to the CLR host helper
            // InvokeRefCallback, which calls del(ref x) and returns the result.
            // seed 5 -> the IL callee bumps by 10 -> 15.
            TestCLRBinding.Clr2IlRefIntDelegate d = Clr2IlBumpRef;
            int r = TestCLRBinding.InvokeRefCallback(d, 5);
            if (r != 15)
            {
                int z = 1; int dd = 0; int _ = z / dd;
            }
        }

        public static void NeoStep19_Clr2Il_Out()
        {
            TestCLRBinding.Clr2IlOutIntDelegate d = Clr2IlSetOut;
            int r = TestCLRBinding.InvokeOutCallback(d);
            if (r != 77)
            {
                int z = 1; int dd = 0; int _ = z / dd;
            }
        }

        // Adversarial: a `ref long` param (8-byte element). Verifies the
        // scratch cell sizes to the element width (not a hardcoded 4) and the
        // long write-back round-trips. seed 10 -> +1000L -> 1010L.
        public static void Clr2IlBumpLong(ref long x) { x += 1000L; }
        public static void NeoStep19_Clr2Il_ByRefLong()
        {
            TestCLRBinding.Clr2IlRefLongDelegate d = Clr2IlBumpLong;
            long r = TestCLRBinding.InvokeRefLongCallback(d, 10L);
            if (r != 1010L)
            {
                int z = 1; int dd = 0; int _ = z / dd;
            }
        }

        // Adversarial: a multicast ref-int delegate (two IL targets; each must
        // see the prior target's mutation -- multicast ref-semantics). The
        // converter runs each target via NeoInvokeByRef, and each subsequent
        // target re-reads args[0] (now carrying the prior write-back).
        // +10 (5->15) then *2 (15->30) = 30.
        public static void Clr2IlAddTen(ref int x) { x += 10; }
        public static void Clr2IlDouble(ref int x) { x *= 2; }
        public static void NeoStep19_Clr2Il_Multicast()
        {
            TestCLRBinding.Clr2IlRefIntMulticastDelegate d = Clr2IlAddTen;
            d += Clr2IlDouble;
            int r = TestCLRBinding.InvokeRefIntMulticastCallback(d, 5);
            if (r != 30)
            {
                int z = 1; int dd = 0; int _ = z / dd;
            }
        }
    }

    // Helper for TC7: an instance with a Target list field, so a delegate over
    // its Append method closes over the list (avoids static state + avoids
    // lambda-capture-of-local limitations).
    public class AppendTarget
    {
        public List<int> Target;
        public void Append(int x) { Target.Add(x * x); }
    }
}
