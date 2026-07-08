#if ENABLE_NEO_MODE
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ILRuntime.CLR.Method;
using ILRuntime.CLR.TypeSystem;
using ILRuntime.Runtime.Intepreter;
using ILRuntime.Runtime.Intepreter.RegisterVM;
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
using AutoList = System.Collections.Generic.List<object>;
#else
using AutoList = ILRuntime.Other.UncheckedList<object>;
#endif

namespace ILRuntime.Runtime.Enviorment
{
    // Step 20 (neo-step20-async) -- custom Neo builder redirections for the
    // SYNC-completing async slice. Overrides the autogen non-functional stubs
    // (System_Runtime_CompilerServices_AsyncTaskMethodBuilder_*_Bi.cs) at
    // registration time (RegisterNeoAsyncRedirections registers in the AppDomain
    // ctor BEFORE the test-harness CLRBindings.Initialize; the redirect map is
    // first-registered-wins, so the custom redirects WIN and autogen is skipped).
    //
    // DUMP-CONFIRMED state-machine representation (OQ2): the C# compiler emits
    // the async state machine as a struct, but ILRuntime loads it as a HEAP
    // ILTypeInstance (the driver newsobj's `<Method>d__N` and stores it via
    // stfld.ref; MoveNext accesses its fields via heap ldfld/stfld). So the SM
    // is NOT an in-frame VT -- it is a heap reference. This sidesteps the
    // [NEO-IL-VT-INSTANCE-COVERAGE] in-frame-VT-instance-call concern entirely
    // (the design's D2 in-frame-VT premise does NOT apply; the heap-object
    // premise is simpler).
    //
    // Builder result stash (OQ1): the CLR AsyncTaskMethodBuilder<T> struct's
    // internal _task field is OPAQUE to the Neo frame (it is a CLR struct field
    // nested in the SM, not directly readable). So D3's auxiliary-map fallback
    // is used: a per-SM-ILTypeInstance side-map holds the completed/faulted
    // Task. The SM is the stable identity across Start/SetResult/SetException/
    // get_Task (the builder byref for the latter three points INTO the SM, so
    // the owning SM is recoverable from the builder's Ref Slot).
    //
    // Sync slice: AwaitUnsafeOnCompleted / AwaitOnCompleted throw a tagged NIE
    // (the suspend slice -- neo-step20-async-suspend -- owns the real impl).
    internal static unsafe class CLRRedirectionsAsyncNeo
    {
        // SM ILTypeInstance -> the completed/faulted Task (or null pending).
        // Sync scope: SetResult/SetException always populate this before get_Task
        // reads it (Start runs MoveNext to completion synchronously).
        [ThreadStatic]
        private static Dictionary<ILTypeInstance, object> _smTaskMap;

        private static Dictionary<ILTypeInstance, object> SmTaskMap
        {
            get
            {
                if (_smTaskMap == null)
                    _smTaskMap = new Dictionary<ILTypeInstance, object>();
                return _smTaskMap;
            }
        }

        // Step 20 fixer round 1: the SM identity for SetResult/SetException/get_Task
        // is the SM being driven by the innermost DriveMoveNext on this thread. The
        // prior RecoverSmFromBuilderByref (reverse-engineering the SM mStack index
        // from the builder byref) is fragile: the byref's objIdx did not reliably
        // point at the SM (the fresh-interpreter mStack layout + the ldflda-produced
        // byref disagreed -> null recovery -> silent default task). The SM is
        // unambiguously known to DriveMoveNext, so a ThreadStatic current-SM with
        // save/restore (sync scope is single-threaded + non-reentrant: MoveNext runs
        // to a terminal state synchronously) is the robust identity source. Nested
        // async save/restore: DriveMoveNext saves the outer SM, sets this to the
        // inner, drives, restores the outer -- so the outer's SetResult/get_Task
        // (which fire after the inner returns) see the outer SM again.
        [ThreadStatic]
        private static ILTypeInstance _currentAsyncSm;

        private static ILTypeInstance CurrentAsyncSm => _currentAsyncSm;

        // neo-async-movenext-fix (Piece 2/3) -- the suspend-side side map. When a
        // truly-incomplete awaiter suspends the SM, AwaitUnsafeOnCompleted_Neo
        // parks the ILAsyncContext<T> here keyed by SM (parallel to SmTaskMap).
        // SmTaskMap holds the COMPLETED/faulted task (sync path); SmContextMap
        // holds the SUSPENDED context (suspend path). get_Task consults SmTaskMap
        // first (completed), then SmContextMap (suspended -> Task<T> bridge).
        [ThreadStatic]
        private static Dictionary<ILTypeInstance, IAsyncContextSink> _smContextMap;

        private static Dictionary<ILTypeInstance, IAsyncContextSink> SmContextMap
        {
            get
            {
                if (_smContextMap == null)
                    _smContextMap = new Dictionary<ILTypeInstance, IAsyncContextSink>();
                return _smContextMap;
            }
        }

        // The context being RESUMED on this thread (the sink-swap, design D3 step
        // 5). Set by ResumeAsync before ExecuteNeo resumes the SM; checked FIRST by
        // SetResult/SetException so the resumed SM's terminal result routes to the
        // context (CompleteResult/CompleteException) instead of SmTaskMap.
        // ThreadStatic save/restore mirrors CurrentAsyncSm (resume scope: the
        // resumed MoveNext runs to terminal state synchronously).
        [ThreadStatic]
        private static IAsyncContextSink _currentAsyncContext;

        // Per-type cached MoveNext ILMethod (avoids a per-call GetMethod lookup).
        private static readonly Dictionary<ILType, ILMethod> _moveNextCache = new Dictionary<ILType, ILMethod>();

        private static ILMethod GetMoveNext(ILType smType)
        {
            if (!_moveNextCache.TryGetValue(smType, out ILMethod m))
            {
                m = smType.GetMethod("MoveNext", 0) as ILMethod;
                _moveNextCache[smType] = m;
            }
            return m;
        }

        // Recover the owning SM ILTypeInstance from a builder-method `this`
        // byref. The builder `this` arrives as a Ref Slot (objIdx, off) where
        // objIdx is the SM's mStack index in the CURRENT interpreter frame.
        // Used by get_Task (which runs in the driver frame where the SM is a
        // real heap local). SetResult/SetException use CurrentAsyncSm instead
        // (they run inside MoveNext on a fresh interpreter whose byref layout
        // is not trivially the SM mStack index).
        private static ILTypeInstance RecoverSmFromBuilderByref(byte* frameBase, int thisOff, AutoList mStack)
        {
            int objIdx = *(int*)(frameBase + thisOff);
            if (objIdx < 0 || objIdx >= mStack.Count) return null;
            return mStack[objIdx] as ILTypeInstance;
        }

        // get_Task runs in the DRIVER frame (after Start returned): try the
        // builder byref first (the SM is a real heap local there); fall back to
        // CurrentAsyncSm for the nested-async-in-MoveNext shape.
        // get_Task runs in the DRIVER frame (after Start returned). The builder
        // byref's objIdx is unreliable here (a pre-existing ldflda/newobj-dest
        // frame-layout drift: the byref reads objIdx=0 while the SM lives at a
        // different mStack index). The SM IS on the driver's mStack (newobj put
        // it there), and its SmTaskMap entry (stashed by SetResult inside
        // MoveNext) is pending. So scan mStack for an ILTypeInstance that has a
        // pending SmTaskMap entry -- this is the SM whose Task we must return.
        // Sound for the sync scope: each entry is consumed (Removed) by exactly
        // one get_Task, and the innermost pending SM (last SetResult) is found
        // first via the reverse scan. The byref is tried first (when it IS valid,
        // e.g. a future fix to the ldflda drift, it wins).
        private static ILTypeInstance RecoverSmForGetTask(byte* frameBase, AutoList mStack)
        {
            ILTypeInstance sm = RecoverSmFromBuilderByref(frameBase, 0, mStack);
            if (sm != null && (SmTaskMap.ContainsKey(sm) || SmContextMap.ContainsKey(sm))) return sm;
            // Reverse scan: the most-recently-stashed SM (innermost nested) wins.
            // A suspended SM lives on SmContextMap (the suspend slice parked its
            // ILAsyncContext<T> there); a completed/faulted SM lives on SmTaskMap.
            for (int i = mStack.Count - 1; i >= 0; i--)
            {
                if (mStack[i] is ILTypeInstance ili && (SmTaskMap.ContainsKey(ili) || SmContextMap.ContainsKey(ili)))
                    return ili;
            }
            return sm ?? CurrentAsyncSm;
        }

