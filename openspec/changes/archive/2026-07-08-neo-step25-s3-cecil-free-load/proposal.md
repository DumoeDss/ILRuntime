# Proposal: neo-step25-s3-cecil-free-load (S3-2 TRUE COMPLETION)

> The Cecil-free AppDomain load -- load a `.neo` into an ILRuntime AppDomain
> that has NOT Cecil-loaded the types, and EXECUTE it correctly. The true AOT
> deployment scenario. Scope-AWARE: the dump-gate (binding) decides the SHIP
> slice vs the SEQUENCE. Legacy is the REFERENCE.

## What this change is

S3-2 closes the hardest remaining AOT piece: a `.neo` loaded into a FRESH
`ILRuntime.Runtime.Enviorment.AppDomain` that has NO Cecil `TypeDefinition`s,
resolving types / methods / fields PURELY from the `.neo` tables, and executing
methods via `ExecuteNeo` with correct results.

S3-partial (neo-step25-s3-full-decoupling) shipped the ILType layout + Neo VTable
rebuild from `NeoTypeDefRecord` as a PURE-DATA completeness proof -- deliberately
NOT installed on a live type (D1). S3-2 INSTALLS that rebuild: it builds live
`ILType`s from `NeoTypeDefRecord`s (no Cecil ctor), populates the fresh
AppDomain's token maps so the deserialized bodies resolve, and runs.

## The dump-gate verdict (binding -- reproduced on HEAD `de0ef01c`)

### Q1. What `LoadAssembly` requires today + where the `.neo` bypass is

`AppDomain.LoadAssembly(Stream)` (`AppDomain.cs:639-642`) -> `LoadAssembly(stream,
symbol, symbolReader)` (`AppDomain.cs:651`) calls `ModuleDefinition.ReadModule(stream)`
(`AppDomain.cs:653`, **Cecil**) -> `InitializeFromModule(module)` (`AppDomain.cs:
659,669-719`). `InitializeFromModule` (`:684-695`) iterates `module.GetTypes()`
(Cecil) and constructs each `ILType` via `new ILType(t, this)` (`:690`) where `t`
is a Cecil `TypeDefinition`, then `AddType(type)` (`:692` -> `:662-667`) registers
it in `mapType` (by FullName) + `mapTypeToken` (by `type.GetHashCode()` AND
`type.TypeDefinition.GetHashCode()`).

The `.neo` bypass: a NEW load entry that takes a `NeoAssemblyModel` (not a Cecil
stream) + the host CLR ref assemblies, and for each `NeoTypeDefRecord` builds a
live `ILType` WITHOUT a Cecil ctor, registering it in `mapType` + `mapTypeToken`.
No Cecil module is read; `loadedModules` stays empty on this AppDomain.

### Q2. What an ILType ctor reads from Cecil that the record must substitute

The ILType ctor (`ILType.cs:1224-1230`) sets `typeRef = def` (a Cecil
`TypeReference`) + calls `RetriveDefinitino(def)` (`:1236-1252`) which assigns
`definition = def as TypeDefinition`. EVERY lazy initializer reads Cecil objects:
`InitializeBaseType` (`:1512-1573`) reads `definition.BaseType`; `InitializeInterfaces`
(`:1486-1511`) reads `definition.Interfaces[i].InterfaceType`; `InitializeFields`
(`:2164+`) reads `definition.Fields`; `InitializeMethods` (`:1693+`) reads
`definition.Methods`. AND each `appdomain.GetType(CecilTypeRef, this, null)` call
(`:1494,1521,1540,1573`) feeds a Cecil `TypeReference` into the resolver.

So a Cecil-free ILType CANNOT use the existing ctor or any lazy init path. The
record must substitute: (a) FullName (TypeRef table); (b) the layout RESULTS
(`TotalPrimitiveSize`/`TotalReferenceCount`/per-field `PrimitiveOffset`/
`ReferenceOffset` -- CARRIED); (c) `naturalAlignment` (RE-DERIVED, the S3-partial
proof); (d) the Neo VTable + interface map (CARRIED via `VTableMethodRefIdxs` +
`Interfaces[]`); (e) base type + interfaces (resolved by NAME from TypeRef).

**Record gaps (S3-partial flagged):** `naturalAlignment` re-derivation is PROVEN
by S3-partial. Per-STATIC-field offsets are NOT in the record (only the static
TOTALS) -- sub-surface 4, the `.cctor`-seeding concern, STAYS deferred (a
static-ctor-free probe makes the capstone independent of it).

### Q3. Token resolution: is the fresh-AppDomain load fundamentally S3-3 territory?

**YES -- and decisively so. This is the binding scope finding.** The deserialized
`OpCodeR[]` bodies carry token operands that were set at JIT-compile time via
`GetTypeTokenHashCode(token)` (`ILMethod.cs:1237-1256`): for IL types it stores
`t.GetHashCode()` (the ILType IDENTITY hash), for CLR types `token.GetHashCode()`
(the Cecil TypeReference hash). Both `ILType.GetHashCode` (`ILType.cs:2714-2719`)
and `ILMethod.GetHashCode` (`ILMethod.cs:1450-1455`) are IDENTITY-based -- a
process-global `static instance_id` counter (`ILType.cs:79-81`; `ILMethod.cs:69`).

