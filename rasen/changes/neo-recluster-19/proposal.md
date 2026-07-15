# Proposal: neo-recluster-19

## Mandate
Wave-2 child of `neo-overhaul` (branch `features/object-model-overhaul`). The full
Neo smoke was at 19 failures. This child: FRESH-ground the current 19, re-cluster,
and batch-fix the most tractable singletons. Success = full-smoke count dropping
(19 -> lower), verified by re-running the full smoke.

## Outcome
- FRESH 19 cluster table written to
  `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-19.md`.
- 2 tractable singletons batch-fixed (DelegateTest19 + Test05.TestForEach).
- **Full smoke 19 -> 17** (verified by re-running the full smoke). NeoStep
  **403/0** (no regression). Legacy build clean (Legacy-neutral).

## Why these two (the most tractable of the 19)
The 19 are deeply fragmented (each a DISTINCT root). The two shipped are the only
clean, single-site, low-risk fixes found on fresh audit:
1. **DelegateTest19** -- a CLR-enum RETURN is mis-sized as a boxed reference
   (RefCount=1) by `AllocateSlotForType`, while the JIT emits enum locals/returns
   as a flat int (RefCount=0 behavior). The mismatch routes the Ret into the
   vt-with-ref-fields branch -> OOB. A CLR enum must be sized as its underlying
   primitive (matching IL enums + the JIT emission). Single JIT branch, ~13
   lines, logically self-consistent.
2. **TestForEach** -- the Neo bottom-of-method re-throw ALWAYS re-wraps the
   pending exception in a fresh `ILRuntimeException`, double-wrapping one that is
   already wrapped (the foreach-finally re-throw path). The host's
   ExpectException check then mismatches. Single-line guard mirroring
   HandleException's own finally-branch guard.

The other 17 are deep singletons (JIT optimizer aliasing, generic-instance field
layout, ILRuntimeType-vs-framework reflection, constrained dispatch, IL-struct
boxing at call boundaries) -- reported in the grounding doc, NOT force-fit.

## Scope (files)
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` (AllocateSlotForType
  CLR-enum branch; Neo-gated `#if ENABLE_NEO_MODE`).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (Ret-unhandled
  re-throw no-double-wrap; file-gated `#if ENABLE_NEO_MODE`).

No new TestCases probes (both fixes are verified by the EXISTING failing tests
flipping to pass + the full-smoke delta + NeoStep 403/0). Capability home =
`neo-optimizer` (AllocateSlotForType) + `neo-exceptions` (Ret re-throw).
