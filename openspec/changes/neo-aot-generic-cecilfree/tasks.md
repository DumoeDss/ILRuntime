# Tasks — neo-aot-generic-cecilfree (child 8)

> **Status: PARKED.** The reproducer + diagnosis are complete; the fix is
> multi-step rework (see `design.md`). Tasks below are the successor's plan.

## Done this child (the reproducer — the forward signal)
- [x] Probe `TestCases/NeoStep25CecilFreeGenericProbe.cs` (`Echo<T>` +
      `ConstGeneric<T>` + wrappers + `NeoStep25CegVal`).
- [x] Capstone `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep25CecilFreeGenericCheck.cs`
      (G1 template-bind coverage + G2 template body-mutation + functional cells).
- [x] CLI special-mode `NeoStep25CecilFreeGeneric` wiring (`Program.cs`).
- [x] HEAD-failure confirmed (10/12; G1 + G2 FAIL; functional cells pass via
      inlining — the S2-probe-inlining pitfall).
- [x] 5 Cecil-dependency blocker sites identified + documented (`design.md`).
- [x] No regression: NeoStep 253/0/0, NeoStep25 gate 11/0, plain-Debug 0 errors.

## Successor tasks (the fix — bounded plan, new child recommended)
- [ ] `.neo` V4: add `string[] GenericParamNames` to `NeoTemplateRecord`
      (`NeoAssembly.cs` + `NeoAssemblyWriter.BuildTemplate`/`WriteTemplate` +
      `NeoAssemblyReader.ReadTemplate`; bump `NeoAssemblyFormat.Version` 3 -> 4).
      Source: `tpl.Definition.Definition.GenericParameters[i].Name`.
- [ ] `ILMethod` Cecil-free generic shell: a `neoShellGenericParamNames` field
      set by the S2 bind loop; `GenericParameterCount` returns its length for a
      shell (replace the `:189` `return 0`); a Cecil-free `MakeGenericMethod`
      branch (no `def.GenericParameters`, no `GenericInstanceMethod(reference)`,
      no Cecil ctor — set `genericParameters`/`genericArguments`/`genericDefinition`
      directly); `FindGenericArgument` `def.HasGenericParameters` guard (`:415`).
- [ ] `JITCompiler` Cecil-free back-half: `BuildInitialRegisterTypes` (`:1220`)
      + `AllocateLocalStackSpaces` (`:1648`) read Cecil-free `IType[]` (params
      from the instance shell's resolved params; locals from the template's
      `VariableTypes` resolved Cecil-free to `IType[]`, NOT Cecil `TypeReference`)
      when `def == null` (a shell). Preserve alignment + ref-counting + the
      F-MAJ-1 CLR-VT-flat-bytes path.
- [ ] `NeoAssemblyLoader` S2 bind loop: set the matched def shell's
      `neoShellGenericParamNames` from the template's `GenericParamNames` BEFORE
      `MatchGenericDefinition` (so `GenericParameterCount > 0`); make
      `ResolveVariableType` shell-aware (branch (a) read
      `definition.neoShellGenericParamNames` instead of Cecil; branch (b) return a
      Cecil-free type representation).
- [ ] `GenericMethodTemplateOps.BuildFromNeoRecord`: plumb the Cecil-free
      variable-type resolution (the closure returns Cecil-free `IType`-backed
      TypeReferences or the template stores `IType[]`).
- [ ] VERIFY: NeoStep25CecilFreeGeneric green (incl G1 + G2); NeoStep25 gate
      (LoadExec 28/28, CecilFreeLoad 7/7, ClrBaseIface 4/4, ClrAdaptor 7/7) +
      NeoStep22SelfCheck 55/55 + NeoStep23Roundtrip 15/15 hold; NeoStep smoke
      253+N/0/0; stash-toggle (G1/G2 FAIL on HEAD -> PASS after); Legacy-neutral.
