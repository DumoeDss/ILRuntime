# Planning Context — neo-f7-delegate-byref (F-7 TRUE COMPLETION)

> SEED. F-7: a delegate with a `ref`/`out` param invoked via Neo (the CLR->IL callback,
> e.g. `List.ForEach(actionWithRefParam)`). The byref is destroyed in 2 sites. Fix = a
> frame-to-frame delegate-invoke fast path. The latent-edges triage gave the diagnosis.

## The diagnosis (from neo-latent-edges, do NOT re-derive)

F-7 is REPRODUCIBLE (3/3 FAIL on HEAD: `ref int` @ `ILIntepreter.Neo.cs:3726`, `out int` @
`:3636`, `ref string` @ `:3831`). The byref Ref Slot is destroyed in TWO sites:
1. **`ReadNeoDelegateInvokeArgs`** (`ILIntepreter.Neo.cs:246-287`) funnels EVERY delegate-Invoke
   arg through `object[]` -- reads only the 4-byte `objectIndex` half of the 8-byte Ref Slot,
   DROPS the offset. So the byref is lost before it reaches the callee.
2. **`NeoInvokeSub`** (`DelegateAdapter.cs:1006-1019`) runs the target on a SEPARATE pooled
   interpreter (`appdomain.RequestILIntepreter()`) whose `frameBase`/`mStack` are unrelated to
   the caller's -- so even if the byref reached the callee, the write-back (the `ref`/`out`
   mutation) couldn't propagate to the caller's frame.

A real fix needs a **frame-to-frame delegate-invoke fast path** that: preserves the byref Ref
Slot (the `(objectIndex, offset)` pair) across the delegate boundary + propagates the `ref`/`out`
write-back to the caller's frame. This is Step-19-sized (mirrors the NeoInvokeSub machinery but
frame-aware).

## The dump-gate (binding -- IS the fast path tractable in one child?)

On HEAD `462e635d`:
1. Confirm the 2 destruction sites (ReadNeoDelegateInvokeArgs `:246-287`; NeoInvokeSub
   `DelegateAdapter.cs:1006-1019`). Cite.
2. The fast-path design: does the delegate-Invoke path have access to BOTH the caller's frame
   + the callee's frame (so it can copy the byref Ref Slot + propagate the write-back)? Or is
   the separate-pooled-interpreter isolation a hard barrier (the byref points into the caller's
   frame, which the callee can't see)?
3. **Scope-aware:** is the fast path SMALL (a byref-aware ReadNeoDelegateInvokeArgs + a
   write-back path) or LARGE (the separate-interpreter isolation fundamentally blocks byref
   propagation, needing a same-frame or a marshaled-byref-handle mechanism)? Ship if tractable;
   sequence if it needs a deep redesign.

## The reference mechanism (mirror)

The byref Ref Slot `(objectIndex, offset)` + its deref/propagation is the SAME mechanism as:
- `CopyNeoCallArguments`'s `PrimitiveByRefSrc` flag (neo-step13-area4 -- the IL->CLR byref direction).
- The delegate-Invoke arg-marshal (Step 19 NeoInvokeSub -- the CLR->IL callback).
F-7 is the byref-typed delegate-param case of the latter. The fix makes the delegate-Invoke arg
marshal byref-aware (preserve the Ref Slot + propagate the write-back), mirroring PrimitiveByRefSrc.

## Authoritative prior context

1. `openspec/changes/archive/2026-07-09-neo-latent-edges/{design.md, ship-log.md}` -- the F-7
  triage (the 2-site destruction proof + the frame-to-frame fast-path recommendation). READ FIRST.
2. `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs:246-319` (ReadNeoDelegateInvokeArgs
   + WriteNeoDelegateInvokeReturn) + `DelegateAdapter.cs:1006-1165` (NeoInvokeSub +
   WriteNeoCallSlot) + the CopyNeoCallArguments PrimitiveByRefSrc pattern.
3. `.trae/documents/neo-deferred-items.md` -- F-7 / NEO-DELEGATE-REFOUT §3 entry.

## Scope (TRUE COMPLETION -- ship if tractable; sequence if deep)

Success criterion: a delegate with a `ref`/`out` param invoked via Neo (the F-7 reproducer:
`ref int`, `out int`, `ref string`) -> the byref marshals correctly + the write-back propagates
(the caller sees the callee's mutation). Adversarial: the write-back is observable (the caller's
variable reflects the callee's write, not the pre-call value).

## Build + test (CRITICAL -- always `-f net8.0`)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep19   # the delegate step (add the byref probe)
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep     # 229/0/0 regression
```
ALWAYS `-f net8.0`. `Debug_Neo` prints huge JIT output -- normal. NeoStep19 8/8 baseline (the existing delegate probes -- the byref probe adds to it).

## Spec authoring traps
- `specs/neo-dispatch/spec.md` (or neo-byref) delta PURE ASCII; SHALL-first. Legacy is the REFERENCE. Neo-only `#if ENABLE_NEO_MODE`. Legacy-neutral.

## Deliverables
`proposal.md`, `design.md` (the fast-path design: byref-aware ReadNeoDelegateInvokeArgs + the
write-back propagation; the scope decision ship-vs-sequence, file:line-cited), `specs/<cap>/spec.md`
(delta), `tasks.md`. Success = the F-7 reproducer (ref int / out int / ref string) PASS.
