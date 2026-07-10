#if ENABLE_NEO_MODE
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using ILRuntime.CLR.Method;
using ILRuntime.CLR.TypeSystem;

namespace ILRuntime.Runtime.Intepreter
{
    // Non-generic sink interface the SetResult/SetException redirects route the
    // resumed-SM result through (the sink-swap, design D3 step 5). The resumed
    // MoveNext sets the redirect's ThreadStatic _currentAsyncContext to this sink
    // BEFORE ExecuteNeo; the redirects check it FIRST and, if set, complete the
    // sink instead of stashing in SmTaskMap. Keeps the generic ILAsyncContext<T>
    // callable from the non-generic redirect code without reflection on every
    // SetResult.
    internal interface IAsyncContextSink
    {
        void CompleteResult(object result);
        void CompleteException(Exception ex);
        // The Task<T> the async method's get_Task returns when the SM suspended
        // (the TaskCompletionSource<T> bridge, design D4 / OQ3 first cut).
        object GetTaskBridge();

        // The IValueTaskSource<T> token (core.Version) for the ZERO-ALLOC path
        // (neo-async-valuetask-zeroalloc). The ValueTask<T>(IValueTaskSource<T>,
        // short) ctor captures this token; the accessor reads status/result with
        // the same token. Non-generic so the redirect (which holds an
        // IAsyncContextSink) can fetch it without reflection on the closed T.
        short GetSourceToken();

        // IValueTaskSource<T> status/result accessors for the ZERO-ALLOC path
        // (neo-async-valuetask-zeroalloc). The ValueTask<T> instance accessors
        // (get_IsCompleted / get_IsFaulted / get_Result) run in the CALLER's frame
        // and delegate to these instead of a Task<T> bridge. Non-generic box-
        // returning signatures so the redirect (non-generic, holds IAsyncContext-
        // Sink) can call them without reflection on the closed T.
        bool GetSourceIsCompleted(short token);
        bool GetSourceIsFaulted(short token);
        object GetSourceResult(short token);
    }

    // Step 20 (neo-step20-async) + neo-async-movenext-fix -- ILAsyncContext<T>: the
    // IValueTaskSource<T> / IAsyncStateMachine bridge for the truly-async
    // suspend/resume path.
    //
    // SYNC SCOPE (neo-step20-async): only the IValueTaskSource<T> live surface +
    // SetResultSync/SetExceptionSync shipped; MoveNext threw the tagged NIE.
    //
    // SUSPEND/RESUME SCOPE (neo-async-movenext-fix): the MoveNext() resumption
    // runs (Piece 3). On suspend, AwaitUnsafeOnCompleted_Neo constructs this
    // context, parks it on SmContextMap[sm], and registers the awaited task's
    // UnsafeOnCompleted continuation (MoveNextInternal). When the task completes,
    // MoveNextInternal -> CLRRedirectionsAsyncNeo.ResumeAsync acquires a fresh
    // pooled interpreter, restores the heap SM as slot-0 this, sets
    // _currentAsyncContext = this, and ExecuteNeo resumes the SM at the await
    // state -> GetResult -> SetResult/SetException, which route to this sink
    // (CompleteResult/CompleteException) and complete the TCS bridge. The async
    // method's get_Task returns the TCS bridge (GetTaskBridge) while suspended.
    //
    // OQ3 resolution (neo-async-valuetask-zeroalloc): the Task<T> bridge is a
    // LAZY TaskCompletionSource<T> -- allocated ONLY if the Task-backed
    // AsyncTaskMethodBuilder<T> suspend path requests it via GetTaskBridge().
    // The ValueTask<T> suspend path returns a ValueTask<T> backed by THIS
    // context's IValueTaskSource<T> (the `core` ManualResetValueTaskSourceCore
    // -- a struct field, zero heap alloc) via the ValueTask<T>(IValueTask-
    // Source<T>, short) ctor, ELIMINATING the TCS/Task<T> allocation.
    internal sealed class ILAsyncContext<T> : IValueTaskSource<T>, IAsyncStateMachine, IAsyncContextSink
    {
        // The hoisted state machine (a heap ILTypeInstance for the common async SM;
        // HoistNeoILValueToHeap is the escape valve for the in-frame-VT-local edge).
        internal ILTypeInstance stateMachine;
        // smType.MoveNext() (cached by the suspend slice when it suspends).
        internal ILMethod moveNextMethod;

        // Constructed by the suspend redirect via reflection (T is runtime-
        // determined from the builder's generic arg). The 2-arg ctor avoids field
        // reflection in the (allocation-sensitive) suspend path.
        internal ILAsyncContext(ILTypeInstance sm, ILMethod moveNext)
        {
            stateMachine = sm;
            moveNextMethod = moveNext;
            NeoAsyncAllocCounters.ContextAllocs++;
        }

        // The IValueTaskSource<T> core. This is the ZERO-ALLOC completion source
        // (neo-async-valuetask-zeroalloc): it is a STRUCT field (no heap alloc),
        // and the suspend path returns a ValueTask<T> backed DIRECTLY by this
        // IValueTaskSource<T> via the public ValueTask<T>(IValueTaskSource<T>,
        // short) ctor -- NO Task<T>/TaskCompletionSource<T> bridge allocation.
        private ManualResetValueTaskSourceCore<T> core;

