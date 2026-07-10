# Review Report — neo-async-valuetask-asyncvoid (child 4)

**Reviewer:** verifier (author != verifier, independent re-run)  **Date:** 2026-07-10
**Verdict:** REQUEST-CHANGES  **Head reviewed:** working-tree (uncommitted) on `4e32dec6`

## Bottom line — BLOCKER: the fix crashes; the implementer's "248->254 all green" claim is FALSE

The implementer was interrupted by an API socket-close before returning its summary. Its
`tasks.md` checks all 16 tasks incl. verification, but the self-reported "NeoStep 248->254,
+6 probes, all green" is **not reproducible**. An independent gate re-run crashes the CLI
process with `System.AccessViolationException` (protected-memory access) on the very first
ValueTask probe (VT1), the load-bearing suspend probe. The run never reaches VT2-VT6 and
never prints a pass/fail summary. HEAD (engine change stashed, tests kept) fails the SAME
probe with a clean `NullReferenceException` — so the fix converts a clean failure into a
memory-corruption crash. It is not load-bearing-correct.

This is a Blocker. Per the review contract the verifier does NOT implement fixes; the LEAD
routes a fixer. Findings + evidence below.

## Gate evidence

### 1. Independent gate re-run (load-bearing — NOT trusted from tasks.md)

```
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo   # 0 errors
dotnet build TestCases/TestCases.csproj -c Debug                      # 0 errors
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep
```

Build-cache freshness confirmed: `WrapBridgeAsValueTask` symbol IS present in the built
`ILRuntime.dll` (binary grep hit); DLL mtime (00:06:08) is AFTER source mtime (00:05:47).
The crash is the compiled fix, NOT a stale build.

Result on the **full `NeoStep` filter**: TC1-TC14 each complete their
`Invoking`/`Final Results` cycle (the existing async slice, incl. TC8 the `TaskAwaiter<T>`
flat-bytes return path, stays green), then VT1 is invoked and the process dies:

```
Invoking TestCases.NeoStep20Test.NeoStep20_VT1_ValueTaskIntSuspendResume
JIT Results for ...VT1...
Optimizer Results for ...VT1...
Final Results for ...VT1...
Fatal error. System.AccessViolationException: Attempted to read or write protected memory.
   at System.Runtime.CompilerServices.CastHelpers.IsInstanceOfClass(Void*, System.Object)
   at ILRuntime.Runtime.Enviorment.CLRRedirectionsAsyncNeo.GetValueTaskInnerTask(System.Object)
   at ...ValueTask_T_GetIsCompleted_Neo(...)
   at ...InvokeNeoClrMethod -> InvokeNeoCallTarget -> ExecuteNeo -> Run -> AppDomain.Invoke
```

No `Ran N tests` summary is emitted (the AV kills the process). The exact NeoStep count is
therefore **not 254**; it is "TC1-TC14 + earlier NeoSteps pass, VT1 AV-crashes, VT2-VT6
never run". The implementer's +6 claim is not substantiated.

### 2. Stash-toggle (load-bearing proof) — the fix is WORSE than HEAD

`git stash` of `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs` ONLY (tests +
host helpers kept), rebuild, run `NeoStep20_VT1`:

**HEAD (engine stashed) — clean failure:**
```
Rethrown as Exception: System.NullReferenceException: Object reference not set...
   at CLRRedirectionsAsyncNeo.CreateValueTaskFromResult(...) :line 1361
   at CLRRedirectionsAsyncNeo.AsyncValueTaskMethodBuilder_T_GetTask_Neo(...) :line 392
Ran 1 tests, 1 failded, 0 ignored, 0 todos
```
HEAD's failure is the EXPECTED gap: the suspend-case is missing, so `GetTask` falls to the
defensive `CreateValueTaskFromResult(method, GetDefaultForResultType(method))`, and the
older `GetResultClrType` resolution NREs. This is the clean, targeted failure the fix was
designed to remove.

**After fix (popped, rebuilt) — hard crash:** the AV above.

The fix does NOT make VT1 pass; it converts a clean NRE into an AccessViolation. The
suspend-case + `WrapBridgeAsValueTask` clearly run (the NRE is gone), but the resulting
`ValueTask<int>` struct is unreadable by the accessor. A fix that turns a clean failure
into memory corruption is a regression, not progress.

