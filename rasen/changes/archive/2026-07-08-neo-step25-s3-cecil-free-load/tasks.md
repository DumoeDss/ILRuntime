# Tasks -- neo-step25-s3-cecil-free-load (S3-2 TRUE COMPLETION)

> Implementation tasks for the Cecil-free AppDomain load + APPROACH-1 cross-
> AppDomain token re-resolution. The three inseparable parts ship together
> (design D-GATE 1): (a) the `.neo` recorded-hash arrays, (b) the Cecil-free
> ILType + ILMethod factories, (c) the `LoadNeoAssembly` entry. All engine
> edits Neo-only (`#if ENABLE_NEO_MODE`); the capstone self-check is
> `#if ENABLE_NEO_MODE && DEBUG`. Legacy is the reference.

## 1. `.neo` Version bump + recorded-hash arrays (APPROACH 1, D1)

- [x] 1.1 `NeoAssemblyFormat.Version` -> 2 (the reader rejects a prior-Version
      `.neo` for the Cecil-free load via a Version guard). Same-AppDomain
      loading (S1/S2/S3-partial) stays permissive (the V2 arrays are additive).
- [x] 1.2 Added `NeoTokenBinding[]` (flat {Hash, RefIdx} list -- a ref may
      appear under MULTIPLE baked hashes, so a flat list, not a parallel int[])
      to `NeoAssemblyModel` (`NeoAssembly.cs`): `TypeTokenBindings` +
      `MethodTokenBindings`. (Replaces the design's parallel-int[] sketch; the
      binding gate finding -- Cecil TypeReference/MethodReference.GetHashCode
      are ALSO identity-based -- generalized it. See ship-log.)
- [x] 1.3 `AppDomain.NeoTypeTokenSnapshot` / `NeoMethodTokenSnapshot` Neo-only
      read-only snapshots of mapTypeToken / mapMethod (so the writer records
      EVERY identity-hash key per resolved ref).
