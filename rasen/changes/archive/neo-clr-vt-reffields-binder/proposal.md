# Proposal — neo-clr-vt-reffields-binder

## Why
The full Neo smoke prints

> `CLR value type with reference fields and no ValueTypeBinder (Step 13b): register a binder. Type: System.RuntimeFieldHandle`

**18 times pre-crash (6+6+3+3, half are "Rethrown as" duplicates of the same throw).**
Every single hit is the SAME type — `System.RuntimeFieldHandle` — produced by C#
array initializers (`new int[]{ ... }` with enough elements) lowering to
`newarr; dup; ldtoken <PrivateImplementationDetails blob field>; call RuntimeHelpers.InitializeArray`.

## Root cause (confirmed by code + smoke)
- **Throw site**: `ILRuntime/CLR/Method/CLRMethod.cs:501` (the Neo reflection-fallback
  `Invoke(byte*)`) and the twin autogen stub at
  `ILRuntime/Runtime/CLRBinding/BindingGeneratorExtensions.cs:206`. Both throw the same
  message. The 18 hits fire on the **reflection-fallback** path (InitializeArray is
  redirected, not autogen-bound).
- **Why it fires**: `RuntimeHelpers.InitializeArray` is registered ONLY on the Legacy
  `RedirectMap` (`AppDomain.cs:150`, `RegisterCLRMethodRedirection`). Neo dispatch uses
  `RedirectMapNeo` **exclusively** (child-2 durable finding; `CLRMethod.cs:151`
  `TryGetRedirection(appdomain.RedirectMapNeo, ...)`), which has NO entry for it. So under
  Neo the call falls through `InvokeNeoClrMethod` (`ILIntepreter.Neo.cs:988-997`) to the
  reflection `Invoke(byte*)`, which marshals the `RuntimeFieldHandle` param.
- `RuntimeFieldHandle` is a CLR value type with a managed reference field (on net8.0 it
  wraps `RuntimeFieldInfoInternal`), so `NeoClrStructHasReferenceField` returns true
  (`CLRMethod.cs:642`), and with no `ValueTypeBinder` the Step-13b guard throws.

## Why the binder / generic-marshal options are ruled out
- **Register a binder for `RuntimeFieldHandle`** — not sensible. It is an opaque
  CLR-system token (no meaningful field decomposition). More importantly the binder API
  (`ValueTypeBinder.cs`) is Legacy-`StackObject`-based; the autogen comment at
  `BindingGeneratorExtensions.cs:167-169` states outright *"a Neo-cursor binder API does
  not exist yet"*. A binder cannot feed the Neo flat-bytes reader.
- **Generic marshal — track ref fields in any no-binder CLR struct** — too broad/risky.
  The Neo model lays out a no-binder CLR struct as pure flat bytes with `RefCount = 0`
  (`Optimizer.Neo.cs:1577-1599`); its GC refs are inline and untracked (the `byte[]`
  Primitives region is not a GC root for interior pointers). Tracking them generically
  requires a runtime auto-decomposition into primitive-bytes + a managed-ref run (i.e. an
  auto-binder), including managed byte-offsets of private ref fields that reflection
  cannot reliably yield. That is a large future feature, not one child.
- These 18 hits are NOT a random sample of ref-field CLR structs — they are 100% the
  array-initializer intrinsic. The correct, narrowly-scoped fix is to **give
  `RuntimeHelpers.InitializeArray` a Neo redirect** (the child-2 follow-up:
  *"RuntimeHelpers.InitializeArray has no RedirectionNeo -> array initializers can't
  complete end-to-end in Neo"*). The redirect intercepts before the reflection-fallback
  marshal, so the `RuntimeFieldHandle` Step-13b NIE is never reached.

## What changes
1. Add `CLRRedirections.InitializeArrayNeo` (`ILRuntime/Runtime/Enviorment/CLRRedirections.cs`,
   `#if ENABLE_NEO_MODE`): read param 0 (the Array) via `ReadNeoReference`, read the
   initializer `byte[]` from param 1, `Marshal.Copy` it into the array (mirror the Legacy
   `InitializeArray` body at `CLRRedirections.cs:232-380`). No return value (void method).
2. Register it on `RedirectMapNeo` in the `AppDomain` ctor (`AppDomain.cs:~150`,
   `#if ENABLE_NEO_MODE`, alongside the existing Legacy registration) via
   `RegisterCLRMethodRedirectionNeo`.
3. **Load-bearing sub-task — make the blob reachable.** `CopyNeoCallArguments`
   (`ILIntepreter.Neo.cs:427`) copies each param by the CALLEE's declared size; param 1 is
   `RuntimeFieldHandle` (~8 managed bytes), but the initializer blob is N bytes (128 for a
   32-int array). The data source IS available: `ILTypeInstance.cs:77-81` stores the RVA
   `byte[] InitialValue` of the `<PrivateImplementationDetails>` blob field as a managed
   reference in `ManagedObjects[offset.ReferenceOffset]`. The Neo `ldtoken` field path
   (`ILIntepreter.Neo.cs:1575-1617`) must surface that `byte[]` as a reference so it flows
   through param 1 to the redirect (exactly mirroring the Legacy redirect's
   `data = param[1] as byte[]`, `CLRRedirections.cs:237`). The apply worker verifies
   empirically what param 1 currently holds (TC5) and adjusts the `ldtoken` field arm
   (push the `byte[]` ref) so the redirect reads it via `ReadNeoReference`.
4. Add `TestCases/NeoClrVtReffieldsBinderTest.cs` with a large array-initializer probe
   that FAULTs (uncaught Step-13b NIE) without the fix and PASSES (correct contents) with
   it. The existing `NeoStepLdtoken_TC5_ArrayInitializerFieldPath` is forward-compatible:
   its `catch (NotImplementedException)` becomes dead and the `arr[i] == 100+i` contents
   assertion takes over, validating the redirect end-to-end.

## Scope / deferral
- The generic no-binder-ref-field-CLR-struct marshal (the broad reading of "Step 13b")
  stays guarded by the existing NIE for genuine cases (a user CLR struct with ref fields
  passed to a non-redirected CLR method). This change only removes the
  `RuntimeFieldHandle`/InitializeArray manifestation.
- **Fallback** (if the `ldtoken` data-representation rework proves too invasive in apply):
  replace the generic Step-13b message at the reflection-fallback site with a tagged,
  accurate deferral (`"Step 13b / RuntimeFieldHandle: array initializer — needs a Neo
  InitializeArray redirect"`), and ship the redirect + registration alone so the NIE is
  bypassed for InitializeArray but the gap stays honestly labeled for any other path.

## Capability home
`neo-arrays` (covers `Newarr`; InitializeArray is the post-`newarr` initialization
intrinsic). ADDED requirement: a Neo redirect for `RuntimeHelpers.InitializeArray`.
Neo-gated (`#if ENABLE_NEO_MODE`) => Legacy-neutral by construction.

## F1-overlap verdict (child-5 follow-up)
**Does NOT subsume it.** Child-5 F1 = Stobj/Ldobj on CLR structs use `refCount = 0` and do
not consult the `ValueTypeBinder` — a different code site (the Stobj/Ldobj `ExecuteNeo`
arms), about in-frame CLR-struct store/load not tracking ref fields. This change adds a
call redirect that BYPASSES the binder marshal for one method; it does not touch the
Stobj/Ldobj arms or the binder mechanism. F1 remains a separate follow-up.
