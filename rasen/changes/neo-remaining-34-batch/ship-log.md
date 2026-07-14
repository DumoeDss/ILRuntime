# neo-remaining-34-batch -- ship log (Wave-2 batch child)

Branch `features/object-model-overhaul`. 2026-07-15. Worker = PLANNER+IMPLEMENTER
(background). NOT committed (LEAD commits).

## Result
- **Full Neo smoke: 34 -> 33** (DelegateTest36 flipped green; zero regressions; the 33 are a
  strict subset of the 34). Ground-truth re-run with the shipped (gated) code.
- **NeoStep smoke: 398/0** (no regression).
- **Legacy-neutral: VERIFIED** -- `dotnet build ILRuntimeTestCLI -c Debug` (plain, ENABLE_NEO_MODE
  off) = 0 errors (both the new method and its registration are `#if ENABLE_NEO_MODE`-gated).

## Batch fix (1 quick-win)
The missing-Neo-redirect class (child-22/6/12). DelegateTest36 hit the broken autogen
`MakeGenericType_4_Neo` stub (calls framework `ILRuntimeType.MakeGenericType` -> NIE "Derived
classes must provide an implementation"). Fix = a hand-written Neo redirect that mirrors the
Legacy `TypeMakeGenericType` (routes through `ToIType` + `MakeGenericInstance`, bypassing the
framework call), registered on `RedirectMapNeo` (first-registered-wins, preempts the stub).

| file | change |
|---|---|
| `ILRuntime/Runtime/Enviorment/CLRRedirections.cs` | +`TypeMakeGenericTypeNeo` (after `ToIType`, `#if ENABLE_NEO_MODE`). |
| `ILRuntime/Runtime/Enviorment/AppDomain.cs` | +`RegisterCLRMethodRedirectionNeo(MakeGenericType, TypeMakeGenericTypeNeo)` (next to Legacy reg, `#if ENABLE_NEO_MODE`). |

Verification: DelegateTest36 PASSES in isolation post-fix; full-smoke delta 34->33 with the
strict-subset property confirmed by set-diff; NeoStep 398/0.

## Why only 1 quick-win this pass
The surviving 33 are increasingly fragmented DISTINCT roots. The clean quick-win categories
(stale-autogen-stub / producer-seeding / missing-Neo-redirect) yielded exactly ONE clean
missing-redirect (TypeMakeGenericType; the only Legacy redirect without a Neo twin that
corresponds to a failing test). The rest are deep:
- Delegate dispatch (Step 19), nested-ldflda-on-byref, IL-type-bridge (no Legacy twin),
  byref out-STRUCT, long-literal/widened-temp call-marshalling, reference-arg aliasing,
  reflection, raw ldfld.i4/stfld.ref collection-struct, hotfix Neo-bridge field-index, plus a
  grab-bag of test-internal assertions (Neo.cs:6862 Throw handler).
Full pinned diagnoses + per-test classification + recommended next-batch priority are in
`rasen/changes/neo-overhaul/handoff/fullsmoke-ground-34-postfix.md`.

## Highest-value next candidates (from the deep-root table)
1. D1 delegate-dispatch arg-marshal (x3 identical, single root).
2. D2 long-literal/widened-temp -- correctness; re-audit whether broad (both long args arrive
   as 0 in an IL-to-IL call; Legacy prints correct values).
3. D4 nested-ldflda-on-byref (x3 incl UnitTest_10051; known deferred item).

## Not committed
Per constraints, the edit set is left uncommitted for LEAD. `git status` will show the two
modified ILRuntime source files + the artifacts.
