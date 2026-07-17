# Review: neo-ilruntimetype-runtime-bridge (Wave-2 child C12)

Reviewer: independent (did NOT write this code). Verified against real code + real runs.
Branch: `features/object-model-overhaul`. Neo = `ExecuteNeo` under `ENABLE_NEO_MODE`.

## Verdict: APPROVE-WITH-FINDINGS

The change is correct, regression-free, and 100% Neo-gated. One Minor finding is a
documentation-accuracy nit (benign, no behavior impact). Ship it.

## Are the 4 Neo redirects faithful Legacy twins?

**Yes for the load-bearing semantics (the ILRuntimeType bridge). Yes verbatim for all
3 delegate twins. EnumToObjectNeo diverges from Legacy in the 2 non-IL branches -- benign
and arguably MORE correct.**

Verified by side-by-side read of the Legacy bodies (CLRRedirections.cs:1738 `EnumToObject`,
:1836 `DelegateCreateDelegate`, :1895 `DelegateCreateDelegate2`, :1962 `DelegateCreateDelegate3`)
vs the Neo twins (CLRRedirections.cs ~:799/:850/:907/:985):

- **DelegateCreateDelegateNeo** (Type, MethodInfo) -- FAITHFUL TWIN. Params read in
  declaration order (`t` then `mi` -- Legacy reads stack-reverse `mi` then `t`; the Neo
  forward order is the documented correct adaptation). ILRuntimeType+IsDelegate ->
  `FindDelegateAdapter`; ILRuntimeWrapperType -> adapter or host `Delegate.CreateDelegate`;
  else host `Delegate.CreateDelegate`. Error messages match. Result via
  `WriteNeoObjectResult`. CONFIRMED.

- **DelegateCreateDelegate2Neo** (Type, object, string) -- FAITHFUL TWIN. Reads `t, obj,
  name`; `obj==null` -> ArgumentNullException; ILRuntimeType/ILTypeInstance + ILRuntimeWrapperType
  + else branches byte-match Legacy, including the two distinct "Cannot find method"
  FullName sources (`it.FullName` vs `ii.Type.FullName`). CONFIRMED.

- **DelegateCreateDelegate3Neo** (Type, object, MethodInfo) -- FAITHFUL TWIN. Reads
  `t, obj, mi`; `obj!=null` path uses `GetDelegateAdapter` then `FindDelegateAdapter` with
  the `ilMethod.IsExtend` -> `ParameterCount - 1` fallback; `obj==null` path caches
  `ilMethod.DelegateAdapter`. ILRuntimeWrapperType + else match. CONFIRMED.

- **EnumToObjectNeo** (Type, int) -- FAITHFUL TWIN for the ILRuntimeType/IsEnum branch
  (the one EnumTest.Test21 exercises): both build an `ILEnumTypeInstance` carrying the
  value. The Neo byte-write is the correct object-model adaptation:
  `ILEnumTypeInstance.Primitives` returns the `protected byte[] fields` sized to the
  underlying primitive (ILTypeInstance.cs:101-103, 303-310; the Legacy `ins[0]=val` indexer
  is `#if !ENABLE_NEO_MODE`). The write `fields.Length==8 ? GetBytes((long)val) :
  GetBytes(val)` + `Buffer.BlockCopy(minLen)` is CORRECT: long-backed enums get 8
  sign-extended bytes; int-backed get 4; short/byte get the low bytes truncated. Matches
  the size-based read-back at ILTypeInstance.cs:194-208.

  **MINOR DIVERGENCE (see Minor-1):** the ILRuntimeWrapperType and else (fallback)
  branches call `Enum.ToObject(...)`, whereas Legacy `EnumToObject` returns
  `Enum.GetName(...)` (a string). The Neo choice matches the method's true contract
  (`Enum.ToObject(Type,int)` returns an enum object, not its name) AND matches the prior
  autogen `ToObject_3_Neo` stub (System_Enum_Binding.cs:179 -> `System.Enum.ToObject`),
  so it is NOT a regression. It just is not a literal Legacy mirror in those 2 branches.

### Param-order / cursor correctness (review dim 1a)
`ReadNeoReference` and `ReadNeoInt32` both `curPrim += 4` (ILIntepreter.Neo.cs:27-31,
122-136); `ReadNeoReference` honors the null sentinel (`idx>=0`). Every Neo twin advances
the cursor in declaration order with no off-by-one (each param +4, consistent with the
autogen `ToObject_3_Neo` arg-read at System_Enum_Binding.cs:177-178). No off-by-one.

