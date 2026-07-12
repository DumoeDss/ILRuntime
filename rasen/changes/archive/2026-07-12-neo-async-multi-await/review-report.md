# Review Report: neo-async-multi-await

**Reviewer:** lead-5 (FRESH, non-author -- the implementer was a prior-session worker;
this reviewer had not seen the code before). Stage: adversarial non-author review,
run between apply and ship per the OPSX autopilot contract.

**Verdict: APPROVED -- ship-ready. 0 Blocker, 0 Major for THIS change.**
All findings are PRE-EXISTING (reproduce on HEAD with the fix stashed) or cosmetic
follow-ups; none is introduced by neo-async-multi-await and none blocks the ship.

## Methodology (the golden rule: RUN, don't read)

The codebase's async machinery has repeatedly masked bugs behind green smoke. So
this review CONSTRUCTED and RAN adversarial probes beyond the binding gate TC12,
isolated per-case, with host-side logging. A temp harness was built (deterministic
non-self-resetting TCSes + a mark signal + host verdict helpers in
`TestClass3.cs`, and a temp probe file `NeoStep20ReviewProbe_TEMP.cs`), RUN, then
fully removed before ship (the working tree now contains only the real change; see
the cleanup verification below).

Build/run (Debug_Neo CLI, Debug TestCases, always `-f net8.0`).

## What was verified GREEN (the fix is correct for its scope)

| Case | Shape | Result | Evidence |
|------|-------|--------|----------|
| TC12 | 2 genuinely-incomplete awaits (the binding gate) | PASS | `Ran 1 tests, 0 failded` |
| TC8 | single truly-async suspend/resume | PASS | `0 failded` |
| TC11 | nested sync async after suspend | PASS | `0 failded` |
| TC1 | sync Task<int> | PASS | `0 failded` |
| Full `NeoStep` smoke | regression | **239/0/0** | `Ran 239 tests, 0 failded`, EXIT=0 |
| Review case 3 | mixed completed-then-incomplete (sync await1 then genuine suspend await2) | PASS | `Result=26` (7+19); `D PASS case3` |
| Review case 2 | **Task FAULTS at the 2nd await** (the load-bearing concern) | PASS | fault propagates through the REUSED bridge -- see below |

### Case 2 (fault at 2nd await) -- the load-bearing concern, VERIFIED GOOD

