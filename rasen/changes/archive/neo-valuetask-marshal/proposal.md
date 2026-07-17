## Why

The `neo-overhaul` portfolio framed `neo-valuetask-marshal` (child 20) around a
`ValueTask<T>` async call-arg-marshalling bug: `AsyncValueTaskMethodBuilder_T_SetResult_Neo`
supposedly did `curPrim += 8`, undershooting the ValueTask builder's 16-byte byref-`this`,
so `ReadResultParam` read a stale 2nd qword instead of the real `T` result (VT1/VT2/VT6
failing). **Investigation proves that bug was already fixed** at commit `9c9b795d`
("Neo async: ValueTask<T> + async-void suspend path (child 4 UNBLOCKED)"), which
replaced the hardcoded `+= 8` with a dynamic `BuilderThisManagedSize(method)` skip
(`Unsafe.SizeOf<builder>`). VT1-VT6 + `VT_ZeroAlloc` all pass on HEAD; the full Neo run
has zero ValueTask/async NIEs. This change exists to **close the framed child honestly
and pin the load-bearing marshalling rule in the spec** so a future "simplification"
cannot regress `BuilderThisManagedSize` back to a hardcoded constant.

## What Changes

- **No engine change.** The fix is already on HEAD (`CLRRedirections.AsyncNeo.cs:449`
  `curPrim += BuilderThisManagedSize(method)`; `BuilderThisManagedSize` at `:1702` calls
  `Optimizer.GetNeoValueTypeManagedSize(decl)` = `Unsafe.SizeOf<T>`). `git diff HEAD` on
  `CLRRedirections.AsyncNeo.cs` is empty. The probes (`NeoStep20_VT1..VT6`, `VT_ZeroAlloc`)
  are committed in `TestCases/NeoStep20Test.cs`.
- **Spec clarification (the only artifact edit):** ADDED a requirement to `neo-async`
  pinning the builder byref-`this` call-arg marshalling rule — the callee param region
  carries the builder struct's flat managed bytes (`Unsafe.SizeOf<builder>`), NOT an
  8-byte byref slot, so `SetResult`/`SetException` redirects SHALL skip the builder's
  actual managed size. Guards the `8 (Task builder) vs 16 (ValueTask builder)` distinction.
- **Disproof record:** documents that the task framing was stale (built from the
  2026-07-10 `blocked.md`, which predated `9c9b795d`), and that the sibling B1
  (field-layout collision) / B2 (binder NIE) / B3 (AmbiguousMatch) / async-Task-Start
  concerns are ALL resolved on HEAD.

## Capabilities

### New Capabilities
<!-- None. -->

### Modified Capabilities
- `neo-async`: ADDED a requirement pinning the async-builder byref-`this` call-arg
  marshalling rule (the builder value-type `this` occupies `Unsafe.SizeOf<builder>`
  flat managed bytes in the callee param region; `SetResult`/`SetException` SHALL skip
  that size, not a hardcoded 8). The existing "Sync-completing async method returning
  ValueTask<T>" scenario already covers the observable behavior; this pins the
  non-obvious internal invariant that makes it correct.

## Impact

- **Code:** none (verify-only; the fix + probes are already on HEAD `e1e7ee7c`).
- **Specs:** `rasen/specs/neo-async/spec.md` gains one requirement (delta in this change).
- **Tests:** no new probes — `NeoStep20_VT1..VT6` + `VT_ZeroAlloc` already exist and pass
  (7/0 on the `NeoStep20_VT` filter; included in the 354/0 NeoStep smoke).
- **Legacy-neutral:** by construction — the entire async-redirect surface is
  `#if ENABLE_NEO_MODE`.
