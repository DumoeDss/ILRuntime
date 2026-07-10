# Blocked — neo-array-multidim-ilvt

> Status: **PARKED** (foundational multi-step gap; the element-copy / param-boxing
> mechanism is not a single unit of work). One real sub-gap was CLOSED and kept
> (the multi-dim array ctor token resolution); the remaining gap is scoped and
> handed off. Tree is GREEN (NeoStep 268/0/0; +1 primitive control probe; the
> red IL-VT probes were NOT kept — they need the follow-up).

## Mandate recap (from lead-7 handoff, child 17)

IL value-type-element multi-dimensional arrays (`Foo[,]` where `Foo` is an IL
struct) + multi-dim `ldelema` (the address of a multi-dim element). lead-6 /
MEDIUM#9: "real remaining gaps from neo-array-multidim". The shipped multi-dim
(`2026-07-06-neo-array-multidim`) handles PRIMITIVE + REFERENCE elements; the
IL-VT-element case was a documented Non-Goal.

## The probe-first finding (IMPORTANT — the gap is NOT where the mandate assumed)

The mandate assumed the gap was "the element copy (byte + ref-slot) for an
IL-VT-element `[,]` Get/Set + the multi-dim ldelema" — i.e. a Neo-element-copy
gap layered on top of a working multi-dim ctor.

**The HEAD reproducer DISPROVED this.** `S[,] arr = new S[2,3]` (S = IL struct
`NeoStep16Vt { int num; string txt; }`) fails at the ARRAY CTOR token
resolution, BEFORE any Set/Get/ldelema is reached:

```
System.Collections.Generic.KeyNotFoundException: Cannot find method:.ctor in
type:TestCases.NeoStep16Vt[0...,0...], token=System.Void
TestCases.NeoStep16Vt[0...,0...]::.ctor(System.Int32,System.Int32)
```

**This is a SHARED-ENGINE gap, NOT Neo-specific** — reproduced IDENTICALLY on
Legacy (plain `Debug`) and Neo (`Debug_Neo`). Root cause: `ILType.GetConstructor`
/ `GetMethod` iterate the array TypeReference's declared methods (an array type
declares NONE), so the multi-dim `newobj S[0...,0...]::.ctor(int,int)` resolves
to null -> KeyNotFoundException. A primitive-element `int[,]` works ONLY because
`int` is a CLR primitive -> the array type is a `CLRType` (resolved via CLR
reflection + an autogen `System_Int32_Array2_Binding` redirect); an IL-VT element
-> the array type is an `ILType` with `IsArray=true` and no resolved ctor/Get/Set.

## What shipped (the CLOSED sub-gap — the ctor token resolution)

**`ILRuntime/CLR/TypeSystem/ILType.cs` (+48):** when an `ILType` is an array
(`IsArray == true`), `GetConstructor(List<IType>)` and
`GetMethod(name, param, genericArguments, ...)` delegate to the underlying CLR
array type's methods (via a new `ResolveArrayClrType()` helper that does
`appdomain.GetType(arrayCLRType) as CLRType`). The IL array wrapper
(`MakeArrayType(rank)` builds an `ILType` wrapping a CLR
`ILTypeInstance[,...]`) declares no ctor/Get/Set itself; the real methods live
on the underlying CLR array type. This is additive: pre-change, an IL array
type's `GetConstructor`/`GetMethod` ALWAYS returned null, so any now-resolved
method is net-new (no existing code could rely on the null return for a
well-formed array token). shared-engine (NOT under `#if ENABLE_NEO_MODE`) — it
fixes BOTH engines identically (Legacy-neutral-by-improvement, the
CATCH-COMPLETE / neo-array-multidim Gap 3 precedent).

**`TestCases/NeoStep16Test.cs` (+37):** 1 keeper probe
`NeoStep16_MultiDimIlVtPrimitiveControl` (primitive-element `int[,]` control —
guards the already-shipped multi-dim primitive path; distinct cells/values from
`NeoStep16_MultiDimRank2Probe`). The 4 IL-VT-element functional probes were
NOT kept (they are red until the follow-up).

### Stash-toggle (proves the ctor fix is load-bearing)

With `ILType.cs` stashed (HEAD) + the temp probe `NeoStep16_MultiDimIlVtRoundTripTmp`
re-added + rebuilt: the probe FAILs with the EXACT `KeyNotFoundException:
Cannot find method:.ctor in type:...NeoStep16Vt[0...,0...]` root-cause signature.
After `git stash pop` + rebuild: the ctor resolves (the probe now fails at the
NEXT downstream gap — see below — not at ctor resolution). Proves the ctor fix
is load-bearing and the gap is correctly narrowed.

## The REMAINING gap (why this is PARKED, not DONE)

