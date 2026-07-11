# Capability: neo-optimizer

## ADDED Requirements

### Requirement: The NeoAssemblyLoader consumes the .neo TemplateTable and binds reconstructed templates to live generic-method definitions (S2: generic-instantiation-at-load)

The Step-25 runtime loader (`NeoAssemblyLoader.Attach`) SHALL additionally
consume the deserialized `NeoAssemblyModel.Templates` table (Step 23) and, for
each `NeoTemplateRecord`, bind a reconstructed `GenericMethodTemplate` to the
matching live generic-method DEFINITION in the SAME AppDomain. The loader SHALL
resolve each record's `DefinitionMethodRefIdx` to its `MethodReferencePatchInfo`,
resolve the declaring-type full name to an `ILType` via
`appdomain.LoadedTypes[fullName]`, and match the open generic definition by
name + parameter count + `GenericParameterCount > 0 && !IsGenericInstance`. The
loader SHALL reconstruct the template via a new
`GenericMethodTemplateOps.BuildFromNeoRecord(definition, appdomain, model,
record, resolveVariableType)` helper (mirroring `StoreFromCapture` but taking
the `.neo` record + a loader-provided `VariableTypes` re-resolution closure),
then install it on the definition via a new Neo-only
`ILMethod.InitTemplateFromNeo(template)` setter that OVERWRITES the
`genericMethodTemplate` field. The execution path SHALL be UNCHANGED: the
existing Step-22 hook in `ILMethod.InitCodeBody` (`ILMethod.cs:720-762`) already
routes a generic-instance `ILMethod` through
`GenericMethodTemplateOps.TryInstantiate` (`CloneAndPatch`) whenever
`genericDefinition.GenericMethodTemplateCache != null`, and `ExecuteNeo` runs the
resulting body unmodified. A template whose reconstruction hits a deferred case
(a T-identity-token patch, or a `VariableTypes` re-resolution miss) SHALL be
SKIPPED -- the generic method keeps its per-occurrence JIT path (the additive
contract) -- and the miss SHALL be recorded in the load report. The loader
SHALL NOT abort on a miss.

#### Scenario: Each NeoTemplateRecord binds to a live generic-method definition
- **WHEN** `NeoAssemblyLoader.Attach(appdomain, model)` is invoked on a
  `NeoAssemblyModel` whose probe types are Cecil-loaded in `appdomain` and
  whose `TemplateTable` carries a template for a generic method on a probe type
- **THEN** for every `NeoTemplateRecord`, the loader SHALL resolve the
  declaring type via `LoadedTypes[fullName]`, match the open generic definition,
  reconstruct the `GenericMethodTemplate`, and call
  `def.InitTemplateFromNeo(template)`
- **AND** the matched definition's `GenericMethodTemplateCache` SHALL be the
  reconstructed template after the attach
- **AND** a subsequent generic-instance call on that definition SHALL route
  through `CloneAndPatch` (the Step-22 hook), NOT the per-occurrence JIT

#### Scenario: The parameterless Run entry exercises a generic call via a wrapper
- **WHEN** a non-generic parameterless wrapper method (e.g.
  `static int Wrap() { return Echo<int>(42); }`) is invoked via the Step-6
  parameterless `ILIntepreter.Run` entry shim (`ILIntepreter.cs:87-120`) and the
  wrapper's body contains a `Call` to a generic method
- **THEN** `ExecuteNeo` SHALL resolve the generic-instance target via
  `MakeGenericMethod` and the callee's `BodyRegister` getter SHALL hit the
  Step-22 hook, which SHALL route through `CloneAndPatch` when the definition's
  cache is set
- **AND** a parametrized probe entry (P2) SHALL NOT be required -- the generic
  args are baked into the wrapper's `Call` token at JIT-compile time and never
  cross the Run boundary (a generic call is the same `Call`-opcode shape as the
  S1 `MixedLocalsProbe` internal byref call)

#### Scenario: A miss is skipped, not fatal
- **WHEN** a `NeoTemplateRecord`'s declaring type is not in `LoadedTypes`, or no
  open generic definition matches, or the template reconstruction returns null
  (a deferred T-identity-token case, or a `VariableTypes` re-resolution miss)
- **THEN** the loader SHALL record the miss in the skip report (target +
  reason) and continue with the remaining records
