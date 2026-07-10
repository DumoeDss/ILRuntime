# Ship Log — neo-array-multidim-ilvt (child 17) — UNBLOCKED (core scope)

**Date:** 2026-07-10  **Capability:** neo-arrays  **Wave:** completion-3, child 17
**Status:** SHIPPED (LEAD-verified) — core IL-VT-element `[,]` Get/Set works. The "foundational
multi-step" framing was DISPROVEN (the child-4 re-audit lesson paid off again).

## Delivered
**IL value-type-element multi-dimensional arrays** (a `Foo[,]` where `Foo` is an IL struct) Get/Set.
The prior "foundational, F-7B/F-10 complexity, needs a per-param ABI rework" framing was **WRONG on the
core claim**: the per-param ref-region base was NEVER unavailable (it's in the call arm already). The
real bugs were specific + tractable.

- **Sub-gap 0 — ctor token resolution** (`ILType.cs`, SHARED-engine): `GetConstructor`/`GetMethod`
  delegate to the underlying CLR array type via new `ResolveArrayClrType()` when `IsArray` (fixes ALL IL
  array types' method resolution — the array `KeyNotFoundException`). **Legacy-neutral-by-improvement**
  (the shared fix IMPROVED Legacy: 812 tests, 20 fail -> 17 fail; no regression).
- **Sub-gap 1 — box the IL-VT arg (the disproof)** (`JITCompiler.cs` + `ILIntepreter.Neo.cs`, Neo-gated):
  REAL root cause = a **dest-slot-sizing bug** — `Set(int,int,ILTypeInstance)`'s element param dest was
  sized by the FORMAL type `ILTypeInstance` (4 bytes), so `CopyNeoCallArguments` copied only the first 4
  bytes + the reader did `mStack[42]` OOB. AND the element ILType was lost to the shared CLR
  `ILTypeInstance[,]` type. Fix: a static token-keyed `s_neoIlVtArrayElementTypes` map (populated in
  `InitializeFunctionParam`, which has the Cecil token) recovers the element ILType; `TryNeoIlVtElementArrayCall`
  intercepts in the `Call` + `Callvirt_CLR` arms, boxes the element from the CALLER frame
  (Instantiate + CopyFrameToIL + Array.SetValue). **NO ABI change.**
- **Sub-gap 2 — Get unbox** (symmetric): `Array.GetValue` -> `CopyILToFrame` into the caller dest. Guard
  `elemIns.Type == elemIl` (the stored box IS the IL-VT's representation).
- **Sub-gap 3 — multi-dim `ref a[i,j]` ldelema — PARKED** (genuinely distinct): the `ref a[i,j]` lowering
  hits a JIT NRE (`ILType.get_IsValueType` dereferences a null `definition` on the Address/byref shape) —
  a different layer (JIT type resolution, not the Set/Get reader). Probe commented out; routed to a
  follow-up. The CORE scope (Get/Set) does not need it.

## Verification (LEAD-verify)
- **NeoStep smoke (LEAD re-ran): 278 tests, 0 failed** (274 + 4 IL-VT-multidim probes: PrimitiveControl,
  RoundTrip, MultiCell, RefFieldNonNull). NeoStep16 26/0/0; NeoOptHardening 24/0/0. No regression.
- **Legacy-neutral-by-improvement:** full Legacy 812 — HEAD **20 fail** -> +fix **17 fail** (3 FEWER).
  The shared `ILType.cs` ctor fix holds + improves the Legacy baseline.
- **Stash-toggle:** RoundTrip FAILs on HEAD (`KeyNotFoundException: Cannot find method:.ctor in type:
  ...NeoStep16Vt[0...,0...]`); +fix PASSES. Load-bearing.

## Durable findings
1. **A parked child's "foundational/needs-an-ABI-rework" framing is OFTEN a mis-attribution** (2nd
   confirmation after child 4). The per-param ref base was available all along; the real bug was a
   dest-slot-sizing + a lost-element-ILType issue. Re-audit before accepting "foundational".
2. **A CLR array type (`ILType`, IsArray) declares no ctor/Get/Set** — methods are on the underlying CLR
  array type; `ILType.GetConstructor`/`GetMethod` must delegate (sub-gap 0; benefits all IL array types).
3. **A token-keyed static map recovers an element ILType lost to a shared CLR array type** (the
  `s_neoIlVtArrayElementTypes` pattern) — reusable for any "the element type was erased to a shared CLR
  type" case.

## Follow-ups
- **Multi-dim `ref a[i,j]` ldelema** (sub-gap 3) — a JIT type-resolution NRE on the Address/byref shape
  (separate layer). The probe is commented out in `NeoStep16Test.cs`.
- **Pre-existing multi-string-`!=` comparison bug on IL-VT struct fields** (rank-1 + multi-dim;
  individual reads correct) — surfaced by a rank-1 IL-VT multi-cell diagnostic, reported in `design.md`,
  out of child-17 scope.

## Review
LEAD-verify (NeoStep smoke re-ran 278/0; Legacy 812 20->17-fail improvement; stash-toggle; Neo-gated
engine changes + the shared ctor fix Legacy-improving).
