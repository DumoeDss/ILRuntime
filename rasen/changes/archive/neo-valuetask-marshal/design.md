## Context

`neo-valuetask-marshal` was framed (child 20 of `neo-overhaul`) as the deepest
remaining item: a `ValueTask<T>` async call-arg-marshalling bug. The framing
(quoted from the planner mandate, ultimately sourced from the 2026-07-10
`neo-clrstruct-sm-field-layout/blocked.md`) said:

> `AsyncValueTaskMethodBuilder_T_SetResult_Neo` does `curPrim += 8` to skip the
> builder byref-`this`, but the ValueTask builder's byref-`this` occupies 16
> call-frame bytes ... `ReadResultParam` reads `frameBase[8]` (stale residue)
> instead of `frameBase[16]` (the real int result). VT1/VT2/VT6 fail.

That description was **accurate as of 2026-07-10** (the `blocked.md` empirically
dumped `b16=14`, the real `v+3` result, stranded at `frameBase[16]`). But it is
**stale today**: the fix landed the same investigation cycle in commit
`9c9b795d` ("Neo async: ValueTask<T> + async-void suspend path (child 4
UNBLOCKED)"). On HEAD `e1e7ee7c` the framed bug does not exist.

This design records (a) the disproof that the framed bug is still live, (b) the
real 8-vs-16 root cause and the marshalling rule (the durable finding worth
pinning), and (c) why this is a single fixed bug, not a cluster.

## Goals / Non-Goals

**Goals:**
- Prove, with runtime evidence on HEAD, that the VT1/VT2/VT6 cluster is already
  fixed (no engine change is needed or possible for the stated goal).
- Pin the load-bearing marshalling invariant (builder byref-`this` = flat
  managed bytes, skip `Unsafe.SizeOf<builder>`) in the `neo-async` spec so a
  future refactor cannot regress `BuilderThisManagedSize` to a hardcoded `+= 8`.
- Record the eliminated hypotheses so no future worker re-opens B1/B2/B3 or the
  async-Task-Start concern for this cluster.

**Non-Goals:**
- Any engine/JIT/object-model edit. The fix is already on HEAD.
- New probes. `NeoStep20_VT1..VT6` + `VT_ZeroAlloc` already exist and pass.
- The broader unrelated NIE surface visible in the full Neo run (Step 17/13b CLR
  field/array access, raw Stfld CLR-VT owner, Ldsflda, IL-delegate Newobj,
  bare NIEs) -- none of which are ValueTask/async.

## Decisions

### D1 -- Close-out shape: spec delta only, no engine change
**Decision:** ship ONE ADDED requirement to `neo-async` (the builder byref-`this`
marshalling rule) and nothing else.
**Why not a pure no-op:** the fix is a single non-obvious line
(`BuilderThisManagedSize(method)` instead of `8`). Without a spec pinning the
rule, a future "simplify the magic number" refactor could silently restore `+= 8`
and re-break ONLY the ValueTask path (the Task path would still pass, masking the
regression in the sync-Task smoke). The spec scenario "SetResult reads the real T
result for a ValueTask<T> builder" makes that regression observable at the
spec level.
**Alternative rejected:** modifying the existing "Neo async method builder
redirection" requirement. Not done -- its observable behavior (the ValueTask
scenario at `rasen/specs/neo-async/spec.md`) is already correct; the gap is an
internal invariant, so ADDED is the right delta operation.

### D2 -- The marshalling rule (the durable finding)
The Neo call-arg lowering (`CopyNeoCallArguments`, byRefSrc slot 0) treats a
byref value-type `this` by **dereferencing the byref and copying the struct's
flat managed bytes** (`Unsafe.SizeOf<T>`) into the callee param region. It does
NOT marshal `this` as an 8-byte byref `(objIdx, offset|flag)` Ref Slot when the
parameter is a value type passed byref. (Contrast: a byref produced by `ldloca`
on a CLR-struct local that is then consumed by `stind`/`ldind` DOES use the 8-byte
`(objIdx, off|flag)` form -- that is a different opcode contract, not a call
param.) Therefore the redirect must skip `Unsafe.SizeOf<builder>`:

| Builder | Fields | `Unsafe.SizeOf` |
|---------|--------|-----------------|
| `AsyncTaskMethodBuilder<T>` | `Task<T>? m_task` | **8** |
| `AsyncValueTaskMethodBuilder<T>` | `object? _obj` + `long _data` | **16** |
| `AsyncVoidMethodBuilder` | (internal state) | dynamic via the same helper |

The `9c9b795d` fix encodes this generally: `BuilderThisManagedSize(method)`
(`CLRRedirections.AsyncNeo.cs:1702`) -> `Optimizer.GetNeoValueTypeManagedSize(decl)`
= `Unsafe.SizeOf<T>`, with a defensive fallback of 8 only when the declaring type
is not a value type. This is correct for ANY builder size, present or future.