- **AND** the unmatched generic definition SHALL keep its JIT path (the cache
  stays null; the Step-22 hook falls through to per-occurrence JIT)
- **AND** the loader SHALL return a report enumerating the attached and skipped
  templates alongside the attached and skipped method defs

#### Scenario: Same-AppDomain token operands resolve naturally (no hash re-resolution)
- **WHEN** an AOT-instantiated generic body executes (via `CloneAndPatch` +
  `ExecuteNeo`) and a token operand is resolved
- **THEN** the operand hash SHALL resolve via the EXISTING `mapTypeToken` /
  `mapMethod` maps (populated at Cecil-load + JIT-compile time in the same
  AppDomain), WITHOUT any cross-AppDomain hash re-registration
- **AND** the S2 loader SHALL NOT record or rewrite compile-time hashes (that
  is the deferred S3 mechanism)

### Requirement: The ILMethod AOT template setter overwrites the JIT-captured template from a .neo-reconstructed GenericMethodTemplate (S2)

`ILMethod` (`ILRuntime/CLR/Method/ILMethod.cs`) SHALL gain a Neo-only internal
`InitTemplateFromNeo(GenericMethodTemplate template)` setter that sets the
`genericMethodTemplate` field DIRECTLY (overwriting any previously-cached
template), distinct from `StoreGenericTemplate` (`ILMethod.cs:1392-1404`) whose
`if (genericMethodTemplate != null) return;` guard PREVENTS overwrite. The
overwrite is REQUIRED because the `NeoCompiler.CaptureTemplate` step
(`NeoCompiler.cs:277-310`) -- which runs during the V2 capstone's compile --
ALREADY caches a JIT-captured template on the definition (it synthesizes a
capture-eligible instance and reads `capInstance.BodyRegister`, firing the
`InitCodeBody` capture hook); the loader's AOT template MUST replace it so a
subsequent generic call routes through `CloneAndPatch` against the AOT template
body, not the JIT-captured one. The setter SHALL be Neo-only (`#if
ENABLE_NEO_MODE`); `StoreGenericTemplate`'s guard SHALL remain intact for the
JIT capture path. The `GenericMethodTemplateCache` getter (`ILMethod.cs:1387-
1390`) SHALL continue to return null for a generic INSTANCE (only the definition
caches).

#### Scenario: The setter overwrites a JIT-cached template
- **WHEN** a generic definition's `GenericMethodTemplateCache` is already
  non-null (a JIT-captured template from `StoreGenericTemplate`) and the loader
  calls `def.InitTemplateFromNeo(aotTemplate)`
- **THEN** the definition's `GenericMethodTemplateCache` SHALL become the AOT
  template (the JIT-captured one is replaced)
- **AND** a subsequent generic-instance call SHALL route through `CloneAndPatch`
  against the AOT template body

#### Scenario: StoreGenericTemplate's guard is unchanged for the JIT path
- **WHEN** a capture-eligible generic instantiation runs via the normal JIT path
  (no AOT loader involved) and the definition's cache is already set
- **THEN** `StoreGenericTemplate` SHALL still return early (no overwrite) -- the
  JIT capture path is byte-identical to before this change

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and runs the Legacy
  register VM
- **THEN** `InitTemplateFromNeo` SHALL compile out (it is gated `#if
  ENABLE_NEO_MODE`), and Legacy `ExecuteR` SHALL be byte-identical to before
  this change

### Requirement: The S2 generic-template slice covers no-T-identity-token generic methods; T-identity-token + cross-AppDomain + EH-bearing cases stay JIT-fallback (deferred to S3)

