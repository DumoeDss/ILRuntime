# Spec Delta: neo-optimizer (neo-step25-s3-cecil-free-load)

> MODIFIED + ADDED delta. PURE ASCII. SHALL-first bodies. The Cecil-free
> AppDomain load (S3-2): a `.neo` loads into a FRESH ILRuntime AppDomain with NO
> Cecil TypeDefinitions and executes correctly via ExecuteNeo. The cross-AppDomain
> token-hash re-registration (APPROACH 1) is a hard prerequisite and SHIPS here
> (sub-surfaces 2 + 3 are inseparable). Sub-surface 4 (static .cctor seeding)
> stays deferred.

## MODIFIED Requirements

### Requirement: The Cecil-free AppDomain load, cross-AppDomain token-hash re-resolution, static .cctor seeding, and full CLR-assembly registration remain DEFERRED from S3 partial (honest deferral -- not promoted to met)

> MODIFIED by S3-2: sub-surfaces 2 (Cecil-free load) + 3 (cross-AppDomain hash
> re-resolution) are PROMOTED to met. Sub-surface 4 (static .cctor seeding) +
> the sub-surface-4 per-static-field offsets STAY deferred. Sub-surface 5
> (host-CLR-assembly registration) was already RESOLVED by S3-5 (unchanged).

The S3-2 slice SHALL deliver the Cecil-free AppDomain load (sub-surface 2) AND the
cross-AppDomain token-hash re-registration (sub-surface 3, APPROACH 1) together --
they are inseparable (the identity-based token hashes do not survive a fresh
AppDomain; `GetType(int)` at `AppDomain.cs:1429-1436` has no fallback). The static
`.cctor` seeding (sub-surface 4) + the per-static-field offsets SHALL remain
deferred: the capstone probe SHALL be `.cctor`-free + static-field-free, so the
Cecil-free INSTANCE layout + VTable load is independent of them.

#### Scenario: Cross-AppDomain token re-resolution via APPROACH 1 (SHIPPED)
- **WHEN** a `.neo` compiled in one ILRuntime AppDomain is loaded into a FRESH
  ILRuntime AppDomain (a Cecil-free load)
- **THEN** the `.neo` SHALL record the compile-time identity hash per TypeRef /
  MethodRef entry (a parallel `int[]` under a `.neo` Version bump; -1 for an
  unresolved/skip entry)
- **AND** the Cecil-free loader SHALL re-register each resolved ref (resolved by
  NAME from the ref tables) in the fresh AppDomain's `mapTypeToken` / `mapMethod`
  under the RECORDED hash
- **AND** the deserialized `OpCodeR[]` bodies SHALL run UNMODIFIED (no body
  rewrite; Approach 3 body-rewrite stays REJECTED)
- **AND** `ILType.GetHashCode` / `ILMethod.GetHashCode` SHALL remain identity-
  based (Approach 2 name-based-hash stays REJECTED)

#### Scenario: Static .cctor seeding stays deferred
- **WHEN** an AOT-loaded type declares a static constructor (`.cctor`)
- **THEN** S3-2 SHALL NOT seed it (the `.cctor` stays suppressed under Neo; the
  per-static-field offsets are not in the `NeoTypeDefRecord`)
- **AND** seeding it SHALL remain a follow-up folded with a `.neo` format
  extension for the per-static-field offsets (sub-surface 4)

#### Scenario: The Cecil-free load is the ILType-decoupling delivered
- **WHEN** the S3-2 slice is reviewed
- **THEN** the Cecil-free load SHALL build a live `ILType` from a
  `NeoTypeDefRecord` WITHOUT a Cecil ctor (no `TypeReference` / `TypeDefinition`)
- **AND** the S3-partial rebuild (layout + VTable + interface map) SHALL be
  INSTALLED on the Cecil-free ILType (NOT merely returned as comparison data)
- **AND** the capstone probe (`TestCases/NeoStep25S3Probe`) SHALL have NO static
  fields + NO `.cctor` so the capstone is independent of sub-surface 4

## ADDED Requirements