### Result write (review dim 1c)
`WriteNeoObjectResult` (CLRRedirections.cs:657-668) is null-aware: `retDst==null` -> no-op;
`result==null` -> writes -1 sentinel; else stores at `retRefBase` and writes the index.
Correct for redirects returning a delegate adapter / ILEnumTypeInstance / null. Using
`WriteNeoDelegateResult` instead would have been WRONG (not null-aware) -- the implementer
chose correctly.

## Registration correctness + no-overfire (review dim 2)
The 4 `RegisterCLRMethodRedirectionNeo` calls (AppDomain.cs:282/307/312/317) reuse the
SAME `mi`/`i` MethodBase tokens the adjacent Legacy `RegisterCLRMethodRedirection` calls
use -- tokens are correct by construction. `RegisterCLRMethodRedirectionNeo` uses a
`ContainsKey` guard (AppDomain.cs:1181) -> first-registered-wins; the ctor runs before
the test-harness `CLRBindings.Initialize`, so `EnumToObjectNeo` preempts the autogen
`ToObject_3_Neo` stub (System_Enum_Binding.cs:55 -- confirmed: that stub at :173-179
passes `ILRuntimeType` RAW to `System.Enum.ToObject`, the "Type must be a type provided by
the runtime" bug). No autogen Delegate.CreateDelegate stub exists in the binding set, so
the Delegate twins fill a reflection-fallback hole rather than shadow another redirect.
`EnumToObjectNeo` is gated to `ToObject(Type,int)` only -- the GetValues/GetNames/HasFlag/
CompareTo autogen stubs are different methods, not shadowed. No overfire.

## Full-smoke delta 133 -> 122 (review dim 3)
Trust implementer's 122. Spot-runs (all REAL, Debug_Neo, this build):

| Test | Result |
|------|--------|
| DelegateTest.DelegateTest25 | PASS (Ran 1, 0 failed) |
| DelegateTest.DelegateTest28 | PASS |
| DelegateTest.DelegateTest29 | PASS |
| DelegateTest.DelegateTest30-35 (cluster) | PASS (each invoked clean) |
| DelegateTest.DelegateTest35 | PASS |
| EnumTest.Test21 | PASS (Ran 1, 0 failed) |
| NeoStep (full) | 380/0 -- regression-free |

`DelegateTest.DelegateTest3*` cluster: 10 ran, 1 failed = **DelegateTest36**
(`NotSupportedException: Derived classes must provide an implementation` at
ExecuteNeo:3796 = `InvokeNeoClrMethod` -> abstract-method throw). DelegateTest36 is NOT a
C12 cluster member (C12 = 25/28-35 + EnumTest21); it is a Step-19+ unimplemented-opcode
TODO (per CLAUDE.md, expected in-progress state), NOT a C12 regression.

## Only-Neo-gated / no regression (review dim 4)
CONFIRMED. Every added line in both files is inside `#if ENABLE_NEO_MODE`. The Legacy
`RegisterCLRMethodRedirection(...)` calls and the Legacy redirects on `RedirectMap` are
untouched. Legacy compiles none of the new code. Both builds clean (Debug_Neo CLI: 0
errors; Debug TestCases: 0 errors).

## Findings

### Minor-1 -- EnumToObjectNeo non-IL branches diverge from Legacy (benign)
`EnumToObjectNeo` ILRuntimeWrapperType + else branches return `Enum.ToObject(...)`
(enum object); Legacy `EnumToObject` returns `Enum.GetName(...)` (string). This is the
ONLY material divergence from "faithful twin". It is:
- Arguably MORE correct (the method IS `Enum.ToObject` -- returning a name string is a
  latent Legacy quirk).
- Regression-free: matches the prior Neo autogen `ToObject_3_Neo` behavior exactly, so no
  Neo test could have depended on the Legacy string-returning path (Neo never ran Legacy's
  redirect).
The proposal/durable-findings "mirrors Legacy EnumToObject" wording is slightly overstated
for those 2 branches. Recommend a one-line comment note that the fallback branches
intentionally follow `Enum.ToObject` semantics (matching the autogen stub), NOT Legacy's
`Enum.GetName`. No code change required.

### Trivial-1 -- test-name shorthand
`durable-findings.md`/`tasks.md` refer to "EnumTest21", but the actual method is
`EnumTest.Test21` -- a name-filter "EnumTest21" matches 0 tests. The implementer obviously
ran the right test (results are valid and I reproduced the PASS). Pure doc shorthand nit.

## Files
- `ILRuntime/Runtime/Enviorment/CLRRedirections.cs` (+~210, 4 new Neo redirects inside the
  existing `#if ENABLE_NEO_MODE` block).
- `ILRuntime/Runtime/Enviorment/AppDomain.cs` (+~20, 4 `RegisterCLRMethodRedirectionNeo`
  calls, each under `#if ENABLE_NEO_MODE`).
