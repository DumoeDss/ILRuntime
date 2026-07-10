# Tasks — neo-array-multidim-ilvt

> Status: **SHIPPED (sub-gaps 0, 1, 2)**; sub-gap 3 (multi-dim `ref a[i,j]` ldelema)
> PARKED for a follow-up. The prior PARK framing ("foundational multi-step") was
> DISPROVEN by the child-4 re-audit — sub-gaps 1+2 were tractable focused fixes.
> See `design.md` for the real root causes + the disproof.

## Sub-gap 0 — multi-dim array ctor token resolution (DONE, GREEN)

- [x] **0.1** Probe-first: construct `S[,] arr = new S[2,3]` (S = IL struct
      `NeoStep16Vt { int; string; }`); confirm the HEAD gap. Finding:
      `KeyNotFoundException: Cannot find method:.ctor in type:S[0...,0...]` at the
      ARRAY CTOR token resolution — a SHARED-ENGINE gap (Legacy + Neo identical), NOT
      a Neo element-copy gap as the mandate assumed.
- [x] **0.2** Fix `ILType.GetConstructor(List<IType>)` to delegate to the underlying
      CLR array type when `IsArray` (new `ResolveArrayClrType()` helper:
      `appdomain.GetType(arrayCLRType) as CLRType`). `ILRuntime/CLR/TypeSystem/ILType.cs`.
- [x] **0.3** Fix `ILType.GetMethod(name, param, genericArguments, ...)` to delegate
      likewise (the multi-dim `a[i,j]` callvirt Get/Set). Same file.
- [x] **0.4** Stash-toggle: HEAD throws `KeyNotFoundException`; +fix resolves the ctor
      (the probe then fails at the box/unbox gap; after 1+2 it PASSES). Load-bearing
      PROVEN.
- [x] **0.5** Add the keeper probes (see sub-gap 4).
- [x] **0.6** Verify: Neo `NeoStep` 278/0/0; `NeoStep16` 26/0/0; `NeoOptHardening`
      24/0/0; Legacy-neutral (full Legacy 812: HEAD 20 fail -> +fix 17 fail, 3 fewer).

## Sub-gap 1 — Box an IL-VT param for the reflection CLR Set (DONE — RE-AUDIT: TRACTABLE)

> Prior framing: "thread the per-param ref-region base into CLRMethod.Invoke — a NEW
> per-param ABI surface, F-7B/F-10 complexity." DISPROVEN: the ref-region base was
> already available in the call arm; the real bug was the dest slot sized by the FORMAL
> type (4 bytes) + the element ILType lost to the shared CLR array type. Fixed WITHOUT
> any ABI change.

- [x] **1.1** Re-audit the failure: `CLRMethod.Invoke:515` reads `mStack[idx]` where
      `idx` = the struct's first 4 bytes (num=42) -> OOB. Root cause: the dest slot was
      sized `ILTypeInstance`=4 bytes (formal), so `CopyNeoCallArguments` copied only 4
      bytes of the struct; the reader dereferences that as an mStack index.
- [x] **1.2** JIT-time element-type stamping: `JITCompiler.InitializeFunctionParam`
      populates a static `ConcurrentDictionary<int,ILType> s_neoIlVtArrayElementTypes`
      keyed by the Cecil method-token hash when the token's declaring type is an
      `ArrayType` whose IL type `IsArray` with an IL-VT element. Exposed via
      `GetNeoIlVtArrayElementType(int)`. `JITCompiler.cs`. (The resolved CLR method's
      declaring type is the SHARED `ILTypeInstance[,]`; the element ILType is otherwise
      unrecoverable at runtime.)
- [x] **1.3** Runtime intercept `TryNeoIlVtElementArrayCall` (`ILIntepreter.Neo.cs`):
      for a Set on an IL-VT-element array, box the value (from the CALLER frame — the
      LAST `PrimitiveSrc` + trailing `RefSrc` entries) via `Instantiate(false)` +
      `CopyFrameToIL`, then `Array.SetValue`. Called from the `Call` + `Callvirt_CLR`
      arms before the generic CLR dispatch.
- [x] **1.4** Verify `NeoStep16_MultiDimIlVtRoundTrip` GREEN (num + txt both correct).

