# Proposal: neo-recluster-60 (Wave-2 child of neo-overhaul)

## Why
The STALE 189-failure grounding (`fullsmoke-ground-2026-07-13.md`) is obsolete --
25+ wave-2 children drove the full Neo smoke from 189 to **60** failures. This
child RE-RUNS the full smoke fresh, re-clusters the current 60 by real exception
+ top Neo.cs frame, and fixes the LARGEST single-root sub-cluster.

## What changed
- FRESH grounding artifact: `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-60.md`
  (the current 60 failures clustered by exception/frame; the cluster table is a
  durable artifact for the next children).
- Fix the LARGEST single-root cluster: **`unbox.any T` for a reference-type T**
  (6 tests -- cluster B). The Neo `Unbox`/`Unbox_Any` arm
  (`ILIntepreter.Neo.cs:5441`) only handled value-type / enum / primitive unboxing
  and threw `InvalidCastException` (IL-class/interface T) / `NullReferenceException`
  (null source). Legacy (`ILIntepreter.Register.cs:4147-4150`) handles a
  reference-type T as a plain reference copy and treats null as a no-op. Added a
  reference-type branch at the top of the Neo arm (`!t.IsValueType` discriminator,
  mirroring Legacy's `t.IsValueType` boundary) that writes the source reference to
  the dest ref slot or `-1` for null.
- Permanent regression probe: `TestCases/NeoStepUnboxAnyRefTypeTest.cs` (3 TCs:
  ref-copy identity, null source, interface-T cast).

## Impact
- Full Neo smoke: 60 -> N (recorded in ship-log; truth = full-smoke number).
- NeoStep: 0 regressions (397/0 with the 3 new probes).
- Legacy-neutral by construction (the change is inside the `#if ENABLE_NEO_MODE`-
  gated `ILIntepreter.Neo.cs`; Legacy `ExecuteR` Unbox_Any arm is untouched).

## Out of scope (reported, not fixed)
The other surviving clusters (cluster A throw-handler grab-bag of 13 distinct
assertions, cluster C interface dispatch, cluster D autogen bindings, cluster E
ldfld/stfld, cluster F byref-to-slot, cluster G ldind/stobj, cluster H Hotfix
Legacy path, cluster I delegate misc). RefOutTest.UnitTest_RefOutNull2 PROGRESSED
past the unbox.any but now hits `stobj T` (cluster G) -- a separate follow-up.
