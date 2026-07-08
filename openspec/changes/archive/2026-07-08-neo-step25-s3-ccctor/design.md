# Design: neo-step25-s3-ccctor (S3-4 TRUE COMPLETION)

> Technical design for the static `.cctor` seeding at Cecil-free AOT load.
> Grounded in the dump-gate reproduced on HEAD `b0041e74`, line-cited. Legacy is
> the REFERENCE, not a target.

## Context

S3-2 (`2026-07-08-neo-step25-s3-cecil-free-load`) shipped the Cecil-free AppDomain
load (sub-surface 2) + the cross-AppDomain APPROACH-1 hash re-registration
(sub-surface 3). It explicitly SEQUENCED sub-surface 4 -- the `.cctor` seeding +
the per-static-field offsets -- as a non-goal (design D-GATE 4b; the S3-2 capstone
probe declares no static fields + no `.cctor`). S3-4 closes sub-surface 4.

## The dump-gate verdict (binding)

### D-GATE 1. How `.cctor` is triggered today + why suppressed under Neo

The `.cctor` is triggered **lazily** on the Legacy path: when `ILType.StaticInstance`
is first accessed (the property getter `ILType.cs:186-214`), it constructs the
`ILTypeStaticInstance` (line 196) and, if not yet called + a `staticConstructor`
exists + the type is non-generic (or a generic instance), it calls
`appdomain.Invoke(staticConstructor, null, null)` (`ILType.cs:209`). A second lazy
site mirrors this in `InitializeMethods` (`ILType.cs:2105-2117`, gated on
`!appdomain.SuppressStaticConstructor`). The `staticConstructor` ILMethod is built
from the Cecil `MethodDefinition` flagged `IsConstructor && IsStatic` during method
enumeration (`ILType.cs:2035-2040`).

The trigger on first `StaticInstance` access is itself **demand-driven**: the
`StaticInstance` getter is entered only when a body executes a `Stsfld` / `Ldsfld`
on the type (the op handlers call `t.StaticInstance` at `ILIntepreter.Register.cs:
3297` / `:3324`).

**Why suppressed under Neo:** both sites (`ILType.cs:203-207` and `:2110-2113`)
carry the SAME stale comment: "TODO Step 7: Neo interpreter still lacks
Stfld_*/Ldfld_* case handlers ... Suppressing cctor invocation here unblocks Step 6
smoke tests; restore once Step 7 lands." **This rationale is OBSOLETE** -- Steps 7
(value-type fields) / 12 (frame-in-value-type + Move_Vt) / 13 (Box/Unbox) shipped
the specialized Stfld/Ldfld handlers years ago. But the suppression was never lifted
because the Cecil-loaded Neo path's capstone probes (`NeoStep*`) avoided static-
`.cctor` state. **This change does NOT lift the stale suppression on the Cecil ctor
path** (Legacy-neutral safety; that is a separate, broader concern). It seeds the
`.cctor` explicitly on the Cecil-free `LoadNeoAssembly` path instead, where the
S3-4 capstone lives.

### D-GATE 2. Is the `.cctor` a `.neo` MethodDef?

**YES.** The `.cctor` is a `MethodDef`, compiled into the `.neo`:

- `NeoCompiler.CompileCore` enumerates every type's `GetConstructors()` (which on
  an ILType includes the static `.cctor` -- built at `ILType.cs:2040`) into the
  per-type method list (`NeoCompiler.cs:289`), force-compiles each (`ilm.BodyRegister`
  at `:330`), and serializes them (`writer.Write(...)` at `:346`). A `.cctor` is
  non-generic, so it routes to `methods[]` (not the TemplateTable).
- The `NeoTypeDefRecord.StaticCtorMethodRefIdx` field is ALREADY recorded by
  `BuildStaticCtorRef` (`NeoAssemblyWriter.cs:941-950`): it scans
  `type.GetConstructors()` for the `IsStatic` ctor and returns its `IndexMethodRef`.
  It is written (`NeoAssemblyWriter.cs:406`, `:883`) and read
  (`NeoAssemblyReader.cs:248`). **But it is CURRENTLY UNUSED by any loader code**
  (grep for `StaticCtorMethodRefIdx` consumers: zero -- only the writer/reader
  touch it). So the `.cctor` MethodRef is in the record; the `.cctor` BODY is in
  the MethodDef table; the GAP is (a) RUNNING it at Cecil-free load and (b) the
  per-static-field offsets (next gate).

