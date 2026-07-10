# Tasks — neo-aot-generic-tidentity

**Status:** DONE (all tasks complete; LEAD commits)

## 1. Reproducer + confirm rejection on HEAD — DONE
- [x] Added `BoxUnbox<T>(T v){ object o = v; return (T)o; }` to
      `NeoStep25CecilFreeGenericProbe` (a T-identity body: `Box T` + `Unbox.Any T`).
- [x] Added the G3 capstone cell (`BoxUnbox<int>` fresh-instance via
      `MakeGenericMethod`, never inlined) to `NeoStep25CecilFreeGenericCheck`.
- [x] Confirmed HEAD rejection: `NotImplementedException: Cecil-free generic
      instance has no cached template (S2 bind skipped): BoxUnbox` + the SKIP
      record `template rebuild miss (S3 case): ...BoxUnbox` (the
      `hasIdentityToken` rejection).

## 2. Root-cause re-audit — DONE
- [x] Confirmed the `.neo` `NeoPatchEntryRecord` carries `GenericParamIdx` +
      `TokenRefIdx` + `CecilTokenKind` (Step 23). NO `.neo` V6 needed.
- [x] Confirmed the rejection was a defensive guard (the S3 path could not
      re-resolve a Cecil token Cecil-free — but the synthetic Cecil
      `GenericParameter` machinery from child-8 already exists and is what
      `BuildInitObjPrefix` feeds `GetTypeTokenHashCode`).

## 3. Fix (Neo-gated) — DONE
- [x] `GenericMethodTemplate.cs`: `RebuildPatchesNoCecil` now takes the
      `resolveVariableType` closure; for a TypeToken T-identity patch it
      re-resolves the generic-param (TokenRefIdx -> closure -> synthetic Cecil
      GenericParameter) into `PatchEntry.CecilToken`. CloneAndPatch's existing
      TypeToken path re-derives the concrete T hash Cecil-free. MethodToken
      T-identity still rejects (deeper sub-case).
- [x] `ILType.cs`: guarded `FindGenericArgument` against null `definition`
      (pre-existing latent NRE on a Cecil-free type, surfaced by the unblocked
      T-identity path).
- [x] `ILType.cs`: guarded `IsByRef` against null `typeRef` (Neo-gated; a
      Cecil-free ILType is never a byref) — pre-existing latent NRE surfaced by
      the fresh-instance CloneAndPatch `HasThis` declaring-type probe.

## 4. Verify — DONE
- [x] NeoStep25CecilFreeGeneric capstone: **15/15** (G1+G2 + the new G3
      T-identity cell + functional wrappers incl. WrapBoxUnboxInt).
- [x] Stash-toggle load-bearing: stash the 2 engine files -> 12/15 (G3+G2+
      WrapEchoStruct FAIL, 1 template skipped on S3 rejection); pop -> 15/15.
- [x] NeoStep smoke: **0 tests failed** (no regression).
- [x] NeoStep25LoadExec 28/28; NeoStep22/23 0 failed (no `.neo` format regression).
- [x] Legacy-neutral: plain `Debug` build 0 errors.

## 5. Scope assessment — DONE (minimal coherent subset, no deep rework)
- [x] The fix is the minimal coherent subset: the requested Box T / Ldobj T /
      Initobj T surface is all TypeToken (handled). MethodToken T-identity
      (`constrained.` T-qualified callvirt) is parked (deeper sub-case). Box/
      Unbox of an IL value type is an engine gap (out of scope, fails the Cecil
      reference too).
