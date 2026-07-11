# Design — neo-step25-runtime-loader (Step 25: runtime `.neo` loader + ILMethod Cecil-decoupling dual-path, scoped S1)

> Technical design for the S1 scoped slice (see proposal.md for the Cecil-coupling
> enumeration, the scope decision, and the deferral table). Grounded in the actual
> init/execution code (HEAD verified, line-cited).

## Context

`ExecuteNeo` (`ILIntepreter.Neo.cs:840-842`) reads ONLY `method.CompiledFrame.
NeoExecuteBody` + `method.CompiledFrame` (frame metadata) + resolves token
operands via the AppDomain hash maps. It is ALREADY Cecil-free at runtime. So
Step 25 does NOT need to touch `ExecuteNeo`; it needs (a) an ILMethod AOT-init
that populates `CompiledFrame` from a `.neo` `NeoMethodDefRecord`, and (b) the
AppDomain hash maps to resolve the body's token operands. S1 runs same-AppDomain
(the compile-time hash maps are already live), so (b) is automatic; only (a) +
a loader that drives it are built here.

## Decisions

### D1. The ILMethod AOT-init dual-path (Plan A: a flag + an alternate init)

`ILMethod` (`ILRuntime/CLR/Method/ILMethod.cs`) holds `compiledFrame` as a
private struct field (`CompiledFrame compiledFrame;` :58) + `bodyRegister` +
`stackRegisterCnt` + `jumptablesR`. Today these are populated by `InitCodeBody(
true)` (`:694`), driven by `JITCompiler.Compile` (`:733`) which reads Cecil
`def.Body`. The `BodyRegister` getter (`:389`) lazily calls `InitCodeBody(true)`
when `bodyRegister == null`.

**The AOT-init (Neo-only, `#if ENABLE_NEO_MODE`):**

```
internal bool isNeoAotBody;   // Neo-only, default false

internal void InitCodeBodyFromNeo(NeoMethodDefRecord rec)
{
    // Populate compiledFrame field-by-field from rec (NO Cecil, NO JIT).
    compiledFrame.NeoExecuteBody    = rec.NeoExecuteBody;
    compiledFrame.LocalInfos        = rec.LocalInfos;
    compiledFrame.ParamInfos        = rec.ParamInfos;
    compiledFrame.TotalStructSize   = rec.TotalStructSize;
    compiledFrame.TotalRefSize      = rec.TotalRefSize;
    // ... every load-bearing field the Step-23 record carries (D3 list) ...
    compiledFrame.StackRegisterCount= rec.StackRegisterCount;
    compiledFrame.SwitchTargets     = RebuildSwitchTargets(rec.SwitchTargets);
    compiledFrame.NeoCallParams     = RebuildNeoCallParams(rec.NeoCallParams);
    // EH rebuild (D2).
    compiledFrame.ExceptionHandlers = RebuildEHFromNeo(rec.ExceptionHandlers,
                                                       rec.NeoExecuteBody.Length);
    // ILMethod-level mirrors.
    bodyRegister      = rec.NeoExecuteBody;
    stackRegisterCnt  = rec.StackRegisterCount;
    jumptablesR       = compiledFrame.SwitchTargets;
    isNeoAotBody      = true;
}
```

**The getter short-circuit (Neo-only):**

```
internal OpCodeR[] BodyRegister
{
    get
    {
#if ENABLE_NEO_MODE
        if (isNeoAotBody) return bodyRegister;   // AOT: already populated
#endif
        if (bodyRegister == null) InitCodeBody(true);
        return bodyRegister;
    }
}
```

The `CompiledFrame` getter (`:423`) already returns `compiledFrame` when
`NeoExecuteBody != null` -- so once `InitCodeBodyFromNeo` populates it, the
getter sees a ready frame with NO change.

