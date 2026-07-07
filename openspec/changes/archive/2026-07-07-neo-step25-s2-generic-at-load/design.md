# Design: neo-step25-s2-generic-at-load

> Technical design for the S2 slice (see proposal.md for the P1/P2 decision,
> the scope, and the deferral table). Grounded in the actual init / execution
> code on HEAD `cfbf8ff9`, line-cited. Legacy is the REFERENCE, not a target.

## Context

S1 proved a deserialized `.neo` `NeoMethodDefRecord` is executable by
`ExecuteNeo` for NON-GENERIC methods (`isNeoAotBody` dual-path). S2 extends the
AOT path to GENERIC methods: at load, reconstruct each `GenericMethodTemplate`
from the `.neo` `TemplateTable` and bind it to the generic DEFINITION's
`GenericMethodTemplateCache`, so a generic-instance call routes through Step-
22's `CloneAndPatch` (already wired) instead of the per-occurrence JIT.

The load-bearing fact that makes S2 tractable: the Step-22 hook is ALREADY in
place. `ILMethod.InitCodeBody` (`ILMethod.cs:720-762`) routes a generic-
instance `ILMethod` through `GenericMethodTemplateOps.TryInstantiate`
(`CloneAndPatch`) whenever `genericDefinition.GenericMethodTemplateCache !=
null` (`ILMethod.cs:731-738`). So S2 changes ONLY what populates that cache;
the execution path (`InitCodeBody`, `ExecuteNeo`, the Step-22 mechanism) is
UNCHANGED. This mirrors S1's "init-only, not an ExecuteNeo change" hinge.

## Decisions

### D1. P1 (parameterless wrapper) over P2 (parametrized probe entry)

The portfolio handoff recorded that the Step-6 `ILIntepreter.Run` entry is a
PARAMETERLESS-ONLY shim and asked whether S2 needs a parametrized probe (P2).
The dump resolves this as P1 -- a non-generic parameterless wrapper that calls
a generic method is sufficient:

- `ILIntepreter.Run(ILMethod method, object instance, object[] p)`
  (`ILIntepreter.cs:87-120`). The Neo arm (`:94-120`) sets up `neoFrame` from
  `method.CompiledFrame` (the ENTRY's struct size + return size + return ref
  region) and calls `ExecuteNeo(method, neoFrame, retDst, retRefBase, ...)`.
  Comment at `:95`: "Step 6 entry shim: only no-arg static methods are expected
  here." The entry's own args are NOT marshaled (parameterless).
- A generic call is an INTERNAL `Call` opcode in the wrapper's body. The
  wrapper `public static int WrapEchoInt() { return Echo<int>(42); }` is
  parameterless. When `ExecuteNeo` runs the wrapper and reaches the `Call`, it
  resolves the method token to a generic-instance `ILMethod` (via `appdomain.
  GetMethod` + `MakeGenericMethod`, `ILMethod.cs:1342`), allocates the callee's
  frame, and the callee's `BodyRegister` getter hits the Step-22 hook.
- S1's `MixedLocalsProbe` (`NeoStep25LoadProbe.cs:121-130`) ALREADY makes an
  internal call (`BumpRef(ref val)`) via this same parameterless Run shim and
  is green (S1 V2 capstone). A generic call is the SAME `Call`-opcode shape;
  the only difference is the resolved target is a generic instance. The
  generic args are baked into the wrapper's Call token at JIT-compile time of
  the wrapper -- they never cross the Run boundary.

=> P2 is NOT needed for S2. The capstone reuses `appdomain.Invoke(m, null)` on
parameterless wrappers. (A parametrized Run entry remains an S3 concern for
full ILType decoupling, where the ENTRY itself may need marshaled args.)

### D2. The loader consumes `model.Templates`; the Step-22 hook does the rest

S1's `NeoAssemblyLoader.Attach` iterates ONLY `model.MethodDefs`
(`NeoAssemblyLoader.cs:51`). S2 extends it to additionally iterate
`model.Templates`:

