# Ship Log — neo-step25-s2-generic-at-load

> Step 25 **S2** follow-up. Shipped 2026-07-07. Capability: `neo-optimizer`
> (the Neo AOT toolchain). Parent portfolio: `neo-completion-portfolio`.

## What shipped

S2 makes the Neo AOT `.neo` loader **consume `model.Templates`** so a generic
method call on an AOT-loaded type runs Step-22 `CloneAndPatch` from the AOT
template instead of falling back to per-occurrence JIT. Init-only + additive
(scope = the **no-T-identity-token** slice; T-identity-token / try-catch /
cross-AppDomain / full-ILType-decoupling deferred to **S3**).

The load-bearing fact: the Step-22 hook is ALREADY in place
(`ILMethod.InitCodeBody` routes a generic instance through
`GenericMethodTemplateOps.TryInstantiate` when the definition's
`GenericMethodTemplateCache != null`). S2 only populates that cache; the
execution path is UNCHANGED. Mirrors S1's "init-only, not an ExecuteNeo change"
hinge.

## P1-vs-P2 decision (resolved: P1)

A parameterless non-generic wrapper that internally calls a generic method is
sufficient — the generic args are baked into the wrapper's `Call` token at JIT-
compile time and never cross the `ILIntepreter.Run` boundary (the Step-6 entry
shim is parameterless-only). P2 (a parametrized probe entry) is NOT needed for
S2. (A parametrized Run entry remains an S3 concern for full ILType decoupling
where the ENTRY itself may need marshaled args.)

## Files changed

Engine (all `#if ENABLE_NEO_MODE`, Neo-only; Legacy byte-identical):
- `ILRuntime/CLR/Method/ILMethod.cs` — `InitTemplateFromNeo(GenericMethodTemplate)`
  setter that OVERWRITES the cache (no `if (!= null) return` guard; the existing
  guarded `StoreGenericTemplate` is unchanged).
- `ILRuntime/Runtime/Intepreter/RegisterVM/GenericMethodTemplate.cs` —
  `BuildFromNeoRecord` (reconstruct the template from a `.neo` record: re-resolve
  `VariableTypes` from `VariableTypeRefIdxs`; rebuild `SwitchTargets`; rebuild
  `Patches` Cecil-free with `RebuildPatchesNoCecil`, rejecting→null→skip any
  T-identity/Method/Type-token patch) + `CompileViaAotTemplateNeoBody` (the
  DEBUG host-side structural-equivalence hook).
- `ILRuntime/Runtime/NeoAOT/NeoAssemblyLoader.cs` — second loop over
  `model.Templates`; `MatchGenericDefinition` + `ResolveVariableType`; build +
  bind via `InitTemplateFromNeo`; skip+report on miss/reject (additive contract).

Tests:
- `TestCases/NeoStep25LoadProbe.cs` — `Echo<T>`, `ConstGeneric<T>` (carries the
  observable `Ldc_I4 1234567` for the mutation cell), + parameterless wrappers
  `WrapEchoInt/Long/Ref/Struct`.
- `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep25LoadExecCheck.cs` — the 4
  adversarial capstone cells.

Docs:
- `.trae/documents/neo-deferred-items.md` — STEP-25-PARTIAL S2 SHIPPED + 2 new
  follow-ups (F-11, F-12).
- `.trae/documents/neo-handoff.md` — HEAD + S2 status line.

## Verification (independent non-author re-run)

| Filter | Result |
|---|---|
| `NeoStep25LoadExec` (capstone) | 11 -> **21/21**, 0 failed; 13 attached, 0 skipped |
| `NeoStep` (Debug_Neo) | 210 -> **215/215**, 0 failed |
| `NeoStep22SelfCheck` | **55/55** (unchanged) |
| `NeoStep23Roundtrip` | **15/15** (unchanged) |
| `NeoStep24CliRoundtrip` | **5/5** (unchanged) |
| `NeoStep` (plain `Debug`, Legacy) | 215, 8 fail — **all pre-existing**
  (NeoStep6/13/14/15/16); **0** from the new `NeoStep25LoadProbe` methods |

## The 4 adversarial capstone cells (all PASS — no partial-ship)

