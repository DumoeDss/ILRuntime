# Tasks — neo-aot-generic-methodtoken-tidentity

**Status:** DONE (all tasks complete; LEAD commits)

## 1. Reproducer + confirm rejection on HEAD -- DONE
- [x] Added `CompareElems<T>(T a, T b) where T: IComparable<T> { return
      a.CompareTo(b); }` to `NeoStep25CecilFreeGenericProbe` (a MethodToken T-
      identity body: a `constrained. T`-qualified callvirt whose method token
      declares on the generic instance `IComparable<T>`).
- [x] Added the `WrapCompareElemsInt` wrapper (sign-normalized 7.CompareTo(5) ->
      1) + the G4 capstone cell (`CompareElems<int>` fresh-instance via
      `MakeGenericMethod`, never inlined).
- [x] Confirmed HEAD rejection: `NotImplementedException: Cecil-free generic
      instance has no cached template (S2 bind skipped): CompareElems` + the SKIP
      record `template rebuild miss (S3 case): ...CompareElems` (the
      `hasMethodIdentityToken` rejection).

## 2. Root-cause re-audit -- DONE
- [x] Confirmed the `.neo` MethodRef table (`MethodReferencePatchInfo`) carries
      `Name` + `DeclaringType` (a `TypeReferencePatchInfo` with the full generic-
      instance structure: `IsGenericInstance` + `ElementType` + `GenericArguments`
      with `IsGenericParameter` args) + `Parameters[]`. NO `.neo` V6 needed.
- [x] Confirmed the MethodToken patch's `TokenRefIdx` is a **MethodRefTable**
      index (not TypeRef), per `BuildPatches` in NeoAssemblyWriter
      (`b.IndexMethodRef(cmr)`).
- [x] Confirmed the rejection was a defensive guard (the S3 MethodToken branch
      was a hard REJECT, mirroring the TypeToken branch BEFORE the TypeToken
      fix). The Cecil-free re-resolution is a SPECIFIC extension: rebuild a Cecil
      `MethodReference` from the MethodRef data + feed CloneAndPatch's EXISTING
      `GetMethodTokenHash` path.

## 3. Fix (Neo-gated) -- DONE
- [x] `NeoAssemblyLoader.cs`: added `BuildCecilTypeRefFromPatchInfo` (inverse of
      `TypeReferencePatchInfo.Create` -- generic-param -> synthetic
      GenericParameter; generic-instance -> `GenericInstanceType` with
      `GenericParameters` populated from the .neo keys; byref/array wrap; plain
      -> bare TypeReference), `BuildMethodReferenceCecilFree` (builds a Cecil
      `MethodReference` from the rebuilt declaring type + params + a
      System.Int32 return-type placeholder; rejects a generic-instance method),
      and `ResolveMethodRef` (the closure). Wired the closure at the S2
      template-bind loop.
- [x] `GenericMethodTemplate.cs`: `BuildFromNeoRecord` + `RebuildPatchesNoCecil`
      take the new `Func<int, MethodReference> resolveMethodRef`. The MethodToken
      T-identity branch re-resolves via `resolveMethodRef(r.TokenRefIdx)` into
      `PatchEntry.CecilToken`; a miss still rejects. Renamed
      `hasMethodIdentityToken` -> `hasUnresolvableToken` (covers both TypeToken
      and MethodToken misses).

## 4. Verify -- DONE
- [x] NeoStep25CecilFreeGeneric capstone: **18/18** (prior 15 held; added
      `WrapCompareElemsInt` + the G4 MethodToken T-identity cell).
- [x] Stash-toggle load-bearing: stash the 2 engine files -> G4 FAILs (15/16,
      the HEAD rejection, 1 template skipped on S3 MethodToken reject); pop ->
      18/18.
- [x] NeoStep smoke: **289 tests, 0 failed** (no regression).
- [x] Legacy-neutral: plain `Debug` build 0 errors.

## 5. Scope assessment -- DONE (minimal coherent subset, no deep rework)
- [x] The fix is the minimal coherent subset: the `constrained. T`-qualified
      callvirt (declaring type = generic instance over the method generic param
      T, e.g. `IComparable<T>::CompareTo`) is re-resolved Cecil-free via the
      MethodRef data. int-T (value-type constrained) covers the surface.
- [x] Reference-type concrete T (`CompareElems<string>`) is out of scope: the
      constrained. ref-type ExecuteNeo arm throws on HEAD (an engine gap, fails
      the A JIT reference too). Noted, wrapper omitted.
- [x] Generic-instance-method call: defensively rejected
      (`BuildMethodReferenceCecilFree` returns null for `IsGenericInstance`); out
      of scope (not the constrained-callvirt shape).
