# Proposal: neo-ceq-null-instance-field

## Summary

Under Neo (`ExecuteNeo`), a DIRECT `brtrue`/`brfalse` on an INSTANCE reference
field that is NULL mis-reads the field as non-null (truthy), because the field
load (`ldfld.ref` / `OpCodeREnum.Ldfld_Ref`) does NOT seed
`registerTypes[dest]` as a reference. The `Brtrue`/`Brfalse` type-specialization
case in `TypeSpecializeNeoOpcodes` (`JITCompiler.cs:1423-1434`) keys SOLELY on
`IsNeoReferenceSlot(registerTypes[op.Register1])`; with the dest unseeded that
test is false, the branch stays the plain integer `Brtrue_S`/`Brfalse_S`, and a
null reference field -- encoded as a VALID mStack index pointing to a null entry
(a NON-ZERO int) -- reads truthy. This silently skips `if (field == null) {...}`
bodies / inverts `(field == null) ? a : b` ternaries, leaving the field null and
NRE-ing downstream (`Neo callvirt this is null`).

This is the INSTANCE-FIELD form of child-11's `Brtrue_Ref`/`Brfalse_Ref` gap.
Child-11 (correctly) seeded the STATIC-field producer (`Ldsfeld`) but explicitly
DEFERRED the instance-field producer -- design D2: "verify (diagnose-first) the
heap `Ldfld_Ref` dest seeding... seed if a smoke pattern needs them", and Risk
"[Other ref-producer seeding gaps]". This child closes that deferred gap. It is
the SAME unseeded-reference-producer defect class as child-11 (`Ldsfeld`),
viewed through the instance-field load.

## Re-Audit Verdict: REPRODUCES (root cause PINNED), but the framed mechanism is WRONG

The framed gap (surfaced by child 22) hypothesized that `Ceq_Ref` FAILS to fire
for `ldfld.ref <instance field>; ldnull; ceq`. **That hypothesis is DISPROVEN by
the JIT dump.** A minimal probe (`TestCases/NeoStepCeqNullInstanceFieldTest.cs`)
shows the `ceq` form of `field == null` WORKS on HEAD:

```
TC2 (bool b = field == null):
  Final Results:
    1:ldfld.ref r7, r0, ...     ; load null instance field
    2:ldnull r8
    3:ceq.ref                   ; <-- Ceq_Ref FIRES (via ldnull's ObjectType seed)
  Return:1                       ; correct
```

`Ldnull` seeds its dest `appdomain.ObjectType` (`JITCompiler.cs:874-875`), a
reference; the `Ceq`->`Ceq_Ref` decision checks `IsNeoReferenceSlot(Register2)
|| IsNeoReferenceSlot(Register3)` (`:957-958`) and fires via the ldnull operand.
So child-12's `Ceq_Ref` is correct and IS firing. TC2 and TC3 (the materialized
-bool and direct-`if` shapes) both PASS on HEAD via `ceq.ref`.

**The REAL bug is the DIRECT branch form**, which Roslyn emits for the TERNARY
`(field == null) ? a : b` (and for some bare-reference branch shapes). TC1 FAULTS
on HEAD (DivideByZero, HEAD `0aa3b4b7`):

```
TC1 (int r = (field == null) ? 1 : 0):   field is null, r must be 1
  Final Results:
    1:ldfld.ref r7, r0, ...     ; load null instance field (dest r7 UNSEEDED)
    2:brfalse.s r7, 5           ; <-- PLAIN Brfalse_S, NOT Brfalse_Ref!
    3:ldc.i4.0 r7               ; fall-through: r = 0  (WRONG -- field IS null)
    4:br.s 6
    5:ldc.i4.1 r7               ; branch target (brfalse taken): r = 1
  => brfalse not taken (null field = valid index N != 0 -> truthy) -> r = 0
  -> `if (r != 1) { 1/0 }` FAULTS (DivideByZero)
```

The `brfalse.s r7` is PLAIN because `registerTypes[r7]` is null/empty after
`ldfld.ref` (`Ldfld_Ref` has NO seeding case in `TypeSpecializeNeoOpcodes`),
so `IsNeoReferenceSlot` is false at `:1428` and the branch is not rewritten to
`Brfalse_Ref`. (Confirmed: the dump prints `brfalse.s`; a specialized arm would
print `brfalse.ref` / `Brfalse_Ref`.)

## Root Cause (pinned)

