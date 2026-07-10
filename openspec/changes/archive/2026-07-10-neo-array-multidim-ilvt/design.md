# Design — neo-array-multidim-ilvt

> Capability: `neo-arrays`. Branch `features/object-model-overhaul`.
> Status: **SHIPPED (sub-gaps 0, 1, 2)** — IL-VT-element `[,]` Get/Set works. Sub-gap 3
> (multi-dim `ref a[i,j]` ldelema) PARKED (a distinct JIT-level type-resolution gap).
> This design supersedes the prior PARK framing: the "foundational multi-step" gap was
> DISPROVEN (child-4 re-audit lesson applied) — sub-gaps 1+2 were tractable, each a
> focused intercept + a JIT-time type stamping (NOT a per-param ABI rework).

## Context

The shipped multi-dim change (`2026-07-06-neo-array-multidim`) closed PRIMITIVE +
REFERENCE-element rank-2+ arrays: the autogen binder
(`System_<Type>_Array2_Binding`) registers Neo redirects for a CLR array type's ctor +
`Set` (and the reflection fallback handles `Get`); the IL-VT-element case was a
documented Non-Goal -> this child.

## The probe-first finding (the gap is shared-engine, at the ctor)

The mandate assumed the gap was a Neo element-copy gap on top of a working multi-dim
ctor. The HEAD reproducer (`S[,] arr = new S[2,3]`, S = IL struct `NeoStep16Vt { int;
string; }`) DISPROVED this: it fails at the ARRAY CTOR token resolution, BEFORE any
Set/Get/ldelema:

```
KeyNotFoundException: Cannot find method:.ctor in type:...NeoStep16Vt[0...,0...],
token=...NeoStep16Vt[0...,0...]::.ctor(Int32,Int32)
```

Reproduced IDENTICALLY on Legacy (plain `Debug`) and Neo — a SHARED-ENGINE gap. Root
cause: an IL array type (`ILType` with `IsArray==true`, built by
`ILType.MakeArrayType(rank)`) wraps a CLR `ILTypeInstance[,...]` but declares NO
ctor/Get/Set of its own; `ILType.GetConstructor`/`GetMethod` iterated the (empty) array
TypeReference -> null. A primitive-element `int[,]` works only because `int` is a CLR
primitive -> the array type is a `CLRType` (CLR reflection + autogen binder).

## Sub-gap 0 — multi-dim array ctor token resolution (DONE, GREEN)

**`ILRuntime/CLR/TypeSystem/ILType.cs`:** when an `ILType` is an array (`IsArray`),
`GetConstructor(List<IType>)` and `GetMethod(name, param, genericArguments, ...)` delegate
to the underlying CLR array type via a new `ResolveArrayClrType()` helper
(`appdomain.GetType(arrayCLRType) as CLRType`).

### Why this is Legacy-neutral-by-improvement (additive)

Pre-change, an IL array type's `GetConstructor`/`GetMethod` ALWAYS returned null (the
array TypeReference declares no methods). So any method the delegation now resolves is
NET-NEW — no existing code could rely on the null return for a well-formed IL array
token. The change is NOT under `#if ENABLE_NEO_MODE` (shared-engine); it fixes both
engines identically. VERIFIED: full Legacy suite goes from 20 failures (HEAD) to 17
(with this change) — 3 FEWER, no regression.

## Sub-gaps 1+2 — the IL-VT element box/unbox (DONE, GREEN) — RE-AUDIT: TRACTABLE

The prior PARK framing called these "foundational, F-7B/F-10 complexity, needs a per-
param ref-region ABI rework." The child-4 re-audit DISPROVED this: neither sub-gap needs
new ABI plumbing. Each is a focused runtime intercept + a JIT-time element-type stamping.

### The REAL root cause (a single, specific bug — not foundational)

After the ctor resolves, the IL-VT-element `Set`/`Get` resolves to the CLR method
`ILTypeInstance[,]::Set(int, int, ILTypeInstance)` / `::Get(int, int)`. The element
type is `ILTypeInstance` (a CLASS — the IL struct's box). The reflection param reader
(`CLRMethod.Invoke` ~:508-526) reads a non-primitive non-enum param as an mStack index
(`mStack[idx]`), but the call-lowering sized the element param's dest slot by the
FORMAL type `ILTypeInstance` (4 bytes + 1 ref — `AllocateNeoCallParamSlot`'s final
`else` branch), and `CopyNeoCallArguments` copied only the FIRST 4 bytes of the struct
(the `num` field) into that 4-byte dest. So the reader dereferences `mStack[num=42]`
-> `ArgumentOutOfRangeException`.

This is NOT a "the per-param ref-region base is unavailable" problem (the prior framing).
The ref-region base IS available in the call arm (`frameRefBase` + the `NeoCallParamMap`'s
`RefSrc`/`RefDst`). The actual issue: the dest slot is the WRONG SIZE (4 bytes for a
struct) AND the reader can't box from flat bytes it doesn't fully have. The clean fix
does NOT touch the reader or the ABI — it intercepts the call BEFORE the mis-sized copy
matters and boxes/unboxes from the CALLER frame (where the full struct + its ref region
live), exactly mirroring the rank-1 `Stelem_Ref`/`Ldelem_Ref` CopyFrameToIL/CopyILToFrame
path.

### The fix (3 focused edits, NO ABI change)

1. **JIT-time element-type stamping** (`JITCompiler.cs`): a static
   `ConcurrentDictionary<int methodTokenHash, ILType elementIlType>` populated in
   `InitializeFunctionParam` (which has the Cecil `MethodReference` token). When the
   token's declaring type is an `ArrayType` whose IL declaring type `IsArray` with an
   IL-VT element, record the element ILType keyed by the token hash (the same key stamped
   on `op.Operand2`). This is the ONLY way to recover the element ILType at runtime:
   the resolved CLR method's declaring type is the SHARED `ILTypeInstance[,]` (all
   IL-VTs map to it via `TypeForCLR.MakeArrayType`), so the element type is LOST post-
   resolution. Keying by the Cecil token hash is unambiguous (distinct IL-VT element
   types have distinct declaring-type tokens). Exposed via
   `JITCompiler.GetNeoIlVtArrayElementType(methodTokenHash)`.