```
foreach (var trec in model.Templates)
{
    var mref = model.MethodRefs[trec.DefinitionMethodRefIdx];
    string typeFullName = mref.DeclaringType?.Name;
    if (!appdomain.LoadedTypes.TryGetValue(typeFullName, out var itype) ||
        !(itype is ILType iltype)) { skip("type not loaded"); continue; }
    // Match the open generic DEFINITION: name + parameter count +
    // GenericParameterCount (the open def has GPC > 0 && !IsGenericInstance).
    var def = MatchGenericDefinition(iltype, mref.Name,
                                     mref.Parameters?.Length ?? 0,
                                     genericParamCountFromRecord);
    if (def == null) { skip("generic def not matched"); continue; }
    var tpl = GenericMethodTemplateOps.BuildFromNeoRecord(
        def, appdomain, model, trec, resolveVariableType);
    if (tpl == null) { skip("template rebuild miss"); continue; }
    def.InitTemplateFromNeo(tpl);   // OVERWRITES any JIT-cached template
    report.Attached.Add(typeFullName + "." + mref.Name + "<T>");
}
```

The execution path is UNCHANGED: once `def.GenericMethodTemplateCache` is set,
the next `MakeGenericMethod(T)` instance's `BodyRegister` getter ->
`InitCodeBody` -> the Step-22 hook (`ILMethod.cs:731-738`) -> `TryInstantiate`
(`CloneAndPatch`). `ExecuteNeo` runs the resulting body. No `ExecuteNeo` /
optimizer / JIT change.

**Why a new setter (`InitTemplateFromNeo`) instead of `StoreGenericTemplate`.**
`ILMethod.StoreGenericTemplate` (`ILMethod.cs:1392-1404`) guards against
overwrite: `if (genericMethodTemplate != null) return;`. But the V2 capstone's
compile step (`NeoCompiler.CaptureTemplate`, `NeoCompiler.cs:277-310`)
ALREADY caches a JIT-captured template on the definition (it synthesizes a
capture-eligible instance and reads `capInstance.BodyRegister`, firing the
`InitCodeBody` capture hook -> `StoreGenericTemplate`). So at attach time the
definition's cache is ALREADY non-null (JIT-captured). The loader's AOT
template MUST replace it, so the setter bypasses the guard -- mirroring S1's
`InitCodeBodyFromNeo` (an AOT-specific init that overrides the JIT path). The
setter is Neo-only; `StoreGenericTemplate`'s guard stays intact for the JIT
capture path.

### D3. The Cecil-re-resolution scope hinge (what S2 covers vs defers)

`GenericMethodTemplateOps.DoCloneAndPatch` (`GenericMethodTemplate.cs:466-565`)
+ `BuildInitObjPrefix` (`:430-460`) consume several Cecil-typed fields. The
dump enumerates which are load-bearing for which generic-method shape:

| Field | Type | Consumer | S2 slice |
|-------|------|----------|----------|
| `TemplateBody` | `OpCodeR[]` | `DoCloneAndPatch :471` | from `.neo` (no Cecil) |
| `Patches[].CecilToken` | `object` (TypeRef/MethodRef) | `DoCloneAndPatch :479-497`; null SKIPS (`:485`) | `IsRefMoveFlag`/no-token only -> `null` (S3 for TypeToken/MethodToken) |
| `VariableTypes` | `TypeReference[]` | `BuildInitObjPrefix :442` (`vt.IsGenericParameter` + `instance.GetTypeTokenHashCode(vt)`) | re-resolve from `VariableTypeRefIdxs` via live Cecil module |
| `Addr` | `Dictionary<Instruction,int>` | `InitCodeBody` EH rebuild (`ILMethod.cs:807-830`) | null (S2 probe has no try/catch in the generic method) |
| `Symbols` | `Dictionary<int,RegisterVMSymbol>` | set on frame (`:559`); NOT read by `RunNeoBackHalf`/`ExecuteNeo` | null |
| `InitObjPrefixRegisters`/`Length`, `VarCnt`, `LocVarRegStart`, `TotalRegCnt`, `NeoCatchExRegFinal`, `StackRegisterCount`, `SwitchTargets` | scalars / `int[]` / pairs | `DoCloneAndPatch` + `RunNeoBackHalf` | from `.neo` (no Cecil) |
| `ConstrainedTypeTokens`/`MethodTokens` | `TypeReference[]`/`MethodReference[]` | consumed ONLY by `ExtractPatches` at capture (`:256-368`); NOT re-read at CloneAndPatch (patches already carry `CecilToken`) | null (S2 does not re-run `ExtractPatches`) |

