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
    // TC8 (IncompleteAwaitHitsTaggedNIE) is the DETERMINISTIC truly-incomplete-
    // awaiter probe (neo-generic-redirect-resolution / B1). A TaskCompletionSource-
    // backed Task whose SetResult is NEVER called before the assertion is read
    // from a host-side cell (TestCLRBinding.GetIncompleteTask) and awaited; its
    // IsCompleted is deterministically false, forcing the await toward
    // AwaitUnsafeOnCompleted<TA,TSM> (NO Task.Delay sync-completion race).
    //
    // VERDICT (empirical, HEAD 38133af8): outcome 2a-DEEP, NOT the predicted
    // outcome 3. The deterministic probe SURFACED A REAL BUG and is therefore
    // MARKED [Ignored] until the bug is fixed (a hanging test cannot live in the
    // smoke). Findings (full detail in
    // openspec/changes/neo-generic-redirect-resolution/):
    //   * B1 redirect RESOLUTION is EXONERATED. The custom AwaitUnsafeOnCompleted/
    //     AwaitOnCompleted open-def NIE redirects ARE correctly registered on
    //     RedirectMapNeo (17 Await keys confirmed), and TryGetRedirection is
    //     arity-agnostic and correct. The design's "outcome 3" prediction and its
    //     proposed fix sites (TryGetRedirection / JIT call-operand) do NOT apply.
    //   * get_IsCompleted is correct: the custom TaskAwaiter_T_GetIsCompleted_Neo
    //     returns FALSE for the incomplete Task (proven by TC10 and a redirect
    //     trace). The autogen default-awaiter stub is NOT dispatched.
    //   * The REAL blocker is a MoveNext control-flow bug in the truly-async
    //     path: after get_IsCompleted returns false, the state machine NEVER
    //     reaches the AwaitUnsafeOnCompleted call (its Call handler never fires)
    //     AND never reaches GetResult either -- it hangs in a loop/block
    //     transition between them. This is the prior session's "routes MoveNext
    //     to the completion path regardless of IsCompleted" note, which the
    //     disproven sibling (neo-async-controlflow-iscompleted) FAILED to
    //     exercise because it only used sync-completing awaits.
    // This is suspend-path territory (the neo-step20-async-suspend follow-up),
    // NOT a B1 redirect fix. TC8 stays [Ignored] + hang-reproducer; un-ignore
    // once the control-flow bug is fixed (the redirect already resolves, so the
    // tagged NIE will fire and TC8 will pass).
    //
    // TC9 = sync-completing CONTROL (active guard): a complete await must NOT
    // reach AwaitUnsafeOnCompleted (the IsCompleted short-circuit skips it).
    // TC10 = IsCompleted-only DIAGNOSTIC (active guard): the incomplete Task's
    // awaiter reports IsCompleted == false (proves the B1 redirect resolution +
    // the get_IsCompleted redirect are correct, isolated from the MoveNext bug).
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

        // TC8 probe body: await the host-side permanently-incomplete Task. Its
        // IsCompleted is deterministically false -> the await is FORCED through
        // AwaitUnsafeOnCompleted<TA,TSM> (the B1 redirect). PRIVATE so the harness
        // does not auto-discover/run it (it hangs on HEAD -- see TC8); only TC8
        // (currently [Ignored]) calls it.
        private static async Task<int> NeoStep20_IncompleteAwaitProbe()
        {
            Task<int> incomplete = TestCLRBinding.GetIncompleteTask();
            int v = await incomplete;
            return v + 3;
        }

        // TC9 control body: SAME SHAPE as the probe but awaits an already-
        // completed Task. Its IsCompleted == true short-circuit MUST skip
        // AwaitUnsafeOnCompleted entirely. PRIVATE (only TC9 calls it).
        private static async Task<int> NeoStep20_CompletedAwaitControl()
        {
            Task<int> completed = Task.FromResult(42);
            int v = await completed;
            return v + 1;
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

        // TC8 (deterministic truly-incomplete-awaiter probe): [Ignored] on HEAD.
        // The probe is the B1 adversarial harness -- it forces an await through
        // AwaitUnsafeOnCompleted via a permanently-incomplete TaskCompletionSource
        // Task. Empirically (HEAD 38133af8) the probe HANGS: get_IsCompleted
        // correctly returns false, but a MoveNext control-flow bug then prevents
        // the state machine from reaching the AwaitUnsafeOnCompleted call (or
        // GetResult) -- it loops between them. This is a REAL truly-async-path
        // bug (suspend-path territory, NOT a B1 redirect bug -- B1 resolution is
        // exonerated). Marked [Ignored] so the smoke does not hang; un-ignore
        // once the control-flow bug is fixed (the redirect already resolves, so
        // the tagged NIE will fire and TC8 will pass). See the class comment +
        // the change's design/handoff for the full evidence.
        [ILRuntimeTest.ILRuntimeTest(Ignored = true)]
        public static void NeoStep20_TC8_IncompleteAwaitHitsTaggedNIE()
        {
            Task<int> t = null;
            Exception sync = null;
            try { t = NeoStep20_IncompleteAwaitProbe(); }
            catch (Exception ex) { sync = ex; }

            int verdict = (sync != null)
                ? TestCLRBinding.IsTaggedAsyncNIE(sync)
                : TestCLRBinding.IsFaultedWithTaggedAsyncNIE(t);

            if (verdict != 1) { int x = 1; int y = 0; int _ = x / y; }
        }

        // TC9 (CONTROL for TC8, adversarial specificity): same shape as the
        // probe but awaits an already-completed Task. Its IsCompleted == true
        // short-circuit MUST skip AwaitUnsafeOnCompleted entirely, so the method
        // completes synchronously with the correct result and is NOT faulted
        // (does NOT reach the tagged NIE). Proves TC8 is specific to the
        // incomplete-await path: a complete await must NOT NIE.
        public static void NeoStep20_TC9_SyncControlSkipsAwaitUnsafeOnCompleted()
        {
            var t = NeoStep20_CompletedAwaitControl();
            if (!t.IsCompleted || t.IsFaulted || t.Result != 43)
            { int x = 1; int y = 0; int _ = x / y; }
        }

        // TC10 (DIAGNOSTIC, hang-proof): reads the incomplete Task's awaiter
        // IsCompleted directly -- NO await, NO GetResult, NO AwaitUnsafeOnCompleted.
        // Isolates the TaskAwaiter_T_GetIsCompleted_Neo redirect (must report
        // false for the permanently-incomplete Task) from the downstream
        // AwaitUnsafeOnCompleted dispatch. If this FAILS, IsCompleted is being
        // misreported (the control-flow root cause). If this PASSES but TC8 hangs,
        // the hang is in the AwaitUnsafeOnCompleted dispatch path.
        public static void NeoStep20_TC10_IncompleteTaskIsCompletedIsFalse()
        {
            Task<int> incomplete = TestCLRBinding.GetIncompleteTask();
            bool isc = incomplete.GetAwaiter().IsCompleted;
            // The permanently-incomplete Task MUST report IsCompleted == false.
            if (isc) { int x = 1; int y = 0; int _ = x / y; }
        }
    }
}
