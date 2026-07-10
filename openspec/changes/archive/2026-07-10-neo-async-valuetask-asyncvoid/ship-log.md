# Ship Log — neo-async-valuetask-asyncvoid (child 4) — UNBLOCKED + SHIPPED

**Date:** 2026-07-10  **Capability:** neo-async  **Wave:** completion-3, child 4
**Status:** SHIPPED (LEAD-verified). Was PARKED (lead-7) on a "cluster of foundational engine gaps";
**UNBLOCKED** when the foundational framing was DISPROVEN — all 3 blockers were tractable,
child-4-scope fixes.

## Delivered
**Async `ValueTask<T>` suspend path + async void suspend path** — all 6 probes (VT1-VT6) GREEN.
`NeoStep 273/0/0`. The prior "foundational gaps" framing (B1 field-layout, B2 binder) was WRONG on
both counts:

### B1 (VT1/VT2/VT6) — DISPROVEN + re-rooted to a curPrim call-arg-marshalling bug
The builder `this` is a VALUE TYPE; the engine's call-arg lowering copies its FLAT MANAGED bytes
(`Unsafe.SizeOf<T>` = **16** for `AsyncValueTaskMethodBuilder<T>`, 8 for the Task builder) into the
callee param region. `SetResult`/`SetException` hardcoded `curPrim += 8` → undershot for the 16-byte
ValueTask builder → `ReadResultParam` read stale struct residue (`frameBase[8]=4`) instead of the real
result (`frameBase[16]=14`). TC8 (Task<int>) only worked because the Task builder is coincidentally
8 bytes. **Fix:** `BuilderThisManagedSize(method)` = `Optimizer.GetNeoValueTypeManagedSize(...)`,
applied in SetResult/SetException (Task + ValueTask). **No `ILType.cs` change** — the shared
`PrimitiveOffset` is BENIGN (disjoint `Primitives[]`/`ManagedObjects[]` storage; the field-layout
collision was a red herring). Cross-ref: `../neo-clrstruct-sm-field-layout/blocked.md` (the B1
disproof).

### B3 (VT3) — CreateFaultedValueTask AmbiguousMatchException
`Task.FromException` has two overloads both taking `(Exception)` (one generic). Fixed via
`Array.Find(methods, m => m.Name=="FromException" && m.IsGenericMethod)` + `MakeGenericMethod(T)`.

### B2 (VT4) — a registration-completeness miss, NOT a foundational binder gap
VT4 (`ValueTask<string>`) called `AsyncValueTaskMethodBuilder<string>`, `TaskAwaiter<string>`, and
`Task<string>` members that were NOT registered (only `<int>`/`<ILTypeInstance>`). The unregistered
calls fell to the reflection fallback, whose Area-4b guard NIEs on a struct-`this`-with-ref-field.
**Fix:** add `<string>` to `RegisterValueTaskBuilderT`/`RegisterAwaiterAccessors`/`RegisterTaskAccessors`
(mirrors `<int>`/`<ILTypeInstance>`). The redirect path bypasses reflection entirely. **VT4 GREEN.**

## Verification (LEAD-verify)
- **NeoStep smoke (LEAD re-ran): 273 tests, 0 failed.** (Fixes the brief red-smoke at 98bc6651, which
  had inadvertently committed the child-4 partial work + red VT probes.)
- **VT1-VT6: 6/6 GREEN** (suspend+resume, sync, faulted, ValueTask<string>, async-void, sync-control).
- **Stash-toggle (load-bearing):** stash ONLY `CLRRedirections.AsyncNeo.cs` → VT1/VT2/VT3/VT4/VT6 fail
  (5/6; VT5 async-void unaffected); pop → 6/6 GREEN.
- **Legacy-neutral:** plain `Debug` build 0 errors (whole-file `#if ENABLE_NEO_MODE`-gated); `ILType.cs`
  empty diff.
- VTDBG2/VTDBG4 diagnostics removed (grep 0).

## Files
- `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs` (the curPrim + B3 + B2 fix; +80/-13;
  `#if ENABLE_NEO_MODE`). The fixer-1 partial (the ThreadStatic accessor AV-fix + the SetResult
  sink-swap + the `ExecuteNeo` Call-case stackalloc→heap overflow fix + the `DebugService` AV guard +
  the `TestClass3` host helpers + the VT1-6 probes) was already on HEAD (committed in 98bc6651) +
  correct — preserved.

## Durable findings (cross-cutting — corrects the lead-7 handoff's "foundational gaps" framing)
1. **B1 field-layout collision is a RED HERRING** — a CLR-struct-with-ref field + a sibling primitive
   field sharing `PrimitiveOffset` is benign (disjoint storage). Don't chase field-layout for the
   async-SM case.
2. **The ValueTask builder `this` marshals as 16 flat bytes (vs the Task builder's 8).** Any redirect
   that skips a builder byref-`this` must use `BuilderThisManagedSize` (the actual managed size), not a
   hardcoded 8. (Generalizes: any CLR-struct-`this` redirect must size the `this` correctly.)
3. **A "binder NIE" on a generic-BCL-struct `this` may be a REGISTRATION miss, not a foundational
   gap** — register the closed generic (e.g. `<string>`) so the call takes the redirect path (which
   handles the struct `this` via `CurrentAsyncSm` + `BuilderThisManagedSize`) instead of the reflection
   fallback. Re-audit the F-3/NEO-BYREF-THIS "foundational gap" framing against this pattern.
4. **`Task.FromException` has TWO `(Exception)` overloads** (one generic) — reflection resolution
   needs `IsGenericMethod` disambiguation.

## UNBLOCKS
- **child 6 (`neo-async-valuetask-zeroalloc`)** — its functional prereq (the ValueTask suspend path)
  is now done. Runnable.

## Review
LEAD-verify (NeoStep smoke re-ran 273/0; VT1-6 6/6; stash-toggle 5/6→6/6; Legacy build 0 errors;
VTDBG2 removed). The child-4 reviewer's original AV Blocker (fixer-1's ThreadStatic fix) + the
round-2 curPrim/B3/B2 fixes together complete the ValueTask suspend path.
