# Proposal: neo-jit-generic-method-specialization (Wave-2 child of neo-overhaul)

## Context
Cluster D row from `fullsmoke-ground-60.md`: CLRBindingTest07/08 fail under Neo with
the autogen-binding-invoke child's note "JIT emits LoadAsset[TestCLRBinding] for
<String>/<Int32> callers -- a generic-method redirect issue". Baseline full Neo
smoke: 932 ran, 54 failed.

## Re-audit (verified at 54)
- CLRBindingTest07/08 PASS in NAME-FILTER isolation but FAIL in the full smoke. This
  is a test-ORDERING / shared-state bug.
- Reproduced with the `CLRBindingTest0` filter (06/07/08/09 together): 9 ran, 2 failed
  (07/08). CLRBindingTest06 runs FIRST and pollutes the generic-method cache.
- Full-smoke errors:
  - CLRBindingTest07: InvalidCast (String -> TestCLRBinding) @ LoadAsset_1_Neo (which
    is the `LoadAsset<TestCLRBinding>` autogen stub) @ Neo.cs:1268/3960.
  - CLRBindingTest08: IndexOutOfRange (List.get_Item) @ LoadAsset_1_Neo @ Neo.cs:3960.

## Root cause (Neo-only; JIT-dump + diagnostic confirmed)
The call chains are:
- CLRBindingTest07 -> CLRBindingTest07Sub<String> -> CLRBindingTest06Sub<String> ->
  binding.LoadAsset("222", obj) = `LoadAsset<String>` (a generic METHOD on CLR type
  TestCLRBinding).
- CLRBindingTest08 -> CLRBindingTest07Sub2<TestCLRBinding,Int32> ->
  CLRBindingTest07Sub<Int32> -> CLRBindingTest06Sub<Int32> -> `LoadAsset<Int32>`.

`CLRBindingTest06Sub<T>` is a generic IL method. Its body calls `LoadAsset<T>` (a
T-qualified generic-method call). Under Neo, generic IL methods are compiled once to a
TEMPLATE (JIT capture) then CloneAndPatch per concrete T (`GenericMethodTemplate.cs`).

`ExtractPatches` records T-IDENTITY operand sites so `DoCloneAndPatch` can re-resolve
them per instance. The Step-22 V1 matrix DEFERRED the Call/Callvirt MethodToken case
(GenericMethodTemplate.cs original comment: "T-qualified Call/Callvirt (MethodToken)
is not exercised by the Step-22 V1 matrix and is deferred"). The `default:` arm skipped
ALL call opcodes. Consequences:

1. `HasIdentityToken()` returned FALSE for `CLRBindingTest06Sub<T>` (its only T-identity
   site is the `LoadAsset<T>` call).
2. `TryInstantiate` then took the REF-SHARE path (all-ref args && !HasIdentityToken):
   it built `template.RefBody` ONCE from the FIRST all-ref instantiation and reused it
   for every subsequent all-ref instantiation WITHOUT re-patching.
3. CLRBindingTest06 (T=TestCLRBinding) ran first, so RefBody was built with
   `LoadAsset<TestCLRBinding>` baked into the call's Operand2. CLRBindingTest07 (T=String)
   and the ref-arg portion of CLRBindingTest08 reused that body -> the inner call
   dispatched `LoadAsset_1_Neo` (the TestCLRBinding stub) -> cast/collection errors.

## The fix (Neo-only, Legacy-neutral; file is `#if ENABLE_NEO_MODE`-gated)
Two parts in `GenericMethodTemplate.cs`:

1. `ExtractPatches`: add cases for `Call/Callvirt/Callvirt_IL/Callvirt_CLR/Call_Redirect`
   that record a `MethodToken` patch on `Operand2` (where the method hash lives, per
   `InitializeFunctionParam`) when `HasGenericParameter(callToken)` (the symbol's Cecil
   MethodReference). This makes `HasIdentityToken()` true for bodies with T-qualified
   calls (no wrongful ref-share) and gives `DoCloneAndPatch` the token to re-resolve.
   The trailing callvirt of a `constrained.` pair is skipped (already handled by the
   Constrained case; its symbol is scrambled per BLOCKER-1).

2. `DoCloneAndPatch` patch-apply loop: add a RELIABILITY CROSS-CHECK for MethodToken
   patches. The call body op's symbol can be STALE after INLINING (the inliner removes
   an inlined call but leaves its Cecil instruction linked at a body index that now
   holds a DIFFERENT call -- e.g. `Test<T>` inlines `Output<T>` down to
   `callvirt.il ACallback::Invoke`, but the symbol at that index still points at the
   removed `call Output<T>`). The check compares the body op's CURRENT method name
   (`appdomain.GetMethod(body[idx].Operand2)` -- Operand2 is the reliable capture-T
   hash) to the Cecil token's re-resolved method name. If they differ, the symbol was
   scrambled -> SKIP the patch (keep the capture-T hash = identical to the token-free
   ref-share semantics). Without this guard the patch corrupts the wrong call's
   Operand2 -> wrong pCnt -> LowerNeoOffsets IndexOOB (the GenericMethodTest3
   regression surfaced by the first iteration of the fix).

## Verify (truth = full-smoke number)
- Name-filter: CLRBindingTest0 -> 0 failed (was 2). GenericMethodTest3 -> 0 failed
  (was a regression in fix iteration 1; resolved by the cross-check).
- FULL SMOKE: 54 -> 51 (-3). FIXED: CLRBindingTest07, CLRBindingTest08, JsonTest9
  (bonus -- JsonTest9 also had a T-qualified-call ref-share). NEW failures: 0.
- NeoStep 398/0 (no regression).
- Stash-toggle (whole file): reverted -> CLRBindingTest0 = 2 failed; restored -> 0.
- Legacy-neutral: plain Debug + useRegister=true + NeoStep = 398 ran / 18 failed
  (documented pre-existing Legacy set; file is Neo-gated).

## Scope note
Generic-method instantiation is core. The fix is narrowly scoped to the
ExtractPatches call case + the DoCloneAndPatch reliability cross-check. It mirrors the
existing Constrained MethodToken handling (MAJOR-2). The `GetMethodTokenHash` re-resolution
and the `AppDomain.GetMethod(token, declaringType, instance, out invalidToken)` path
(preferred over the mapMethod hash cache which can return invalidToken=false for a
cached generic token) are reused unchanged.
