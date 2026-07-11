# Design: neo-step25-s3-cecil-free-load (S3-2 TRUE COMPLETION)

> Technical design for the Cecil-free AppDomain load. Grounded in the dump-gate
> reproduced on HEAD `de0ef01c`, line-cited. Legacy is the REFERENCE, not a target.

## Context

S1 (neo-step25-runtime-loader) proved a deserialized `.neo` executes via
`ExecuteNeo` same-AppDomain. S2 extended to generic methods. S3-partial
(neo-step25-s3-full-decoupling) shipped the ILType layout + Neo VTable rebuild
from `NeoTypeDefRecord` as a PURE-DATA proof -- deliberately NOT installed.
S3-5 (neo-step25-s3-clr-registration) registered host CLR refs via `Assembly.
LoadFrom`. The TRUE AOT scenario -- a `.neo` loaded into a FRESH ILRuntime
AppDomain with NO Cecil `TypeDefinition`s -- is OPEN. S3-2 closes it.

## The dump-gate verdict (binding -- the Cecil-free load boundary)

### D-GATE 1. The Cecil-free load boundary: same-process-fresh-AppDomain is NOT a shortcut

The planning-context hypothesized a "same-process fresh-AppDomain" path where
refs resolve via host CLR + `.neo` tables with NO cross-AppDomain re-resolution.
**This is FALSE.** The hashes are AppDomain-instance-local, not process-local:

- `ILType.GetHashCode` (`ILType.cs:2714-2719`) + `ILMethod.GetHashCode`
  (`ILMethod.cs:1450-1455`) are IDENTITY-based: `System.Threading.Interlocked.Add(
  ref instance_id, 1)` against a process-global `static instance_id` counter
  (`ILType.cs:79-81`; `ILMethod.cs:69`).
- The deserialized `OpCodeR[]` bodies carry token operands baked at JIT time by
  `GetTypeTokenHashCode(token)` (`ILMethod.cs:1237-1256`): `t.GetHashCode()` for
  IL types (`:1250`), `token.GetHashCode()` for CLR types (`:1253`).
- At execution, `ExecuteNeo` resolves via `appdomain.GetMethod(ins.TokenInteger)`
  (`ILMethod.cs:586`) / `appdomain.GetType((int)(ins.TokenLong>>32))`
  (`ILMethod.cs:613`) -> `AppDomain.GetType(int hash)` (`AppDomain.cs:1429-1436`),
  a **pure hash lookup with NO fallback** (`mapTypeToken.TryGetValue(hash,...)`,
  else return null).
- `mapTypeToken` / `mapMethod` are PER-`ILRuntime.Runtime.Enviorment.AppDomain`-
  INSTANCE fields (`AppDomain.cs:62-63`), populated at Cecil-load + JIT time in
  the COMPILING AppDomain (`AddType` at `AppDomain.cs:662-667`; `mapMethod` at
  `AppDomain.cs:1724,1834,1840,1842`).

A fresh ILRuntime AppDomain B (same process) has its OWN empty `mapTypeToken`/
`mapMethod`; B's freshly-built ILTypes mint NEW identity hashes. The compile-time
hashes baked into the `.neo` NEVER resolve in B. **The cross-AppDomain token-hash
re-resolution (sub-surface 3, APPROACH 1) is a HARD prerequisite for sub-surface
2. S3-2 and S3-3 are INSEPARABLE. SHIP together.**

This is consistent with S1's recorded finding ("The cross-AppDomain token problem
is REAL (identity-based hashes)" -- neo-step25-runtime-loader/planning-context.md
:188) and S3-partial's sub-surface-3 verdict (design.md:31).

### D-GATE 2. What `LoadAssembly` requires + the `.neo` bypass

`AppDomain.LoadAssembly(Stream stream)` (`AppDomain.cs:639-642`) delegates to
`LoadAssembly(stream, null, null)` (`:641`) -> `LoadAssembly(stream, symbol,
symbolReader)` (`:651`):