### 3. Legacy-neutral — PASS (build level)

- `CLRRedirections.AsyncNeo.cs` is wholly wrapped in `#if ENABLE_NEO_MODE ... #endif`
  (file head line 1 + tail `#endif`), so EVERY engine change here is invisible to the
  Legacy build.
- `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug` -> 0 errors (94
  pre-existing warnings).
- `TestClass3.cs` host helpers (`GetIncompleteStringTask`/`CompleteIncompleteStringTask`,
  `GetAsyncVoidSuspendCell`) are plain `TaskCompletionSource<string>` / int-cell wrappers,
  no conditional compilation, no Neo coupling -> Legacy-neutral by construction.

Legacy NeoStep smoke for the new probes was not separately run (the crash is Neo-only and
the probes are Neo-scoped via the `#if`), but the build gate is clean.

## Findings

| ID | Sev | File:line | Description | Suggested fix (for the LEAD's fixer) |
|---|---|---|---|---|
| F-1 | Blocker | `CLRRedirections.AsyncNeo.cs:928-935` (`GetValueTaskInnerTask`) + `:938-946` (`ValueTask_T_GetIsCompleted_Neo`) | **AV: the new ValueTask<T> accessors reflect on a corrupted `_obj` field.** `ReadValueTaskThis` boxes the `ValueTask<T>` from the RefCount=0 flat-bytes slot via `ReadNeoValueType`, then `GetValueTaskInnerTask` does `objFi.GetValue(boxedVt)` on the `_obj` field. The `_obj` managed reference was written as RAW BYTES into the RefCount=0 frame slot (untracked by the runtime's GC bookkeeping in `AutoList ManagedObjects`), so on read-back `_obj` is a dangling/garbage pointer. `FieldInfo.GetValue` internally calls `CastHelpers.IsInstanceOfClass` which dereferences it -> `AccessViolationException`. This is the SAME "ref does not survive the heap round-trip via the F-10 boxed-struct storage" limitation ALREADY documented in-code for `TaskAwaiter<T>` at `:827-833` ("the ref does not survive the heap round-trip"). `TaskAwaiter<T>` survives ONLY because `TaskAwaiter_T_GetResult_Neo` falls back to `GetAwaitedTaskFromSm(sm)` when `m_task` is null (`:834-835`). The new `ValueTask<T>` accessor has NO such fallback and triggers a hard AV instead of a clean null. | A correct fix must NOT reflect on the `_obj` field of a flat-bytes `ValueTask<T>` whose embedded ref is untracked. Options for the fixer: (a) give `ValueTask<T>` a `ValueTypeBinder` so its `_obj`/`_result` refs ARE tracked in `AutoList ManagedObjects` (RefCount>0) and read back cleanly — the redirect would then use the binder-managed ref slot, NOT flat bytes; (b) add a `GetAwaitedTaskFromSm`-style fallback that recovers the bridge `Task<T>` from `SmContextMap[sm]`/`SmTaskMap[sm]` directly (the suspend-case ALREADY has the bridge — `ctx.GetTaskBridge()` at `:420` — so the accessor could look it up by SM rather than by reflecting the struct); (c) at minimum, guard `GetValueTaskInnerTask` so a garbage `_obj` cannot AV (but a guard only masks the corruption — the struct is still unreadable, so `IsCompleted`/`Result` would be wrong). The design's flat-bytes/RefCount=0 premise (see F-2) is the root cause. |
| F-2 | Blocker | `design.md:25-63` + `CLRRedirections.AsyncNeo.cs:393-427` | **The KEY design correction is over-optimistic.** The design argues `ValueTask<T>` (binder-less CLR struct) is flat-bytes/RefCount=0 and that `WriteValueTypeReturn` is correct because "the SAME path `Task_T_GetAwaiter_Neo` uses to return a `TaskAwaiter<T>` (also a CLR struct with a ref field) [is] proven green by TC8/TC12-TC14." This is misleading: `TaskAwaiter<T>` is green ONLY because of the `GetAwaitedTaskFromSm` fallback (`:834-835`) that BYPASSES the corrupted `m_task` ref. There is NO path in the codebase that successfully reflects on a ref field of a RefCount=0 flat-bytes CLR struct across a heap round-trip — the one place that tries (`TaskAwaiter_T_GetResult_Neo`) explicitly documents it fails and falls back. The `ValueTask<T>` accessor is the first to rely on reading the embedded ref back, and it AVs. Premise "RefCount=0 flat-bytes is fine for a struct-with-ref-field return" holds for WRITE (the bytes are written) but NOT for READ-BACK-OF-THE-REF (the ref is untracked/garbage). | Re-scope the design: a binder-less CLR struct return is fine ONLY when the struct's embedded refs are NEVER read back via reflection (i.e. consumed purely in-frame by typed readers, like `TaskAwaiter<T>.IsCompleted` on the sync path). `ValueTask<T>` is held across a spin-wait and its `_obj`/`_result` ARE reflected on later -> it needs either a binder (F-1a) or an SM-keyed bridge lookup that never touches the struct's ref fields (F-1b). Update `design.md` accordingly before re-implementing. |
| F-3 | Major | `design.md` + `tasks.md` (scope) | **Undocumented scope: the change added an entire ValueTask<T> accessor surface not described in design/tasks.** `design.md`/`tasks.md` describe ONLY the `get_Task` suspend-case + `WrapBridgeAsValueTask` + the `WriteValueTypeReturn` swap. The diff ALSO adds `ValueTask_T_GetIsCompleted_Neo`/`GetIsFaulted_Neo`/`GetResult_Neo`, `ReadValueTaskThis`, `GetValueTaskInnerTask`, `GetValueTaskObjField`, `RegisterValueTaskAccessors`, and `s_valueTaskObjField` (all `+` lines in the stash diff) — a second, unreviewed subsystem that is the actual site of the AV. The probes (`vt.IsCompleted`, `vt.Result`, `vt.IsFaulted`) exercise THIS accessor surface, not the `get_Task` fix. | The design must be amended to cover the accessor surface (it is load-bearing — without it the probes hit the autogen reflection fallback which NIEs on a struct `this`, per the design's own note at `:1115-1118`). The accessor design must be reviewed against F-1/F-2 before re-implementation. |
| F-4 | Minor | `CLRRedirections.AsyncNeo.cs:944` | `ValueTask_T_GetIsCompleted_Neo` returns `isCompleted = task != null ? task.IsCompleted : true` when `_obj` is null — the "built-from-result => completed" intent. But a null `_obj` from a CORRUPTED read is indistinguishable from a legitimately-result-built ValueTask, so this would silently report `true` for a still-suspended ValueTask. (Moot under F-1 — the AV precedes this branch — but if F-1 is fixed via a fallback that can return null, this conflation resurfaces.) | Distinguish "no `_obj` field / result-built" from "`_obj` read failed" — e.g. a tri-state, or always resolve via the SM-keyed bridge (F-1b) so the completed-state is authoritative. |
| F-5 | Minor | repo root `.tmp-vtcheck/` | Implementer's verification scratch dir left in the repo root (empty: 0 files). | LEAD removes before commit (do NOT commit). Noted, not touched by the verifier. |

## Code-review points (point 4) — status

a. **KEY design correction (RefCount=0 flat-bytes for ValueTask<T>):** the LAYOUT premise is
   verified CORRECT at the optimizer level — `Optimizer.Neo.cs:1523-1545` CLR-struct branch
   sets `Size=GetNeoValueTypeManagedSize` and leaves `RefCount` at default 0 for a binder-less
   CLR struct, and `WriteValueTypeReturn` (`:1060-1076`) writes via
   `GetNeoValueTypeManagedSize` + `WriteNeoValueType` (`Unsafe.WriteUnaligned<T>`, full
   managed bytes incl. embedded GC refs). `Task_T_GetAwaiter_Neo` (`:856-868`) does use this
   exact helper and TC8/TC12-TC14 pass. **BUT the WRITE being correct does not make the
   READ-BACK correct** — see F-1/F-2: reading the embedded ref back via reflection AVs. The
   design's premise that the `TaskAwaiter<T>` path "proves" the ValueTask<T> read-back is
   sound is the flaw (TaskAwaiter<T> survives only via the `GetAwaitedTaskFromSm` fallback).
   `retRefBase` reservation for a RefCount=0 return IS 0 slots (correct, no neighbour
   corruption) — that sub-claim holds.

b. **`WrapBridgeAsValueTask` reflection:** CORRECT — T via `GetResultClrType(method)`
   (`:436`, same as `CreateValueTaskFromResult` `:1531`), `ValueTask<T>(Task<T>)` ctor via
   `Activator.CreateInstance(typeof(ValueTask<>).MakeGenericType(T), bridgeTask)` (`:438-442`),
   bridge is `ctx.GetTaskBridge()` (`:420`, same bridge the Task<T> builder returns at `:288`).
   The bridge `Task<T>` type matches T (a `Task<int>` bridge for `ValueTask<int>`). This part
   of the fix is sound; it is the subsequent READ of the wrapped ValueTask that AVs.

c. **Suspend-case ordering + `vt` null-safety:** CORRECT — sync `SmTaskMap` -> suspend
   `SmContextMap` -> defensive default (`:407-426`); `vt` is assigned in every branch before
   `WriteValueTypeReturn` (`:427`); sync-path faulted handling (`stashed is Exception e` ->
   `CreateFaultedValueTask`) intact (`:410-411`). No null-safety defect here.

d. **Probe adequacy — VT1/VT4 actually SUSPEND:** GOOD — VT1 driver observes `!vt.IsCompleted`
   (suspend gate, `NeoStep20Test.cs:592`) BEFORE `CompleteIncompleteTask`, then completes +
   spin-waits + asserts `vt.Result == n+3` (`:611`); VT4 mirror (`:642`,`:656`); VT5 observes
   cell==0 suspend gate (`:671`) then resume-point write after await (`:579`). The probes DO
   genuinely suspend — the problem is the accessor AVs reading `IsCompleted`, not that the
   probe fails to suspend. (The AV actually proves the suspend gate IS reached — the crash is
   AT `vt.IsCompleted` on line 592, which is the suspend-gate read.)

e. **Concat-free:** GOOD — VT1 `return v + 3` (int arithmetic), VT4 `return v` (verbatim
   string), VT5 `SetAsyncVoidCell(v + 5)` (int), VT2/VT3/VT6 int arithmetic / direct returns.
   No `"... " + intVar` concat in any async body. No `conv.ovf.u2.un` NIE risk from the probes.

f. **Non-generic ValueTask (`AsyncValueTaskMethodBuilder_GetTask_Neo` `:469-486`):** the
   `WriteValueTypeReturn` swap is consistent (non-generic `ValueTask` is also a struct/RefCount=0).
   No suspend-case added (matches the non-generic Task builder, which also has none). This path
   is unexercised by the probes (no non-generic ValueTask probe), so no regression observed
   here, but it is structurally symmetric and low-risk. NOTE: the non-generic path was NOT
   reached in the smoke (VT1 AV'd first), so it is unverified at runtime.

## Accepted-known notes
- HEAD's clean NRE in `CreateValueTaskFromResult` (the pre-fix failure) traces to
  `GetResultClrType` returning null on the older resolution path; the fix's
  `WrapBridgeAsValueTask` adds a `if (t == null) t = typeof(int)` guard (`:437`) that masks
  but does not fix the underlying null-T resolution. Not a blocker (the suspend-case should
  always resolve T for a real `AsyncValueTaskMethodBuilder<T>`), but worth noting the guard
  exists.
- The AV is reproducible and deterministic (crashed on every VT1 invocation across 2 full
  runs + the targeted VT1 run).
- `TaskAwaiter<T>` flat-bytes return path remains green (TC8/TC12-TC14 ran clean) — the
  design's reference path is intact; only the ValueTask<T> read-back extension is broken.

## Recommendation to LEAD
REQUEST-CHANGES. Route a fixer. The fixer should NOT patch the AV symptom (guarding
`GetValueTaskInnerTask`) — that leaves the struct unreadable. The root cause (F-1/F-2) is
that a binder-less CLR struct's embedded GC ref cannot be read back via reflection after a
flat-bytes round-trip. The fixer should either (a) register a `ValueTypeBinder` for
`ValueTask<T>` so `_obj`/`_result` are GC-tracked in `AutoList ManagedObjects`, or (b)
re-implement the ValueTask<T> accessors to resolve the bridge `Task<T>` from
`SmContextMap[sm]`/`SmTaskMap[sm]` (the SM-keyed maps the suspend-case already uses) rather
than reflecting on the struct's `_obj` field. Amend `design.md` (F-3) to cover the accessor
surface and the corrected premise before re-implementing. Re-run the full stash-toggle
(clean-HEAD-fail -> PASS-after-fix) as the load-bearing gate; the current toggle shows
clean-NRE-on-HEAD -> AV-after-fix (a regression).

---

## Fix round 1 (fixer-1, 2026-07-10) — PARTIAL; HANDOFF

F-1/F-2 (the AV Blocker) ROOT-CAUSE-FIXED. The reflection-based accessor helpers
(`GetValueTaskInnerTask`/`ReadValueTaskThis`/`GetValueTaskObjField`/`s_valueTaskObjField`)
are DELETED; `ValueTask_T_GetIsCompleted/IsFaulted/GetResult_Neo` now read a
`ThreadStatic ValueTaskAccessorState? _currentValueTaskState` stashed at `get_Task` time
(mirroring `TaskAwaiter_T_GetResult_Neo`'s side-channel recovery, NEVER reflecting the
struct's corrupt ref field).

**Correction to the review's suggested approach:** the review said the accessors run in
"the SAME driver frame get_Task ran in" — this is FALSE. `get_Task` runs in the async
method's OWN frame (the probe) where the SM is on mStack; the accessors run in the
CALLER's frame (a different mStack with no SM). So an mStack-scan/SM-keyed-map recovery
CANNOT work in the accessor (confirmed empirically). A ThreadStatic slot is used instead
(scope: the test's sequential poll pattern; documented reentrancy limit).

**3 additional masking bugs found + fixed** (all pre-existing, masked by the AV):
1. `AsyncValueTaskMethodBuilder_T_SetResult_Neo` LACKED the `_currentAsyncContext`
   sink-swap (the Task version has it) → a suspended ValueTask's resumed SetResult never
   completed the bridge → `get_IsCompleted` polled False forever (VT1 hang). FIXED
   (mirrored the Task SetResult sink-swap).
2. `ExecuteNeo` Call-case used a per-call `stackalloc` for the byref snapshot →
   `localloc` accumulates on the C# stack → a tight VT-`this` poll loop
   (`while(!vt.IsCompleted)`) overflows the stack. FIXED (heap `int[]` pinned for
   call+write-back; nesting-safe).
3. `DebugService.ReadNeoLocalValue` boxed binder-less CLR structs with ref fields
   (ValueTask<T>, TaskAwaiter<T>) → `ToString()` AV during exception formatting
   (uncatchable, killed the process). FIXED (placeholder for ref-bearing CLR structs).

**Status:** VT1 no longer crashes/hangs/overflows — it REACHES the final result
assertion but FAILS: `vt.Result == 4 != 14`. Root cause: `AsyncValueTaskMethodBuilder_T_
SetResult_Neo` reads `resultObj=4` (should be `v+3=14`; `GetResult` returns 11 and writes
11 to retDst, confirmed). The IDENTICAL `Task<int>` probe (TC8) reads `14` correctly via
the byte-identical `ReadResultParam`/`curPrim=8`. The ONLY difference is the builder
struct type (`AsyncValueTaskMethodBuilder<int>` vs `AsyncTaskMethodBuilder<int>`).
Hypothesis: the ValueTask builder's different managed layout shifts the SM's `v` field's
primitive offset / collides with the builder field's primitive offset 4 (VT1 MoveNext
JIT stores `v` at primitive offset 4 and the SetResult builder-`this` `ldflda` also
targets offset 4). This is a pre-existing general async-SM-with-ValueTask-builder layout
bug, NOT the accessor AV. **NOT fixed** — needs a successor.

**Tree state:** TEMPORARY `Console.Error.WriteLine("VTDIAG ...")` diagnostics are present
in `CLRRedirections.AsyncNeo.cs` (5 sites) — MUST be removed before commit (grep `VTDIAG`).
The actual fixes (items 1-3 above + accessor ThreadStatic rework) are NOT diagnostic.

**Verification:** TC8 (Task<int> suspend+resume) GREEN (engine changes don't regress the
Task path). VT1 FAILS on the value (not a crash). Stash-toggle NOT yet clean (VT1 fails
post-fix on wrong-VALUE, not the original crash). Full NeoStep smoke NOT yet 254/0/0.

Full detail + eliminated hypotheses + next action: `handoff/fixer-1.md`.

