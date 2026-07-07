# Design: neo-step25-s3-full-decoupling

> Technical design for the S3 PARTIAL slice (see proposal.md for the scope
> decision). Grounded in the actual init / load / serialization code on HEAD
> `3ec1fa37`, line-cited. Legacy is the REFERENCE, not a target.

## Context

S1 proved a deserialized `.neo` `NeoMethodDefRecord` is executable by
`ExecuteNeo` for non-generic methods (`isNeoAotBody` dual-path). S2 extended it
to generic methods (`CloneAndPatch` from a `.neo`-reconstructed template). Both
decoupled ONLY the `ILMethod` side. The `ILType` side still reads Cecil at init,
so a `.neo` cannot reconstruct a type's layout or VTable without the Cecil
`TypeDefinition`. S3 is the `ILType`-side decoupling step.

The Cecil-coupling hinge from S1 (zero Cecil at execution) HOLDS for the ILType
side too: `ExecuteNeo` resolves field/virtual/interface dispatch via the runtime
`fieldOffsets` / `neoVTable` / `neoInterfaceMap` structures, which are PURE
DATA computed at init. So the ILType decoupling is (again) an INIT concern:
rebuild those data structures from the `.neo` record instead of Cecil. The
question the dump-gate answers: does `NeoTypeDefRecord` carry ENOUGH?

## The per-sub-surface dump-gate (binding -- SMALL vs LARGE, each file:line-cited)

S3 spans 5 sub-surfaces. Probed on HEAD `3ec1fa37`. The dump is the arbiter.

