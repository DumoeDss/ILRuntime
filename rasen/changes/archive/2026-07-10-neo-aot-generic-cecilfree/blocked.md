# Blocked — neo-aot-generic-cecilfree (child 8) — UNBLOCKED + SHIPPED

**Date:** 2026-07-10  **Capability:** neo-optimizer  **Status: SHIPPED (re-audit UNBLOCKED).**

Was PARKED on a "multi-step 5-site JIT back-half rework" framing.
**UNBLOCKED** when the child-4/child-17 re-audit skepticism was applied: the 5
sites are each REAL blockers (not mis-attributed), but each site fix is a SMALL
additive Neo-gated patch -- NOT a back-half rework, NOT an ABI change, NOT a
shared-cache risk. The "multi-step rework" verdict was over-cautious (it conflated
"5 distinct sites" with "a risky rework"). See `design.md`.

## The re-audited root cause (the 5-site framing was RIGHT on the COUNT, WRONG on the EFFORT)

The 5 (technically 6 -- the PARK note UNDER-counted) sites, each a small patch:
1. `ILMethod.GenericParameterCount` returned 0 on a shell -> a `.neo`-stamped
   `neoShellGenericParamNames` field (V5).
2. **The generic-def SHELL was never created at Cecil-free load** (the .neo
   MethodDefs table carries NON-generic methods only; generic defs live ONLY in
   the TemplateTable). The S2 bind loop now builds + registers it
   (`BuildAndRegisterGenericDefShell`).
3. `ILMethod.MakeGenericMethod` NRE'd on a shell -> a Cecil-free
   `MakeGenericMethodShell` branch.
4. `ILMethod.InitCodeBody` NRE'd on a shell instance before the template path ->
   a Cecil-free shell-instance guard routing through `TryInstantiate`.
5. `JITCompiler.BuildInitialRegisterTypes` + `AllocateLocalStackSpaces` read
   `def.Body`/`def.Parameters` -> the child-4/17-style insight: the template
   ALREADY has the local types (`VariableTypes`) + the shell has T-substituted
   params; the back-half reads those when `def == null`, flowing through the
   EXISTING `appdomain.GetType` (NO new type representation, NO ABI change).
6. `NeoAssemblyLoader.ResolveVariableType`: branch (a) NRE'd on a shell ->
   synthesize a Cecil `GenericParameter` from the .neo names; branch (b) CLR
   `ImportReference` failed (no Cecil module) -> a BARE `TypeReference` whose
   FullName resolves via `appdomain.GetType`.

Plus a `.neo` **V5** addition: `NeoTemplateRecord.GenericParamNames` +
`ReturnTypeRefIdx` (the MethodRef omits the return type; a generic-param return
"T" must resolve for RunNeoBackHalf return-slot sizing).

## What is delivered (SHIPPED)

- **Cecil-free generic METHOD instances work:** the capstone passes 12/12
  (G1 template-bind + G2 fresh-instance template-mutation + the 4 functional
  wrappers + compile + load + the A-JIT reference cells). A non-inlined generic
  call (`ConstGeneric<string>` via a FRESH `MakeGenericMethod`) routes through
  Step-22 CloneAndPatch against the AOT template, re-resolved Cecil-free.
- The `.neo` V5 format (additive; same-AppDomain loads ignore the new fields).
- No regression: NeoStep25LoadExec 28/28, NeoStep22SelfCheck 55/55,
  NeoStep23Roundtrip 15/15; NeoStep smoke 278/0/0; plain-Debug build 0 errors.

## The durable finding (carry forward)

**The S2-probe-inlining pitfall** (a trivial generic method's body is JIT-inlined
into its caller, so functional wrapper cells pass WITHOUT exercising the
Cecil-free generic dispatch). G1 (template-bind) + G2 (fresh-instance
template-mutation) are the load-bearing proofs; functional cells alone are
insufficient. (Carried from the prior PARK note; still true.)

**The re-audit lesson (3rd confirmation after child 4 + 17):** a parked child's
"multi-site / foundational / needs-a-rework" framing is OFTEN over-cautious. The
5-site COUNT was right here (genuinely multi-site), but each site fix was small.
Re-audit the EFFORT, not just the SITE COUNT, before accepting "rework".

## Files (this child)

- `ILRuntime/Runtime/NeoAOT/NeoAssembly.cs` (V5: GenericParamNames +
  ReturnTypeRefIdx on NeoTemplateRecord; Version 4 -> 5).
- `ILRuntime/Runtime/NeoAOT/NeoAssemblyWriter.cs` (V5 write + BuildTemplate capture
  + BuildGenericParamNames).
- `ILRuntime/Runtime/NeoAOT/NeoAssemblyReader.cs` (V5 read).
- `ILRuntime/Runtime/NeoAOT/NeoAssemblyLoader.cs` (MatchGenericDefinitionCecilFree;
  BuildAndRegisterGenericDefShell + ResolveGenericDefParamType; the synthetic
  GenericParameter owner + AcquireSyntheticGenericParam; ResolveVariableType
  shell-aware + bare-TypeReference CLR fallback; ResolveReturnTypeFromTemplate).
- `ILRuntime/CLR/Method/ILMethod.cs` (neoShellGenericParamNames field;
  GenericParameterCount shell-aware; FindGenericArgument guard; MakeGenericMethodShell
  + SubstituteGenericParam; SetNeoShellGenericParamNames/ReturnType; the
  InitCodeBody Cecil-free shell-instance guard; IsNeoAotBodyBound accessor).
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` (BuildInitialRegisterTypes
  + AllocateLocalStackSpaces Cecil-free `def == null` path via GetTemplateVariableTypes
  / GetLocalCount / GetLocalType).
- `ILRuntime/Runtime/Intepreter/RegisterVM/GenericMethodTemplate.cs` (unchanged
  machinery -- the re-audit proved the template path is NOT the blocker).
- `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep25CecilFreeGenericCheck.cs`
  (G2 now passes a `this` instance -- ConstGeneric<T> is an instance method; the
  prior `Invoke(freshInst, null)` NRE'd once MakeGenericMethod worked. The G1/G2
  guards + the inlining pitfall documentation are unchanged.)
