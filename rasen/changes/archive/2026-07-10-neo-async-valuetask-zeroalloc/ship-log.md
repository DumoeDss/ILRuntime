# Ship Log — neo-async-valuetask-zeroalloc (child 6)

**Date:** 2026-07-10  **Capability:** neo-async  **Wave:** completion-3, child 6 (perf)
**Status:** SHIPPED (LEAD-verified). UNBLOCKED by child 4; this is the zero-alloc perf optimization.

## Delivered
**Reduced-alloc `ValueTask<T>` suspend path** — eliminates the `Task<T>`/`TaskCompletionSource<T>`
bridge. `ILAsyncContext<T>` ALREADY implements `IValueTaskSource<T>` (via a struct
`ManualResetValueTaskSourceCore<T> core`, wired in the Step-20-suspend slice) — so this was a focused
redirect change, not an IValueTaskSource implementation task (child 4's OQ3 note deferred exactly this).
- The suspend branch of `AsyncValueTaskMethodBuilder_T_GetTask_Neo` now returns a `ValueTask<T>` backed
  **directly** by the parked context's `IValueTaskSource<T>` (`ValueTask<T>(IValueTaskSource<T>, short)`
  ctor) — no bridge. The lever: making the `tcs` field **lazy** (allocated only in `GetTaskBridge`, the
  Task-path fallback; the ValueTask path never calls it).
- The accessor redirects (`get_IsCompleted`/`get_IsFaulted`/`get_Result`) read the `core` via new
  non-generic sink methods (`GetSourceIsCompleted`/`IsFaulted`/`GetResult(token)`) — priority order:
  IValueTaskSource > BridgeTask > SyncResult.
- Resume (`CompleteResult`/`CompleteException`) completes `core` unconditionally + the TCS only if lazily
  created.

## Verification (LEAD-verify)
- **VT1-VT6: 6/6 GREEN** (suspend+resume correctness preserved — the ValueTask<T> now backed by the
  IValueTaskSource completes + the accessor reads the result).
- **NeoStep smoke (LEAD re-ran): 274 tests, 0 failed** (+1 `NeoStep20_VT_ZeroAlloc` probe). No regression.
- **Allocation measurement (a direct counter, not GC bytes):** `NeoAsyncAllocCounters.BridgeTaskAllocs` —
  ValueTask<int> suspend: **`BridgeTaskAllocs == 0`** (zero-alloc path) + `ContextAllocs >= 1` (the
  IValueTaskSource holder still allocates — honest); Task<int> CONTROL: `BridgeTaskAllocs >= 1` (proves
  the counter load-bearing). Stash-toggle (force bridge fallback → probe FAILs).
- **Legacy-neutral:** all runtime edits Neo-gated except `NeoAsyncAllocCounters.cs` (intentionally
  non-gated public — the test harness compiles against the plain-`Debug` non-Neo runtime, so the counter
  type must be visible there; pure static, zero behavior on Legacy; Neo-gated increments fire only under
  `Debug_Neo`). Plain-`Debug` build 0 errors.

## Honest residual (reduced-alloc, NOT fully zero-alloc)
Per suspend, these REMAIN (out of scope; shared by old+new paths): (1) the `Activator.CreateInstance(
ValueTask<T>)` BOX in `get_Task` (shared by both paths — eliminating it needs a typed non-reflective
ValueTask<T> construction); (2) the `ILAsyncContext<T>` itself; (3) the resume `Action` + `MoveNext`
sub-frame. What this child ELIMINATES is specifically the `TaskCompletionSource<T>` + its `Task<T>` —
the allocation targeted by the OQ3 note.

## Durable findings
1. `ILAsyncContext<T>` already implements `IValueTaskSource<T>` via a struct `core` — the zero-alloc
   ValueTask path was always a focused redirect change, never an implementation task.
2. The bridge-specific allocation is the EAGERLY-allocated `TaskCompletionSource<T>` (in the ctor), NOT
   the `ValueTask<T>(Task<T>)` ctor call. Making the TCS lazy is the lever; the `WrapBridgeAsValueTask`
   Activator box is a SHARED allocation (both paths) — not the bridge-specific saving.
3. A non-generic sink accessor is the clean way to read the IValueTaskSource state from the non-generic
   redirect (avoids reflection on the closed T). The token is `core.Version`, stable for the
   single-completion suspend model.
4. A direct allocation counter beats GC byte deltas for a focused alloc claim; the counter must be
   public + non-Neo-gated if the test harness compiles against the non-Neo runtime DLL.

## Review
LEAD-verify (NeoStep smoke re-ran 274/0; VT1-6 6/6; the direct BridgeTaskAllocs counter + stash-toggle;
Legacy build 0 errors; Neo-gated incl. the deliberate non-gated counter type).