At execution, `ExecuteNeo` resolves these via `appdomain.GetMethod(TokenInteger)`
(`ILMethod.cs:586`) / `appdomain.GetType(hash)` (`ILMethod.cs:613` -> `AppDomain.
GetType(int)` at `AppDomain.cs:1429-1436`, a **pure hash lookup with NO fallback**).
The maps `mapTypeToken` / `mapMethod` are PER-`ILRuntime.Runtime.Enviorment.
AppDomain`-INSTANCE (fields at `AppDomain.cs:62-63`), populated at Cecil-load +
JIT time in the COMPILING AppDomain.

So: a `.neo` compiled in AppDomain A has A's identity hashes baked into its
bodies; a FRESH AppDomain B has its OWN empty `mapTypeToken`/`mapMethod`, and B's
freshly-built ILTypes mint DIFFERENT identity hashes. The baked hashes NEVER
resolve in B. `GetType(int)` returns null; execution fails. This holds for a
fresh ILRuntime-AppDomain **even in the same process** (the planning-context's
"same-process, no cross-AppDomain re-resolution" optimism is WRONG -- the hashes
are AppDomain-instance-local, not process-local).

**The planning-context's "same-process" path does NOT exist as a shortcut.** The
cross-AppDomain token-hash re-resolution (sub-surface 3, APPROACH 1: record the
compile-time `GetHashCode()` per ref entry under a `.neo` Version bump; the loader
re-registers resolved refs under the recorded hash) is a HARD PREREQUISITE for any
Cecil-free fresh-AppDomain load. S3-2 and S3-3 are INSEPARABLE.

### Q4. Scope decision: SHIP the install-the-rebuild + Cecil-free load + hash re-resolution as ONE coherent change

**SHIP (the coherent slice):** the Cecil-free load into a fresh same-process
ILRuntime AppDomain, which REQUIRES the three inseparable parts:

1. **APPROACH 1 hash re-registration** (the prerequisite): extend the `.neo` to
   record the compile-time identity hash per TypeRef / MethodRef / (string) entry
   under a Version bump; the Cecil-free loader re-registers each RESOLVED ref
   under the recorded hash so the baked token operands resolve in B's maps.
2. **Install-the-rebuild ILType factory** (S3-partial D1 lifted): build a live
   ILType from a `NeoTypeDefRecord` (no Cecil ctor) -- install the layout +
   `naturalAlignment` (re-derived) + Neo VTable + interface map directly, set
   `baseType`/`interfaces` by NAME, register in `mapType`/`mapTypeToken`.
3. **The Cecil-free load entry**: `AppDomain.LoadNeoAssembly(model, hostClrRefs)`
   that iterates `model.TypeDefs`, builds each ILType, resolves refs (IL via the
   `.neo` TypeDef table; CLR via `Assembly.LoadFrom` host refs, the S3-5 pattern),
   re-registers under recorded hashes, then `NeoAssemblyLoader.Attach` binds bodies.

**SEQUENCE (NOT shipped here):** cross-PROCESS load (a `.neo` built by `ilrt_neoc`
in process P1 loaded in P2 -- a deployment scenario needing file-based ref
discovery, NOT a re-resolution concern since APPROACH 1's recorded hashes are
already process-independent). Multi-hotfix-assembly cross-refs (one IL hotfix
referencing another IL hotfix). The standalone-CLI Cecil-free load driving.

## The capstone + adversarial gate (binding)

**Capstone (a green smoke does NOT prove the gate):** compile a `.neo` of the S3
probe (`TestCases/NeoStep25S3Probe.cs` -- 3 instance fields + base virtual +
interface impl) in AppDomain A; load it into a FRESH Cecil-free AppDomain B; invoke
a method via `ExecuteNeo` -> correct result (field read + virtual dispatch +
interface dispatch all exercised).

**Adversarial mutation probe (the load-bearing proof):** the S3-partial discipline
applied at the LOAD level. Two INDEPENDENT mutations on a fresh `model2` BEFORE the
Cecil-free load, asserting the BUILT ILType diverges from the Cecil-built one
exactly where mutated:

1. **Body-mutation:** mutate a deserialized `Ldc_I4` constant in a
   `NeoMethodDefRecord.NeoExecuteBody` BEFORE load -> assert the Cecil-free
   execution yields the MUTATED value (proves ExecuteNeo runs the genuine `.neo`
   body, not a Cecil/JIT fallback).
2. **Layout-mutation:** mutate a `NeoTypeDefRecord.Fields[k].PrimitiveOffset`
   BEFORE load -> assert the Cecil-free ILType's rebuilt `fieldOffsets[k]` differs
   from Cecil's exactly at `k` (proves the Cecil-free ILType is built from the
   `.neo` record, not Cecil). Mirrors the S3-partial structural-equivalence cell
   but on a LIVE Cecil-free type.

