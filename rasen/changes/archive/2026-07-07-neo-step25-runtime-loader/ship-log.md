# Ship Log — neo-step25-runtime-loader (SCOPE S1)

> Change: `neo-step25-runtime-loader` (portfolio child of `neo-completion-portfolio`)
> Capability: `neo-optimizer`
> Branch: `features/object-model-overhaul`
> Date: 2026-07-07
> Pipeline: small-feature, Tier A, full autonomy.
> Status: **PARTIAL SHIP (S1)** -- non-generic load+execute V2-PROVEN; S2/S3 deferred.

## Verdict

**SHIPPED (S1) — clean.** Review verdict **APPROVE** (0 Blocker, 0 Major); review-loop
round 1 adopted the reviewer's top recommendation (the permanent body-mutation guard)
+ a typed-catch cell + 2 fail-loud Minors. TEST-ONLY + defensive; gates green; no real
S1 bug. LEAD non-author confirmation (gate-run). The first end-to-end "deserialize +
ExecuteNeo == JIT" proof (the AOT capstone).

## KEY discovery (the design hinge)

**Cecil-coupling is ZERO at execution.** `ExecuteNeo` reads ONLY
`method.CompiledFrame.NeoExecuteBody` + frame metadata + resolves token operands via
the AppDomain hash maps -- NO Cecil at runtime. So Step 25 does NOT touch `ExecuteNeo`;
it is purely an INIT concern (populate `CompiledFrame` from the `.neo`, bypass
`InitCodeBody`/JIT). This made S1 tractable.

## Delivered scope (S1 -- non-generic load + execute, same-AppDomain)

ADDITIVE (the JIT/Cecil path stays byte-identical; the AOT path is a Neo-gated flag):

- **ILMethod AOT-init dual-path** (`ILRuntime/CLR/Method/ILMethod.cs`, SHARED, all
  `#if ENABLE_NEO_MODE`): a Neo-only `isNeoAotBody` flag (default false) +
  `InitCodeBodyFromNeo(NeoMethodDefRecord, resolveCatchType)` that populates
  `compiledFrame` field-by-field from the Step-23 record (NeoExecuteBody OpCodeR[],
  LocalInfos/ParamInfos/StackSlotInfo, sizes, ParameterCount; rebuilds `SwitchTargets`,
  `NeoCallParams` (resolving `PrimitiveByRefElemType` CLR `System.Type[]` from aqnames),
  + the runtime `Method.ExceptionHandler[]` from `NeoExceptionHandlerRecord[]`
  body-indices, applying the exclusive->inclusive `-1` to match the runtime's
  `addr <= TryEnd` convention -- mirrors `InitCodeBody`'s Cecil conversion). The
  `BodyRegister` getter short-circuits on the flag. Cecil/JIT path byte-identical.
- **`NeoAssemblyLoader.Attach`** (`ILRuntime/Runtime/NeoAOT/NeoAssemblyLoader.cs`,
  NEW, Neo-only): same-AppDomain, non-generic bind. Per `NeoMethodDefRecord`:
  resolve `MethodRefIdx` -> `MethodReferencePatchInfo` (DeclaringType is inline) ->
  `LoadedTypes[fullName]` -> match by name+paramcount -> `InitCodeBodyFromNeo`. Misses
  skipped (method keeps JIT -- additive contract). SAME-AppDomain, so token operands
  resolve via the EXISTING live hash maps (no cross-AppDomain work in S1).
- **`NeoStep25LoadExecCheck.cs`** (V2 host-side self-check, 11 cells post-round-1) +
  `TestCases/NeoStep25LoadProbe.cs` (dedicated non-generic probe: arithmetic + try/catch
  + mixed locals/byref-call + ConstProbe + TypedCatchProbe) + the `NeoStep25LoadExec`
  CLI hook.

## Verification evidence (the V2 capstone + the load-bearing guards)

### V2 functional (deserialize + ExecuteNeo == JIT): 11/11 cells, 7 attached
`NeoStep25LoadExecCheck.Run`: compile the probe via Step 24's `NeoCompiler` (in-memory
stream, same AppDomain) -> `.neo` -> `NeoAssemblyReader.Read` -> run via JIT (capture A,
before Attach) -> `Attach` -> run via AOT (capture B) -> assert A == B == known-expected.
Matrix (non-generic): ArithProbe (47), TryCatchProbe (100, EH rebuild), MixedLocalsProbe
(61, NeoCallParamMap rebuild + byref-call), TypedCatchProbe (200, typed EH dispatch),
ConstProbe (body-mutation).