### Requirement: A `.neo` loads into a FRESH Cecil-free ILRuntime AppDomain and executes correctly via ExecuteNeo (S3-2 capstone)

The runtime SHALL provide a Neo-only `AppDomain.LoadNeoAssembly(NeoAssemblyModel
model, IReadOnlyList<string> hostClrRefPaths)` entry that loads a `.neo` into the
calling AppDomain WITHOUT reading any Cecil `ModuleDefinition`. The fresh
AppDomain's `mapType` / `mapTypeToken` / `mapMethod` SHALL be populated PURELY
from the `.neo` tables + the host CLR refs (via `Assembly.LoadFrom`, the S3-5
pattern). A method invoked on a Cecil-free ILType SHALL execute via `ExecuteNeo`
and yield the correct result, exercising field read + virtual dispatch +
interface dispatch on the Cecil-free type.

#### Scenario: A Cecil-free load builds live ILTypes from NeoTypeDefRecords
- **WHEN** `LoadNeoAssembly(model, hostClrRefPaths)` is called on a fresh
  AppDomain
- **THEN** the loader SHALL NOT call `ModuleDefinition.ReadModule` (no Cecil
  stream) + SHALL NOT add to `loadedModules`
- **AND** for each `NeoTypeDefRecord` in `model.TypeDefs`, the loader SHALL build
  a live `ILType` via a Neo-only factory that sets the instance layout
  (`totalPrimitiveSize`, `totalReferenceCnt`, per-field `fieldOffsets`,
  `fieldTypes`, `fieldMapping`), the re-derived `naturalAlignment`, the Neo VTable
  (from `VTableMethodRefIdxs` resolved to live `IMethod[]`), and the interface map
  (from `Interfaces[]`) DIRECTLY
- **AND** each Cecil-free ILType SHALL be registered in `mapType[fullName]` +
  `mapTypeToken[freshHash]`
- **AND** a Cecil-free ILType's Cecil-reading properties (`TypeDefinition`,
  `TypeReference`, `GenericParameters`, etc.) SHALL throw a descriptive
  `NotSupportedException` when accessed (NEVER silently return null/wrong)

#### Scenario: A two-pass build resolves intra-.neo base + interface references
- **WHEN** a `.neo` declares a type whose base type or interface is ANOTHER type
  in the same `.neo`
- **THEN** the loader SHALL build all `.neo` ILTypes in a first pass (registered
  in `mapType` by FullName) + resolve base/interface by NAME in a second pass
- **AND** a base/interface type NOT in the `.neo` SHALL resolve by name via the
  host CLR fallback (`GetType(string)`, post `Assembly.LoadFrom`)

#### Scenario: The capstone executes correctly on a Cecil-free AppDomain
- **WHEN** the S3 probe (`TestCases.NeoStep25S3Probe`, 3 instance fields of
  differing widths + a base-virtual override + an interface impl) is compiled in
  AppDomain A, loaded Cecil-free into a fresh AppDomain B, and a method invoked
- **THEN** the invocation SHALL return the known-expected value
- **AND** field read, virtual dispatch, and interface dispatch SHALL all execute
  on the Cecil-free ILType

#### Scenario: A green smoke does NOT prove the Cecil-free load (adversarial gate)
- **WHEN** the Cecil-free load is validated
- **THEN** a body-mutation cell SHALL mutate a `Ldc_I4` constant in an
  INDEPENDENT `model2`'s `NeoExecuteBody` BEFORE load + assert the Cecil-free
  execution yields the MUTATED value (not the Cecil/JIT value)
