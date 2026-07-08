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
    // OQ3 resolution (this child): the Task<T> bridge is a TaskCompletionSource<T>
    // (simplest, one alloc). A zero-alloc custom Task<T> from
    // IValueTaskSource<T> is a later optimization (Non-Goal).
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
        }

        private ManualResetValueTaskSourceCore<T> core;

        // The Task<T> bridge returned by get_Task while suspended. Completed by
        // CompleteResult/CompleteException when the resumed MoveNext reaches
        // SetResult/SetException. RunContinuationsAsynchronously avoids inline
        // continuation stack-dives when the resume thread completes the TCS while
        // a polling caller holds the call stack.
        private readonly TaskCompletionSource<T> tcs =
            new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        // --- sync shortcut (reserved for a future ValueTask<T> getter; NOT used
        //     by the sync Task getter or the suspend Task bridge) ---
        internal void SetResultSync(T result) => core.SetResult(result);
        internal void SetExceptionSync(Exception e) => core.SetException(e);

        // --- IAsyncContextSink (the sink-swap target; completes the TCS bridge) ---
        public void CompleteResult(object result)
        {
            // result is the boxed SetResult(T) value read by the redirect. Unbox to T.
            T value;
            try { value = result == null ? default : (T)result; }
            catch (InvalidCastException) { tcs.SetException(new InvalidOperationException("Neo async: result type mismatch on resume")); return; }
            core.SetResult(value);
            tcs.SetResult(value);
        }
        public void CompleteException(Exception ex)
        {
            core.SetException(ex);
            tcs.SetException(ex);
        }
        public object GetTaskBridge() => tcs.Task;

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