**S2 COVERS** generic methods whose template (a) has NO T-identity token patches
(no `Box T` / `Unbox T` / `Unbox_Any T` / `Isinst T` / `Castclass T` /
`Newarr T[]` / `Stobj T` / `Ldobj T` / `Constrained T` / T-qualified callvirt)
-- the patch table is `IsRefMoveFlag`-only or empty, so `CecilToken = null`
(`DoCloneAndPatch` skips them at `:485`, and `TypeSpecializeNeoOpcodes` in the
back-half re-derives the is-ref flag); AND (b) has no try/catch (no `Addr`
needed). The probe's generic methods are chosen to fit this slice (a pure-
dataflow `T Echo<T>(T v)` and a small arithmetic variant).

**S2 DEFERS to S3:**
- T-identity token re-resolution. `GetTypeTokenHashCode` (`ILMethod.cs:1237-
  1256`) needs a Cecil `TypeReference` token (`appdomain.GetType(token, ...)`
  + `((TypeReference)token).Name`). Re-resolving a `TokenRefIdx` to a live
  Cecil `TypeReference` (esp. a `GenericParameter` or a `T[]`/`List<T>`) is
  feasible same-AppDomain but non-trivial; the clean AOT direction is a Cecil-
  FREE patch applier keyed on `GenericParamIdx` (compute the concrete hash
  directly from `instance.GenericArugmentsArray[k]`). Both belong with the
  cross-AppDomain work (S3).
- Generic methods WITH try/catch (need Cecil `Addr` -- per-instruction Cecil
  handle recovery, not feasible Cecil-free).
- Cross-AppDomain (the S2 `VariableTypes` re-resolution uses the live Cecil
  module, which a Cecil-free AppDomain lacks) + full `ILType` decoupling +
  Approach-1 token-hash re-resolution + `.cctor` seeding.

A template whose reconstruction hits a deferred case is SKIPPED (additive
contract -- the generic method keeps JIT). The skip is reported in the load
report. This is the same "a miss is a skip, not a fatal" contract S1 uses.

### D4. `BuildFromNeoRecord` -- the reconstruction helper

Mirrors `GenericMethodTemplateOps.StoreFromCapture` (`GenericMethodTemplate.cs:
383-404`) but takes a `NeoTemplateRecord` + the live definition + a
`resolveVariableType` closure (TypeRef idx -> Cecil `TypeReference`, provided
by the loader):

```
internal static GenericMethodTemplate BuildFromNeoRecord(
    ILMethod definition, AppDomain appdomain, NeoAssemblyModel model,
    NeoTemplateRecord rec, Func<int, TypeReference> resolveVariableType)
{
    var tpl = new GenericMethodTemplate();
    tpl.Definition = definition;
    tpl.TemplateBody = rec.TemplateBody;
    tpl.LocVarRegStart = rec.LocVarRegStart;
    tpl.TotalRegCnt = rec.TotalRegCnt;
    tpl.NeoCatchExRegFinal = rec.NeoCatchExRegFinal;
    tpl.StackRegisterCount = rec.StackRegisterCount;
    tpl.VarCnt = rec.VarCnt;
    tpl.InitObjPrefixLength = rec.InitObjPrefixLength;
    tpl.InitObjPrefixRegisters = rec.InitObjPrefixRegisters ?? Array.Empty<int>();
    tpl.SwitchTargets = RebuildSwitchTargets(rec.SwitchTargets); // pairs -> dict
    tpl.VariableTypes = ResolveVariableTypes(rec.VariableTypeRefIdxs, resolveVariableType);
    // S2 slice: patches carry NO Cecil token (IsRefMoveFlag / no-token only).
    // T-identity-token patches (TypeToken/MethodToken with a non-none
    // CecilTokenKind) -> the template is INCOMPLETE for AOT; mark it so the
    // loader skips the bind (the generic method keeps JIT).
    tpl.Patches = RebuildPatchesNoCecil(rec.Patches, out bool hasIdentityToken);
    if (hasIdentityToken) return null;   // S3
    tpl.Addr = null;          // S2 probe has no try/catch in the generic method
    tpl.Symbols = null;       // not read at CloneAndPatch / ExecuteNeo
    tpl.ConstrainedTypeTokens = null;  // only ExtractPatches reads these
    tpl.ConstrainedMethodTokens = null;
    return tpl;
}
```

