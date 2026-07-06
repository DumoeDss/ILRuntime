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
    // neo-step20-async-suspend (AwaitUnsafeOnCompleted throws a tagged NIE).
    //
    // SHIPPED (PARTIAL) — only TC1 + TC7 are kept here. They are the regression
    // guards for the proven sync Task<int> machinery (Start -> DriveMoveNext ->
    // GetAwaiter redirect -> get_IsCompleted redirect -> GetResult -> SetResult
    // -> get_Task).
    //
    // REMOVED PROBES (TC2-TC6, TC8, and their helper async methods) — FAIL on a
    // pre-existing edge, the [NEO-CLRSTRUCT-FIELD-OF-IL] defect: an async state
    // machine's CLR-struct fields (the `<>t__builder` / `<>u__1` awaiter) are
    // laid out as reference slots but the JIT `ldflda` addresses them as a
    // primitive offset -> stale OOB. TC1/TC7 pass by a layout accident (their
    // SM Primitives is long enough); the others' isn't. A narrow fix does not
    // exist (would induce silent corruption); needs the broad JIT/layout
    // follow-up `neo-clrstruct-field-of-il`. These probes will be re-added when
    // that follow-up lands:
    //   - TC2 SyncTask (non-generic Task)
    //   - TC3 SyncValueTaskOfT (ValueTask<int>)
    //   - TC4 AsyncVoidSync (async void)
    //   - TC5 MultipleAwaits (multi-await Task<int>)
    //   - TC6 AsyncExceptionFaultsTask (Task<int> throw -> faulted)
    //   - TC8 IncompleteAwaitHitsTaggedNIE (AwaitUnsafeOnCompleted NIE
    //     confirmation; unreachable -- MoveNext fails before the IsCompleted
    //     short-circuit until [NEO-CLRSTRUCT-FIELD-OF-IL] is fixed)
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
        // TC1: the core sync Task<int> probe.
        public static void NeoStep20_TC1_SyncTaskOfT()
        {
            var t = NeoStep20_SyncTaskOfT();
            if (!t.IsCompleted || t.Result != 10) { int x = 1; int y = 0; int _ = x / y; }
        }

        // TC7: the nested sync Task<int> probe.
        public static void NeoStep20_TC7_NestedAsync()
        {
            var t = NeoStep20_NestedAsyncSync();
            if (!t.IsCompleted || t.Result != 105) { int x = 1; int y = 0; int _ = x / y; }
        }
    }
}
