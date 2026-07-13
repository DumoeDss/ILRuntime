# Proposal: neo-ilruntimetype-runtime-bridge (Wave-2 child C12)

## Why
Full Neo smoke (post wave-2 C1/C4/C2) is **133 failed**. Cluster C12 is 10 of those
(DelegateTest25/28-35 + EnumTest21), all throwing `System.ArgumentException`:
`Type must be a runtime Type object. (Parameter 'type')` (Delegate.CreateDelegate) and
`Type must be a type provided by the runtime. (Parameter 'enumType')` (Enum.ToObject).

## Root cause (RE-AUDIT confirmed, Neo-vs-Legacy)
An `ILRuntimeType` (ILRuntime's wrapper for an IL type) is passed where .NET reflection
requires a REAL runtime `System.Type` (a `System.RuntimeType`). Legacy bridges this via
hand-written redirects registered on `RedirectMap`:
- `DelegateCreateDelegate/2/3` (CLRRedirections.cs:1607/1666/1733) -- detect
  `t is ILRuntimeType` + `it.IsDelegate`, build an IL delegate adapter instead of calling
  the framework `Delegate.CreateDelegate`.
- `EnumToObject` (CLRRedirections.cs:1509) -- detects `t is ILRuntimeType` + `it.IsEnum`,
  builds an `ILEnumTypeInstance` instead of calling the framework `Enum.ToObject`.

Under Neo, `RedirectMapNeo` is consulted EXCLUSIVELY (child-2 lineage). These hand-written
redirects were registered on Legacy's `RedirectMap` ONLY -- there was no Neo entry. So Neo
dispatch fell through to the reflection fallback (`CLRMethod.Invoke`, DelegateTest25 stack
trace `CLRMethod.cs:583` -> `ILIntepreter.Neo.cs:1256`) / the autogen `ToObject_3_Neo` stub
(System_Enum_Binding.cs:173) -- both pass the `ILRuntimeType` RAW to the framework method,
which throws `Type must be a runtime Type`.

This is the SAME defect class as child-6 (`InitializeArrayNeo`) and child-22
(`CreateInstanceNeo`): "hand-written Legacy redirect on RedirectMap only; needs a
Neo-signature twin on RedirectMapNeo." ONE bridge missing, manifesting on two API
surfaces (Delegate.CreateDelegate x3 overloads + Enum.ToObject x1). Confirmed:
- DelegateTest25 fails on Neo (`Type must be a runtime Type`); PASSES on Legacy.
- EnumTest21 fails on Neo (`Type must be a type provided by the runtime`); PASSES on Legacy.

## What changes (Neo-gated -> Legacy-neutral)
4 new Neo-signature redirects in `CLRRedirections.cs` (mirror the Legacy bodies; params
read in DECLARATION order via the Neo cursor `ReadNeoReference`/`ReadNeoInt32`; result
written via the existing `WriteNeoObjectResult` helper):
1. `DelegateCreateDelegateNeo` (Type, MethodInfo) -- mirrors DelegateCreateDelegate.
2. `DelegateCreateDelegate2Neo` (Type, object, string) -- mirrors DelegateCreateDelegate2.
3. `DelegateCreateDelegate3Neo` (Type, object, MethodInfo) -- mirrors DelegateCreateDelegate3.
4. `EnumToObjectNeo` (Type, int) -- mirrors EnumToObject; under Neo an `ILEnumTypeInstance`
   stores its value as raw bytes in `Primitives` (a `byte[]` sized to the underlying
   primitive), so the int is written as the underlying-type bytes (sign-extended for a
   long-backed enum, truncated for byte/short) via `Buffer.BlockCopy`.

Registered on `RedirectMapNeo` in the AppDomain ctor (first-registered-wins: this ctor runs
BEFORE the test-harness `CLRBindings.Initialize` autogen `Register`, preempting the autogen
`ToObject_3_Neo` stub and any autogen Delegate.CreateDelegate stub).

## Impact
Expect full-smoke 133 -> 123 (C12 cluster flips green: 10 tests). NeoStep 380/0 (no
regression). Legacy-neutral (all changes `#if ENABLE_NEO_MODE`).

## Capability
`neo-dispatch` (the Neo redirect/RedirectMapNeo surface; sibling of child-6/child-22). No
spec-delta required for the smoke fix (mirror of existing Legacy redirects); the durable
finding is the defect-class pattern.
