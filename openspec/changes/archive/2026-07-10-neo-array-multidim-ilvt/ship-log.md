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
- **Sub-gap 3 — multi-dim `ref a[i,j]` ldelema — RESOLVED** (re-audited: the "distinct JIT-layer NRE"
  framing was a specific, tractable bug — two of them). Previously PARKED as "a JIT type-resolution
  NRE separate from the Set/Get reader." Re-audit found it was (a) the documented NRE + (b) a stale
  register-type miss, BOTH fixed with focused guards (NOT a deep rework):
  - **(a) The NRE** (`ILType.get_IsValueType` dereferences a null `definition`): a `ref T` param's
    register type is a ByRef ILType (`MakeByRefType` constructs an ILType wrapping a `ByReferenceType`,
    and `RetriveDefinitino` deliberately leaves `definition` null for it). Added an `IsByRef` short-
    circuit to `ILType.IsValueType` (returns false — a byref is never a value type). Shared-engine,
    Legacy-safe (semantically correct for both engines).
  - **(b) Stale in-frame-VT type on a Call dest** (`JITCompiler.Translate` type-inference pass, Neo):
    the `Address` call's dest register was REUSED from an earlier `ldloca` of a VT local (which seeds
    the register with the VT type). The type-inference switch had no `Call` case, so the dest RETAINED
    the stale VT type, and the inlined `ref T` callee's `stfld` was mis-lowered to `_Inline` (writing
    into the frame instead of through the byref to the array cell's box -> mutation not observable).
    Fix: a `Call`/`Callvirt` case that clears a stale in-frame-VT type on the dest when the call's
    return is NOT a by-value IL-VT (a byref/reference/primitive return is never an in-frame VT; a
    by-value VT return — the `Get` path, or any VT-returning IL method — IS materialized into the dest
    via CopyILToFrame and is KEPT). The IL-VT-element array `Get`'s element type is recovered via the
    token-keyed `GetNeoIlVtArrayElementType` map (its resolved ReturnType is the shared CLR
    `ILTypeInstance`).
  - **Runtime `Address` intercept** (`ILIntepreter.Neo.cs` `TryNeoIlVtElementArrayCall`): an `Address`
    branch materializes the element's box (lazy-init for a null cell) and writes an 8-byte Ref Slot
    `(mStackIdx_of_the_box, fieldOffset=0)` to the caller's return dest. A consumer's `stfld`/`stobj`
    through the byref resolves `mStack[mStackIdx]` -> the SAME box the array cell references, so the
    in-place mutation is observable on a subsequent `a[i,j]` Get. (The probe's `BumpByRef(ref a[0,0])`
    is inlined; with fix (b) the inlined `stfld` stays non-inline and writes through the byref to the box.)

## Verification (LEAD-verify)
- **NeoStep smoke (LEAD re-ran): 279 tests, 0 failed** (274 + 4 IL-VT-multidim Get/Set probes + the
  now-GREEN ldelema mutate probe `NeoStep16_MultiDimIlVtLdelemaMutate`). NeoStep16 27/0/0;
  NeoOptHardening 24/0/0. No regression.
- **Legacy-neutral-by-improvement:** full Legacy 812 — HEAD **20 fail** -> +fix **17 fail** (3 FEWER).
  The shared `ILType.cs` ctor fix holds + improves the Legacy baseline. The sub-gap-3 `IsByRef` guard
  is Legacy-safe (Legacy plain-`Debug` builds 0 errors; the 2 NeoStep16 failures on Legacy — TC8
  StelemI + the ldelema probe — are pre-existing Neo-on-Legacy gaps, identical on HEAD).
- **Stash-toggle (load-bearing):** the ldelema probe NREs on HEAD at
  `ILType.get_IsValueType()` (the null-`definition` deref, line 1838); +fix PASSES (in-place mutation
  via `ref a[0,0]` observable — `r.num == 99`). RoundTrip also FAILs on HEAD
  (`KeyNotFoundException: Cannot find method:.ctor`); +fix PASSES.

## Durable findings
1. **A parked child's "foundational/needs-an-ABI-rework" framing is OFTEN a mis-attribution** (2nd
   confirmation after child 4). The per-param ref base was available all along; the real bug was a
   dest-slot-sizing + a lost-element-ILType issue. Re-audit before accepting "foundational".
2. **A parked child's "distinct JIT-layer NRE" framing is ALSO often a mis-attribution** (3rd
   confirmation): the sub-gap-3 NRE was a specific null-guard miss (a ByRef ILType has
   `definition==null` by design) + a stale-register-type miss (a Call dest reused from a VT-seeded
   `ldloca`). Two focused guards, NOT a deep rework. Re-audit parked JIT findings before accepting
   "separate layer / deep."
3. **A CLR array type (`ILType`, IsArray) declares no ctor/Get/Set** — methods are on the underlying CLR
  array type; `ILType.GetConstructor`/`GetMethod` must delegate (sub-gap 0; benefits all IL array types).
4. **A token-keyed static map recovers an element ILType lost to a shared CLR array type** (the
  `s_neoIlVtArrayElementTypes` pattern) — reusable for any "the element type was erased to a shared CLR
  type" case.
5. **A Call result register is NEVER an in-frame value type** (it is a reference / primitive / byref),
  so the Neo type-inference pass must clear any stale in-frame-VT type on a reused Call dest — EXCEPT
  a by-value IL-VT return (materialized into the dest via CopyILToFrame). The `Address` (byref return)
  case must be cleared; the `Get` (by-value VT return) case must be kept.

## Follow-ups
- ~~**Multi-dim `ref a[i,j]` ldelema** (sub-gap 3)~~ — **RESOLVED** (see Delivered above). The probe
  `NeoStep16_MultiDimIlVtLdelemaMutate` is now GREEN and part of the NeoStep smoke (278 -> 279).
- **Pre-existing multi-string-`!=` comparison bug on IL-VT struct fields** (rank-1 + multi-dim;
  individual reads correct) — surfaced by a rank-1 IL-VT multi-cell diagnostic, reported in `design.md`,
  out of child-17 scope.

## Review
LEAD-verify (NeoStep smoke re-ran 278/0; Legacy 812 20->17-fail improvement; stash-toggle; Neo-gated
engine changes + the shared ctor fix Legacy-improving).