        // The Task<T> bridge -- LAZY (neo-async-valuetask-zeroalloc). Previously
        // this was a `readonly` field initialized in the ctor, which allocated a
        // TaskCompletionSource<T> (+ its Task<T>) on EVERY context construction --
        // the allocation this child eliminates. The zero-alloc path (the
        // IValueTaskSource-backed ValueTask<T>) NEVER touches this field, so the
        // TCS is created ONLY if a Task-backed fallback is explicitly requested
        // via GetTaskBridge() (the AsyncTaskMethodBuilder<T> suspend path still
        // needs a Task<T>; the ValueTask path does not). RunContinuations-
        // Asynchronously avoids inline continuation stack-dives when the resume
        // thread completes the TCS while a polling caller holds the call stack.
        private TaskCompletionSource<T> tcs;

        // --- sync shortcut (reserved for a future ValueTask<T> getter; NOT used
        //     by the sync Task getter or the suspend Task bridge) ---
        internal void SetResultSync(T result) => core.SetResult(result);
        internal void SetExceptionSync(Exception e) => core.SetException(e);

        // The IValueTaskSource<T> token for the zero-alloc path. Captured at
        // get_Task (when the ValueTask<T>(IValueTaskSource<T>, token) is built)
        // so the accessor redirects can read status/result with the matching
        // token. core.Version is stable until a SetResult/SetException completes
        // the source (single-completion suspend model: the context is completed
        // once at the resumed SetResult; multi-await REUSES the context but the
        // driver observes only the first suspend's ValueTask, so the core is
        // completed exactly once -> the token is stable for that completion).
        public short GetSourceToken() => core.Version;

        // IValueTaskSource<T> accessors for the ZERO-ALLOC path (neo-async-
        // valuetask-zeroalloc). The ValueTask<T> instance accessors delegate here
        // (via the ThreadStatic _currentValueTaskState stashed at get_Task) instead
        // of to a Task<T> bridge. The core validates the token; a stale token
        // (post-completion re-read after Reset) throws -- but the single-
        // completion suspend model never Resets, so the token is stable.
        public bool GetSourceIsCompleted(short token)
        {
            var s = core.GetStatus(token);
            return s == ValueTaskSourceStatus.Succeeded
                || s == ValueTaskSourceStatus.Faulted
                || s == ValueTaskSourceStatus.Canceled;
        }
        public bool GetSourceIsFaulted(short token)
        {
            return core.GetStatus(token) == ValueTaskSourceStatus.Faulted;
        }
        public object GetSourceResult(short token)
        {
            // core.GetResult rethrows the stored exception on a faulted source
            // (matches ValueTask<T>.Result semantics -- the accessor's contract).
            return core.GetResult(token);
        }

        // --- IAsyncContextSink (the sink-swap target) ---
        // neo-async-valuetask-zeroalloc: complete the IValueTaskSource<T> core
        // (the zero-alloc path) UNCONDITIONALLY -- the ValueTask<T> the accessor
        // polls is backed by `core`, so it MUST be completed for the accessor to
        // observe IsCompleted/Result. The TCS bridge is completed ONLY if it was
        // lazily created (the Task-backed AsyncTaskMethodBuilder<T> path); the
        // zero-alloc ValueTask path leaves tcs null (no allocation).
        public void CompleteResult(object result)
        {
            T value;
            try { value = result == null ? default : (T)result; }
            catch (InvalidCastException)
            {
                core.SetException(new InvalidOperationException("Neo async: result type mismatch on resume"));
                tcs?.SetException(new InvalidOperationException("Neo async: result type mismatch on resume"));
                return;
            }
            core.SetResult(value);
            tcs?.SetResult(value);
        }
        public void CompleteException(Exception ex)
        {
            core.SetException(ex);
            tcs?.SetException(ex);
        }
        // The Task<T> bridge -- LAZY. Only the Task-backed suspend path
        // (AsyncTaskMethodBuilder_T_GetTask_Neo) calls this; the zero-alloc
        // ValueTask path (AsyncValueTaskMethodBuilder_T_GetTask_Neo) does NOT.
        // Creating the TCS here (on first request) keeps the zero-alloc path
        // allocation-free: a context that only ever backs a ValueTask never
        // allocates a TCS.
        public object GetTaskBridge()
        {
            if (tcs == null)
            {
                tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                NeoAsyncAllocCounters.BridgeTaskAllocs++;
            }
            return tcs.Task;
        }

        // --- IValueTaskSource<T> (live; the core handles token races) ---
        public T GetResult(short token) => core.GetResult(token);
        public ValueTaskSourceStatus GetStatus(short token) => core.GetStatus(token);
        public void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
            => core.OnCompleted(continuation, state, token, flags);

        // --- IAsyncStateMachine.MoveNext (the resumption entry). Called by the
        //     awaited task's UnsafeOnCompleted continuation (registered in
        //     AwaitUnsafeOnCompleted_Neo) on the thread that completed the task. ---
        void IAsyncStateMachine.MoveNext() => MoveNextInternal();

        // The actual resume entry (also the Action target bound at suspend time).
        // Internal so the suspend redirect can reflect a delegate onto it without
        // navigating the explicit IAsyncStateMachine interface mapping.
        internal void MoveNextInternal()
        {
            // ResumeAsync acquires a fresh pooled interpreter, restores the heap SM
            // as slot-0 this, sets _currentAsyncContext = this, and runs ExecuteNeo
            // to terminal state. The pool request/free is balanced in its finally
            // (the Step-19 F1 lesson).
            ILRuntime.Runtime.Enviorment.CLRRedirectionsAsyncNeo.ResumeAsync(stateMachine, moveNextMethod, this);
        }

        void IAsyncStateMachine.SetStateMachine(IAsyncStateMachine stateMachine)
        {
            // No-op (the Neo path never uses the CLR interface).
        }
    }
}
#endif
