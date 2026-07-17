# Ship Log — neo-ceq-null-instance-field (child 23)

**Change:** seed `Ldfld_Ref` in `registerTypes` so the direct-branch form of an instance-field null
comparison (`(field == null) ? a : b`, lowered to `brfalse`) specializes to `Brfalse_Ref`. Closes the
instance-field form of the null-comparison gap (child-11 deferred `Ldfld_Ref`).
**Capability:** `neo-optimizer` (ADDED requirement).
**Pipeline:** small-feature (re-audit -> propose -> apply -> verify -> review-clean -> ship).
**Date:** 2026-07-13. **Branch:** `features/object-model-overhaul`. **Base HEAD:** `0aa3b4b7`.

## Re-audit vindicated (framed mechanism DISPROVEN, real root cause pinned)
The framed question "why doesn't `Ceq_Ref` fire for an instance field?" was a FALSE PREMISE — the JIT dump
proved `Ceq_Ref` fires FINE for the `ceq` form (`bool b = field == null;` / `if(field==null)` both PASS on
HEAD via ldnull's `ObjectType` seed). The REAL gap: the TERNARY `(field == null) ? a : b` lowers to a DIRECT
`brfalse.s` (no ceq), which stays PLAIN because `Ldfld_Ref` has no `registerTypes` seeding case → the
`Brtrue`/`Brfalse` specialization (keys solely on `IsNeoReferenceSlot(registerTypes[op.Register1])`) no-ops
→ a null instance field (the `-1` sentinel, `!= 0`) reads TRUTHY → wrong branch → wrong result.

**Roslyn lowering variance (durable):** `bool b = ref==null` and `if(ref==null)` lower to `ceq`; the ternary
`(ref==null)?a:b` lowers to a DIRECT `brfalse`. A fault probe for a null-comparison gap MUST use the ternary.

## What shipped (Neo-only, +29 lines / 0 removed, single file)
**`ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`** — a `case OpCodeREnum.Ldfld_Ref:` seeding case
in `TypeSpecializeNeoOpcodes` (after child-21's raw-Ldfld case, ~:1105):
```csharp
case OpCodeREnum.Ldfld_Ref:
    if (op.Operand4 == 0)   // exclude the F-10 boxed-CLR-struct case (dest is flat bytes, not a ref)
        SetRegisterType(registerTypes, op.Register1, appdomain.ObjectType);
    break;
```
Seed `ObjectType` (consumers key on `IsNeoReferenceSlot` only). The `Operand4 == 0` guard excludes the F-10
boxed-CLR-struct case: the ONLY `Operand4` stamp reachable on `Ldfld_Ref` is `IsClrStructFieldOfIL`
(`:3072-3073`, requires `fieldType.IsValueType` → genuine reference fields leave it 0); the F-10 runtime arm
flattens the dest to flat bytes, so not seeding it as a reference is correct. `Ldfld_Ref` is emitted ONLY
for non-primitive, non-IL-VT fields → a reference seed can never collide with an int-branch path.

Plus the permanent probe `TestCases/NeoStepCeqNullInstanceFieldTest.cs` (TC1 ternary faulting + TC2/TC3 ceq
controls + TC4 non-null guard).

## Why
The unseeded-reference-producer defect class: Neo's untyped frame relies on JIT-time `registerTypes` seeding
for `Brtrue_Ref`/`Brfalse_Ref` specialization. Child-11 seeded `Ldsfeld` (static) but explicitly DEFERRED
`Ldfld_Ref` (heap instance). This closes that deferral — seeding `Ldfld_Ref` fixes BOTH the direct
`brtrue`/`brfalse` form AND the two-reference `ceq`/`beq`/`bne.un` identity in one shot.

## Verification
- **NeoStep smoke: 365/0** (361 baseline + 4 probes), no regressions, EXIT=0. Child-11 delegate-cache
  canaries (Tr2/Tr5) clean.
- **Stash-toggle (airtight):** stash `JITCompiler.cs` (keep probe) → TC1 FAULTS (`brfalse.s` plain,
  DivideByZero at the 1/0 guard); pop → PASS (`brfalse.ref` fires, returns 1).
- **JIT dump (post-fix):** TC1 `2:brfalse.ref` (was `2:brfalse.s` on HEAD).
- **BONUS (causal, stash-toggle-confirmed):** child-22's residual `ActivatorCreateInstanceWithArgsTest` now
  PASSES — the `ILValue` auto-property getter inlines to `ldfld.ref` → the seed fires → `ILValue == null`
  reads correctly. Stash-toggle: 1 failed on HEAD → 2/0 PASS with fix.
- **Legacy-neutral:** the enclosing `TypeSpecializeNeoOpcodes` method is `#if ENABLE_NEO_MODE`-gated.

## Review
**APPROVE-WITH-FINDINGS** (reviewer != implementer; independent re-run of smoke + stash-toggle + the bonus
test + Operand4-guard cross-check).
- **0 Blocker / 0 Major.**
- Trivial: the new comment describes a null instance field as "a valid mStack index N != 0" but the runtime
  arm encodes null as the `-1` sentinel (`-1 != 0` either way → conclusion/fix correct). Doc-accuracy nit.
- Minor: the spec asserts a two-reference-field identity (`d.A == d.B` → `ceq.ref`) but no TC5 verifies it;
  mechanically sound (producer-side seed covers it), untested claim.
- Out-of-scope (pre-existing, latent): `Ldfld_Ref_Inline` has no F-10 flatten branch and its seed (`:843`)
  is unguarded — interaction with an in-frame-VT-owner F-10 field is the latent O2 edge, not reached by smoke.

## Delivery
**Mode:** local commit (portfolio per-child delivery; push per parent directive). Committed with the
portfolio-run.json + planning-context.md record updates. No PR.

## Durable findings (for future planning)
1. **The unseeded-reference-producer defect class is now FULLY CLOSED for the heap instance-field path**
   (child-11 `Ldsfeld` static → child-21 primitive floats → child-23 `Ldfld_Ref` heap instance). Seeding
   `Ldfld_Ref` fixes both the direct branch form and the two-reference ceq/beq/bne.un identity (consumers
   key on `IsNeoReferenceSlot` alone).
2. **Roslyn ternary `(ref==null)?a:b` lowers to a DIRECT `brfalse` (no ceq); `if(ref==null)` and
   `bool b=ref==null` lower to `ceq`.** A fault probe for a null-comparison gap MUST use the ternary. Check
   the JIT dump for plain `brfalse.s`/`brtrue.s` vs `ceq.ref` before assuming `Ceq_Ref`.
3. **Auto-property getters of reference type INLINE to `ldfld.ref`**, so this fix covers them implicitly —
   which is why child-22's `ActivatorCreateInstanceWithArgsTest` P1 unblocked for free.
