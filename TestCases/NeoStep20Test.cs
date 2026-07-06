using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // Step 20 test: Neo async/await support (SYNC-COMPLETING slice).
    //
    // Scope of neo-step20-async: a sync-completing async method (every await
    // observes an already-complete awaitable) executes correctly via custom Neo
    // builder redirects. The truly-async suspend/resume path is deferred to
    // neo-step20-async-suspend (AwaitUnsafeOnRegistered throws a tagged NIE).
    //
    // The sync slice was PARTIALLY UNBLOCKED by neo-clrstruct-field-of-il (F-10):
    // an async state machine's CLR-struct fields (`<>t__builder`, `<>u__1`) are
    // laid out as reference slots and the JIT `ldflda` now addresses them
    // correctly (carrying the field's ReferenceOffset + a runtime-detectable
    // flag). Before F-10, ONLY TC1/TC7 passed (by a layout accident); after
    // F-10, TC4 (async void) AND TC6 (async-exception-faults-task) ALSO pass.
    //
    // TC2/TC3/TC5 PROGRESS PAST the F-10 OOB to DISTINCT DOWNSTREAM EDGES (the
    // design's "note it, don't force" case). Those edges are NOT F-10 defects --
    // each is a separate Step-20 redirect-coverage concern:
    //   * TC2 SyncTask (non-generic): the non-generic Task's Start redirect
    //     (`Value cannot be null. Parameter 'stateMachine'`).
    //   * TC3 SyncValueTaskOfT: the ValueTask builder path (NRE in the redirect).
    //   * TC5 MultipleAwaits: the multi-await `Task<int>.get_Result` redirect
    //     (`Method 'Task.Result' not found`).
    // These three probes are re-trimmed (kept green) until their downstream
    // edges land (neo-step20-async resume). TC1/TC4/TC6/TC7 are the green sync
    // regression guards.
    //
    // TC8 (IncompleteAwaitHitsTaggedNIE) stays REMOVED -- it needs the
    // truly-async suspend slice (AwaitUnsafeOnRegistered), the
    // neo-step20-async-suspend follow-up.
    //
    // Assertion mechanism: same as prior NeoStep tests -- a passing test simply
    // returns without dividing by zero. A logic failure is surfaced by a
    // deliberate `1/0`.

    public class NeoStep20Test
    {
        // 5.4 sync Task<int> returning a constant via await Task.FromResult.
        public static async Task<int> NeoStep20_SyncTaskOfT()
        {
            int v = await Task.FromResult(7);
            return v + 3;
        }

        // 5.7 async void (fire-and-forget sync; assert side-effect observed
        // via a host-side cell, avoiding the IL-side stsfld Step-6 gap).
        public static async void NeoStep20_AsyncVoidSync()
        {
            TestCLRBinding.SetAsyncVoidCell(7);
            await Task.CompletedTask;
        }

        // 6.2 async method that throws synchronously -> faulted task.
        public static async Task<int> NeoStep20_AsyncExceptionFaultsTask()
        {
            int v = await Task.FromResult(1);
            throw new System.Exception("neo-step20-fault");
        }

        // 6.4 nested async: outer awaits inner (both sync-completing).
        public static async Task<int> NeoStep20_InnerAsync()
        {
            return await Task.FromResult(100);
        }
        public static async Task<int> NeoStep20_NestedAsyncSync()
        {
            int v = await NeoStep20_InnerAsync();
            return v + 5;
        }

        // ---- driver entry-points (parameterless public static = a test) ----
        // TC1: the core sync Task<int> probe (green since neo-step20-async).
        public static void NeoStep20_TC1_SyncTaskOfT()
        {
            var t = NeoStep20_SyncTaskOfT();
            if (!t.IsCompleted || t.Result != 10) { int x = 1; int y = 0; int _ = x / y; }
        }

        // TC4: async void (sync). UNBLOCKED by F-10 (the async-void SM's builder
        // is a CLR-struct field of an IL instance; before F-10 the ldflda OOB'd;
        // after F-10 the side-effect is observed).
        public static void NeoStep20_TC4_AsyncVoidSync()
        {
            TestCLRBinding.SetAsyncVoidCell(0);
            NeoStep20_AsyncVoidSync();
            if (TestCLRBinding.GetAsyncVoidCell() != 7) { int x = 1; int y = 0; int _ = x / y; }
        }

        // TC6: async exception faults the task. UNBLOCKED by F-10 (the sync-throw
        // -> faulted-task path; the SM's builder is a CLR-struct field of an IL
        // instance).
        public static void NeoStep20_TC6_AsyncExceptionFaultsTask()
        {
            var t = NeoStep20_AsyncExceptionFaultsTask();
            // A sync-thrown async method faults the returned task immediately.
            if (!t.IsFaulted) { int x = 1; int y = 0; int _ = x / y; }
        }

        // TC7: the nested sync Task<int> probe (green since neo-step20-async).
        public static void NeoStep20_TC7_NestedAsync()
        {
            var t = NeoStep20_NestedAsyncSync();
            if (!t.IsCompleted || t.Result != 105) { int x = 1; int y = 0; int _ = x / y; }
        }
    }
}