A Cecil-free load that secretly fell back to Cecil (e.g. resolved via a shared
module, or used the compile-AppDomain's maps) would equal Cecil -> the mutation
cell FAILS -> the gate catches it.

## Goals / Non-Goals

**Goals:**
- A `.neo` loads into a FRESH Cecil-free ILRuntime AppDomain (no Cecil
  `ModuleDefinition` read on the load side) and executes methods correctly via
  `ExecuteNeo`.
- The `.neo` records compile-time identity hashes (APPROACH 1) under a Version
  bump; the Cecil-free loader re-registers resolved refs under the recorded hash.
- A live `ILType` is built from a `NeoTypeDefRecord` (no Cecil ctor); the S3-partial
  rebuild is INSTALLED (layout + VTable + interface map + re-derived
  `naturalAlignment`).
- IL refs resolve via the `.neo` TypeDef table; CLR refs resolve via `Assembly.
  LoadFrom` host CLR refs (the S3-5 pattern) + the BCL.
- The capstone + the two adversarial mutation cells (body + layout) pass.

**Non-Goals:**
- Cross-PROCESS load (P1-built `.neo` loaded in P2) -- SEQUENCED (deployment
  concern; APPROACH 1 hashes are already process-independent).
- Multi-hotfix-assembly cross-references (one IL hotfix referencing another IL
  hotfix). Deferred V1 limitation (unchanged from S3-5).
- Static `.cctor` seeding (sub-surface 4) -- the capstone probe is `.cctor`-free;
  per-static-field offsets stay deferred.
- Generic-method / generic-type instances on the Cecil-free path (S2's
  T-identity-token + cross-AppDomain generic-instance re-resolution stays JIT-
  fallback on the Cecil-free side; the capstone probe is non-generic).
- Any change to `ExecuteNeo`, the optimizer, the JIT, the Step-22 template
  mechanism, the Legacy `ExecuteR`, or `ILType.GetHashCode` semantics (Approach 2
  name-based-hash stays REJECTED -- identity-uniqueness is relied on by SHARED maps).

## SHIP vs SEQUENCE

| Slice | Decision | Why |
|-------|----------|-----|
| APPROACH 1 hash re-registration (sub-surface 3) | **SHIP** (inseparable) | Hard prerequisite for any Cecil-free fresh-AppDomain load (`GetType(int)` has no fallback). |
| Install-the-rebuild ILType factory (sub-surface 2) | **SHIP** | The Cecil-free load's type-construction path; S3-partial's completeness proof de-risks it. |
| Cecil-free `LoadNeoAssembly` entry (sub-surface 2) | **SHIP** | The load entry wiring the two above. |
| Static `.cctor` seeding (sub-surface 4) | **SEQUENCE** | Per-static-field offsets not in the record; `.cctor`-free probe makes the capstone independent. |
| Cross-PROCESS load | **SEQUENCE** | Deployment concern; APPROACH 1 hashes are already process-independent. |
| Multi-hotfix cross-refs | **SEQUENCE** | Deferred V1 limitation (unchanged). |
| Generic instances on Cecil-free path | **SEQUENCE** | S2 T-identity-token + cross-AppDomain generic-instance re-resolution. |

## Legacy / Neo gating

- All new code is `#if ENABLE_NEO_MODE`. Under plain `Debug` it compiles out ->
  Legacy byte-identical.
- The `.neo` Version bump (the recorded-hash arrays) is BACKWARD-compatible: the
  Cecil-load path (S1/S2/S3-partial, same-AppDomain) does NOT read the recorded
  hashes (it uses the live maps); a V1 `.neo` (no recorded hashes) is rejected by
  the Cecil-free loader (Version guard) but still loadable same-AppDomain.
- `ILType.GetHashCode` / `ILMethod.GetHashCode` are UNCHANGED (Approach 2 rejected).
- The Cecil `LoadAssembly(Stream)` path is UNCHANGED (the reference path + the
  same-AppDomain S1/S2/S3-partial path).

## Capability

`neo-optimizer` (the Step-25 AOT loader lives under this capability).

## Risks (high-level -- design.md has the file:line-cited detail)

- **[A Cecil-free load that secretly re-reads Cecil]** -> the two adversarial
  mutation cells (body + layout) are the load-bearing proofs.
- **[APPROACH 1's recorded-hash arrays drift from the live maps]** -> the
  Cecil-free loader re-registers the RESOLVED ref (looked up by NAME from the ref
  tables) under the recorded hash; the name-resolution + hash-rebind is the
  contract, exercised by the capstone's virtual/interface dispatch.
- **[ILType is SHARED -- a Cecil-free factory edit risks Legacy]** -> the factory
  is Neo-only + opt-in (the Cecil ctor + all lazy inits are unchanged); a
  Cecil-free ILType sets the layout/VT fields directly. Mitigation: NeoStep smoke
  (223+ green) + plain-`Debug` build (factory compiles out).
- **[The Version bump breaks same-AppDomain loads]** -> the recorded-hash arrays
  are ADDITIVE; the same-AppDomain loader ignores them. Mitigation: the S1/S2/S3-
  partial regression (NeoStep 223+ + NeoStep25LoadExec 28/28).