`resolveVariableType` re-resolves a `VariableTypeRefIdx` to a Cecil
`TypeReference`: for an IL type, `iltype.TypeReference` / module lookup; for a
generic parameter, `definition.Definition.GenericParameters[k]`; for a CLR
type, `appdomain.LoadedModules[0].ImportReference(clrType.TypeForCLR)`. This
re-resolution is same-AppDomain (Cecil available). A miss returns null and the
template is skipped (additive contract).

**Open question OQ1:** does `BuildInitObjPrefix` need the FULL `VariableTypes`
array, or only the generic-param-typed entries? It iterates `varCnt` and reads
`template.VariableTypes[v]` unconditionally (`:442`). So the loader MUST
provide a non-null array of length `>= varCnt`. For the S2 probe (`T Echo<T>
(T v)` with no locals, `varCnt == 0`), the array is empty -- no re-resolution
needed. For a generic method with locals, every local's `VariableTypeRefIdx`
is re-resolved. Confirm at apply.

### D5. The capstone (the binding gate -- a green smoke does NOT prove it)

The S2 capstone EXTENDS `NeoStep25LoadExecCheck` (`NeoStep25LoadExecCheck.cs`)
with generic cells. A green "JIT == AOT" comparison is INSUFFICIENT (Step 23
already proved the AOT template body == JIT body byte-for-byte, so JIT == AOT
holds EVEN IF the JIT path still ran). The decisive proofs:

**(1) Template body-mutation cell** (the load-bearing one, mirroring S1's
`ConstProbe` mutation). Mutate a constant in the deserialized `.neo`
`TemplateBody` BEFORE attach, then invoke a wrapper calling a generic method ->
assert the MUTATED value. If `CloneAndPatch` ran the JIT-captured template
(not the AOT one), the mutation would have no effect; observing the MUTATED
value proves the AOT template body drove the instantiation. (The mutation is
on a `Ldc_I4` operand inside the template body -- chosen so it is observable
through the generic method's return. Requires a generic method that loads a
constant, e.g. `T EchoWithBias<T>(T v) where ...` with an int constant folded
into the return path -- confirm the JIT keeps the `Ldc_I4` in the register-
index template body at apply.)

**(2) Fresh-instance cell.** The after-attach run invokes a concrete-T wrapper
NEVER instantiated before attach (e.g. before-run uses `WrapEchoInt`, after-run
uses `WrapEchoLong`). `Echo<long>` was never instantiated, so its first call
after attach MUST route through `CloneAndPatch` via the AOT template (it cannot
reuse a JIT-cached instance body -- there is none). This isolates the AOT-
template path from any JIT-cached instance state.

**(3) Structural-equivalence cell** (DEBUG host-side). Reuse the Step-22 V1
comparator shape (`GenericMethodTemplateOps.BodiesEqual`, `GenericMethodTemplate.cs:
651-670`): the AOT-reconstructed template's `CloneAndPatch` body EQUALS the
per-occurrence JIT body for each concrete T. This needs a new DEBUG hook
`CompileViaAotTemplateNeoBody` (mirrors `CompileViaTemplateNeoBody` at `:637`
but uses the `.neo`-reconstructed template instead of `ForceBuildTemplate`).

**(4) Functional cells.** Parameterless wrappers at concrete T = int / long /
struct / ref -> assert each result EQUALS its known-expected value (pins
JIT == AOT == correct, ruling out a both-garbage false pass).

The probe type (`TestCases/NeoStep25LoadProbe.cs`) gains the generic method +
the wrappers. Stays NON-NESTED + within the BCL-refs-only boundary (the S1
constraint). The capstone reuses the SAME `NeoCompiler.Compile` ->
`NeoAssemblyReader.Read` -> `NeoAssemblyLoader.Attach` seams (no test-only
compile path).

### D6. Gating: Neo-only, Legacy-neutral, additive

