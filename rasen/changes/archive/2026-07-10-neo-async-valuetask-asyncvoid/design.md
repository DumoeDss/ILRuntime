# Design — neo-async-valuetask-asyncvoid (child 4)

**Date:** 2026-07-09  **Capability:** neo-async  **Wave:** completion-3, child 4
**Status:** IMPLEMENTING  **Prerequisite:** `neo-ret-vt-with-ref-fields` (HIGH#1, SHIPPED)

> **IMPLEMENTATION ROUND 2 DONE (2026-07-10) — VT1-VT6 ALL GREEN; smoke 273/0/0.**
> The three remaining blockers (the curPrim/byref-`this` marshalling bug [VT1/VT2/VT6;
> B1 disproven], the `CreateFaultedValueTask` AmbiguousMatch [VT3; B3], and the
> registration-completeness gap for T=string [VT4; B2 — NOT a foundational binder gap])
> are FIXED. Summary (full detail in `tasks.md` items 17-19 + `blocked.md` round-2):
> - **curPrim:** the builder `this` is a value type whose flat managed bytes
>   (`Unsafe.SizeOf<T>` = 16 for `AsyncValueTaskMethodBuilder<T>`, 8 for the Task builder)
>   are copied into the callee param region by `CopyNeoCallArguments` — NOT an 8-byte
>   byref. New `BuilderThisManagedSize(method)` skips the actual size. B1 (the ILType
>   field-layout-collision hypothesis) is DISPROVEN — the shared `PrimitiveOffset` is
>   benign (disjoint storage); no `ILType.cs` change (see
>   `../neo-clrstruct-sm-field-layout/blocked.md`).
> - **B3:** `Task.FromException` is ambiguous (two overloads both taking `(Exception)` —
>   one generic). Resolve the generic def via `Array.Find(...IsGenericMethod)` + close it.
> - **B2:** add `<string>` to the `AsyncValueTaskMethodBuilder<T>`, `TaskAwaiter<T>`, and
>   `Task<T>` accessor registrations (was only `<int>`/`<ILTypeInstance>`). The redirect
>   path bypasses the reflection fallback's Area-4b struct-`this`-with-ref-field NIE.
> Fixer-1's accessor rework (ThreadStatic `_currentValueTaskState`) + the Call-case heap
> buffer + DebugService guard were correct and KEPT. VTDBG2/VTDBG4 diagnostics REMOVED.

> **FIXER ROUND 1 IN PROGRESS (2026-07-10) — see `handoff/fixer-1.md` FIRST.**
> F-1/F-2 (accessor AV) ROOT-CAUSE-FIXED (reflection helpers deleted; accessors use a
> ThreadStatic `ValueTaskAccessorState` slot stashed at get_Task, mirroring the
> TaskAwaiter<T> side-channel precedent — NEVER reflect the struct's `_obj` ref).
> 3 additional masking bugs fixed (missing `SetResult` sink-swap; per-call `stackalloc`
> stack-overflow in the `Call` case; debugger `ValueTask.ToString()` AV). VT1 now reaches
> the final assertion but FAILS on `vt.Result == 4 != 14` — a PRE-EXISTING
> SM/builder-layout bug specific to `AsyncValueTaskMethodBuilder<int>` (the identical
> `Task<int>` probe TC8 reads 14 correctly). NOT yet fixed; needs a successor. The
> accessor-surface design (this doc's F-3 gap) + the corrected "accessors run in the
> CALLER frame, not get_Task's frame" premise are documented in fixer-1.md and will be
> folded back here once the layout bug is resolved. TEMP `VTDIAG` diagnostics are in the
> tree — grep + remove before commit.

> **FIXER ROUND 1 IN PROGRESS (2026-07-10) — see `handoff/fixer-1.md` FIRST.**
> F-1/F-2 (accessor AV) ROOT-CAUSE-FIXED (reflection helpers deleted; accessors use a
> ThreadStatic `ValueTaskAccessorState` slot stashed at get_Task, mirroring the
> TaskAwaiter<T> side-channel precedent — NEVER reflect the struct's `_obj` ref).
> 3 additional masking bugs fixed (missing `SetResult` sink-swap; per-call `stackalloc`
> stack-overflow in the `Call` case; debugger `ValueTask.ToString()` AV). VT1 now reaches
> the final assertion but FAILS on `vt.Result == 4 != 14` — a PRE-EXISTING
> SM/builder-layout bug specific to `AsyncValueTaskMethodBuilder<int>` (the identical
> `Task<int>` probe TC8 reads 14 correctly). NOT yet fixed; needs a successor. The
> accessor-surface design (this doc's F-3 gap) + the corrected "accessors run in the
> CALLER frame, not get_Task's frame" premise are documented in fixer-1.md and will be
> folded back here once the layout bug is resolved. TEMP `VTDIAG` diagnostics are in the
> tree — grep + remove before commit.

## Problem (pinned by lead-6; HIGH#1 blocker now removed)

An `async ValueTask<T>` method that SUSPENDS on a truly-incomplete await breaks in
`AsyncValueTaskMethodBuilder_T_GetTask_Neo` (`CLRRedirections.AsyncNeo.cs:374-394`).
Two gaps:

1. **Missing `SmContextMap` suspend-case.** When the SM suspends, `SmTaskMap` has no
   entry (SetResult did not run) and the parked context lives on `SmContextMap`. The
   Task<T> builder's `AsyncTaskMethodBuilder_T_GetTask_Neo` already handles this
   (lines 280-289: `ctx.GetTaskBridge()`). The ValueTask<T> builder LACKS this branch
   — it only checks `SmTaskMap` (sync) then unconditionally builds a `ValueTask<T>`
   from `resultObj` (null when suspended → wrong default).
2. **`WriteReferenceReturn` is WRONG for `ValueTask<T>`.** The current
   `WriteReferenceReturn(vt, retDst, retRefBase, mStack)` writes a boxed-object
   reference (a 4-byte mStack index) into `retDst`. But the caller's dest slot is a
   VALUE-TYPE local sized as the full flat managed bytes of `ValueTask<T>` (see the
   layout finding below), so a 4-byte index where the struct's bytes are expected →
   the caller's `GetAwaiter`/`Result` field reads garbage.

## Key layout finding (load-bearing — corrects the task's premise)

The task's framing assumed `ValueTask<T>` has "1 ref field" that must be written to a
separate `mStack[retRefBase]` slot (analogous to the `Ret`-opcode VT-with-ref-fields
copy in `ILIntepreter.Neo.cs:2940`). That premise is WRONG for a CLR struct without a
registered `ValueTypeBinder`:

`Optimizer.Neo.cs:1523-1545` (`AllocNeoParamSlot` / the `else if (type.IsValueType)`
CLR-struct branch) sets, for a binder-less CLR struct like `ValueTask<T>`:
- `slot.Size = GetNeoValueTypeManagedSize(ValueTask<T>)` = `Unsafe.SizeOf<ValueTask<T>>()`
  (the FULL managed size, INCLUDING the embedded GC-reference bytes of the `_task`/
  `_obj` field — a raw managed pointer folded into the flat bytes).
- `slot.RefCount = 0` (no binder → NO ref slots are allocated on the mStack for this slot).

So the caller's dest for a `ValueTask<T>` return is **flat managed bytes with RefCount=0** —
there is NO separate mStack ref slot to write. The ENTIRE struct (refs included, as raw
managed pointers in the flat bytes) must be written to `retDst`.

This is EXACTLY the representation the existing `WriteNeoValueType(value, dst, sz)`
helper produces: it calls `Unsafe.WriteUnaligned<T>` which writes the full managed
struct, embedded GC refs and all, as flat bytes. And this path is ALREADY PROVEN GREEN
for a CLR-struct-WITH-ref-field return: `Task_T_GetAwaiter_Neo` returns a
`TaskAwaiter<T>` (which has a `Task m_task` reference field) via `WriteValueTypeReturn`
→ `WriteNeoValueType`, and TC8/TC12/TC13/TC14 (the suspend/resume probes that read the
awaiter's `m_task`) all pass.

**Decision: reuse the existing `WriteValueTypeReturn(vt, retDst, retRefBase, mStack)`
helper** (the `TaskAwaiter<T>` return path) for the ValueTask<T> return write. Do NOT
create a new `WriteNeoValueTypeReturn` that writes a separate ref slot — there is no
ref slot (RefCount=0), and writing to `mStack[retRefBase]` would corrupt a neighbouring
slot. The `retRefBase`/`retRefBase` mStack reservation for a RefCount=0 return is 0
slots (the `for (i<returnRefCount) mStack.Add(null)` loop in `DriveMoveNextCore` adds
nothing), so `WriteValueTypeReturn`'s no-op-on-zero-ref behaviour is correct.

This is the durable cross-cutting gotcha: **a CLR-struct return (no binder) is ALWAYS
flat-bytes/RefCount=0, even when the struct has reference fields.** The `Ret`-opcode
VT-with-ref-fields path (neo-ret-vt) applies to **IL** value-type returns (where
`TotalReferenceCount > 0` allocates real ref slots); it does NOT apply to CLR-struct
returns via redirect. The redirect must use the flat-bytes write.

## Fix (the two gaps)

### Gap 1: SmContextMap suspend-case + WrapBridgeAsValueTask

Add a suspend-case branch to `AsyncValueTaskMethodBuilder_T_GetTask_Neo` mirroring
`AsyncTaskMethodBuilder_T_GetTask_Neo:280-289`:

```csharp
object vt = null;
// sync path: SmTaskMap has the stashed result/exception
if (sm != null && SmTaskMap.TryGetValue(sm, out var stashed)) {
    SmTaskMap.Remove(sm);
    if (stashed is Exception e) vt = CreateFaultedValueTask(method, e);
    else vt = CreateValueTaskFromResult(method, stashed);
}
// SUSPEND path: SmTaskMap empty (SetResult did not run); SmContextMap has the
// parked context. Wrap its Task<T> bridge as a ValueTask<T> via the public
// ValueTask<T>(Task<T>) ctor.
else if (sm != null && SmContextMap.TryGetValue(sm, out IAsyncContextSink ctx)) {
    vt = WrapBridgeAsValueTask(method, ctx.GetTaskBridge());
}
else {
    // Defensive: no stash + no suspend -> completed default.
    vt = CreateValueTaskFromResult(method, GetDefaultForResultType(method));
}
WriteValueTypeReturn(vt, retDst, retRefBase, mStack);
```

`WrapBridgeAsValueTask(method, bridgeTask)`:
- T = `GetResultClrType(method)` (the builder's first generic arg, same resolution
  `CreateValueTaskFromResult` uses).
- `vtClosed = typeof(ValueTask<>).MakeGenericType(T)`.
- `Activator.CreateInstance(vtClosed, bridgeTask)` — the public
  `ValueTask<T>(Task<T>)` ctor wraps the (possibly-incomplete) bridge Task as a
  `ValueTask<T>`. The caller observes an incomplete `ValueTask<T>` that completes when
  the resumed SM's SetResult routes to the TCS bridge (the same bridge the Task<T>
  builder returns directly).

### Gap 2: value-type return write (flat bytes, RefCount=0)

Replace `WriteReferenceReturn(vt, retDst, retRefBase, mStack)` with
`WriteValueTypeReturn(vt, retDst, retRefBase, mStack)` in ALL THREE branches of
`AsyncValueTaskMethodBuilder_T_GetTask_Neo` (suspend / sync-completion / faulted all
produce a `ValueTask<T>` struct). The helper writes the full flat managed bytes
(`Unsafe.WriteUnaligned<ValueTask<T>>`) to `retDst` — byte-consistent with the caller's
RefCount=0 dest layout by construction.

Also apply to `AsyncValueTaskMethodBuilder_GetTask_Neo` (non-generic ValueTask, line
420) for consistency: it returns a `ValueTask` (non-generic struct, RefCount=0 flat
bytes too). Replace its `WriteReferenceReturn` with `WriteValueTypeReturn`.

## async void half (verify + guard)

lead-5/lead-6 say async void suspend ALREADY works (pre-existing: `AsyncVoidMethodBuilder`
uses `AsyncTaskMethodBuilder_T_Start_Neo` machinery + the suspend/resume path; async void
SetResult is a no-op side-effect). The async void `SetException` rethrows synchronously.
VERIFY with a probe (`NeoStep20_AsyncVoidSuspendProbe`): an `async void M()` that
awaits a truly-incomplete Task (suspend), sets a host cell at the resume point, the
driver polls the cell. If green, keep as a permanent regression guard. If NOT green,
fix (lead evidence says green).

## Test design (adversarial; async-SM concat-free)

CRITICAL constraint: string concat INSIDE an async state machine (`"... " + intVar`)
lowers to `conv.ovf.u2.un` → NIE (Step 6 gap, NOT this fix). Async test bodies are
CONCAT-FREE; the existing TC8/TC12-TC14 probes avoid concat by returning `v + const`
(int arithmetic, not string). The new ValueTask probes mirror this — int/string
results returned via arithmetic or direct return, no host string-building inside the SM.

Probes (method names contain `NeoStep20_` so the `NeoStep` filter catches them):
1. **VT1 ValueTask<int> truly-async suspend+resume** — `async ValueTask<int>` awaits the
   deterministic incomplete `Task<int>` (host `GetIncompleteTask`), driver polls
   `!vt.IsCompleted` (suspend gate), `CompleteIncompleteTask(n)`, spin-waits, asserts
   `vt.Result == n + 3`. Load-bearing (exercises suspend-case + value-type return).
2. **VT2 ValueTask<int> sync completion** — `async ValueTask<int>` awaits an already-
   completed `Task.FromResult(7)` → asserts `Result == 10`. Exercises sync
   `CreateValueTaskFromResult` + value-type return.
3. **VT3 ValueTask<int> faulted** — `async ValueTask<int>` throws → driver observes
   `vt.IsFaulted`. Exercises `CreateFaultedValueTask` + value-type return.
4. **VT4 ValueTask<string> (ref-T) truly-async suspend** — `async ValueTask<string>`
   suspends+resumes → asserts the string result. Exercises the value-type return write
   with a ref-type T (the result is itself a ref, but the ValueTask<string> struct is
   still flat-bytes/RefCount=0 — T being a ref does not change the struct's Neo layout).
   Needs a host `Task<string>` incomplete source (add `GetIncompleteStringTask` +
   `CompleteIncompleteStringTask`).
5. **VT5 async void suspend** — the verify+guard. `async void` suspends+resumes,
   observed via a host side-effect cell.
6. **VT6 sync ValueTask<int> control** — no await, returns a constant. Happy path.

Each suspend probe MUST FAIL on HEAD (NRE / wrong result / garbage) and PASS after the
fix — load-bearing via stash-toggle (stash the engine file, keep tests, run probes).

## Build/test (ALWAYS -f net8.0; CLI=Debug_Neo, TestCases=Debug)

```
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep
```
Baseline 248/0/0. Build-cache gotcha: confirm DLL rebuilt (mtime / grep a new string
literal) before trusting `--no-build`. >10-60s run = infinite loop → KILL.
