# Design — neo-aot-generic-tidentity

**Date:** 2026-07-11  **Capability:** neo-optimizer (AOT)  **Wave:** completion-3, child-8 follow-up
**Status:** DONE (re-audit: a specific tractable guard, NOT a deep rework; .neo V5 already carried the data)

## The gap (re-audited)
A Cecil-free generic method whose body has a T-identity-token patch (`Box T` /
`Ldobj T` / `Initobj T` / `Stobj T` / `Unbox.Any T` where T is a method generic
param) — at Cecil-free load + instantiation, the S3 `GenericMethodTemplateOps.
BuildFromNeoRecord` REJECTED it (`hasIdentityToken` / non-none `CecilTokenKind`).

So `T BoxUnbox<T>(T v){ object o = v; return (T)o; }` (Box T + Unbox.Any T)
failed Cecil-free. The non-T-identity case (child-8's `Echo<T>`) worked.

## Re-audit verdict: a SPECIFIC guard, NOT a real .neo gap (5-for-5 holds)
The `.neo` **already carried enough info** to re-substitute Cecil-free. The
`NeoPatchEntryRecord` (Step 23) has, per patch:
- `GenericParamIdx` — which method generic arg drives the value
- `TokenRefIdx` — TypeRefTable index (for TypeToken: the generic-param's TypeRef,
  whose Name is e.g. "T")
- `CecilTokenKind` — 0=TypeReference, 1=MethodReference, 2=none
- `Kind`/`Field`/`InstrIdx`

So NO `.neo` V6 / writer / reader change was needed. The rejection was a
DEFENSIVE guard (the comment said "needs a Cecil TypeReference to re-resolve,
which the Cecil-free .neo does not carry in a form S2 re-resolves"). But child-8
already built the Cecil-free `resolveVariableType` closure that returns a
synthetic Cecil `GenericParameter` (Name="T", IsGenericParameter=true) for a
generic-param name — the SAME object `BuildInitObjPrefix` already feeds to
`instance.GetTypeTokenHashCode(vt)`. The fix is to feed it for T-identity
TypeToken patches too.

## The fix (3 engine files, all `#if ENABLE_NEO_MODE`-gated or Neo-safe)

### 1. `GenericMethodTemplate.cs` — `RebuildPatchesNoCecil` (the core fix)
Before: REJECTED every TypeToken/MethodToken patch (`hasIdentityToken = true` ->
`BuildFromNeoRecord` returned null -> the template was skipped -> the generic
method fell back to JIT, which a Cecil-free AppDomain cannot run).

After: for a **TypeToken** T-identity patch (`Kind==TypeToken`,
`CecilTokenKind==0`), re-resolve the generic-param via the `resolveVariableType`
closure (`TokenRefIdx` -> the closure -> a synthetic Cecil GenericParameter
whose IsGenericParameter=true + Name="T") and store it in `PatchEntry.
CecilToken`. CloneAndPatch's EXISTING TypeToken path
(`instance.GetTypeTokenHashCode(pe.CecilToken)`) then re-derives the concrete T
hash via `FindGenericArgument(token.Name)` — no Cecil module needed, no new
code path. A resolution miss falls through to reject (the additive contract).

A **MethodToken** T-identity patch (a `constrained.` T-qualified callvirt) still
REJECTS (`hasMethodIdentityToken`) — a Cecil-free method-token re-resolution on
the concrete T is a deeper sub-case (needs the concrete-T method ref), NOT in
the requested surface (`Box T` / `Ldobj T` / `Initobj T` are all TypeToken).

### 2. `ILType.cs` — `FindGenericArgument` null-`definition` guard (pre-existing latent bug, surfaced by the fix)
`ILType.FindGenericArgument` accessed `definition.GenericParameters` with no
null-`definition` guard. A Cecil-free ILType has `definition == null` (built
from a .neo record), so this NRE'd. Reached via `ILMethod.FindGenericArgument`
-> `GetTypeTokenHashCode` -> the CloneAndPatch T-identity T-substitution AND the
struct-T Initobj prefix rebuild. Guarded with `definition != null &&`.

### 3. `ILType.cs` — `IsByRef` Cecil-free guard (pre-existing latent bug, surfaced by the fix)
`ILType.IsByRef` returned `typeRef.IsByReference` with no null-`typeRef` guard.
A Cecil-free ILType has `typeRef == null`, so this NRE'd. Reached via
`IsValueType` -> `AllocateLocalStackSpaces` (the `HasThis` declaring-type probe)
on a Cecil-free generic instance. Guarded with `if (isNeoAotType) return false;`
(a Cecil-free ILType is never a byref).

These two latent guards were PRE-EXISTING bugs on HEAD (they caused G2 +
`WrapEchoStruct` to fail with NRE even before this change — confirmed by the
stash-toggle: 12/15 without the fix). They were masked because the S3 rejection
prevented any T-identity / fresh-instance Cecil-free generic dispatch from
REACHING them. The T-identity fix unblocked the path; the guards close it.

## Scope notes / out of scope
- **MethodToken T-identity** (`constrained.` T-qualified callvirt) still rejects
  — the deeper sub-case. Not in the requested surface.
- **Box/Unbox of an IL value type** (`BoxUnbox<NeoStep25CegVal>`) fails even in
  a Cecil-loaded JIT run (the "A JIT" reference) — an engine-level Box/Unbox-of-
  IL-VT gap, NOT a T-identity Cecil-free regression. The struct-T wrapper is
  omitted; int-T (`WrapBoxUnboxInt`) + the G3 fresh-instance cell cover the
  T-identity surface.
- **Duplicate generic-def shells** (`GetMethod` appends a generic INSTANCE per
  call resolution, ILType.cs:2465) is a pre-existing accumulation pattern; the
  G3 cell selects the cached (authoritative) open def.

## Verification
- **NeoStep25CecilFreeGeneric capstone: 15/15** (added: G3 BoxUnbox<T> T-identity
  Cecil-free dispatch + the BoxUnbox probe method + WrapBoxUnboxInt wrapper).
- **Stash-toggle (load-bearing):** stash the 2 engine files -> G3 + G2 +
  WrapEchoStruct FAIL (12/15, the HEAD state, 1 template skipped on S3
  rejection); pop -> 15/15 PASS.
- **NeoStep smoke: 0 tests failed** (no regression). NeoStep25LoadExec 28/28,
  NeoStep22/23 0 failed (no `.neo` format regression — no V6 bump).
- **Legacy-neutral:** plain `Debug` build 0 errors (all Neo-gated; the
  FindGenericArgument guard is unconditional C# safe for Legacy which always has
  a non-null definition).