The S2 `BuildFromNeoRecord` reconstruction SHALL cover generic methods whose
template has NO T-identity token `PatchEntry` (no `Box T` / `Unbox T` /
`Unbox_Any T` / `Isinst T` / `Castclass T` / `Newarr T[]` / `Stobj T` /
`Ldobj T` / `Constrained T` type-token, and no T-qualified `IComparable<T>::
CompareTo`-style method-token). For such a template the patch table is
`IsRefMoveFlag`-only or empty; `BuildFromNeoRecord` SHALL set each patch's
`CecilToken` to null (a null `CecilToken` makes `DoCloneAndPatch` SKIP the patch
at `GenericMethodTemplate.cs:485`, and the back-half's
`TypeSpecializeNeoOpcodes` re-derives the is-ref flag). `BuildFromNeoRecord`
SHALL re-resolve `VariableTypes` from the record's `VariableTypeRefIdxs` via the
loader-provided closure (TypeRef idx -> Cecil `TypeReference`, same-AppDomain),
so `BuildInitObjPrefix` (`GenericMethodTemplate.cs:430-460`) works for generic-
param-typed locals. `BuildFromNeoRecord` SHALL set `Addr` and `Symbols` to null
(the S2 probe's generic methods carry no exception handler; `Addr` is needed
only for EH rebuild and `Symbols` is not read at `CloneAndPatch` /
`ExecuteNeo`). A template whose `Patches` contain a `TypeToken` or
`MethodToken` with a non-`none` `CecilTokenKind` (a T-identity-token site that
needs a Cecil `TypeReference` / `MethodReference` to re-resolve, or a Cecil-free
patch applier keyed on `GenericParamIdx`) SHALL be REJECTED by
`BuildFromNeoRecord` (return null -> the loader skips the bind -> the generic
method keeps JIT). This rejection is the honest S2 boundary: T-identity-token
re-resolution, generic methods WITH try/catch (Cecil `Addr`), cross-AppDomain
(a Cecil-FREE AppDomain), full `ILType` decoupling, and Approach-1 token-hash
re-resolution are DEFERRED to S3.

#### Scenario: A no-T-identity-token generic method binds and runs via CloneAndPatch
- **WHEN** a generic method `T Echo<T>(T v) { return v; }` (a pure-dataflow
  body with NO `Box T` / `Constrained T` / T-qualified callvirt) is in the
  probe and the loader binds its reconstructed template
- **THEN** a concrete-T call (e.g. `Echo<int>(42)`) SHALL route through
  `CloneAndPatch` from the AOT template and return the correct result
- **AND** the structural-equivalence check SHALL confirm the `CloneAndPatch`
  body EQUALS the per-occurrence JIT body for that concrete T

#### Scenario: A T-identity-token template is rejected (S3)
- **WHEN** a generic method body contains a T-identity token site (e.g.
  `Box T`, `constrained.callvirt T.GetHashCode`, `IComparable<T>::CompareTo`)
  and the loader processes its template
- **THEN** `BuildFromNeoRecord` SHALL return null (the template is rejected)
- **AND** the loader SHALL skip the bind and record it in the skip report
- **AND** the generic method SHALL keep its per-occurrence JIT path (correctness
  preserved; only the AOT optimization is lost for this case)

#### Scenario: A VariableTypes re-resolution miss is skipped (S3)
- **WHEN** the loader cannot re-resolve some `VariableTypeRefIdx` to a Cecil
  `TypeReference` (e.g. a CLR type from an unloaded assembly)
- **THEN** `BuildFromNeoRecord` SHALL return null (skip)
- **AND** the generic method SHALL keep its JIT path
- **AND** the miss SHALL be reported (the S3 cross-AppDomain + Cecil-free
  re-resolution concern)

#### Scenario: A generic method WITH try/catch is deferred (S3)
- **WHEN** a generic method body contains an exception handler (needs Cecil
  `Addr` for the EH rebuild)