The handoff's #1 worry: the `SuspendStateMachine` context-REUSE deviation could
swallow/orphan a fault at the 2nd await (an orphaned context1 bridge would leave
the driver's `t` hung incomplete). Probe: `await incompleteTaskA; await faultTaskB;`
driven through suspend#1 -> resume#1 -> suspend#2 -> fault B. Observed:

```
P resumed1 va=41            (resume#1 read the CORRECT await1 result)
P mark2 awaitFault          (reached the 2nd await, suspended on B)
HOST Fault ok=True          (B faulted)
D settled state=2           (state=2 == t.IsFaulted=True -- the bridge FAULTED)
EX: ... Task<int>.GetResultCore ... TaskAwaiter_T_GetResult_Neo:763
     -> AggregateException -> System.Exception: REVIEW-FAULT-AT-AWAIT2
D PASS case2
```

The fault PROPAGATES: B's `.Result` throws -> `TaskAwaiter_T_GetResult_Neo:763`
(`InvokeMember("Result")`) -> SM catch -> `AsyncTaskMethodBuilder_T_SetException_Neo`
-> `ctx.CompleteException` -> the REUSED context's TCS bridge faults with the
original exception preserved. **The context-reuse does NOT swallow faults.** This
is exactly the property the deviation was designed to preserve, and it holds.

## Findings (none block the ship)

### F1 -- `Task` -> `Task<int>` ArgumentException (PRE-EXISTING, follow-up)
Two probe shapes (single sync-fault await; 3-await SM) throw
`System.ArgumentException: Object of type 'System.Threading.Tasks.Task' cannot be
converted to type 'System.Threading.Tasks.Task`1[System.Int32]'` at
`ILIntepreter.InvokeNeoCallTarget` (ILIntepreter.Neo.cs:635, from ExecuteNeo:2492)
-- i.e. in the CLR **call** machinery, NOT in `GetAwaitedTaskFromSm` or
`SuspendStateMachine` (the only code this change touches).

**Regression proof (stash-toggle):** with `CLRRedirections.AsyncNeo.cs` stashed
(HEAD engine) BOTH shapes throw the IDENTICAL exception. So this is PRE-EXISTING,
not introduced by this change. The single-sync-fault shape is especially clean
proof: a pre-faulted task -> `IsCompleted=true` -> sync `GetResult` -> it NEVER
calls `SuspendStateMachine`/`GetAwaitedTaskFromSm`, yet throws -- so the fix's code
is provably uninvolved. Route: a Task-generic-arg CLR-binding follow-up. NOTE: this
means 3+ awaits is currently UNVERIFIED end-to-end (a 3-await SM is blocked by this
pre-existing edge before the multi-await logic runs); the multi-await logic itself
is verified by case 2 (2 awaits + fault through both the awaiter-first scan AND the
reused context).

### F2 -- `conv.ovf.u2.un` not implemented (Step 6) (PRE-EXISTING, follow-up)
String concatenation INSIDE an async state machine (`"... " + intVar`) lowers to
`conv.ovf.u2.un`, which ExecuteNeo does not implement (Step 6 NIE). TC12 avoids
in-SM concat (returns `va + vb`). Not introduced by this change (opcode gap). Route:
Step 6 opcode follow-up; async test authors must keep SM bodies concat-free until
then.

### F3 -- fault exceptions are AggregateException/TargetInvocationException-wrapped (PRE-EXISTING, cosmetic)
Neo `GetResult` reads `task.Result` via `InvokeMember`, which wraps the throw in
`TargetInvocationException`; and `.Result` itself wraps in `AggregateException`. So
a faulted async Task's bridge exception is `AggregateException(TargetInvocationException(AggregateException(original)))`,
whereas real C# async unwraps to the original `Exception` in the `catch`. The
exception CONTENT is preserved (the original message/stack is in the chain), so
correctness holds; only the wrapping depth differs. Not introduced by this change.
Route: async exception-unwrapping fidelity follow-up.

### F4 (coverage recommendation, not a defect) -- add a fault-propagation TC
TC12 covers only successful multi-await. Case 2 above proves fault propagation works
but is a temp probe. Recommend a future change add a permanent fault-propagation TC
to `neo-async` (fault the 2nd awaited Task; assert the bridge faults) so this
load-bearing property stays green-guarded. Out of scope for THIS change (a reviewer
adds findings, not shipped tests).

## Cleanup verification (working tree is clean)

The temp harness was fully removed. `TestClass3.cs` was reverted to HEAD and the
real `cell2` (`s_incompleteTcs2`/`GetIncompleteTask2`/`CompleteIncompleteTask2`)
re-applied verbatim -- `git diff` shows `index 93666bad..aeb42c09`, byte-identical
to the original change. The temp probe file and all run logs are deleted. Remaining
uncommitted source changes are EXACTLY the real change:
- `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs` (D1 awaiter-first + context-reuse)
- `TestCases/NeoStep20Test.cs` (TC12)
- `ILRuntimeTestBase/TestFramework/TestClass3.cs` (cell2)
- `openspec/changes/neo-async-multi-await/` (proposal/design/tasks/specs/review-report)

## Conclusion

Ship it. The neo-async-multi-await change is correct and verified for its scope
(2-await suspend/resume/resume + fault propagation through the reused context +
mixed sync/suspend; full NeoStep smoke 239/0/0; Legacy-neutral by construction --
all edits Neo-only). The findings are pre-existing edges (F1/F2/F3) and a coverage
recommendation (F4), none of which this change introduces or must fix. F1's 3+ await
gap and F2's in-SM-concat gap are the most worthwhile follow-ups for a future async
child.
