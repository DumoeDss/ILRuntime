# Proposal: neo-step25-s2-generic-at-load

## Why

Step 25 S1 (archived `2026-07-07-neo-step25-runtime-loader`) proved the FIRST
end-to-end "deserialize a `.neo` + `ExecuteNeo` == JIT" result, but ONLY for
NON-GENERIC methods. S1's `NeoAssemblyLoader.Attach`
(`ILRuntime/Runtime/NeoAOT/NeoAssemblyLoader.cs`) consumes the `.neo`
`MethodDefTable` and skips every generic method:

- `NeoAssemblyLoader.cs:61-65` skips generic INSTANCES
  (`mr.IsGenericInstance` -> "generic instance (S2)").
- `NeoAssemblyLoader.cs:86-90` skips generic DEFINITIONS
  (`ilm.GenericParameterCount > 0` -> "generic definition (S2)").
- `NeoAssemblyLoader.cs:112` makes `MatchMethod` skip generic defs too.

So a generic method call on an AOT-loaded type TODAY falls back to the per-
occurrence JIT path (correct, but NOT AOT). The `.neo` `TemplateTable` (Step
23) is deserialized into `model.Templates` but NEVER consumed -- `Attach` only
iterates `model.MethodDefs`. This change (S2) closes that gap: at load time,
reconstruct each `GenericMethodTemplate` from the `.neo` `TemplateTable` and
bind it to the live generic-method DEFINITION's `GenericMethodTemplateCache`,
so a generic call on an AOT-loaded type runs through the Step-22 `CloneAndPatch`
path from the AOT template instead of falling back to JIT.

## What changes (one-line)

S2 is an INIT-ONLY loader extension (like S1): the loader additionally consumes
`model.Templates`, reconstructs each `GenericMethodTemplate` (TemplateBody +
frame metadata from the `.neo`; the Cecil-typed `VariableTypes` re-resolved
from `VariableTypeRefIdxs` via the live same-AppDomain Cecil module), and sets
it on the matched generic definition's `GenericMethodTemplateCache` via a new
`ILMethod.InitTemplateFromNeo` setter. The execution path is UNCHANGED: the
Step-22 hook already in `ILMethod.InitCodeBody` (`ILMethod.cs:720-762`) routes
a generic-instance `ILMethod` through `GenericMethodTemplateOps.TryInstantiate`
(`CloneAndPatch`) when `genericDefinition.GenericMethodTemplateCache != null`.
`ExecuteNeo`, the optimizer, the JIT, the Step-22 template mechanism, and the
Step-23 format are NOT modified.

## The P1-vs-P2 dump-gate decision (resolved: P1)

The portfolio handoff flagged a load-bearing design question: can the existing
PARAMETERLESS V2 self-check entry (`NeoStep25LoadExecCheck`, driven via the
Step-6 `ILIntepreter.Run` shim) exercise a generic call, or does S2 need a
PARAMETRIZED probe entry (P2)? The dump says P1 (parameterless suffices):

- `ILIntepreter.Run` (`ILIntepreter.cs:87-120`, Neo arm) is a PARAMETERLESS
  entry shim. Comment at `:95`: "Step 6 entry shim: only no-arg static methods
  are expected here." It allocates the ENTRY's frame + return ref region, then
  calls `ExecuteNeo`.
- A generic call is an INTERNAL `Call` opcode inside a parameterless wrapper's
  body. The wrapper `static int Wrap() { return Echo<int>(42); }` is
  parameterless; its body's `Call Echo<int>(42)` is resolved by the JIT at the
  Call site (the generic args are baked into the wrapper's method token, NOT
  marshaled at the Run boundary).
- `ExecuteNeo`'s Call handling (Steps 10/11) already resolves a generic-
  instance method via `MakeGenericMethod` (`ILMethod.cs:1342`) and allocates
  the callee's frame. The generic callee's `BodyRegister` getter hits the
  Step-22 hook (`ILMethod.cs:720-762`).
- S1's `MixedLocalsProbe` (`NeoStep25LoadProbe.cs:121-130`) ALREADY makes an
  internal call (`BumpRef(ref val)`) via the same parameterless Run shim and is
  green (S1 V2 8/8). A generic call is the SAME `Call`-opcode shape with a
  generic-instance target.

