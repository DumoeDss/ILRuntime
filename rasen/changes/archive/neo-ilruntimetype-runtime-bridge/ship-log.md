# Ship Log — neo-ilruntimetype-runtime-bridge (Wave-2 child C12)

**Change:** `Delegate.CreateDelegate` / `Enum.ToObject` bridge redirects (ILRuntimeType -> IL delegate adapter / ILEnumTypeInstance) existed on Legacy `RedirectMap` only; under Neo they fell through to the framework which threw `Type must be a runtime Type`.
**Capability:** `neo-dispatch` (ADDED). **Date:** 2026-07-14. **Base HEAD:** `78ecbefb`.

## Root cause (4th "missing Neo redirect" instance)
Hand-written Legacy bridge redirects for Delegate.CreateDelegate + Enum.ToObject were on `RedirectMap` only, never `RedirectMapNeo`. Neo consults `RedirectMapNeo` exclusively (child-2 lineage) -> fell through to reflection fallback / autogen stub -> framework got an ILRuntimeType where a runtime Type is required -> ArgumentException. Same defect class as child-6 (InitializeArrayNeo) + child-22 (CreateInstanceNeo).

## What shipped (Neo-gated -> Legacy-neutral; +252/0)
- `CLRRedirections.cs`: 4 Neo redirects (params in declaration order via Neo cursor, result via WriteNeoObjectResult): `DelegateCreateDelegateNeo` (Type,MethodInfo) :799, `DelegateCreateDelegate2Neo` (Type,object,string) :850, `DelegateCreateDelegate3Neo` (Type,object,MethodInfo) :907, `EnumToObjectNeo` (Type,int) :985 (writes int as underlying-type bytes into ILEnumTypeInstance.Primitives, sign-extended for long-backed enums).
- `AppDomain.cs`: 4 `RegisterCLRMethodRedirectionNeo` in the ctor (:282/307/312/317); first-registered-wins preempts the autogen `ToObject_3_Neo` stub (ctor runs before CLRBindings.Initialize).

## Verification
- **FULL SMOKE: 133 -> 122 (-11).** All 10 C12 tests green (DelegateTest25/28-35 + EnumTest.Test21) + 1 collateral.
- **Stash-toggle airtight:** DelegateTest25/EnumTest21 FAIL pre-fix (the ArgumentException); PASS post-fix.
- **NeoStep 380/0** (no regression). **Legacy-neutral:** DelegateTest25/EnumTest.Test21 PASS under plain Debug+useRegister=true; diff is 100% `#if ENABLE_NEO_MODE`.

## Review
**APPROVE-WITH-FINDINGS** (reviewer != implementer; 0 Blocker/Major). 3 delegate twins byte-match Legacy (params Neo-forward vs Legacy-stack-reverse, the documented adaptation); EnumToObjectNeo faithful for the IL-enum branch (int->underlying-bytes correct: long sign-extended, short/byte truncated) -- its non-IL branches diverge from Legacy but match the Enum.ToObject contract + the prior autogen stub (benign, not a regression). first-registered-wins preemption verified.

## Delivery
local commit + push (portfolio per-child). No PR.

## Durable finding
The "hand-written Legacy redirect missing a RedirectMapNeo twin" defect class now has 4 members (InitializeArray, Activator.CreateInstance, Delegate.CreateDelegate, Enum.ToObject). The fix recipe is mechanical: Neo-signature twin + first-registered-wins registration in the AppDomain ctor. ANY future "ILRuntimeType/IL object rejected by a CLR reflection API under Neo" -> first check whether Legacy has a hand-written bridge redirect missing its Neo twin.