| # | Sub-surface | Verdict | Evidence (file:line on HEAD) |
|---|-------------|---------|------------------------------|
| 1 | ILType AOT-init from `NeoTypeDefRecord` (layout + VTable rebuild), same-AppDomain | **SMALL -> SHIP** | The record (`NeoAssembly.cs:162-179`) carries `TotalPrimitiveSize`/`TotalReferenceCount`/`StaticTotalPrimitiveSize`/`StaticTotalReferenceCount`/`Fields[]` (`NeoFieldLayoutRecord{FieldRefIdx,PrimitiveOffset,ReferenceOffset}`, `:181-186`)/`VTableMethodRefIdxs[]`/`Interfaces[]`/`BaseTypeRefIdx`/`StaticCtorMethodRefIdx`. The runtime structs are pure data: `ILTypeFieldOffset{PrimitiveOffset,ReferenceOffset}` (`ILType.cs:18-22`); the layout pass (`InitializeFields`, `:2007-2180`) computes `fieldOffsets[]`/`totalPrimitiveSize`/`totalReferenceCnt`; `BuildNeoVTable` (`:444-525`) computes `neoVTable`/`neoVTableSlots`/`neoVTableSlotKeys`. The record carries the RESULTS. A rebuild + a structural-equivalence self-check is the S2 `BodiesEqual` pattern for types. |
| 2 | Cecil-free AppDomain load (the true AOT scenario) | **LARGE -> DEFER** | `AppDomain.LoadAssembly` (`AppDomain.cs:639-660`) requires `ModuleDefinition.ReadModule(stream)` (Cecil) -> `InitializeFromModule` (`:669-692`) constructs each `ILType` via `new ILType(TypeDefinition, this)` + `AddType` (`:662-667`, registers in `mapType` + `mapTypeToken` keyed by identity `GetHashCode()`). A Cecil-free load needs a new ILType factory from `NeoTypeDefRecord`, injection into `LoadedTypes` + `mapType` + `mapTypeToken` (with a RECORDED hash), and TypeRef/MethodRef/FieldRef resolution with NO Cecil module. Coupled to sub-surface 3. |
| 3 | Cross-AppDomain token-hash re-resolution (APPROACH 1) | **LARGE -> DEFER (depends on 2)** | `ILType.GetHashCode`/`ILMethod.GetHashCode` are IDENTITY-based (process-global `instance_id` counter, `ILType.cs:79-81`; `ILMethod.cs:1245-1249`). The token maps `mapTypeToken`/`mapMethod` (populated at `AppDomain.cs:662-666` + JIT time) / `fieldTokenMapping` (`ILType.cs:63,1915-1939`) / `jumptables` (`ILMethod.cs`) are keyed by these. A fresh load-AppDomain mints new hashes -> the deserialized bodies' token operands do NOT resolve. APPROACH 1 (DECIDED, handoff lead-1.md): extend the `.neo` to record the compile-time hash per ref entry under a Version bump; the loader re-registers resolved refs under the recorded hash. Approaches 2/3 REJECTED (name-based hash / body rewrite -- identity-uniqueness is relied on by SHARED maps; Legacy regression risk). This is a `.neo` format Version bump + a parallel hash array per reference table + the loader re-registration. Only meaningful with sub-surface 2. |
| 4 | Static `.cctor` seeding via `.neo` | **DEFER (depends on 2 for functional value)** | `.cctor` is SUPPRESSED under `ENABLE_NEO_MODE` (`ILType.cs:186-200`, comment "TODO Step 7"). The record carries `StaticCtorMethodRefIdx` (`NeoAssembly.cs:176-178`); S1's `Attach` already binds the `.cctor` body (`GetConstructors` includes the static `.cctor`, `NeoCompiler.cs:217`). So invoking it post-attach is mechanically small. BUT the static-instance machinery (`ILTypeStaticInstance`, `ILType.cs:174-203`) + the per-static-FIELD offsets are needed for it to be FUNCTIONAL, and the per-static-field offsets are NOT in the record (only the static TOTALS are -- sub-surface 1 gap b). In a same-AppDomain Cecil-loaded test the static instance is already Cecil-driven, so `.cctor` seeding has thin functional value without sub-surface 2. |
| 5 | Full CLR aqname / host-CLR-assembly registration (Step-24 `TestCLREnum` gap) | **STRETCH -> lean DEFER** | The CLI (`Program.cs`) -> `NeoCompiler.Compile(input, refs, output)` (`NeoCompiler.cs:52-160`). IL refs are `LoadAssembly`-ed; a ref that fails to load as IL is silently skipped (`:131-134`), relying on the CLR fallback `appdomain.GetType(aqname)`. The `TestCLREnum` gap: a host CLR assembly defining an enum referenced by the IL is not registered, so compiling a method touching that enum fails with a CLR-type-resolution fatal. This is a Step-24-CLI ergonomics concern (NOT a decoupling concern). A fix would register failing refs on the CLR side (`Assembly.LoadFrom`). SMALL in concept but tangential to the S3 theme and needs the AppDomain CLR-resolution path verified. Lean DEFER; ship IFF a clean <=1-call seam exists at apply. |

### The SHIP-vs-DEFER split

- **SHIP (sub-surface 1):** the `ILType` layout + VTable rebuild from
  `NeoTypeDefRecord`, validated by a structural-equivalence self-check +
  an adversarial mutation cell (sub-surface 1 same-AppDomain). The token maps
  already resolve same-AppDomain (S1/S2), so NO cross-AppDomain work. This is
  the natural extension of S1's `InitCodeBodyFromNeo` + S2's
  `CompileViaAotTemplateNeoBody`/`BodiesEqual`.
- **DEFER (sub-surfaces 2, 3, 4):** the Cecil-free AppDomain load + the
  cross-AppDomain token-hash re-resolution + `.cctor` seeding. These are the
  true Cecil-free AOT scenario; they are LARGE, mutually coupled, and need a
  `.neo` format Version bump. Recorded as follow-ups (see spec delta +
  `neo-deferred-items.md` STEP-25-PARTIAL).
- **STRETCH (sub-surface 5):** CLR-assembly registration. Ship IFF a clean seam
  exists at apply; else DEFER.

