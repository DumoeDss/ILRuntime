# Proposal: neo-recluster-16

## Why
Wave-2 child of `neo-overhaul`. The full Neo smoke is at 16 failures (down from
189 via 47 wave-2 children). This child re-grounds the CURRENT 16 fresh, then
batch-fixes the most tractable singletons. Truth = the full-smoke number
(pre 16 -> post N).

## What changed
1. **Fresh grounding + re-cluster**: ran the full smoke (no filter) on the
   current HEAD, extracted + classified the 16 failures, wrote the cluster
   table to `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-16.md`.
2. **1 fix shipped**: `GenericMethodTest11` -- constrained-callvirt box-once arm
   grabbed the ILTypeInstance (a primitive field's OWNER) instead of reading
   the primitive field VALUE from `Primitives` for a `constrained <primitive>`
   callvirt on `ldflda <primField>` of an IL instance. See design.md.
3. **RegisterVMTest04 pin CORRECTED**: ground-17's "generic-instance
   field-layout -> ManagedObjects 0 ref slots" pin is DISPROVEN (the layout is
   correct: TRC=1); the real root is a >3-arg virtual-IL-call param-marshalling
   bug (documented, not fixed -- deep).

## Impact
- Full Neo smoke: **16 -> 15** (GenericMethodTest11 removed; strict subset).
- NeoStep: 404/0 (no regression).
- Legacy-neutral: the fix is inside the `#if ENABLE_NEO_MODE` file-gated
  `ILIntepreter.Neo.cs` -> structural.

## Non-goals (honestly deferred, all DEEP)
The other 15 survivors are distinct deep roots (call/param marshalling,
inlined-call arg aliasing, IL-struct boxing, struct-newobj dest, reflection
null arrays, constrained-callvirt property on generic struct, enumerator
dispatch). Each needs its own focused child. Prioritized in fullsmoke-ground-16.md.
