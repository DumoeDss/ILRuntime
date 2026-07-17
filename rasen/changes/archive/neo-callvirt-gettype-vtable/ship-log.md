# Ship Log — neo-callvirt-gettype-vtable (Wave-2 child C4)

**Change:** two Neo callvirt defects -- (4a) IL instance calling inherited non-virtual CLR method `GetType()`
hit "cannot resolve VTable slot"; (4b) a stale Step-7 `.cctor` suppression left every IL static field with an
inline initializer null -> "Neo callvirt this is null".
**Capability:** `neo-dispatch` (4a) + `neo-optimizer` (4b .cctor trigger).
**Pipeline:** small-feature (grounding -> re-audit -> propose+apply -> verify -> review -> fix MAJOR -> ship).
**Date:** 2026-07-14. **Branch:** `features/object-model-overhaul`. **Base HEAD:** `68038a6e`.

## Root causes (PINNED via Neo-vs-Legacy comparison + real run)
**4a — "cannot resolve VTable slot for System.Type GetType()":** `Object.GetType()` is non-virtual, so
`IsNeoVTableCandidate` (ILType.cs:703) rejects it -> never in the Neo VTable. `ResolveNeoCallvirtILTarget` then
THREW on the slot lookup. Legacy handles it via the `ObjectGetType` redirect on Legacy `RedirectMap` (returns
`Type.ReflectionType` for an ILTypeInstance) -- that redirect was NOT on `RedirectMapNeo`, and Neo threw instead
of falling back to CLR dispatch.

**4b — "Neo callvirt this is null" (static-field sub-class):** `ILType` had a STALE Step-7 `#if ENABLE_NEO_MODE`
suppression of the `.cctor` invocation ("restore once Step 7 lands"). Step 7 landed long ago. So every IL static
field with an inline initializer (`= new Dictionary(...)`) stayed null -> callvirt on it gave "this is null".
**This was the highest-value stale-TODO in the tree** -- it silently nullified far more than the C4-4b surface
(also fixed GenericMethodTest18/UnitTest_Test1/UnitTest_10030 + incidental C16 NREs).

## What shipped (Neo-gated; Legacy-neutral)
- **Fix 4a** -- `ILIntepreter.Neo.cs` `ResolveNeoCallvirtILTarget` (~:1210): when VTable lookup fails AND
  `declaredMethod is CLRMethod`, `return declaredMethod` (fall back to CLR dispatch) instead of throwing. Virtual
  Object methods (ToString/Equals/GetHashCode) stay on the VTable path (they ARE candidates). + new `ObjectGetTypeNeo`
  redirect (`CLRRedirections.cs:~764`, mirrors Legacy) + register on `RedirectMapNeo` (`AppDomain.cs:~288`).
- **Fix 4b** -- `ILType.cs`: removed the stale Step-7 suppression at the lazy `StaticInstance` getter (~:214-230) so
  `.cctor` now fires under Neo; the eager invocation in `InitializeMethods` (~:2424) is wrapped `#if !ENABLE_NEO_MODE`
  (defers to the lazy getter, which fires after init completes -- the eager site re-entered init before storage was
  materialized -> NRE in the Stsfld VT arm).
- **MAJOR-1 fix (reviewer-flagged, pre-ship)** -- the .cctor lift double-invoked on the NeoAOT path: added
  `StaticConstructorCalledForNeoAOT` get/set accessor (`ILType.cs:348`, `staticConstructorCalled` is private) + guarded
  `LoadNeoAssembly`'s explicit seed (`AppDomain.cs:888-893`: `_ = t.StaticInstance; if (!Called) { set; Invoke }`,
  set-before-invoke to mirror the getter's re-entrancy guard). `.cctor` now fires EXACTLY ONCE on both Cecil + NeoAOT paths.
- 2 stale Step-25 comments cleaned (`ILType.cs:201-210`, `:1507-1521`) + the seed-block comment in AppDomain.cs.

## Verification (truth = full-smoke number)
- **FULL SMOKE: 153 -> 140 (−13, 0 regressions).** 4a fully fixed (7): DelegateTest38/39/40, EnumTest15,
  ReflectionTest03, ReflectionTest.TestMethodParametersInfo, StaticTest01. 4b fully fixed (4): SimpleTest.StaticTest,
  GenericMethodTest18, MyTest.UnitTest_Test1, UnitTest_10030 + 2 incidental C16 NREs. Several PROGRESSED (not
  regressed) to other-cluster errors: DelegateTest36/37 + ReflectionTest10 -> C12; ReflectionTest11 -> Add_I8;
  EnumTest30/32 -> enum Equals.
- **Stash-toggle airtight** (4a): stash engine files -> StaticTest01 FAILS "cannot resolve VTable slot GetType"; pop -> PASS.
- **NeoStep 380/0** (no regression); all 140 post-smoke failures are in the 189-grounding set.
- **Legacy-neutral:** StaticTest 10/0, ReflectionTest 31/0 under plain Debug+useRegister=true; ILType.cs changes are
  Legacy-neutral by construction (Legacy always ran .cctor at both sites).
- MAJOR-1 is AOT-path-only (Debug_Neo/Cecil never calls LoadNeoAssembly); NeoStep 380/0 + C4 spot tests (StaticTest01,
  SimpleTest.StaticTest) confirm the guard did NOT regress the Cecil-path fix. Full smoke not re-run for the guard
  (correct -- it's unexercised by the Cecil load).

## Review
**APPROVE-WITH-FINDINGS** (reviewer != implementer; 0 Blocker; MAJOR-1 fixed pre-ship by an independent fixer worker,
LEAD diff-reviewed the guard). 4a-no-overfire CONFIRMED (fallback gated on CLRMethod; virtual Object methods stay on
VTable; ILMethod miss still throws). 4b-cctor-change SAFE for the Cecil path (lazy fires before field access; beforefieldinit-correct).
- MINOR-1/2 (stale comments) cleaned. TRIVIAL path-typo in review brief.

## Delivery
**Mode:** local commit + push (portfolio per-child). No PR.

## Durable findings (for future planning)
1. **The Step-7 `.cctor` suppression was the highest-value stale-TODO in the tree** -- it nullified every IL static
   field with an inline initializer under Neo. Any "static field reads null / `this` is null on a static field" report:
   first confirm the .cctor runs.
2. **`IsNeoVTableCandidate` rejects non-virtual CLR methods** (GetType, MemberwiseClone). Neo callvirt must fall back
   to CLR dispatch (not throw) for inherited non-virtual CLR methods on IL instances. A Neo redirect is needed for any
   such method requiring IL-aware behavior (GetType done; Equals/ToString for IL-enum receivers is a follow-up -- IL
   enums have empty VTables via the value-type skip).
3. **The .cctor single-invoke invariant has exactly two Neo fire sites** (lazy `StaticInstance` getter + `LoadNeoAssembly`
   explicit seed); once the getter became the trigger for both Cecil + Cecil-free types, the explicit seed MUST be guarded.