**STOP / partial-ship is binding.** Forcing sub-surfaces 2+3 (the LARGEST, with
a format Version bump) into this child would make the diff unreviewable and
couple an unproven Cecil-free load to the proven rebuild. Ship the coherent
slice; defer the rest.

## Decisions

### D1. The ILType AOT-init form: a rebuild BUILDER, not a parallel AOT-ILType

A parallel AOT-ILType type would duplicate the surface `ExecuteNeo` and the
dispatch path depend on (`fieldOffsets`, `neoVTable`, `neoInterfaceMap`,
`BaseType`, `Implements`, `GetFieldIndex`, etc.). Instead, mirror S1's flag +
alternate-init seam at the DATA level: a Neo-only builder that, given a
Cecil-loaded `ILType` + a deserialized `NeoTypeDefRecord`, REBUILDS the layout
structs + VTable structs from the record and hands them to the self-check for an
EQUALITY comparison against the Cecil-computed values. The Cecil path STAYS
byte-identical (the builder reads the record, never replacing the Cecil init in
this slice; it is invoked only by the DEBUG self-check).

This deliberately does NOT install the rebuilt layout/VTable on a live type in
this slice. WHY: in the same-AppDomain Cecil-loaded test, the Cecil layout is
already correct, so overwriting it has no functional effect AND raises the
regression risk (a shared-mutable-state hazard on `ILType` for zero functional
gain). The slice's VALUE is the COMPLETENESS PROOF (the record rebuilds the
layout + VTable exactly), which is the property sub-surface 2 will rely on.
Installing the rebuild is deferred to the Cecil-free load (where there is no
Cecil layout to begin with).

### D2. What the rebuild reconstructs (and the two honest record gaps)

The rebuild reconstructs, from `NeoTypeDefRecord`, and compares to the
Cecil-computed values on the same `ILType`:

- **Instance field layout:** `TotalPrimitiveSize`, `TotalReferenceCount`, and
  per-field `ILTypeFieldOffset{PrimitiveOffset, ReferenceOffset}` (rebuilt from
  `Fields[]`'s `NeoFieldLayoutRecord`). Compared field-by-field against
  `iltype.fieldOffsets` + the `TotalPrimitiveSize`/`TotalReferenceCount`
  getters (`ILType.cs:366-395`).
- **Neo VTable:** the `IMethod[]` slot array (`neoVTable`) rebuilt by resolving
  each `VTableMethodRefIdxs[i]` -> `MethodReferencePatchInfo` -> the live
  `IMethod` (name + declaring type + param count match, reusing the S1/S2
  match helper), compared slot-by-slot against `iltype.NeoVTable` (`:347-353`).
  The slot-key map (`neoVTableSlotKeys`) is NOT in the record; the rebuild
  RE-DERIVES it from each slot method's `SignatureString` (the same key
  `BuildNeoVTable` uses at `:617-680`), and compares the derived keys to the
  live `neoVTableSlotKeys`.
- **Interface map:** each `NeoInterfaceEntryRecord` (`InterfaceTypeRefIdx` +
  `VTableOffset` + `MethodSlotKeys[]` + `ClassSlotRemap[]`) resolves to the
  runtime `IType` + offset; compared against `iltype`'s interface offset map
  (`GetInterfaceVTableOffset`, `:725-746`). The record DOES carry the slot keys
  + the class-slot remap, so the comparison is exact.

