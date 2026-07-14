# Ship log: neo-recluster-60

Shipped (not committed -- LEAD commits). Branch `features/object-model-overhaul`.

## Files changed
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (+22/-0): reference-
  type branch at the top of the Unbox/Unbox_Any arm (after `t = AppDomain.GetType`).
- `TestCases/NeoStepUnboxAnyRefTypeTest.cs` (NEW): 3 regression probes
  (`NeoStepUnboxAnyRef_TC1/TC2/TC3`).

## Artifacts written
- `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-60.md` -- the FRESH 60-failure
  grounding + cluster table (durable artifact for subsequent children).
- `rasen/changes/neo-recluster-60/{proposal,design,ship-log}.md`.

## Verification matrix
| Check | Result |
|---|---|
| Fresh full smoke (pre-fix) | 928 ran / 60 failed (exit 0) |
| Stash-toggle probes (HEAD) | 3/3 FAULT (InvCast 5484 / NRE 5447) |
| Probes after fix | 3/3 PASS |
| Name-filter cluster B | 5/6 flip green; RefOutNull2 progresses to stobj |
| NeoStep smoke | 397 ran / 0 failed (no regression) |
| **FULL SMOKE (post-fix)** | **931 ran / 55 failed (exit 0); 0 new failures** |
| Legacy (plain Debug+useRegister=true) | all 6 cluster-B tests PASS |
| Legacy-neutral | structural (file-gated ENABLE_NEO_MODE) + empirical |

## Full-smoke delta
**60 -> 55 (-5 flipped, 0 regressions).** Flipped green:
GenericMethodTest9, GenericMethodTest15, GenericStaticMethodTest19,
InheritanceTest06, InheritanceTest18. (RefOutNull2 progressed past unbox.any to
`stobj T` -- cluster G, separate follow-up.)

The delta (-5) is well above the known ±1 crash-order noise and the run exited 0
(graceful, full failure list + summary emitted), so no second run was needed.

## Capability
`neo-type-checks` (the Unbox/Unbox_Any / castclass / isinst type-conversion surface;
sibling of the enum-reflection overrides). No spec delta written (rasen
proposal/design artifacts suffice for this Wave-2 child; the durable record is the
grounding doc + this ship-log).

## Remaining clusters (from fullsmoke-ground-60.md, for the next children)
- A: 13 `throw`-handler assertions (grab-bag, NOT one root -- each its own bug).
- C: 4 interface callvirt (ResolveNeoCallvirtInterfaceTarget slot-0 / CLR-object).
- D: 8 autogen-binding invoke (callvirt.clr -> *_Neo stub; generic-method bindings).
- E: 9 raw ldfld/stfld (incl. 3 "CLR object via IL-instance path, Owner Int32" NIE).
- F: 2 NeoMarshalByrefFieldToSlot (Dict.TryGetValue out param).
- G: 6 ldind/stobj/ldelema/ldlen (incl. the RefOutNull2 stobj T residual).
- H: 6 Hotfix patched-IL via Legacy Execute(StackObject*) -- AUDIT FIRST (Legacy-
  path; may be pre-existing Legacy bugs, not Neo).
- I: 3 delegate/generic callvirt misc.
- J: 1 DelegateTest19 (List.get_Item via binding).