## Sub-gap 2 — Unbox a boxed-IL-VT Get return (DONE — symmetric to 1)

- [x] **2.1** In `TryNeoIlVtElementArrayCall`, the Get arm: `Array.GetValue` the stored
      box; `CopyILToFrame` into the caller's dest frame region. Guard
      `elemIns.Type == elemIl` (NOT `!elemIns.Boxed` — the stored box is the IL-VT's
      representation whether or not it was marked Boxed on store). Null/uninitialized
      cell -> zero the dest (default struct).
- [x] **2.2** Verify `NeoStep16_MultiDimIlVtRefFieldNonNull` + `MultiDimIlVtMultiCell`
      GREEN.

## Sub-gap 3 — Multi-dim `ref a[i,j]` ldelema (PARKED — DISTINCT JIT gap)

- [x] **3.1** Probe `NeoStep16_MultiDimIlVtLdelemaMutate` (`BumpByRef(ref a[0,0])`).
      Finding: the `ref a[i,j]` lowering (a multi-dim `Address` method + a byref param
      call) hits an NRE during JIT — `ILType.get_IsValueType` dereferences a null
      `definition` (an ILType constructed for the Address/byref shape with no
      TypeDefinition). This is a JIT-level type-resolution failure, NOT the Set/Get
      reflection-reader mechanism (sub-gaps 1+2). Distinct gap.
- [x] **3.2** Do NOT keep the probe (commented out) so the NeoStep smoke stays green.
      Route to a follow-up child for the multi-dim ref-address JIT shape.

## Sub-gap 4 — Probes (DONE)

- [x] **4.1** `NeoStep16_MultiDimIlVtPrimitiveControl` (primitive-element `[,]` control).
- [x] **4.2** `NeoStep16_MultiDimIlVtRoundTrip` (single-cell Set/Get, num + txt).
- [x] **4.3** `NeoStep16_MultiDimIlVtMultiCell` (multi-cell isolation; each field in its
      OWN statement — a combined `||` of string-`!=` hits a PRE-EXISTING rank-1 string-
      comparison bug on HEAD, out of scope).
- [x] **4.4** `NeoStep16_MultiDimIlVtRefFieldNonNull` (ref field non-null + correct).

## Sub-gap 5 — Scope boundary

- [x] **5.1** Rank-2 IL-VT-element `[,]`: DONE (the core target). Rank-3+ uses the SAME
      mechanism (the intercept keys on `arr.Rank` for the index count) — include if it
      falls out. Not separately probed (rank-2 is representative; the mechanism is
      rank-agnostic via `arr.Rank`).
- [x] **5.2** Nested-VT-element (a VT containing a VT): NOT probed (the element ILType's
      `TotalPrimitiveSize`/`TotalReferenceCount` carry the full flattened layout, so the
      box/unbox via `CopyFrameToIL`/`CopyILToFrame` is layout-agnostic; scoped out, not
      separately validated).
- [x] **5.3** Non-zero-based lower-bound arrays: stay a documented Non-Goal of the
      shipped multi-dim.

## Sub-gap 6 — `ILRuntimeType.GetConstructor` guard (OPEN — latent, low priority)

- [ ] **6.1** `ILRuntimeType.GetConstructor` (`ILRuntimeType.cs:533-536`) casts the
      result to `(ILMethod)`. After sub-gap 0, an IL array type's ctor returns a
      `CLRMethod` -> that cast would throw if managed-reflection code ever probed an IL
      array type's ctor. NOT exercised by the suite (IL code uses the token resolver,
      not managed reflection); flagged as a latent regression vector for a follow-up.

## Findings to carry forward

- The "foundational multi-step" PARK framing for sub-gaps 1+2 was WRONG. Child-4 lesson
  applied: re-audit found a specific dest-slot-sizing bug + an element-type-recovery
  stamping — both tractable, NO ABI rework. (See `design.md` "Why the prior framing was
  wrong.")
- A PRE-EXISTING rank-1 string-comparison bug (`||`-chained multi-string-`!=` evaluates
  wrong on HEAD for IL-VT arrays) was found and reported; out of child-17 scope.
- Sub-gap 3 (multi-dim `ref a[i,j]`) is a genuine distinct JIT gap (null TypeDefinition
  on the Address/byref shape) — the next follow-up.
