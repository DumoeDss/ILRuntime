# Design — neo-aot-generic-methodtoken-tidentity

**Date:** 2026-07-11  **Capability:** neo-optimizer (AOT)  **Wave:** completion-3, child-8 MethodToken follow-up
**Status:** DONE (re-audit: a SPECIFIC extension of the TypeToken T-identity fix to the MethodToken path; the .neo V5 MethodRef table already carried the data)

## The gap (re-audited)
A Cecil-free generic method whose body has a `constrained. T`-qualified callvirt
where the callvirt's METHOD token is T-qualified (its declaring type contains a
method generic param -- e.g. `T M<T>(T a, T b) where T: IComparable<T> { return
a.CompareTo(b); }`, whose callvirt declares on the generic instance
`IComparable<T>`) -- at Cecil-free load + instantiation, the S3
`RebuildPatchesNoCecil` REJECTED the MethodToken T-identity patch
(`hasMethodIdentityToken`), so `BuildFromNeoRecord` returned null -> the template
was skipped -> the generic method fell back to JIT, which a Cecil-free AppDomain
cannot run.

The prior change (neo-aot-generic-tidentity, TypeToken half) fixed the TypeToken
T-identity (Box T / Ldobj T / Initobj T / Stobj T / Unbox.Any T) by re-resolving
a synthetic Cecil GenericParameter. It explicitly PARKED the MethodToken half ("a
Cecil-free method-token re-resolution on the concrete T is a deeper sub-case").
This change closes the MethodToken half.

## Re-audit verdict: a SPECIFIC extension of the TypeToken fix (NOT deep)
Per the 8-for-8 lesson, the MethodToken T-identity was likely a specific guard
miss, not a deep rework. The re-audit confirms it. The `.neo` **V5 MethodRef
table already carries** everything needed to rebuild a Cecil `MethodReference`
Cecil-free (no V6 bump, no writer/reader change -- mirroring the TypeToken
finding):

The `NeoPatchEntryRecord` for a MethodToken patch has `CecilTokenKind==1`
(MethodReference) + `TokenRefIdx` -> a **MethodRefTable** index (NOT a TypeRef
index; `BuildPatches` in NeoAssemblyWriter indexes it via
`b.IndexMethodRef(cmr)`). The MethodRefTable entry is a
`MethodReferencePatchInfo` carrying:
- `Name` -- the method name (e.g. "CompareTo")
- `DeclaringType` -- a `TypeReferencePatchInfo` that fully carries the generic-
  instance structure: `IsGenericInstance=true`, `ElementType` = the open def
  (e.g. `System.IComparable`1`), `GenericArguments` = `[(T, IsGenericParameter=
  true, Name="T")]`
- `Parameters[]` -- the param `TypeReferencePatchInfo`s
- `IsGenericInstance` (always false for a constrained-callvirt method token)

So NO `.neo` format change was needed. The rejection was a defensive guard that
mirrored the TypeToken guard BEFORE the TypeToken fix. The fix rebuilds a Cecil
`MethodReference` Cecil-free from this data + feeds it to CloneAndPatch's
EXISTING `GetMethodTokenHash` path (which the front-half already uses for the
Cecil-loaded case).

## The fix (2 engine files, all Neo-gated)

### 1. `NeoAssemblyLoader.cs` -- the Cecil-free MethodReference rebuild
Three new helpers (all `#if ENABLE_NEO_MODE` via the enclosing class):

- `BuildCecilTypeRefFromPatchInfo(TypeReferencePatchInfo)` -- the inverse of the
  serialize-side `TypeReferencePatchInfo.Create(TypeReference)`. Rebuilds a Cecil
  `TypeReference` Cecil-free:
  - `IsGenericParameter` -> the synthetic Cecil `GenericParameter` (Name="T",
    cached via `AcquireSyntheticGenericParam` -- the SAME stable object the
    TypeToken fix uses, so identity-hash lookups agree).
  - `IsGenericInstance` -> a Cecil `GenericInstanceType` whose `ElementType` is a
    bare `TypeReference` (FullName == the open def) + whose `GenericParameters`
    are populated from the `.neo` keys (so `AppDomain.GetType`'s generic-instance
    arm at AppDomain.cs:1676 reads `tr.GenericParameters[i].Name`) + whose
    `GenericArguments` are the rebuilt arg refs.
  - `IsByReference` / `IsArray` -> wrap the rebuilt ElementType.
  - plain -> a bare `TypeReference` (FullName == Name, via the existing
    `BuildBareCecilTypeReference`).
- `BuildMethodReferenceCecilFree(MethodReferencePatchInfo)` -- builds a Cecil
  `MethodReference(name, retType, declType)` from the rebuilt declaring type +
  params. The MethodRef table OMITS the return type (HybridPatch's
  MethodReferencePatchInfo); `appdomain.GetMethod` reads `_ref.ReturnType` but
  `GetMethod(name,...)` does NOT match on return type, so a bare
  `System.Int32` placeholder suffices (any non-null ref whose FullName
  `appdomain.GetType` resolves). Rejects a `IsGenericInstance` method (out of
  scope for the constrained-callvirt surface).
- `ResolveMethodRef(methodRefIdx, model)` -- the closure handed to
  `BuildFromNeoRecord`. Returns null on any miss (-> the additive contract: skip
  the bind, keep JIT).

The closure is wired at the S2 template-bind loop (next to
`resolveVariableType`).

### 2. `GenericMethodTemplate.cs` -- thread the closure through
- `BuildFromNeoRecord` takes a new `Func<int, MethodReference> resolveMethodRef`
  arg + threads it into `RebuildPatchesNoCecil`.
- `RebuildPatchesNoCecil`: the MethodToken T-identity branch (formerly a hard
  REJECT) now re-resolves via `resolveMethodRef(r.TokenRefIdx)` into
  `PatchEntry.CecilToken`. A miss still rejects (the additive contract). The
  `hasMethodIdentityToken` flag is renamed `hasUnresolvableToken` (it now covers
  BOTH a TypeToken miss and a MethodToken miss -- the same single skip signal).

CloneAndPatch's EXISTING `GetMethodTokenHash` path (DoCloneAndPatch, unchanged)
then re-derives the concrete-T method hash via `appdomain.GetMethod` (which
resolves the rebuilt declaring type's generic-param arg via
`contextMethod.FindGenericArgument` -> the concrete T -> finds the method on the
concrete `IComparable<T>`). No new apply path.

## How appdomain.GetMethod resolves the rebuilt ref (concrete T=int)
`appdomain.GetMethod(rebuiltRef, declaringType, instance)`:
1. `_ref.DeclaringType` = `GenericInstanceType{ IComparable<>, [T_gp] }`.
2. `GetType(typeDef, ...)` -> the `IsGenericInstance` arm: `gType.ElementType.
   FullName` = "System.IComparable`1" -> `GetType(string)` resolves the CLR type
   Cecil-free (via `Type.GetType` + referenceAssemblies); `tr.GenericParameters
   [0].Name` = "T" (populated from the .neo key); `gType.GenericArguments[0]`
   = T_gp (IsGenericParameter) -> `contextMethod.FindGenericArgument("T")` ->
   int.
3. `res = IComparable<>.MakeGenericInstance([("T", int)])` -> `IComparable<int>`.
4. `type.GetMethod("CompareTo", [int], null, retType)` -> `IComparable<int>::
   CompareTo(int)`.
5. `invalidToken` = true (the generic-instance declaring type ContainsGeneric-
   Parameter at the type level) -> returns `m.GetHashCode()` (the concrete method
   hash) -- exactly what the runtime Constrained arm reads at
   `cv->Operand2` (`ILIntepreter.Neo.cs`).

## Scope notes / out of scope
- **Reference-type concrete T** (`CompareElems<string>`): a `constrained. T`
  callvirt whose concrete T is a reference type hits a SEPARATE ExecuteNeo
  Constrained arm ("box-once no-op / ref-type this") that throws on HEAD -- an
  engine-level gap (`Step 17: constrained.callvirt on a null or unsupported
  constrained type is not handled`). It FAILS the "A JIT" reference (a Cecil-
  loaded run with no T-identity machinery in play), so it is NOT a T-identity
  Cecil-free regression. The string-T wrapper is omitted; int-T (value-type
  constrained) covers the MethodToken T-identity surface. (Mirrors the prior
  change's Box/Unbox-of-IL-VT scope note.)
- **Generic-instance-method call** (a `G<T>::M()` generic-instance method token):
  the `.neo` carries `IsGenericInstance` but a constrained-callvirt method token
  is never a generic-instance method; `BuildMethodReferenceCecilFree` rejects
  `IsGenericInstance` defensively. Not in the requested surface.
- **Declaring type IS T** (calling a method directly on the generic param, e.g.
  `T::SomeMethod()`): the rebuilt `DeclaringType` is the synthetic
  GenericParameter; `appdomain.GetMethod`'s `GetType(IsGenericParameter)` arm
  resolves it via `FindGenericArgument`. Covered by the same machinery, but not
  exercised by a cell (the common constrained-callvirt shape declares on the
  interface, sub-case 2).

## Verification
- **NeoStep25CecilFreeGeneric capstone: 18/18** (added: the `CompareElems<T>` T-
  identity body + the `WrapCompareElemsInt` wrapper + the G4 MethodToken T-
  identity fresh-instance cell; the prior 15 cells held).
- **Stash-toggle (load-bearing):** stash the 2 engine files -> G4 FAILs (15/16,
  the HEAD rejection `Cecil-free generic instance has no cached template (S2
  bind skipped): CompareElems`, 1 template skipped on S3 MethodToken reject);
  pop -> 18/18 PASS.
- **NeoStep smoke: 289 tests, 0 failed** (no regression).
- **Legacy-neutral:** plain `Debug` build 0 errors (all Neo-gated; the
  BuildFromNeoRecord signature change is inside `#if ENABLE_NEO_MODE`).
