# Tasks -- neo-typeof-generic-param

## 1. RE-AUDIT (DONE)
- Confirmed TestGenericMethod2 fails under Neo (Invalid cast String->Int32 @
  Test05.cs:167, the FIRST GetRows<int,int> call's ChangeType). PASSES on Legacy.
- JIT dump: GetRows body captured from <int,int>; BOTH ldtoken resolve to
  "System.Int32" (capture-T). Runtime diagnostic: typeof(A) pushes
  ILRuntimeWrapperType(System.Int32) (CLRType.ReflectionType); Convert.ChangeType
  rejects the wrapper.
- PINNED ROOT (two compounding bugs):
  (a) ExtractPatches has no Ldtoken case -> template ldtoken keeps capture-T hash
      -> typeof(B) resolves to int for the <int,double> call.
  (b) ChangeType_1_Neo autogen stub does a raw (System.Type) cast -> no unwrap of
      ILRuntimeWrapperType -> ChangeType fails even for the correct typeof(A)=int.
  (c) SURFACED during implementation: even with (a)+(b), V printed as a garbage
      denormalized double because the template's Stfld_I4 (capture-T int) is not
      re-specialized to Stfld_R8 for the concrete double backing field.

## 2. IMPLEMENT (DONE; Neo-gated)
- [2.1] `GenericMethodTemplate.cs` ExtractPatches: add `case OpCodeREnum.Ldtoken`
       -> field PatchField.Operand2, Kind TypeToken (Fix a).
- [2.2] `GenericMethodTemplate.cs`: add CaptureTypeArgs field; FieldOpcodeCategory
       helper; TryInstantiate category fall-back (Fix c).
- [2.3] `JITCompiler.cs`: refactor GetStfldCodeForType to delegate to a static
       GetNeoStfldCodeForType(fieldType, appdomain) (authoritative category source).
- [2.4] `ILMethod.cs`: StoreGenericTemplate(cap, captureTypeArgs) + pass
       genericArguments at the call site.
- [2.5] `CLRRedirections.cs`: ChangeTypeNeo redirect (unwrap ILRuntimeWrapperType
       /ILRuntimeType -> RealType/TypeForCLR) (Fix b).
- [2.6] `AppDomain.cs`: register ChangeTypeNeo on RedirectMapNeo.
- [2.7] `TestCases/NeoStepTypeofGenericParamTest.cs`: TC1 (template ldtoken patch,
       non-inlined generic via try/catch, two ref types) + TC2 (ChangeType unwrap).

## 3. VERIFY (DONE)
- Stash-toggle airtight: stash the 5 engine files -> TC1/TC2/TestGenericMethod2
  FAIL on HEAD; pop -> PASS.
- Full smoke 20 -> 19 (TestGenericMethod2 fixed; 19 strict subset).
- NeoStep 403/0 (401 + 2 probes).
- Legacy build clean; TestGenericMethod2 + probes PASS under plain Debug.

## 4. SHIP (LEAD commits -- DO NOT commit)
- Files (uncommitted): GenericMethodTemplate.cs, JITCompiler.cs, ILMethod.cs,
  CLRRedirections.cs, AppDomain.cs, new TestCases/NeoStepTypeofGenericParamTest.cs.
