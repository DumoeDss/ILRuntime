# Tasks -- neo-hotfix-patched-il-execute (wave-2 cluster H)

## Phase 1 -- RE-AUDIT (DONE)
- [x] Build CLI (Debug_Neo --no-incremental, UseSharedCompilation=false) + TestCases (Debug). 0 errors.
- [x] Get the EXACT 6 cluster-H tests + messages from a real run (Hotfix filter). Confirmed
      messages are NOT "is not bound!" (that was the resolved C1-cascade). Real messages:
      Test03/04/05/07 = `result = False` (soft); Generic.Test02 = NRE @ CLRMethod.Invoke:818
      <- Execute ILIntepreter.cs:2260; Inheritance.Test03 = bare NIE @ Execute ILIntepreter.cs:1317.
- [x] AUDIT the path: they DO go via InvocationContext.Invoke, but `CanInvokeNeo` rejects at
      `if (!useRegister) return false` (patched body = raw eval-stack Op[], Cecil body empty,
      ShouldUseRegisterVM==false) -> Legacy `Execute(StackObject*)`. NOT a missing Neo branch.
- [x] Root cause: under ENABLE_NEO_MODE, `ILTypeInstance.PushToStack`/`AssignFromStack`/
      `CopyToRegister`/`CopyValueTypeToStack`/`InitializeField` are EMPTY no-ops + `Fields=>null`.
      The eval-stack interpreter (reached only by patched-IL under Neo) reads/writes IL fields
      through them -> silent wrong results. Verified NOT a pre-existing eval-stack bug:
      Legacy passes 14/14 (same eval-stack code; only ILTypeInstance storage differs).

## Phase 2 -- implement + verify (DONE; fix only the tractable subset)
- [x] Implement Neo `ILTypeInstance.PushToStack` (read field -> eval-stack slot via
      ReadNeoPrimitive/managedObjs + ILIntepreter.PushObject). Mirrors the Neo indexer get-arm.
      File: ILRuntime/Runtime/Intepreter/ILTypeInstance.cs (#else of the PushToStack split).
- [x] Implement Neo `ILTypeInstance.AssignFromStack(fieldIdx)` (write <- eval-stack slot via
      StackObject.ToObject + Convert.ChangeType/ToBoolean + WriteNeoPrimitive). Mirrors the
      Neo indexer set-arm. Same file (#else of the AssignFromStack split).
- [x] IL-value-type fields: tagged NIE (deferred; not reached by the 6 cluster-H shapes).
- [x] Leave CopyToRegister / CopyValueTypeToStack / InitializeField empty (not reached).

## VERIFY (truth = full-smoke number)
- [x] FULL Neo smoke: **46 -> 42 (-4)**. 932 ran / 42 failed / 20 ignored / 7 todos. Raw log
      `.tmp-clusterh-fullsmoke-post.log`.
- [x] NeoStep: **398/0** (no regression; stubs reached ONLY by the patched-IL path).
      Raw log `.tmp-clusterh-neostep-post.log`.
- [x] Legacy-neutral: plain Debug + useRegister=true, Hotfix filter = **14/0** (unchanged;
      the change is entirely `#else`/ENABLE_NEO_MODE-gated; Legacy compiles none of it).

## NOT DONE (deep residuals -- honestly reported, NOT forced)
- [ ] Test04 (patched-type STATIC added fields): eval-stack Stsfld/Ldsfld ILType branch
      (`ILIntepreter.cs:2555-2559`/`:2586-2590`) does not consult the ILType static-field
      callback (only the CLR branch does). fieldIdx=1 out of range (TotalFieldCount=1).
      Fix is in SHARED eval-stack code (would alter Legacy) -> out of scope for a Neo-gated child.
- [ ] Test05 (array initializer): Ldtoken on a `.size N` `<PrivateImplementationDetails>`
      struct (TotalFieldCount=0) via the eval-stack path. The child-6 Cecil `InitialValue`
      blob fix covered only the ExecuteNeo ldtoken arm. Deep -> candidate follow-up child.
