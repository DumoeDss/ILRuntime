## Why

Neo Steps 1-16 ship object/VT/array/dispatch/exception machinery but cannot run
`ref`/`out` parameters, `ldloca`/`ldflda`/`ldarga` of an *escaped* address (one
passed to a method, stind/ldind, `fixed`, or stored), `stind_*`/`ldind_*`,
`stobj`/`ldobj`, `ldelema`, or `constrained.`-on-value-type. The current Neo
`Ldloca`/`Ldflda` arms are runtime **no-ops** that only work because the Step 12
`addrAlias` folding resolves in-frame-VT field access to compile-time offsets;
any genuine byref use (a real address that escapes the folding window) is
silently unimplemented. This step closes that gap with a unified 8-byte **Ref
Slot** model, and folds in the two deferred items whose prerequisite is exactly
this byref/VT-address model: **D-LDELEMA** (`ldelema`, deferred from Step 16)
and **D-CONSTRAINED** (`constrained.`-on-value-type, deferred from Step 13
area 3).

## What Changes

**Core: unified 8-byte Ref Slot + stind/ldind dispatch (in this pass).**
- Add a runtime Ref Slot = `(objectIndex: int, offset: int)` stored in the
  frame byte region as 8 bytes (4-byte aligned). `objectIndex == -1` means a
  frame-native (unmanaged) address: `offset` is an absolute frame byte offset.
  `objectIndex >= 0` means an mStack object: `offset` is a field/element
  primitive offset inside that object (or a CLR field hash).