```
var module = ModuleDefinition.ReadModule(stream);   // Cecil, :653
if (symbolReader != null && symbol != null)
    module.ReadSymbols(...);                         // :656-658
InitializeFromModule(module);                        // :659
```

`InitializeFromModule(module)` (`AppDomain.cs:669-719`):
- `loadedModules.Add(module)` (`:671`) -- the Cecil module is stored.
- Iterates `module.GetTypes()` (`:686`) -- Cecil type enumeration.
- For each non-primitive Cecil `TypeDefinition t`: `new ILType(t, this)`
  (`:690`, ctor at `ILType.cs:1224`) + `AddType(type)` (`:692`).

`AddType(type)` (`AppDomain.cs:662-667`):
```
mapType[type.FullName] = type;                       // :664
mapTypeToken[type.GetHashCode()] = type;             // :665
mapTypeToken[type.TypeDefinition.GetHashCode()] = type;  // :666
```

**The `.neo` bypass:** a NEW `AppDomain.LoadNeoAssembly(NeoAssemblyModel model,
IReadOnlyList<string> hostClrRefPaths)` (Neo-only) that:
- Does NOT call `ModuleDefinition.ReadModule` (no Cecil stream).
- Does NOT add to `loadedModules`.
- For each `NeoTypeDefRecord` in `model.TypeDefs`: builds a live `ILType` via a
  Neo-only factory (D2), resolves refs, and registers in `mapType` +
  `mapTypeToken` under BOTH the fresh hash AND the recorded compile-time hash
  (D3).

### D-GATE 3. What an ILType ctor reads from Cecil that the record must substitute

The ILType ctor `ILType(TypeReference def, AppDomain domain)` (`ILType.cs:1224-
1230`) sets `typeRef = def` + calls `RetriveDefinitino(def)` (`:1236-1252`) which
sets `definition = def as TypeDefinition` (`:1250`). EVERY lazy initializer reads
Cecil objects:

| Init path | Cecil field read | Record substitute |
|-----------|------------------|-------------------|
| `InitializeBaseType` (`ILType.cs:1512-1573`) | `definition.BaseType` (`:1514`) + `appdomain.GetType(CecilTypeRef,...)` (`:1521,1540,1573`) | `BaseTypeRefIdx` -> TypeRef.Name -> `appdomain.GetType(name)` (D4) |
| `InitializeInterfaces` (`ILType.cs:1486-1511`) | `definition.Interfaces[i].InterfaceType` (`:1489,1494`) | `Interfaces[].InterfaceTypeRefIdx` -> TypeRef.Name (D4) |
| `InitializeFields` (`ILType.cs:2164+`) | `definition.Fields` | carried `Fields[]` (`PrimitiveOffset`/`ReferenceOffset`) + `TotalPrimitiveSize`/`TotalReferenceCount`; `naturalAlignment` RE-DERIVED (S3-partial proof, `ILType.cs:828-846`) |
| `InitializeMethods` (`ILType.cs:1693+`) | `definition.Methods` + `definition.HasMethods` | methods come from the `.neo` `MethodDefs` (resolved by name via `MethodRef`) -- the bodies are bound by `NeoAssemblyLoader.Attach` |

A Cecil-free ILType CANNOT use the ctor or ANY lazy init. It must set the fields
DIRECTLY (D2).

### D-GATE 4. Is the record complete? (the S3-partial gaps revisited for INSTALL)

S3-partial flagged two gaps; both are addressed or confirmed non-blocking for
INSTALL:
- **(a) `naturalAlignment` NOT carried** (`ILType.cs:99`): S3-partial PROVED the
  re-derivation matches Cecil (`ILType.cs:828-846`, the max natural size over the
  resolved own field types). INSTALL re-derives it the same way. NO record change
  needed.
