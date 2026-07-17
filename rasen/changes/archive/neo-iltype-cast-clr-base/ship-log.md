# Ship Log — neo-iltype-cast-clr-base (Wave-2 child C2)

**Change:** IL instance (ILTypeInstance) passed as a CLR-base/interface-typed arg to a CLR call wasn't unwrapped
to its CrossBindingAdaptor -> `InvalidCastException` (~14 tests).
**Capability:** `neo-dispatch` (ADDED requirement).
**Pipeline:** small-feature (grounding -> re-audit -> propose+apply -> verify -> review -> ship).
**Date:** 2026-07-14. **Branch:** `features/object-model-overhaul`. **Base HEAD:** `0414c296`.

## Root cause (PINNED; the task's castclass/isinst framing was DISPROVEN)
Neo's castclass/isinst already match Legacy (raw-obj-on-success). The real site is the **autogen Neo CLR binding's
call-arg read**: `(T)ReadNeoReference(...)` -- a direct cast. Legacy's binding uses `typeof(T).CheckCLRTypes(...)`,
whose ILTypeInstance branch returns `ins.CLRInstance` (the CrossBindingAdaptor that IS-A the base / implements the
interface). Neo's reflection fallback projects only `this`; the autogen-redirect path projected NEITHER `this` nor
reference params. So an ILTypeInstance hit the binding's `(CLRBase)` cast and threw.

## What shipped (ONE file, Neo-gated -> Legacy-neutral)
`ILIntepreter.Neo.cs`: added `ProjectNeoClrCallRefArgs` + `ProjectNeoClrRefSlot` + `NeoClrPrimitiveSlotSize`,
called at the top of `InvokeNeoClrMethod` (the SINGLE choke point for both redirect + reflection CLR-call paths).
It walks the callee-frame layout (mirrors `CLRMethod.Invoke`'s curPrim walk: prim 1/2/4/8, enum=4, CLR-VT=managed
size, ref=4, newobj skips retRefBase) and rewrites each ILTypeInstance ref slot's index to a FRESH mStack slot
holding `CLRInstance`. `this` projects UNCONDITIONALLY (the adaptor always exists; fixes the `base.ToString()`
StackOverflow -- an IL `call Object::ToString` virtually re-dispatched on the ILTypeInstance -> infinite recursion);
params project only when `!targetType.IsInstanceOfType(obj)` (avoids converting an ILTypeInstance-typed/object param
that should stay -- the NeoStep14 regression). Byref (write-back safety), delegates (binding self-unwraps), and
ILType params are skipped. Caller frame (`frameBase`) is NEVER mutated -- only the callee `targetBase` view.

## Verification (truth = full-smoke number)
- **FULL SMOKE: 140 -> 133 (-7).** C2 flipped green: InheritanceTest01/02/03/04/14, TestIs.TestInterface (6 C2) +
  InheritanceTest05 (C14 TargetException bonus). Remaining C2-listed tests have DISTINCT downstream roots (reported,
  not fixed): InheritanceTest16 (Muli_R4 JIT gap), 21/22 (NRE), 06 (other cast), TestAs03/RefOutTest (cast to
  'Adaptor'), StructTest6 (reverse String->ILTypeInstance), GenericMethodTest11 (constrained-callvirt-to-interface edge).
- **Stash-toggle airtight** (pathspec-stash Neo.cs only): InheritanceTest01 FAILS `Unable to cast ILTypeInstance to
  ClassInheritanceTest`; pop -> PASS.
- **NeoStep 380/0** (no regression; confirmed twice -- the NeoStep14 regression was caught + fixed during dev).
- **Legacy-neutral:** single file, entirely `#if ENABLE_NEO_MODE`; InheritanceTest01 PASS under plain Debug+useRegister=true.

## Review
**APPROVE-WITH-FINDINGS** (reviewer != implementer; 0 Blocker/Major). Projection-no-overfire PASS (only ILTypeInstance
by-value CLR-ref param slots + non-VT `this`; primitive/CLR-VT/ILType/byref advanced-over-not-touched; caller frame
never mutated; byref skipped so write-back targets the correct caller cell; stride byte-consistent with both readers).
Spot-tests + NeoStep + Legacy-neutral + stash-toggle all re-confirmed by reviewer.
- Minor-1: `object`/base-typed CLR params receiving an ILTypeInstance aren't projected (the `!IsInstanceOfType` guard
  keys on whether the direct cast throws) -- deliberate minimal scope; full `object`-param parity is a follow-up.
- Minor-2: constrained.callvirt-to-CLR `this` offset (GenericMethodTest11) -- projection is a safe no-op there; deferred.
- Minor-3: projected slots are unreclaimed `mStack.Add` appends -- matches the existing newobj/box pattern (no new leak class).

## Delivery
local commit + push (portfolio per-child). No PR.

## Durable findings (for future planning)
1. **Neo castclass/isinst already match Legacy** (raw-obj-on-success) -- do NOT chase the ILTypeInstance->adaptor
   unwrap there. The unwrap belongs in the CALL-ARG path, where Legacy does it per-arg via `CheckCLRTypes` and Neo
   (autogen bindings + reflection `this`-only) missed it.
2. **`InvokeNeoClrMethod` is the single choke point** for all CLR-call arg projection (redirect + reflection).
   Projecting there (not per-binding) fixes all static bindings without regen.
3. **`this` projects UNCONDITIONALLY** (no assignability guard) -- else `base.ToString()`/`base.Equals()` (an IL
   `call Object::method`) virtually re-dispatches on the ILTypeInstance -> infinite recursion. Virtual calls on IL
   objects route to callvirt.il, so projecting the Object binding's `this` only affects base/explicit calls (safe).
4. **Generator follow-up** (out of scope): `MethodBindingGenerator.cs:302` + `BindingGeneratorExtensions.cs:250`
   still emit the bare cast; future regen should emit `CheckCLRTypes` (the stale-binding class, C1/C2 both hit it).
