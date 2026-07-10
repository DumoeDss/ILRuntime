# Blocked — neo-array-multidim-ilvt

> Status: **SHIPPED (sub-gaps 0, 1, 2)** — IL-VT-element `[,]` Get/Set works end-to-end
> (Neo `NeoStep` 278/0/0; +4 IL-VT multidim probes). ONE sub-gap remains PARKED:
> sub-gap 3 (multi-dim `ref a[i,j]` ldelema) — a DISTINCT JIT-level type-resolution
> gap, NOT the Set/Get mechanism.
>
> **The prior PARK verdict ("foundational multi-step") was DISPROVEN** by the child-4
> re-audit. Sub-gaps 1+2 (the "box/unbox the IL-VT element") were tractable focused
> fixes, NOT a per-param ABI rework. This file documents the real root causes + the
> disproof + the one remaining gap.

## What shipped (sub-gaps 0, 1, 2 — GREEN)

1. **Sub-gap 0 — ctor token resolution** (`ILType.cs`): `GetConstructor`/`GetMethod`
   delegate to the underlying CLR array type when `IsArray` (new `ResolveArrayClrType()`
   helper). Fixes BOTH engines (Legacy-neutral-by-improvement: full Legacy 812 -> HEAD
   20 fail, +fix 17 fail).

2. **Sub-gaps 1+2 — IL-VT element box/unbox** (`JITCompiler.cs` + `ILIntepreter.Neo.cs`):
   - JIT-time element-type stamping (`s_neoIlVtArrayElementTypes`, keyed by Cecil
     method-token hash) — recovers the element ILType that the shared CLR
     `ILTypeInstance[,]` type loses.
   - Runtime intercept `TryNeoIlVtElementArrayCall` (called from the `Call` +
     `Callvirt_CLR` arms): Set boxes the element from the caller frame (Instantiate +
     CopyFrameToIL + Array.SetValue); Get unboxes via CopyILToFrame. NO ABI change.

See `design.md` for the full mechanism + the disproof of the prior framing.

## Why the prior "foundational multi-step" PARK was WRONG (child-4 lesson)

The prior `blocked.md` claimed:
- "the per-param ref-region base is unavailable to `CLRMethod.Invoke`" -> WRONG. The
  ref-region base was always in the call arm (`frameRefBase` + the NeoCallParamMap's
  `RefSrc`/`RefDst`). It was never the constraint.
- "threading it in is a NEW per-param ABI surface, F-7B/F-10 complexity" -> WRONG. No
  ABI change was needed. The real bug was (a) the dest slot sized by the FORMAL type
  (4 bytes for `ILTypeInstance`, truncating the struct), and (b) the element ILType
  lost to the shared CLR array type. (a) is sidestepped by intercepting before the
  mis-sized dest matters + reading the full struct from the caller frame; (b) is fixed
  by a JIT-time token-keyed type map. This mirrors child-4's disproof exactly (a
  "foundational/plumbing" framing that was a specific bug + a registration/stamping
  miss).

## The REMAINING gap — sub-gap 3 (multi-dim `ref a[i,j]` ldelema)

The `ref a[i,j]` lowering (a multi-dim `Address` method + a byref param call) hits an
NRE during JIT: `ILType.get_IsValueType` (`ILType.cs:1838`) dereferences a null
`definition` — an ILType was constructed for the Address/byref shape with no
TypeDefinition. This is a DISTINCT JIT-level type-resolution failure for the multi-dim
ref-address shape, NOT the Set/Get reflection-reader mechanism (sub-gaps 1+2 are GREEN).

The probe `NeoStep16_MultiDimIlVtLdelemaMutate` was probed, found to fail at this JIT
point, and is NOT kept (commented out) so the NeoStep smoke stays green.

### Why PARK sub-gap 3 (not bundle it)

- It's a different failure layer (JIT type resolution for the Address shape, not the
  runtime Set/Get reader).
- The core mandate ("IL-VT-element `[,]` Get/Set") is satisfied by sub-gaps 0+1+2.
- The fix requires understanding the multi-dim `Address` method's type construction (a
  null TypeDefinition), which is its own investigation — beyond the "IL-VT Get/Set" unit.

