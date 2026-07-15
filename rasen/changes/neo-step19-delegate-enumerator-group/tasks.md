# Tasks: neo-step19-delegate-enumerator-group

## Phase 1 -- RE-AUDIT (DONE)
- [x] Build CLI (Debug_Neo --no-incremental, UseSharedCompilation=false) + TestCases (Debug). 0 errors.
- [x] Run each of the 4 by name filter under Neo; capture EXACT message + stack.
  - DelegateTest42: `throw` @DelegateTest.cs:662, locals v2=F v3=F v4=T v5=F.
  - UnitTest_10046: `throw` @TestValueTypeBinding.cs:470 (a.X!=2 in delegate body).
  - UnitTest_10051: `throw` @TestValueTypeBinding.cs:619 (list[0].V2.x.RawValue!=999).
  - MyTest.Test: InvalidCast String->IEnumerator @get_Current_0_Neo:48 <- Neo.cs:1331.
- [x] Confirm all 4 PASS on Legacy (plain Debug + useRegister=true). 0 failed each.
- [x] Shared-root verdict: DISTINCT (table in design.md). DelegateTest42 most tractable.

## Phase 2 -- implement + verify (DONE)
- [x] `CLRRedirections.cs`: add `DelegateGetTargetNeo` (mirrors Legacy DelegateGetTarget;
      IDelegateAdapter -> .Instance else ((Delegate).Target; WriteNeoObjectResult null-aware).
- [x] `AppDomain.cs`: `RegisterCLRMethodRedirectionNeo(mi, DelegateGetTargetNeo)` under
      `#if ENABLE_NEO_MODE` after the Legacy get_Target registration.
- [x] Rebuild CLI Debug_Neo. 0 errors.
- [x] Name-filter verify: DelegateTest42 PASS (0 failed). Other 3 unchanged (distinct roots).
- [x] Stash-toggle airtight: stash the 2 engine files -> rebuild -> DelegateTest42 FAILS (1) ->
      pop -> rebuild -> DelegateTest42 PASSES (0).
- [x] NeoStep smoke: 410/0 (no regression).
- [x] FULL smoke: **13 -> 12** (DelegateTest42 flipped; the other 12 == ground-13 set minus
      DelegateTest42; no new failures, no unexpected flips). EXIT 127 (known graceful Dict-NRE).
- [x] Legacy-neutral: plain Debug build, 0 errors.

## Files (NOT committed -- LEAD commits)
- `ILRuntime/Runtime/Enviorment/CLRRedirections.cs` (+29: DelegateGetTargetNeo).
- `ILRuntime/Runtime/Enviorment/AppDomain.cs` (+5: RedirectMapNeo registration, #if-gated).
