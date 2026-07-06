#if ENABLE_NEO_MODE
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using ILRuntime.CLR.Method;
using ILRuntime.CLR.TypeSystem;

namespace ILRuntime.Runtime.Intepreter
{
    // Step 20 (neo-step20-async) -- ILAsyncContext<T>: the IValueTaskSource<T> /
    // IAsyncStateMachine bridge skeleton for the deferred suspend/resume slice.
    //
    // SYNC SCOPE (this change): only the IValueTaskSource<T> live surface +
    // SetResultSync/SetExceptionSync ship. The MoveNext() resumption body
    // throws a tagged NotImplementedException (the suspend slice owns it).
    // The sync Task getter does NOT use this type -- it returns Task<T> /
    // ValueTask<T>.FromResult directly. The skeleton ships so the type exists
    // and is unit-probed, de-risking the suspend-slice continuation plumbing
    // (the ManualResetValueTaskSourceCore<T> integration).
    internal sealed class ILAsyncContext<T> : IValueTaskSource<T>, IAsyncStateMachine
    {
        // The hoisted state machine (null on the sync path -- the SM stays
        // in-frame / on the heap as the caller's own object there).
        internal ILTypeInstance stateMachine;
        // smType.MoveNext() (cached by the suspend slice when it hoists).
        internal ILMethod moveNextMethod;

        private ManualResetValueTaskSourceCore<T> core;

        // --- sync shortcut (used by a future ValueTask<T> getter on the
        //     suspend slice; NOT used by the sync Task getter) ---
        internal void SetResultSync(T result) => core.SetResult(result);
        internal void SetExceptionSync(Exception e) => core.SetException(e);

        // --- IValueTaskSource<T> (live; the core handles token races) ---
        public T GetResult(short token) => core.GetResult(token);
        public ValueTaskSourceStatus GetStatus(short token) => core.GetStatus(token);
        public void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
            => core.OnCompleted(continuation, state, token, flags);

        // --- IAsyncStateMachine.MoveNext (the resumption entry; DEFERRED slice) ---
        void IAsyncStateMachine.MoveNext()
        {
            throw new NotImplementedException("Neo async resumption: neo-step20-async-suspend (Step 20 suspend slice)");
        }

        void IAsyncStateMachine.SetStateMachine(IAsyncStateMachine stateMachine)
        {
            // No-op (the sync slice never uses the CLR interface).
        }
    }
}
#endif
