#define TEST_MISSING_METHOD
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ILRuntime.Other;

namespace ILRuntimeTest.TestFramework
{
    [NeedAdaptor]
    public class TestClass3
    {
        public TestStruct Struct;
        // neo-clr-static-fields: a writable CLR static int for the Stsfld+Ldsfld
        // primitive round-trip probe (mscorlib's statics are mostly initonly).
        public static int NeoClrStaticProbe;
        // neo-clr-static-vt-field: a writable CLR value-type static whose type
        // has a registered ValueTypeBinder (TestVector3), for the Stsfld+Ldsfld
        // blittable-binder round-trip probe. Initialized to default (do NOT
        // stomp TestVector3.One).
        public static TestVector3 NeoClrVtStaticProbe;
        // neo-raw-stfld-ldfld: a writable CLR INSTANCE int for the raw Stfld+Ldfld
        // primitive round-trip probe (the field's declaring type is this CLR class,
        // so the Neo typed-splitter leaves the raw opcode; ExecuteNeo handles it).
        public int NeoClrInstProbe;
        public static string getString(int startIndex = 0, int length = -1)
        {
            throw new Exception();
        }

        public static string getString(ref int startIndex, int length = -1)
        {
            startIndex++;
            return startIndex.ToString() + length;
        }

        public static void setBit(ref byte value, int pos, int bit) { value = (byte)(value & ~(1 << pos) | (bit << pos)); }
    }

    [NeedAdaptor]
    public class TestClass4
    {
        protected int a;
        protected int b;
        protected TestClass2 cls2;

        public virtual void KKK()
        {
            a = 1;
            b = 2;
        }

        public void TestArrayOut(out TestStruct[] arr)
        {
            arr = new TestStruct[10];
        }
    }

    public struct TestStruct
    {
        public static TestStruct instance;
        public Action<int> testField;
        public int value;
        public static void DoTest(ref TestStruct a)
        {
            a.value = 11111;
        }

        public static void DoTest(ref int a)
        {
            a = 22222;
        }

        public static void DoTest2(TestStruct aaa)
        {
            aaa.value = 232425235;
        }

        public static int Add(int a, int b)
        {
            return a + b;
        }
    }
    public class TestHashMap<TKey, TValue>
    {
        private System.Collections.Generic.Dictionary<TKey, TValue> dic;
        public TestHashMap()
        {
            dic = new System.Collections.Generic.Dictionary<TKey, TValue>();
        }
        public TValue this[TKey key]
        {
            get => dic[key];
            set => dic[key] = value;
        }

        public System.Collections.Generic.IEnumerator<System.Collections.Generic.KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            return dic.GetEnumerator();
        }

