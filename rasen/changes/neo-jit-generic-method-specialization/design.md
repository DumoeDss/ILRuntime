# Design: neo-jit-generic-method-specialization

## The defect class
A Neo generic-method template (`GenericMethodTemplate`) is captured once from the
first capture-eligible instantiation and CloneAndPatch'd per concrete T. The template
records T-IDENTITY operand sites (`PatchEntry`) so `DoCloneAndPatch` can re-resolve
them per instance. `TryInstantiate` uses `HasIdentityToken()` to decide between
REF-SHARE (reuse one body for all all-ref-T instantiations -- valid ONLY when the body
is token-free, i.e. ref-T-invariant) and CloneAndPatch (re-specialize per T).

The Step-22 V1 matrix DEFERRED the Call/Callvirt MethodToken case. So a generic method
whose only T-identity site is a T-qualified call (e.g. `CLRBindingTest06Sub<T>` calling
`LoadAsset<T>`) had `HasIdentityToken() == false` -> wrongful ref-share -> the capture-T
call target baked into every instance -> wrong CLR redirect dispatch.

## The fix (two parts, both in GenericMethodTemplate.cs, Neo-gated)

### D1: ExtractPatches records MethodToken for T-qualified calls
Add `Call/Callvirt/Callvirt_IL/Callvirt_CLR/Call_Redirect` to the switch. Field =
`Operand2` (where `InitializeFunctionParam` stamps the method hash). CecilToken =
`sym.Instruction.Operand`. Record only when `HasGenericParameter(token)`. Skip the
trailing callvirt of a constrained pair (the Constrained case already owns its
Operand2 from the pre-captured reliable Cecil pair; BLOCKER-1 symbol scrambling).

This makes `HasIdentityToken()` true for T-qualified-call bodies and gives
`DoCloneAndPatch` the token to re-resolve via `GetMethodTokenHash` (which returns
`m.GetHashCode()` when `invalidToken` -- T-dependent).

### D2: DoCloneAndPatch reliability cross-check (CRITICAL -- without it, a regression)
The call body op's symbol can be STALE after INLINING. Symptom (iteration 1):
`GenericMethodTest3`'s `Test<T>` inlines `Output<T>` down to
`callvirt.il ACallback::Invoke`, but the symbol at that body index still points at the
removed `call Output<T>`. D1 recorded a patch with `CecilToken = Output<T>` for the
Invoke op -> `DoCloneAndPatch` stamped Output's hash onto Invoke's Operand2 -> wrong
pCnt (3 vs 2) -> `LowerNeoOffsets` read `Register4 = -1` -> IndexOOB.

The cross-check: for a MethodToken patch, compare the body op's CURRENT method Name
(`appdomain.GetMethod(body[idx].Operand2)` -- Operand2 is the reliable capture-T hash)
to the Cecil token's re-resolved method Name (`appdomain.GetMethod(CecilToken,
declaringType, instance, out _)`). On mismatch the symbol was scrambled -> SKIP the
patch (keep the capture-T hash = identical to the token-free ref-share semantics for
that call). Safe defaults: if either lookup is null, APPLY the patch (no false skip).

This distinguishes:
- `LoadAsset<T>` (CLRBindingTest07/08): reliable symbol -> names match -> patch applied
  -> correct per-T dispatch.
- `ACallback::Invoke` after Output inlined (GenericMethodTest3): scrambled symbol ->
  names differ ("Invoke" vs "Output") -> patch skipped -> Invoke keeps capture-T hash
  (delegate invoke is T-erased at runtime) -> no crash, correct behavior.

## Eliminated hypotheses
- "The autogen `LoadAsset_*_Neo` stubs are stale" (autogen-binding-invoke child note):
  DISPROVEN. The stubs are correct; the JIT served the WRONG stub (`LoadAsset_1_Neo` =
  `LoadAsset<TestCLRBinding>`) because the template body's call Operand2 was not
  re-specialized per T (ref-share baked the capture-T target).
- "The redirect resolution (`TryGetRedirection` GetGenericMethodDefinition precedence)
  is wrong": DISPROVEN. The redirect resolution is correct; it received the wrong
  method hash from the un-specialized template body.
- "The bug is in `DoCloneAndPatch`/`RunNeoBackHalf`": DISPROVEN for the target. The
  regression in iteration 1 WAS in this area but only because D1 fed it a scrambled
  token; D2's cross-check resolves it. The back-half itself is correct for a correctly-
  patched body.
- "JsonTest9 (bonus fix) is crash-order noise": DISPROVEN. JsonTest9 fails in NAME-
  FILTER ISOLATION without the fix (1 failed) and passes with it -- causally linked
  (JsonTest9 uses a generic method with a T-qualified call that was ref-shared).

## Why "ref-share" exists and when it is valid
`TryInstantiate` ref-shares when `allRef && !HasIdentityToken()`: for a body whose
T-identity sites are all type-erased at runtime across reference T's (no `Box T`,
`Isinst T`, or T-qualified call that dispatches T-specifically), any all-ref-T arg-set
yields a byte-identical body. This is a valid optimization (object == string for a
token-free body). The bug was that a T-qualified call IS a T-identity site but was not
recognized, so ref-share fired incorrectly. D1 makes it recognized; D2 keeps ref-share
semantics for the inlined/scrambled subset where the call is T-erased at runtime.

## Scope / risk
- The change is additive (new switch case + a guarded skip). The TypeToken/Constrained
  paths are unchanged. The non-MethodToken patch path is unchanged.
- The cross-check adds two `appdomain.GetMethod` lookups per MethodToken patch (a
  handful per generic-method instantiation) -- negligible.
- Neo-gated file -> Legacy-neutral by construction (verified: plain Debug NeoStep
  398/18 = pre-existing Legacy set).