- **(b) Per-STATIC-field offsets NOT carried** (only static TOTALS,
  `NeoAssembly.cs:168-169`): this is the `.cctor`-seeding concern (sub-surface 4).
  The capstone probe (`NeoStep25S3Probe`) has NO static fields + NO `.cctor`, so
  INSTALL of its INSTANCE layout + VTable is INDEPENDENT of this gap. STATIC
  fields on a Cecil-free type would get the total-static-size but not per-field
  offsets -> `StaticInstance` access is partial. SEQUENCE `.cctor` seeding + the
  per-static-field offsets (folded, sub-surface 4) -- the capstone does not need
  them.

**Record completeness for the SHIP slice (instance layout + VTable + interface
map + ref resolution + hash re-registration): COMPLETE.**

## Decisions

### D1. APPROACH 1 hash re-registration: record compile-time hashes + re-register

**Format (`.neo` Version bump, NeoAssemblyFormat.Version 1 -> 2):**
- Add a parallel `int[]` to each reference table, holding the compile-time
  identity hash:
  - `TypeRefHashes[]` (parallel to `TypeRefs`, `NeoAssemblyModel.TypeRefs`).
  - `MethodRefHashes[]` (parallel to `MethodRefs`).
  - (String-token hashes are NOT identity-based -- the string interner is keyed
    by string content, stable across AppDomains -- so NO StringRefHashes; the
    `jumptables`/switch targets use Cecil-array-hashes that are body-local and
    already carried. See D6.)
- `NeoAssemblyFormat.Version` -> 2. The reader rejects a V1 `.neo` for the
  Cecil-free load (Version guard); a V2 `.neo` still loads same-AppDomain (the
  hash arrays are additive, ignored by S1/S2/S3-partial).

**Serialization (NeoAssemblyWriter):** after the Cecil-load + force-compile in
the compiling AppDomain, for each TypeRef index, capture the RESOLVED IType's
`GetHashCode()` (the value `GetTypeTokenHashCode` would store); similarly each
MethodRef -> the resolved `IMethod.GetHashCode()`. Store verbatim. For an
unresolved ref (a skip), store -1 (the Cecil-free loader re-resolves by NAME;
-1 = "no hash to rebind").

**Load (Cecil-free):** after building each live ILType + resolving each ref by
NAME (D4), the loader does:
```
// Pseudocode -- the recorded hash -> the resolved live object
if (TypeRefHashes[i] != -1) {
    var resolved = ResolveTypeRefByName(model, i);   // D4
    if (resolved != null) mapTypeToken[TypeRefHashes[i]] = resolved;
}
// same for MethodRefHashes -> mapMethod
```
This makes the compile-time hash (baked into the bodies) resolve in B's maps to
B's freshly-built objects. NO body mutation; NO `GetHashCode` semantics change
(Approach 2 REJECTED); the identity-uniqueness of the counter is PRESERVED (the
recorded hash is just an ALIAS key added to the map, alongside the fresh hash).

### D2. Install-the-rebuild ILType factory (S3-partial D1 lifted)

A Neo-only factory `ILType.CreateFromNeoRecord(NeoTypeDefRecord rec,
NeoAssemblyModel model, AppDomain domain)` that builds a live ILType WITHOUT the
Cecil ctor, setting the fields the Cecil lazy-inits would compute -- DIRECTLY:

