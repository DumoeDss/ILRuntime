# Spec Delta: neo-optimizer (neo-step25-s3-ccctor)

> MODIFIED + ADDED delta. PURE ASCII. SHALL-first bodies. S3-4 (TRUE COMPLETION):
> a type's static constructor (`.cctor`) runs when a `.neo` Cecil-free-loads, so a
> static field a `.cctor` writes reads back the `.cctor`-set value. This promotes
> the S3-2 sub-surface-4 deferral (static `.cctor` seeding + per-static-field
> offsets) to met, on the Cecil-free load path only.

## MODIFIED Requirements

### Requirement: The Cecil-free AppDomain load, cross-AppDomain token-hash re-resolution, static .cctor seeding, and full CLR-assembly registration remain DEFERRED from S3 partial (honest deferral -- not promoted to met)

A Cecil-free-loaded type's static constructor (`.cctor`) SHALL run at Cecil-free load so its static fields read back the `.cctor`-set values; the Cecil-ctor path's stale `.cctor` suppression SHALL remain untouched.

> MODIFIED by S3-4: sub-surface 4 (static `.cctor` seeding + the per-static-field
> offsets) is PROMOTED to met ON THE CECIL-FREE LOAD PATH. The S3-2 deferral
> ("Static .cctor seeding stays deferred") is lifted for a Cecil-free-loaded type:
> its `.cctor` SHALL run at load + its static fields SHALL read back the
> `.cctor`-set values. The stale `.cctor` suppression on the CECIL CTOR path
> (`ILType.cs` lazy `StaticInstance` getter + `InitializeMethods`) is NOT touched
> by S3-4 (a separately-gated concern).

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

#### Scenario: Static .cctor seeding on a Cecil-free-loaded type (SHIPPED by S3-4)
- **WHEN** an AOT-loaded type declares a static constructor (`.cctor`) AND a
  static field the `.cctor` writes
- **AND** the type is Cecil-free-loaded (via `LoadNeoAssembly`)
- **THEN** the `.cctor` SHALL run at load (after the bodies are bound) so the
  static field reads back the `.cctor`-set value (NOT the default zero)
- **AND** the `.neo` `NeoTypeDefRecord` SHALL carry a per-static-field layout
  array (name + type + per-field offsets) so the Cecil-free `ILType` can locate
  each static field in its static `byte[]` + `AutoList` storage
- **AND** a body-mutation probe SHALL mutate the `.cctor` body's stored constant
  BEFORE load + assert the read reflects the MUTATED constant (proves the
  deserialized `.cctor` body genuinely ran)

#### Scenario: The Cecil-free load is the ILType-decoupling delivered
- **WHEN** the S3-2 slice is reviewed
- **THEN** the Cecil-free load SHALL build a live `ILType` from a
  `NeoTypeDefRecord` WITHOUT a Cecil ctor (no `TypeReference` / `TypeDefinition`)
- **AND** the S3-partial rebuild (layout + VTable + interface map) SHALL be
  INSTALLED on the Cecil-free ILType (NOT merely returned as comparison data)
- **AND** the S3-2 capstone probe (`TestCases/NeoStep25S3Probe`) SHALL have NO
  static fields + NO `.cctor` so the S3-2 capstone is independent of sub-surface 4
  (the S3-4 capstone uses a SEPARATE probe that DOES declare a static + a `.cctor`)

## ADDED Requirements

### Requirement: A Cecil-free-loaded type's static constructor runs at load and its static fields read back the .cctor-set values (S3-4 capstone)