**Two honest record gaps (forward-compat, NOT blockers for the SHIP slice):**
- (a) `naturalAlignment` (`ILType.cs:99`, computed at `:2043,2144,2160-2162,
  2173-2174`) is NOT in the record. It is a function of the field types (the
  max natural size over the type's fields). The rebuild RE-DERIVES it same-
  AppDomain from the resolved field types (each `FieldRefIdx` -> `FieldRefTable`
  -> field type -> size) and compares. A future Cecil-free load would either
  re-derive it the same way or the record gains a field under a Version bump.
- (b) Per-STATIC-field offsets (`staticFieldOffsets`, `ILType.cs:36,2028,
  2078-2104`) are NOT in the record (only the static TOTALS are). This is a
  sub-surface 4 / static-instance concern; the SHIP slice compares the static
  TOTALS (carried) and documents the per-field gap. Closing it is a `.neo`
  format extension folded with sub-surface 4.

Both gaps are documented in the spec delta + `neo-deferred-items.md`. Neither
blocks the completeness proof for the structures the record DOES carry.

### D3. The structural-equivalence self-check (the gate -- a green rebuild does NOT prove the record is read)

The S2 lesson applies: a structural-equivalence cell alone is INSUFFICIENT,
because the record is built FROM Cecil's values at serialize time (Step 23), so
equality can hold trivially even if the rebuild does not read the record. The
decisive proof is a MUTATION cell (the S1 `ConstProbe` / S2 `ConstGeneric<T>`
discipline):

1. **Structural-equivalence cells (host-side, DEBUG + Neo):** for the existing
   Step-25 probe type, deserialize a `.neo`, rebuild the layout + VTable from
   its `NeoTypeDefRecord`, and assert the rebuild EQUALS the Cecil-computed
   values (field-by-field, slot-by-slot, interface-by-interface). Proves the
   record carries the right data.
2. **Mutation cell (load-bearing):** take an INDEPENDENT deserialized `.neo`
   (`model2`), MUTATE a field offset in `NeoTypeDefRecord.Fields[k].
   PrimitiveOffset` (or swap two `VTableMethodRefIdxs` entries) BEFORE rebuild,
   rebuild, and assert the rebuilt layout DIVERGES from Cecil's exactly where
   mutated (and ONLY there). A rebuild that ignored the record would still
   equal Cecil -> the mutation cell FAILS -> proves the rebuild genuinely reads
   the record. Mirrors S1's `ConstProbe` mutation + S2's template body
   mutation.

The probe type STAYS the existing `TestCases/NeoStep25LoadProbe.cs` (no new
probe type; its fields + virtual/interface methods already exercise the layout +
VTable). If the probe declares too few fields / no interface to make the
comparison meaningful, ADD a small probe type (e.g. `NeoStep25S3Probe`) with 2+
instance fields of differing widths + a base-class virtual + an interface impl
-- confirm at apply (the dump-gate's "is the probe rich enough?" question).

### D4. The VTable-method + interface resolution helper (the one new loader helper)

`NeoAssemblyLoader` gains a small helper that resolves a `NeoTypeDefRecord`'s
references to runtime objects, reusing the existing S1/S2 machinery:

```
// Resolve VTableMethodRefIdxs[] -> IMethod[] (each slot -> the live method on
// the Cecil-loaded ILType, matched by name + param count from the MethodRef).
IMethod[] ResolveVTableFromRecord(ILType iltype, NeoAssemblyModel model,
                                  NeoTypeDefRecord rec)
{
    var vtable = new IMethod[rec.VTableMethodRefIdxs.Length];
    for (int i = 0; i < rec.VTableMethodRefIdxs.Length; i++)
    {
        int mridx = rec.VTableMethodRefIdxs[i];
        var mr = model.MethodRefs[mridx];
        vtable[i] = MatchMethod(iltype, mr.Name, mr.Parameters?.Length ?? 0);
        // a miss is tolerable for the structural comparison (reported); it is a
        // forward signal for sub-surface 2 (a Cecil-free load needs every slot).
    }
    return vtable;
}
```

This reuses `NeoAssemblyLoader.MatchMethod` (the S1 helper). Interface entries
reuse `ResolveTypeRefToIType` (the S1 helper). No new resolution strategy; the
same-AppDomain maps do all the work.

### D5. Gating: Neo-only, Legacy-neutral, additive

- The rebuild builder + the loader helper are `#if ENABLE_NEO_MODE`. The
  self-check cells are `#if ENABLE_NEO_MODE && DEBUG` (matching the existing
  `NeoStep25LoadExecCheck`).
- The Cecil / JIT init path is UNCHANGED (it is the reference + the comparison
  target). The builder does NOT install the rebuild on a live type in this
  slice (D1) -> zero shared-mutable-state hazard -> a Legacy regression is
  impossible (the builder compiles out of plain `Debug`).
- `ExecuteNeo`, the optimizer, the JIT, the Step-22 template mechanism, the
  Step-23 `.neo` format, and the Step-24 CLI are NOT modified.

## The parametrized-Run prerequisite (F-12) does NOT block this slice

The ship slice is a HOST-SIDE structural comparison (rebuild layout/VTable
structs from the record, compare to Cecil's). It does NOT invoke any method via
the `ILIntepreter.Run` shim. So F-12 (`NeoBoxReturnValue` handles primitive
returns only; the Step-6 Run shim is parameterless-only) and the
parameterless-only Run limitation are IRRELEVANT to this slice (same as S2's
structural-equivalence cell). DEFER (already recorded on STEP-25-PARTIAL).

## Risks / Trade-offs

- **[A rebuild that secretly re-reads Cecil instead of the record]** -> the
  mutation cell (D3.2) is the load-bearing proof: a rebuild that ignored the
  record would still equal Cecil and the mutated cell would FAIL. Mitigation:
  the mutation cell is mandatory in the self-check.
- **[The probe is too thin to make the comparison meaningful]** -> the probe
  must have 2+ instance fields of differing widths + a base virtual + an
  interface impl. If the existing probe lacks these, ADD a dedicated
  `NeoStep25S3Probe`. Confirm at apply (D3).
- **[The two record gaps (`naturalAlignment`, per-static-field offsets) hide a
  deeper incompleteness]** -> ACKNOWLEDGED: the gaps are documented (D2) and
  the rebuild RE-DERIVES `naturalAlignment` (proving the re-derivation matches
  Cecil) rather than skipping it. The per-static-field gap is honestly
  deferred (sub-surface 4). The SHIP slice proves completeness for the
  structures the record CARRIES, not a Cecil-free load.
- **[ILType is SHARED -- a builder edit risks the Legacy path]** -> LOW. The
  builder is Neo-only + opt-in (invoked only by the DEBUG self-check); it never
  replaces the Cecil init in this slice. Mitigation: the NeoStep smoke (215+
  green) + a plain-`Debug` build (the builder compiles out).
- **[Same-AppDomain hides the cross-AppDomain hash bug]** -> ACKNOWLEDGED +
  INTENTIONAL: this slice does NOT exercise cross-AppDomain (sub-surface 3 is
  deferred). The slice's value is the ILType-side completeness proof, NOT the
  standalone-loader proof (which needs sub-surfaces 2+3). Recorded honestly.

## Migration Plan

None. The change is additive and Neo-only. Rollback = revert the builder + the
loader helper + the new self-check cells; no shared code depends on them (the
builder is opt-in; the Cecil path is the only init path in this slice).

## Open Questions

- **OQ1:** Does the existing `TestCases/NeoStep25LoadProbe.cs` declare enough
  instance fields (of differing widths) + a base virtual + an interface impl to
  make the layout + VTable comparison meaningful? Default: if not, ADD a small
  `NeoStep25S3Probe` (2+ fields + base virtual + interface). Confirm at apply.
- **OQ2:** Should the rebuild also compare the `neoVTableSlotKeys` (re-derived
  from each slot's `SignatureString`) against the live keys, or only the
  `IMethod[]` slots? Default: compare BOTH (the slot-key map drives
  `TryGetNeoVTableSlot`; a key drift would mis-route interface dispatch in a
  future Cecil-free load). Confirm at apply.
- **OQ3:** For sub-surface 5 (CLR registration), is there a clean
  `Assembly.LoadFrom(refPath)`-then-`appdomain`-registers seam, or does it need
  a new AppDomain CLR-registration entry? Default: probe at apply; ship IFF a
  <=1-call seam exists, else DEFER with the rest. Not a blocker for sub-surface 1.
