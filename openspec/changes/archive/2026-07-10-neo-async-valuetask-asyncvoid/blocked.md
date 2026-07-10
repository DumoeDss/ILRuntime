# BLOCKED — neo-async-valuetask-asyncvoid (child 4)

**Date:** 2026-07-10  **State:** PARKED — blocked by a CLUSTER of deeper engine gaps
(out of child-4 scope). Partial work preserved in git stash
`child4-valuetask-blocked-partial` (pop to resume).

> **IMPLEMENTATION ROUND 2 RESOLVED (2026-07-10) — UNBLOCKED. VT1-VT6 ALL GREEN.**
> All three blockers (B1/B2/B3) are FIXED. The "foundational engine gaps" framing was
> WRONG: B1 was DISPROVEN (a call-arg-marshalling bug, not field-layout), B2 was a
> registration-completeness miss (not a binder gap), and B3 was a reflection-overload
> ambiguity. Full detail in `tasks.md` items 17-19. Summary:
> - **B1 (VT1/VT2/VT6) — DISPROVEN + re-rooted to `curPrim` (call-arg marshalling).** The
>   builder `this` is a VALUE TYPE; the engine copies its flat managed bytes
>   (`Unsafe.SizeOf<T>` = 16 for the ValueTask builder, 8 for the Task builder) into the
>   callee param region. `SetResult`/`SetException` hardcoded `curPrim += 8` → undershot
>   for the 16-byte ValueTask builder → read stale struct bytes (resultObj=4) instead of
>   the real result (14). New `BuilderThisManagedSize(method)` skips the actual size
>   (`Optimizer.GetNeoValueTypeManagedSize`). NO `ILType.cs` change (the shared
>   `PrimitiveOffset` is benign — disjoint `Primitives[]`/`ManagedObjects[]`). Cross-ref:
>   `../neo-clrstruct-sm-field-layout/blocked.md`.
> - **B3 (VT3) — `CreateFaultedValueTask` AmbiguousMatchException.** `Task.FromException`
>   has two overloads both taking `(Exception)` (one generic). Resolved the generic def
>   via `Array.Find(..., m => m.Name == "FromException" && m.IsGenericMethod)` + closed it
>   with T.
> - **B2 (VT4) — registration-completeness gap, NOT a foundational binder gap.** VT4
>   (ValueTask<string>) calls `AsyncValueTaskMethodBuilder<string>`, `TaskAwaiter<string>`,
>   and `Task<string>` members that were NOT registered (only `<int>`/`<ILTypeInstance>`).
>   The unregistered calls fell to the reflection fallback, whose Area-4b guard NIEs on a
>   struct-`this`-with-ref-field. Fix: add `<string>` to the builder/awaiter/Task accessor
>   registrations (mirrors `<int>`/`<ILTypeInstance>`). The redirect path bypasses
>   reflection entirely, so the Area-4b guard is never reached. **VT4 is GREEN — not parked.**
>
> **Verification:** NeoStep smoke **273 ran, 0 failed**; VT1-VT6 6/6 green; TC1-TC14 +
> prior NeoStep unchanged. Stash-toggle: stash ONLY `CLRRedirections.AsyncNeo.cs` →
> VT1/VT2/VT3/VT4/VT6 fail (5/6; VT5 async-void unaffected); pop → 6/6 green. Legacy-
> neutral (plain `Debug` 0 errors; file `#if ENABLE_NEO_MODE`-gated; `ILType.cs` empty
> diff). VTDBG2 + VTDBG4 diagnostics removed.

## LEAD smoke verdict (2026-07-10, HEAD 4e32dec6 + fixer-1 partial work)

Full `NeoStep` smoke: **254 ran, 5 failed** (the 6 VT probes minus VT5):
- **VT1 `NeoStep20_VT1_ValueTaskIntSuspendResume`** — FAIL (DivideByZero assertion trap; the result is wrong).
- **VT2 `NeoStep20_VT2_ValueTaskIntSync`** — FAIL (DivideByZero; sync path returns wrong value too).
- **VT3 `NeoStep20_VT3_ValueTaskIntFaulted`** — FAIL `AmbiguousMatchException` at
  `CLRRedirections.AsyncNeo.cs:1632` (`CreateFaultedValueTask`: `Task.FromException(Exception)` overload ambiguity).
- **VT4 `NeoStep20_VT4_ValueTaskStringSuspend`** — FAIL `NotImplementedException` "CLR value-type `this`
  with reference fields and no ValueTypeBinder (Step 13 Area 4b): register a binder. Type:
  AsyncValueTaskMethodBuilder<string>" (the F-3 / NEO-BYREF-THIS family — a CLR-struct-with-ref
  passed as a byref `this` to a CLR method via the reflection fallback).
- **VT6 `NeoStep20_VT6_ValueTaskIntSyncControl`** — FAIL (DivideByZero; sync control wrong value).
- **VT5 `NeoStep20_VT5_AsyncVoidSuspend`** — **PASS** (async void suspend+resume works — the one green probe).

## The blocker cluster (3 distinct root causes; 2 are foundational/pre-existing)

