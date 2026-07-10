# Design — neo-array-multidim-ilvt

> Capability: `neo-arrays`. Branch `features/object-model-overhaul`.
> Status: **SHIPPED (sub-gaps 0, 1, 2, 3)** — IL-VT-element `[,]` Get/Set works AND
> `ref a[i,j]` ldelema mutate works. Sub-gap 3 (the parked "distinct JIT-layer NRE")
> was RESOLVED on re-audit: two focused guards (a ByRef null-guard + a stale-Call-dest
> type clear), NOT a deep rework.
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

## Sub-gap 3 — multi-dim `ref a[i,j]` ldelema (RESOLVED — re-audited: NOT a deep gap)

The "distinct JIT-layer NRE" framing was, per the child-4/8/17 re-audit lesson, a specific
tractable bug — in fact TWO focused bugs, neither a deep rework:

### (a) The NRE — a null-guard miss on a ByRef ILType (`ILType.cs`, shared-engine)

The `ref T` param's register type is a ByRef ILType: `MakeByRefType` constructs an ILType
wrapping a `ByReferenceType`, and `RetriveDefinitino` deliberately leaves `definition` null
for a byref/array shape. `ILType.get_IsValueType` dereferenced that null `definition`. Fix:
an `IsByRef` short-circuit in `IsValueType` (a byref is itself never a value type — return
false before the deref). Legacy-safe (semantically correct for both engines; plain `Debug`
builds 0 errors; the NeoStep16-on-Legacy failures are pre-existing Neo-on-Legacy gaps).

### (b) Stale in-frame-VT type on a Call dest — `JITCompiler.Translate` type-inference pass (Neo)

After the NRE was fixed, the probe's mutation was NOT observable: `BumpByRef(ref a[0,0])` is
inlined, and the inlined `stfld` (the `v.num = 99` body) was mis-lowered to `Stfld_I4_Inline`
(writing into the frame), NOT the non-inline `Stfld_I4` (which writes through the byref's
mStack index to the array cell's box).

Root cause: the `Address` call's dest register was REUSED from an earlier `ldloca` of the VT
local `s` (the type-inference pass seeds an `ldloca` of a VT local with that VT type —
correct for the in-frame `s.num=5` writes). The type-inference switch had NO `Call` case, so
the dest RETAINED the stale VT type after the `Address` call. `TryRewriteFieldAccessForInline`
then saw the inlined `stfld`'s operand as an in-frame VT and rewrote it to `_Inline`.

Fix: a `Call`/`Callvirt`/`Callvirt_IL`/`Callvirt_CLR`/`Call_Redirect` case in the type-
inference switch that clears a stale in-frame-VT type on the dest (`Register1`) when the
call's return is NOT a by-value IL-VT. A Call result is NEVER an in-frame VT (it is a
reference / primitive / byref); a by-value IL-VT return (the `Get` path, or any VT-returning
IL method) IS materialized into the dest via CopyILToFrame and is KEPT. The IL-VT-element
array `Get`'s element type is recovered via `GetNeoIlVtArrayElementType` (its resolved
ReturnType is the shared CLR `ILTypeInstance`).

### Runtime `Address` intercept — `ILIntepreter.Neo.cs` `TryNeoIlVtElementArrayCall` (Neo)

An `Address` branch (symmetric to the Set/Get branches) materializes the element's box
(lazy-inits a null cell), pushes it onto mStack, and writes an 8-byte Ref Slot
`(mStackIdx_of_the_box, fieldOffset=0)` to the caller's return dest. With fix (b) the
inlined `stfld` stays non-inline, resolves `mStack[mStackIdx]` -> the SAME box the array cell
references, and mutates `box.Primitives` — observable on a subsequent `a[i,j]` Get.

The probe `NeoStep16_MultiDimIlVtLdelemaMutate` is now GREEN and part of the NeoStep smoke
(278 -> 279).

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
- `NeoStep16_MultiDimIlVtLdelemaMutate` — IL-VT `[,]` `ref a[i,j]` in-place mutate
  (sub-gap 3): `BumpByRef(ref a[0,0])` writes `v.num=99` through the byref to the cell's
  box; a subsequent `a[0,0]` Get observes `r.num==99`. Previously PARKED (the JIT NRE);
  now GREEN.

### PRE-EXISTING bug found (out of scope, NOT kept)

A rank-1 IL-VT multi-cell probe (`NeoStep16_TC5b`, a temporary diagnostic) exposed a
PRE-EXISTING string-comparison bug: a combined `if (r0.txt != "a" || r1.txt != "b")`
evaluates TRUE (fails) on pure HEAD for BOTH rank-1 AND multi-dim IL-VT arrays, even
though each individual `r0.txt != "a"` is correct. The primitive fields and individual
ref-field reads are correct; only the `||`-chained multi-string-`!=` is wrong. This is
a string-`op_Inequality`-in-`||` issue independent of arrays (fails on HEAD with no
child-17 changes). Reported here; NOT fixed (out of child-17 scope).

## Verification

- Neo `NeoStep` **279/0/0** (274 baseline + 4 IL-VT multidim Get/Set probes + the
  ldelema-mutate probe, sub-gap 3).
- Neo `NeoStep16` **27/0/0**; `NeoOptHardening` **24/0/0**.
- **Legacy-neutral-by-improvement:** full Legacy suite 812 tests: HEAD 20 failed ->
  with this change 17 failed (3 FEWER, no regression). The NeoStep16-on-Legacy failures
  (TC8 StelemI + the ldelema probe) are pre-existing Neo-on-Legacy gaps (identical on
  HEAD; the sub-gap-3 `IsByRef` guard is Legacy-safe — plain `Debug` builds 0 errors).
- **Stash-toggle (load-bearing):**
  - `NeoStep16_MultiDimIlVtRoundTrip` FAILs on HEAD with `KeyNotFoundException: Cannot
    find method:.ctor in type:...NeoStep16Vt[0...,0...]`; +fix PASSES.
  - `NeoStep16_MultiDimIlVtLdelemaMutate` (sub-gap 3) NREs on HEAD at
    `ILType.get_IsValueType()` (the null-`definition` deref); +fix PASSES (in-place
    mutate via `ref a[0,0]` observable — `r.num == 99`).
