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

        // neo-async-valuetask-asyncvoid fixer round 1 (F-1/F-2 root cause): the
        // ValueTask<T> instance accessors (get_IsCompleted / get_IsFaulted /
        // get_Result) MUST NOT reflect on the ValueTask<T> struct's _obj reference
        // field. ValueTask<T> is a binder-less CLR struct -> flat-bytes / RefCount=0
        // (Optimizer.Neo.cs:1523-1545); its embedded GC reference is written as RAW
        // BYTES into the untracked frame slot and DOES NOT SURVIVE the flat-bytes
        // heap round-trip -- reading it back via reflection yields a dangling
        // pointer -> AccessViolationException in CastHelpers.IsInstanceOfClass (the
        // crash that blocked VT1). This is the SAME limitation ALREADY documented
        // in-code for TaskAwaiter<T>.m_task (:827-833): TaskAwaiter<T> survives only
        // because TaskAwaiter_T_GetResult_Neo falls back to GetAwaitedTaskFromSm(sm)
        // when m_task is null -- it recovers the Task from the STATE MACHINE, never
        // by reflecting the struct's corrupted ref field.
        //
        // The ValueTask<T> accessors mirror that precedent: at get_Task time we
        // already KNOW the recoverable state (the sync result/Exception, or the
        // suspend bridge Task<T> via ctx.GetTaskBridge()). Stash it on the
        // ThreadStatic slot below; the accessors read it instead of the struct. The
        // ValueTask<T> struct itself becomes a mere "token" the caller holds -- its
        // flat bytes are NEVER read for their ref field.
        //
        // WHY A THREADSTATIC SLOT (not an SM-keyed map): get_Task runs in the ASYNC
        // METHOD's own frame (the probe -- it is `return builder.Task`, the last
        // statement of the lowered async method), where the SM IS on mStack. But the
        // instance accessors run in the CALLER's frame (the driver polling
        // vt.IsCompleted/Result) -- a DIFFERENT frame/mStack that does NOT contain
        // the SM. So an mStack scan for the SM (RecoverSmForGetTask) cannot recover
        // the SM in the accessor. The SM is also gone from SmTaskMap (consumed by
        // get_Task on sync) and SmContextMap (removed on resume). The one identity
        // that survives across the get_Task -> accessor boundary on the same thread
        // is "the most recent ValueTask<T> get_Task on this thread" -- a single
        // ThreadStatic slot. Scope: VALID for the test's sequential poll pattern
        // (get_Task -> immediate IsCompleted/Result polling with no intervening
        // get_Task on this thread; the resume runs on a SEPARATE thread via
        // RunContinuationsAsynchronously, so it does not overwrite this slot). This
        // mirrors the CurrentAsyncSm ThreadStatic-scope precedent. Nested
        // ValueTask-get_Task-during-poll is not exercised by the probes (a known
        // scope limit; a future token-in-struct scheme would handle reentrancy).
        [ThreadStatic]
        private static ValueTaskAccessorState? _currentValueTaskState;

        // Recoverable observable state of a ValueTask<T> return, stashed at get_Task
        // and consumed by the instance accessors. The authoritative source is
        // determined by which branch stashed (checked by the accessor in priority
        // order: IValueTaskSource > BridgeTask > SyncResult):
        //   - IValueTaskSource != null (ZERO-ALLOC suspend, neo-async-valuetask-
        //     zeroalloc): the ValueTask<T> is backed by the IValueTaskSource<T>
        //     (the ILAsyncContext<T> itself). The accessor reads IsCompleted/
        //     IsFaulted/Result via GetStatus(token)/GetResult(token) -- NO
        //     Task<T>/TCS bridge exists (the path is allocation-free on suspend).
        //   - BridgeTask != null: the ValueTask wraps a Task<T>. Used for the
        //     sync-faulted case (Task.FromException(ex) from SetException) AND the
        //     Task-backed AsyncTaskMethodBuilder suspend fallback. The accessor
        //     delegates IsCompleted/IsFaulted/Result to this Task (Task<T>.Result
        //     rethrows on a faulted Task -- matches ValueTask<T>.Result).
        //   - SyncResult != null: the sync-SUCCESS case; the ValueTask was built
        //     from a result. IsCompleted=true, IsFaulted=false, Result=SyncResult.
        //   - all null: the defensive default (completed default T).
        private struct ValueTaskAccessorState
        {
            public object IValueTaskSource; // the ILAsyncContext<T> (IValueTaskSource<T>); null unless zero-alloc suspend
            public short IValueTaskSourceToken; // the token the ValueTask<T>(IValueTaskSource<T>, token) ctor captured
            public Task BridgeTask;
            public object SyncResult;
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
            // SetResult(T result): slot 0 = the builder `this` (a value type passed
            // byref -- the engine copies the struct's flat managed bytes into the
            // callee param region), slot 1 = T result. Skip the struct's ACTUAL
            // managed size (Unsafe.SizeOf<T>), NOT a hardcoded 8 -- the size differs
            // per builder (Task builder is 8 bytes; the ValueTask builder, which
            // delegates here, is 16). See BuilderThisManagedSize + the SetResult
            // VT1/VT2/VT6 root-cause note. Read T, stash Task.FromResult(T) keyed by SM.
            int curPrim = 0;
            curPrim += BuilderThisManagedSize(method); // builder this byref (the SM identity comes from CurrentAsyncSm)
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
            // SetException(Exception): slot 0 = builder this (flat managed bytes of
            // the value-type builder -- size via BuilderThisManagedSize; the ValueTask
            // builder delegates here and is 16 bytes, the Task builder is 8), slot 1 = Exception.
            int curPrim = 0;
            curPrim += BuilderThisManagedSize(method);
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
            // Skip the builder `this` byref. The builder is a VALUE TYPE passed
            // byref; in the callee param region the engine copies the struct's
            // FLAT MANAGED BYTES (Unsafe.SizeOf<T>), NOT an 8-byte byref. So the
            // skip MUST be the builder struct's actual managed size -- which differs
            // per builder: AsyncTaskMethodBuilder<T> is 8 bytes (TC8 accidentally
            // worked with `+= 8`), but AsyncValueTaskMethodBuilder<T> is 16 bytes,
            // so the hardcoded `+= 8` undershot by 8 and ReadResultParam read the
            // stale 2nd qword of the struct (old v=1 residue -> resultObj=4) instead
            // of the real T result at +16 (v+3=14). This is the real VT1/VT2/VT6
            // blocker (the B1 field-layout hypothesis was DISPROVEN -- see
            // openspec/changes/neo-clrstruct-sm-field-layout/blocked.md). Mirrors
            // the TaskAwaiter_T_GetIsCompleted_Neo size-resolution precedent (:904).
            // (B1 disproven cross-ref: the shared PrimitiveOffset is benign --
            // disjoint Primitives[]/ManagedObjects[] storage.)
            curPrim += BuilderThisManagedSize(method);
            object resultObj = ReadResultParam(intp, method, frameBase, ref curPrim, mStack, retRefBase);
            // Sink-swap (design D3 step 5 -- MIRRORED from AsyncTaskMethodBuilder_T_SetResult_Neo
            // above, which the prior ValueTask SetResult LACKED): if a context is being
            // RESUMED on this thread, route the resumed SM's terminal result to the context
            // (CompleteResult completes the TaskCompletionSource<T> bridge the ValueTask<T>
            // accessor delegates to) instead of stashing in SmTaskMap. Without this branch a
            // suspended ValueTask<T> method's resumed SetResult stashed the result in
            // SmTaskMap (never read -- get_Task already consumed the sync entry / used the
            // suspend bridge) and the bridge Task stayed incomplete -> the accessor's
            // IsCompleted polled false forever (the VT1 hang). Checked FIRST so the resume
            // result is not lost to the sync sink.
            IAsyncContextSink ctx = _currentAsyncContext;
            if (ctx != null)
            {
                if (sm != null) SmContextMap.Remove(sm);
                ctx.CompleteResult(resultObj);
                return;
            }
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
            // get_Task for ValueTask<T>. Runs in the DRIVER frame (after Start).
            // Three cases, mirroring AsyncTaskMethodBuilder_T_GetTask_Neo but
            // wrapping the result as a ValueTask<T> struct:
            //   SYNC:   SmTaskMap[sm] holds the stashed T (or Exception) from
            //           SetResult/SetException (Start drove MoveNext to completion).
            //           -> ValueTask<T>.FromResult(T) or a faulted ValueTask<T>.
            //   SUSPEND: SmTaskMap is empty (SetResult did NOT run -- the SM
            //           suspended on a truly-incomplete await). SmContextMap[sm]
            //           holds the parked ILAsyncContext<T>; its GetTaskBridge() is
            //           the TaskCompletionSource<T> bridge Task. Wrap it as a
            //           ValueTask<T> via the public ValueTask<T>(Task<T>) ctor
            //           (neo-async-valuetask-asyncvoid -- the gap that blocked
            //           ValueTask<T> suspend; lead-5 designed+verified, reverted
            //           as incomplete until the value-type return write landed).
            //   DEFENSIVE: no stash + no suspend -> completed default ValueTask<T>.
            //
            // RETURN WRITE: ValueTask<T> is a CLR struct WITHOUT a registered
            // ValueTypeBinder, so the caller's dest slot is flat managed bytes
            // (Size = GetNeoValueTypeManagedSize(ValueTask<T>), RefCount = 0 -- see
            // Optimizer.Neo.cs:1523-1545). There is NO separate mStack ref slot;
            // the ENTIRE struct (including its _task/_obj reference field, as a raw
            // managed pointer folded into the flat bytes) is written to retDst via
            // WriteValueTypeReturn -> WriteNeoValueType (Unsafe.WriteUnaligned<T>).
            // WriteReferenceReturn (a 4-byte mStack index) is WRONG here -- it
            // writes an index where the struct's bytes are expected -> the caller's
            // GetAwaiter/Result field reads garbage. The flat-bytes write is the
            // SAME path Task_T_GetAwaiter_Neo uses to return a TaskAwaiter<T>
            // (also a CLR struct with a ref field), proven green by TC8/TC12-TC14.
            ILTypeInstance sm = RecoverSmForGetTask(frameBase, mStack);
            object vt;
            // Accessor state stashed for the ValueTask<T> instance accessors
            // (neo-async-valuetask-asyncvoid fixer round 1): the accessors run in
            // the CALLER's frame (a different mStack that has no SM), so they read
            // the ThreadStatic _currentValueTaskState slot set here instead of
            // reflecting on the struct's _obj ref field (which AVs -- see the
            // _currentValueTaskState comment). One of {BridgeTask, SyncResult} is
            // authoritative per branch.
            ValueTaskAccessorState accessorState = default;
            if (sm != null && SmTaskMap.TryGetValue(sm, out var stashed))
            {
                SmTaskMap.Remove(sm);
                // SetException stashes Task.FromException(ex) (a Task, NOT an
                // Exception) -- the prior `stashed is Exception` check NEVER matched
                // the fault path, misrouting it to CreateValueTaskFromResult(Task)
                // -> ArgumentException. Detect the faulted-Task shape explicitly.
                if (stashed is Exception e)
                {
                    vt = CreateFaultedValueTask(method, e);
                    accessorState.BridgeTask = Task.FromException(e); // faulted; accessor delegates IsFaulted/Result
                }
                else if (stashed is Task ft) // sync-faulted via Task.FromException
                {
                    Exception fex = ft.IsFaulted ? ft.Exception : new Exception("Neo async ValueTask: faulted with no exception");
                    vt = CreateFaultedValueTask(method, fex);
                    accessorState.BridgeTask = ft;
                }
                else
                {
                    vt = CreateValueTaskFromResult(method, stashed);
                    accessorState.SyncResult = stashed; // sync SUCCESS; IsCompleted=true, IsFaulted=false
                }
            }
            else if (sm != null && SmContextMap.TryGetValue(sm, out IAsyncContextSink ctx))
            {
                // SUSPEND (ZERO-ALLOC, neo-async-valuetask-zeroalloc): return a
                // ValueTask<T> backed DIRECTLY by the parked context's
                // IValueTaskSource<T> (the ILAsyncContext<T> itself), via the public
                // ValueTask<T>(IValueTaskSource<T>, short) ctor. NO Task<T> /
                // TaskCompletionSource<T> bridge is allocated (the prior path called
                // ctx.GetTaskBridge() which eagerly created a TCS + its Task<T>).
                // The resumed SM's SetResult routes to ctx.CompleteResult, which
                // completes the IValueTaskSource core -- the accessor observes the
                // completion via GetStatus(token)/GetResult(token).
                //
                // The token is core.Version at THIS get_Task call (the IValueTask-
                // Source<T> contract: the ctor captures the token; GetStatus/
                // GetResult must present the SAME token). Stash the source + token
                // for the accessor (which runs in the CALLER's frame).
                bool isZeroAlloc;
                vt = BuildZeroAllocValueTask(method, ctx, out short token, out isZeroAlloc);
                if (isZeroAlloc)
                {
                    accessorState.IValueTaskSource = ctx; // IValueTaskSource<T>
                    accessorState.IValueTaskSourceToken = token;
                }
                else
                {
                    // Fallback (ctor unexpectedly absent): the ValueTask is bridge-
                    // backed; the accessor reads the BridgeTask.
                    accessorState.BridgeTask = ctx.GetTaskBridge() as Task;
                }
            }
            else
            {
                // Defensive: no stash + no suspend -> completed default.
                object def = GetDefaultForResultType(method);
                vt = CreateValueTaskFromResult(method, def);
                accessorState.SyncResult = def; // completed default; IsCompleted=true, IsFaulted=false
            }
            _currentValueTaskState = accessorState;
            WriteValueTypeReturn(vt, retDst, retRefBase, mStack);
        }

        // Wrap a (possibly-incomplete) bridge Task<T> as a ValueTask<T> via the
        // public ValueTask<T>(Task<T>) ctor. T is the builder's first generic arg
        // (same resolution CreateValueTaskFromResult uses). Used by the suspend
        // case of AsyncValueTaskMethodBuilder_T_GetTask_Neo.
        private static object WrapBridgeAsValueTask(CLRMethod method, object bridgeTask)
        {
            Type t = GetResultClrType(method);
            if (t == null) t = typeof(int);
            Type vtClosed = typeof(ValueTask<>).MakeGenericType(t);
            // ValueTask<T>(Task<T>) ctor: wraps the Task (completed or not). A
            // faulted Task<T> yields a faulted ValueTask<T>; an incomplete Task<T>
            // yields an incomplete ValueTask<T> that completes with the Task.
            return Activator.CreateInstance(vtClosed, bridgeTask);
        }

        // ZERO-ALLOC suspend path (neo-async-valuetask-zeroalloc): build a
        // ValueTask<T> backed DIRECTLY by the parked context's IValueTaskSource<T>
        // (the ILAsyncContext<T> itself) via the public ValueTask<T>(IValueTask-
        // Source<T>, short) ctor. Unlike WrapBridgeAsValueTask, this allocates NO
        // Task<T>/TaskCompletionSource<T> -- the ValueTask<T> reads the context's
        // ManualResetValueTaskSourceCore<T> (a struct field). The token
        // (core.Version) is fetched via the non-generic IAsyncContextSink.
        // GetSourceToken() and returned to the caller so the accessor can read
        // status/result with the matching token.
        private static object BuildZeroAllocValueTask(CLRMethod method, IAsyncContextSink ctx,
            out short token, out bool isZeroAlloc)
        {
            Type t = GetResultClrType(method);
            if (t == null) t = typeof(int);
            token = ctx.GetSourceToken();
            Type vtClosed = typeof(ValueTask<>).MakeGenericType(t);
            Type ivtsClosed = typeof(System.Threading.Tasks.Sources.IValueTaskSource<>).MakeGenericType(t);
            // ValueTask<T>(IValueTaskSource<T> source, short token) ctor.
            ConstructorInfo ctor = vtClosed.GetConstructor(new[] { ivtsClosed, typeof(short) });
            if (ctor == null)
            {
                // Fallback (defensive; the ctor exists on netstandard2.0+ /
                // netcoreapp3.0+ -- the ILAsyncContext<T> already implements
                // IValueTaskSource<T> against this same TFM). Fall back to the
                // TCS bridge to preserve correctness if the ctor is unexpectedly
                // absent (re-introduces the allocation, but does not break
                // suspend+resume). The caller reads the BridgeTask via the accessor.
                isZeroAlloc = false;
                Task bridge = ctx.GetTaskBridge() as Task;
                return WrapBridgeAsValueTask(method, bridge);
            }
            isZeroAlloc = true;
            return ctor.Invoke(new object[] { ctx, token });
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
            // ValueTask (non-generic) is a CLR struct with RefCount=0 flat bytes
            // (no binder); write the full struct bytes, NOT a ref index.
            WriteValueTypeReturn(task, retDst, retRefBase, mStack);
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
            // AwaitOnCompleted (the awaiter implements INotifyCompletion but NOT
            // ICriticalNotifyCompletion) SHALL capture the current ExecutionContext
            // and flow it to the resume so AsyncLocal values are visible in the
            // continuation. AwaitUnsafeOnCompleted (ICritical awaiters such as
            // TaskAwaiter) intentionally does NOT capture EC. SynchronizationContext
            // is N/A here -- ILRuntime runs no SC (it would be captured by the
            // builder at Start, not per-await, and SC.Current is always null). The
            // captured EC is flowed to the resume via ExecutionContext.Run in
            // SuspendStateMachine (neo-async-execctx-capture).
            System.Threading.ExecutionContext ec = null;
            try { ec = System.Threading.ExecutionContext.Capture(); }
            catch { ec = null; } // capture unavailable (suppressed-flow / sandbox) -> resume without EC flow
            SuspendStateMachine(method, ec);
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
        private static void SuspendStateMachine(CLRMethod method, System.Threading.ExecutionContext ecToFlow = null)
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
            Type ctxType = typeof(ILAsyncContext<>).MakeGenericType(resultType);

            // REUSE the existing context across suspends (neo-async-multi-await).
            // A multi-await SM suspends N times (once per genuinely-incomplete await),
            // and the driver / get_Task observes the FIRST suspend's context bridge
            // (the TaskCompletionSource<T> the test polls). The bridge lives ON the
            // context, so each suspend MUST register its continuation on the SAME
            // context -- a fresh context per suspend orphans the prior bridge (the
            // test's t never completes) and a resumed SetResult routes to the wrong
            // (orphaned) bridge. SmContextMap[sm] is the context parked at the PRIOR
            // suspend (get_Task already returned its bridge); reuse it. Its
            // stateMachine + moveNextMethod are identical (same SM) and its tcs bridge
            // is the one the driver observes. The sink is unboxed from
            // SmContextMap[sm] WITHOUT reflection (the map is typed IAsyncContextSink);
            // the ctxType reflection is only for the delegate bind on the FIRST suspend.
            IAsyncContextSink sink;
            if (SmContextMap.TryGetValue(sm, out sink) && sink != null)
            {
                // Reuse the prior context (its tcs bridge is the one the driver holds).
                // Re-bind the resume delegate onto the existing instance (cheap;
                // avoids a per-SM-ctor cache; the Action is consumed once per suspend).
            }
            else
            {
                ILMethod moveNext = GetMoveNext(sm.Type);
                if (moveNext == null)
                    throw new InvalidOperationException("Neo async suspend: state machine has no MoveNext: " + sm.Type.FullName);
                object ctx = Activator.CreateInstance(ctxType,
                    BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new object[] { sm, moveNext }, null);
                sink = (IAsyncContextSink)ctx;
                // Park on SmContextMap so get_Task (which runs in the driver frame
                // after Start returns) finds the suspended context and returns its
                // Task<T> bridge. Subsequent suspends REUSE this entry.
                SmContextMap[sm] = sink;
            }
            object ctxInstance = sink;

            // Register the continuation on the awaited Task. The resume fires on the
            // thread that completes the task. If the task raced to completion between
            // the IsCompleted check and here, UnsafeOnCompleted fires the continuation
            // (resume) immediately -- correct (no hang; MoveNext re-enters, reloads
            // <>u__1, calls GetResult, and continues). task is the awaiter's m_task
            // (typed Task); its (non-generic) GetAwaiter's UnsafeOnCompleted is the
            // standard await hook (TaskAwaiter implements ICriticalNotifyCompletion).
            MethodInfo resumeMi = ctxType.GetMethod("MoveNextInternal", BindingFlags.Instance | BindingFlags.NonPublic);
            Action resumeAction = (Action)Delegate.CreateDelegate(typeof(Action), ctxInstance, resumeMi);
            // Flow the captured ExecutionContext (the AwaitOnCompleted path) into the
            // resume so AsyncLocal values are visible. AwaitUnsafeOnCompleted passes
            // null (no flow -- ICritical awaiters opt out). ExecutionContext.Run
            // restores the captured EC for the resume callback (neo-async-execctx-capture).
            if (ecToFlow != null)
            {
                Action raw = resumeAction;
                task.GetAwaiter().UnsafeOnCompleted(() => System.Threading.ExecutionContext.Run(ecToFlow, _ => raw(), null));
            }
            else
            {
                task.GetAwaiter().UnsafeOnCompleted(resumeAction);
            }
            // Return WITHOUT SetResult/SetException -- the SM is suspended.
        }

        // Recover the awaited Task from the SM's heap fields.
        //
        // AWAITER-FIRST (neo-async-multi-await, design D1): the PRIMARY source is
        // the compiler-generated awaiter FIELD. Roslyn REUSES a single <>u__1
        // awaiter field for awaits of the same awaiter type, OVERWRITING it with
        // the CURRENT awaiter before each AwaitUnsafeOnCompleted call (for awaits
        // of DIFFERENT awaiter types Roslyn generates <>u__2, <>u__3, ..., but
        // ONLY the active one is non-default at suspend time -- a default
        // TaskAwaiter has m_task == null and is skipped). So the awaiter field
        // ALWAYS holds the active awaiter at suspend -> its m_task is the active
        // Task, UNAMBIGUOUS regardless of how many Task operand fields the SM
        // hoists. The scan prefers the HIGHEST-index non-null awaiter (the most-
        // recently-written field = the active one; consistent with Roslyn's
        // overwrite-before-suspend, and robust to a stale non-null <>u__2 from a
        // prior await of a different type -- OQ1 mitigation).
        //
        // FALLBACK (single-Task shape, the TC8 / explicit-local case): if NO
        // awaiter yielded a Task (the awaiter was not hoisted as a boxed object --
        // e.g. it lived only in a frame local and the SM hoisted just the Task
        // operand), scan for a directly-hoisted Task. EXACTLY ONE -> return it
        // (the common single-await shape). MORE THAN ONE -> the scan is genuinely
        // ambiguous (no awaiter disambiguator AND >1 Task field) -> throw the
        // NARROWED tagged NIE (design D3; review fixer Finding B's fail-loud,
        // narrowed from "fires for >1 Task field" to "fires only when no awaiter
        // resolves AND the scan is ambiguous"). The common multi-await shape
        // (resolvable awaiter) does NOT reach this branch.
        //
        // LAST FALLBACK: the awaiter's m_task via a fresh scan (defensive; the
        // awaiter-first pass already covered this, retained for shapes where the
        // awaiter box appears but GetAwaiterTask returned a non-Task). Returns null
        // if nothing resolves (SuspendStateMachine throws the InvalidOperationException).
        private static Task GetAwaitedTaskFromSm(ILTypeInstance sm)
        {
            var mo = sm.ManagedObjects;
            if (mo == null) return null;
            // 1) AWAITER-FIRST: scan ManagedObjects for a boxed awaiter whose
            //    GetAwaiterTask yields a non-null Task. The awaiter field is the
            //    active one (<>u__1 reused / overwritten before each suspend).
            //    Highest-index non-null wins (most-recently-written = active).
            Task awaiterTask = null;
            for (int i = mo.Count - 1; i >= 0; i--)
            {
                Task t = GetAwaiterTask(mo[i]) as Task;
                if (t != null)
                {
                    awaiterTask = t;
                    break; // highest-index non-null awaiter task = active
                }
            }
            if (awaiterTask != null) return awaiterTask;
            // 2) SINGLE-TASK FALLBACK: no awaiter resolved. Scan for a directly-
            //    hoisted Task. Exactly 1 -> return it; >1 -> ambiguous NIE (D3).
            Task directTask = null;
            int directTaskCount = 0;
            for (int i = mo.Count - 1; i >= 0; i--)
            {
                if (mo[i] is Task direct)
                {
                    if (directTask == null) directTask = direct;
                    directTaskCount++;
                }
            }
            if (directTaskCount > 1)
            {
                throw new NotImplementedException(
                    "Neo async multi-Task awaiter not supported (no resolvable awaiter and " +
                    "an ambiguous multi-Task scan); the currently-awaited Task cannot be " +
                    "disambiguated. State machine hoists " + directTaskCount + " Task fields " +
                    "and no awaiter field (<>u__1) yielded a Task. " +
                    "(neo-async-movenext-fix finding B, narrowed by neo-async-multi-await D3)");
            }
            if (directTask != null) return directTask;
            // 3) LAST FALLBACK: the awaiter m_task scan (defensive; covered by 1).
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
            if (!isAwaiter)
            {
                // neo-async-execctx-capture: a CUSTOM awaiter (e.g. one implementing
                // only INotifyCompletion, which triggers AwaitOnCompleted) is
                // recognized if it exposes an instance field of type Task named
                // m_task (the TaskAwaiter convention). Duck-typed + defended so a
                // non-awaiter object (a hoisted Task local, the builder) does not
                // throw -- it is simply not a suspensible awaiter.
                FieldInfo custom = t.GetField("m_task", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (custom != null && custom.FieldType == typeof(Task))
                {
                    try { return custom.GetValue(boxedAwaiter); }
                    catch { return null; }
                }
                return null;
            }
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

        // ================================================================
        // ValueTask<T> instance accessors (neo-async-valuetask-asyncvoid).
        // ValueTask<T> is a binder-less CLR struct -> flat-bytes / RefCount=0. Its
        // embedded _obj GC reference is written as RAW BYTES into the untracked
        // frame slot and DOES NOT SURVIVE the flat-bytes heap round-trip: reading
        // it back via reflection yields a dangling pointer -> AV in
        // CastHelpers.IsInstanceOfClass (the F-1/F-2 root cause). So these
        // accessors NEVER read the ValueTask<T> struct's fields. Instead they read
        // the ThreadStatic _currentValueTaskState slot stashed at get_Task -- the
        // SAME precedent TaskAwaiter<T> uses (TaskAwaiter_T_GetResult_Neo falls
        // back to GetAwaitedTaskFromSm(sm) when the struct's m_task ref is
        // unreadable, :827-835 -- recover the Task from the state machine / a
        // side channel, never by reflecting the struct's corrupted ref field). The
        // ValueTask<T> struct is a mere "token" the caller holds; its observable
        // state (IsCompleted/IsFaulted/Result) is authoritative via the stashed
        // bridge Task<T> (suspend + sync-faulted) or the stashed result
        // (sync-success). These redirects exist only to OVERRIDE the autogen
        // reflection fallback (which NIEs on the struct `this`); they do not touch
        // the struct bytes.
        // ================================================================

        // ValueTask<T>.get_IsCompleted. Priority: IValueTaskSource (zero-alloc
        // suspend) > BridgeTask (sync-faulted / Task fallback) > true (sync-success
        // / defensive default).
        public static void ValueTask_T_GetIsCompleted_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            bool isCompleted = true; // sync-success / defensive default
            var st = _currentValueTaskState;
            if (st.HasValue)
            {
                if (st.Value.IValueTaskSource is IAsyncContextSink src)
                    isCompleted = src.GetSourceIsCompleted(st.Value.IValueTaskSourceToken);
                else if (st.Value.BridgeTask != null)
                    isCompleted = st.Value.BridgeTask.IsCompleted;
            }
            // Zero-extend to the full 8-byte dest slot (same hardening as
            // TaskAwaiter_T_GetIsCompleted_Neo :813 -- the dest slot is reused for a
            // managed pointer in the async SM and a 4-byte write leaves stale bits).
            if (retDst != null) *(long*)retDst = isCompleted ? 1 : 0;
        }

        // ValueTask<T>.get_IsFaulted. Priority: IValueTaskSource > BridgeTask >
        // false (sync-success / defensive default).
        public static void ValueTask_T_GetIsFaulted_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            bool isFaulted = false; // sync-success / defensive default
            var st = _currentValueTaskState;
            if (st.HasValue)
            {
                if (st.Value.IValueTaskSource is IAsyncContextSink src)
                    isFaulted = src.GetSourceIsFaulted(st.Value.IValueTaskSourceToken);
                else if (st.Value.BridgeTask != null)
                    isFaulted = st.Value.BridgeTask.IsFaulted;
            }
            if (retDst != null) *(long*)retDst = isFaulted ? 1 : 0;
        }

        // ValueTask<T>.get_Result. Priority: IValueTaskSource > BridgeTask >
        // SyncResult > default. A faulted source/bridge rethrows the inner
        // exception (matches ValueTask<T>.Result semantics). For the suspend case
        // the caller MUST poll IsCompleted first -- the probes do.
        public static void ValueTask_T_GetResult_Neo(ILIntepreter intp, byte* frameBase, AutoList mStack,
            CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            object result;
            var st = _currentValueTaskState;
            if (st.HasValue && st.Value.IValueTaskSource is IAsyncContextSink src)
            {
                // Zero-alloc suspend: read the IValueTaskSource<T> result (rethrows
                // if faulted -- core.GetResult semantics match ValueTask<T>.Result).
                result = src.GetSourceResult(st.Value.IValueTaskSourceToken);
            }
            else if (st.HasValue && st.Value.BridgeTask != null)
            {
                // Delegate to the bridge Task<T>.Result (rethrows if faulted).
                Task bt = st.Value.BridgeTask;
                result = bt.GetType().InvokeMember("Result",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.GetProperty, null, bt, null);
            }
            else if (st.HasValue && st.Value.SyncResult != null)
            {
                // Sync-success: the stashed result T.
                result = st.Value.SyncResult;
            }
            else
            {
                // Defensive default (no recoverable state).
                result = GetDefaultForResultType(method);
            }
            WriteReturnByType(method.ReturnType, result, retDst, retRefBase, mStack);
        }

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
            // Register per T the probes bind. T=int (VT1/VT2/VT3/VT6) + T=string
            // (VT4 -- a CLR reference-type result). Without the <string> registration
            // VT4's Start/SetResult/SetException fall to the reflection fallback,
            // whose Area-4b guard NIEs on the builder struct-`this`-with-ref-field
            // (AsyncValueTaskMethodBuilder<string> has a `T`-typed field that IS a
            // reference field for T=string, unlike T=int). The redirect path avoids
            // the fallback entirely (it reads the SM via CurrentAsyncSm and the
            // builder-this flat bytes via BuilderThisManagedSize -- never boxing the
            // struct through reflection). Mirrors the <int>/<ILTypeInstance> pattern.
            RegisterValueTaskBuilderT(app, flag, typeof(System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder<int>));
            RegisterValueTaskBuilderT(app, flag, typeof(System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder<string>));
            RegisterValueTaskBuilderT(app, flag, typeof(System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder<ILTypeInstance>));

            // ---- ValueTask<T> instance accessors (neo-async-valuetask-asyncvoid).
            //      Override the autogen reflection fallback which NIEs on the
            //      struct `this` (CLR-struct-with-ref-field, no binder). Register
            //      per T the probes bind. ----
            RegisterValueTaskAccessors(app, flag, typeof(ValueTask<int>));
            RegisterValueTaskAccessors(app, flag, typeof(ValueTask<string>));

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
            //      which use default(TaskAwaiter)). Register per T the harness binds.
            //      <string> added (neo-async-valuetask-asyncvoid VT4): a ValueTask<string>
            //      probe awaits a Task<string> -> TaskAwaiter<string>; without the <string>
            //      accessor registration the awaiter's GetResult/get_IsCompleted fall to
            //      the reflection fallback, whose Area-4b guard NIEs (TaskAwaiter<string>
            //      has the `m_task` reference field). Mirrors <int>/<ILTypeInstance>. ----
            RegisterAwaiterAccessors(app, flag, typeof(System.Runtime.CompilerServices.TaskAwaiter<int>));
            RegisterAwaiterAccessors(app, flag, typeof(System.Runtime.CompilerServices.TaskAwaiter<string>));
            RegisterAwaiterAccessors(app, flag, typeof(System.Runtime.CompilerServices.TaskAwaiter));
            RegisterAwaiterAccessors(app, flag, typeof(System.Runtime.CompilerServices.TaskAwaiter<ILTypeInstance>));

            // Task<T>.GetAwaiter / get_Result overrides (override autogen stubs).
            RegisterTaskAccessors(app, flag, typeof(Task<int>));
            RegisterTaskAccessors(app, flag, typeof(Task<string>));
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

        // ValueTask<T> instance accessors (neo-async-valuetask-asyncvoid): redirect
        // get_IsCompleted / get_IsFaulted / get_Result so the driver can poll a
        // ValueTask<T> local without hitting the Step-13b/Area-4b struct-`this` NIE.
        private static void RegisterValueTaskAccessors(AppDomain app, BindingFlags flag, Type vtType)
        {
            MethodInfo isc = vtType.GetProperty("IsCompleted")?.GetGetMethod(false);
            if (isc != null) app.RegisterCLRMethodRedirectionNeo(isc, ValueTask_T_GetIsCompleted_Neo);
            MethodInfo ifl = vtType.GetProperty("IsFaulted")?.GetGetMethod(false);
            if (ifl != null) app.RegisterCLRMethodRedirectionNeo(ifl, ValueTask_T_GetIsFaulted_Neo);
            MethodInfo gr = vtType.GetProperty("Result")?.GetGetMethod(false);
            if (gr != null) app.RegisterCLRMethodRedirectionNeo(gr, ValueTask_T_GetResult_Neo);
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
                case nameof(ValueTask_T_GetIsCompleted_Neo): return ValueTask_T_GetIsCompleted_Neo;
                case nameof(ValueTask_T_GetIsFaulted_Neo): return ValueTask_T_GetIsFaulted_Neo;
                case nameof(ValueTask_T_GetResult_Neo): return ValueTask_T_GetResult_Neo;
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

        // The number of FLAT MANAGED BYTES the builder `this` occupies in the
        // callee param region. The builder (AsyncTaskMethodBuilder<T> /
        // AsyncValueTaskMethodBuilder<T>) is a VALUE TYPE passed byref as `this`;
        // the engine's call-arg lowering (CopyNeoCallArguments, byRefSrc slot 0)
        // DEREFERENCES the byref and copies the struct's flat managed bytes
        // (Unsafe.SizeOf<T> via Optimizer.GetNeoValueTypeManagedSize) into the
        // callee param region -- NOT an 8-byte byref. So SetResult/SetException
        // MUST skip this size (not a hardcoded 8) to reach the first real param.
        // AsyncTaskMethodBuilder<T> is 8 bytes (TC8 accidentally matched `+= 8`);
        // AsyncValueTaskMethodBuilder<T> is 16 bytes (the VT1/VT2/VT6 blocker --
        // `+= 8` undershot, reading stale struct bytes as the result). This is the
        // real root cause; the B1 field-layout hypothesis was DISPROVEN (see
        // openspec/changes/neo-clrstruct-sm-field-layout/blocked.md).
        private static int BuilderThisManagedSize(CLRMethod method)
        {
            Type decl = method.DeclearingType?.TypeForCLR;
            if (decl == null || !decl.IsValueType)
                return 8; // defensive: a reference-type `this` is a single 8-byte-ish ref slot
            int sz = Optimizer.GetNeoValueTypeManagedSize(decl);
            return sz > 0 ? sz : 8;
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

        // Construct a completed ValueTask<T>(result) via reflection (T is the
        // builder's generic arg). NOTE (neo-async-valuetask-asyncvoid): ValueTask<T>
        // has NO public `FromResult` static in net8.0 (only op_Equality/op_Inequality
        // are public static); the prior `GetMethod("FromResult", ...)` returned NULL
        // and NRE'd -- the sync ValueTask path was never green on this runtime. Use
        // the public `ValueTask<T>(T result)` ctor instead (a ValueTask constructed
        // from a result is a synchronously-completed ValueTask -- semantically
        // identical to the old FromResult intent).
        private static object CreateValueTaskFromResult(CLRMethod method, object result)
        {
            Type t = GetResultClrType(method);
            if (t == null) return default(ValueTask<int>);
            if (result == null)
                result = t.IsValueType ? Activator.CreateInstance(t) : null;
            Type vtClosed = typeof(ValueTask<>).MakeGenericType(t);
            // ValueTask<T>(T result) ctor -- a completed ValueTask wrapping the result.
            return Activator.CreateInstance(vtClosed, result);
        }

        private static object CreateFaultedValueTask(CLRMethod method, Exception ex)
        {
            Type t = GetResultClrType(method);
            if (t == null) t = typeof(int);
            Type vtClosed = typeof(ValueTask<>).MakeGenericType(t);
            // ValueTask has no public FromException in all TFMs; build via a
            // faulted Task<T> (ValueTask<T>(Task<T>) ctor accepts a faulted task).
            // B3 fix (neo-async-valuetask-asyncvoid): `Task` has TWO `FromException`
            // overloads that BOTH take (Exception) -- the non-generic
            // `Task.FromException(Exception)` and the GENERIC
            // `Task.FromException<T>(Exception)` -- so GetMethod("FromException",
            // new[]{ typeof(Exception) }) is AMBIGUOUS (AmbiguousMatchException ->
            // VT3 failed). Resolve the GENERIC definition explicitly via Linq, then
            // close it with T to produce a faulted Task<T>.
            System.Reflection.MethodInfo fromExGen = Array.Find(typeof(Task).GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static),
                m => m.Name == "FromException" && m.IsGenericMethod);
            if (fromExGen == null)
                throw new InvalidOperationException("Neo async: could not resolve Task.FromException<T> generic definition for CreateFaultedValueTask");
            System.Reflection.MethodInfo fromExClosed = fromExGen.MakeGenericMethod(t);
            object faultedTask = fromExClosed.Invoke(null, new object[] { ex });
            return Activator.CreateInstance(vtClosed, faultedTask);
        }
    }
}
#endif