**Why a flag + alternate init (not a parallel AOT-ILMethod type).** A parallel
type would duplicate the ILMethod surface `ExecuteNeo` and the Call dispatch
path depend on (parameter list, return type, declaring type, generic info,
`SignatureString`, etc.). The flag reuses the EXISTING Cecil-initialized ILMethod
(the assembly is Cecil-loaded in S1's same-AppDomain test) and only swaps the
BODY source. This is the minimum-risk seam. The Cecil path STAYS byte-identical
(the flag defaults false; the getter short-circuit is Neo-gated).

**Legacy-neutrality.** The flag, `InitCodeBodyFromNeo`, and the getter short-
circuit are all `#if ENABLE_NEO_MODE`. Under plain `Debug`, `ILMethod` is
byte-identical to before. The JIT path (Neo + Legacy) is unchanged when the flag
is false (the AOT loader is the only setter).

### D2. EH rebuild from the body-index table (the one structural reconstruction)

`ExecuteNeo`'s EH dispatch reads the runtime `Method.ExceptionHandler` /
`exceptionHandlerR` structures, which `InitCodeBody` (`ILMethod.cs:776-818`)
builds from the Cecil-keyed `addr[]` map at JIT time. `addr[]` is not
serializable, so Step 23 re-represented the EH table as `NeoExceptionHandler-
Record[]` with try/handler ranges as BODY INDICES (resolved via `addr[]` at
serialize time). The AOT-init rebuilds the runtime EH structures DIRECTLY from
these indices -- no Cecil, no `addr[]`:

```
Method.ExceptionHandler[] RebuildEHFromNeo(NeoExceptionHandlerRecord[] recs,
                                           int bodyLen)
{
    var arr = new Method.ExceptionHandler[recs.Length];
    for (int i = 0; i < recs.Length; i++)
    {
        arr[i] = new Method.ExceptionHandler {
            TryStart     = recs[i].TryStartIdx,      // already a body index
            TryEnd       = recs[i].TryEndIdx,
            HandlerStart = recs[i].HandlerStartIdx,
            HandlerEnd   = recs[i].HandlerEndIdx,
            FilterStart  = recs[i].FilterIdx,        // -1 if none
            HandlerType  = (ExceptionHandlerType)recs[i].HandlerType,
            CatchType    = recs[i].CatchTypeRefIdx >= 0
                ? ResolveCatchType(recs[i].CatchTypeRefIdx)  // D3
                : null,
        };
    }
    return arr;
}
```

The mapping is 1:1 (body indices map directly to `NeoExecuteBody` positions,
which is what `ExecuteNeo`'s `ip` arithmetic uses). `ResolveCatchType` (D3)
resolves a TypeRef index to the runtime `IType` so `CheckExceptionType` can
match a thrown exception to the catch clause.

### D3. Ref binding (TypeRef -> IType, MethodRef -> IMethod, FieldRef -> field)

S1 is same-AppDomain: the probe types are Cecil-loaded, so `appdomain.
LoadedTypes[fullName]` resolves every IL TypeRef to the live `ILType`, and
`mapTypeToken` / `mapMethod` (populated at Cecil-load + JIT-compile time)
resolve every token operand the body carries. The loader's ref binding is
therefore a STRUCTURAL match (TypeRef.Name -> LoadedTypes) for the MethodDef ->
ILMethod attach, NOT a hash re-registration.

```
ILMethod Match(ILType iltype, MethodReferencePatchInfo mr)
{
    // V1: name + parameter count (the probe is small). Round 2 adds full sig
    // matching + cross-AppDomain CLR-aqname resolution.
    int wantParam = mr.ParameterCount;   // from the Step-23 MethodRef record
    foreach (var ilm in iltype.GetMethods().Concat(iltype.GetConstructors()))
    {
        if (ilm.IsGenericInstance) continue;
        if (ilm.Name != mr.Name) continue;
        if (ilm.ParameterCount != wantParam) continue;
        return ilm;
    }
    return null;   // match miss -> skip (additive contract; method keeps JIT)
}
```

The catch-type resolver for D2 likewise uses `appdomain.LoadedTypes[fullName]`
(IL catch types in the probe are IL types). CLR catch types route through
`appdomain.GetType(aqname)` (the existing CLR path). Full CLR-aqname indexing +
robust IL-vs-CLR discrimination is the round-2 / Step-24-CLI concern (the
`TestCLREnum` deferral).

### D4. The loader entry

```
internal static class NeoAssemblyLoader
{
    // Same-AppDomain attach: bind each NeoMethodDefRecord to a live ILMethod
    // and populate its CompiledFrame from the .neo (bypass JIT). Returns a
    // report of attached / skipped methods.
    internal static NeoLoadReport Attach(AppDomain appdomain, NeoAssemblyModel model)
    {
        var report = new NeoLoadReport();
        foreach (var rec in model.MethodDefs)
        {
            var mr = model.MethodRefs[rec.MethodRefIdx];
            var typeFullName = model.TypeRefs[mr.DeclaringTypeIdx].Name;
            if (!appdomain.LoadedTypes.TryGetValue(typeFullName, out var iltype))
            { report.Skipped.Add(("type not loaded", typeFullName)); continue; }
            var ilm = Match((ILType)iltype, mr);
            if (ilm == null)
            { report.Skipped.Add(("method not matched", typeFullName + "." + mr.Name)); continue; }
            ilm.InitCodeBodyFromNeo(rec);
            report.Attached.Add(typeFullName + "." + mr.Name);
        }
        return report;
    }
}
```

Generic methods are out of scope (S1): a `NeoMethodDefRecord` is always a
non-generic method (Step 24's `CompileCore` partitions generic definitions to
the `TemplateTable`, never `MethodDefs`). A `.neo` carrying templates is
accepted by the loader; the templates are simply not consumed in S1 (deferred
to S2). No match is attempted for generic instances (they are runtime artifacts,
not in `MethodDefs`).

### D5. The V2 functional self-check (the capstone gate)

`NeoStep25LoadExecCheck.Run(appdomain)` (host-side, DEBUG+Neo), driven via the
`ILRuntimeTestCLI` `NeoStep25LoadExec` hook (mirrors `NeoStep24CliRoundtrip`).
The check is the FIRST end-to-end "deserialize + execute" proof:

```
1. Select the probe ILType (Cecil-loaded in the test AppDomain).
2. using var ms = new MemoryStream();
   new NeoCompiler().Compile(new[] { probeType }, ms);   // produce .neo
3. ms.Position = 0;
   var model = NeoAssemblyReader.Read(ms);               // Step 23 deserialize
4. foreach probe method m:
      resultJIT[m] = Invoke(m, ...)                      // run via JIT (pre-attach)
5. NeoAssemblyLoader.Attach(appdomain, model);           // attach AOT bodies
6. foreach probe method m:
      resultAOT[m] = Invoke(m, ...)                      // run via AOT body
7. foreach probe method m:
      if (!Equal(resultJIT[m], resultAOT[m])) int x = 1/0;   // divide-assert
```

The probe matrix (S1, non-generic only): `ArithProbe` (arithmetic + return),
`TryCatchProbe` (EH), `MixedLocalsProbe` (various locals / a byref param). The
probe type is a dedicated `TestCases/NeoStep25LoadProbe.cs` (mirrors the Step-24
`NeoStep24CliProbe` pattern), NOT the full `TestCases.dll` (keeps it sub-second
+ stays within the BCL-refs-only boundary).

`Equal` compares the observable result (the test methods are chosen to return a
primitive / a small deterministic value). The before-attach vs after-attach
comparison isolates the AOT body as the ONLY variable (the JIT run proves the
method is correct on HEAD; the AOT run must match).

### D6. Gating: Neo-only, Legacy-neutral, additive

- The loader (`NeoAssemblyLoader`) + the self-check (`NeoStep25LoadExecCheck`)
  + the probe type are `#if ENABLE_NEO_MODE` (the self-check file is
  `#if ENABLE_NEO_MODE && DEBUG`, matching the Step-23/24 self-checks).
- The `ILMethod` AOT-init (`isNeoAotBody`, `InitCodeBodyFromNeo`) + the
  `BodyRegister` getter short-circuit are `#if ENABLE_NEO_MODE`. Under plain
  `Debug`, `ILMethod` compiles byte-identically (the flag defaults false; the
  short-circuit compiles out).
- The Cecil / JIT path is UNCHANGED (it is the reference + the fallback). When
  `isNeoAotBody` is false, `BodyRegister` behaves exactly as before.
- `ExecuteNeo`, the optimizer, the JIT, the Step-22 template mechanism, and the
  Step-23/24 artifacts are NOT modified.

## Risks / Trade-offs

- **[The getter short-circuit breaks the JIT path]** -> the flag defaults false;
  the short-circuit is Neo-gated AND flag-gated. The JIT path is byte-identical
  when the flag is false. Mitigation: the NeoStep 205/205 + NeoStep22/23/24
  regression smokes + a stash-toggle Legacy run.
- **[The EH rebuild diverges from the JIT-built EH]** -> the body-index
  representation is 1:1 with `NeoExecuteBody` positions (Step 23 resolved via
  `addr[]` at serialize time). The V2 matrix includes a `TryCatchProbe` cell,
  which fails the divide-assert if the rebuilt EH mis-dispatches. The V1
  roundtrip (Step 23, 15/15) already proved the EH table roundtrips structurally.
- **[The match-by-name-and-paramcount collides on overloads]** -> V1 accepts a
  match ambiguity risk for the small probe (which declares no colliding
  overloads). A collision is reported as a skip (additive contract). Full sig
  matching is round-2 (folded with the cross-AppDomain work).
- **[Same-AppDomain hides the cross-AppDomain hash bug]** -> ACKNOWLEDGED: S1
  does NOT exercise cross-AppDomain (the compile-time hash maps are shared). The
  cross-AppDomain decision (Approach 1, record compile-time hashes) is recorded
  in the proposal; the S3 round-2 child owns the format extension + the
  re-registration. S1's value is the deserialize+execute functional proof, NOT
  the standalone-loader proof.
- **[Attaching overwrites a JIT-cached body]** -> by design: the V2 check
  captures the JIT result BEFORE attach, then attach overwrites `compiledFrame`
  with the AOT body, then captures the AOT result. The two bodies are byte-equal
  (Step 23 V1), so this is safe; the functional run is the proof.

## Migration Plan

None. The change is additive and Neo-only. Rollback = revert the loader + the
ILMethod AOT-init + the self-check; no shared code depends on them (the flag
defaults false; the getter short-circuit compiles out of plain `Debug`).

## Open Questions

- **OQ1: Should `InitCodeBodyFromNeo` also rebuild the `Variables` /
  `LocalVariableCount` mirrors?** `ExecuteNeo` reads frame layout from
  `CompiledFrame.LocalInfos`, not from `ILMethod.Variables` (which is the Cecil
  `VariableDefinition` collection). So the `Variables` mirror is NOT needed for
  execution. Default: skip it (set `localVarCnt` from `LocalInfos.Length` for
  consistency only). Confirm at apply.
- **OQ2: Does the AOT-init need to populate `registerSymbols` (the debug
  symbol map)?** `Symbols` is Cecil-keyed and NOT serialized (Step 23 skipped
  it -- debugger-only). `ExecuteNeo` does not read symbols. Default: leave null
  (debugger on AOT-loaded methods is a deferred item). Confirm at apply.
- **OQ3: Catch-type resolution for CLR catch types.** `ResolveCatchType` (D2)
  for an IL catch type uses `LoadedTypes`; for a CLR catch type uses
  `GetType(aqname)`. The S1 probe uses IL catch types only. CLR catch types are
  round-2 (the IL-vs-CLR classification concern). Confirm at apply.
