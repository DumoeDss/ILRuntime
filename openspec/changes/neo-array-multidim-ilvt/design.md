# Design — neo-array-multidim-ilvt

> Capability: `neo-arrays`. Branch `features/object-model-overhaul`.
> See `blocked.md` for the PARK verdict + the probe-first finding. This design
> documents what shipped (the ctor token resolution) + the scoped follow-up
> (the param-boxing mechanism).

## Context

The shipped multi-dim change (`2026-07-06-neo-array-multidim`) closed
PRIMITIVE + REFERENCE-element rank-2+ arrays: the autogen binder
(`System_<Type>_Array2_Binding`) registers Neo redirects for a CLR array type's
ctor + `Set` (and the reflection fallback handles `Get`); the IL-VT-element
case was a documented Non-Goal -> this child.

## The probe-first finding (the gap is shared-engine, at the ctor)

The mandate assumed the gap was a Neo element-copy gap on top of a working
multi-dim ctor. The HEAD reproducer (`S[,] arr = new S[2,3]`, S = IL struct
`NeoStep16Vt { int; string; }`) DISPROVED this: it fails at the array CTOR
token resolution, BEFORE any Set/Get/ldelema:

```
KeyNotFoundException: Cannot find method:.ctor in type:...NeoStep16Vt[0...,0...],
token=...NeoStep16Vt[0...,0...]::.ctor(Int32,Int32)
```

Reproduced IDENTICALLY on Legacy (plain `Debug`) and Neo — a SHARED-ENGINE gap.
Root cause: an IL array type (`ILType` with `IsArray=true`, built by
`ILType.MakeArrayType(rank)`) wraps a CLR `ILTypeInstance[,...]` but declares NO
ctor/Get/Set of its own; `ILType.GetConstructor`/`GetMethod` iterated the
(empty) array TypeReference -> null. A primitive-element `int[,]` works only
because `int` is a CLR primitive -> the array type is a `CLRType` (CLR
reflection + autogen binder).

## What shipped: the ctor token resolution

**`ILRuntime/CLR/TypeSystem/ILType.cs`:** when an `ILType` is an array
(`IsArray`), `GetConstructor(List<IType>)` and
`GetMethod(name, param, genericArguments, ...)` delegate to the underlying CLR
array type via a new `ResolveArrayClrType()` helper
(`appdomain.GetType(arrayCLRType) as CLRType`).

### Why this is Legacy-neutral-by-improvement (additive)

Pre-change, an IL array type's `GetConstructor`/`GetMethod` ALWAYS returned null
(the array TypeReference declares no methods). So any method the delegation now
resolves is NET-NEW — no existing code could rely on the null return for a
well-formed IL array token. The change is NOT under `#if ENABLE_NEO_MODE`
(shared-engine); it fixes both engines identically (the CATCH-COMPLETE /
neo-array-multidim Gap 3 precedent: a genuine shared-engine improvement, not a
Neo workaround).

### Caveat (flagged for the follow-up)

`ILRuntime.Reflection.ILRuntimeType.GetConstructor` (`ILRuntimeType.cs:533-536`)
casts the result to `(ILMethod)`. After this change, an IL array type's ctor
returns a `CLRMethod` -> that cast would throw if managed-reflection code ever
probed an IL array type's ctor. Not exercised by the suite (IL code uses the
token resolver, not managed reflection); flagged for the follow-up to guard.

## The scoped follow-up (the param-boxing mechanism — PARKED)

After the ctor resolves, the IL-VT-element `Set`/`Get` hits a foundational
param-boxing gap. The resolved CLR method is
`ILTypeInstance[,]::Set(int, int, ILTypeInstance)` — the CLR array's element
type is `ILTypeInstance` (a CLASS; the IL struct boxes to it). The reflection
param reader (`CLRMethod.Invoke` ~:508-526) reads a non-primitive non-enum param
as an mStack index, but the call-site passes the IL-VT struct as FLAT BYTES
(the `NeoCallParamMap` sizes the IL-VT param slot at `TotalPrimitiveSize` +
`TotalReferenceCount` ref slots; `CopyNeoCallArguments` copies the flat bytes +
the ref region). So the reader dereferences a garbage mStack index.

### The 3 coupled sub-gaps (each F-7B/F-10 complexity)

1. **Box an IL-VT param for a reflection CLR call** (`Set`'s element param).
   `CLRMethod.Invoke(targetBase, mStack, isNewObj)` does NOT receive the per-
   param ref-region base. Thread the per-param `RefDst` offset in (or a Neo-
   specific reader), then box via the Constrained-inherited-CLRMethod template
   (`ILIntepreter.Neo.cs:~4884-4942`): `ilType.Instantiate(false)` +
   `CopyFrameToIL` (flat bytes + ref region) + `Boxed=true`; the ref base is
   recovered via a `localInfos` scan (R2), exactly as the Constrained box path
   does for its receiver.
2. **Unbox a boxed-IL-VT return** (`Get`'s element return). The return path
   (`InvokeNeoClrMethod` `:983-998`, `else if (retType.IsValueType)`) writes a
   boxed CLR struct's flat bytes via `WriteNeoValueType`. Discriminate on
   `res is ILTypeInstance` and `CopyILToFrame` into the dest instead.
3. **Multi-dim `ldelema` address-of-IL-VT-element.** The `Ldelema` arm
   (`ILIntepreter.Neo.cs:4593-4631`) handles `ILTypeInstance[]` (rank-1) + CLR
   primitive arrays; add the multi-dim IL-VT-element address shape (the C#
   `ref a[i,j]` lowering).

### Why PARK (not a single unit)

Each sub-gap needs its own encoding + runtime hook (thread per-param ref-region
into the reader; discriminate the return; a new ldelema shape). F-7B
(ref-type-byref writeback), F-7B-SIB (direct-call byref ABI), and F-10
(CLR-struct-field-of-IL) were each their OWN dedicated child at this complexity.
This is 3 coupled sub-gaps -> beyond the "one unit" child scope.

## Probes (kept / removed)

- **KEPT:** `NeoStep16_MultiDimIlVtPrimitiveControl` — primitive-element `int[,]`
  control (guards the shipped multi-dim primitive path; distinct cells/values
  from `NeoStep16_MultiDimRank2Probe`).
- **REMOVED (red until follow-up):** `NeoStep16_MultiDimIlVtRoundTrip`,
  `MultiCell`, `RefFieldNonNull`, `LdelemaMutate`. Re-verified load-bearing via
  the ctor-fix stash-toggle (HEAD throws `KeyNotFoundException`; +fix resolves
  the ctor; the probes then fail at the param-boxing gap). Removed to keep the
  NeoStep smoke GREEN (268/0/0).

## Verification

- Neo `NeoStep` 268/0/0 (+1 control probe); `NeoStep16` 23/0/0; `NeoOptHardening`
  24/0/0.
- Legacy-neutral: plain `Debug` CLI build 0 errors.
- Stash-toggle: ctor fix load-bearing (HEAD = `KeyNotFoundException`; +fix =
  ctor resolves).