1. **Template body-mutation (LOAD-BEARING):** mutate `Ldc_I4` 1234567->7654321
   in the deserialized `.neo` TemplateBody pre-attach; observe **7654321**.
   **Stash-toggle proof:** stashing the single load-bearing line
   (`def.InitTemplateFromNeo(tpl)`) flips this cell PASS->FAIL
   (7654321->1234567 = the JIT-template value) while every other cell stays
   green — so the cell genuinely distinguishes AOT-template from JIT-template,
   NOT reading a JIT path by accident.
2. **Fresh-instance:** `WrapEchoLong` (`Echo<long>`, never instantiated
   pre-attach) routes through CloneAndPatch via the AOT template.
3. **Structural-equivalence (DEBUG host-side):** `CompileViaAotTemplateNeoBody`
   + `BodiesEqual` == per-occurrence JIT body for int/long/string/struct T.
4. **Functional:** int/long/struct/ref T wrappers each equal the known-expected
   value.

## Dump-gate outcomes (OQ1/OQ2/OQ3 — resolved from the dump)

- **OQ1 (VariableTypes recovery):** `BuildInitObjPrefix` reads
  `VariableTypes[v]` unconditionally for `v in [0,varCnt)` -> full non-null
  array required; `BuildFromNeoRecord` rebuilds it from `VariableTypeRefIdxs`,
  any miss -> `return null` (skip).
- **OQ2 (observable mutation target):** `ConstGeneric<T>` TemplateBody =
  `[Ldc_I4=1234567, Br_S, Ret]`; the `Ldc_I4` is NOT optimizer-folded.
- **OQ3 (T-identity rejection):** `RebuildPatchesNoCecil` flags any
  `TypeToken`/`MethodToken` patch with non-none `CecilTokenKind` -> `return
  null`. Probe patches are `[]` (no-T-identity-token slice); rejection path
  implemented but not exercised (see M1).

## Review verdict: APPROVE-WITH-FINDINGS (0 Blocker/Major)

Ship-ready. Accepted-known + S3-routed (recorded in `auto-run.json`
`acceptedKnownFindings`):
- **M1 (Minor):** the T-identity/Method/Type-token rejection path is correct-by-
  inspection but UNTESTED (probe `Patches=[]`). Recommend a synthetic-record
  cell in **S3**.
- **T1 (Trivial):** redundant `&& CecilTokenKind != 2` clause in the rejection
  check (a malformed TypeToken+none record would slip through and silently skip
  at DoCloneAndPatch). Simplify to `Kind==TypeToken||MethodToken` (S3 cleanup).
- **T2 (Trivial):** only the mutation cell proves AOT-ran (structural-equiv is
  JIT-vs-JIT when S2 is stashed). Doc-only.
- **T3 (Trivial):** `MatchGenericDefinition` doesn't disambiguate by full
  signature (collision->skip; acceptable for the S2 matrix).

## New follow-ups surfaced (S3-routed)

- **F-11 / NEO-AOT-GENERIC-EAGER-COMPILE:** a generic instance force-compiled
  before the loader binds the AOT template keeps a stale (but CORRECT) JIT
  `bodyRegister` — missed optimization, NOT corruption (`DoCloneAndPatch` copies
  into a new array; the setter replaces the definition field, not the live
  template). The capstone works around it via a fresh `MakeGenericMethod`
  instance. **S3 action item** (load-order / per-instance refresh).
- **F-12 / NEO-RUN-REF-RETURN:** `ILIntepreter.Run`'s `NeoBoxReturnValue`
  handles primitive returns only; a reference-type return reads raw bytes.
  Pre-existing (NOT introduced by S2); folded into the S3 parametrized-Run
  prerequisite.

## Deferrals (honestly tagged, NOT promoted to "met")

T-identity-token re-resolution, generic methods with try/catch (Cecil `Addr`),
cross-AppDomain (Cecil-free AppDomain), full `ILType` decoupling + Approach-1
token-hash re-resolution + `.cctor` seeding + full CLR aqname/host registration.
All remain **S3** (`neo-step25-s3-full-decoupling`).

## Legacy impact

None. Every engine edit is `#if ENABLE_NEO_MODE` (NeoAssemblyLoader.cs whole-
file; GenericMethodTemplate.cs Neo region; ILMethod.cs setter). Plain-`Debug`
build = 0 errors. Legacy NeoStep run = same pre-existing failure set with/without
the change; the new probe methods add 0 Legacy failures.
