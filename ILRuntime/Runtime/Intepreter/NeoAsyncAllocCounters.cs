using System;

namespace ILRuntime.Runtime.Intepreter
{
    // PUBLIC allocation counters for the Neo async ValueTask<T> zero-alloc path
    // (neo-async-valuetask-zeroalloc). NOT gated by ENABLE_NEO_MODE: it is a pure
    // static counter (zero cost on Legacy; the Neo-only allocation sites that
    // increment it live in ILAsyncContext.cs which IS Neo-gated), and it MUST be
    // visible to the non-Neo build of ILRuntimeTestBase / TestCases (the test
    // harness compiles against the plain Debug -- non-Neo -- ILRuntime DLL; the
    // TestCases DLL is then loaded by the Debug_Neo CLI at run time, where the
    // Neo-gated increments fire).
    //
    // Public (not internal) so the test harness (ILRuntimeTestBase, a separate
    // assembly with NO InternalsVisibleTo) can reset + read the counters. The
    // internal Neo async code increments these at the exact allocation sites,
    // giving a decisive per-suspend measurement (GC byte deltas are muddied by
    // the shared Activator box + the MoveNext frame + JIT caches).
    //
    //   BridgeTaskAllocs: increments each time a TaskCompletionSource<T> bridge
    //     is allocated (the allocation this child eliminates for the ValueTask
    //     path; the Task path still allocates one).
    //   ContextAllocs: increments each time an ILAsyncContext<T> is constructed
    //     (the IValueTaskSource holder -- present on BOTH paths; the zero-alloc
    //     claim is about the BRIDGE, not the context).
    public static class NeoAsyncAllocCounters
    {
        public static int BridgeTaskAllocs;
        public static int ContextAllocs;
        public static void Reset() { BridgeTaskAllocs = 0; ContextAllocs = 0; }
    }
}
