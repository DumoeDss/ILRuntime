# Tasks: neo-jit-generic-method-specialization

## 1. Re-audit (DONE)
- [x] Build CLI (Debug_Neo --no-incremental) + TestCases (Debug). 0 errors.
- [x] Confirm full-smoke baseline = 932 ran / 54 failed.
- [x] Confirm CLRBindingTest07/08 PASS in name-filter isolation but FAIL in full
      smoke (test-ordering). Reproduce with `CLRBindingTest0` filter (9 ran/2 failed).
- [x] Confirm PASS on Legacy (plain Debug) -- Neo-specific.
- [x] Diagnostic: confirm ExtractPatches records 0 patches for
      CLRBindingTest06Sub<T> (Callvirt_CLR LoadAsset<T> hits `default: continue`);
      TryInstantiate ref-shares (allRef=True, hasIdentity=False).

## 2. Implement (DONE; Neo-gated, Legacy-neutral)
- [x] `ExtractPatches` (GenericMethodTemplate.cs): add Call/Callvirt/Callvirt_IL/
      Callvirt_CLR/Call_Redirect cases. Field = Operand2; Kind = MethodToken;
      CecilToken = sym.Instruction.Operand when HasGenericParameter. Skip the
      trailing callvirt of a constrained pair (`body[i-1].Code == Constrained`).
- [x] `DoCloneAndPatch` patch-apply (GenericMethodTemplate.cs): for MethodToken,
      add reliability cross-check -- compare `appdomain.GetMethod(body[idx].
      Operand2).Name` to the Cecil token's re-resolved method Name; skip on
      mismatch (inliner-scrambled symbol).

## 3. Verify (DONE; truth = full-smoke number)
- [x] Name-filter: CLRBindingTest0 -> 0 failed (was 2). GenericMethodTest3 -> 0
      failed (no regression; the cross-check fixed the iteration-1 regression).
- [x] FULL SMOKE: 54 -> 51 (-3). FIXED: CLRBindingTest07, CLRBindingTest08,
      JsonTest9 (bonus). NEW failures: 0.
- [x] NeoStep 398/0 (no regression).
- [x] Stash-toggle (whole file): reverted -> CLRBindingTest0 = 2 failed;
      restored -> 0 failed.
- [x] Legacy-neutral: plain Debug + useRegister=true + NeoStep = 398 ran/18 failed
      (pre-existing Legacy set; Neo-gated file).

## 4. Out of scope / notes
- The first fix iteration (ExtractPatches call-case alone) regressed
  GenericMethodTest3 (IndexOOB in LowerNeoOffsets) because the inliner leaves a
  STALE symbol on an inlined call's body index. The DoCloneAndPatch name
  cross-check resolves it (skip scrambled patches). Documented as a durable
  finding.
- The mapMethod hash cache (`AppDomain.GetMethod(int)`) can return a method with
  invalidToken=false for a previously-cached generic token; the patch path uses
  `GetMethodTokenHash` (which re-resolves via `GetMethod(token, declaringType,
  instance, out invalidToken)` and returns `m.GetHashCode()` when invalidToken) so
  the re-resolved hash is T-dependent. Verified by the LoadAsset dispatch fixing.
