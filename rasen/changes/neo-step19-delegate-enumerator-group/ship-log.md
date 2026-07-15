# Ship Log: neo-step19-delegate-enumerator-group

Date: 2026-07-16. Wave-2 child of neo-overhaul. BACKGROUND planner+implementer.

## Outcome
- Re-audited the 4 ground-13 tests flagged as "possibly shared Step-19 root".
- **Verdict: 4 DISTINCT roots (no shared multi-flip).** Ground-13's hypothesis disproven.
- Fixed the 1 tractable root (DelegateTest42). Reported the other 3 as distinct deep roots.

## The fix (DelegateTest42)
`System.Delegate.get_Target` had a Legacy redirect but NO Neo twin -> the JIT emitted a raw
`callvirt.clr get_Target` and the reflection fallback returned the wrong object for an
IDelegateAdapter-held IL delegate. Added `DelegateGetTargetNeo` (mirrors Legacy, returns
`((IDelegateAdapter)dele).Instance` for adapters) + registered on RedirectMapNeo. Classic
child-2/6/22 "missing Neo redirect twin" defect class. ~34 added lines, Neo-gated.

## Verify (truth = full-smoke number)
- Name-filter: DelegateTest42 PASS (was FAIL). Other 3 unchanged (distinct roots).
- Stash-toggle airtight: stash 2 engine files -> rebuild -> DelegateTest42 FAILS (1) ->
  pop -> rebuild -> PASSES (0).
- NeoStep: **410/0** (no regression).
- **FULL SMOKE: 13 -> 12** (DelegateTest42 flipped). The 12 == ground-13 set minus
  DelegateTest42 (no new failures, no unexpected flips). EXIT 127 (known graceful Dict-NRE).
- Legacy-neutral: plain Debug build 0 errors; all changes `#if ENABLE_NEO_MODE`-gated.

## NOT committed (LEAD commits)
Files: `ILRuntime/Runtime/Enviorment/CLRRedirections.cs` (+29),
`ILRuntime/Runtime/Enviorment/AppDomain.cs` (+5).

## Distinct deep roots remaining (future children)
- UnitTest_10046: delegate-INVOKE struct-arg marshalling (CLR delegate wrapping IL static
  method, by-value struct arg). Step-19 invoke path. Needs JIT-dump triage.
- UnitTest_10051: constrained-callvirt PROPERTY read on a NESTED struct field
  (`.x.RawValue`); F-10 child already pinned this as separate from F-10/delegate.
- MyTest.Test: enumerator-loop `this`-register aliasing (corrupted to a String at
  `get_Current_0_Neo:48`); a register/liveness bug, not delegate dispatch.

## Surfaced follow-up (latent, not in the 13)
`Delegate.op_Equality` / `op_Inequality` also have no Neo twin (AppDomain.cs:235-242).
No current failing test exercises them; add Neo twins identically if one surfaces.