- **AND** a layout-mutation cell SHALL mutate a `PrimitiveOffset` in `model2`'s
  `Fields[]` BEFORE load + assert the Cecil-free ILType's `fieldOffsets` reflects
  the mutation (not Cecil's)
- **AND** a Cecil-free load that secretly fell back to Cecil or used the
  compile-AppDomain's maps SHALL fail BOTH mutation cells

### Requirement: The .neo records compile-time identity hashes per reference entry for cross-AppDomain re-registration (APPROACH 1, sub-surface 3)

The `.neo` format SHALL carry a parallel `int[]` of compile-time identity hashes
alongside the TypeRef and MethodRef tables, recorded at serialize time (a
`.neo` Version bump). The Cecil-free loader SHALL re-register each resolved ref
under the recorded hash so the identity-hash token operands baked into the
deserialized `OpCodeR[]` bodies resolve in the fresh AppDomain. This SHALL NOT
mutate the bodies and SHALL NOT change `GetHashCode` semantics.

#### Scenario: The TypeRef + MethodRef tables carry recorded identity hashes
- **WHEN** a `.neo` is serialized after Cecil-load + force-compile in the
  compiling AppDomain
- **THEN** each TypeRef entry SHALL carry the compile-time identity hash of the
  resolved `IType` (`t.GetHashCode()`, the value `GetTypeTokenHashCode` stores
  at `ILMethod.cs:1250`), or -1 for an unresolved/skip entry
- **AND** each MethodRef entry SHALL carry the compile-time identity hash of the
  resolved `IMethod` (`m.GetHashCode()`), or -1
- **AND** the `.neo` Version SHALL be bumped (the reader SHALL reject a prior-
  Version `.neo` for the Cecil-free load via a Version guard)

#### Scenario: The Cecil-free loader re-registers resolved refs under recorded hashes
- **WHEN** the Cecil-free loader resolves a ref by NAME (IL via the `.neo` TypeDef
  table; CLR via `GetType(string)` post `Assembly.LoadFrom`)
- **AND** the recorded hash for that ref is != -1
- **THEN** the loader SHALL register the resolved live object in `mapTypeToken`
  (for a type) or `mapMethod` (for a method) under the RECORDED hash
- **AND** the deserialized `OpCodeR[]` token operands (baked with the compile-time
  hash) SHALL resolve in the fresh AppDomain via `GetType(int)` / `GetMethod(int)`

#### Scenario: Same-AppDomain loads ignore the recorded hashes
- **WHEN** a V2 `.neo` (with recorded hashes) is loaded same-AppDomain via the
  S1/S2/S3-partial `NeoAssemblyLoader.Attach` path
- **THEN** the recorded-hash arrays SHALL be IGNORED (the live maps resolve the
  bodies naturally)
- **AND** the same-AppDomain S1/S2/S3-partial behavior SHALL be unchanged

#### Scenario: String-token + switch-target hashes need no recording
- **WHEN** a deserialized body's ldstr token or switch-target hash is resolved in
  the fresh AppDomain
- **THEN** the string interner SHALL be content-keyed (stable across AppDomains,
  no recording needed) for ldstr
- **AND** the switch-target hashes SHALL be body-local (carried verbatim in
  `SwitchTargets`, rebuilt by `RebuildSwitchTargetsFromNeo`, no cross-AppDomain
  concern)
- **AND** the static-field token path SHALL be unexercised by the capstone
  (SEQUENCE with sub-surface 4)

### Requirement: Host CLR reference assemblies are registered via Assembly.LoadFrom on the Cecil-free load side (reuses the S3-5 pattern)

The Cecil-free loader SHALL register host CLR reference assemblies via
`System.Reflection.Assembly.LoadFrom(path)` (best-effort try/catch), so
`AppDomain.GetType(string)`'s live `System.AppDomain.CurrentDomain.
GetAssemblies()` CLR fallback resolves host CLR types as `CLRType`. The loader
SHALL NOT `LoadAssembly(refStream)` the host CLR refs (which would shadow CLR
types as `ILType`, the S3-5 Q1.2 finding).

#### Scenario: Host CLR refs resolve as CLRType on the Cecil-free side
- **WHEN** `LoadNeoAssembly` is called with `hostClrRefPaths`
- **THEN** each path SHALL be registered via `Assembly.LoadFrom(path)` (best-
  effort try/catch; a BCL/already-loaded/unresolvable ref is skipped, never fatal)
- **AND** a CLR type referenced by the `.neo` SHALL resolve via `GetType(string)`
  as a `CLRType` (NOT a shadow `ILType`)
- **AND** the loader SHALL NOT call `LoadAssembly(refStream)` for a host CLR ref
