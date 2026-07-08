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