### Follow-up for sub-gap 3

A dedicated child to fix the multi-dim `Address`/`ref a[i,j]` JIT shape (make the
constructed ILType carry a TypeDefinition, or guard `IsValueType` against a null
definition for the Address shape). Then re-add the `LdelemaMutate` probe.

## Verification (GREEN)

- Neo `NeoStep` **278/0/0** (274 baseline + 4 IL-VT multidim probes).
- Neo `NeoStep16` **26/0/0**; `NeoOptHardening` **24/0/0**.
- **Legacy-neutral-by-improvement:** full Legacy 812 tests — HEAD 20 fail -> +fix 17
  fail (3 FEWER, no regression). NeoStep-filter on Legacy: 15 pre-existing Neo-on-Legacy
  failures (HEAD 18 -> +fix 15; unrelated to this change).
- **Stash-toggle (load-bearing):** `NeoStep16_MultiDimIlVtRoundTrip` FAILs on HEAD
  (`KeyNotFoundException: Cannot find method:.ctor in type:...NeoStep16Vt[0...,0...]`);
  +fix PASSES.

## Files

- `ILRuntime/CLR/TypeSystem/ILType.cs` — `ResolveArrayClrType()` + ctor/GetMethod
  delegation (sub-gap 0; shared-engine, NOT `#if ENABLE_NEO_MODE`).
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` —
  `s_neoIlVtArrayElementTypes` + `GetNeoIlVtArrayElementType` + the stamping in
  `InitializeFunctionParam` (sub-gap 1; `#if ENABLE_NEO_MODE`).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` —
  `TryNeoIlVtElementArrayCall` + the intercept in the `Call` + `Callvirt_CLR` arms
  (sub-gaps 1+2; `#if ENABLE_NEO_MODE`).
- `TestCases/NeoStep16Test.cs` — 4 keeper probes (PrimitiveControl, RoundTrip,
  MultiCell, RefFieldNonNull) + the commented-out LdelemaMutate (sub-gap 3 PARKED).

## Durable findings (carry forward)

1. An IL array type (`ILType` `IsArray=true`) declares NO ctor/Get/Set — they live on
   the underlying CLR array type (`arrayCLRType`, built by `MakeArrayType(rank)` as
   `ILTypeInstance[,...]`). Any array-method resolution on an IL array type MUST
   delegate to `ResolveArrayClrType()` (sub-gap 0 installed ctor + Get/Set).
2. ALL IL-VT element types share the SAME CLR array type `ILTypeInstance[,]` (via
   `TypeForCLR.MakeArrayType`), so the resolved CLR method's declaring type LOSES the
   element ILType. Recover it via the JIT-time token-keyed `s_neoIlVtArrayElementTypes`
   map (sub-gap 1's stamping) — the token's Cecil declaring type IS element-specific.
3. The multi-dim IL-VT Set/Get mirrors rank-1's CopyFrameToIL/CopyILToFrame box/unbox,
   done from the CALLER frame (where the full struct + ref region live), NOT from the
   callee param region (which is sized by the formal `ILTypeInstance` type — 4 bytes,
   truncating the struct). The intercept belongs in the call arm, NOT the reflection
   reader.
4. A PRE-EXISTING rank-1 string-comparison bug: `||`-chained multi-string-`!=` on IL-VT
   struct fields evaluates wrong on HEAD (rank-1 AND multi-dim). Reported; out of scope.
5. Sub-gap 3 (multi-dim `ref a[i,j]`) is a JIT type-resolution gap (null TypeDefinition
   on the Address shape) — the next follow-up.

## Build/test

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental
dotnet build TestCases/TestCases.csproj -c Debug
# Neo:
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep   # 278/0/0
# Legacy-neutral:
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug
dotnet run -c Debug -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep
```
ALWAYS `-f net8.0`; CLI = `Debug_Neo` (Neo) / plain `Debug` (Legacy); TestCases = `Debug`.