`TypeSpecializeNeoOpcodes` (`JITCompiler.cs`) seeds `registerTypes[dest]` for
`Ldc_*`, `Ldnull`, `Ldstr`, `Ldsfeld` (child-11), the typed `Ldfld_I4/I8/R4/R8`
arms, the raw `Ldfld` (child-21, CLR-struct-primitive only), `Ldind_*`/`Ldelem_*`
(child-16), `Conv_*`, `Ldloca`, `Ldflda`, and `Newobj` (IL VT). There is NO
`case OpCodeREnum.Ldfld_Ref:`. So an instance REFERENCE field load leaves its
dest unseeded, and any DIRECT `brtrue`/`brfalse` consumer stays the plain
integer branch. This is hypothesis (d) -- the unseeded-producer class -- on the
REFERENCE-branch specialization path (child-11's domain), NOT on the Ceq_Ref
path (child-12).

## Eliminated hypotheses (mandatory)

- **(a) `ceq`'s reference-operand check looks at the wrong register.** DISPROVEN.
  `Ceq_Ref` fires correctly for the ceq form (TC2/TC3 JIT: `ceq.ref`), and the
  `Ceq` decision checks BOTH `Register2` and `Register3` (`:957-958`). Not a
  `ceq` bug at all.
- **(b) `ldnull`'s dest is not seeded as a reference / the Ceq_Ref decision
  requires BOTH operands.** DISPROVEN. `Ldnull` seeds `appdomain.ObjectType`
  (`:874-875`), and the decision is "EITHER operand" (`||`). TC2 proves it.
- **(c) The field value flows through a temp local / Move that loses the
  reference type.** DISPROVEN for TC1. The TC1 JIT dump shows `ldfld.ref r7`
  flowing DIRECTLY into `brfalse.s r7` -- no intermediate `Move`. The loss is at
  the PRODUCER (`Ldfld_Ref` does not seed), not via a Move. (The `Move` case at
  `:880-884` DOES propagate the source type, so a Move would preserve a seed if
  one existed -- confirming the producer is the gap.)
- **(d) Unseeded-producer class: the instance-field load does not seed.**
  CONFIRMED -- this IS the root cause. `Ldfld_Ref` has no seeding case.
- **(e) `Ceq_Ref` fires but its deref is wrong for the instance-field null
  encoding.** DISPROVEN. `Ceq_Ref`'s resolve (`R(v) = v>=0 ? mStack[v] : null`,
  `ILIntepreter.Neo.cs:2031-2032`) is correct; TC2/TC3 return correct values.

## Goals / Non-Goals

**Goals:**
- A DIRECT `brtrue`/`brfalse` on an INSTANCE reference field (the Roslyn lowering
  for `(field == null) ? a : b` and bare-reference branch conditions) tests the
  referenced object's nullness, not the raw mStack-index int. The lazy-init /
  null-guard patterns work for instance fields.
- Root-cause, minimal, Neo-only, Legacy-neutral fix at the seeding site (sibling
  of child-11 `Ldsfeld` and child-16/21 primitive-producer seeding).
- NeoStep probe(s) that FAULT on HEAD (TC1 proves the defect) and pass after.

**Non-Goals:**
- `Ldfld_Ref_Inline` (a reference field of an in-frame VT owner) -- rarer shape;
  seed only if a smoke pattern demands it (open question O2).
- A general runtime type-tag for the Neo frame (architecturally rejected; child-11
  D1). The JIT-time `registerTypes` dataflow remains the only signal.
- The `ceq`/`beq`/`bne.un` form of instance-field `== null` -- already correct on
  HEAD (TC2/TC3); NOT touched.
- `Stfld_Ref` -- it CONSUMES a value (no dest feeding a branch); needs no seed
  (same reasoning as child-21 raw-`Stfld` exclusion).

## Capability

`neo-optimizer` (owns `TypeSpecializeNeoOpcodes` + the `Brtrue`/`Brfalse`
specialization; child-11/child-16/child-21 precedent). ADDED requirement
pinning "an instance reference field load (`Ldfld_Ref`) MUST seed
`registerTypes[dest]` as a reference so the direct `Brtrue_Ref`/`Brfalse_Ref`
specialization fires."

## Probe status

`TestCases/NeoStepCeqNullInstanceFieldTest.cs` is LEFT IN PLACE (not removed).
TC1 is the load-bearing FAULT evidence (ternary/direct-brfalse form). TC2/TC3
are useful controls proving the `ceq` form already works (so the apply worker
does not chase `Ceq_Ref`). TC4 is the non-null false-positive guard. The apply
worker should keep TC1 (and TC4), and may prune/refine TC2/TC3 or add a
two-reference-field identity probe (see design D4).
