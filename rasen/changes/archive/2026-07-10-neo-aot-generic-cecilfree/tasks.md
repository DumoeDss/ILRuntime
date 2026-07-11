# Tasks — neo-aot-generic-cecilfree (child 8)

> **Status: SHIPPED (re-audit UNBLOCKED).** The reproducer was complete in the
> PARKED state; the fix is a coherent set of SMALL additive Neo-gated patches
> (NOT the "multi-step back-half rework" the prior framing warned of). See
> `design.md` for the re-audited root cause.

## Done (the reproducer — completed in the PARKED state)
- [x] Probe `TestCases/NeoStep25CecilFreeGenericProbe.cs` (`Echo<T>` +
      `ConstGeneric<T>` + wrappers + `NeoStep25CegVal`).
- [x] Capstone `NeoStep25CecilFreeGenericCheck.cs` (G1 + G2 + functional cells).
- [x] CLI special-mode `NeoStep25CecilFreeGeneric` wiring (`Program.cs`).
- [x] HEAD-failure confirmed (10/12; G1 + G2 FAIL; functional cells pass via
      inlining — the S2-probe-inlining pitfall).

## Done (the fix — this child, re-audited)
- [x] **`.neo` V5:** `NeoTemplateRecord.GenericParamNames` +
      `ReturnTypeRefIdx` (`NeoAssembly.cs` + `NeoAssemblyWriter.BuildTemplate`/
      `WriteTemplate`/`BuildGenericParamNames` + `NeoAssemblyReader.ReadTemplate`;
      Version 4 -> 5). Additive; same-AppDomain loads IGNORE both.
- [x] **`ILMethod` Cecil-free generic shell:** `neoShellGenericParamNames` field;
      `GenericParameterCount` returns its length for a shell (replaces the `:189`
      `return 0`); `FindGenericArgument` `def.HasGenericParameters` guard;
      `MakeGenericMethodShell` (a Cecil-free `MakeGenericMethod` branch -- no
      `def.GenericParameters`, no `GenericInstanceMethod(reference)`, no Cecil ctor;
      T-substituted params + return via `SubstituteGenericParam`);
      `SetNeoShellGenericParamNames`/`SetNeoShellReturnType`; `IsNeoAotBodyBound`;
      the `InitCodeBody` Cecil-free shell-instance guard (routes through
      `TryInstantiate` without `def.HasBody`).
- [x] **`JITCompiler` Cecil-free back-half:** `BuildInitialRegisterTypes` +
      `AllocateLocalStackSpaces` read Cecil-free sources when `def == null`
      (params from `method.Parameters`; locals from `template.VariableTypes` via
      `GetTemplateVariableTypes`/`GetLocalCount`/`GetLocalType`), flowing through
      the EXISTING `appdomain.GetType` (T resolved via `FindGenericArgument`). NO
      ABI change; alignment + ref-counting + the F-MAJ-1 CLR-VT path preserved
      (unchanged -- the resolved `IType` is read identically).
- [x] **`NeoAssemblyLoader` S2 bind loop:** `MatchGenericDefinitionCecilFree`
      (name + param-count + !IsGenericInstance, no `GenericParameterCount > 0`
      filter); `BuildAndRegisterGenericDefShell` (creates the generic-def shell
      from the template's open-def MethodRef -- the .neo MethodDefs table carries
      NON-generic methods only, so the generic-def shell was never created at
      Cecil-free type build); stamp `.neo` GenericParamNames + ReturnType before
      the VariableType re-resolution; `ResolveVariableType` shell-aware (branch (a)
      synthetic `GenericParameter` from the names via a tiny
      `IGenericParameterProvider` owner; branch (b) CLR bare-`TypeReference`
      fallback when no Cecil module); `ResolveReturnTypeFromTemplate`.
- [x] **`GenericMethodTemplate.cs`:** UNCHANGED (the re-audit proved the Step-22
      template machinery is NOT the blocker; `BuildFromNeoRecord` /
      `DoCloneAndPatch` / `TryInstantiate` work as-is once the Cecil-free sources
      feed the back-half).
- [x] **Probe capstone tweak:** G2 now passes a `this` instance
      (`domainB2.Instantiate(ProbeFullName)`) -- ConstGeneric<T> is an instance
      method; the prior `Invoke(freshInst, null)` NRE'd once MakeGenericMethod
      worked. The G1/G2 guards + the inlining-pitfall doc are unchanged.

## VERIFY (all green)
- [x] **NeoStep25CecilFreeGeneric capstone: 12/12 cells passed** (G1 + G2 + the 4
      functional wrappers + compile + load + the 4 A-JIT reference cells). Attach:
      8, 0 skipped.
- [x] **Stash-toggle (load-bearing):** stash the 7 engine files (keep the check)
      -> G1+G2 FAIL (10/12, the HEAD state); pop -> 12/12 GREEN.
- [x] **NeoStep25 gate: 0 failed.** NeoStep25LoadExec 28/28, NeoStep22SelfCheck
      55/55, NeoStep23Roundtrip 15/15 (no .neo-format regression from V5).
- [x] **NeoStep smoke: 278 ran, 0 failed** (the child-17 baseline; no regression).
- [x] **Legacy-neutral:** plain `Debug` build 0 errors (every engine change
      `#if ENABLE_NEO_MODE`-gated; the .neo V5 change is additive).

## Follow-ups (NOT done; out of scope)
- [ ] **Generic TYPE instances** (`List<ILType>` field): the method-instance path
      is the core delivered here. A generic TYPE field's Cecil-free load is a
      separate surface (the ILType field-layout path).
- [ ] **T-identity-token generic methods** (Box T / Ldobj T / a T-qualified
      callvirt): the S2 slice covers IsRefMoveFlag-only + empty patch tables.
      A T-identity-token method still hits the S3 reject (the `hasIdentityToken`
      return-null in `BuildFromNeoRecord`); a Cecil-free T-identity patch applier
      keyed on `GenericParamIdx` is the S3 follow-up.