- The loader's template-consumption extension, `BuildFromNeoRecord`, the
  `InitTemplateFromNeo` setter, and the capstone's generic cells are all
  `#if ENABLE_NEO_MODE` (the self-check file is `#if ENABLE_NEO_MODE && DEBUG`).
- The Step-22 hook (`InitCodeBody` `:720-762`), `ExecuteNeo`, the optimizer,
  the JIT, `StoreGenericTemplate`, the Step-23 format, and the Step-24 tool are
  UNCHANGED.
- The Cecil / JIT path is the reference + the fallback. A generic method whose
  template is not reconstructed (a deferred case, or a re-resolution miss)
  keeps JIT -- `GenericMethodTemplateCache` stays null -> `InitCodeBody`'s hook
  falls through to per-occurrence JIT (`ILMethod.cs:744-755`).
- Under plain `Debug`, the whole addition compiles out; Legacy `ExecuteR` is
  byte-identical (stash-toggle proof: same pre-existing Legacy failure set).

## Risks / Trade-offs

- **[The setter overwrites a JIT-cached template that a concurrent run depends
  on]** -> the loader runs ONCE at attach (init time), before any generic call
  in the AOT scenario. The V2 capstone's before-run capture is deliberately
  overwritten (that IS the proof). A generic instance already instantiated
  before attach has its own `bodyRegister` cached (on the instance, not the
  definition) -- the after-attach run uses a FRESH concrete T (D5 cell 2) to
  avoid reusing it. Mitigation: the capstone's fresh-instance cell.
- **[`VariableTypes` re-resolution misses for an unusual local type]** -> the
  template is skipped (additive contract; the generic method keeps JIT). The
  miss is reported. S3 owns the Cecil-free re-resolution.
- **[The no-T-identity-token slice is too narrow to be useful]** -> ACKNOWLEDGED:
  S2 proves the `.neo` template body is AOT-executable via `CloneAndPatch` for
  the common pure-dataflow generic method. The T-identity-token cases (`Box T`,
  `Constrained T`, `IComparable<T>::CompareTo`) are the S3 value. S2's value is
  the deserialize + CloneAndPatch-from-AOT-template functional proof (the S1
  analog for generic methods), NOT the full AOT coverage.
- **[Regression: populating `GenericMethodTemplateCache` at load changes
  existing JIT behavior]** -> LOW. The cache is read ONLY by the Step-22 hook,
  which is additive (no cache -> JIT, byte-identical). A non-AOT scenario never
  calls the loader, so the cache is never set by the loader. The JIT capture
  path (`StoreGenericTemplate`) is unchanged. Mitigation: full NeoStep smoke
  (210/210) + the AOT self-check filters + a stash-toggle Legacy run.

## Migration Plan

None. The change is additive and Neo-only. Rollback = revert the loader's
template-consumption extension + `BuildFromNeoRecord` + the `InitTemplateFromNeo`
setter + the capstone's generic cells. No shared code depends on them (the
cache defaults null; `StoreGenericTemplate`'s guard is unchanged; the Step-22
hook falls through to JIT when the cache is null).

## Open Questions

- **OQ1:** Does `BuildInitObjPrefix` need the full `VariableTypes` array, or
  can the loader pass a sparse array? It reads `template.VariableTypes[v]`
  unconditionally for `v in [0, varCnt)` (`:442`). Default: provide the full
  array, re-resolved from `VariableTypeRefIdxs`. Confirm at apply.
- **OQ2:** The template body-mutation cell (D5 cell 1) needs a generic method
  whose register-index template body carries an observable `Ldc_I4` constant
  (not folded away). Confirm the JIT keeps such a constant in the template body
  at apply (if the optimizer folds it, pick a different mutation target -- e.g.
  an `Initobj` Operand, or seed the constant via a byref out-param).
- **OQ3:** Should `BuildFromNeoRecord` reject a template whose `Patches`
  contain a `TypeToken`/`MethodToken` with a non-`none` `CecilTokenKind` (the
  S3 case), or attempt a best-effort Cecil re-resolution same-AppDomain?
  Default: REJECT (return null -> skip -> JIT fallback). A best-effort path
  risks a silent wrong-type-token dispatch (the F-10 / Step-22 Constrained-
  token class). The honest S2 slice skips T-identity-token templates. Confirm
  at apply.
