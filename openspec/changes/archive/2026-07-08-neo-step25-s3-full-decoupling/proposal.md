## Why

Step 25 S1 + S2 proved a deserialized `.neo` drives `ExecuteNeo` correctly for
non-generic methods (S1, the `isNeoAotBody` dual-path) and for generic methods
(S2, `CloneAndPatch` from a `.neo`-reconstructed template) -- but ONLY the
`ILMethod` side was decoupled. The `ILType` side still reads Cecil at init
(`InitializeFields` / `BuildNeoVTable` / `InitializeBaseType`), so a `.neo`
cannot yet reconstruct a type's field layout or VTable without the Cecil
`TypeDefinition` that produced it. S3 is the `ILType`-side decoupling step: prove
the `.neo` `NeoTypeDefRecord` carries ENOUGH to rebuild an `ILType`'s layout +
VTable without Cecil -- the foundation a future Cecil-free AppDomain load needs.

S3 spans 5 sub-surfaces (ILType AOT-init / Cecil-free load / cross-AppDomain
token re-resolution / `.cctor` seeding / CLR-assembly registration). The
dump-gate (binding, see design.md) finds sub-surfaces 2-4 LARGE and mutually
coupled (the true Cecil-free AOT scenario + a `.neo` format Version bump), so
this change PARTIAL-SHIPS: the coherent, reviewable slice is sub-surface 1
(ILType AOT-init from `NeoTypeDefRecord`, validated by a structural-equivalence
self-check on a Cecil-loaded type, same AppDomain), and sub-surfaces 2-5 are
deferred with honest spec deltas.

## What Changes

- ADD an `ILType` layout + VTable rebuild-from-record path (Neo-only,
  `#if ENABLE_NEO_MODE`): a builder that reconstructs the field layout
  (`TotalPrimitiveSize` / `TotalReferenceCount` / per-field `PrimitiveOffset` /
  `ReferenceOffset`) and the Neo VTable (`IMethod[]` slot array + slot-key map +
  interface offset map) from a deserialized `NeoTypeDefRecord`, resolving each
  `VTableMethodRefIdx` / interface `TypeRefIdx` to a runtime `IMethod` / `IType`
  via the same-AppDomain maps. Mirrors S1's `InitCodeBodyFromNeo` for the
  `ILMethod`, applied to the `ILType`.
- ADD a host-side structural-equivalence self-check (DEBUG + Neo) that rebuilds
  the layout + VTable from the deserialized `.neo` `NeoTypeDefRecord` for the
  existing Step-25 probe type and asserts EQUALITY with the Cecil-computed
  layout + VTable. This is the S2 `CompileViaAotTemplateNeoBody` + `BodiesEqual`
  pattern applied to the `ILType`: it PROVES the record is complete for type
  reconstruction (the property a Cecil-free load will rely on) WITHOUT requiring
  the Cecil-free AppDomain machinery.
- ADD an adversarial mutation cell to that self-check: mutate a field offset (or
  swap a VTable slot) in the deserialized `NeoTypeDefRecord` BEFORE rebuild and
  assert the rebuilt layout DIVERGES from Cecil's as expected. A green
  structural-equivalence cell alone is INSUFFICIENT (the record is built FROM
  Cecil's values at serialize time, so equality can hold trivially); the
  mutation cell is the load-bearing proof the rebuild genuinely reads the record
  (the S1/S2 body-mutation discipline).
- EXTEND the existing `NeoStep25LoadExecCheck` capstone (the S1/S2 host-side
  self-check, `#if ENABLE_NEO_MODE && DEBUG`) with the new cells. No new CLI
  hook (reuses `NeoStep25LoadExec`).
- DEFER (honest spec deltas, NOT promoted to "met"): sub-surface 2 (Cecil-free
  AppDomain load -- `LoadAssembly(Stream)` requires a Cecil module); sub-surface
  3 (cross-AppDomain token-hash re-resolution, APPROACH 1 -- record the
  compile-time `GetHashCode()` per ref entry under a `.neo` Version bump; the
  identity-based hashes do not survive a fresh AppDomain); sub-surface 4
  (static `.cctor` seeding -- `.cctor` is currently suppressed under Neo and the
  per-static-field offsets are not in the record); sub-surface 5 (full CLR
  aqname / host-CLR-assembly registration -- the Step-24 `TestCLREnum` gap, a
  CLI ergonomics concern). Each stays at its current NIE / JIT-fallback /
  suppressed state. No BREAKING change.

## Capabilities

### New Capabilities
<!-- None. S3 extends the existing AOT toolchain capability. -->

### Modified Capabilities
- `neo-optimizer`: ADD a requirement that the `.neo` `NeoTypeDefRecord` carries
  enough to rebuild an `ILType`'s field layout + Neo VTable without Cecil, and
  that a host-side structural-equivalence self-check (+ an adversarial
  mutation cell) proves it. The existing S2 deferral requirement (T-identity
  token / cross-AppDomain / full-ILType-decoupling deferred to S3) is NARROWED:
  the ILType layout + VTable rebuild is now IN scope; the Cecil-free AppDomain
  load, cross-AppDomain token re-resolution, `.cctor` seeding, and CLR-assembly
  registration remain DEFERRED (recorded as follow-ups).

## Impact

- `ILRuntime/CLR/TypeSystem/ILType.cs` (SHARED): a Neo-only
  layout/VTable-from-record builder + accessor, `#if ENABLE_NEO_MODE`, gated so
  the Cecil init path stays byte-identical when not exercised (Legacy-neutral;
  the builder is only invoked by the AOT self-check). Reads only fields the
  record carries; the two record gaps (`naturalAlignment`; per-static-field
  offsets) are documented forward-compat items, not blockers for the SHIP slice.
- `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep25LoadExecCheck.cs` (Neo-only,
  DEBUG): new structural-equivalence + mutation cells for the `ILType` rebuild.
- `ILRuntime/Runtime/NeoAOT/NeoAssemblyLoader.cs` (Neo-only): a small helper
  that resolves a `NeoTypeDefRecord`'s `VTableMethodRefIdxs` / interface
  `TypeRefIdxs` to runtime `IMethod` / `IType` (reuses the existing
  `ResolveTypeRefToIType`; adds the method-ref -> `IMethod` resolution). No
  change to the S1/S2 attach flow (the builder is driven by the self-check, not
  the loader, in this slice).
- Regression risk: MEDIUM. The `ILType` edits are SHARED; the dump-gate + the
  Neo-only gate + the additive contract (the builder is opt-in, never replacing
  the Cecil path in this slice) bound it. Gate: NeoStep smoke (215+ green) +
  `NeoStep25LoadExec` (21/21 + the new cells) + NeoStep22/23/24 self-checks
  unchanged + a Legacy-neutral plain-`Debug` build.
- No change to `ExecuteNeo`, the JIT, the optimizer, the Step-22 template
  mechanism, the Step-23 `.neo` format, or the Step-24 CLI (all consumed
  unchanged).