### B1. Field-offset collision — VT1/VT2/VT6 (F-10 family) [FOUNDATIONAL, pre-existing]
An async `ValueTask<T>` state machine's hoisted primitive local (`v`) and the `<>t__builder` field
(a CLR struct WITH a reference field) are BOTH assigned Neo **primitiveOffset 4** in the SM's
`ILType` field layout. Evidence (fixer-2 transcript `agent-a3080fcf1da011349.jsonl`):
- `stfld.i4 r0, r7, (4,2)` stores `v` (int) at primitiveOffset 4.
- `stfld.ref r0, r2, (4,0)` stores the builder (boxed ref) at primitiveOffset 4.
The identical `Task<int>` probe (TC8) does NOT collide (its builder has a different managed size →
`v` lands elsewhere). Even the SYNC probes (VT2/VT6) hit this (any `ValueTask<T>` SM that hoists a
local alongside the builder). Root cause site: the `ILType` field-layout allocation for a
CLR-struct-with-reference-field that is a FIELD of an IL class (the SM) — the F-10 /
NEO-CLRSTRUCT-FIELD-OF-IL family (the `<>t__builder` field's primitive region must advance so the
sibling primitive local does not reuse its offset). **This is its own child** (e.g.
`neo-clrstruct-sm-field-layout`); NOT child-4 scope.

### B2. CLR-struct-with-ref `this` binder NIE ��� VT4 (F-3 / NEO-BYREF-THIS family) [FOUNDATIONAL, pre-existing]
The `AsyncValueTaskMethodBuilder<T>` passed as a byref `this` to a CLR method (SetResult/internal)
via `CLRMethod.Invoke`'s reflection fallback hits the Step 13 Area 4b guard: "CLR value-type `this`
with reference fields and no ValueTypeBinder — register a binder." Fires for T=string (VT4); T=int
(VT1) gets past it (different builder internal shape) but then hits B1. This is the NEO-BYREF-THIS /
F-3 family for CLR structs with reference fields — a known foundational gap. **Its own child** (or
fold into the F-3 follow-up); NOT child-4 scope.

### B3. `CreateFaultedValueTask` AmbiguousMatchException — VT3 [EASY, child-4-own]
`CLRRedirections.AsyncNeo.cs:1632` resolves `Task.FromException(Exception)` via reflection without
overload disambiguation → `AmbiguousMatchException` (there are `Task.FromException` overloads).
Easy fix: specify the overload (`GetMethod("FromException", new[]{ typeof(Exception) })` or the
generic `Task<T>.FromException`). This IS child-4 scope; fix it when B1/B2 are resolved.

## What IS done + verified (fixer-1, TC8-green — preserved in the stash)
1. ValueTask<T> accessor AV root-cause-FIXED: accessors read a `ThreadStatic
   ValueTaskAccessorState` stashed at get_Task time (NOT reflecting the struct's corrupt ref — the
   `TaskAwaiter<T>.m_task` precedent generalized). **CORRECTED the review's premise:** accessors run
   in the CALLER's frame (not get_Task's frame), so SM-keyed-map recovery does NOT work; the
   ThreadStatic slot is the correct side channel.
2. `AsyncValueTaskMethodBuilder_T_SetResult_Neo` sink-swap added (was the VT1 hang).
3. `get_Task` sync-faulted classification + accessor-state stash (all branches).
4. **`ExecuteNeo` Call-case `stackalloc`→heap buffer** (a REAL pre-existing engine bug: per-call
   `stackalloc` accumulated on the C# stack → overflow in tight VT-`this` loops). Neo-gated.
5. **`DebugService.ReadNeoLocalValue` AV guard** for binder-less CLR structs with ref fields (a REAL
   pre-existing engine bug: boxing a struct with a dangling ref → AV during ToString). 
Items 4 + 5 are genuine engine-hardening fixes INDEPENDENT of ValueTask (latent without the probes
that exercise them). They are preserved in the stash; re-apply when child 4 (or a foundational
child) is tackled — or extract + ship standalone as a hardening commit if desired.

## Recovery path (for the successor that resumes child 4)
1. `git stash pop` (restore the partial work; VTDIAG diagnostics already removed — confirmed grep 0).
2. Fix B3 (the easy AmbiguousMatch in `CreateFaultedValueTask`).
3. Tackle B1 (the field-offset collision) — needs its own focused child; read the `ILType` field-
   layout allocation for a CLR-struct-with-ref field on an IL class, find why the builder field does
   not advance the primitive offset. Dump `typeof(AsyncValueTaskMethodBuilder<int>)` vs
   `typeof(AsyncTaskMethodBuilder<int>)` layouts.
4. Tackle B2 (the binder NIE) — the F-3/NEO-BYREF-THIS follow-up for CLR-struct-with-ref `this`.
5. Re-verify VT1-VT6 green + smoke 254/0/0 + stash-toggle + Legacy-neutral.

## Why parked (not forced through)
Forcing the 2 foundational gaps (B1 F-10-family, B2 F-3-family) inline under child 4 = scope
explosion + the same depth that defeated 2 subagent attempts (fixer-1 ran out of context, fixer-2
502'd mid-investigation). The disciplined move: sequence B1 + B2 as their own children, then child 4
unblocks cleanly. Meanwhile the portfolio's 15 sibling children proceed (they are serial-by-shared-
files, not functionally gated on child 4). See `review-report.md` + `handoff/fixer-1.md` for full detail.