After the ctor resolves, the IL-VT-element multi-dim `Set`/`Get` hits a
**foundational param-boxing gap** in the Neo reflection param reader
(`CLRMethod.Invoke`, `ILRuntime/CLR/Method/CLRMethod.cs`). The new failure
signatures (4 red probes on HEAD+ctor-fix):

| Probe | Failure after ctor fix |
|-------|------------------------|
| `MultiDimIlVtRoundTrip` | `Index was out of range` (the `Set` call's IL-VT param read) |
| `MultiDimIlVtMultiCell` | `Object of type 'System.String' cannot be converted to type 'ILTypeInstance'` |
| `MultiDimIlVtRefFieldNonNull` | `Object of type 'ILTypeInstance[,]' cannot be converted to type 'ILTypeInstance'` |
| `MultiDimIlVtLdelemaMutate` | `NullReferenceException` |

**Root cause:** the resolved CLR method is `ILTypeInstance[,]::Set(int, int,
ILTypeInstance)` — the CLR array's element type is `ILTypeInstance` (a CLASS,
because the IL struct boxes to an ILTypeInstance). The param reader
(`CLRMethod.Invoke` ~:508-526) reads a non-primitive non-enum param as an
mStack index (`mStack[idx]`), but the call-site passes the IL-VT struct as FLAT
BYTES (the Neo `NeoCallParamMap` sizes the IL-VT param slot at
`TotalPrimitiveSize` + `TotalReferenceCount` ref slots; `CopyNeoCallArguments`
copies the flat bytes). So the reader dereferences a garbage mStack index -> the
observed failures. The element must be BOXED (flat bytes + ref region -> a real
`ILTypeInstance` via the `ilType.Instantiate(false)` + `CopyFrameToIL` Box-arm
machinery, `ILIntepreter.Neo.cs` ~:4936-4941) BEFORE the reflection `SetValue`
stores it, and UNBOXED (the boxed `ILTypeInstance` return -> flat bytes via
`CopyILToFrame`) on the `Get` return path.

**Why PARK (not a single unit):** the box/unbox must happen inside the reflection
param reader / return writer, but `CLRMethod.Invoke(targetBase, mStack, isNewObj)`
does NOT receive the per-param ref-region base (the struct's `TotalReferenceCount`
ref slots were copied by `CopyNeoCallArguments` into the call frame's ref region
at per-param `RefDst` offsets — `Optimizer.Neo.cs:1382-1386`, `NeoCallParamMap.RefSrc`
/ `RefDst`). Threading the per-param ref-region offset into `CLRMethod.Invoke` so
the reader can `CopyFrameToIL` the flat bytes + ref region into a fresh boxed
ILTypeInstance is a NEW per-param ABI surface, at the complexity of **F-7B /
F-10 / NEO-CLRSTRUCT-FIELD-OF-IL** (each of those was its own dedicated child
with its own encoding + runtime hook). The multi-dim `ldelema` address-of-IL-VT-
element is a THIRD sub-gap (the existing `Ldelema` arm handles `ILTypeInstance[]`
rank-1 + CLR primitive arrays; a multi-dim IL-VT-element address needs a new
shape). This is 3 coupled sub-gaps, each foundational — beyond the "one unit of
work" / child scope.

### The JIT body that proves the narrowed gap

`NeoStep16_MultiDimIlVtRoundTrip` final JIT (HEAD + ctor fix):
```
4:newobj r0, r7, r8, ILRuntime.Runtime.Intepreter.ILTypeInstance[,]::Void .ctor(Int32, Int32)   <- RESOLVES now (was KeyNotFound)
...
14:call -, r8, r9, r1, ILRuntime.Runtime.Intepreter.ILTypeInstance[,]::Void Set(Int32, Int32, ILTypeInstance)   <- FAILS here (param r1 is IL-VT flat bytes, Set wants a boxed ILTypeInstance)
17:call r2, r0, r8, r9, ILRuntime.Runtime.Intepreter.ILTypeInstance[,]::ILTypeInstance Get(Int32, Int32)
```
The array `a` local is correctly typed `NeoStep16Vt[0...,0...]`; the struct local
`s = { num = 42, txt = mdvt }` is correct; the ctor resolves. The gap is purely
the `Set`/`Get` element box/unbox in the reflection reader.

## Scope boundary (what is / is NOT in the follow-up)