- [x] 1.4 `NeoAssemblyWriter.BuildTokenBindings`: AFTER CompileFresh (A's maps
      carry every baked hash), snapshot + match each (hash -> resolved) entry to
      its .neo ref index by NAME (avoids ImportReference's AssemblyResolution-
      Exception). Written as V2-additive trailing tables after the 7 indexed
      tables (the V1 header's 7 TableOffsets unchanged).
- [x] 1.5 `NeoAssemblyReader.ReadTokenBindings`: reads the 2 trailing tables;
      tolerates a stream that ends after the 7 V1 tables (a V1 .neo -- NEVER
      fatal). DECISION: single Version=2 + always-written trailing tables (the
      reader's EOF-tolerance makes a V1 stream still readable).

## 2. The Cecil-free ILType + ILMethod factories (D2, D5)

- [x] 2.1 `ILType.CreateFromNeoRecord(rec, model, domain)` (Neo-only): builds a
      live ILType WITHOUT the Cecil ctor (definition/typeRef null, `isNeoAotType`
      set). Pass 1 installs FullName + instance layout (`fieldTypes`,
      `fieldMapping`, `fieldOffsets`, totals, re-derived `naturalAlignment`) +
      method/constructor shells. `FinalizeFromNeoRecord` (pass 2) resolves
      base/interface by NAME + installs Neo VTable + interface map + field
      indices + firstCLRBase/Interface. Lifted from S3-partial's rebuild.
- [x] 2.2 `ILMethod.CreateFromNeoShell(...)` (Neo-only): builds a Cecil-free
      ILMethod shell (`def = null`, `isNeoAotShell` flag). The Cecil-reading
      properties (Name, HasThis, Parameters, ReturnType, SignatureString,
      IsConstructor, IsStatic, IsVirtual, GenericParameterCount) return the
      recorded shell data when the flag is set. Return type sourced from the
      MethodDef's new `ReturnTypeRefIdx` (the MethodRef table omits it).
- [x] 2.3 Guarded the ILType Cecil-reading properties (`FullName`,
      `HasGenericParameter`, `IsGenericParameter`, `IsValueType`, `IsInterface`)
      behind `isNeoAotType` + the lazy inits (`InitializeBaseType`/
      `InitializeInterfaces`/`InitializeFields`) no-op when the flag is set.
      Cecil ctor + ALL lazy inits UNCHANGED for the non-AOT path.

## 3. The Cecil-free load entry (D3, D4)

- [x] 3.1 `AppDomain.LoadNeoAssembly(model, hostClrRefPaths)` (Neo-only
      internal): does NOT call ModuleDefinition.ReadModule; does NOT add to
      loadedModules. Registers host CLR refs via `Assembly.LoadFrom` (best-
      effort; NEVER LoadAssembly(refStream)). Also initializes the BCL primitive
      types (a fresh Cecil-free AppDomain has no Cecil load -> InitializeFromModule
      never ran) via `EnsureNeoPrimitiveTypes`.
- [x] 3.2 Two-pass build: Pass 1 builds each `.neo` ILType via
      CreateFromNeoRecord + registers in `mapType[fullName]` + `mapTypeToken`;
      Pass 2 (`FinalizeFromNeoRecord`) resolves base/interface by NAME + installs
      VTable/interface-map/field-indices.
- [x] 3.3 `ReRegisterTokenBindings` (APPROACH 1): for each recorded NAME-based
      binding (Hash + FullName [+ MethodName + ParamCount]), re-resolve by NAME +
      rebind in `mapTypeToken` / `mapMethod` under the RECORDED hash. A method
      whose declaring Cecil-free ILType lacks it (an interface's abstract method,
      not in the .neo MethodDefs) gets a synthesized shell (the dispatch reads
      only its DeclearingType; the VTable provides the impl).
- [x] 3.4 `NeoAssemblyLoader.Attach(this, model)` binds bodies to the Cecil-free
      shells via MatchMethod (works on the shells: GetMethods returns them).

## 4. The capstone + adversarial gate (D7, binding)

- [x] 4.1 `NeoStep25CecilFreeLoadCheck` (`#if ENABLE_NEO_MODE && DEBUG`) via the
      `NeoStep25CecilFreeLoad` CLI hook. Compiles the S3 probe (CLOSURE: probe +
      base + interface) in A; loads Cecil-free into a FRESH `new AppDomain()` B;
      invokes `Compute()` via `domainB.Invoke` (after Instantiate) -> asserts
      EQUALS A's JIT result (155). Exercises field write/read + virtual dispatch
      + interface dispatch on the Cecil-free ILType. 5/5 cells PASS.
- [x] 4.2 M1 (body-mutation): mutate `Ldc_I4_S 100` (FLong) -> MUTATED in
      model2's Compute body BEFORE load -> assert the MUTATED-derived value (a
      Cecil-fallback yields the unmutated value). PASS.
- [x] 4.3 M2 (layout-mutation): mutate Fields[0].PrimitiveOffset in model3
      BEFORE load -> assert the Cecil-free ILType's fieldOffsets reflects the
      mutation (NOT Cecil's). PASS.
- [x] 4.4 CLI hook `NeoStep25CecilFreeLoad` added to `Program.cs`.

## 5. Build + regression gates (CRITICAL -- always `-f net8.0`)

- [x] 5.1 `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`
      (0 errors).
- [x] 5.2 `dotnet build TestCases/TestCases.csproj -c Debug` (0 errors; the S3
      probe gained a parameterless `Compute()` entry method).
- [x] 5.3 `NeoStep25LoadExec` capstone: 28/28 (S1/S2/S3-partial) UNCHANGED +
      `NeoStep25CecilFreeLoad` 5/5 (the S3-2 capstone + M1 + M2). 0 failed.
- [x] 5.4 `NeoStep` smoke (Debug_Neo + useRegister=true, filter `NeoStep`):
      **223/0/0** (unchanged -- additive; the probe's Compute is not counted by
      the NeoStep filter).
- [x] 5.5 Legacy-neutral: plain `Debug` build of ILRuntimeTestCLI = **0 errors**
      (all new code compiles out under plain Debug).

## 6. Docs + spec honesty

- [x] 6.1 Updated `.trae/documents/neo-deferred-items.md` STEP-25-PARTIAL row:
      S3-2 SHIPPED (Cecil-free load + APPROACH-1 re-resolution for the self-
      contained probe); sub-surface 4 (.cctor seeding + per-static-field offsets)
      + CLR base/interface resolution on the Cecil-free side + generic instances
      on the Cecil-free path + cross-PROCESS load + multi-hotfix cross-refs STAY
      deferred (honestly recorded, NOT promoted to met).
- [x] 6.2 Updated `.trae/documents/neo-handoff.md` HEAD + status line.
- [x] 6.3 Spec delta (`specs/neo-optimizer/spec.md`) is honest: the Cecil-free
      load SHIPS for the self-contained probe; the sequenced non-goals are
      recorded as DEFERRED. No promotion.

## Out of scope (SEQUENCED -- recorded, NOT implemented)

- Static `.cctor` seeding + per-static-field offsets (sub-surface 4 -- the probe
  is `.cctor`-free + static-field-free).
- CLR base/interface resolution on the Cecil-free path (the probe's base +
  interface are BOTH IL types in the same `.neo`; a CLR base/interface needs the
  CrossBindingAdaptor path which is V1-deferred -- the NEO-AOT-ADAPTOR-SKIP
  pattern).
- Generic-method / generic-type instances on the Cecil-free path (S2's
  T-identity-token + cross-AppDomain generic-instance re-resolution -- the probe
  is non-generic).
- Cross-PROCESS load (P1-built `.neo` loaded in P2 -- a deployment concern;
  APPROACH 1's recorded hashes are already process-independent).
- Multi-hotfix-assembly cross-references (deferred V1 limitation).