        // ----------------------------------------------------------------
        // AsyncTaskMethodBuilder<T>
        // ----------------------------------------------------------------

        public static void AsyncTaskMethodBuilder_T_Create_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            // Create() returns a default builder (no allocation). The sync path
            // never reads the builder's CLR fields -- the result stash lives in
            // the SM-keyed side-map. Write a default builder placeholder into the
            // dest (the caller's <>t__builder field is a CLR struct; under Neo
            // the field write is a no-op byte region we do not consume). Leave
            // dest zeroed (default struct).
        }

        public static void AsyncTaskMethodBuilder_T_Start_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            // Start<TSM>(ref sm): slot 0 = builder `this` byref, slot 1 = ref sm.
            // The optimizer lays out byref params as 8-byte Ref Slots (contiguous).
            // Read the SM ILTypeInstance from slot 1's Ref Slot, then drive its
            // MoveNext IL method to completion via the Neo call machinery (a
            // recursive ExecuteNeo on the SAME interpreter, sub-frame built at
            // the current stack top -- Start's own frame at `frameBase` is void
            // with no locals, so it is reused as the MoveNext sub-frame base).
            int curPrim = 0;
            // slot 0 (builder this, 8-byte byref) -- skip.
            curPrim += 8;
            // slot 1 (ref sm, 8-byte byref): (objIdx, off).
            int smObjIdx = *(int*)(frameBase + curPrim);
            curPrim += 4;
            int smOff = *(int*)(frameBase + curPrim);
            ILTypeInstance sm = null;
            if (smObjIdx >= 0 && smObjIdx < mStack.Count)
                sm = mStack[smObjIdx] as ILTypeInstance;
            if (sm == null)
                throw new NullReferenceException("Neo async Start: state machine is null (heap ILTypeInstance expected)");

            ILMethod moveNext = GetMoveNext(sm.Type);
            if (moveNext == null)
                throw new InvalidOperationException("Neo async Start: state machine has no MoveNext: " + sm.Type.FullName);

