# Ship Log — neo-step25-s3-full-decoupling (S3 PARTIAL)

> Step 25 **S3** — the FULL Cecil-decoupling slice (PARTIAL: sub-surface 1 only).
> Shipped 2026-07-08. Capability: `neo-optimizer`. Parent portfolio:
> `neo-completion-portfolio`.

## What shipped (partial — sub-surface 1 only; the LARGEST Step-25 slice)

S3 dump-gated 5 sub-surfaces; shipped the coherent slice + deferred the large
remainder:

- **SHIPPED — Sub-surface 1 (ILType AOT-init from `NeoTypeDefRecord`,
  same-AppDomain).** A Neo-only builder (`ILType.RebuildFromNeoRecord`,
  `#if ENABLE_NEO_MODE`) reconstructs an `ILType`'s instance field layout
  (`TotalPrimitiveSize` / `TotalReferenceCount` / per-field `ILTypeFieldOffset`)
  + `naturalAlignment` (re-derived from the resolved field types) + the Neo
  VTable (`IMethod[]` slots resolved hierarchy-aware from `VTableMethodRefIdxs`
  + re-derived slot-key map) + the interface offset map — as PURE DATA, WITHOUT
  reading the Cecil `TypeDefinition`. A DEBUG host-side self-check
  (`NeoStep25LoadExecCheck`, 7 S3 cells) compares the rebuild EQUAL to the
  Cecil-computed values. **The rebuild is NOT installed on a live `ILType`**
  (the Cecil init path stays the only init path -> zero shared-mutable-state
  hazard -> Legacy-neutral). The value is the COMPLETENESS PROOF the Cecil-free
  load (sub-surface 2, deferred) will rely on.

## DEFERRED (honestly tagged, NOT promoted to "met")

- **Sub-surface 2 — Cecil-free AppDomain load.** `AppDomain.LoadAssembly` still
  requires a Cecil module (`AppDomain.cs:639-660`); a `.neo`-only load needs a
  new ILType factory + injection into `mapType`/`mapTypeToken`. LARGE.
- **Sub-surface 3 — Cross-AppDomain token-hash re-resolution (APPROACH 1).**
  Record the compile-time `GetHashCode()` per ref entry under a `.neo` Version
  bump; the loader re-registers resolved refs under the recorded hash. Depends
  on sub-surface 2. **Approaches 2/3 (name-based hash / body rewrite) REJECTED**
  (identity-uniqueness relied on by shared maps).
- **Sub-surface 4 — Static `.cctor` seeding.** `.cctor` is suppressed under Neo
  (`ILType.cs:186-200`); per-static-field offsets are NOT in the record.
- **Sub-surface 5 — Full CLR aqname / host-CLR-assembly registration** (the
  Step-24 `TestCLREnum` gap). Tangential; stretch.

## Files changed

Engine (the `ILType.cs` edit is SHARED — entirely `#if ENABLE_NEO_MODE`,
Legacy-neutral):
- `ILRuntime/CLR/TypeSystem/ILType.cs` (+157, `#if ENABLE_NEO_MODE`):
  `RebuildFromNeoRecord` static builder (reconstructs layout + VTable + interface
  map; re-derives `naturalAlignment`; **does NOT install on a live ILType**) +
  `NeoVTableSlotKeysForAOT` internal accessor (mirrors Step-23
  `NeoInterfaceMapForAOT`) + 2 private helpers.
- `ILRuntime/Runtime/NeoAOT/NeoAssemblyLoader.cs` (+102/-4, whole file
  `#if ENABLE_NEO_MODE`): `ResolveVTableFromRecord` helper (hierarchy-aware via
  `IType.GetMethod`, not own-only `MatchMethod` — needed for inherited base/CLR
  slots) + `ResolveTypeRefToIType` made `internal` + `NeoTypeRebuild` result
  struct.
- `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep25LoadExecCheck.cs` (+307,
  `#if ENABLE_NEO_MODE && DEBUG`): the 7 S3 cells + helpers.
- `TestCases/NeoStep25S3Probe.cs` (NEW): the probe type (OQ1 — the existing probe
  was a static-only container, too thin; this adds a base ILType + virtual +
  interface impl + 3 differing-width instance fields).
- Docs: `.trae/documents/neo-deferred-items.md` (STEP-25-PARTIAL row -> S3
  partial shipped + 2/3/4/5 deferred) + `.trae/documents/neo-handoff.md`.

## Verification (independent non-author re-run — GOLD-STANDARD adversarial)

| Check | Result |
|---|---|
| `NeoStep25LoadExec` (capstone) | 21 -> **28/28 cells, 0 failed** (21 S1/S2 + 7 S3) |
| `NeoStep` (Debug_Neo) | **218/0 failed/1 ignored** (unchanged) |
| `NeoStep22SelfCheck` | **55/55** (unchanged) |
| `NeoStep23Roundtrip` | **15/15** (unchanged) |
| `NeoStep24CliRoundtrip` | **5/5** (unchanged) |
| Legacy-neutral (CRITICAL — ILType.cs SHARED) | stash-toggle of the 3 engine files: WITH vs WITHOUT **identical** (plain-Debug 0 errors both ways; NeoStep 218/8/1 both ways; the 8-name failure set diff = **empty**, all NeoStep6/13/14/15/16 pre-existing). Builder entirely within `#if ENABLE_NEO_MODE` (ILType.cs:761-916). |

## The mutation cells are GOLD-STANDARD load-bearing (adversarially re-proven)

A structural-equivalence cell ALONE is insufficient (the record is built from
Cecil's values at serialize time — equivalence could be trivially true). The
reviewer ran the DECISIVE adversarial break: made `RebuildFromNeoRecord` read
**Cecil** instead of the record for the field offsets, rebuilt, ran the capstone.
Result exactly as designed: layout structural-equiv **still PASS** (Cecil==Cecil,
proving structural-equiv alone is insufficient) + layout mutation **FAIL**
(27/28). Reverted -> 28/28 restored. This DECISIVELY proves the mutation cells
genuinely prove the rebuild reads the `NeoTypeDefRecord` — they are not a
Cecil-cache artifact (the `model2` is freshly deserialized; `mutatedPO = orig +
9999` is unproducible by Cecil). The VTable-slot-swap mutation cell is the same
shape. The stash-toggle (un-mutated record -> equality; mutated -> divergence)
is the load-bearing proof.

## Review verdict: APPROVE-WITH-FINDINGS (0 Blocker, 0 Major)

Ship-ready. 2 Informational notes (accepted + documented):
- **I1:** `naturalAlignment` re-derivation handles only simple named field types
  (array/byref/generic-instance skipped on miss) — documented design D2 gap;
  the structural-equiv cell would catch any mismatch; the probe is simple
  (int/long/string). Forward-compat for sub-surface 2.
- **I2:** `ResolveVTableFromRecord` skips `mr.IsGenericInstance` method refs
  (counted `unresolved`) — documented forward signal for sub-surface 2.

## Legacy impact

None. The `ILType.cs` additions are entirely `#if ENABLE_NEO_MODE` (one block at
:761-916; shared code resumes unchanged at :922). Plain-`Debug` build = 0 errors.
Stash-toggle PROVEN: the NeoStep Legacy failure set is byte-identical with and
without the change. The rebuild is NOT installed on a live ILType (the Cecil init
path is the only init path).
