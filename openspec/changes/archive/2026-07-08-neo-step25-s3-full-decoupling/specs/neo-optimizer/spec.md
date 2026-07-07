# Capability: neo-optimizer

## ADDED Requirements

### Requirement: The NeoTypeDefRecord carries enough to rebuild an ILType field layout + Neo VTable without Cecil, proven by a host-side structural-equivalence self-check + an adversarial mutation cell (S3 partial: ILType AOT-init, same-AppDomain)

The Step-25 runtime AOT path SHALL additionally decouple the `ILType` side: a
Neo-only builder (`#if ENABLE_NEO_MODE`) SHALL reconstruct an `ILType`'s
instance field layout and Neo VTable as PURE DATA from a deserialized
`NeoTypeDefRecord` (`NeoAssembly.cs:162-179`), WITHOUT reading the Cecil
`TypeDefinition`. The builder SHALL reconstruct the instance field layout
(`TotalPrimitiveSize`, `TotalReferenceCount`, and the per-field
`ILTypeFieldOffset{PrimitiveOffset, ReferenceOffset}` from each
`NeoFieldLayoutRecord`) and the Neo VTable (the `IMethod[]` slot array, by
resolving each `VTableMethodRefIdxs[i]` to the live `IMethod` via the
same-AppDomain maps, plus the slot-key map re-derived from each slot method's
`SignatureString`, plus the interface offset map from each
`NeoInterfaceEntryRecord`). The builder SHALL NOT install the rebuilt layout or
VTable on a live `ILType` in this slice (the Cecil init path stays as the only
init path; the rebuild is consumed by the self-check). The Cecil / JIT init
path (`ILType.InitializeFields` / `BuildNeoVTable`) SHALL stay byte-identical
when the builder is not exercised (Legacy-neutral). The two honest record gaps
(`naturalAlignment`, not carried but re-derived from the resolved field types;
per-static-field offsets, not carried -- only the static totals are) SHALL be
documented; `naturalAlignment` SHALL be re-derived and compared, and the
per-static-field offset gap SHALL be deferred with sub-surface 4.

#### Scenario: The rebuilt instance layout equals the Cecil-computed layout
- **WHEN** the host-side self-check deserializes a `.neo` for a probe `ILType`
  that declares 2+ instance fields of differing primitive widths (and the type
  is Cecil-loaded in the SAME AppDomain)
- **THEN** the builder SHALL rebuild `TotalPrimitiveSize`,
  `TotalReferenceCount`, and each per-field `PrimitiveOffset` /
  `ReferenceOffset` from the `NeoTypeDefRecord`
- **AND** each rebuilt value SHALL EQUAL the Cecil-computed value on that
  `ILType` (`iltype.TotalPrimitiveSize`, `iltype.TotalReferenceCount`,
  `iltype.fieldOffsets`)
- **AND** the re-derived `naturalAlignment` SHALL EQUAL the Cecil-computed
  `iltype.NaturalAlignment`

#### Scenario: The rebuilt Neo VTable equals the Cecil-computed VTable
- **WHEN** the probe `ILType` declares a base-class virtual method and/or
  implements an interface (so the VTable + interface map are non-trivial)
- **THEN** the builder SHALL resolve each `VTableMethodRefIdxs[i]` to the live
  `IMethod` and rebuild the `IMethod[]` slot array
- **AND** the rebuilt slot array SHALL EQUAL `iltype.NeoVTable` slot-by-slot
- **AND** the re-derived slot-key map SHALL EQUAL `iltype`'s slot-key map
- **AND** each rebuilt interface offset SHALL EQUAL
  `iltype.GetInterfaceVTableOffset(interfaceType)`

#### Scenario: A mutated record produces a divergent rebuild (the load-bearing mutation cell)
- **WHEN** the self-check mutates a field `PrimitiveOffset` (or swaps two
  `VTableMethodRefIdxs` entries) in an INDEPENDENT deserialized
  `NeoTypeDefRecord` BEFORE rebuild
