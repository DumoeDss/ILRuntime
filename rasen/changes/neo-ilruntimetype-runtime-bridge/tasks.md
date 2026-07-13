# Tasks: neo-ilruntimetype-runtime-bridge (Wave-2 C12)

## Phase 1 -- RE-AUDIT (DONE)
- [x] Build CLI (Debug_Neo) + TestCases (Debug) -- 0 errors.
- [x] Confirm DelegateTest25 + EnumTest21 FAIL on Neo (ArgumentException, runtime-Type msg).
      DelegateTest25 stack: CLRMethod.Invoke:583 -> Neo.cs:1256 (reflection fallback).
- [x] Confirm DelegateTest25/28/30 + EnumTest21 PASS on Legacy (plain Debug+useRegister=true).
- [x] Pin root: hand-written Legacy redirects (DelegateCreateDelegate/2/3 + EnumToObject)
      bridge ILRuntimeType; registered on RedirectMap ONLY, not RedirectMapNeo -> Neo falls
      through to reflection fallback / autogen stub passing ILRuntimeType raw.
- [x] Verdict: ONE bridge missing (RedirectMapNeo twin), two API surfaces.

## Phase 2 -- implement + verify (DONE)
- [x] Add `DelegateCreateDelegateNeo` (Type, MethodInfo) to CLRRedirections.cs.
- [x] Add `DelegateCreateDelegate2Neo` (Type, object, string).
- [x] Add `DelegateCreateDelegate3Neo` (Type, object, MethodInfo).
- [x] Add `EnumToObjectNeo` (Type, int) -- writes int as underlying-type bytes to
      ILEnumTypeInstance.Primitives.
- [x] Register all 4 on RedirectMapNeo in AppDomain ctor (first-registered-wins preempts
      autogen ToObject_3_Neo + any Delegate.CreateDelegate stub).
- [x] Build clean (0 errors, Debug_Neo).
- [x] Name-filter: DelegateTest25/28/29/30/31/32/33/34/35 + EnumTest21 all PASS (0 failed).
- [x] NeoStep 380/0 (no regression).
- [ ] FULL SMOKE: record delta 133 -> N (running; result in final report).
- [ ] Legacy-neutral: DelegateTest25/EnumTest21 PASS under plain Debug+useRegister=true.

## Files touched
- `ILRuntime/Runtime/Enviorment/CLRRedirections.cs` (+~210, 4 new Neo redirects under
  the existing `#if ENABLE_NEO_MODE` block).
- `ILRuntime/Runtime/Enviorment/AppDomain.cs` (+~20, 4 RegisterCLRMethodRedirectionNeo
  calls under `#if ENABLE_NEO_MODE`).