### D-GATE 3. Does `.cctor` seeding NEED per-static-field offsets?

**YES -- the per-static-field offsets are a HARD prerequisite, and the Cecil-free
ILType currently does NOT have them.** The static-field access path:

1. The JIT bakes `Stsfld`/`Ldsfld` operands via `appdomain.GetStaticFieldIndex(token,
   ...)` (`JITCompiler.cs:2281/2285`), which returns `(typeHash << 32) | fieldIdx`
   where `fieldIdx = iltype.GetFieldIndex(token)` (`AppDomain.cs:2078`, `ILType.cs:
   2396-2417`) -- for a static field this is `staticFieldMapping[name]`, a sequential
   static-field index.
2. At execution, `Stsfld` resolves the declaring type by the high-32-bit hash
   (`AppDomain.GetType((int)(ip->OperandLong >> 32))` at `ILIntepreter.Register.cs:
   3290` -- already handled by S3-2's hash re-registration) and the static field by
   the low-32-bit index via `t.StaticInstance.AssignFromStack((int)ip->OperandLong,
   ...)` (`ILIntepreter.Register.cs:3297`; `Ldsfld` analog at `:3324`).
3. The `ILTypeStaticInstance` stores statics as a `byte[] Primitives` (sized to
   `type.StaticTotalPrimitiveSize`) + `AutoList ManagedObjects` (sized to
   `type.StaticTotalReferenceCount`) under Neo (`ILTypeInstance.cs:50-60`). To
   locate a field within the `byte[]`, the per-static-field byte offset is needed
   (`type.GetStaticFieldOffset(idxStatic)` at `ILTypeInstance.cs:68`).
4. Those per-static-field offsets live in `ILType.staticFieldOffsets[]`
   (`ILType.cs:36`), populated by the Cecil `InitializeFields` static branch
   (`ILType.cs:2560-2601`) -- which the Cecil-free factory does NOT run
   (`CreateFromNeoRecord` returns early from `InitializeFields` at `ILType.cs:2493`,
   and only sets the static TOTALS at `:1292-1293`).
5. **Additionally**, the `ILTypeStaticInstance` ctor under Neo READS
   `type.TypeDefinition.Fields` (`ILTypeInstance.cs:62`) to replay `InitialValue`
   -- which is NULL on a Cecil-free ILType (a `NotSupportedException`-throwing
   property per S3-2 D5). So merely installing `staticFieldOffsets` is not enough;
   the static-instance ctor MUST take a Cecil-free branch that does not touch
   `TypeDefinition`.

Therefore the fix has THREE coupled parts, all required: (a) carry per-static-field
layout in the record (name + type + offsets), (b) install it on the Cecil-free
ILType, and (c) make the `ILTypeStaticInstance` ctor Cecil-free-safe. The `.cctor`
body itself (which writes statics via the same Stsfld token path) then runs
correctly once (a)-(c) are in place -- the baked Stsfld token resolves the type
via S3-2's hash re-registration and the static-field index via the installed
`staticFieldOffsets`.

> **Could the offsets be re-derived without the record?** The instance path
> RE-DERIVES `naturalAlignment` from the resolved field types (S3-2 D-GATE 4a,
> `ILType.cs:828-846`) because alignment is a pure function of field widths. But
> the per-static-field **byte offsets are NOT** a pure function of widths alone
> (the Cecil layout places primitives first, then references; an IL value-type
> static field spans both regions at `ILType.cs:2582-2591`). Re-deriving the
> EXACT same offsets Cecil produced would duplicate `InitializeFields`' placement
> logic -- error-prone and divergence-prone. **Carrying the offsets in the record
> (faithful, like the instance `Fields[]`)** is the robust choice and mirrors the
> instance path the S3-2 factory already installs.

### D-GATE 4. Scope: SMALL

SMALL. The `.cctor` body + MethodRef are already in the `.neo`. The seeding is one
`appdomain.Invoke(cctor, null, null)` per type after `Attach` (the Legacy call). The
format change is one parallel per-static-field layout array (a Version bump). The
factory + static-instance-ctor edits are guarded Cecil-free branches. All additive,
Neo-only. SHIP.

## Decisions

### D1. The `.neo` format extension (V2 -> V3): a parallel per-static-field layout

`NeoTypeDefRecord` gains:

```
public NeoFieldLayoutRecord[] StaticFields;   // parallel to the instance Fields[];
                                              // each entry = {FieldRefIdx, PrimitiveOffset,
                                              // ReferenceOffset} -- the SAME struct
                                              // already used for instance fields
                                              // (NeoAssembly.cs:196-201).
```

The existing `StaticTotalPrimitiveSize` / `StaticTotalReferenceCount` fields stay
(totals). `StaticCtorMethodRefIdx` stays (now consumed). The `.neo` Version bumps
to 3. The reader rejects a V2 `.neo` for the Cecil-free static-field path (Version
guard); a V3 `.neo` still loads same-AppDomain (the new array is additive, ignored
by the Cecil-init path). The writer serializes `StaticFields[]` from the
Cecil-computed `staticFieldOffsets` + `staticFieldTypes` (resolved to FieldRef by
name + type, the same `BuildFieldLayout` pattern the instance path uses). A type
with no static fields writes an empty array.

### D2. Factory: install the static layout + track the `.cctor`

`ILType.CreateFromNeoRecord` (`ILType.cs:1278-1370`), after the instance-layout
block, adds a static-layout block mirroring it:

- `staticFieldMapping[name] = i` from `StaticFields[i].FieldRefIdx` (by name).
- `staticFieldTypes[i]` resolved by name from the FieldRef's type (the same
  `ResolveNamedIType` helper used for instance fields at `:1315`).