- **THEN** S2 SHALL NOT cover it (the probe's generic methods carry no EH); it
  SHALL keep JIT, deferred to the S3 per-instruction Cecil-handle recovery

### Requirement: The V2 generic self-check proves deserialize + CloneAndPatch-from-AOT-template == JIT for a generic probe matrix (the S2 capstone gate)

The S2 load-bearing gate SHALL extend the existing host-side self-check
`NeoStep25LoadExecCheck.Run(appdomain)` (`#if ENABLE_NEO_MODE && DEBUG`, driven
via the existing `ILRuntimeTestCLI` special-mode hook `if (nameFilter ==
"NeoStep25LoadExec")`) with GENERIC cells. The probe type
(`TestCases/NeoStep25LoadProbe.cs`) SHALL gain a generic method + non-generic
parameterless wrappers that call it at concrete T's (a primitive, an 8-byte
primitive, an IL struct, a reference type). The capstone SHALL: (1) compile a
`.neo` for the probe via the SAME `NeoCompiler.Compile` driver the CLI uses
(which captures the generic template into the `TemplateTable`); (2) read it
back via `NeoAssemblyReader.Read`; (3) `NeoAssemblyLoader.Attach` (which now
ALSO binds the reconstructed templates to the generic definitions, OVERWRITING
the JIT-captured template from the compile step); (4) invoke parameterless
wrappers calling FRESH generic instances (concrete T's not instantiated before
attach) via `ExecuteNeo`; (5) assert each result EQUALS its known-expected
value. A green JIT==AOT comparison is INSUFFICIENT (Step 23 already proved the
AOT template body == JIT body byte-for-byte), so the capstone SHALL include an
ADVERSARIAL template body-mutation cell: mutate a deserialized `TemplateBody`
constant BEFORE attach, invoke a wrapper calling a fresh generic instance, and
assert the MUTATED value -- observing the MUTATED value PROVES `CloneAndPatch`
ran the genuine AOT template body (not the JIT-captured one), the binding "a
green smoke does not prove a gate correct" lesson. The probe type SHALL stay
NON-NESTED + within the BCL-refs-only boundary (the S1 constraint). The
capstone reuses the SAME `NeoCompiler` / `NeoAssemblyReader` /
`NeoAssemblyLoader` seams (no test-only compile path).

#### Scenario: The generic cells pass via the AOT template path
- **WHEN** `NeoStep25LoadExecCheck.Run(appdomain)` is invoked host-side
  (DEBUG+Neo) with the extended generic cells
- **THEN** every generic wrapper's AOT-run result SHALL equal its known-expected
  value (the divide-assert does NOT trip) for each concrete T (primitive /
  8-byte-primitive / IL-struct / reference-type)
- **AND** each generic call SHALL route through `CloneAndPatch` from the AOT
  template (the definition's `GenericMethodTemplateCache` is the reconstructed
  template, overwriting the JIT-captured one)

#### Scenario: The template body-mutation cell proves the AOT template ran
- **WHEN** a deserialized `TemplateBody` constant is MUTATED before attach and a
  wrapper calling a FRESH generic instance (not instantiated before attach) is
  invoked post-attach
- **THEN** the observed result SHALL be the MUTATED value (NOT the unmutated
  JIT value)
- **AND** this SHALL prove `CloneAndPatch` ran the genuine AOT template body,
  not the JIT-captured template (the load-binding property of the S2 slice)

#### Scenario: The fresh-instance cell isolates the AOT-template path
- **WHEN** the after-attach run invokes a concrete-T wrapper NEVER instantiated
  before attach (e.g. before-run used `Echo<int>`, after-run uses `Echo<long>`)
- **THEN** the `Echo<long>` call SHALL route through `CloneAndPatch` via the AOT
  template (it cannot reuse a JIT-cached instance body -- none exists)
- **AND** the result SHALL be correct (isolating the AOT-template path from any
  JIT-cached instance state)

#### Scenario: Structural equivalence holds for the AOT-reconstructed template
- **WHEN** a DEBUG host-side comparator asserts the AOT-reconstructed template's
  `CloneAndPatch` body EQUALS the per-occurrence JIT body (reusing the Step-22
  V1 `BodiesEqual` comparator shape) for each concrete T
- **THEN** the two bodies SHALL be structurally equal (same length, same
  `Code`/`Register1/2/3`/`Operand`/`Operand2/3/4` per index)
- **AND** a divergence SHALL trip the check (NOT be silently accepted)

#### Scenario: Regression smoke stays green (additive)
- **WHEN** the loader's template-consumption extension + `BuildFromNeoRecord` +
  the `InitTemplateFromNeo` setter + the capstone's generic cells are added
  (`Debug_Neo`)
- **THEN** the NeoStep smoke SHALL stay 210/210, NeoStep22SelfCheck 55/55,
  NeoStep23Roundtrip 15/15, NeoStep24CliRoundtrip 5/5, and the extended
  NeoStep25LoadExec SHALL pass (ZERO regressions; the loader extension is a new
  consumer of the unchanged Step-22 hook, and the setter defaults the cache to
  null for every non-AOT scenario)
- **AND** a plain-`Debug` + `useRegister=true` NeoStep-filter run SHALL show the
  SAME pre-existing Legacy failure set with and without the change
  (Legacy-neutral; everything new is gated `#if ENABLE_NEO_MODE`)