IN scope for the follow-up child:
1. **Box an IL-VT param for a reflection CLR call** (`Set`'s element param): thread
   the per-param ref-region offset into `CLRMethod.Invoke` (or a Neo-specific
   reader), build a boxed `ILTypeInstance` from the flat bytes + ref region via
   `CopyFrameToIL`. MIRROR: the Constrained-inherited-CLRMethod box path
   (`ILIntepreter.Neo.cs:~4884-4942`) already boxes an IL-VT receiver this way —
   reuse its `localInfos`-scan ref-base recovery (R2) for the param case.
2. **Unbox a boxed-IL-VT return** (`Get`'s element return): the return path
   (`InvokeNeoClrMethod` `:983-998`, `else if (retType.IsValueType)`) writes a
   boxed CLR struct's flat bytes via `WriteNeoValueType`; a boxed ILTypeInstance
   must instead `CopyILToFrame` into the dest. Discriminate on
   `res is ILTypeInstance`.
3. **Multi-dim `ldelema` address-of-IL-VT-element**: the `Ldelema` arm
   (`ILIntepreter.Neo.cs:4593-4631`) handles `ILTypeInstance[]` (rank-1) + CLR
   primitive arrays; add the multi-dim IL-VT-element address shape (the C#
   `ref a[i,j]` lowering).

OUT of scope / deferred:
- Rank-3+ IL-VT-element arrays (same mechanism once 1-3 land; if it falls out,
  include it; else note).
- Nested-VT-element (element is a VT containing a VT) — recurse one level if
  cheap, else scope out.
- Non-zero-based lower-bound arrays (already a documented Non-Goal of the
  shipped multi-dim).

## Verification (current state, GREEN)

- **Neo full `NeoStep` smoke: 268/0/0** (267-268 baseline + 1 primitive control
  probe). The 4 red IL-VT probes were NOT kept (red until the follow-up).
- **Neo `NeoStep16` gate: 23/0/0** (22 baseline + 1 control).
- **Neo `NeoOptHardening`: 24/0/0** (no CLR-struct / FCP regression).
- **Legacy-neutral:** plain `Debug` CLI build = 0 errors. The ctor fix is
  shared-engine (NOT `#if ENABLE_NEO_MODE`); the IL-VT multidim gap is identical
  on Legacy (both engines fail the same way pre-fix; both benefit from the ctor
  resolution; the param-boxing gap affects Neo only — Legacy's `StackObject`
  param model is a different surface, untested here).
- **Stash-toggle:** ctor fix PROVEN load-bearing (HEAD throws
  `KeyNotFoundException`; +fix resolves the ctor).

## Durable findings (carry forward)

1. **An IL array type (`ILType` with `IsArray=true`) declares NO ctor/Get/Set** —
   `ILType.GetConstructor`/`GetMethod` iterated the (empty) array TypeReference.
   The methods live on the underlying CLR array type (`arrayCLRType`, built by
   `MakeArrayType(rank)` as `ILTypeInstance[,...]`). Any future array-method
   resolution on an IL array type MUST delegate to `ResolveArrayClrType()`
   (this change installs that delegation for ctor + Get/Set; the
   `ILRuntimeType.GetConstructor` reflection wrapper at `ILRuntimeType.cs:533-536`
   casts the result to `(ILMethod)` — now a potential `InvalidCastException` if
   managed-reflection code ever probes an IL array type's ctor; untested by the
   suite, flagged for the follow-up to guard).
2. **A primitive-element `[,]` is a CLRType; an IL-VT-element `[,]` is an ILType**
   — the autogen binder (`System_<Type>_Array2_Binding`, registered via
   `RegisterCLRMethodRedirectionNeo`) only exists for CLR primitive/known element
   types. An IL-VT-element `[,]` has NO autogen binder -> it falls to the
   reflection path, which is where the param-boxing gap lives.
3. **The reflection CLR-method param reader (`CLRMethod.Invoke`) does NOT receive
   the per-param ref-region base** — it reads flat bytes (`ReadNeoValueType` for
   CLR structs, `mStack[idx]` for references) from `targetBase + curPrim`. Boxing
   an IL-VT param (flat bytes + ref region -> ILTypeInstance) needs the ref-region
   offset threaded in — the same per-param ABI gap that F-7B (ref-type-byref
   writeback) and F-10 (CLR-struct-field-of-IL) each needed their own encoding +
   runtime hook to close. This is the load-bearing complexity of the follow-up.
4. **The Constrained-inherited-CLRMethod box path (`ILIntepreter.Neo.cs:~4884-
   4942`) is the reusable template** for boxing an IL-VT from flat bytes + ref
   region into a real `ILTypeInstance` (it recovers the ref base via a
   `localInfos` scan, R2). The follow-up's param-boxing should reuse this.

## Follow-up

Route to a dedicated follow-up child (`neo-array-multidim-ilvt-boxing` or a
split): the 3 coupled sub-gaps above (param-box + return-unbox + multi-dim
ldelema). Estimated F-7B/F-10 complexity (its own encoding + runtime hook). The
ctor token resolution (this change) is the prerequisite and is already GREEN on
the branch (uncommitted — LEAD commits).

## Build/test (CRITICAL)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental   # after touching ILType.cs
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep   # 268/0/0
```
ALWAYS `-f net8.0`; CLI = `Debug_Neo`, TestCases = `Debug` (NEVER TestCases with
`Debug_Neo`). The red IL-VT probes were re-verified load-bearing via the
stash-toggle then removed to keep the smoke green.