        public bool Add(TKey key, TValue value)
        {
            dic.Add(key, value);
            return true;
        }
    }

    public class TestCLRBinding
    {
        public static int ValidateNeoSmallPrimitiveArgs(byte b, sbyte sb, short s, ushort us, bool flag, char ch, int tail)
        {
            return b + sb + s + us + (flag ? 1000 : 0) + ch + tail;
        }

        // ---- Step 13b K2/K2-FAM helpers (host C#; read by the IL side via the
        //      reflection fallback CLRMethod.Invoke(byte*) -- no autogen redirect
        //      for these). Taking a CLR struct BY VALUE and returning a primitive
        //      verifies the param bytes cross IL->CLR correctly without needing
        //      IL-side ldfld on CLR struct fields (a separate deferred concern).
        //      These mirror the K2 reproducer shape. ----

        // K2: a CLR struct by-value PARAMETER (no binder). Returns the int sum
        // of the three float fields as a check the struct's flat bytes arrived
        // unchanged. Before 13b the caller-temp-slot fallback miscopied the
        // boxed-ref mStack index into the param region (K2).
        public static int SumTestVector3NoBindingFields(TestVector3NoBinding v)
        {
            return (int)(v.x + v.y + v.z);
        }

        // K2: a CLR struct by-value PARAMETER (WITH binder). TestVector3 has a
        // registered ValueTypeBinder (pure-primitive, 3 floats).
        public static int SumTestVector3Fields(TestVector3 a, TestVector3 b)
        {
            return (int)(a.X + a.Y + a.Z + b.X + b.Y + b.Z);
        }

        // ---- neo-callvirt-clr-struct-arg host helpers. Read list[0] / all X
        //      ENTIRELY in the host (CLR) to verify the struct ARG marshalling
        //      to a CLR-generic instance method (List<TestVector3>.Add) delivered
        //      the real struct, not a zero. Sidesteps any IL-side ldfld/conv.i4
        //      read bug. The list itself is a reference (mStack index) so passing
        //      it by value is unaffected. ----
        public static int HostSumListTestVector3First(List<TestVector3> list)
        {
            if (list == null || list.Count == 0) return -1000;
            var v = list[0];
            return (int)(v.X + v.Y + v.Z);
        }
        public static int HostSumListTestVector3AllX(List<TestVector3> list)
        {
            if (list == null) return -1000;
            float s = 0;
            for (int i = 0; i < list.Count; i++) s += list[i].X;
            return (int)s;
        }
        public static int HostListIntFirst(List<int> list)
        {
            if (list == null || list.Count == 0) return -1000;
            return list[0];
        }

        // K2 (return side): a CLR struct RETURN value. The reflection return
        // path (InvokeNeoClrMethod) must write the struct's flat bytes into the
        // caller's dest local. Returns a known struct; the IL caller checks it
        // by re-feeding it to Sum... above (no IL-side ldfld needed).
        public static TestVector3NoBinding MakeTestVector3NoBinding(float x, float y, float z)
        {
            return new TestVector3NoBinding(x, y, z);
        }

        // ---- F-10 / NEO-CLRSTRUCT-FIELD-OF-IL host helpers. These are CLR
        //      methods (host assembly) so ILRuntime's trivial inliner CANNOT
        //      fold them -- the `ref c.field` lowering reaches a REAL `call`
        //      with the ldflda-produced byref, exercising CopyNeoCallArguments
        //      -> NeoMarshalByrefFieldToSlot (the Step-20 builder-byref hot
        //      path). Field reads happen on the HOST side (no IL-side `ldfld` on
        //      a CLR struct field -- the separate Step-6 gap, avoided). ----

        // F-10: sum a CLR struct passed BY REF. The IL caller lowers
        // `SumTestVector3NoBindingByRef(ref c.field)` to `ldflda c.field; call`.
        public static int SumTestVector3NoBindingByRef(ref TestVector3NoBinding v)
        {
            return (int)(v.x + v.y + v.z);
        }

        // F-10: a MUTATING byref helper (the Area-4a write-back shape). The
        // mutation must propagate through the F-10 byref write-back to the IL
        // instance's ManagedObjects slot.
        public static void MutateTestVector3NoBindingByRef(ref TestVector3NoBinding v, float dx)
        {
            v.x += dx; v.y += dx; v.z += dx;
        }

        // F-10-R1: a byref SETTER (seeds the field THROUGH the ldflda-produced
        // byref). Used by the IL-VT-with-CLR-struct-field latent probe to
        // initialize the field via the byref write-back path (the F-10-R1 shape)
        // rather than via `stfld` (a separate Step-6 gap for IL-VT stfld).
        public static void SetTestVector3NoBindingByRef(ref TestVector3NoBinding v, float x, float y, float z)
        {
            v.x = x; v.y = y; v.z = z;
        }

        // F-10: a CLR struct WITH a reference-type field (the TaskAwaiter shape),
        // passed BY REF. Verifies the boxed struct's reference field survives the
        // F-10 byref round-trip.
        public static int SumTestClrStructWithRefByRef(ref TestClrStructWithRef v)
        {
            return v.n + (v.s != null ? v.s.Length : 0);
        }
        public static TestClrStructWithRef MakeTestClrStructWithRef(int n, string s)
        {
            return new TestClrStructWithRef(n, s);
        }

        // ---- neo-raw-stfld-array-element host read-back helper. The READ-BACK
        //      happens on the CLR (host) side, so the probe verifies the Neo
        //      `arr[i].field = x` Stfld WRITE landed in the array element WITHOUT
        //      depending on Neo Ldfld (deferred for array-element owners) or Neo
        //      float arithmetic (the pre-existing addi/conv.i4 float bugs). ----
        public static int NeoArrElemFieldSum(NeoArrElemIntProbe[] arr, int i)
        {
            return arr[i].A + arr[i].B;
        }

        // ---- neo-raw-ldfld-array-element host-built-array helper (TEMP probe).
        //      Builds and populates a 2-element array on the HOST side so the IL
        //      probe depends ONLY on the raw Ldfld array-element READ (no IL Stfld
        //      write). arr[0]={a0,b0}, arr[1]={a1,b1}. ----
        public static NeoArrElemIntProbe[] BuildNeoArrElemProbeArray(int a0, int b0, int a1, int b1)
        {
            return new NeoArrElemIntProbe[]
            {
                new NeoArrElemIntProbe { A = a0, B = b0 },
                new NeoArrElemIntProbe { A = a1, B = b1 },
            };
        }

        // ---- neo-stelem-any-vt-element host read-back helper. The READ-BACK
        //      happens on the CLR (host) side (arr[i].X + arr[i].Y + arr[i].Z),
        //      so the probe verifies the Neo `stelem.any TestVector3` WRITE landed
        //      the correct struct bytes into the array element WITHOUT depending
        //      on Neo Ldelem or Neo float arithmetic (the pre-existing float bugs).
        //      Sums the X/Y/Z of elements i and j. ----
        public static int SumTestVector3ArrayElems(TestVector3[] arr, int i, int j)
        {
            return (int)(arr[i].X + arr[i].Y + arr[i].Z + arr[j].X + arr[j].Y + arr[j].Z);
        }

        // ---- neo-raw-stfld-clr-object-vt-field host read-back helper. The
        //      READ-BACK happens on the CLR (host) side (o.S.a + o.S.b + o.S.c),
        //      so the probe verifies the Neo `owner.S.field = x` raw Stfld WRITE
        //      persisted to the containing CLR object's struct field WITHOUT
        //      depending on Neo Ldfld (the read sibling, deferred) or Neo float
        //      arithmetic. ----
        public static int NeoClrObjVtFieldProbeSum(NeoClrObjVtFieldOwner o)
        {
            return o.S.a + o.S.b + o.S.c;
        }

        // ---- neo-raw-ldfld-clr-object-vt-field host SETUP helper. The STRUCT
        //      FIELD VALUES are written on the CLR (host) side so an IL probe can
        //      exercise the Neo `x = owner.S.field` raw Ldfld READ WITHOUT
        //      depending on the Neo raw Stfld WRITE (child-27, the sibling) or on
        //      Neo float arithmetic. This isolates the READ path under test: the
        //      probe only reads, the host pre-populates the struct field. ----
        public static NeoClrObjVtFieldOwner BuildNeoClrObjVtFieldOwner(int a, int b, int c)
        {
            return new NeoClrObjVtFieldOwner { S = new NeoClrObjVtFieldProbe { a = a, b = b, c = c } };
        }

        // ---- neo-float-vtreturn-opaddition DIAGNOSTIC helpers. Read-back + assert
        //      happen on the CLR (host) side so the probes do not depend on Neo
        //      float arithmetic (addi/conv.i4 bit-reinterpret). NeoAssertEq throws
        //      with the ACTUAL value in the message so a diagnosis run reveals the
        //      real computed sum (not just pass/fail). SumTestVector3ArrElem reads
        //      the array element on the HOST side (sidesteps the separately-broken
        //      ldelem.any CLR-struct-array path). ----
        public static int SumTestVector3ArrElem(TestVector3[] arr, int i)
        {
            return (int)(arr[i].X + arr[i].Y + arr[i].Z);
        }
        // Host-built array so an IL probe can exercise `arr[0] += TestVector3.One`
        // (ldobj + op_Addition + stobj) WITHOUT depending on the separately-broken
        // stelem.any/ldelem.any CLR-struct-array paths (child-26 noted). arr[0]=(1,1,1).
        public static TestVector3[] BuildTestVector3OneArray()
        {
            return new TestVector3[] { TestVector3.One };
        }
        public static void NeoAssertEq(int actual, int expected, string label)
        {
            if (actual != expected)
                throw new Exception("[NeoDiag " + label + "] actual=" + actual + " expected=" + expected);
        }

        // ---- neo-byref-array-element-marshal host helpers. IL lowers
        //      `M(ref arr[i])` to `ldelema <T>; call M` -- the byref routes through
        //      CopyNeoCallArguments -> NeoMarshalByrefFieldToSlot (forward) and
        //      CopyNeoCallThisBack -> NeoMarshalByrefFieldToSlot (write-back).
        //      These CLR helpers are HOST methods (the trivial inliner cannot fold
        //      them), so a REAL `call` marshals the array-element byref. The
        //      mutations run host-side (native arithmetic -- sidesteps the
        //      unrelated Neo addi/conv.i4 float bugs). ----

        // TC1: mutate a primitive `byte` element by reference.
        public static void NeoByrefArrElemIncrementByte(ref byte b)
        {
            b = (byte)(b + 5);
        }

        // TC2: mutate a primitive `int` element by reference.
        public static void NeoByrefArrElemIncrementInt(ref int v)
        {
            v += 100;
        }

        // TC3: mutate a CLR struct (TestVector3) element by reference. Returns the
        // INCOMING X+Y+Z sum BEFORE the mutation, so a probe that calls this TWICE
        // on the same element can prove the write-back persisted: the second call
        // observes the first call's mutation. (Reading a struct array element IL-
        // side requires ldelema+ldobj, a separate unimplemented path -- the twice-
        // call pattern isolates the byref marshal without that dependency.)
        public static int NeoByrefArrElemMutateVectorAndReturnIncomingSum(ref TestVector3 v)
        {
            int sum = (int)(v.X + v.Y + v.Z);
            v.X += 1000f;
            v.Y += 2000f;
            v.Z += 3000f;
            return sum;
        }

        // TC3: build a 1-element TestVector3[] on the HOST side with a known
        // initial value (7, 70, 700) so the twice-call probe does not depend on
        // Neo newarr zero-init semantics for a struct array.
        public static TestVector3[] BuildNeoByrefVectorArray()
        {
            return new TestVector3[] { new TestVector3(7f, 70f, 700f) };
        }

        // ---- Step 20 async-void side-effect box: a host-side int cell the IL
        //      async-void method writes (avoids the IL-side `stsfld` Step-6 gap;
        //      the cell is held on the host so no IL static-field store is
        //      needed). ----
        private static int s_asyncVoidCell;
        public static void SetAsyncVoidCell(int v) { s_asyncVoidCell = v; }
        public static int GetAsyncVoidCell() { return s_asyncVoidCell; }

        // ---- Step 20 deterministic-probe host cells (neo-generic-redirect-
        //      resolution / B1). The probe MUST force an await through
        //      AwaitUnsafeOnCompleted with no sync-completion race, so the
        //      awaitable is a TaskCompletionSource-backed Task whose SetResult
        //      is NEVER called before the assertion (IsCompleted is deterministi-
        //      cally false). Held on the host so the IL side needs no
        //      TaskCompletionSource CLR-construction binding (Option A wiring).
        //      The fault inspectors do the AggregateException unwrap + message
        //      check host-side (avoids needing Task.Exception/AggregateException
        //      redirects in the interpreter). ----

        // B1 / neo-async-movenext-fix: an incomplete Task<int> the async probe
        // awaits (its TaskAwaiter_T_GetIsCompleted_Neo returns false -> the await
        // falls through to AwaitUnsafeOnCompleted<TA,TSM>). The TCS is MUTABLE and
        // self-resetting: CompleteIncompleteTask atomically swaps in a fresh
        // incomplete TCS and completes the previous one. This keeps the probe
        // deterministic across test orderings / re-runs: GetIncompleteTask always
        // returns an incomplete Task (TC10's IsCompleted diagnostic stays green
        // even when it runs after TC8 drove completion of an earlier TCS).
        private static TaskCompletionSource<int> s_incompleteTcs = new TaskCompletionSource<int>();
        public static Task<int> GetIncompleteTask() { return s_incompleteTcs.Task; }

        // neo-async-movenext-fix (TC8 redesign, design D5): DRIVE completion of the
        // deterministic probe's Task from the host side. TC8 asserts the await
        // TRULY SUSPENDED (the returned Task is NOT sync-completed), then calls this
        // to complete the owning TCS, then spin-waits for the resumed Task.Result.
        // The continuation the Neo suspend path registered (task.GetAwaiter()
        // .UnsafeOnCompleted) fires on this SetResult -> the resume runs GetResult +
        // continues + SetResult, completing the bridge Task the test observes. The
        // swap ensures the NEXT GetIncompleteTask returns a fresh incomplete Task.
        public static void CompleteIncompleteTask(int value)
        {
            TaskCompletionSource<int> current = System.Threading.Interlocked.Exchange(
                ref s_incompleteTcs, new TaskCompletionSource<int>());
            current.SetResult(value);
        }

        // neo-async-multi-await (TC12): a SECOND independent self-resetting
        // TaskCompletionSource<int>. A single shared TCS cannot back TWO
        // simultaneous incomplete awaits -- completing it swaps in a fresh one,
        // so the SM's FIRST await operand goes stale (it pointed at the now-
        // completed/old TCS.Task). TC12's multi-await SM awaits TWO genuinely-
        // incomplete Tasks at TWO distinct await points, so each await needs its
        // OWN independent incomplete Task. Byte-identical contract to the first
        // pair (self-resetting atomic swap -> deterministic across test order).
        private static TaskCompletionSource<int> s_incompleteTcs2 = new TaskCompletionSource<int>();
        public static Task<int> GetIncompleteTask2() { return s_incompleteTcs2.Task; }
        public static void CompleteIncompleteTask2(int value)
        {
            TaskCompletionSource<int> current = System.Threading.Interlocked.Exchange(
                ref s_incompleteTcs2, new TaskCompletionSource<int>());
            current.SetResult(value);
        }

        // neo-async-valuetask-asyncvoid (VT4): a self-resetting TaskCompletionSource<string>
        // for the ref-T ValueTask<string> suspend+resume probe. Byte-identical contract to
        // the int pair (atomic swap -> deterministic across test order). A ref-type T
        // exercises the ValueTask<T> value-type return write with T itself a reference
        // (the ValueTask<string> struct is still flat-bytes/RefCount=0; T being a ref
        // does not change the struct's Neo layout -- it only changes what the resumed
        // SetResult stashes).
        private static TaskCompletionSource<string> s_incompleteStringTcs = new TaskCompletionSource<string>();
        public static Task<string> GetIncompleteStringTask() { return s_incompleteStringTcs.Task; }
        public static void CompleteIncompleteStringTask(string value)
        {
            TaskCompletionSource<string> current = System.Threading.Interlocked.Exchange(
                ref s_incompleteStringTcs, new TaskCompletionSource<string>());
            current.SetResult(value);
        }

        // neo-async-valuetask-asyncvoid (VT5): an async-void suspend side-effect cell.
        // Reuses the existing s_asyncVoidCell, but the suspend probe writes a DISTINCT
        // value at the RESUME point (after the await) -- a sync async-void writes the
        // cell BEFORE the await (NeoStep20_AsyncVoidSync), so the resume-time write
        // proves the suspend/resume actually ran. The cell is read-back only here.
        public static int GetAsyncVoidSuspendCell() { return s_asyncVoidCell; }

        // ---- neo-async-valuetask-zeroalloc: allocation-measurement host helpers.
        //      The IL probe resets the counters before a suspend+resume, then reads
        //      the bridge counter to assert the TCS bridge was NOT allocated (the
        //      zero-alloc path returns a ValueTask<T> backed by the IValueTaskSource<T>
        //      directly). The decisive proof is the direct bridge counter; a GC byte
        //      delta would be muddied by the shared Activator box + MoveNext frame
        //      (present on both old + new paths), so it is not measured here. ----
        public static void ResetAsyncAllocCounters()
        {
            ILRuntime.Runtime.Intepreter.NeoAsyncAllocCounters.Reset();
        }
        public static int GetAsyncBridgeTaskAllocs()
        {
            return ILRuntime.Runtime.Intepreter.NeoAsyncAllocCounters.BridgeTaskAllocs;
        }
        public static int GetAsyncContextAllocs()
        {
            return ILRuntime.Runtime.Intepreter.NeoAsyncAllocCounters.ContextAllocs;
        }

        // neo-async-execctx-capture: a CUSTOM awaiter implementing INotifyCompletion
        // but NOT ICriticalNotifyCompletion -- the C# compiler lowers `await` on it
        // to AwaitOnCompleted (the EC-capturing path), not AwaitUnsafeOnCompleted. It
        // wraps a Task (the m_task field, the TaskAwaiter convention) so the Neo
        // suspend path (GetAwaiterTask) resolves the underlying Task. TC13 drives a
        // suspend/resume through this awaiter (exercises the AwaitOnCompleted path);
        // TC14 observes ExecutionContext flow via the host-side AsyncLocal below.
        public sealed class ECProbeAwaiter : System.Runtime.CompilerServices.INotifyCompletion
        {
            internal Task m_task;
            public ECProbeAwaiter(Task t) { m_task = t; }
            public bool IsCompleted { get { return m_task.IsCompleted; } }
            public void OnCompleted(Action continuation) { m_task.GetAwaiter().OnCompleted(continuation); }
            public int GetResult() { return ((Task<int>)m_task).Result; }
        }
        public sealed class ECProbeAwaitable
        {
            private readonly Task _t;
            public ECProbeAwaitable(Task t) { _t = t; }
            public ECProbeAwaiter GetAwaiter() { return new ECProbeAwaiter(_t); }
        }
        public static ECProbeAwaitable GetECProbeAwaitable() { return new ECProbeAwaitable(s_incompleteTcs.Task); }
        // Host-side AsyncLocal for the EC-flow observation (avoids AsyncLocal-from-IL
        // binding edges). SetAL before the await; GetAL in the continuation. With EC
        // capture+flow (the AwaitOnCompleted path), GetAL returns the pre-await value;
        // without EC flow it returns the default (0).
        private static System.Threading.AsyncLocal<int> s_al = new System.Threading.AsyncLocal<int>();
        public static void SetAL(int v) { s_al.Value = v; }
        public static int GetAL() { return s_al.Value; }
        // Complete the OLD s_incompleteTcs from a threadpool work item queued WITHOUT
        // flowing ExecutionContext (UnsafeQueueUserWorkItem does NOT flow EC, unlike
        // Task.Run). The await continuation then resumes on that threadpool thread
        // under a DEFAULT EC, so an AsyncLocal set on the caller thread is visible in
        // the continuation ONLY if AwaitOnCompleted captured+flowed the caller's EC.
        // (ECProbeAwaitable captured the OLD Task before this swap.)
        public static void CompleteIncompleteTaskNoECFlow(int value)
        {
            TaskCompletionSource<int> current = System.Threading.Interlocked.Exchange(
                ref s_incompleteTcs, new TaskCompletionSource<int>());
            System.Threading.ThreadPool.UnsafeQueueUserWorkItem(
                _ => current.SetResult(value), null);
        }

        // B1 verdict inspectors. Returns 1 iff `ex` is the tagged Neo async-
        // suspend NIE (outcome 3: the 2-generic-arg redirect resolved on
        // RedirectMapNeo and dispatched to the tagged deferral).
        public static int IsTaggedAsyncNIE(Exception ex)
        {
            return (ex is NotImplementedException
                    && ex.Message != null
                    && ex.Message.Contains("neo-step20-async-suspend")) ? 1 : 0;
        }

        // B1 verdict inspector for the faulted-task propagation mode: MoveNext's
        // compiler-lowered try/catch captures the thrown NIE and faults the
        // returned Task (same path TC6 exercises). Unwraps the AggregateException
        // and reuses IsTaggedAsyncNIE. Returns 1 iff the inner exception is the
        // tagged NIE; 0 for not-faulted / wrong-exception / null.
        public static int IsFaultedWithTaggedAsyncNIE(Task t)
        {
            if (t == null || !t.IsFaulted || t.Exception == null) return 0;
            return IsTaggedAsyncNIE(t.Exception.InnerException);
        }


        public void LoadAsset<T>(string name, T obj)
        {

        }
        public void Emit<T>(T obj)
        {
            LoadAsset("123", obj);
        }

        // ---- F-MAJ-1 helpers (host C#; read by the IL side via the reflection
        //      fallback). Mirror the Make/Sum pattern for the boundary structs
        //      and the int-return control. ----
        public static TestStruct4 MakeTestStruct4(int a)
        {
            return new TestStruct4(a);
        }
        public static int SumTestStruct4(TestStruct4 v)
        {
            return v.a;
        }
        public static TestStruct8 MakeTestStruct8(int a, int b)
        {
            return new TestStruct8(a, b);
        }
        public static int SumTestStruct8(TestStruct8 v)
        {
            return v.a + v.b;
        }
        // Two CLR int returns (the documented control: primitive writes 4 bytes
        // into a 4-byte slot; no overflow). Different values to distinguish them.
        public static int MakeIntA() { return 600; }
        public static int MakeIntB() { return 3; }

        // neo-clr-static-fields diagnostic: read the TestClass3.NeoClrStaticProbe
        // CLR static from the HOST (a plain CLR call -- no stsfld/ldsfld), so the
        // probe can tell a broken Stsfld WRITE from a broken Ldsfld READ.
        public static int HostReadNeoClrStaticProbe() { return TestClass3.NeoClrStaticProbe; }
        // neo-clr-static-vt-field: read the TestClass3.NeoClrVtStaticProbe CLR
        // value-type static from the HOST (a plain CLR call -- no stsfld/ldsfld),
        // so the probe can tell a broken Stsfld WRITE from a broken Ldsfld READ
        // (the child-3 TC1 pattern). Returns the int sum X+Y+Z.
        public static int HostReadNeoClrVtStaticProbe() { return (int)(TestClass3.NeoClrVtStaticProbe.X + TestClass3.NeoClrVtStaticProbe.Y + TestClass3.NeoClrVtStaticProbe.Z); }
        // A method that touches the frame between two Make() calls (live-range
        // overlap probe): returns an int the caller must observe so the compiler
        // does not dead-code-eliminate the call.
        public static int TouchFrame(int x)
        {
            return x + 1;
        }

        // ---- Review-loop M1/M2/M3 host helpers (neo-opt-harden-2 review fix).
        //      These exist to give the IL-side probes a way to (a) build a runtime
        //      string that occupies an mStack ref slot (a "canary" neighbour for
        //      the Initobj-zero-init probe), (b) read its length back, and (c)
        //      unbox a boxed CLR struct and report a field-derived sum so Box/
        //      Isinst/Castclass on a CLR struct local can be observed. ----

        // M1 canary: a 7-char string built at runtime (forces an mStack ref slot).
        public static string MakeCanary()
        {
            return "CANARY!";
        }

        // M1 canary read-back: returns the string length (expect 7).
        public static int StringLength(string s)
        {
            return s != null ? s.Length : -1;
        }

        // M2/M3 read-back: unbox a boxed TestVector3NoBinding and report the int
        // sum of its three float fields (expect 600 for (100,200,300)).
        public static int UnboxAndSumVector3NoBinding(object o)
        {
            if (o is TestVector3NoBinding v)
                return (int)(v.x + v.y + v.z);
            return -1;
        }

        // M3: report whether `o is TestVector3NoBinding` on the host side (a
        // second opinion independent of the IL-side isinst). Returns 1 / 0.
        public static int IsVector3NoBinding(object o)
        {
            return o is TestVector3NoBinding ? 1 : 0;
        }

        // Mixed-frame helper (M-extra): consume a CLR struct, an int, and a
        // string in one frame to confirm no cross-corruption among neighbouring
        // flat-bytes / primitive / ref slots.
        public static int MixedFrameSum(TestVector3NoBinding v, int n, string s)
        {
            int sum = (int)(v.x + v.y + v.z) + n;
            return sum + (s != null ? s.Length : 0);
        }

        // ---- Step 13 Area 4c helpers (host C#; read by the IL side via the
        //      reflection fallback CLRMethod.Invoke(byte*) when no autogen
        //      redirect is registered, OR via the autogen *_Neo redirect when
        //      a binder is registered). A CLR method with ref/out params --
        //      the typed-ref bridge. Before 4c the byref param's 8-byte Ref
        //      Slot was read as the raw value (silent-wrong) and never written
        //      back. ----

        // 4c: a CLR `ref int` (read + write back). The IL caller passes a
        // local int by ref; the mutation must propagate to the caller's local.
        public static void BumpRefInt(ref int v)
        {
            v += 10;
        }

        // 4c: a CLR `out int` (write back only). The IL caller observes the
        // assigned value.
        public static void ProduceOutInt(out int r)
        {
            r = 4242;
        }

        // 4c: a CLR `ref` to a pure-primitive CLR struct (TestVector3NoBinding,
        // 3 floats -- no binder so this routes through the reflection fallback).
        // Mutates the struct in place; the caller must observe the mutation.
        public static void BumpRefStruct(ref TestVector3NoBinding v)
        {
            v.x += 1f;
            v.y += 2f;
            v.z += 3f;
        }

        // 4c: a CLR `out` reference-type (string). The caller observes the
        // assigned reference.
        public static void ProduceOutString(out string s)
        {
            s = "from-clr-out";
        }

        // 4c: multiple byref params in one call (ref int + out int).
        public static void BumpRefAndProduceOut(ref int a, out int b)
        {
            a += 100;
            b = 999;
        }

        // 4c: an `in`-only param must NOT be written back (the call observes
        // the value but the caller's local is unchanged -- the marshal gates
        // write-back on !IsIn || IsOut). Returns the observed value so the IL
        // side can assert it read the right input.
        public static int ObserveInOnly(in int v)
        {
            return v + 5;
        }

        // 4c: non-byref CLR method regression (byte-identical control). A
        // plain by-value int param + return -- the discriminator must NOT fire
        // for it.
        public static int PlainByValue(int v)
        {
            return v * 2;
        }

        // 4c NIE probe: a CLR value type WITH a reference field and NO binder,
        // passed by ref. The reflection fallback's byref read derefs to flat
        // bytes; a struct WITH reference fields cannot be materialized from flat
        // bytes (GC refs unmappable without a binder) -> a clearly-tagged NIE.
        public static void BumpRefClrStructWithRef(ref TestClrStructWithRef v)
        {
            v.n += 1;
        }

        // ---- Step 13 Area 4c+4d interaction helpers. ----

        // 4d host: a CLR class with primitive + reference-type fields, the
        // target for ldflda + stind/ldind via field identity.
        public class Area4dHolder
        {
            public int intField;
            public string refField;
            public Area4dHolder(int a, string s) { intField = a; refField = s; }
            public Area4dHolder() { intField = 0; refField = null; }
        }

        // 4c+4d interaction: a CLR `ref int` that takes the address of a CLR
        // object's field (the byref PARAM's Ref Slot points at an mStack
        // object field; the deref must route through the field accessor).
        public static Area4dHolder MakeArea4dHolder(int a, string s)
        {
            return new Area4dHolder(a, s);
        }

        // 4d read-back helpers (so the IL side can observe a CLR object's
        // field without itself doing an ldfld on a CLR type -- avoids a
        // separate Step-6 gap contaminating the 4d probe).
        public static int ReadArea4dIntField(Area4dHolder h) { return h.intField; }
        public static string ReadArea4dRefField(Area4dHolder h) { return h.refField; }

        // ---- child-14 (neo-byref-clr2il-delegate): the CLR->IL delegate
        //      callback direction with a BYREF param. The IL side hands a
        //      delegate bound to an IL method that takes a `ref int` / `out
        //      int`; a CLR host helper invokes it WITH the byref. The IL
        //      callee mutates the byref; the mutation MUST be observable on
        //      the CLR side after the call returns (the write-back channel).
        //      This is the REVERSE of F-7 (IL->IL delegate-invoke). ----

        // A custom delegate type carrying a `ref int` param. The standard
        // Action<>/Func<> family is by-value, so a byref callback requires a
        // dedicated delegate type + a RegisterDelegateConvertor that marshals
        // the byref through the NeoInvokeSub arg-write + a write-back.
        public delegate void Clr2IlRefIntDelegate(ref int x);
        public delegate void Clr2IlOutIntDelegate(out int x);
        // Adversarial: a `ref long` (8-byte element, exercises the scratch-cell
        // sizing past the 4-byte int case).
        public delegate void Clr2IlRefLongDelegate(ref long x);
        // Adversarial: a multicast ref-int delegate (two IL targets; each must
        // see the prior target's mutation -- multicast ref-semantics).
        public delegate void Clr2IlRefIntMulticastDelegate(ref int x);

        // The host helper: invokes the (CLR-wrapped IL) delegate WITH the
        // byref. IL code passes its IL-method delegate here; the helper calls
        // del(ref x) and returns the resulting x so the IL side can assert.
        public static int InvokeRefCallback(Clr2IlRefIntDelegate del, int seed)
        {
            int x = seed;
            del(ref x);
            return x;
        }
        public static int InvokeOutCallback(Clr2IlOutIntDelegate del)
        {
            int x;
            del(out x);
            return x;
        }
        public static long InvokeRefLongCallback(Clr2IlRefLongDelegate del, long seed)
        {
            long x = seed;
            del(ref x);
            return x;
        }
        public static int InvokeRefIntMulticastCallback(Clr2IlRefIntMulticastDelegate del, int seed)
        {
            int x = seed;
            del(ref x);
            return x;
        }

#if TEST_MISSING_METHOD
        public int missingField;
        public void MissingMethodGeneric<T>(T obj)
        {

        }

        public void MissingMethod()
        {

        }

        public static void MissingStaticMethod()
        {

        }
#endif
    }

#if TEST_MISSING_METHOD
    public class MissingType
    {
    }
#endif

    // ---- F-MAJ-1 boundary structs (managed sizes 4 and 8 bytes; pure primitive).
    //      Used by the NeoOptHardTest_Fmaj1_StructSize4 / ...Size8 probes to test
    //      the overflow onset: a 4-byte struct must NOT overflow a 4-byte slot;
    //      an 8-byte struct overflows by 4. ----
    public struct TestStruct4
    {
        public int a;
        public TestStruct4(int a) { this.a = a; }
    }

    public struct TestStruct8
    {
        public int a;
        public int b;
        public TestStruct8(int a, int b) { this.a = a; this.b = b; }
    }
}