The Cecil-free loader SHALL seed a type's static constructor (`.cctor`) so a
static field the `.cctor` writes reads back the `.cctor`-set value (not the
default). The `.cctor` body is already in the `.neo` MethodDef table (the `.cctor`
is a non-generic method, force-compiled + serialized like any other); the
`NeoTypeDefRecord.StaticCtorMethodRefIdx` (recorded at serialize) points at it. The
loader SHALL run each Cecil-free type's `.cctor` via `appdomain.Invoke(cctor, null,
null)` AFTER the bodies are bound by `Attach` + AFTER the cross-AppDomain hash
re-registration (so the `.cctor`'s own Stsfld token operands resolve). This SHALL
be additive + Neo-only; the Cecil-ctor path's `.cctor` handling is UNCHANGED.

#### Scenario: The .neo carries a per-static-field layout array
- **WHEN** a `.neo` is serialized for a type that declares static fields
- **THEN** the `NeoTypeDefRecord` SHALL carry a `StaticFields[]` array parallel to
  the instance `Fields[]`, each entry holding the static field's `FieldRefIdx`
  (name + type + IsStatic) + its `PrimitiveOffset` + `ReferenceOffset`
- **AND** the `.neo` Version SHALL be bumped (V2 -> V3); the Cecil-free loader
  SHALL reject a prior-Version `.neo` for the static-field path via a Version guard
- **AND** a type with NO static fields SHALL carry an empty `StaticFields[]`

#### Scenario: The Cecil-free factory installs the static-field layout
- **WHEN** `ILType.CreateFromNeoRecord` builds a Cecil-free ILType
- **THEN** the factory SHALL install `staticFieldOffsets[]` + `staticFieldTypes[]`
  + `staticFieldMapping{}` from the `StaticFields[]` record (by name + per-field
  offsets), mirroring the instance-layout install
- **AND** the factory SHALL record the `.cctor` ILMethod (from
  `StaticCtorMethodRefIdx` / the `.cctor` name) as the type's `staticConstructor`
  with `staticConstructorCalled == false`
- **AND** the Stsfld / Ldsfld baked token (the static-field index) SHALL resolve
  via the installed `staticFieldMapping` UNCHANGED (no token-encoding change)

#### Scenario: The static-instance ctor is Cecil-free-safe
- **WHEN** an `ILTypeStaticInstance` is constructed for a Cecil-free ILType
- **THEN** the ctor SHALL NOT read `type.TypeDefinition.Fields` (which is NULL on
  a Cecil-free type)
- **AND** the ctor SHALL size its `byte[]` Primitives + `AutoList` from the type's
  static totals + locate each field via the installed `staticFieldOffsets`
- **AND** the ctor SHALL skip the Cecil `InitialValue` byte-blob replay (a `.neo`
  carries no raw initial-value blobs; the `.cctor` is the initializer)

#### Scenario: The loader seeds the .cctor at Cecil-free load
- **WHEN** `LoadNeoAssembly` finishes building + binding the bodies of a Cecil-free
  type whose `StaticCtorMethodRefIdx != -1`
- **THEN** the loader SHALL invoke the `.cctor` via `appdomain.Invoke(cctor, null,
  null)` (the SAME call the Legacy lazy path uses)
- **AND** the invoke SHALL be best-effort (a throwing `.cctor` is recorded as a
  skip, never fatal; the static state is left at default)
- **AND** a type whose `StaticCtorMethodRefIdx == -1` SHALL be skipped (no `.cctor`)

#### Scenario: The capstone reads the .cctor-set static value
- **WHEN** a probe declaring a `static int` field + a `.cctor` that sets it to a
  known non-zero constant + a `ReadStatic()` reader is compiled, Cecil-free-loaded,
  and `ReadStatic()` invoked
- **THEN** the result SHALL equal the `.cctor`-set constant (NOT the default zero)

#### Scenario: A green smoke does NOT prove the .cctor seeding (adversarial gate)
- **WHEN** the `.cctor` seeding is validated
- **THEN** a body-mutation cell SHALL mutate the `.cctor` body's stored constant
  (a `Ldc_I4` `TokenInteger` / the stored operand) in an INDEPENDENT `model2`'s
  `NeoExecuteBody` BEFORE `LoadNeoAssembly` + assert the Cecil-free read yields the
  MUTATED constant
- **AND** a Cecil-free load that secretly re-read Cecil's `.cctor`, or that never
  ran the `.cctor` (a default-zero read), SHALL fail the mutation cell

### Requirement: Same-AppDomain loads ignore the new static-field layout array (additive + backward-compatible)

The new `StaticFields[]` array + the `.neo` Version bump SHALL be additive: the
same-AppDomain S1/S2/S3 path SHALL ignore `StaticFields[]` (the Cecil
`InitializeFields` provides the static offsets). A V2 `.neo` SHALL remain valid
same-AppDomain. The Cecil ctor + ALL lazy inits SHALL be UNCHANGED. The stale
`.cctor` suppression on the Cecil-path lazy `StaticInstance` getter SHALL NOT be
lifted by S3-4 (S3-4 seeds ONLY the Cecil-free path explicitly at `LoadNeoAssembly`).

#### Scenario: The same-AppDomain path ignores StaticFields[]
- **WHEN** a V3 `.neo` (with `StaticFields[]`) is loaded same-AppDomain via the
  S1/S2/S3-partial `NeoAssemblyLoader.Attach` path
- **THEN** the `StaticFields[]` array SHALL be IGNORED (the Cecil `InitializeFields`
  provides `staticFieldOffsets` naturally)
- **AND** the same-AppDomain S1/S2/S3 behavior SHALL be unchanged

#### Scenario: The Cecil-path .cctor suppression is untouched
- **WHEN** a Cecil-loaded Neo type's `StaticInstance` is first accessed
- **THEN** S3-4 SHALL NOT change the existing `#if ENABLE_NEO_MODE` suppression at
  the lazy `StaticInstance` getter / `InitializeMethods` (Legacy-neutral safety)
- **AND** the `.cctor` seeding SHALL apply ONLY to the Cecil-free `LoadNeoAssembly`
  path