- `staticFieldOffsets[i] = { PrimitiveOffset, ReferenceOffset }` from the record.
- `staticConstructor`: located from `rec.StaticCtorMethodRefIdx`. The factory
  ALREADY builds a `.cctor` ILMethod shell into `t.constructors` (the `.cctor` name
  match at `ILType.cs:1349`). D2 records it as `staticConstructor` too (set the
  private field), resolved from `constructors` by the `.cctor` name, OR directly
  from `StaticCtorMethodRefIdx` -> the matching shell. `staticConstructorCalled` is
  left FALSE (the seed runs it).

The field-index encoding (`GetFieldIndex` -> `staticFieldMapping[name]`,
`ILType.cs:2405`) works UNCHANGED once `staticFieldMapping` is populated -- so the
baked Stsfld token (the static-field index) resolves correctly.

### D3. The `ILTypeStaticInstance` Cecil-free-safe ctor

The Neo branch of the ctor (`ILTypeInstance.cs:50-74`) currently iterates
`type.TypeDefinition.Fields` to replay `InitialValue`. On a Cecil-free ILType that
property throws (S3-2 D5). D3 adds a `#if ENABLE_NEO_MODE` guard: if
`type.isNeoAotType`, the ctor skips the `TypeDefinition.Fields` loop entirely (a
`.neo` carries no raw `InitialValue` byte arrays; the `.cctor` is the initializer --
a field with a non-constant initializer is the `.cctor`'s job, already seeded). The
`byte[]`/`AutoList` sizing + per-field offset access (`GetStaticFieldOffset`) are
ALREADY driven by the type's static totals + `staticFieldOffsets`, which D2
installed -- no Cecil read needed.

### D4. Seed the `.cctor` at Cecil-free load

`LoadNeoAssembly` (`AppDomain.cs:719-792`) gains a final step (5), AFTER the
`NeoAssemblyLoader.Attach` body-binding step (4): for each built Cecil-free ILType,
if `rec.StaticCtorMethodRefIdx != -1`, resolve the live `.cctor` ILMethod (from
`iltype.staticConstructor`, set by D2 -- or by name `.cctor` on the type) and call
`appdomain.Invoke(cctor, null, null)` (the SAME call Legacy uses at `ILType.cs:209/
2114`). Each invoke is wrapped in the best-effort try/catch pattern (a `.cctor`
that throws is recorded as a skip, never fatal -- the additive contract; the static
state is left at default). A type whose `StaticCtorMethodRefIdx == -1` is skipped
(no `.cctor`).

The seed runs AFTER `Attach` so the `.cctor`'s `CompiledFrame` is populated (its
body comes from the `.neo`); running it before `Attach` would JIT-fallback (no AOT
body bound). The seed runs AFTER hash re-registration (step 3) so the `.cctor`'s
own Stsfld token operands resolve.