            DriveMoveNext(intp, sm, moveNext, frameBase, mStack);
        }

        public static void AsyncTaskMethodBuilder_T_SetResult_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            // SetResult(T result): slot 0 = builder this byref (8 bytes),
            // slot 1 = T result. Read T, stash Task.FromResult(T) keyed by SM.
            int curPrim = 0;
            curPrim += 8; // builder this byref (the SM identity comes from CurrentAsyncSm)
            // The result T: read it as the method's first explicit param. Its
            // layout depends on T (primitive / ref / VT). Use the CLRMethod's
            // parameter type to size the read.
            object resultObj = ReadResultParam(intp, method, frameBase, ref curPrim, mStack, retRefBase);
            // Sink-swap (design D3 step 5): if a context is being RESUMED on this
            // thread, route the resumed SM's terminal result to the context
            // (CompleteResult completes the TaskCompletionSource<T> bridge) instead
            // of stashing in SmTaskMap. Checked FIRST so the resume result is not
            // lost to the sync sink. OQ2 resolved: _currentAsyncContext wins.
            IAsyncContextSink ctx = _currentAsyncContext;
            if (ctx != null)
            {
                ILTypeInstance sm = CurrentAsyncSm;
                if (sm != null) SmContextMap.Remove(sm);
                ctx.CompleteResult(resultObj);
                return;
            }
            ILTypeInstance sm2 = CurrentAsyncSm;
            if (sm2 == null) return;
            SmTaskMap[sm2] = Task.FromResult(resultObj);
        }

        public static void AsyncTaskMethodBuilder_T_SetException_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            // SetException(Exception): slot 0 = builder this byref, slot 1 = Exception.
            int curPrim = 0;
            curPrim += 8;
            int exIdx = *(int*)(frameBase + curPrim);
            Exception ex = (exIdx >= 0 && exIdx < mStack.Count) ? mStack[exIdx] as Exception : null;
            if (ex == null && exIdx >= 0 && exIdx < mStack.Count && mStack[exIdx] is ILTypeInstance ilEx)
            {
                // IL exception: unwrap via CLRInstance (the ExceptionAdaptor).
                ex = ilEx.CLRInstance as Exception;
            }
            // Sink-swap (resume): route the resumed SM's terminal exception to the
            // context (CompleteException faults the TaskCompletionSource<T> bridge).
            IAsyncContextSink ctx = _currentAsyncContext;
            if (ctx != null)
            {
                ILTypeInstance sm = CurrentAsyncSm;
                if (sm != null) SmContextMap.Remove(sm);
                ctx.CompleteException(ex ?? new Exception("Neo async: unknown exception"));
                return;
            }
            ILTypeInstance sm2 = CurrentAsyncSm;
            if (sm2 == null) return;
            SmTaskMap[sm2] = Task.FromException(ex ?? new Exception("Neo async: unknown exception"));
        }

        public static void AsyncTaskMethodBuilder_T_GetTask_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            // get_Task runs in the DRIVER frame (after Start returned). The SM is
            // a heap ILTypeInstance local in the driver; the builder `this` byref
            // (slot 0) points into it. Recover the SM via the byref. (SetResult /
            // SetException run INSIDE MoveNext and use CurrentAsyncSm; get_Task is
            // the one builder method that runs in the caller, so it uses the
            // byref -- which IS valid in the driver where the SM is a real local.)
            ILTypeInstance sm = RecoverSmForGetTask(frameBase, mStack);
            object task = null;
            if (sm != null && SmTaskMap.TryGetValue(sm, out task))
                SmTaskMap.Remove(sm);
            if (task == null && sm != null && SmContextMap.TryGetValue(sm, out IAsyncContextSink ctx))
            {
                // neo-async-movenext-fix (D4): the SM SUSPENDED on a truly-incomplete
                // await. SmTaskMap has no entry (SetResult did not run); SmContextMap
                // holds the parked context. Return the context's Task<T> bridge (a
                // TaskCompletionSource<T> -- OQ3 first cut). The caller observes an
                // incomplete Task that completes when the awaited task does (resume
                // -> SetResult -> ctx.CompleteResult -> TCS.SetResult).
                task = ctx.GetTaskBridge();
            }
            if (task == null)
            {
                // Defensive: sync path always stashed in Start; if reached
                // without a stash, return a completed default task.
                task = Task.FromResult(GetDefaultForResultType(method));
            }
            WriteReferenceReturn(task, retDst, retRefBase, mStack);
        }

        // ----------------------------------------------------------------
        // AsyncTaskMethodBuilder (non-generic Task)
        // ----------------------------------------------------------------

        public static void AsyncTaskMethodBuilder_Create_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            // no-op (default builder; sync path uses the SM-keyed side-map).
        }

        public static void AsyncTaskMethodBuilder_Start_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            AsyncTaskMethodBuilder_T_Start_Neo(intp, frameBase, mStack, method, isNewObj, retDst, retRefBase);
        }

        public static void AsyncTaskMethodBuilder_SetResult_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            // SetResult(): slot 0 = builder this byref only.
            int curPrim = 0;
            ILTypeInstance sm = CurrentAsyncSm;
            if (sm == null) return;
            SmTaskMap[sm] = Task.CompletedTask;
        }

        public static void AsyncTaskMethodBuilder_SetException_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            AsyncTaskMethodBuilder_T_SetException_Neo(intp, frameBase, mStack, method, isNewObj, retDst, retRefBase);
        }

        public static void AsyncTaskMethodBuilder_GetTask_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            ILTypeInstance sm = RecoverSmForGetTask(frameBase, mStack);
            object task = null;
            if (sm != null && SmTaskMap.TryGetValue(sm, out task))
                SmTaskMap.Remove(sm);
            if (task == null)
                task = Task.CompletedTask;
            WriteReferenceReturn(task, retDst, retRefBase, mStack);
        }

        // ----------------------------------------------------------------
        // AsyncValueTaskMethodBuilder<T> / AsyncValueTaskMethodBuilder
        // (registration-only; sync path returns Task-backed ValueTask via FromResult)
        // ----------------------------------------------------------------

        public static void AsyncValueTaskMethodBuilder_T_Create_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase) { }

        public static void AsyncValueTaskMethodBuilder_T_Start_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            AsyncTaskMethodBuilder_T_Start_Neo(intp, frameBase, mStack, method, isNewObj, retDst, retRefBase);
        }

        public static void AsyncValueTaskMethodBuilder_T_SetResult_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            int curPrim = 0;
            ILTypeInstance sm = CurrentAsyncSm;
            curPrim += 8;
            object resultObj = ReadResultParam(intp, method, frameBase, ref curPrim, mStack, retRefBase);
            if (sm == null) return;
            SmTaskMap[sm] = resultObj; // stash the raw T; getter wraps in ValueTask<T>
        }

        public static void AsyncValueTaskMethodBuilder_T_SetException_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            AsyncTaskMethodBuilder_T_SetException_Neo(intp, frameBase, mStack, method, isNewObj, retDst, retRefBase);
        }

        public static void AsyncValueTaskMethodBuilder_T_GetTask_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            // get_Task for ValueTask<T>: return ValueTask<T>.FromResult(T).
            ILTypeInstance sm = RecoverSmForGetTask(frameBase, mStack);
            object resultObj = null;
            bool faulted = false;
            Exception faultEx = null;
            if (sm != null && SmTaskMap.TryGetValue(sm, out var stashed))
            {
                SmTaskMap.Remove(sm);
                if (stashed is Exception e) { faulted = true; faultEx = e; }
                else resultObj = stashed;
            }
            object vt;
            if (faulted)
                vt = CreateFaultedValueTask(method, faultEx);
            else
                vt = CreateValueTaskFromResult(method, resultObj);
            WriteReferenceReturn(vt, retDst, retRefBase, mStack);
        }

        public static void AsyncValueTaskMethodBuilder_Create_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase) { }

        public static void AsyncValueTaskMethodBuilder_Start_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            AsyncTaskMethodBuilder_T_Start_Neo(intp, frameBase, mStack, method, isNewObj, retDst, retRefBase);
        }

        public static void AsyncValueTaskMethodBuilder_SetResult_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            int curPrim = 0;
            ILTypeInstance sm = CurrentAsyncSm;
            if (sm == null) return;
            SmTaskMap[sm] = new BoxedValueTaskDefault(); // marker for "completed ValueTask"
        }

        public static void AsyncValueTaskMethodBuilder_SetException_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            AsyncTaskMethodBuilder_T_SetException_Neo(intp, frameBase, mStack, method, isNewObj, retDst, retRefBase);
        }

        public static void AsyncValueTaskMethodBuilder_GetTask_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            ILTypeInstance sm = RecoverSmForGetTask(frameBase, mStack);
            object task = null;
            if (sm != null && SmTaskMap.TryGetValue(sm, out var stashed))
            {
                SmTaskMap.Remove(sm);
                if (stashed is Exception e)
                    task = CreateFaultedValueTask(method, e);
                else
                    task = default(ValueTask); // completed ValueTask (box)
            }
            if (task == null) task = default(ValueTask);
            WriteReferenceReturn(task, retDst, retRefBase, mStack);
        }

        // ----------------------------------------------------------------
        // AsyncVoidMethodBuilder (async void)
        // ----------------------------------------------------------------

        public static void AsyncVoidMethodBuilder_Create_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase) { }

        public static void AsyncVoidMethodBuilder_Start_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            // AsyncVoid Start<AIA>(ref sm) -- same shape as Task Start.
            int curPrim = 0;
            curPrim += 8; // builder this byref
            int smObjIdx = *(int*)(frameBase + curPrim);
            curPrim += 4;
            int smOff = *(int*)(frameBase + curPrim);
            ILTypeInstance sm = null;
            if (smObjIdx >= 0 && smObjIdx < mStack.Count)
                sm = mStack[smObjIdx] as ILTypeInstance;
            if (sm == null)
                throw new NullReferenceException("Neo async void Start: state machine is null");
            ILMethod moveNext = GetMoveNext(sm.Type);
            if (moveNext == null)
                throw new InvalidOperationException("Neo async void Start: no MoveNext on " + sm.Type.FullName);
            DriveMoveNext(intp, sm, moveNext, frameBase, mStack);
        }

        public static void AsyncVoidMethodBuilder_SetResult_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            // async void SetResult: no task; the side-effect already happened.
        }

        public static void AsyncVoidMethodBuilder_SetException_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            // async void SetException: native C# rethrows on the SynchronizationContext;
            // the Neo interpreter has no SC, so rethrow on the caller's thread
            // (Legacy async-void semantics: the exception propagates synchronously).
            int curPrim = 0;
            curPrim += 8; // builder this byref
            int exIdx = *(int*)(frameBase + curPrim);
            Exception ex = (exIdx >= 0 && exIdx < mStack.Count) ? mStack[exIdx] as Exception : null;
            if (ex == null && exIdx >= 0 && exIdx < mStack.Count && mStack[exIdx] is ILTypeInstance ilEx)
                ex = ilEx.CLRInstance as Exception;
            if (ex != null)
                throw ex; // propagate synchronously to the caller
        }

        // ----------------------------------------------------------------
        // AwaitUnsafeOnCompleted / AwaitOnCompleted -- the SUSPEND body
        // (neo-async-movenext-fix Piece 2). The state machine reached here AFTER
        // the Piece-1 fix made get_IsCompleted's false result zero-extended (the
        // 8-byte Brtrue read is clean -> falls through to the suspend block).
        // ----------------------------------------------------------------

        public static void AwaitUnsafeOnCompleted_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            SuspendStateMachine(method);
        }

        public static void AwaitOnCompleted_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            // AwaitOnCompleted semantics differ from AwaitUnsafeOnCompleted by
            // capturing the ExecutionContext. The capture is a deferred concern
            // (design Non-Goal); the suspend body is otherwise identical.
            SuspendStateMachine(method);
        }

        // The SUSPEND body (design D2). Recovers the heap SM (CurrentAsyncSm),
        // reads the awaited Task from the SM's <>u__1 field (stored by MoveNext
        // just before this call), builds an ILAsyncContext<T> (T = the builder's
        // result type), parks it on SmContextMap[sm] (so get_Task returns the
        // bridge), and registers the task's UnsafeOnCompleted continuation (the
        // resume entry). Returns WITHOUT SetResult -- the SM is suspended. The SM
        // is already a heap ILTypeInstance (dump-confirmed), so its state (<>1__state)
        // and awaiter (<>u__1) survive across the suspension; HoistNeoILValueToHeap
        // is NOT needed for the SM (retained for the in-frame-VT-local edge).
        private static void SuspendStateMachine(CLRMethod method)
        {
            ILTypeInstance sm = CurrentAsyncSm;
            if (sm == null)
                throw new InvalidOperationException("Neo async suspend: no current state machine (CurrentAsyncSm is null)");

            Task task = GetAwaitedTaskFromSm(sm);
            if (task == null)
                throw new InvalidOperationException("Neo async suspend: could not recover the awaited task from the state machine (<>u__1 empty)");

            // T = the async method's result type (the builder's first generic arg).
            // method.DeclearingType is AsyncTaskMethodBuilder<T> / AsyncValueTaskMethodBuilder<T>.
            Type resultType = GetResultClrType(method);
            if (resultType == null) resultType = typeof(object);

            // Build ILAsyncContext<T> (T is runtime-determined -> reflect the ctor).
            ILMethod moveNext = GetMoveNext(sm.Type);
            if (moveNext == null)
                throw new InvalidOperationException("Neo async suspend: state machine has no MoveNext: " + sm.Type.FullName);
            Type ctxType = typeof(ILAsyncContext<>).MakeGenericType(resultType);
            object ctx = Activator.CreateInstance(ctxType,
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new object[] { sm, moveNext }, null);
            IAsyncContextSink sink = (IAsyncContextSink)ctx;

            // Park on SmContextMap so get_Task (which runs in the driver frame after
            // Start returns) finds the suspended context and returns its Task<T> bridge.
            SmContextMap[sm] = sink;

            // Register the continuation on the awaited Task. The resume fires on the
            // thread that completes the task. If the task raced to completion between
            // the IsCompleted check and here, UnsafeOnCompleted fires the continuation
            // (resume) immediately -- correct (no hang; MoveNext re-enters, reloads
            // <>u__1, calls GetResult, and continues). task is the awaiter's m_task
            // (typed Task); its (non-generic) GetAwaiter's UnsafeOnCompleted is the
            // standard await hook (TaskAwaiter implements ICriticalNotifyCompletion).
            MethodInfo resumeMi = ctxType.GetMethod("MoveNextInternal", BindingFlags.Instance | BindingFlags.NonPublic);
            Action resumeAction = (Action)Delegate.CreateDelegate(typeof(Action), ctx, resumeMi);
            task.GetAwaiter().UnsafeOnCompleted(resumeAction);
            // Return WITHOUT SetResult/SetException -- the SM is suspended.
        }

        // Recover the awaited Task from the SM's heap fields. The awaited Task is
        // either hoisted directly onto the SM (a Task/Task<T> reference field --
        // Roslyn hoists the await operand when it outlives the GetAwaiter call) OR
        // carried inside the <>u__1 awaiter's m_task field. Prefer a DIRECT Task
        // reference (robust to the SM field layout / count, which varies with the
        // number of hoisted locals); fall back to reading the awaiter's m_task.
        //
        // SINGLE-TASK SHAPE ONLY (review fixer Finding B): when the SM hoists MORE
        // THAN ONE Task reference field, the currently-awaited Task CANNOT be
        // disambiguated -- ManagedObjects is FIELD-DECLARATION order, NOT assignment
        // order, so the reverse-scan "highest-index wins" heuristic is silent-wrong
        // (it would register the continuation on the wrong Task + read the wrong
        // GetResult at resume). A recovery-miss MUST fail the SAME way across all
        // shapes (the stobj-refloop M1 lesson: loud NIE, never silent-skip). So an
        // AMBIGUOUS scan (directTaskCount > 1) throws a TAGGED NIE that routes the
        // multi-Task case to the deferred multi-await follow-up. The single-Task
        // shape (TC8 / explicit-local single await) keeps working -- the common case.
        private static Task GetAwaitedTaskFromSm(ILTypeInstance sm)
        {
            var mo = sm.ManagedObjects;
            if (mo == null) return null;
            // 1) A directly-hoisted Task reference (the common case for the await
            //    operand). Count them: >1 is ambiguous -> fail loud. Exactly 1 ->
            //    return it (the reverse scan remembers the single hit).
            Task directTask = null;
            int directTaskCount = 0;
            for (int i = mo.Count - 1; i >= 0; i--)
            {
                if (mo[i] is Task direct)
                {
                    directTask = direct;
                    directTaskCount++;
                }
            }
            if (directTaskCount > 1)
            {
                throw new NotImplementedException(
                    "Neo async multi-Task awaiter not supported (single-Task shape only); " +
                    "the currently-awaited Task cannot be disambiguated. " +
                    "State machine hoists " + directTaskCount + " Task fields. " +
                    "(neo-async-movenext-fix finding B -> deferred multi-await follow-up)");
            }
            if (directTask != null) return directTask;
            // 2) The awaiter's m_task (<>u__1 boxed TaskAwaiter/TaskAwaiter<T>).
            for (int i = 0; i < mo.Count; i++)
            {
                Task t = GetAwaiterTask(mo[i]) as Task;
                if (t != null) return t;
            }
            return null;
        }

        public static void SetStateMachine_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            // no-op (the CLR interface is never used).
        }

        // ================================================================
        // Awaiter / Task accessor redirects (override the autogen stubs which
        // use `default(TaskAwaiter)` and never read the real awaiter). The
        // TaskAwaiter struct wraps a single Task field (m_task); reading it via
        // reflection gives the Task, whose IsCompleted / Result are the real
        // values. The awaiter arrives as a CLR-struct `this` (flat managed bytes
        // under the F-MAJ-1 model) -- ReadNeoValueType boxes it.
        // ================================================================

        private static readonly FieldInfo s_taskAwaiterTaskField =
            typeof(System.Runtime.CompilerServices.TaskAwaiter).GetField("m_task",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static object GetAwaiterTask(object boxedAwaiter)
        {
            if (boxedAwaiter == null) return null;
            Type t = boxedAwaiter.GetType();
            // Only TaskAwaiter / TaskAwaiter<T> carry an m_task field. Using the
            // fallback (TaskAwaiter.m_task) on a non-awaiter object (e.g. a hoisted
            // Task<T> local, or the builder) throws ArgumentException at GetValue --
            // gate on the object actually being a TaskAwaiter, and defend GetValue.
            bool isAwaiter = t == typeof(System.Runtime.CompilerServices.TaskAwaiter);
            if (!isAwaiter && t.IsGenericType)
                isAwaiter = t.GetGenericTypeDefinition() == typeof(System.Runtime.CompilerServices.TaskAwaiter<>);
            if (!isAwaiter) return null;
            FieldInfo fi = t.GetField("m_task", BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi == null)
            {
                // Generic TaskAwaiter<T> also stores m_task on the base field.
                fi = s_taskAwaiterTaskField;
            }
            try { return fi?.GetValue(boxedAwaiter); }
            catch { return null; }
        }

        // TaskAwaiter<T>.get_IsCompleted -- read awaiter, return task.IsCompleted.
        public static void TaskAwaiter_T_GetIsCompleted_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            int curPrim = 0;
            // Fixer round 1: GetNeoValueTypeManagedSize (NOT Marshal.SizeOf -- it
            // throws on the generic TaskAwaiter<T>).
            int sz = Optimizer.GetNeoValueTypeManagedSize(method.DeclearingType.TypeForCLR);
            object awaiter = ILIntepreter.ReadNeoValueType(method.DeclearingType.TypeForCLR, frameBase, ref curPrim, sz);
            Task task = GetAwaiterTask(awaiter) as Task;
            bool isCompleted = task != null && task.IsCompleted;
            // Piece 1 (neo-async-movenext-fix): zero-extend the bool result to the
            // FULL 8-byte dest slot. The optimizer sizes Brtrue/Brfalse by the
            // physical slot width (localInfos[r1].Size), and the IsCompleted dest
            // slot is reused for a managed pointer elsewhere in the async SM
            // (Ldloca_S -> &awaiter), so it is 8 bytes. Writing only 4 bytes
            // ( *(int*)retDst ) leaves stale non-zero high pointer bits -> the
            // 8-byte Brtrue read misroutes when IsCompleted == false (the hang).
            // Zero-extending makes the 8-byte read clean (CONFIRMED by the propose-
            // phase probe: the SM falls through to the suspend block and REACHES
            // AwaitUnsafeOnCompleted). Neo-only, async-specific (the generic
            // Call-return zero-fill is the Option-B hardening, deferred).
            if (retDst != null) *(long*)retDst = isCompleted ? 1 : 0;
        }

        // TaskAwaiter<T>.GetResult -- read awaiter, return task.Result (or rethrow).
        public static void TaskAwaiter_T_GetResult_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            int curPrim = 0;
            // Fixer round 1: GetNeoValueTypeManagedSize (NOT Marshal.SizeOf).
            int sz = Optimizer.GetNeoValueTypeManagedSize(method.DeclearingType.TypeForCLR);
            object awaiter = ILIntepreter.ReadNeoValueType(method.DeclearingType.TypeForCLR, frameBase, ref curPrim, sz);
            Task task = GetAwaiterTask(awaiter) as Task;
            if (task == null)
            {
                // On RESUME, the awaiter reloaded from <>u__1 (ldfld of a CLR-struct-
                // with-ref-field hoisted on the heap SM) may surface with a null
                // m_task (the ref does not survive the heap round-trip via the
                // F-10 boxed-struct storage). Fall back to the awaited Task hoisted
                // directly on the SM (the same source the suspend path registered
                // the continuation on via GetAwaitedTaskFromSm). The sync path
                // (fresh awaiter, m_task present) does not hit this fallback.
                ILTypeInstance sm = CurrentAsyncSm;
                if (sm != null) task = GetAwaitedTaskFromSm(sm);
            }
            if (task == null) throw new NullReferenceException("Neo async GetResult: awaiter has no task");
            // B2 (neo-step20-async-suspend): this redirect is registered for BOTH
            // the generic TaskAwaiter<T> AND the non-generic TaskAwaiter (via
            // RegisterAwaiterAccessors on typeof(TaskAwaiter)). The non-generic
            // TaskAwaiter.GetResult() is void -- a non-generic Task (e.g. the
            // Task+DelayPromise from Task.Delay) has NO Result property, so an
            // unconditional InvokeMember("Result") throws MissingMethodException.
            // Gate the Result read on the awaiter being the generic TaskAwaiter<T>;
            // the void path writes nothing (GetResult returns void). Same family as
            // the sync-slice TC2/TC5 redirect-coverage edges.
            Type awaiterClr = method.DeclearingType.TypeForCLR;
            if (!awaiterClr.IsGenericType)
                return;
            object result = task.GetType().InvokeMember("Result",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.GetProperty, null, task, null);
            WriteReturnByType(method.ReturnType, result, retDst, retRefBase, mStack);
        }

        // Task<T>.GetAwaiter -- return a TaskAwaiter<T> wrapping this task.
        public static void Task_T_GetAwaiter_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            int curPrim = 0;
            int taskIdx = *(int*)(frameBase + curPrim);
            curPrim += 4;
            Task task = (taskIdx >= 0 && taskIdx < mStack.Count) ? mStack[taskIdx] as Task : null;
            if (task == null) throw new NullReferenceException("Neo async GetAwaiter: null task");
            // Construct the TaskAwaiter<T> via the Task's GetAwaiter() (real CLR).
            object awaiter = task.GetType().InvokeMember("GetAwaiter",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.InvokeMethod, null, task, null);
            WriteValueTypeReturn(awaiter, retDst, retRefBase, mStack);
        }

        // Task<T>.get_Result -- return task.Result.
        public static void Task_T_GetResult_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            int curPrim = 0;
            int taskIdx = *(int*)(frameBase + curPrim);
            curPrim += 4;
            Task task = (taskIdx >= 0 && taskIdx < mStack.Count) ? mStack[taskIdx] as Task : null;
            if (task == null) throw new NullReferenceException("Neo async get_Result: null task");
            object result = task.GetType().InvokeMember("Result",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.GetProperty, null, task, null);
            WriteReturnByType(method.ReturnType, result, retDst, retRefBase, mStack);
        }

        // Task.FromResult<T> -- static, returns Task<T>.FromResult(arg).
        public static void Task_FromResultT_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            // The generic arg T is the method's generic argument.
            Type t = GetMethodGenericArgType(method);
            int curPrim = 0;
            object arg = ReadPrimitiveOrRef(t, frameBase, ref curPrim, mStack);
            // Task<T>.FromResult is the static Task.FromResult<T>(T); resolve on
            // the open Task type and close it with T (Task<T>.GetMethod cannot
            // find the inherited static by signature alone).
            System.Reflection.MethodInfo fromResultOpen = typeof(Task).GetMethod("FromResult",
                BindingFlags.Public | BindingFlags.Static);
            System.Reflection.MethodInfo fromResult = fromResultOpen?.MakeGenericMethod(t);
            object task = fromResult?.Invoke(null, new[] { arg });
            WriteReferenceReturn(task, retDst, retRefBase, mStack);
        }

        // Task.get_CompletedTask -- static, returns Task.CompletedTask.
        public static void Task_GetCompletedTask_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            WriteReferenceReturn(Task.CompletedTask, retDst, retRefBase, mStack);
        }

        // Task.Delay(int) -- static, returns the real Task.Delay(ms). Permanent
        // redirect (neo-step20-async-suspend): the awaitable source for the suspend
        // green test. A genuinely-completing-on-threadpool Task whose IsCompleted is
        // false at the await check is what triggers the suspend path
        // (AwaitUnsafeOnCompleted); Task.Run(ilLambda) cannot serve this role (the
        // IL lambda is a Step-19 DelegateAdapter that does not round-trip through
        // the un-redirected Task.Run reflection fallback). The real Task.Delay is
        // returned verbatim (its threadpool completion drives the resume).
        public static void Task_Delay_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            int curPrim = 0;
            int ms = *(int*)(frameBase + curPrim);
            WriteReferenceReturn(Task.Delay(ms), retDst, retRefBase, mStack);
        }

        // Helpers: read/write the return value by its Neo kind.
        private static Type GetMethodGenericArgType(CLRMethod method)
        {
            try
            {
                if (method.GenericArgumentsCLR != null && method.GenericArgumentsCLR.Length > 0)
                    return method.GenericArgumentsCLR[0];
            }
            catch { }
            return typeof(int);
        }

        private static unsafe object ReadPrimitiveOrRef(Type t, byte* frameBase, ref int curPrim, AutoList mStack)
        {
            if (t == typeof(int)) { int v = *(int*)(frameBase + curPrim); curPrim += 4; return v; }
            if (t == typeof(long)) { long v = *(long*)(frameBase + curPrim); curPrim += 8; return v; }
            if (t == typeof(float)) { float v = *(float*)(frameBase + curPrim); curPrim += 4; return v; }
            if (t == typeof(double)) { double v = *(double*)(frameBase + curPrim); curPrim += 8; return v; }
            if (t == typeof(bool)) { int v = *(int*)(frameBase + curPrim); curPrim += 4; return v != 0; }
            // Reference type: mStack index.
            int idx = *(int*)(frameBase + curPrim);
            curPrim += 4;
            return (idx >= 0 && idx < mStack.Count) ? mStack[idx] : null;
        }

        private static unsafe void WriteReturnByType(IType retType, object value, byte* retDst, int retRefBase, AutoList mStack)
        {
            if (retDst == null) return;
            Type clr = retType?.TypeForCLR;
            if (clr == typeof(int) || (clr != null && clr.IsEnum)) { *(int*)retDst = value == null ? 0 : Convert.ToInt32(value); return; }
            if (clr == typeof(long)) { *(long*)retDst = value == null ? 0 : Convert.ToInt64(value); return; }
            if (clr == typeof(float)) { *(float*)retDst = value == null ? 0 : Convert.ToSingle(value); return; }
            if (clr == typeof(double)) { *(double*)retDst = value == null ? 0 : Convert.ToDouble(value); return; }
            if (clr == typeof(bool)) { *(int*)retDst = value == null ? 0 : ((bool)value ? 1 : 0); return; }
            // Reference return.
            WriteReferenceReturn(value, retDst, retRefBase, mStack);
        }

        private static unsafe void WriteValueTypeReturn(object value, byte* retDst, int retRefBase, AutoList mStack)
        {
            // The autogen GetReturnValueCodeNeo convention for a struct return is
            // to write the flat managed bytes (the optimizer sized the dest).
            // DUMP-GATED (fixer round 1): MUST use the GC-aware MANAGED size
            // (Optimizer.GetNeoValueTypeManagedSize = Unsafe.SizeOf<T>), NOT
            // Marshal.SizeOf. Marshal.SizeOf throws ArgumentException ("must not
            // be a generic type") for a generic struct such as TaskAwaiter<int>/
            // TaskAwaiter<T>, which killed the GetAwaiter return write and routed
            // the state machine to SetException (the whole sync slice was blocked
            // by this single throw). The managed size is what the optimizer used
            // to size the dest slot (AllocateNeoCallParamSlot), so it is
            // byte-consistent by construction.
            if (retDst == null || value == null) return;
            int sz = Optimizer.GetNeoValueTypeManagedSize(value.GetType());
            ILIntepreter.WriteNeoValueType(value, retDst, sz);
        }

        // ================================================================
        // Helpers
        // ================================================================

        private struct BoxedValueTaskDefault { }

        // ================================================================
        // Registration (called from AppDomain ctor; first-registered-wins
        // means the autogen *Neo stub registrations in CLRBindings.Initialize
        // are SKIPPED for these MethodBases).
        // ================================================================
        public static void Register(AppDomain app)
        {
            BindingFlags flag = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            // ---- AsyncTaskMethodBuilder<T> (register per T the test harness binds) ----
            RegisterTaskBuilderT(app, flag, typeof(AsyncTaskMethodBuilder<int>), "Task`1");
            RegisterTaskBuilderT(app, flag, typeof(AsyncTaskMethodBuilder<ILTypeInstance>), "Task`1");

            // ---- AsyncTaskMethodBuilder (non-generic) ----
            {
                Type t = typeof(AsyncTaskMethodBuilder);
                RegisterSimple(app, flag, t, "Create", Type.EmptyTypes,
                    nameof(AsyncTaskMethodBuilder_Create_Neo));
                RegisterSimple(app, flag, t, "get_Task", Type.EmptyTypes,
                    nameof(AsyncTaskMethodBuilder_GetTask_Neo));
                RegisterSimple(app, flag, t, "SetException", new[] { typeof(Exception) },
                    nameof(AsyncTaskMethodBuilder_SetException_Neo));
                RegisterSimple(app, flag, t, "SetResult", Type.EmptyTypes,
                    nameof(AsyncTaskMethodBuilder_SetResult_Neo));
                RegisterAwaiters(app, flag, t);
            }

            // ---- AsyncValueTaskMethodBuilder<T> ----
            RegisterValueTaskBuilderT(app, flag, typeof(System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder<int>));
            RegisterValueTaskBuilderT(app, flag, typeof(System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder<ILTypeInstance>));

            // ---- AsyncValueTaskMethodBuilder (non-generic) ----
            {
                Type t = typeof(System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder);
                RegisterSimple(app, flag, t, "Create", Type.EmptyTypes,
                    nameof(AsyncValueTaskMethodBuilder_Create_Neo));
                RegisterSimple(app, flag, t, "get_Task", Type.EmptyTypes,
                    nameof(AsyncValueTaskMethodBuilder_GetTask_Neo));
                RegisterSimple(app, flag, t, "SetException", new[] { typeof(Exception) },
                    nameof(AsyncValueTaskMethodBuilder_SetException_Neo));
                RegisterSimple(app, flag, t, "SetResult", Type.EmptyTypes,
                    nameof(AsyncValueTaskMethodBuilder_SetResult_Neo));
                RegisterAwaiters(app, flag, t);
            }

            // ---- AsyncVoidMethodBuilder ----
            {
                Type t = typeof(System.Runtime.CompilerServices.AsyncVoidMethodBuilder);
                RegisterSimple(app, flag, t, "Create", Type.EmptyTypes,
                    nameof(AsyncVoidMethodBuilder_Create_Neo));
                RegisterSimple(app, flag, t, "SetException", new[] { typeof(Exception) },
                    nameof(AsyncVoidMethodBuilder_SetException_Neo));
                RegisterSimple(app, flag, t, "SetResult", Type.EmptyTypes,
                    nameof(AsyncVoidMethodBuilder_SetResult_Neo));
                RegisterAwaiters(app, flag, t);
                // Start<AIA>(ref AIA): register the open generic definition so it
                // matches any TSM instantiation (TryGetRedirection tries
                // GetGenericMethodDefinition first).
                MethodInfo startOpen = t.GetMethod("Start", flag);
                if (startOpen != null && startOpen.IsGenericMethodDefinition)
                    app.RegisterCLRMethodRedirectionNeo(startOpen, AsyncVoidMethodBuilder_Start_Neo);
            }

            // ---- Awaiter / Task accessor overrides (override the autogen stubs
            //      which use default(TaskAwaiter)). Register per T the harness binds. ----
            RegisterAwaiterAccessors(app, flag, typeof(System.Runtime.CompilerServices.TaskAwaiter<int>));
            RegisterAwaiterAccessors(app, flag, typeof(System.Runtime.CompilerServices.TaskAwaiter));
            RegisterAwaiterAccessors(app, flag, typeof(System.Runtime.CompilerServices.TaskAwaiter<ILTypeInstance>));

            // Task<T>.GetAwaiter / get_Result overrides (override autogen stubs).
            RegisterTaskAccessors(app, flag, typeof(Task<int>));
            RegisterTaskAccessors(app, flag, typeof(Task));
            RegisterTaskAccessors(app, flag, typeof(Task<ILTypeInstance>));

            // Task.FromResult<T>(T) -- the open generic static.
            MethodInfo fromResultOpen = typeof(Task).GetMethod("FromResult", flag);
            if (fromResultOpen != null && fromResultOpen.IsGenericMethodDefinition)
                app.RegisterCLRMethodRedirectionNeo(fromResultOpen, Task_FromResultT_Neo);
            // Task.get_CompletedTask (static).
            MethodInfo gct = typeof(Task).GetMethod("get_CompletedTask", flag);
            if (gct != null)
                app.RegisterCLRMethodRedirectionNeo(gct, Task_GetCompletedTask_Neo);
            // Task.Delay(int) -- permanent redirect (the suspend awaitable source).
            MethodInfo delay = typeof(Task).GetMethod("Delay", flag, null, new[] { typeof(int) }, null);
            if (delay != null)
                app.RegisterCLRMethodRedirectionNeo(delay, Task_Delay_Neo);
        }

        private static void RegisterAwaiterAccessors(AppDomain app, BindingFlags flag, Type awaiterType)
        {
            MethodInfo isc = awaiterType.GetMethod("get_IsCompleted", flag, null, Type.EmptyTypes, null);
            if (isc != null) app.RegisterCLRMethodRedirectionNeo(isc, TaskAwaiter_T_GetIsCompleted_Neo);
            MethodInfo gr = awaiterType.GetMethod("GetResult", flag, null, Type.EmptyTypes, null);
            if (gr != null) app.RegisterCLRMethodRedirectionNeo(gr, TaskAwaiter_T_GetResult_Neo);
        }

        private static void RegisterTaskAccessors(AppDomain app, BindingFlags flag, Type taskType)
        {
            MethodInfo ga = taskType.GetMethod("GetAwaiter", flag);
            if (ga != null) app.RegisterCLRMethodRedirectionNeo(ga, Task_T_GetAwaiter_Neo);
            MethodInfo gr = taskType.GetProperty("Result")?.GetGetMethod(false);
            if (gr != null) app.RegisterCLRMethodRedirectionNeo(gr, Task_T_GetResult_Neo);
        }

        private static void RegisterTaskBuilderT(AppDomain app, BindingFlags flag, Type builderType, string taskGetterSuffix)
        {
            RegisterSimple(app, flag, builderType, "Create", Type.EmptyTypes,
                nameof(AsyncTaskMethodBuilder_T_Create_Neo));
            RegisterSimple(app, flag, builderType, "get_Task", Type.EmptyTypes,
                nameof(AsyncTaskMethodBuilder_T_GetTask_Neo));
            RegisterSimple(app, flag, builderType, "SetException", new[] { typeof(Exception) },
                nameof(AsyncTaskMethodBuilder_T_SetException_Neo));
            Type resultType = builderType.GetGenericArguments()[0];
            RegisterSimple(app, flag, builderType, "SetResult", new[] { resultType },
                nameof(AsyncTaskMethodBuilder_T_SetResult_Neo));
            // Start<TSM>(ref TSM): register the open generic definition (matches any TSM).
            MethodInfo startOpen = builderType.GetMethod("Start", flag);
            if (startOpen != null && startOpen.IsGenericMethodDefinition)
                app.RegisterCLRMethodRedirectionNeo(startOpen, AsyncTaskMethodBuilder_T_Start_Neo);
            RegisterAwaiters(app, flag, builderType);
        }

        private static void RegisterValueTaskBuilderT(AppDomain app, BindingFlags flag, Type builderType)
        {
            RegisterSimple(app, flag, builderType, "Create", Type.EmptyTypes,
                nameof(AsyncValueTaskMethodBuilder_T_Create_Neo));
            RegisterSimple(app, flag, builderType, "get_Task", Type.EmptyTypes,
                nameof(AsyncValueTaskMethodBuilder_T_GetTask_Neo));
            RegisterSimple(app, flag, builderType, "SetException", new[] { typeof(Exception) },
                nameof(AsyncValueTaskMethodBuilder_T_SetException_Neo));
            Type resultType = builderType.GetGenericArguments()[0];
            RegisterSimple(app, flag, builderType, "SetResult", new[] { resultType },
                nameof(AsyncValueTaskMethodBuilder_T_SetResult_Neo));
            MethodInfo startOpen = builderType.GetMethod("Start", flag);
            if (startOpen != null && startOpen.IsGenericMethodDefinition)
                app.RegisterCLRMethodRedirectionNeo(startOpen, AsyncValueTaskMethodBuilder_T_Start_Neo);
            RegisterAwaiters(app, flag, builderType);
        }

        private static void RegisterAwaiters(AppDomain app, BindingFlags flag, Type builderType)
        {
            // AwaitUnsafeOnCompleted<TA,TSM> / AwaitOnCompleted<TA,TSM>: register
            // the open generic definitions (tagged NIE stubs).
            foreach (string name in new[] { "AwaitUnsafeOnCompleted", "AwaitOnCompleted" })
            {
                MethodInfo openDef = null;
                foreach (var m in builderType.GetMethods(flag))
                {
                    if (m.Name == name && m.IsGenericMethodDefinition)
                    {
                        openDef = m; // the 2-arg version (TA ref, TSM ref)
                        break;
                    }
                }
                if (openDef != null)
                {
                    var del = (name == "AwaitUnsafeOnCompleted") ? (CLRRedirectionDelegateNeo)AwaitUnsafeOnCompleted_Neo : AwaitOnCompleted_Neo;
                    app.RegisterCLRMethodRedirectionNeo(openDef, del);
                }
            }
            // SetStateMachine(IAsyncStateMachine): no-op.
            MethodInfo ssm = null;
            foreach (var m in builderType.GetMethods(flag))
            {
                if (m.Name == "SetStateMachine" && !m.IsGenericMethod) { ssm = m; break; }
            }
            if (ssm != null)
                app.RegisterCLRMethodRedirectionNeo(ssm, SetStateMachine_Neo);
        }

        private static void RegisterSimple(AppDomain app, BindingFlags flag, Type builderType,
            string methodName, Type[] argTypes, string redirectName)
        {
            MethodInfo m = builderType.GetMethod(methodName, flag, null, argTypes, null);
            if (m == null) return;
            // Resolve redirectName -> delegate via a small switch (the redirects
            // are all in this class).
            CLRRedirectionDelegateNeo del = ResolveRedirect(redirectName);
            if (del != null)
                app.RegisterCLRMethodRedirectionNeo(m, del);
        }

        private static CLRRedirectionDelegateNeo ResolveRedirect(string name)
        {
            switch (name)
            {
                case nameof(AsyncTaskMethodBuilder_T_Create_Neo): return AsyncTaskMethodBuilder_T_Create_Neo;
                case nameof(AsyncTaskMethodBuilder_T_Start_Neo): return AsyncTaskMethodBuilder_T_Start_Neo;
                case nameof(AsyncTaskMethodBuilder_T_SetResult_Neo): return AsyncTaskMethodBuilder_T_SetResult_Neo;
                case nameof(AsyncTaskMethodBuilder_T_SetException_Neo): return AsyncTaskMethodBuilder_T_SetException_Neo;
                case nameof(AsyncTaskMethodBuilder_T_GetTask_Neo): return AsyncTaskMethodBuilder_T_GetTask_Neo;
                case nameof(AsyncTaskMethodBuilder_Create_Neo): return AsyncTaskMethodBuilder_Create_Neo;
                case nameof(AsyncTaskMethodBuilder_Start_Neo): return AsyncTaskMethodBuilder_Start_Neo;
                case nameof(AsyncTaskMethodBuilder_SetResult_Neo): return AsyncTaskMethodBuilder_SetResult_Neo;
                case nameof(AsyncTaskMethodBuilder_SetException_Neo): return AsyncTaskMethodBuilder_SetException_Neo;
                case nameof(AsyncTaskMethodBuilder_GetTask_Neo): return AsyncTaskMethodBuilder_GetTask_Neo;
                case nameof(AsyncValueTaskMethodBuilder_T_Create_Neo): return AsyncValueTaskMethodBuilder_T_Create_Neo;
                case nameof(AsyncValueTaskMethodBuilder_T_Start_Neo): return AsyncValueTaskMethodBuilder_T_Start_Neo;
                case nameof(AsyncValueTaskMethodBuilder_T_SetResult_Neo): return AsyncValueTaskMethodBuilder_T_SetResult_Neo;
                case nameof(AsyncValueTaskMethodBuilder_T_SetException_Neo): return AsyncValueTaskMethodBuilder_T_SetException_Neo;
                case nameof(AsyncValueTaskMethodBuilder_T_GetTask_Neo): return AsyncValueTaskMethodBuilder_T_GetTask_Neo;
                case nameof(AsyncValueTaskMethodBuilder_Create_Neo): return AsyncValueTaskMethodBuilder_Create_Neo;
                case nameof(AsyncValueTaskMethodBuilder_Start_Neo): return AsyncValueTaskMethodBuilder_Start_Neo;
                case nameof(AsyncValueTaskMethodBuilder_SetResult_Neo): return AsyncValueTaskMethodBuilder_SetResult_Neo;
                case nameof(AsyncValueTaskMethodBuilder_SetException_Neo): return AsyncValueTaskMethodBuilder_SetException_Neo;
                case nameof(AsyncValueTaskMethodBuilder_GetTask_Neo): return AsyncValueTaskMethodBuilder_GetTask_Neo;
                case nameof(AsyncVoidMethodBuilder_Create_Neo): return AsyncVoidMethodBuilder_Create_Neo;
                case nameof(AsyncVoidMethodBuilder_Start_Neo): return AsyncVoidMethodBuilder_Start_Neo;
                case nameof(AsyncVoidMethodBuilder_SetResult_Neo): return AsyncVoidMethodBuilder_SetResult_Neo;
                case nameof(AsyncVoidMethodBuilder_SetException_Neo): return AsyncVoidMethodBuilder_SetException_Neo;
                default: return null;
            }
        }

        // Drive a state machine's MoveNext. SYNC scope: every await short-circuits,
        // so MoveNext completes synchronously here (sink == null -> SetResult/
        // SetException stash in SmTaskMap). SUSPEND scope: if an await is truly
        // incomplete, AwaitUnsafeOnCompleted_Neo suspends (registers a continuation,
        // returns WITHOUT SetResult); DriveMoveNext returns with the SM parked on
        // SmContextMap. The RESUME is driven by ResumeAsync (sink != null).
        // Uses a FRESH pooled interpreter (mirrors Step 19 NeoInvokeSub) so
        // MoveNext's frame + mStack reservation is fully isolated from the caller's
        // in-flight frame -- the async SM's recursive calls (SetResult/
        // SetException/etc.) cannot perturb the caller's frame bytes or mStack
        // region. The fresh interpreter is returned to the pool in finally
        // (unbounded growth guard; the Step-19 F1 lesson).
        private static unsafe void DriveMoveNext(ILIntepreter callerIntp, ILTypeInstance sm, ILMethod moveNext,
            byte* subFrameBase, AutoList callerMStack)
        {
            DriveMoveNextCore(callerIntp.AppDomain, sm, moveNext, null);
        }

        // neo-async-movenext-fix (Piece 3) -- the RESUME entry, called by
        // ILAsyncContext<T>.MoveNextInternal on the thread that completed the
        // awaited task. There is no in-flight Neo frame (the resume fires off the
        // threadpool), so the AppDomain is recovered from the SM's ILType. Sets
        // _currentAsyncContext = sink so the resumed SM's SetResult/SetException
        // route to the context (CompleteResult/CompleteException) instead of
        // SmTaskMap (the sink-swap, design D3 step 5).
        internal static unsafe void ResumeAsync(ILTypeInstance sm, ILMethod moveNext, IAsyncContextSink sink)
        {
            DriveMoveNextCore(sm.Type.AppDomain, sm, moveNext, sink);
        }

        private static unsafe void DriveMoveNextCore(AppDomain appdomain, ILTypeInstance sm, ILMethod moveNext,
            IAsyncContextSink sink)
        {
            ILIntepreter intp = appdomain.RequestILIntepreter();
            // Set the current async SM + (for resume) the context sink for the
            // duration of this drive (save/restore for nested async).
            // SetResult/SetException/get_Task read CurrentAsyncSm to key the
            // SmTaskMap; SetResult/SetException check _currentAsyncContext FIRST
            // (the sink-swap) so the resumed SM's terminal result routes to ctx.
            ILTypeInstance prevSm = _currentAsyncSm;
            IAsyncContextSink prevCtx = _currentAsyncContext;
            _currentAsyncSm = sm;
            // UNCONDITIONAL (review fixer Finding A): assign the sink even when null
            // so a SYNC nested DriveMoveNext during a resume CLEARS the outer resume
            // context. Previously this was `if (sink != null)` -- a sync drive
            // (Start of a nested async that completes synchronously) left
            // _currentAsyncContext inherited from the outer resume (ctx_A), so the
            // nested SM's SetResult misrouted ITS result to the OUTER bridge
            // (CompleteResult on ctx_A), and the outer SM's own later SetResult
            // double-completed ctx_A (wrong value + InvalidOperationException). The
            // null assignment isolates the nested sync drive (its SetResult stashes
            // in SmTaskMap like any top-level sync drive); the prevCtx save/restore
            // in finally reinstates ctx_A for the resumed SM's continued execution.
            // Top-level sync drives have prevCtx == null so the unconditional null
            // write is a no-op for them.
            _currentAsyncContext = sink;
            try
            {
                var stack = intp.Stack;
                AutoList mStack = stack.ManagedStack;
                int mStackBase = mStack.Count;
                stack.ResetValueTypePointer();

                ref readonly var nf = ref moveNext.CompiledFrame;
                int frameSize = nf.TotalStructSize;
                int totalRefSize = nf.TotalRefSize;

                byte* frameBase = (byte*)stack.StackBase;
                byte* esp = frameBase;
                byte* newEsp = esp + frameSize;

                // Zero the locals primitive region.
                if (nf.LocalsPrimitiveSize > 0)
                    Unsafe.InitBlock(frameBase + nf.ParamPrimitiveSize, 0, (uint)nf.LocalsPrimitiveSize);
                // Zero-init ref-typed locals.
                var localInfos = nf.LocalInfos;
                var localIsRef = nf.LocalIsReference;
                if (localInfos != null && localIsRef != null)
                {
                    for (int i = 0; i < localInfos.Length; i++)
                    {
                        if (localIsRef[i])
                            *(int*)(frameBase + localInfos[i].Offset) = -1;
                    }
                }

                // Managed-stack reservation for this frame's reference slots.
                int frameRefBase = mStack.Count;
                for (int i = 0; i < totalRefSize; i++)
                    mStack.Add(null);

                // Write `this` (slot 0) = the SM ILTypeInstance (a reference).
                var thisSlot = nf.ParamInfos[0];
                int thisRefIdx = frameRefBase + thisSlot.RefOffset;
                mStack[thisRefIdx] = sm;
                *(int*)(frameBase + thisSlot.Offset) = thisRefIdx;

                // Return slot (MoveNext is void).
                int retRefCount = nf.ReturnRefCount;
                byte* retDst = newEsp;
                int retRefBase = mStack.Count;
                for (int i = 0; i < retRefCount; i++)
                    mStack.Add(null);

                bool unhandled;
                intp.ExecuteNeo(moveNext, frameBase, retDst, retRefBase, out unhandled);
                mStack.RemoveRange(mStackBase, mStack.Count - mStackBase);

                if (unhandled)
                {
                    // The SM threw an unhandled exception. In the sync scope, a throw
                    // inside MoveNext routes through SetException (the SM's catch), so
                    // unhandled here means a runtime bug -- rethrow.
                    throw new Exception("Neo async MoveNext: unhandled exception in state machine " + moveNext.DeclearingType?.FullName);
                }
            }
            finally
            {
                // Restore the outer SM for nested async (save/restore is sound;
                // the prior "keep top-level SM" variant was unsound -- a stale
                // pointer contaminated subsequent async tests in a multi-test
                // run). get_Task recovers the SM via RecoverSmForGetTask instead.
                _currentAsyncSm = prevSm;
                _currentAsyncContext = prevCtx;
                appdomain.FreeILIntepreter(intp);
            }
        }

        // Read the result param of a SetResult(T) call. T's Neo layout depends
        // on its kind: primitive (inline bytes), reference (mStack index), or
        // CLR value type (flat managed bytes -> box). Returns a boxed object.
        private static unsafe object ReadResultParam(ILIntepreter intp, CLRMethod method, byte* frameBase, ref int curPrim, AutoList mStack, int retRefBase)
        {
            // method.DeclearingType is the builder CLR generic type, e.g.
            // AsyncTaskMethodBuilder<T>. Its first generic arg is T.
            Type clrT = GetResultClrType(method);
            if (clrT == null)
            {
                // Unknown: assume reference (read mStack index).
                int idx = *(int*)(frameBase + curPrim);
                curPrim += 4;
                return (idx >= 0 && idx < mStack.Count) ? mStack[idx] : null;
            }
            if (clrT.IsPrimitive)
            {
                if (clrT == typeof(int)) { int v = *(int*)(frameBase + curPrim); curPrim += 4; return v; }
                if (clrT == typeof(long)) { long v = *(long*)(frameBase + curPrim); curPrim += 8; return v; }
                if (clrT == typeof(float)) { float v = *(float*)(frameBase + curPrim); curPrim += 4; return v; }
                if (clrT == typeof(double)) { double v = *(double*)(frameBase + curPrim); curPrim += 8; return v; }
                if (clrT == typeof(bool)) { int v = *(int*)(frameBase + curPrim); curPrim += 4; return v != 0; }
                if (clrT == typeof(byte)) { int v = *(int*)(frameBase + curPrim); curPrim += 4; return (byte)v; }
                if (clrT == typeof(sbyte)) { int v = *(int*)(frameBase + curPrim); curPrim += 4; return (sbyte)v; }
                if (clrT == typeof(short)) { int v = *(int*)(frameBase + curPrim); curPrim += 4; return (short)v; }
                if (clrT == typeof(ushort)) { int v = *(int*)(frameBase + curPrim); curPrim += 4; return (ushort)v; }
                if (clrT == typeof(uint)) { uint v = *(uint*)(frameBase + curPrim); curPrim += 4; return v; }
                if (clrT == typeof(ulong)) { ulong v = *(ulong*)(frameBase + curPrim); curPrim += 8; return v; }
                if (clrT == typeof(char)) { int v = *(int*)(frameBase + curPrim); curPrim += 4; return (char)v; }
                // Fallback: treat as 4-byte int.
                { int v = *(int*)(frameBase + curPrim); curPrim += 4; return v; }
            }
            // Reference type (T is a class): mStack index.
            int ridx = *(int*)(frameBase + curPrim);
            curPrim += 4;
            return (ridx >= 0 && ridx < mStack.Count) ? mStack[ridx] : null;
        }

        private static Type GetResultClrType(CLRMethod method)
        {
            try
            {
                Type decl = method.DeclearingType?.TypeForCLR;
                if (decl != null && decl.IsGenericType)
                {
                    Type[] ga = decl.GetGenericArguments();
                    if (ga != null && ga.Length > 0)
                        return ga[0];
                }
            }
            catch { }
            return null;
        }

        private static object GetDefaultForResultType(CLRMethod method)
        {
            Type t = GetResultClrType(method);
            if (t != null && t.IsValueType)
                return Activator.CreateInstance(t);
            return null;
        }

        private static unsafe void WriteReferenceReturn(object value, byte* retDst, int retRefBase, AutoList mStack)
        {
            if (retDst == null) return;
            if (retRefBase >= mStack.Count)
                mStack.Add(value);
            else
                mStack[retRefBase] = value;
            *(int*)retDst = retRefBase;
        }

        // Construct ValueTask<T>.FromResult(result) via reflection (T is the
        // builder's generic arg).
        private static object CreateValueTaskFromResult(CLRMethod method, object result)
        {
            Type t = GetResultClrType(method);
            if (t == null) return default(ValueTask<int>);
            if (result == null)
                result = t.IsValueType ? Activator.CreateInstance(t) : null;
            Type vtOpen = typeof(ValueTask<>);
            Type vtClosed = vtOpen.MakeGenericType(t);
            System.Reflection.MethodInfo fromResult = vtClosed.GetMethod("FromResult", new[] { t });
            return fromResult.Invoke(null, new[] { result });
        }

        private static object CreateFaultedValueTask(CLRMethod method, Exception ex)
        {
            Type t = GetResultClrType(method);
            if (t == null) t = typeof(int);
            Type vtClosed = typeof(ValueTask<>).MakeGenericType(t);
            // ValueTask has no public FromException in all TFMs; build via a
            // faulted Task<T> (ValueTask<T>(Task<T>) ctor accepts a faulted task).
            Type taskClosed = typeof(Task<>).MakeGenericType(t);
            System.Reflection.MethodInfo fromEx = typeof(Task).GetMethod("FromException", new[] { typeof(Exception) });
            // Task.FromException is generic: Task<T>.FromException<T>(Exception).
            System.Reflection.MethodInfo fromExClosed = fromEx.MakeGenericMethod(t);
            object faultedTask = fromExClosed.Invoke(null, new object[] { ex });
            return Activator.CreateInstance(vtClosed, faultedTask);
        }
    }
}
#endif