- **THEN** the rebuilt layout (or VTable) SHALL DIVERGE from the Cecil-computed
  value exactly where mutated
- **AND** a rebuild that ignored the record would still equal Cecil and this
  cell SHALL FAIL for such a rebuild -- so a PASS proves the rebuild genuinely
  reads the `NeoTypeDefRecord`

#### Scenario: The self-check reuses the existing capstone hook
- **WHEN** the `NeoStep25LoadExec` CLI special-mode hook runs
- **THEN** the existing `NeoStep25LoadExecCheck.Run(appdomain)` self-check
  (`#if ENABLE_NEO_MODE && DEBUG`) SHALL additionally run the
  structural-equivalence cells and the mutation cell
- **AND** the loader/helper additions SHALL be additive and Neo-only (no new
  CLI hook; the S1/S2 attach flow is unchanged)

### Requirement: The Cecil-free AppDomain load, cross-AppDomain token-hash re-resolution, static .cctor seeding, and full CLR-assembly registration remain DEFERRED from S3 partial (honest deferral -- not promoted to met)

The S3 partial slice SHALL NOT deliver the Cecil-free AppDomain load, the
cross-AppDomain token-hash re-resolution, the static `.cctor` seeding via
`.neo`, or the full CLR aqname / host-CLR-assembly registration. Each SHALL
remain at its current state: `AppDomain.LoadAssembly` SHALL still require a
Cecil module (sub-surface 2); the identity-based token hashes
(`ILType.GetHashCode` / `ILMethod.GetHashCode`) SHALL remain un-recorded in the
`.neo` (sub-surface 3, APPROACH 1 -- the loader re-registers resolved refs
under a recorded compile-time hash; Approaches 2/3 name-based-hash /
body-rewrite stay REJECTED); the static `.cctor` SHALL remain suppressed under
`ENABLE_NEO_MODE` (`ILType.cs:186-200`) (sub-surface 4); and the standalone
CLI SHALL keep its current reference-assembly handling (sub-surface 5, the
Step-24 `TestCLREnum` gap). The deferral SHALL be recorded in
`.trae/documents/neo-deferred-items.md` under the STEP-25-PARTIAL row. Nothing
in this slice SHALL be promoted to "met" in the spec without a functional gate.

#### Scenario: Cross-AppDomain token re-resolution stays APPROACH 1 (deferred)
- **WHEN** a `.neo` compiled in one AppDomain is loaded into a FRESH
  AppDomain (a Cecil-free load)
- **THEN** S3 partial SHALL NOT support it (the identity-based token hashes do
  not survive the fresh AppDomain)
- **AND** the APPROACH 1 design (record the compile-time `GetHashCode()` per
  ref entry under a `.neo` Version bump; the loader re-registers resolved refs
  under the recorded hash) SHALL be the recorded follow-up
- **AND** Approaches 2/3 (name-based hash / body rewrite) SHALL remain REJECTED

#### Scenario: Static .cctor seeding stays deferred
- **WHEN** an AOT-loaded type declares a static constructor (`.cctor`)
- **THEN** S3 partial SHALL NOT seed it (the `.cctor` stays suppressed under
  Neo; the per-static-field offsets are not in the `NeoTypeDefRecord`)
- **AND** seeding it SHALL remain a follow-up folded with the Cecil-free load
  (sub-surface 2) + a `.neo` format extension for the per-static-field offsets

#### Scenario: The ILType layout + VTable rebuild is the ONLY ILType-decoupling delivered
- **WHEN** the S3 partial slice is reviewed
- **THEN** the rebuild builder SHALL be consumed ONLY by the DEBUG self-check
  (it SHALL NOT replace the Cecil init on a live type in this slice)
- **AND** the Cecil-free functional load (sub-surface 2) + the cross-AppDomain
  re-resolution (sub-surface 3) SHALL remain deferred to a follow-up child