=> P2 (a parametrized probe entry / a fuller Run entry) is NOT needed for S2.
S2's capstone reuses the parameterless Run path via a non-generic wrapper that
calls a generic method. (A parametrized Run entry remains an S3 concern for
full ILType decoupling -- out of scope here.)

## Scope (what S2 delivers) -- the Cecil-re-resolution boundary

The `GenericMethodTemplate` (`GenericMethodTemplate.cs:83-148`) has Cecil-typed
fields that `DoCloneAndPatch` + `RunNeoBackHalf` consume:
`VariableTypes` (`TypeReference[]`, read by `BuildInitObjPrefix` at `:430-460`),
`Patches[].CecilToken` (read by `DoCloneAndPatch` at `:479-497`; a null
`CecilToken` SKIPS the patch at `:485`), `Addr` (Cecil `Instruction` -> body
index; used by `InitCodeBody`'s EH rebuild at `ILMethod.cs:807-830`), and
`Symbols` (set on the frame at `GenericMethodTemplate.cs:559`, NOT read by
`RunNeoBackHalf`/`ExecuteNeo`).

S2 DELIVERS the common-case slice:
- Generic methods whose template has NO T-identity token patches (no `Box T` /
  `Unbox T` / `Isinst T` / `Castclass T` / `Newarr T[]` / `Constrained T` /
  T-qualified `IComparable<T>::CompareTo` callvirt). The `.neo` `TemplateBody`
  drives `CloneAndPatch`; the patch table is `IsRefMoveFlag`-only or empty
  (`CecilToken` = none; `IsRefMoveFlag` is re-derived by the back-half's
  `TypeSpecializeNeoOpcodes` -- no Cecil token needed).
- `VariableTypes` is re-resolved from `VariableTypeRefIdxs` via the live same-
  AppDomain Cecil module (the type is Cecil-loaded in S2's same-AppDomain
  test), so `BuildInitObjPrefix` works for generic-param-typed locals.
- `Addr` / `Symbols` left null (the S2 probe's generic methods have no
  try/catch).

S2 DEFERS to S3 (the full Cecil-free decoupling):
- T-identity token re-resolution (`Box T`, `Constrained T`,
  `IComparable<T>::CompareTo`). These need either Cecil `TypeReference` /
  `MethodReference` recovery from the live module OR a Cecil-free patch
  applier keyed on `GenericParamIdx` (a new `DoCloneAndPatch` arm). Both are
  non-trivial and belong with the cross-AppDomain work.
- Generic methods WITH try/catch (need Cecil `Addr` for EH -- per-instruction
  Cecil handle recovery).
- Cross-AppDomain (a Cecil-FREE AppDomain; the S2 `VariableTypes` re-resolution
  depends on the live Cecil module) + full `ILType` decoupling + token-hash
  re-resolution (Approach 1) + `.cctor` seeding + full CLR aqname indexing.

This is an honest partial-ship: S2 proves the `.neo` `TemplateBody` is AOT-
executable via `CloneAndPatch` for the common (no-T-identity-token) generic
method, the same way S1 proved the `.neo` `MethodDef` body is AOT-executable
for non-generic methods. The T-identity-token + cross-AppDomain cases stay
JIT-fallback (the additive contract), NOT promoted to "met".

## Files the implementer will touch (engine + tests; the AOT tool is UNCHANGED)

Engine (all `#if ENABLE_NEO_MODE`):
- `ILRuntime/Runtime/NeoAOT/NeoAssemblyLoader.cs` -- extend `Attach` to consume
  `model.Templates`: for each `NeoTemplateRecord`, resolve the definition via
  the `DefinitionMethodRefIdx` -> declaring type + name + generic-param-count
  match, reconstruct the `GenericMethodTemplate`, and call the new setter.
- `ILRuntime/Runtime/Intepreter/RegisterVM/GenericMethodTemplate.cs` -- a new
  `GenericMethodTemplateOps.BuildFromNeoRecord(template, definition, appdomain,
  model, rec, resolveVariableType)` that mirrors `StoreFromCapture` but takes
  the `.neo` record + re-resolves `VariableTypes` from
  `VariableTypeRefIdxs`. `Patches` carry `CecilToken = null` for the S2 slice
  (the `IsRefMoveFlag`/no-token case); T-identity-token patches are skipped
  (S3).
- `ILRuntime/CLR/Method/ILMethod.cs` -- a new Neo-only
  `InitTemplateFromNeo(GenericMethodTemplate)` setter that OVERWRITES the
  `genericMethodTemplate` field (the existing `StoreGenericTemplate` at `:1392`
  guards against overwrite -- `if (genericMethodTemplate != null) return;` --
  which the V2 capstone's before-run JIT capture would trip; the setter bypasses
  the guard so the AOT template replaces the JIT-captured one).

Tests (the capstone + the probe):
- `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep25LoadExecCheck.cs` --
    extend the matrix with GENERIC cells (a parameterless wrapper calling a
  generic method; ref-T + value-T + struct-T concrete args) + a TEMPLATE
  BODY-MUTATION cell (mutate a constant in the `.neo` `TemplateBody` before
  attach -> invoke a wrapper calling a FRESH generic instance -> assert the
  MUTATED value; proves `CloneAndPatch` ran the genuine AOT template body, not
  a JIT fallback -- the binding "green smoke does not prove a gate correct"
  lesson).
- `TestCases/NeoStep25LoadProbe.cs` -- add a generic method + parameterless
  wrappers that call it at concrete T's (int / long / struct / ref). Stays
  NON-NESTED + within the BCL-refs-only boundary (mirrors the S1 probe).

The AOT standalone tool (`ILRuntimeNeoCompiler/`, `ilrt_neoc`) and the Step-23
format are UNCHANGED -- the `TemplateTable` is already serialized + deserialized
(Step 23 V1 roundtrip 15/15). S2 only adds the loader CONSUMER.

## Regression risk + adversarial-probe plan

Risk level: LOW-MEDIUM. The change is init-only + additive: the loader sets
`GenericMethodTemplateCache` from the `.neo`; the Step-22 hook (unchanged)
routes generic instances through `CloneAndPatch` when the cache is set. A
template the loader cannot reconstruct (a T-identity-token case, or a
re-resolution miss) is SKIPPED (additive contract -- the generic method keeps
its JIT path). The setter is Neo-only; Legacy `ExecuteR` is byte-identical.

A GREEN SMOKE DOES NOT PROVE THE GATE CORRECT (the binding lesson from Step 17
B1 / Step 22 Constrained-token / Step 25 body-mutation). The capstone's
adversarial probes:
1. A TEMPLATE BODY-MUTATION cell: mutate a deserialized `TemplateBody`
   constant before attach -> observe the MUTATED value from a generic call
   (proves the AOT template body, not a JIT-cached one, drove `CloneAndPatch`).
2. A FRESH-instance cell: the after-attach run invokes a generic instance NEVER
   instantiated before attach (so its first call MUST route through
   `CloneAndPatch` via the AOT template, not reuse a JIT-cached instance body).
3. A structural-equivalence cell (DEBUG host-side): the AOT-reconstructed
   template's `CloneAndPatch` body EQUALS the per-occurrence JIT body
   (`GenericMethodTemplateOps.BodiesEqual`, reusing the Step-22 V1 comparator
   shape) for each concrete T.
4. Regression: NeoStep 210/210, NeoStep22SelfCheck 55/55, NeoStep23Roundtrip
   15/15, NeoStep24CliRoundtrip 5/5, NeoStep25LoadExec (the extended capstone)
   all green; a stash-toggle plain-`Debug` Legacy run shows the same pre-
   existing failure set.

## Partial-ship signal

If the dump at apply shows the Cecil-token re-resolution is intractable for
even the no-T-identity-token slice (e.g. `BuildInitObjPrefix` cannot recover
`VariableTypes` without a live `def.Body` that Release nulls), S2 PARTIAL-
ships: the structural-equivalence proof (host-side, DEBUG, where `def.Body` is
present) ships; the production AOT direction (cross-AppDomain, Cecil-free)
stays deferred to S3. This is an accepted outcome (precedent: S1, Step 20
Phase 1) -- the canonical spec stays honest (the deferred cases stay JIT-
fallback, NOT promoted to "met"). DO NOT force a fix past the dump-gate.
