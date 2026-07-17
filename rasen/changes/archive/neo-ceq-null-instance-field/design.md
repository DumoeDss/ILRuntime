## Context

Neo (`ExecuteNeo`) runs on a compact `byte*` frame whose slots are UNTYPED. A
reference is encoded as an mStack index in the slot's primitive bytes; NULL is
either a VALID index pointing to a null mStack entry (instance fields, IL-static
fields) or the `-1` sentinel (`ldnull`, CLR-static fields). Because the frame
carries no per-slot type tag, the ref-vs-int distinction for a branch condition
MUST be made at JIT time, via the `registerTypes[]` dataflow in
`TypeSpecializeNeoOpcodes` (`JITCompiler.cs`). This is the constraint child-11
(Brtrue_Ref/Brfalse_Ref) and child-12 (Ceq_Ref/Beq_Ref/Bne_Un_Ref) operate under.

`Brtrue`/`Brfalse` (`ILIntepreter.Neo.cs:2063`/`:2071`) test
`*(int*)(frameBase + ip->DstOffset) != 0`. Correct for an int32 truth value,
WRONG for a reference: a null instance field is a NON-ZERO mStack index (valid
index to a null entry) -> reads truthy -> null mis-classified as non-null.

The `TypeSpecializeNeoOpcodes` Brtrue/Brfalse case (`JITCompiler.cs:1423-1434`)
rewrites `op.Code` -> `Brtrue_Ref`/`Brfalse_Ref` when
`IsNeoReferenceSlot(GetRegisterType(registerTypes, op.Register1))`. Child-11
seeded the STATIC-field producer (`Ldsfeld`, `:1395-1409`) so `ldsfld ref;
brtrue` works. But the INSTANCE-field producer (`Ldfld_Ref`) was NOT seeded --
child-11 design D2 explicitly deferred it ("verify ... the heap Ldfld_Ref dest
seeding ... seed if a smoke pattern needs them"). This child closes that gap.

## The defect (JIT-dump evidence, HEAD `0aa3b4b7`)

Probe `TestCases/NeoStepCeqNullInstanceFieldTest.cs`, TC1
`(field == null) ? 1 : 0` on a null `string` instance field -- FAULTS
(DivideByZero). Roslyn lowers the ternary to a DIRECT branch on the field:

```
1:ldfld.ref r7, r0, ...      ; load null instance field -- dest r7 UNSEEDED
2:brfalse.s r7, 5            ; PLAIN Brfalse_S (not Brfalse_Ref)
3:ldc.i4.0 r7                ; r = 0  (WRONG: field IS null)
4:br.s 6
5:ldc.i4.1 r7                ; r = 1  (branch-taken target)
```

Plain `Brfalse_S` tests `r7 == 0`. The null field holds a valid mStack index
`N != 0` -> not equal to 0 -> branch NOT taken -> `r = 0` -> wrong -> the
`if (r != 1) { 1/0 }` guard FAULTS.

The CONTROL probes confirm the `ceq` form is NOT the bug:
- TC2 `bool b = field == null;` lowers to `ldfld.ref; ldnull; ceq.ref` and
  RETURNS CORRECT (`ceq.ref` fires via ldnull's `ObjectType` seed). PASS on HEAD.
- TC3 `if (field == null) {r=1;}` lowers (under this Roslyn) to the `ceq` form
  (`ldfld.ref; ldnull; ceq.ref; brfalse`). PASS on HEAD.
- TC4 non-null field: PASS on HEAD.

So the bug is SPECIFICALLY the direct-branch lowering of `(field == null) ? ..`
(Roslyn emits `ldfld.ref; brfalse/brtrue` directly, no `ceq`). The same
mis-specialization would affect any DIRECT `brtrue`/`brfalse` whose operand is a
bare instance reference field/local-temp produced by `Ldfld_Ref`.

## Goals / Non-Goals

**Goals:**
- A DIRECT `brtrue`/`brfalse` on an instance reference field tests the referenced
  object's nullness. `(field == null) ? a : b` and bare-reference branch
  conditions work for instance fields.
- The fix ALSO closes the latent TWO-reference-field identity via `ceq`
  (`field1 == field2`, both `ldfld.ref`-loaded, no `ldnull`): today that stays
  plain `Ceq` (BOTH operands unseeded -> `Ceq_Ref` no-ops); after seeding both
  operands, `Ceq_Ref` fires. (Not separately probed; covered for free by the
  producer-side seed.)
- Minimal, Neo-only, Legacy-neutral.

**Non-Goals:**
- `Ldfld_Ref_Inline` seeding (a ref field of an in-frame VT owner) -- O2.
- A runtime frame type-tag (rejected, child-11 D1).
- The `ceq`/`beq`/`bne.un` form of `field == null` (already correct).
- `Stfld_Ref` (consumer, no dest-to-branch flow).

## Decisions

### D1: Fix at the seeding site -- a `case OpCodeREnum.Ldfld_Ref:` in `TypeSpecializeNeoOpcodes`

**Decision:** add a seeding case for `Ldfld_Ref` so
`registerTypes[op.Register1]` carries a reference type. The existing
Brtrue/Brfalse specialization (`:1423-1434`) and the Ceq/Beq/Bne_Un
specialization (`:956-961`/`:997-1002`) then fire unchanged on the instance-
field operand.

**Rationale:** identical to child-11's `Ldsfeld` seeding rationale and child-16/
21's primitive-producer seeding rationale. It is the root cause (the producer
never seeded the dest, so the consumer's `IsNeoReferenceSlot` test is false);
seeding the producer fixes the whole class -- direct `brtrue`/`brfalse` AND
two-reference `ceq`/`beq`/`bne.un` -- for free. It is Neo-only (the seeding
switch is `#if ENABLE_NEO_MODE`) and touches no shared code.

**Alternatives considered and rejected:**
- *Make the Brtrue/Brfalse case also accept `LocalIsReference`.* REJECTED.
  `frame.LocalIsReference[]` marks only DECLARED ref locals/params; the
  `ldfld.ref` RESULT lives in a TEMP, which `LocalIsReference` leaves false
  (child-11 D1). The `registerTypes` dataflow is the only signal that tracks a
  temp's current value type.
- *Seed at the consumer (teach Brtrue/Brfalse to look back at the producing
  op).* REJECTED. Backward lookups in the specialize pass are fragile (the
  producer may already have been rewritten, e.g. `Ldfld_Ref` -> `Ldfld_Ref_Inline`).
  The producer-side seed is the established idiom.
- *Broaden `IsNeoReferenceSlot` to treat null as a reference.* REJECTED. That
  would mis-specialize genuinely-null-type (fresh-unused) registers and corrupt
  the int-branch path.

### D2: What type to seed -- canonical `ObjectType`, guarded to exclude the F-10 boxed-CLR-struct case

`Ldfld_Ref` is emitted by `GetLdfldCodeForType` (`JITCompiler.cs:3232-3302`) for
NON-primitive, non-IL-VT fields. That covers two cases:
1. A genuine REFERENCE field (string/class/interface/array/delegate) -- dest
   holds an mStack index. A reference encoding.
2. A boxed CLR-STRUCT field of an IL type (the F-10 / `IsClrStructFieldOfIL`
   case, `:3072-3073`) -- the runtime `Ldfld_Ref` arm FLATTENS the boxed struct
   into the dest flat-bytes region (`:3066-3071` comment); the dest does NOT
   hold an mStack index.

For case (1) the dest IS a reference encoding -> `Brtrue_Ref`/`Brfalse_Ref`
(testing `mStack[idx] != null`) is correct. For case (2) the dest is FLAT BYTES
-> `Brtrue_Ref` would dereference them as an index -> wrong/OOB. So the seed
MUST be guarded to exclude F-10.

**Decision:** seed `appdomain.ObjectType` (a canonical reference type; makes
`IsNeoReferenceSlot` true) when the F-10 marker is ABSENT:

```
case OpCodeREnum.Ldfld_Ref:
    // Seed a genuine reference-field load's dest as a reference so a direct
    // Brtrue/Brfalse (and a two-reference Ceq/Beq/Bne_Un) on the instance
    // field specializes to the _Ref variant. The dest holds an mStack index
    // for a genuine reference field. EXCLUDE the F-10 boxed-CLR-struct case
    // (Operand4 == fieldType.GetHashCode(), stamped at JIT body emission,
    // :3072-3073): there the runtime arm flattens the boxed struct into the
    // dest flat-bytes region, so the dest is NOT a reference encoding and
    // Brtrue_Ref must NOT fire.
    if (op.Operand4 == 0)
        SetRegisterType(registerTypes, op.Register1, appdomain.ObjectType);
    break;
```

**Why `ObjectType` (not the exact field type):** the only consumer that matters
for THIS defect (`Brtrue_Ref`/`Brfalse_Ref`/`Ceq_Ref`/`Beq_Ref`/`Bne_Un_Ref`)
keys on `IsNeoReferenceSlot` alone -- ANY reference type satisfies it. Resolving
the exact field type at specialize time is not straightforward (the field
identity for an ILType declaring owner is split across `Operand`=typeHash /
`Operand2`=PrimitiveOffset / `Operand3`=ReferenceOffset, NOT a recoverable
field token; `TypeSpecializeNeoOpcodes` has no `fieldType` in hand). Seeding
`ObjectType` is correct (the dest genuinely holds a reference encoding) and
sufficient. The other `registerTypes` consumers are safe with `ObjectType`:
- `Move` (`:880-884`) propagates it (still a reference).
- `Call` stale-ref clear (`:1291-1310`) keeps/clears it correctly.
- `TryRewriteFieldAccessForInline` keys on the field-access OPERAND (owner)
  being an in-frame VT, NOT on a field-LOAD dest type -- unaffected. (And for a
  chained `a.b.c` where `b` is a reference field, `b`'s dest being `ObjectType`
  correctly keeps the `c` access on the heap `Ldfld_Ref` arm, not inlined.)

**Why the `Operand4 == 0` guard is reliable:** for `Ldfld_Ref`, the ONLY
`Operand4` stamp at body emission (`:3064-3073`) is the F-10 marker
(`IsClrStructFieldOfIL` -> `fieldType.GetHashCode()`, always non-zero for a real
type). Genuine reference fields leave `Operand4 == 0`. (`Ldfld_Value`'s
`Operand4` stamp at `:3065` is a different opcode.) The specialize pass runs
AFTER body emission (child-21 established this), so the marker is already set
when the seeding case runs. The field-access-inline rewriter
(`TryRewriteFieldAccessForInline`) can rewrite `Ldfld_Ref` -> `Ldfld_Ref_Inline`
for an in-frame-VT owner, but that rewriter runs in a separate step and the
inline variant is addressed by O2; the `Ldfld_Ref` seeding case as written
handles the heap-instance-field path (the load-bearing fault).

### D3: Placement + pass-ordering

Place the new case among the other `Ldfld_*` seeding cases (~`JITCompiler.cs:
1089`, immediately after the raw `Ldfld` case). The specialize pass is a single
forward linear pass over the body, mutating `registerTypes` in place; the
producer (`ldfld.ref`) precedes the consumer (`brfalse`) in straight-line code,
so the seed propagates to the branch in the same pass. This is the same
pass-ordering child-11 relies on for `Ldsfeld; brtrue`.

### D4: Probe design

Keep the re-audit probe `TestCases/NeoStepCeqNullInstanceFieldTest.cs`. The
apply worker should:
- KEEP TC1 `(field == null) ? 1 : 0` (ternary -> direct `brfalse`) -- the
  load-bearing FAULT on HEAD. Asserts `r == 1` via `if (r != 1) { 1/0 }`
  (DivideByZero on HEAD).
- KEEP TC4 non-null control `(field = "hello"; (field == null) ? 1 : 0)` --
  guards against a fix that makes every field read as null. Asserts `r == 0`.
- Optionally PRUNE TC2/TC3 (they pass on HEAD; useful only as "ceq form is not
  the bug" documentation). Keeping them is harmless (they assert correct
  values too).
- Optionally ADD TC5 `field1 == field2` two-reference identity (both null ->
  equal): closes the latent `ceq` two-field case for free; assert via
  DivideByZero-on-wrong.

The fault criterion is the child-1/child-2/child-11 FAULT discipline: TC1 MUST
throw on HEAD (DivideByZero from the inverted ternary) and pass after the seed.

## Risks / Trade-offs

- **[registerTypes single-pass, no phi-merge]** (child-11/16/21 gotcha): a
  reused register could in theory carry a stale seed. Mitigation: the
  `ldfld.ref` dest is a straight-line fresh temp (top-of-stack produced
  immediately before the branch, same block) -- reliable for this shape. The
  full NeoStep smoke is the regression net.
- **[Seeding ObjectType could perturb other type-spec decisions]** -- verified
  safe: the other `registerTypes` consumers key on `IsNeoReferenceSlot` or
  `IsValueType`; `ObjectType` is a reference and is exactly what `Ldnull`/
  `Ldstr`/`Ldsfeld`(ref) already seed. No perturbation.
- **[F-10 boxed-CLR-struct field]** -- the `Operand4 == 0` guard excludes it
  (its dest is flat bytes, not a reference encoding). If a future smoke pattern
  brtrue-tests a boxed-CLR-struct field's nullness, that is a separate
  scenario (the encoding genuinely differs); out of scope.
- **[Ldfld_Ref_Inline]** (O2) -- a ref field of an in-frame VT owner is not
  seeded by this case (different opcode). Seed only if a smoke demands it.
- **[Roslyn lowering variance]** -- TC3 (`if (field == null)`) lowered to the
  `ceq` form under this compiler, so it does NOT exercise the bug; TC1
  (ternary) reliably does. The apply worker MUST verify TC1 still lowers to the
  direct `brfalse` form via the JIT dump (the fault evidence depends on it).

## Migration Plan

None. The change is additive seeding under `#if ENABLE_NEO_MODE`. Rollback =
revert the one `case OpCodeREnum.Ldfld_Ref:` hunk; TC1 fails-open (it only
asserts). No persisted artifact, no API, no schema.

## Open Questions

- **O1:** Does any current NeoStep `Brtrue`/`Brfalse` on a primitive/int
  produced by `Ldfld_Ref`... -- N/A. `Ldfld_Ref` is ONLY emitted for
  non-primitive fields (primitive fields use `Ldfld_I4`/`R4`/...). So seeding
  `Ldfld_Ref`'s dest as a reference can NEVER collide with an int-branch path.
  This is the key safety property (stronger than child-16/21, which had to
  guard the seed to primitive-only).
- **O2:** Is `Ldfld_Ref_Inline` (ref field of an in-frame VT owner) reachable
  by a smoke pattern that then brtrue-tests it? If yes, add a sibling
  `case OpCodeREnum.Ldfld_Ref_Inline:` seed (same `ObjectType` logic). The
  apply worker should check the full smoke; seed only if needed.
- **O3:** Should the seed use the exact field type instead of `ObjectType`? Not
  needed for this defect (all consumers key on `IsNeoReferenceSlot`). Keeping
  `ObjectType` avoids the field-token recovery complexity. Only revisit if a
  future consumer needs the exact type.
