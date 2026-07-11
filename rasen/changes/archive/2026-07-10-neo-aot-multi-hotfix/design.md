# Design: neo-aot-multi-hotfix (child 10)

> Cecil-free AOT (`.neo`) load of a MULTI-hotfix-assembly setup: one IL hotfix
> assembly references a TYPE in another IL hotfix assembly (cross-assembly IL-
> to-IL type references on the Cecil-free path). Wave: completion-3, child 10
> (the last AOT child). lead-6 ref: MEDIUM#6.

## Context

S3-2 (neo-step25-s3-cecil-free-load) shipped the Cecil-free load of a SINGLE
assembly's self-contained types into a fresh AppDomain. The probe + its base +
its interface were compiled into ONE `.neo` and loaded together — every cross-
type reference was INTRA-`.neo`, resolved during the two-pass build (Pass 1
registers all types in `mapType`; Pass 2 resolves base/interface by name; field
types resolve in Pass 1's `CreateFromNeoRecord` via `ResolveNamedIType`).

The gap (lead-6 MEDIUM#6, deferred V1 limitation recorded in S3-2 tasks.md item
6.1): **cross-assembly IL-to-IL refs** — a type in "AssemblyA" references a type
in "AssemblyB" (a field of type B, a method param of type B, a call to a B
method, `isinst`/`castclass` B), compiled to TWO `.neo` models and Cecil-free-
loaded into one fresh AppDomain.

## The reproduced gap (HEAD)

Two IL types (both in `TestCases.dll`, but PARTITIONED into two `.neo` models —
one per "assembly" — the closest feasible simulation of two distinct IL hotfix
assemblies, since the harness AOT-compiles from a single DLL):

- `NeoStep25MultiHotfixA` (the referencing type): a FIELD of type B, a method
  PARAM of type B, a CALL to a B method (`BEcho`), `isinst` B, `castclass` B.
- `NeoStep25MultiHotfixB` (the referenced type): a value field + `BEcho` +
  `BMagic`.

Compiled to `.neo-A` + `.neo-B`. Loaded Cecil-free in BOTH orders:

- **B-then-A (works on HEAD):** when A is built, B is already in `mapType` ->
  A's field type / param resolve by name.
- **A-then-B (FAILS on HEAD):** when A is built (`CreateFromNeoRecord` Pass 1),
  B is NOT yet loaded -> `ResolveNamedIType` returns null for the B field type
  -> `A.fieldTypes[BField-slot]` stays **NULL** silently. Pass 2
  (`FinalizeFromNeoRecord`) likewise resolves base/interface by name; a cross-
  assembly base/interface in the same shape stays null. At exec, a null field
  type means `Stfld`/`Ldfld` on that field cannot resolve the target type ->
  NRE/NIE or wrong dispatch. (Confirmed: HEAD yields `A.BField field type NULL`
  in the A-then-B cell; 3/5 cells pass, A-then-B + M1 fail.)

## Decisions

### D1. The fix: deferred cross-assembly re-resolution (name-aliased, idempotent)

Mirror the S3-2 name-based re-resolution pattern, but ACROSS `.neo` loads. A
Cecil-free AppDomain accumulates types across `LoadNeoAssembly` calls; a type
built in an EARLIER load may have NULL field/base/interface slots that a LATER
load makes resolvable. The fix:

- **Track** every Cecil-free ILType built by `LoadNeoAssembly`, paired with its
  originating `NeoTypeDefRecord` + `NeoAssemblyModel` (a new
  `AppDomain.neoAotBuilt` list, Neo-only, lazily allocated). The record is the
  source of truth for a Cecil-free type (its `fieldReferences[]` are null).
- **Re-resolve** on EVERY `LoadNeoAssembly`, AFTER this model's types are built +
  registered: for each tracked Cecil-free type, call the new
  `ILType.ReResolveCrossAssemblyRefs(model, rec)`, which re-resolves ONLY the
  still-NULL slots by name (`ResolveNamedIType` for fields; the
  `NeoAssemblyLoader.ResolveTypeRefToIType` path for base/interface + the
  clrbase-iface CrossBindingAdaptor install). Already-resolved slots are a fast
  no-op (per-slot null guards). A still-unresolvable slot stays null (the
  additive contract — best-effort, never fatal).
- **Recompute** the dependent indices when a base resolves: `fieldStartIdx` /
  `totalFieldCnt` (inherited-field indexing) + `firstCLRBaseType` /
  `firstCLRInterface` (the CLR-base chain), mirroring `FinalizeFromNeoRecord`.

This is idempotent (running it again after all refs resolve changes nothing) and
order-independent (both load orders converge once all referenced types are in
`mapType`). It reuses the existing name-resolution helpers verbatim — NO new
hash machinery, NO `.neo` format change, NO body mutation.

### D2. Why name-based, not hash-based (consistent with the cross-process finding)

The cross-process child (neo-aot-crossprocess) proved the APPROACH-1 recorded
hash is non-deterministic but IRRELEVANT: `ReRegisterTokenBindings` re-resolves
each ref **by NAME** and aliases the recorded hash. Cross-assembly refs are the
SAME mechanism — a name is resolvable in the accumulating AppDomain once the
referenced type is loaded. D1's re-resolution is the field/base/interface
analogue of `ReRegisterTokenBindings` (which covers method-call + type-test
token operands baked into bodies). Together they make BOTH the data-layout refs
(fields/base/interface) AND the body token refs (calls/isinst/castclass) resolve
cross-assembly.

### D3. The capstone + adversarial gate

`NeoStep25CecilFreeMultiHotfixCheck` (host-side, `#if ENABLE_NEO_MODE && DEBUG`),
driven via the `NeoStep25CecilFreeMultiHotfix` CLI hook. 5 cells:

- **A** (JIT reference): run `ACompute(5)` with `Setup(B(10))` via A's JIT ->
  pins the expected value (1031) so a "both-garbage" false pass is ruled out.
- **Compile** `.neo-A` + `.neo-B` (partitioned, V2).
- **B-then-A** load + `ACompute` -> assert == 1031.
- **A-then-B** load + `ACompute` -> assert == 1031 (the gap-exposing order;
  FAILS on HEAD).
- **M1** (body-mutation): mutate the `1000` constant (BMagic's value, which the
  optimizer inlines into ACompute/BEcho/BMagic) in an INDEPENDENT model2 BEFORE
  the Cecil-free load -> assert the MUTATED-derived value (2031). A Cecil-
  fallback yields the unmutated value -> FAIL. Searches ACompute (model A) +
  BEcho/BMagic (model B) for the constant (the optimizer may inline it into any
  of the three) and mutates the first hit.

A green capstone WITHOUT the M1 cell is insufficient (the record is built from
Cecil's values; a Cecil-fallback could pass the dispatch cells). M1 is
mandatory.

### D4. Gating: Neo-only, Legacy-neutral, additive

- All new code (`ReResolveCrossAssemblyRefs`, the `neoAotBuilt` list + its
  population/iteration) is `#if ENABLE_NEO_MODE`. Under plain `Debug` it
  compiles out -> Legacy byte-identical (confirmed: plain-Debug CLI = 0 errors).
- `LoadNeoAssembly` is itself Neo-only (`internal` under `#if ENABLE_NEO_MODE`),
  so the new re-resolution loop inside it is Neo-only transitively.
- The Cecil ctor + ALL lazy inits are UNCHANGED (the reference + same-AppDomain
  path). `ReResolveCrossAssemblyRefs` is a no-op on a non-AOT type (the
  `isNeoAotType` guard).
- The probe + check are additive (not counted by the NeoStep filter).

## Scope boundary (honest)

- **Simulated multi-assembly, not two distinct DLLs.** The harness AOT-compiles
  from a single `TestCases.dll`, so the two "assemblies" are two IL types in the
  same DLL, PARTITIONED into two `.neo` models at compile time (one `Compile`
  call per type-set). This is the closest feasible simulation and exercises the
  SAME load-time cross-model resolution a true two-DLL setup would (the loader
  has no notion of "assembly" for a Cecil-free type — it accumulates `.neo`-
  built types into one AppDomain by name). A true two-DIL-DLL AOT compile is a
  harness limitation, NOT a `.neo`/engine limitation (the `.neo` format + the
  name-based resolution are assembly-agnostic).
- **Load order is arbitrary (both orders covered).** The fix is order-
  independent; the capstone asserts BOTH orders converge.
- Out of scope: generic-instance cross-assembly refs (the S2 T-identity-token
  concern, child 8); cross-PROCESS multi-assembly (child 9 proved portability by
  construction); the static `.cctor` cross-assembly seeding (a type's .cctor
  referencing a static on another assembly's type — sub-surface 4 territory).

## Risks / Trade-offs

- **[A re-resolution that re-resolves a slot to a DIFFERENT type than the .neo
  intends]** -> the name is the only key; a name collision across two `.neo`
  models would alias. Mitigation: the capstone's dispatch + body-mutation cells
  catch a mis-resolution (wrong field type -> wrong dispatch -> assertion fails).
  Names are full-namespaced (collision is a user error, same as the single-
  assembly case).
- **[The re-resolution loop is O(N^2) across loads]** -> LOW (a hotfix deploy
  loads a handful of assemblies once; N is small; each pass is null-guarded and
  fast). Not a hot path.
- **[ILType.cs is SHARED]** -> the new method is a SEPARATE Neo-only method; the
  Cecil ctor + lazy inits are unchanged. Confirmed Legacy-neutral (plain-Debug 0
  errors; the method compiles out).

## Migration Plan

None. Additive + Neo-only. Rollback = revert `ReResolveCrossAssemblyRefs` +
the `neoAotBuilt` list + its population/iteration in `LoadNeoAssembly` + the
probe + the check + the CLI hook. No shared code depends on the new path.