### The body-mutation guard (the load-bearing proof, permanent post-round-1)
`ConstProbe`: deserialize the `.neo`, MUTATE the deserialized `Ldc_I4` constant
1234567 -> 7654321 BEFORE Attach, run post-Attach, assert the result == 7654321 (NOT the
JIT value 1234567). PROVES ExecuteNeo ran the genuine deserialized AOT body (rules out
the "JIT body still runs -> A==B trivially" false-pass -- Step 23 made the bodies byte-
identical, so the flag-assertion + a green JIT==AOT alone are insufficient). The
reviewer proved this ad-hoc (Probe A); round 1 made it permanent.

### Regression gates (all green; S1 is additive)
- NeoStep smoke: **210/210** (was 205 + the new probe methods).
- NeoStep22SelfCheck **55/55**, NeoStep23Roundtrip **15/15**, NeoStep24CliRoundtrip
  **5/5**.
- **Legacy-neutral (CRITICAL -- ILMethod is SHARED):** the flag + `InitCodeBodyFromNeo`
  + the BodyRegister short-circuit + the loader + the self-check all compile out under
  `#if ENABLE_NEO_MODE`; plain-`Debug` build = 0 errors; ILMethod byte-identical to
  before. A Legacy regression is impossible.

## Review-loop round 1 (TEST-ONLY + 2 defensive)
Round-0 review APPROVE (0 Blocker/Major); recommended the permanent body-mutation
guard. Round-1 fixer (non-author) adopted: (1) the body-mutation cell (ConstProbe,
permanent guard); (2) a typed-catch cell (TypedCatchProbe, typed EH dispatch); (3)
Minor-1 `RebuildEHFromNeo` default-arm fail-loud (was silently misclassifying unknown/
Filter as Catch -- now a tagged NIE, matching `InitCodeBody`); (4) Minor-2
`ResolveElemTypesFromNeo` guard (null aqname-miss -> tagged NIE). No real S1 bug found
(the Minors are forward-only S2/S3 cases, NOT exercised in S1).

## Deferred (S2/S3/Step-26 follow-ups -- recorded in neo-deferred-items.md STEP-25-PARTIAL)
- **S2:** generic-instantiation-at-load (CloneAndPatch from the `.neo` template into an
  AOT ILMethod).
- **S3:** full ILType Cecil-decoupling (ILType AOT-init from `NeoTypeDefRecord`) +
  load into a Cecil-free AppDomain + cross-AppDomain token-hash re-resolution
  (Approach 1: record compile-time `GetHashCode()` per ref entry; Approaches 2/3
  rejected -- identity-uniqueness relied on by SHARED maps) + static `.cctor` seeding
  via `.neo` + full CLR aqname indexing / host-CLR-assembly registration (the Step-24
  `TestCLREnum` gap).
- **KEY S2/S3 prerequisite:** the Neo host entry `ILIntepreter.Run` is a Step-6
  PARAMETERLESS-ONLY shim (doesn't marshal `object[] p` into the frame). A stronger V2
  (parametrized + CLR-catch-shape coverage) needs a fuller Run entry or an internal-call-
  driven invocation. (The S1 V2 is parameterless; the EH rebuild itself is proven.)
- **Step 26:** perf benchmarks.

## Lessons reaffirmed
- **Probe before designing (the design hinge).** The Cecil-coupling enumeration found
  ZERO Cecil at execution -- collapsing Step 25 from "decouple ExecuteNeo" to "init
  only", making S1 tractable. A design from §8.4 alone would have over-scoped it.
- **A green JIT==AOT check does NOT prove the AOT body runs.** Step 23 made the bodies
  byte-identical, so JIT==AOT is trivially true even if the JIT body runs. The body-
  mutation guard (mutate the deserialized body, assert the mutated result) is the ONLY
  proof ExecuteNeo ran the genuine AOT body. Made permanent in round 1.
- **Adversarial review caught the property-proof gap.** The reviewer's Probe A (the
  body-mutation) was the load-bearing proof; round 1 made it a permanent regression
  guard for S2/S3.