### D5. The capstone + adversarial gate (binding)

A dedicated probe (`TestCases/NeoStep25S3CctorProbe`): a top-level non-generic IL
class with (a) a `static int` field, (b) a `.cctor` that sets it to a known
non-zero constant, (c) a `ReadStatic()` method returning the field. BCL-refs-only
(matches the S3-2 capstone discipline). The host-side self-check
(`NeoStep25CecilFreeLoadCheck`, extended with a `.cctor` result struct or a new
sibling check) compiles the probe, Cecil-free-loads it, invokes `ReadStatic()`,
and asserts the result EQUALS the `.cctor`-set constant.

**Adversarial body-mutation cell (mandatory):** deserialize `modelA` into
`model2`, MUTATE the `.cctor` body's `Ldc_I4` constant (the value the `.cctor`
stores) in `model2.MethodDefs[k].NeoExecuteBody[i].TokenInteger` BEFORE
`LoadNeoAssembly`, load into a fresh AppDomain B2, invoke `ReadStatic()` -> assert
the result is the MUTATED constant. A Cecil-fallback (re-reading Cecil's `.cctor`)
yields the unmutated value -> FAIL. A default-zero read (the `.cctor` never ran)
also FAILS. Only a genuine execution of the deserialized `.cctor` body passes.

A green capstone WITHOUT the mutation cell is INSUFFICIENT (the record's `.cctor`
body + constant come from Cecil at serialize; a Cecil-fallback or default-read
passes a non-mutated capstone trivially).

### D6. Gating: Neo-only, Legacy-neutral, additive

- All new code (`StaticFields[]` record field + writer/reader, the factory static-
  install block, the `ILTypeStaticInstance` Cecil-free branch, the `LoadNeoAssembly`
  seed step, the probe, the self-check) is `#if ENABLE_NEO_MODE`. Under plain
  `Debug` it compiles out -> Legacy byte-identical.
- The `.neo` Version bump V2 -> V3 is ADDITIVE + backward-compatible: the same-
  AppDomain S1/S2/S3 path ignores `StaticFields[]` (the Cecil `InitializeFields`
  provides the offsets); a V2 `.neo` is rejected ONLY by the Cecil-free loader's
  Version guard for the static-field path (a V2 `.neo` with no static fields still
  Cecil-free-loads via the instance-only path -- the loader treats a missing
  `StaticFields[]` as "no static fields").
- The Cecil ctor + ALL lazy inits are UNCHANGED. The stale `.cctor` suppression at
  `ILType.cs:203-207` / `:2110-2113` is NOT touched (Legacy-neutral safety; lifting
  it on the Cecil path is a separate, broader concern tracked elsewhere).
- `ExecuteNeo`, the optimizer, the JIT, Step-22/23/24, the standalone CLI, Legacy
  `ExecuteR` UNCHANGED.

## Risks / Trade-offs

- **[A Cecil-free `.cctor` that silently runs the compile-AppDomain's `.cctor` or
  reads Cecil]** -> D5's body-mutation cell is the load-bearing proof. A fresh
  AppDomain B is constructed with NO Cecil module; the mutation cell asserts B's
  read reflects the `.neo`'s MUTATED `.cctor` constant, not Cecil's. Mitigation:
  the mutation cell is mandatory.
- **[Per-static-field offset divergence from Cecil]** -> the offsets are CARRIED
  (faithful, like instance `Fields[]`), not re-derived, so there is no divergence
  surface. Mitigation: carry, do not re-derive (D1/D2).
- **[The `ILTypeStaticInstance` Cecil-free branch hides a needed `InitialValue`**
  replay]** -> a `.neo` carries no raw `InitialValue` byte arrays (those are a
  Cecil-emit detail); a field with an initializer expression goes in the `.cctor`
  (which is seeded). A field with ONLY a `InitialValue` blob (e.g. a static
  `byte[]` with a RVA initializer) would NOT initialize -- this is a known
  limitation, SEQUENCED (the capstone probe uses a primitive static + a `.cctor`,
  not an RVA-initialized blob). Mitigation: the capstone probe shape + a noted OQ.
- **[The `.cctor` throws on the Cecil-free type]** -> best-effort try/catch records
  the skip; static state is left at default. The capstone's `.cctor` is trivial
  arithmetic (does not throw). Mitigation: additive contract (a throwing `.cctor`
  is a skip, never fatal).
- **[The Version bump breaks the same-AppDomain V2 roundtrip]** -> `StaticFields[]`
  is ADDITIVE; the V2 same-AppDomain path ignores it (Cecil `InitializeFields`
  provides the offsets). Mitigation: `NeoStep25LoadExecCheck` regression + NeoStep
  smoke.
- **[ILType / ILTypeInstance are SHARED -- a factory/ctor edit risks Legacy]** ->
  LOW. The factory is a SEPARATE Cecil-free construction path (S3-2 D2). The
  static-instance-ctor edit is a `#if ENABLE_NEO_MODE` guard on `isNeoAotType`.
  The Cecil ctor + lazy inits are unchanged. Mitigation: NeoStep smoke (green) +
  plain-`Debug` build (the new code compiles out).

## Migration Plan

None. The change is additive and Neo-only. Rollback = revert the `StaticFields[]`
record field + writer/reader + the factory static-install block + the
static-instance-ctor guard + the `LoadNeoAssembly` seed step + the probe + the
self-check. No shared code depends on the new path. A V2 `.neo` remains valid
same-AppDomain.

## Open Questions

- **OQ1 (RVA-initialized static fields):** a static field initialized via a Cecil
  `InitialValue` blob (e.g. a static `byte[]` / `decimal` constant baked as raw
  bytes) is NOT covered (the Cecil-free static-instance ctor skips the
  `InitialValue` replay; a `.neo` carries no raw blobs). Default: the capstone uses
  a primitive static + a `.cctor`; RVA-initialized statics are SEQUENCED. Confirm
  at apply whether the probe should also assert an RVA-static (likely NO -- keep
  the capstone focused on the `.cctor` seeding).
- **OQ2 (`.cctor` ordering):** should the seed run per-type in `.neo` TypeDef
  order, or be deferred to first `StaticInstance` access (the Legacy lazy model)?
  Default: eager at `LoadNeoAssembly` (the proposal's TRUE-COMPLETION is "runs when
  a `.neo` Cecil-free-loads"); a `.cctor` that depends on ANOTHER `.neo` type's
  static state resolves because all types are built + their `.cctor`s seeded in
  TypeDef order (cross-type static-init cycles are out of scope -- Legacy has the
  same caveat). Confirm at apply.
- **OQ3 (lifting the stale Cecil-path suppression):** should this change ALSO lift
  the `ILType.cs:203-207` / `:2110-2113` suppression on the Cecil-loaded Neo path
  (so a Cecil-loaded Neo type's `.cctor` runs lazily like Legacy)? Default: NO --
  that is a broader, separately-gated change (it would affect the NeoStep smoke
  suite + every Cecil-loaded Neo type); S3-4 seeds ONLY the Cecil-free path
  explicitly at `LoadNeoAssembly`. Confirm at apply (the stale comment should at
  least be updated to point at S3-4).