### D3 -- The 8-vs-16 dump (the comparison the mandate asked for)
The mandate asked to dump the call-frame layout for both `AsyncTaskMethodBuilder<int>`
(TC8-style, works) and `AsyncValueTaskMethodBuilder<int>` (VT1, was broken) at
`SetResult` entry. The `blocked.md` already captured this empirically (the
dump `VTSetResult resultObj=4 b4=0 b8=4 b12=<garbage> b16=14`):
- **Task builder:** `this` = 8 flat bytes -> `T result` at `frameBase[8]`.
  The old `+= 8` skip landed exactly on the result -> TC8 read correctly
  (the "accidentally worked" case).
- **ValueTask builder:** `this` = 16 flat bytes -> `T result` at `frameBase[16]`.
  The old `+= 8` skip landed on `frameBase[8]` = the struct's 2nd qword (stale
  `v=1` residue = 4) -> VT1 read the wrong value.

So the call-convention difference is NOT a byref-width difference (both builders
arrive the same way); it is purely the **flat managed size of the builder struct**
(8 vs 16). The general `Unsafe.SizeOf` skip resolves it for both.

### D4 -- Single fix, not a cluster
The mandate asked whether VT1/VT2/VT6 is one fix or a cluster (lead-7's B1/B2/B3
framing, with B1 disproven). Verdict: **one fix, already shipped, plus the
sibling SetException/AwaitUnsafeOnCompleted redirects which were already correct
or made so in the same commit**:
- **B1** (SM field-layout collision: `<>t__builder` and a hoisted int local
  share `PrimitiveOffset`): DISPROVEN (`neo-clrstruct-sm-field-layout`). The
  builder is a CLR-struct-with-ref -> stored at `ManagedObjects[ReferenceOffset]`;
  the int local is at `Primitives[PrimitiveOffset]`. Disjoint arrays; benign.
- **The 8-vs-16 skip** (the real bug): fixed in `9c9b795d` for `SetResult` AND
  `SetException` (the ValueTask `SetException` delegates to
  `AsyncTaskMethodBuilder_T_SetException_Neo`, which also uses
  `BuilderThisManagedSize`).
- **B2** (binder NIE on the ValueTask struct accessor `this`): fixed in
  `9c9b795d` via the `_currentValueTaskState` thread-static + the
  `ValueTask_T_GetIsCompleted/IsFaulted/Result` redirects (never reflect the
  struct's `_obj` ref field).
- **B3** (`Task.FromException` AmbiguousMatchException on VT3): fixed in
  `9c9b795d` (`CreateFaultedValueTask` resolves the generic
  `Task.FromException<T>` definition explicitly).
- **AwaitUnsafeOnCompleted:** needs no builder-this skip -- it recovers the SM
  via `CurrentAsyncSm` and the awaited task via `GetAwaitedTaskFromSm` (the SM's
  awaiter field). Correct as-is.

The "async Task is blocked by `AsyncTaskMethodBuilder.Start` ArgumentNullException"
note from child-2 is ALSO stale: `NeoStep20_TC1_SyncTaskOfT` (sync `Task<int>`)
passes, and `VT_ZeroAlloc`'s control arm drives a `Task<int>` suspend+resume
successfully (its `bridgeAfterTaskPath > bridgeBeforeTaskPath` gate passed).

## Risks / Trade-offs

- **[Spec-only change is easy to dismiss] -> Mitigation:** the ADDED requirement
  is worded as a SHALL with a concrete `frameBase[16]` vs `frameBase[8]` scenario,
  so a future hardcoded-`+= 8` regression breaks the spec, not just a probe.
- **[Full Neo run is pre-crash only] -> Mitigation:** the crash (exit 127, a
  `HotfixTestInheritanceTestCases.Test03` / `CalculateValue is not bound`
  NotSupportedException, then the known Dict-NRE) is unrelated to async; the
  pre-crash NIE surface was grepped specifically for `ValueTask` /
  `AsyncMethodBuilder` and is empty. The NeoStep smoke (354/0) is the
  authoritative regression bar and is complete (no crash).
- **[Stash `child4-valuetask-blocked-partial` still exists] -> Mitigation:** it
  is NOT applied (`git status` shows no source modifications; `git diff HEAD` on
  `CLRRedirections.AsyncNeo.cs` is empty) and contains obsolete `VTDBG2`
  diagnostics that are already absent from HEAD. The stash should be dropped by
  the LEAD after this close-out is archived; it must NOT be popped (it would
  re-introduce the pre-fix diagnostics over the committed fix).

## Open Questions

None. The depth verdict is **TRACTABLE and ALREADY COMPLETE** -- there is no
remaining engine work in this child's scope. The only artifact edit is the
`neo-async` spec delta.