- Give `Ldloca`/`Ldloca_S` a real runtime arm producing `(-1, frameOffset)`
  for an in-frame operand, **coexisting** with the Step 12 `addrAlias` folding
  for the pure `ldloca;ldflda;stfld/ldfld` in-frame-VT field-access pattern
  (the highest-value fast path stays zero-overhead; see design sec "addrAlias
  reconciliation").
- Add real `Ldflda` / `Ldarga` / `Ldarga_S` arms: in-frame VT field ref
  (`-1, frameOffset+fieldOffset`), heap IL field ref
  (`(mStackIdx, fieldPrimitiveOffset)`), and a CLR-field path. `ldelema`
  produces `(arrayMStackIdx, elementByteOffset)`.
- Add `stind_*` / `ldind_*` / `stobj` / `ldobj` ExecuteNeo arms dispatching on
  the Ref Slot: frame-native pointer arithmetic; ILTypeInstance pinned
  `Primitives` read/write (+ the ref-region slot for ref fields); and (deferred)
  CLR field-by-hash.
- JIT: `AllocateSlotForType` / `AllocateNeoCallParamSlot` allocate 8 bytes (4
  alignment) for a byref-typed local/param/temp, and the call-lowering emits an
  8-byte Ref Slot copy for byref params instead of copying the referent's value.

**Byref call-ABI (in this pass).** A `ref`/`out` IL parameter (`IsByRef`) is
sized 8 bytes on both caller and callee sides; the caller writes the Ref Slot
into the callee's param region and the callee reads/stores-through it. No
copy of the referent; mutation through the slot propagates to the caller's
frame/object (the whole point of `ref`/`out`).

**D-CONSTRAINED (in this pass).** Add a runtime `Constrained` arm so a
`constrained.callvirt T.M` on a value-type `this` boxes-once-or-direct-calls
informed by the constrained type (which is now an mStack object whose address
the byref model can produce via `ldarga`/`ldloca`). The JIT's current
"re-append Constrained after the callvirt" move stays; the Constrained opcode
carries the constrained type token and the callvirt's dispatch result.

**Explicit In / Deferred list.**
- **In this pass:** Ref Slot representation + frame storage; `Ldloca`/`Ldloca_S`
  real arm + addrAlias coexistence; `Ldarga`/`Ldarga_S`; `Ldflda` (in-frame-VT +
  heap-IL arms); `stind_*`/`ldind_*` for frame-native and ILTypeInstance
  (pinned-Primitives) targets; `stobj`/`ldobj` for the same; byref call-ABI for
  IL-method `ref`/`out` params; D-LDELEMA (`ldelema`); D-CONSTRAINED runtime arm
  for the common VT-box/direct-call cases; NeoStep17 regression tests.
- **Deferred (stated explicitly, NOT silently dropped):**
  - **CLR-object stind/ldind via field hash** (`(objMStackIdx, fieldHash)` path
    in stind/ldind/Ldflda) -- the most complex sub-part; rare in IL hot code
    (IL code stind/ldind targets a frame ref or an IL object far more often
    than a raw CLR field). Throws a Step-17-tagged NIE until a test exercises
    it; lands in Step 13b (CLR binding/param layout) where the field-hash
    plumbing already has to be revisited.
  - **Generic-byref** (`ref T` / `out T` where `T` is a generic parameter
    spanning IL/CLR) and **explicit-interface byref** -- defer to a follow-up
    once the concrete byref ABI is green on closed types.
  - **`fixed`** (unmanaged-pinning block) -- out of scope; the address model
    enables it but the pinning/`Pinned`-slot machinery is its own concern.

## Capabilities

### New Capabilities
- `neo-byref`: the unified 8-byte Ref Slot representation, the
  `ldloca`/`ldflda`/`ldarga` address-producing arms, the
  `stind`/`ldind`/`stobj`/`ldobj` store/load-indirect dispatch, the byref
  call-ABI for `ref`/`out` IL parameters, the `ldelema` array-address producer,
  and the `constrained.` runtime arm. Includes the contract that the Step 12
  in-frame-VT `addrAlias` folding fast path is preserved.

### Modified Capabilities
- `neo-value-types`: the `Ldloca`/`Ldflda` runtime arms change from no-ops to
  real Ref-Slot producers (the no-op-fast-path stays only for the folded
  in-frame-VT case; genuine byref use now works). This is a behavior change to
  the requirement "ldloca of an in-frame VT is a no-op" -> "ldloca produces a
  real Ref Slot unless the addrAlias folding already resolved its consumers."
- `neo-arrays`: `ldelema` is added (was a Step-16-tagged NIE / explicit
  non-goal).

## Impact

- **Code:** `ILIntepreter.Neo.cs` (new `Ldloca`/`Ldflda`/`Ldarga` real arms,
  `stind_*`/`ldind_*`/`stobj`/`ldobj`/`ldelema`/`Constrained` arms, byref
  call-ABI writeback); `JITCompiler.cs` (`AllocateSlotForType` /
  `AllocateNeoCallParamSlot` 8-byte byref sizing, byref call-param copy,
  `Ldflda`/`Ldelema` operand stamping); `Optimizer.Neo.cs` (Ref-Slot-aware
  lowering for the new arms, byref param-map entries, addrAlias coexistence).
- **Highest regression risk of any step:** the Step 12 `addrAlias` folding, the
  OPT-HARDEN `ldloca-kill` (K1 fix), and Steps 12-16 inline field access are all
  ldloca-adjacent. A bug in reconciling the real Ref Slot with the folding can
  regress every value-type-field-access test. The full NeoStep smoke (72 cases)
  is the gate; the reconciliation strategy (design sec "addrAlias reconciliation")
  is coexistence, not replacement.
- **K1 interaction:** the OPT-HARDEN `ldloca-kill` stays sound and unchanged --
  taking an address is still a potential-mutation escape, whether the address
  is folded or a real Ref Slot.
- **Tests:** new `TestCases/NeoStep17Test.cs` (ASCII): `ref int` frame ref
  (`Increment(ref x)`); `ref` heap-object field; `out` param; ref to an in-frame
  VT field; `ldelema` + `stind`/`ldind` round-trip. Legacy (`ExecuteR`) is the
  reference and is NOT modified.
- **Non-goals:** whole value-type newobj (Step 18), CLR-field-hash stind/ldind
  (Step 13b), generic-byref, explicit-interface byref, `fixed`.
