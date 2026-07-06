## Why

The Neo AOT toolchain (Steps 22-26) needs a generic-method specialization
mechanism that does NOT require pre-expanding every generic instantiation at
precompile time. Today the Neo JIT instantiates generics PER-OCCURRENCE: each
`MakeGenericMethod` call runs the full `JITCompiler.Compile` (CIL parse + FCP/BCP
optimizer + lowering) for `List<int>.Add`, `List<long>.Add`, etc. independently.
Step 22 introduces the **template + `CloneAndPatch`** layer (design doc §11/§8.4.2):
compile a generic method DEFINITION once to a template, then specialize at
runtime. This is the foundation the `.neo` serializer (Step 23), the `ilrt_neoc`
CLI (Step 24), and the runtime `.neo` loader (Step 25) build on. It is a pure
optimization layer — the per-occurrence JIT path already runs every generic
method the smoke covers, so there is NO functional change for existing behavior.

## What Changes

- **Add a `PatchEntry` struct + patch table** that records the operand-level
  sites in a generic method body that depend on a concrete generic argument's
  IDENTITY (type-token hashes, method-token hashes, the `Move` is-ref flag).
  Grounded in a per-occurrence JIT dump diff (see design.md §2): the dump shows
  `Register1/2/3` are T-INVARIANT across instantiations and only `Operand`-level
  fields differ at the register-index level.
- **Add a template compile** that captures, for a generic method definition
  (`GenericParameterCount > 0 && !IsGenericInstance`), the register-index
  `OpCodeR[]` body after Translate+Optimizer+CleanupRegister (the T-invariant
  structure) plus the `PatchEntry[]` table. Cached per-definition.
- **Add `CloneAndPatch(template, concreteTypeArgs)`** runtime instantiation:
  clone the template, re-run the T-dependent back-half
  (`TypeSpecializeNeoOpcodes` + `AllocateLocalStackSpaces` + `LowerNeoOffsets`)
  with the concrete type args, and (for the AOT direction) apply the PatchEntry
  table. Produces a `NeoExecuteBody` provably equivalent to the per-occurrence
  JIT body for the same instantiation.
- **Add ref-type-share vs value-type-CloneAndPatch discrimination**: when EVERY
  concrete generic arg is a reference type AND the body has no T-identity token,
  all instantiations share ONE body verbatim (the dump confirms
  `Fill<object>` == `Fill<string>` byte-for-byte); otherwise `CloneAndPatch`.
- **Add an additive cache** keyed by the generic method definition, hooked into
  `ILMethod.BodyRegister` / `MakeGenericMethod`. The per-occurrence JIT path
  STAYS as the reference + fallback; the template path is a cache layer gated
  `#if ENABLE_NEO_MODE` (Neo-only; Legacy `ExecuteR` is unaffected).
- **Add a V1 structural-equivalence test** (`CloneAndPatch(template,T)` ==
  per-occurrence JIT body) as the load-bearing gate, + a V2 functional roundtrip
  as sanity. The NeoStep smoke (204/204) is the regression gate.

Non-goals (deferred to later AOT steps): the `.neo` binary format + serializer
(Step 23); the `ilrt_neoc` precompile CLI (Step 24); the runtime `.neo` loader
(Step 25); perf benchmarks (Step 26). Step 22 ships the IN-MEMORY template
mechanism + its equivalence test. The per-occurrence JIT instantiation path is
NOT removed or changed (it is the reference + the fallback).

## Capabilities

### New Capabilities

(None — the generic-method template mechanism is a correctness/optimization
invariant of the existing Neo code-path, recorded under `neo-optimizer`.)

### Modified Capabilities

- `neo-optimizer`: add Requirements for the generic-method template mechanism —
  the `PatchEntry` invariant (what the dump-diff proved it must capture), the
  template-cache + `CloneAndPatch` equivalence-to-per-occurrence invariant, the
  ref-type-share vs value-type-CloneAndPatch discrimination, the pre-lowering
  patch / post-lowering re-derive decision, and the additive + Legacy-neutral
  gating.

## Impact

- **`ILRuntime/CLR/Method/ILMethod.cs`** — the template cache (on the generic
  definition) + the `BodyRegister` getter / `MakeGenericMethod` hook that routes
  a generic instance through `CloneAndPatch` when a template is available.
- **`ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`** — template
  capture (the register-index body after CleanupRegister, before the T-dependent
  back-half) + the `PatchEntry` extraction. The `Compile` pipeline is refactored
  so the T-dependent back-half (`TypeSpecializeNeoOpcodes` +
  `AllocateLocalStackSpaces` + `LowerNeoOffsets`) is callable on a cloned body.
- **New file (likely `ILRuntime/Runtime/Intepreter/RegisterVM/GenericMethodTemplate.cs`)**
  — the `PatchEntry` struct, `GenericMethodTemplate` holder, and `CloneAndPatch`.
  Neo-only (`#if ENABLE_NEO_MODE`).
- **`TestCases/NeoStep22GenericTemplateTest.cs`** — the V1 equivalence test
  (host-side `OpCodeR[]` comparator + value-path assertion) + the V2 roundtrip.
- **No breaking changes.** The per-occurrence JIT path is the additive fallback;
  Legacy `ExecuteR` is untouched (template mechanism is Neo-only). Regression
  risk is MEDIUM and gated by the full NeoStep smoke + the V1 equivalence test.