2. **Runtime intercept** (`ILIntepreter.Neo.cs`, `TryNeoIlVtElementArrayCall`): a helper
   called from the `Call` + `Callvirt_CLR` arms (after `CopyNeoCallArguments`, before
   the generic CLR dispatch). When `GetNeoIlVtArrayElementType(ip->Operand2)` returns an
   ILType and the method is `Set`/`Get`:
   - **Set**: read the array `this` + int indices from `targetBase`; recover the value
     param's caller-frame prim/ref source from the `NeoCallParamMap` (the LAST
     `PrimitiveSrc` entry + the trailing `elemRefCount` `RefSrc` entries); box into a
     fresh `ILTypeInstance` via `Instantiate(false)` + `CopyFrameToIL`; `Array.SetValue`.
   - **Get**: `Array.GetValue` the stored box; `CopyILToFrame` it into the caller's dest
     frame region (`frameBase + ip->DstOffset`, `frameRefBase + ip->Operand3`). A null
     (uninitialized) cell -> zero the dest (default struct).
   The box is marked `Boxed=true` on store; Get unboxes regardless of the Boxed flag
   (the stored box IS the IL-VT's representation; `elemIns.Type == elemIl` guards it).

3. **Ctor**: unchanged — the reflection ctor path works once sub-gap 0 resolves the
   token (it creates an empty `ILTypeInstance[,]`; the cells are lazily filled by Set).

### Why the prior framing was wrong (the child-4 lesson)

The prior `blocked.md` claimed the per-param ref-region base was "unavailable" to the
reader and that "threading it in is a NEW per-param ABI surface, F-7B/F-10 complexity."
Re-audit: the ref-region base was NEVER the problem (it's in the call arm already). The
real issues were (a) the dest slot sized by the FORMAL type (4 bytes), and (b) the
element ILType lost to the shared CLR array type. Both are fixed WITHOUT any ABI change
— (a) by intercepting before the mis-sized dest matters and reading the full struct from
the caller frame, (b) by a JIT-time token-keyed type map. This is the same shape as
child-4's disproof: a "foundational/plumbing" framing that was actually a specific bug
+ a registration/stamping miss.

## Sub-gap 3 — multi-dim `ref a[i,j]` ldelema (PARKED — a DISTINCT gap)

The `ref a[i,j]` lowering (a multi-dim `Address` method + a byref param call) hits an
NRE during JIT: `ILType.get_IsValueType` dereferences a null `definition` (an ILType
constructed for the Address/byref shape with no TypeDefinition). This is NOT the same
mechanism as sub-gaps 1+2 (it's a JIT-level type-resolution failure for the multi-dim
ref-address shape, not the Set/Get reflection reader). The probe
(`NeoStep16_MultiDimIlVtLdelemaMutate`) was probed, found to fail at this distinct JIT
point, and is NOT kept (commented out) so the NeoStep smoke stays green. Route to a
follow-up.

## Probes (kept)

- `NeoStep16_MultiDimIlVtPrimitiveControl` — primitive-element `int[,]` control (guards
  the shipped multi-dim primitive path; distinct cells/values from `MultiDimRank2Probe`).
- `NeoStep16_MultiDimIlVtRoundTrip` — IL-VT `[,]` Set/Get round-trip (num + txt both
  survive a single-cell get).
- `NeoStep16_MultiDimIlVtMultiCell` — IL-VT `[,]` multi-cell isolation (each field
  compared in its OWN statement; a combined `||` of multiple string-`!=` hits a
  PRE-EXISTING rank-1 string-comparison bug on HEAD — see below — separate statements
  avoid it and still prove per-cell isolation).
- `NeoStep16_MultiDimIlVtRefFieldNonNull` — IL-VT `[,]` ref field non-null + correct.

### PRE-EXISTING bug found (out of scope, NOT kept)

A rank-1 IL-VT multi-cell probe (`NeoStep16_TC5b`, a temporary diagnostic) exposed a
PRE-EXISTING string-comparison bug: a combined `if (r0.txt != "a" || r1.txt != "b")`
evaluates TRUE (fails) on pure HEAD for BOTH rank-1 AND multi-dim IL-VT arrays, even
though each individual `r0.txt != "a"` is correct. The primitive fields and individual
ref-field reads are correct; only the `||`-chained multi-string-`!=` is wrong. This is
a string-`op_Inequality`-in-`||` issue independent of arrays (fails on HEAD with no
child-17 changes). Reported here; NOT fixed (out of child-17 scope).

## Verification

- Neo `NeoStep` **278/0/0** (274 baseline + 4 IL-VT multidim probes).
- Neo `NeoStep16` **26/0/0**; `NeoOptHardening` **24/0/0**.
- **Legacy-neutral-by-improvement:** full Legacy suite 812 tests: HEAD 20 failed ->
  with this change 17 failed (3 FEWER, no regression). The 15 NeoStep-filter failures
  on Legacy are pre-existing Neo-on-Legacy gaps (unrelated).
- **Stash-toggle (load-bearing):** `NeoStep16_MultiDimIlVtRoundTrip` FAILs on HEAD with
  `KeyNotFoundException: Cannot find method:.ctor in type:...NeoStep16Vt[0...,0...]`;
  +fix PASSES (4/4 IL-VT probes green).