- `typeRef` / `definition`: NULL (no Cecil). The factory sets a Neo-only flag
  `isNeoAotType` (mirrors `ILMethod.isNeoAotBody`, `ILMethod.cs:51`). Every Cecil-
  reading property (`TypeReference`, `TypeDefinition`, `GenericParameters`, etc.)
  on a Cecil-free ILType either (a) is unreachable for the capstone probe
  (non-generic, no nested generics), OR (b) gets a Neo-only guard that throws a
  descriptive `NotSupportedException` ("Cecil-free ILType: Cecil property <X> not
  available") -- NEVER silently returns null/wrong (D5 risk).
- `FullName`: from the TypeRef table (`model.TypeRefs[rec.TypeRefIdx].Name`).
- Instance layout: `totalPrimitiveSize`, `totalReferenceCnt`,
  `fieldOffsets[]` (from `rec.Fields[]`), `fieldTypes[]` + `fieldMapping` (by
  field name from FieldRef table). `naturalAlignment` RE-DERIVED (the S3-partial
  `NaturalSizeOfFieldType` path, `ILType.cs:828-846`).
- `baseType`: resolved by NAME from `rec.BaseTypeRefIdx` -> `appdomain.GetType(
  name)` (D4). For the capstone (`NeoStep25S3Base`, an IL type in the same `.neo`)
  this resolves via the `.neo` TypeDef table; for `System.Object` etc. via the
  BCL CLR fallback.
- `interfaces[]`: resolved by NAME from `rec.Interfaces[].InterfaceTypeRefIdx`
  (D4).
- `methods` / `constructors`: populated from the `.neo` `MethodDefs` whose
  `MethodRef.DeclaringType == this.FullName` -- the methods are built (their
  `CompiledFrame` is populated by `NeoAssemblyLoader.Attach` post-factory). The
  factory creates the `ILMethod` shells (name + param count from the MethodRef);
  `Attach` binds the bodies.
- Neo VTable + interface map: INSTALLED from `rec.VTableMethodRefIdxs` (resolved
  to live `IMethod[]` via `NeoAssemblyLoader.ResolveVTableFromRecord`, the S3-
  partial helper) + `rec.Interfaces[]` (resolved interface types + carried
  `VTableOffset`/`MethodSlotKeys`/`ClassSlotRemap`). The slot-key map re-derived
  from each slot's `SignatureString` (S3-partial D2).

This is exactly S3-partial's `RebuildFromNeoRecord` (`ILType.cs:808-882`) -- but
INSTALLING on a fresh ILType instead of returning a comparison struct. The S3-
partial completeness proof (structural-equivalence + mutation cells) DE-RISKS this
directly.

### D3. The Cecil-free load entry

`AppDomain.LoadNeoAssembly(NeoAssemblyModel model, IReadOnlyList<string>
hostClrRefPaths)` (Neo-only, `#if ENABLE_NEO_MODE`):

1. **Host CLR ref registration:** for each `hostClrRefPaths`, `System.Reflection.
   Assembly.LoadFrom(path)` (best-effort try/catch, the S3-5 pattern). Puts the
   host CLR assemblies into `System.AppDomain.CurrentDomain` so `GetType(string)`
   CLR fallback (`AppDomain.cs:1068`) resolves their types as `CLRType` -- NOT
   `LoadAssembly(refStream)` (which shadows as `ILType`, the S3-5 Q1.2 finding).
2. **Two-pass type build** (to handle base-type / interface forward references
   within the `.neo`):
   - Pass 1: for each `rec` in `model.TypeDefs`, `ILType.CreateFromNeoRecord(rec,
     model, this)` (D2), register in `mapType[fullName]` (so pass 2 + ref
     resolution find it). Base/interface resolution is DEFERRED (set null + a
     "needs init" flag).
   - Pass 2: resolve each type's `baseType`/`interfaces` by NAME (now all `.neo`
     types are in `mapType`) + finalize VTable/interface-map (which reference the
     resolved base ILType's VTable). Install.
3. **Hash re-registration (D1):** for each TypeRef/MethodRef with a recorded
   hash != -1, resolve by NAME (D4) and rebind in `mapTypeToken`/`mapMethod`
   under the recorded hash.
4. **Bind bodies:** `NeoAssemblyLoader.Attach(this, model)` (the S1/S2 path) --
   matches each `NeoMethodDefRecord` to a live `ILMethod` (now built Cecil-free
   in pass 1) + populates `CompiledFrame` via `InitCodeBodyFromNeo`.

### D4. Name-based ref resolution on the Cecil-free side

A Cecil-free loader has NO Cecil `TypeReference`/`MethodReference` to feed
`appdomain.GetType(CecilTypeRef, ...)`. It resolves by NAME from the `.neo` ref
tables (reusing the S1/S2/S3-partial helpers, which ALREADY resolve by name):
- **IL types:** `model.TypeRefs[idx].Name` -> `appdomain.LoadedTypes[name]` (the
  `.neo` types built in pass 1) -> `ILType`.
- **CLR types:** `model.TypeRefs[idx].Name` -> `appdomain.GetType(name)` (the
  CLR fallback, `AppDomain.cs:948+`), which (post step-1 `Assembly.LoadFrom`)
  scans `System.AppDomain.CurrentDomain.GetAssemblies()` (`AppDomain.cs:1068`) ->
  `CLRType`.
- The IL/CLR discrimination reuses the S1/S2 try-both order (`NeoAssemblyLoader.
  ResolveTypeRefToIType`, `NeoAssemblyLoader.cs:265-276`: LoadedTypes first, then
  `GetType(fullName)`). The `NeoTypeRefKind` byte (`NeoAssembly.cs:252-256`) is a
  HINT, not load-bearing (round-2 robustness, unchanged from S1).
- **Method refs:** `model.MethodRefs[idx]` -> the live `ILMethod` matched by name
  + param count on the declaring type (the S1 `MatchMethod` /
  `ResolveVTableFromRecord` pattern).

### D5. The Cecil-free ILType's Cecil-property surface

A Cecil-free ILType has `definition == null` + `typeRef == null`. Properties that
read them must be guarded. The capstone probe exercises only the
Neo-data-structure surface (`fieldOffsets`, `neoVTable`, `neoInterfaceMap`,
`BaseType`, `Implements`, `GetMethods`, `GetConstructors`, `Instantiate`,
`GetFieldIndex`) -- all set DIRECTLY by the factory, none read Cecil. Cecil-only
properties (`TypeDefinition`, `TypeReference`, `GenericParameters`,
`HasGenericParameters`, `IsEnum`-via-Cecil, the reflection `ILRuntimeType`) get a
Neo-only guard: `#if ENABLE_NEO_MODE` `if (isNeoAotType) throw new
NotSupportedException("Cecil-free ILType: <prop> not available");` `#endif`. This
NEVER silently returns wrong data (a fallback-null would mis-execute); it fails
loudly so a future caller is unambiguous. The capstone probe avoids these.

### D6. Why string-token hashes + switch-target hashes need NO recording

- **String tokens** (ldstr): the string interner (`AppDomain` string table) is
  keyed by string CONTENT (stable across AppDomains). The deserialized `TokenLong`
  for a ldstr is the string's content-hash, recomputed the same way in B. NO
  identity re-registration needed. (Confirm at apply: the exact ldstr token path
  in `ExecuteNeo` -- if it resolves via a content-keyed map, no work; if via an
  identity-keyed map, record the hash too. Default: content-keyed, no work.)
- **Switch targets** (`SwitchTargets`, `NeoMethodDefRecord`): `Dictionary<int,
  int[]>` keyed by stable Cecil-array-hashes that are LOCAL to the method body
  (the serialized `KeyValuePair<int,int[]>[]` carries these verbatim, rebuilt by
  `RebuildSwitchTargetsFromNeo`, `ILMethod.cs:888`). Body-local -> no cross-
  AppDomain concern.
- **Static-field tokens** (`GetStaticFieldIndex`): the capstone probe has NO
  static fields, so this path is unexercised. SEQUENCE with sub-surface 4.

### D7. The capstone + adversarial gate (binding -- green smoke does NOT prove it)

Host-side self-check `NeoStep25CecilFreeLoadCheck` (`#if ENABLE_NEO_MODE && DEBUG`,
mirrors `NeoStep25LoadExecCheck`), driven via an `ILRuntimeTestCLI` hook. Uses the
S3 probe (`TestCases/NeoStep25S3Probe.cs`):

1. **Compile (AppDomain A):** `new NeoCompiler().Compile(testCasesDll, refs, msA)`
   -> `modelA` (a V2 `.neo` with recorded hashes). The compiler Cecil-loads
   `TestCases.dll` in A; `NeoStep25S3Probe` is in `mapType`/`mapTypeToken` of A.
2. **Load Cecil-free (fresh AppDomain B):** `var domainB = new AppDomain();`
   `domainB.LoadNeoAssembly(modelA, hostClrRefs);` -- B has NO Cecil module, B's
   `mapType`/`mapTypeToken` populated PURELY from `modelA`.
3. **Execute + assert (the capstone):** `domainB.Invoke("TestCases.NeoStep25S3Probe",
   "SomeMethod")` -> assert the result EQUALS the known-expected value (computed
   independently). This exercises field read (`FInt`/`FLong`), virtual dispatch
   (`BaseVirtual` override), and interface dispatch (`INeoStep25S3Iface.
   IfaceMethod`) -- all on Cecil-free ILTypes.

**Adversarial mutation probe (the load-bearing proof):**
- **Cell M1 (body-mutation):** deserialize `modelA` into `model2`, MUTATE a
  `Ldc_I4` constant in `model2.MethodDefs[k].NeoExecuteBody[i].TokenInteger` (or
  `Operand`) BEFORE `LoadNeoAssembly`, load into B2, invoke -> assert the result
  is the MUTATED value (not the Cecil/JIT value). A Cecil-free load that secretly
  fell back to Cecil would yield the unmutated value -> FAIL -> caught.
- **Cell M2 (layout-mutation):** mutate `model2.TypeDefs[j].Fields[m].
  PrimitiveOffset` BEFORE load, load into B3, read the Cecil-free ILType's
  `fieldOffsets[m]` -> assert it EQUALS the mutated offset (not Cecil's). A
  factory that ignored the record would yield Cecil's offset -> FAIL -> caught.

A green capstone WITHOUT the mutation cells is INSUFFICIENT (the record is built
from Cecil's values, so a Cecil-fallback could pass). The two mutation cells are
mandatory.

### D8. Gating: Neo-only, Legacy-neutral, additive

- All new code (`CreateFromNeoRecord`, `LoadNeoAssembly`, the recorded-hash arrays
  + writer/reader edits) is `#if ENABLE_NEO_MODE`. Under plain `Debug` it compiles
  out -> Legacy byte-identical.
- The `.neo` Version bump is ADDITIVE + backward-compatible: the same-AppDomain
  S1/S2/S3-partial path ignores the recorded hashes (uses the live maps); a V1
  `.neo` is rejected ONLY by the Cecil-free loader (Version guard), still loads
  same-AppDomain.
- The Cecil ctor + ALL lazy inits are UNCHANGED (the reference + same-AppDomain
  path). The Cecil-free factory is a SEPARATE construction path.
- `ILType.GetHashCode` / `ILMethod.GetHashCode` UNCHANGED (Approach 2 REJECTED).
- `ExecuteNeo`, the optimizer, the JIT, Step-22/23/24, the standalone CLI, the
  Legacy `ExecuteR` UNCHANGED.

## Risks / Trade-offs

- **[A Cecil-free load that secretly re-reads Cecil / shares A's maps]** -> D7's
  two mutation cells (body + layout) are the load-bearing proofs. A fresh
  AppDomain B is constructed with NO Cecil module; the mutation cells assert B's
  behavior reflects the `.neo`, not Cecil. Mitigation: both cells mandatory.
- **[APPROACH 1 recorded-hash drift]** -> the loader re-registers the RESOLVED
  (by-name) ref under the recorded hash. A recorded hash whose name does not
  resolve in B is a -1 (skip); the capstone's probe uses only refs that resolve
  (IL types in the same `.neo` + BCL). A stale recorded hash (wrong value) is
  caught by the capstone's dispatch assertions (a misregistered slot -> wrong
  dispatch -> assertion fails). Mitigation: the capstone exercises virtual +
  interface dispatch.
- **[ILType is SHARED -- a factory edit risks Legacy]** -> LOW. The factory is a
  SEPARATE construction path; the Cecil ctor + lazy inits are unchanged. The
  Cecil-free ILType sets fields directly. Mitigation: NeoStep smoke (223+ green)
  + plain-`Debug` build (factory compiles out) + the S1/S2/S3-partial regression.
- **[The Cecil-property guards (D5) hide a needed field]** -> the guards throw
  loudly (`NotSupportedException`), never silently null. The capstone probe is
  curated to avoid them. A future caller hitting one is an unambiguous signal,
  not a silent mis-execution.
- **[The Version bump breaks the same-AppDomain V1 roundtrip]** -> the recorded
  hashes are ADDITIVE; the V1 same-AppDomain path ignores them. Mitigation:
  `NeoStep25LoadExecCheck` (28/28) regression + NeoStep 223+.
- **[Base-type forward-reference ordering]** -> the two-pass build (D3) handles
  intra-`.neo` base/interface references. A base type NOT in the `.neo` (a BCL /
  host-CLR base) resolves by name in pass 2. Mitigation: the capstone's base is
  `NeoStep25S3Base` (in the same `.neo`).

## Migration Plan

None. The change is additive and Neo-only. Rollback = revert the factory +
`LoadNeoAssembly` + the recorded-hash arrays (writer/reader/format-Version) + the
self-check + its CLI hook + any probe tweak. No shared code depends on the new
path (the Cecil ctor is the only construction path used by Legacy + same-AppDomain
S1/S2/S3-partial). A V1 `.neo` remains valid same-AppDomain.

## Open Questions

- **OQ1 (string-token path):** does `ExecuteNeo`'s ldstr resolution use a content-
  keyed map (no recording needed, D6) or an identity-keyed one? Default: content-
  keyed (confirm at apply by reading the ldstr case in `ExecuteNeo`); if identity-
  keyed, record string hashes too.
- **OQ2 (CLR-ref path on the Cecil-free side):** does the capstone probe need a
  non-BCL host CLR ref, or is it BCL-only? Default: the S3 probe is BCL-refs-only
  (no host-CLR dependency) -> `hostClrRefPaths` can be empty for the capstone; the
  `Assembly.LoadFrom` path is exercised but with no-op refs. Confirm at apply.
- **OQ3 (generic-instance on the Cecil-free path):** the capstone probe is non-
  generic. Should the Cecil-free loader reject / skip a generic-instance method
  ref (S2's T-identity-token concern) explicitly, or let it JIT-fallback?
  Default: explicit skip in the Cecil-free loader's `Attach` (a generic method on
  a Cecil-free type has no Cecil `MethodReference` for `ResolveVariableType` to
  re-resolve) -> reported in the load report. Confirm at apply.
- **OQ4 (the `mapTypeToken` double-registration):** `AddType` registers under
  BOTH `type.GetHashCode()` and `type.TypeDefinition.GetHashCode()` (`AppDomain.cs:
  665-666`). A Cecil-free ILType has `TypeDefinition == null` -> only the single
  fresh-hash + recorded-hash registrations. Is there any consumer that resolves
  via the `TypeDefinition.GetHashCode()` key? Default: no (the JIT, which uses
  `GetTypeTokenHashCode` -> `t.GetHashCode()`, not `TypeDefinition.GetHashCode()`);
  confirm at apply via a grep for `mapTypeToken[` writes/reads of the Cecil-hash
  form.
