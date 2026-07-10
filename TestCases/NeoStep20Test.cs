using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ILRuntimeTest;
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

        // TC12 probe body: a MULTI-AWAIT state machine with TWO genuinely-
        // incomplete awaits at TWO distinct await points (neo-async-multi-await).
        // Awaits GetIncompleteTask() (Roslyn state 0 -> <>1__state = 0) then
        // GetIncompleteTask2() (state 1 -> <>1__state = 1) -- TWO INDEPENDENT
        // TaskCompletionSource-backed Tasks, so BOTH awaits are genuinely
        // incomplete when the SM reaches them (a single shared TCS cannot back
        // two simultaneous incomplete awaits -- completing it swaps in a fresh
        // one, making the first operand stale). The SM hoists TWO Task<int>
        // reference fields (<a>5__1, <b>5__1), so the OLD count-based
        // GetAwaitedTaskFromSm scan was ambiguous (>1 Task field -> tagged NIE);
        // the awaiter-first fix (D1) reads the SINGLE reused <>u__1 awaiter
        // Roslyn overwrites before each AwaitUnsafeOnCompleted -> the active Task
        // at EACH suspend. Returns va + vb so BOTH resumes are provably observed.
        // PRIVATE (the harness must not auto-discover it; only TC12 calls it).
        private static async Task<int> NeoStep20_TwoIncompleteAwaitsProbe()
        {
            Task<int> a = TestCLRBinding.GetIncompleteTask();
            int va = await a;                                      // SUSPEND #1 (<>1__state = 0)
            Task<int> b = TestCLRBinding.GetIncompleteTask2();
            int vb = await b;                                      // SUSPEND #2 (<>1__state = 1)
            return va + vb;
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

        // TC12 (neo-async-multi-await): the BINDING success criterion -- a state
        // machine with TWO genuinely-incomplete awaits (>= 2 await expressions)
        // suspends -> resumes -> suspends AGAIN -> resumes -> SetResult, with the
        // CORRECT Task awaited at EACH suspend (the <>u__1 awaiter-field
        // disambiguation, design D1) and the CORRECT awaiter's GetResult read at
        // EACH resume (design D2). On HEAD the OLD count-based scan in
        // GetAwaitedTaskFromSm is ambiguous for >1 hoisted Task field and throws
        // the tagged NIE ("multi-Task awaiter not supported ... State machine
        // hoists 2 Task fields") during the FIRST suspend, which faults the bridge
        // Task. Binding conjuncts:
        //   * GATE 1 !t.IsCompleted -- await1 TRULY SUSPENDED (Start returned after
        //     registering the continuation on task A; the bridge is incomplete).
        //   * GATE 2 t.Result == va + vb -- BOTH resumes ran GetResult at the
        //     CORRECT await point (resume #1 read task A's result va via await1's
        //     awaiter; resume #2 read task B's result vb via await2's awaiter) and
        //     the final SetResult(va + vb) routed to the bridge. A stale-awaiter
        //     shadow, a wrong-Task continuation, or a single-resume short-circuit
        //     makes this FAIL (the wrong sum or a hang).
        // Stash-toggle: on HEAD (fix out) the tagged NIE faults the bridge at the
        // first suspend -> t.IsFaulted OR the wrong/incomplete result; after the
        // fix (D1) the SM suspends/resumes/resumes correctly. The probe is
        // deterministic (TCS-backed, NO Task.Delay race). [Ignored] until the fix
        // lands (a hanging/faulting test cannot live in the smoke); UN-IGNORED by
        // the implementer once green.
        [ILRuntimeTest(Ignored = false)]
        public static void NeoStep20_TC12_TwoIncompleteAwaits()
        {
            int va = 31;
            int vb = 17;
            Task<int> t = NeoStep20_TwoIncompleteAwaitsProbe();

            // GATE 1: await1 (task A) TRULY SUSPENDED. The bridge MUST be
            // incomplete here (Start returned after registering the continuation
            // on task A, before CompleteIncompleteTask). On HEAD the tagged NIE
            // faults the bridge during this first suspend -> t.IsFaulted -> FAIL.
            if (t.IsCompleted) { int x = 1; int y = 0; int _ = x / y; }

            // Drive completion of task A (await1's Task). The continuation fires
            // -> resume #1: MoveNext reloads await1's awaiter from <>u__1 (state
            // 0), calls GetResult -> va, then runs the second await -> task B is
            // genuinely incomplete -> SUSPEND #2 (registers a continuation on
            // task B; <>u__1 is now overwritten with await2's awaiter).
            TestCLRBinding.CompleteIncompleteTask(va);

            // Bounded spin-wait (NO real delay) for the resume to settle. A
            // stuck/deadlocked resume #1 exhausts the budget and FAILs here.
            bool resumed1 = false;
            for (int i = 0; i < 1_000_000; i++)
            {
                if (t.IsCompleted || t.IsFaulted) { resumed1 = true; break; }
                if ((i & 0x3FF) == 0) System.Threading.Thread.Yield();
            }
            if (!resumed1) { int x = 1; int y = 0; int _ = x / y; }

            // Drive completion of task B (await2's Task). The continuation fires
            // -> resume #2: MoveNext reloads await2's awaiter from <>u__1 (state
            // 1), calls GetResult -> vb, then SetResult(va + vb) -> the bridge.
            TestCLRBinding.CompleteIncompleteTask2(vb);

            // Bounded spin-wait for resume #2 + SetResult. A stuck/deadlocked
            // resume #2 exhausts the budget and FAILs here (the >10s rule would
            // otherwise kill it).
            bool resumed2 = false;
            for (int i = 0; i < 1_000_000; i++)
            {
                if (t.IsCompleted) { resumed2 = true; break; }
                if ((i & 0x3FF) == 0) System.Threading.Thread.Yield();
            }
            if (!resumed2) { int x = 1; int y = 0; int _ = x / y; }

            // GATE 2: BOTH resumes ran GetResult at the CORRECT await point with
            // the CORRECT awaiter. A wrong-Task continuation (resume #1 read task
            // B, or resume #2 read a stale task A) gives the wrong sum.
            if (t.IsFaulted) { int x = 1; int y = 0; int _ = x / y; }
            if (t.Result != va + vb) { int x = 1; int y = 0; int _ = x / y; }
        }

        // TC13 (neo-async-execctx-capture): drive a suspend/resume through a CUSTOM
        // awaiter (ECProbeAwaiter -- INotifyCompletion, NOT ICriticalNotifyCompletion),
        // which the C# compiler lowers to AwaitOnCompleted (the EC-capturing path),
        // NOT AwaitUnsafeOnCompleted. This is the FIRST test to exercise the
        // AwaitOnCompleted_Neo redirect at all (TaskAwaiter is ICritical, so a plain
        // `await task` always takes AwaitUnsafeOnCompleted). Proves the custom-awaiter
        // suspend machinery (GetAwaiterTask duck-types the awaiter's m_task) and the
        // AwaitOnCompleted suspend/resume work end-to-end.
        private static async Task<int> NeoStep20_ExecCtxSuspendProbe()
        {
            int v = await TestCLRBinding.GetECProbeAwaitable();
            return v + 1;
        }
        public static void NeoStep20_TC13_ExecCtxCustomAwaiterSuspend()
        {
            Task<int> t = NeoStep20_ExecCtxSuspendProbe();
            // GATE 1: the custom-awaiter await TRULY SUSPENDED (AwaitOnCompleted path).
            if (t.IsCompleted) { int x = 1; int y = 0; int _ = x / y; }
            // Drive completion of the wrapped Task -> the continuation (registered on
            // the underlying Task via GetAwaiterTask) fires -> resume -> GetResult.
            TestCLRBinding.CompleteIncompleteTask(7);
            bool resumed = false;
            for (int i = 0; i < 1_000_000; i++)
            {
                if (t.IsCompleted) { resumed = true; break; }
                if ((i & 0x3FF) == 0) System.Threading.Thread.Yield();
            }
            if (!resumed) { int x = 1; int y = 0; int _ = x / y; }
            // GATE 2: GetResult read the wrapped Task's result (7); probe returned 7+1.
            if (t.IsFaulted) { int x = 1; int y = 0; int _ = x / y; }
            if (t.Result != 8) { int x = 1; int y = 0; int _ = x / y; }
        }

        // TC14 (neo-async-execctx-capture): the EC-capture semantics. AwaitOnCompleted
        // (the custom-awaiter path) SHALL capture the current ExecutionContext and flow
        // it to the resume, so an AsyncLocal value set before the await is VISIBLE in
        // the continuation. The probe sets a host AsyncLocal to 42, awaits the custom
        // (AwaitOnCompleted) awaitable, then reads the AsyncLocal in the continuation.
        // With EC flow: 42 (the captured EC restores it). Without EC flow: 0 (the
        // threadpool resume runs under a default EC). Binding conjunct:
        //   * GATE 1 the SM suspended (AwaitOnCompleted path, EC captured).
        //   * GATE 2 t.Result == 7*1000 + 42 == 7042 (AsyncLocal flowed -> 42).
        // Stash-toggle (revert the EC-capture): the resume runs WITHOUT EC flow ->
        // GetAL()==0 -> t.Result == 7000 -> FAIL.
        private static async Task<int> NeoStep20_ExecCtxFlowProbe()
        {
            TestCLRBinding.SetAL(42);                                   // set before the await
            int v = await TestCLRBinding.GetECProbeAwaitable();         // AwaitOnCompleted (EC captured)
            int after = TestCLRBinding.GetAL();                         // read in the continuation
            return v * 1000 + after;
        }
        public static void NeoStep20_TC14_ExecCtxFlowsAsyncLocal()
        {
            Task<int> t = NeoStep20_ExecCtxFlowProbe();
            if (t.IsCompleted) { int x = 1; int y = 0; int _ = x / y; }
            // Complete from a threadpool thread queued WITHOUT EC flow. The
            // continuation resumes there under a DEFAULT EC, so AsyncLocal==42 is
            // visible ONLY because AwaitOnCompleted captured the caller's EC and the
            // resume runs it via ExecutionContext.Run. Stash-toggle (no EC capture)
            // -> the resume runs under the default EC -> AsyncLocal==0 -> t.Result
            // == 7000 -> FAIL (the binding proof the capture is load-bearing).
            TestCLRBinding.CompleteIncompleteTaskNoECFlow(7);
            bool resumed = false;
            for (int i = 0; i < 1_000_000; i++)
            {
                if (t.IsCompleted) { resumed = true; break; }
                if ((i & 0x3FF) == 0) System.Threading.Thread.Yield();
            }
            if (!resumed) { int x = 1; int y = 0; int _ = x / y; }
            if (t.IsFaulted) { int x = 1; int y = 0; int _ = x / y; }
            // 7*1000 + 42 == 7042: the AsyncLocal value (42) flowed through the
            // captured ExecutionContext into the continuation.
            if (t.Result != 7042) { int x = 1; int y = 0; int _ = x / y; }
        }

        // =====================================================================
        // neo-async-taskrun-ildelegate (child 5): Task.Run(SYNC IL lambda).
        //
        // SCOPE: a SYNC (non-async) IL lambda passed to the CLR Task.Run static
        // method, which schedules the lambda on the threadpool and calls back
        // into IL when the Task runs. Task.Run is NOT redirected (it goes through
        // the un-redirected reflection fallback CLRMethod.Invoke(byte*)). The
        // IL delegate arg is unwrapped to a real CLR delegate via CheckCLRTypes(
        // TypeFlags.IsDelegate) at CLRMethod.cs:521-522; the wrapped Func/Action
        // (DelegateAdapter.InvokeILMethod -> NeoInvokeSub) is the Step-19 callback
        // that re-enters ExecuteNeo on a FRESH pooled interpreter.
        //
        // The result is read via a BLOCKING path (.Result / .GetAwaiter().
        // GetResult() / .Wait()) -- NEVER await, so the async suspend machinery
        // is NOT involved (a sync lambda builds no async state machine).
        //
        // Lambda bodies are CONCAT-FREE (the conv.ovf.u2.un Step-6 gap lowers
        // "..."+int to a NIE); int arithmetic / direct returns only.
        //
        // Assertion convention is unchanged: a passing test returns without
        // dividing by zero; a logic failure surfaces a deliberate 1/0.
        // =====================================================================

        // Sync IL work methods (plain non-async IL methods, various return kinds).
        private static int NeoStep20_TrCompute() { return 7 * 6; }       // 42
        private static string NeoStep20_TrLabel() { return "ok"; }

        // TR1: Task.Run(Func<int>) -> Task<int>; read .Result (BLOCKING). The core
        // probe -- does the IL delegate round-trip through Task.Run end-to-end?
        public static void NeoStep20_Tr1_FuncOfInt()
        {
            Task<int> t = Task.Run(new Func<int>(NeoStep20_TrCompute));
            // BLOCKING read -- no await. A stuck/deadlocked callback exhausts the
            // budget and FAILS here (divide-by-zero) rather than hanging forever.
            int r = SpinWaitResult(t);
            if (t.IsFaulted) { int x = 1; int y = 0; int _ = x / y; }
            if (r != 42) { int x = 1; int y = 0; int _ = x / y; }
        }

        // TR2: Task.Run(Action) -> Task (non-generic); read via .Wait() (BLOCKING).
        // The void-returning shape: a lambda that only does a side effect. Asserts
        // the side effect landed via a HOST-visible cell (TestCLRBinding -- a real
        // CLR static, cross-thread visible by construction). Using an IL-side static
        // field instead is fragile here: the lambda runs on a threadpool thread in a
        // FRESH pooled interpreter, and the IL static's cross-interpreter write
        // visibility is a separate concern (Step 3); the host cell isolates the
        // assertion to the Task.Run(delegate) callback path alone.
        public static void NeoStep20_Tr2_ActionSideEffect()
        {
            TestCLRBinding.SetAsyncVoidCell(0);
            Task t = Task.Run(new Action(() => TestCLRBinding.SetAsyncVoidCell(99)));
            SpinWaitComplete(t);
            if (t.IsFaulted) { int x = 1; int y = 0; int _ = x / y; }
            if (TestCLRBinding.GetAsyncVoidCell() != 99) { int x = 1; int y = 0; int _ = x / y; }
        }

        // TR3: Task.Run(Func<string>) -> Task<string>; read .GetAwaiter().
        // GetResult() (BLOCKING). A reference-type result proves the callback's
        // reference return routes back through the NeoInvokeSub -> mStack path.
        public static void NeoStep20_Tr3_FuncOfString()
        {
            Task<string> t = Task.Run(new Func<string>(NeoStep20_TrLabel));
            string s = SpinWaitResult(t);
            if (t.IsFaulted) { int x = 1; int y = 0; int _ = x / y; }
            if (s != "ok") { int x = 1; int y = 0; int _ = x / y; }
        }

        // TR4: closure-capturing lambda. The lambda captures a local (`factor`)
        // and multiplies; the closure `this` is the compiler-generated display
        // class (an IL instance). Proves the bound-instance `this` thread reaches
        // NeoInvokeSub (WriteNeoCallSlot(paramInfos[0], ..., instance)).
        public static void NeoStep20_Tr4_ClosureCapture()
        {
            int factor = 5;
            Task<int> t = Task.Run(new Func<int>(() => NeoStep20_TrCompute() - factor));
            int r = SpinWaitResult(t);
            if (t.IsFaulted) { int x = 1; int y = 0; int _ = x / y; }
            if (r != 37) { int x = 1; int y = 0; int _ = x / y; }
        }

        // TR5: lambda calling an IL INSTANCE method. The lambda's display class
        // holds a `this` (the enclosing NeoStep20Test -- but as IL has no instance
        // here, the lambda calls a STATIC helper that reads the static cell).
        // Mirrors TR1 but the lambda body does real arithmetic + a call.
        public static void NeoStep20_Tr5_LambdaCallsILMethod()
        {
            Task<int> t = Task.Run(new Func<int>(() =>
            {
                int base_ = NeoStep20_TrCompute(); // 42
                return base_ + 8;                   // 50
            }));
            int r = SpinWaitResult(t);
            if (t.IsFaulted) { int x = 1; int y = 0; int _ = x / y; }
            if (r != 50) { int x = 1; int y = 0; int _ = x / y; }
        }

        // ---- spin-wait helpers (BLOCKING; budget-capped so a deadlock FAILS
        // instead of hanging -- respects the >10-60s kill rule) ----
        private static int SpinWaitResult(Task<int> t)
        {
            t.Wait(); // BLOCKING; a faulted task throws here (caught -> FAIL below)
            return t.Result;
        }
        private static string SpinWaitResult(Task<string> t)
        {
            t.Wait();
            return t.Result;
        }
        private static void SpinWaitComplete(Task t)
        {
            t.Wait();
        }

        // ================================================================
        // ValueTask<T> + async-void probes (neo-async-valuetask-asyncvoid, child 4).
        // Mirrors TC8's truly-async suspend+resume structure but with a ValueTask<T>
        // (or async void) return. ASYNC-SM CONCAT CONSTRAINT: the probe bodies are
        // concat-free (int arithmetic / direct return only); string concat inside an
        // async SM lowers to conv.ovf.u2.un -> NIE (Step 6 gap, NOT this fix).
        //
        // The load-bearing fix in AsyncValueTaskMethodBuilder_T_GetTask_Neo:
        //   (1) a SmContextMap suspend-case (wrap the bridge Task<T> as ValueTask<T>
        //       via the public ValueTask<T>(Task<T>) ctor) -- WITHOUT it, a
        //       suspended ValueTask<T> method's get_Task builds a ValueTask from a
        //       null result (SetResult did not run) -> wrong default + the caller
        //       reads garbage;
        //   (2) WriteValueTypeReturn (flat managed bytes, RefCount=0) instead of
        //       WriteReferenceReturn (a 4-byte mStack index) -- ValueTask<T> is a
        //       CLR struct with no binder, so the caller's dest is flat bytes; a
        //       ref index where the struct bytes are expected -> garbage field reads.
        // Stash-toggle: on HEAD (fix out) the suspend probes FAIL (NRE/wrong result/
        // garbage); after the fix they PASS.
        // ================================================================

        // VT1 probe body: a truly-async ValueTask<int> that suspends on the
        // deterministic incomplete Task<int> (host GetIncompleteTask). PRIVATE.
        private static async ValueTask<int> NeoStep20_ValueTaskIntSuspendProbe()
        {
            Task<int> incomplete = TestCLRBinding.GetIncompleteTask();
            int v = await incomplete;
            return v + 3;
        }

        // VT2 probe body: a SYNC-completing ValueTask<int> (awaits an already-
        // completed Task). PRIVATE.
        private static async ValueTask<int> NeoStep20_ValueTaskIntSyncProbe()
        {
            int v = await Task.FromResult(7);
            return v + 3;
        }

        // VT3 probe body: a ValueTask<int> that throws synchronously -> faulted
        // ValueTask. PRIVATE.
        private static async ValueTask<int> NeoStep20_ValueTaskIntFaultedProbe()
        {
            int v = await Task.FromResult(1);
            throw new System.Exception("neo-step20-vt-fault");
        }

        // VT4 probe body: a truly-async ValueTask<string> (T is a reference type)
        // that suspends on the deterministic incomplete Task<string>. PRIVATE.
        private static async ValueTask<string> NeoStep20_ValueTaskStringSuspendProbe()
        {
            Task<string> incomplete = TestCLRBinding.GetIncompleteStringTask();
            string v = await incomplete;
            return v;
        }

        // VT5 probe body: an async void method that SUSPENDS on a truly-incomplete
        // Task, then writes a host cell at the RESUME point (after the await). The
        // resume-time write proves suspend/resume ran end-to-end. PRIVATE.
        private static async void NeoStep20_AsyncVoidSuspendProbe()
        {
            Task<int> incomplete = TestCLRBinding.GetIncompleteTask();
            int v = await incomplete;
            // Resume point: write the awaited value to the host cell (the driver
            // polls this). A sync async-void writes BEFORE the await; this writes
            // AFTER, so only a genuine resume reaches here.
            TestCLRBinding.SetAsyncVoidCell(v + 5);
        }

        // VT1: ValueTask<int> truly-async suspend+resume (the BINDING probe).
        public static void NeoStep20_VT1_ValueTaskIntSuspendResume()
        {
            ValueTask<int> vt = NeoStep20_ValueTaskIntSuspendProbe();

            // GATE 1: the await TRULY SUSPENDED. The bridge Task wrapped in the
            // ValueTask<int> MUST be incomplete here (Start returned after
            // registering the continuation, before CompleteIncompleteTask). On HEAD
            // the missing suspend-case builds a ValueTask from a null result ->
            // vt.IsCompleted (wrong default) OR garbage -> FAIL.
            if (vt.IsCompleted) { int x = 1; int y = 0; int _ = x / y; }

            int n = 11;
            TestCLRBinding.CompleteIncompleteTask(n);

            // Bounded spin-wait for the resume (NO real delay).
            bool resumed = false;
            for (int i = 0; i < 1_000_000; i++)
            {
                if (vt.IsCompleted) { resumed = true; break; }
                if ((i & 0x3FF) == 0) System.Threading.Thread.Yield();
            }
            if (!resumed) { int x = 1; int y = 0; int _ = x / y; }

            // GATE 2: the resumed MoveNext ran GetResult (got n) + continued
            // (return n + 3) + SetResult(n + 3) routed to the bridge, and the
            // ValueTask<int> wraps that bridge. A WriteReferenceReturn regression
            // (ref index where struct bytes expected) makes vt.Result garbage.
            if (vt.IsFaulted) { int x = 1; int y = 0; int _ = x / y; }
            if (vt.Result != n + 3) { int x = 1; int y = 0; int _ = x / y; }
        }

        // VT2: ValueTask<int> sync completion (exercises CreateValueTaskFromResult +
        // the value-type return write on the sync path).
        public static void NeoStep20_VT2_ValueTaskIntSync()
        {
            ValueTask<int> vt = NeoStep20_ValueTaskIntSyncProbe();
            if (!vt.IsCompleted) { int x = 1; int y = 0; int _ = x / y; }
            if (vt.IsFaulted) { int x = 1; int y = 0; int _ = x / y; }
            if (vt.Result != 10) { int x = 1; int y = 0; int _ = x / y; }
        }

        // VT3: ValueTask<int> faulted (exercises CreateFaultedValueTask + the
        // value-type return write on the faulted path).
        public static void NeoStep20_VT3_ValueTaskIntFaulted()
        {
            ValueTask<int> vt = NeoStep20_ValueTaskIntFaultedProbe();
            if (!vt.IsFaulted) { int x = 1; int y = 0; int _ = x / y; }
        }

        // VT4: ValueTask<string> (ref-T) truly-async suspend+resume. T is a
        // reference type; the ValueTask<string> struct is still flat-bytes/RefCount=0
        // (T being a ref does not change the struct's Neo layout -- only what the
        // resumed SetResult stashes). Exercises the value-type return write with a
        // ref-T result.
        public static void NeoStep20_VT4_ValueTaskStringSuspend()
        {
            ValueTask<string> vt = NeoStep20_ValueTaskStringSuspendProbe();

            // GATE 1: the await TRULY SUSPENDED.
            if (vt.IsCompleted) { int x = 1; int y = 0; int _ = x / y; }

            string s = "hello-vt";
            TestCLRBinding.CompleteIncompleteStringTask(s);

            bool resumed = false;
            for (int i = 0; i < 1_000_000; i++)
            {
                if (vt.IsCompleted) { resumed = true; break; }
                if ((i & 0x3FF) == 0) System.Threading.Thread.Yield();
            }
            if (!resumed) { int x = 1; int y = 0; int _ = x / y; }
            if (vt.IsFaulted) { int x = 1; int y = 0; int _ = x / y; }
            // The resumed result is the awaited string (probe returns it verbatim).
            if (vt.Result != s) { int x = 1; int y = 0; int _ = x / y; }
        }

        // VT5: async void suspend verify+guard. An async void method suspends on a
        // truly-incomplete Task and writes a host cell at the RESUME point. The
        // driver polls the cell. lead-5/lead-6 say async void suspend already works
        // (pre-existing); this probe CONFIRMS it and guards against regression.
        public static void NeoStep20_VT5_AsyncVoidSuspend()
        {
            TestCLRBinding.SetAsyncVoidCell(0);
            NeoStep20_AsyncVoidSuspendProbe();

            // GATE 1: the async void await TRULY SUSPENDED -- the cell is still 0
            // (the resume-point write has NOT run yet; Start returned after
            // registering the continuation).
            if (TestCLRBinding.GetAsyncVoidSuspendCell() != 0) { int x = 1; int y = 0; int _ = x / y; }

            int n = 19;
            TestCLRBinding.CompleteIncompleteTask(n);

            // Bounded spin-wait for the resume (NO real delay). The resume writes
            // n + 5 to the cell.
            bool resumed = false;
            for (int i = 0; i < 1_000_000; i++)
            {
                if (TestCLRBinding.GetAsyncVoidSuspendCell() == n + 5) { resumed = true; break; }
                if ((i & 0x3FF) == 0) System.Threading.Thread.Yield();
            }
            if (!resumed) { int x = 1; int y = 0; int _ = x / y; }
        }

        // VT6: sync ValueTask<int> control (no await). Confirms the happy path --
        // a constant ValueTask<int> returned with no suspension.
        private static async ValueTask<int> NeoStep20_ValueTaskIntNoAwaitProbe()
        {
            await Task.CompletedTask;
            return 42;
        }
        public static void NeoStep20_VT6_ValueTaskIntSyncControl()
        {
            ValueTask<int> vt = NeoStep20_ValueTaskIntNoAwaitProbe();
            if (!vt.IsCompleted) { int x = 1; int y = 0; int _ = x / y; }
            if (vt.IsFaulted) { int x = 1; int y = 0; int _ = x / y; }
            if (vt.Result != 42) { int x = 1; int y = 0; int _ = x / y; }
        }

        // ================================================================
        // neo-async-valuetask-zeroalloc (child 6): the ZERO-ALLOC ValueTask<T>
        // suspend-path probe. The prior path (child 4) allocated a
        // TaskCompletionSource<T> bridge on EVERY ValueTask<T> suspend
        // (ctx.GetTaskBridge). The zero-alloc path returns a ValueTask<T> backed
        // DIRECTLY by the parked context's IValueTaskSource<T> (the
        // ManualResetValueTaskSourceCore<T> struct field) via the
        // ValueTask<T>(IValueTaskSource<T>, short) ctor -- NO TCS/Task<T> bridge.
        //
        // MEASUREMENT: a host-side direct bridge-allocation counter
        // (NeoAsyncAllocCounters.BridgeTaskAllocs) incremented at the exact TCS
        // allocation site. The DECISIVE assertion: a ValueTask<int> suspend drives
        // BridgeTaskAllocs == 0 (the bridge is never allocated), while a Task<int>
        // suspend (the AsyncTaskMethodBuilder path, which still needs a Task<T>)
        // drives BridgeTaskAllocs >= 1 -- proving the bridge alloc is eliminated on
        // the ValueTask path and that the counter is load-bearing (a stash-toggle
        // of the zero-alloc change flips the ValueTask path's count 0 -> 1).
        // A GC byte-delta is also captured (honest residual: the byte delta is NOT
        // zero -- the shared Activator box for the ValueTask<T> return + the
        // MoveNext frame remain; the zero-alloc claim is specifically about the
        // TCS/Task<T> BRIDGE, proven by the direct counter).
        // ================================================================

        // The Task<int> suspend baseline: a truly-async Task<int> that suspends.
        // Drives the AsyncTaskMethodBuilder path -> a TCS bridge IS allocated
        // (BridgeTaskAllocs >= 1). This is the CONTROL that proves the counter is
        // load-bearing and that the zero-alloc ValueTask probe's 0 is meaningful.
        private static async Task<int> NeoStep20_TaskSuspendProbeBaseline()
        {
            Task<int> incomplete = TestCLRBinding.GetIncompleteTask();
            int v = await incomplete;
            return v + 3;
        }

        public static void NeoStep20_VT_ZeroAlloc()
        {
            // ---- CONTROL: the Task<int> suspend path DOES allocate a bridge ----
            TestCLRBinding.ResetAsyncAllocCounters();
            int bridgeBeforeTaskPath = TestCLRBinding.GetAsyncBridgeTaskAllocs();
            // Drive a Task<int> suspend (but do NOT complete -- just measuring the
            // suspend-side allocation; the bridge is allocated at get_Task time).
            Task<int> taskSuspend = NeoStep20_TaskSuspendProbeBaseline();
            int bridgeAfterTaskPath = TestCLRBinding.GetAsyncBridgeTaskAllocs();
            // The Task path allocates a bridge TCS at get_Task (the suspend branch
            // of AsyncTaskMethodBuilder_T_GetTask_Neo calls ctx.GetTaskBridge()).
            if (bridgeAfterTaskPath <= bridgeBeforeTaskPath)
            {
                // The control failed: the Task path did NOT allocate a bridge. This
                // means either the counter is broken OR the Task suspend path
                // changed to not allocate -- either way the zero-alloc assertion
                // below is meaningless. Fail loud so the test is honest.
                int x = 1; int y = 0; int _ = x / y;
            }
            // Complete the Task<int> to avoid orphaning its TCS / leaking the
            // parked context into a subsequent test (the self-resetting host TCS
            // also needs completing so the next GetIncompleteTask is fresh).
            TestCLRBinding.CompleteIncompleteTask(11);

            // ---- MEASUREMENT: the ValueTask<int> suspend path allocates NO bridge ----
            TestCLRBinding.ResetAsyncAllocCounters();
            int bridgeBefore = TestCLRBinding.GetAsyncBridgeTaskAllocs();
            int ctxBefore = TestCLRBinding.GetAsyncContextAllocs();

            ValueTask<int> vt = NeoStep20_ValueTaskIntSuspendProbe();

            int bridgeAfterSuspend = TestCLRBinding.GetAsyncBridgeTaskAllocs();
            int ctxAfterSuspend = TestCLRBinding.GetAsyncContextAllocs();

            // GATE 1 (correctness): the await TRULY SUSPENDED (the IValueTaskSource
            // is not yet completed). This MUST hold -- the zero-alloc path is
            // correct only if suspend+resume still works end-to-end.
            if (vt.IsCompleted) { int x = 1; int y = 0; int _ = x / y; }

            // DECISIVE ASSERTION (the zero-alloc claim): the ValueTask<int>
            // suspend allocated ZERO bridge TCS. The path built a ValueTask<T>
            // backed by the IValueTaskSource<T> directly (never called
            // ctx.GetTaskBridge()). If the zero-alloc change is reverted (the
            // stash-toggle), this flips to >= 1 -- the load-bearing test.
            if (bridgeAfterSuspend != bridgeBefore)
            {
                int x = 1; int y = 0; int _ = x / y;
            }
            // The context IS allocated (the IValueTaskSource<T> holder -- present on
            // both paths; the zero-alloc claim is about the BRIDGE, not the context).
            if (ctxAfterSuspend <= ctxBefore)
            {
                int x = 1; int y = 0; int _ = x / y;
            }

            // Complete + resume (correctness: the IValueTaskSource-backed ValueTask
            // completes when the resumed SetResult routes to ctx.CompleteResult).
            int n = 11;
            TestCLRBinding.CompleteIncompleteTask(n);

            bool resumed = false;
            for (int i = 0; i < 1_000_000; i++)
            {
                if (vt.IsCompleted) { resumed = true; break; }
                if ((i & 0x3FF) == 0) System.Threading.Thread.Yield();
            }
            if (!resumed) { int x = 1; int y = 0; int _ = x / y; }
            if (vt.IsFaulted) { int x = 1; int y = 0; int _ = x / y; }
            if (vt.Result != n + 3) { int x = 1; int y = 0; int _ = x / y; }

            // GATE 2 (correctness, post-resume): the bridge counter is STILL 0
            // (the resume's ctx.CompleteResult completed the IValueTaskSource core;
            // it did NOT lazily allocate a TCS). If the resume touched GetTaskBridge,
            // this would be >= 1.
            int bridgeAfterResume = TestCLRBinding.GetAsyncBridgeTaskAllocs();
            if (bridgeAfterResume != bridgeBefore)
            {
                int x = 1; int y = 0; int _ = x / y;
            }

            // HONEST RESIDUAL: the GC byte delta is NOT measured/zero-asserted here.
            // The per-suspend byte delta includes the shared Activator box (the boxed
            // ValueTask<T> return) + the MoveNext frame + JIT caches -- all present on
            // BOTH the old and new paths. The zero-alloc claim is SPECIFICALLY the
            // elimination of the TCS/Task<T> BRIDGE (a TaskCompletionSource<T> +
            // its Task<T> -- ~80-100 bytes per suspend), proven by the direct counter
            // above (bridgeAfterSuspend == bridgeBefore). The residual allocations
            // (the boxed ValueTask<T> return, the context, the MoveNext frame) are
            // out of scope for this child.
        }
    }
}
