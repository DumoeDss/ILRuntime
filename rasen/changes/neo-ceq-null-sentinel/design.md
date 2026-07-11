## Context

Neo (`ExecuteNeo`) runs on a compact `byte*` frame whose slots are **untyped**:
a slot holds either flat primitive bytes, an in-frame value type, or a
**reference** encoded as an mStack index in the slot's primitive bytes. There is
no per-slot type tag (Legacy `ExecuteR` has `StackObject.ObjectType`; Neo does
not). This is the same core constraint child-11 operated under for the branch
condition, and it is the core reason the equality-compare bug exists and the
core constraint on the fix.

`Ceq` (`ILIntepreter.Neo.cs:1968`), `Beq` (`:2137`), and `Bne_Un` (`:2144`)
currently compare two operand slots as raw `int32`s. That is correct for
primitive compares but wrong for a **reference**: a reference is stored as an
mStack index, and `null` is encoded as a non-zero index (IL-static `Ldsfld` does
`mStack.Add(null)` then stores the index, `Neo.cs:4302-4304`) or `-1` (CLR-static
`Ldsfld` `:4350`; `Ldnull` `:1526`). So the compare tests two index/sentinel
integers, never the referenced objects.

This breaks the **ceq form** of the C# reference-equality / lazy-init pattern.
When the comparison result is materialized into a `bool` local/field/return, or
is a two-reference identity test, Roslyn lowers `x == null` / `x == y` via
`ceq`/`beq`/`bne.un` rather than a direct `brtrue`. With the bug, for a null
IL-static `x` (index `N`) compared to `ldnull` (`-1`): `N != -1` -> `ceq` yields
`0` -> `x == null` is FALSE -> the `if (x == null) { x = init; }` body is
skipped -> `x` stays null -> downstream NRE. Actively failing:
`TestStaticFieldInstance` (`TestA.Instance` lazy-init), `RegisterVMTest04`
(`Bug778.instance` + delegate wiring).

Legacy handles this via `reg->ObjectType` (`ILIntepreter.Register.cs`):
- `Ceq` (`:4557-4603`): same-type -> `Object`=`mStack[a]==mStack[b]`,
  `Null`=true; mixed Object/Null -> `mStack[v]==null`.
- `Beq` (`:2090-2136`): same structure (branches on the result).
- `Bne_Un` (`:2172-2220`): the inverse.

Neo has no such runtime tag, so the distinction must be made at **JIT time** --
exactly as child-11 did for `Brtrue`/`Brfalse`.

## Goals / Non-Goals

**Goals:**
- A reference-typed `Ceq`/`Beq`/`Bne_Un` compares the referenced objects'
  identity/nullness (`R(a) == R(b)`), not the raw mStack-index `int32`s. The
  `if(x==null){init}` lazy-init and the `x == y` reference-identity patterns
  work in their ceq/beq/bne.un lowering.
- The three reference-equality opcodes are handled uniformly (ceq, beq, bne.un).
  `Cgt_Un` (the `ldnull; cgt.un` "not null" idiom) is already handled by the
  Step-15 runtime patch and is NOT touched.
- Legacy-neutral; Neo-gated.

**Non-Goals:**
- A general runtime type-tag for the Neo frame (architecturally rejected; the
  Neo design is deliberately untyped and type-specializes at JIT time -- child-11
  D1 settled this).
- Reference *ordering* compares (`Cgt`/`Clt`/`Blt`/`Bgt`/... on references) --
  not valid CIL (references support only == / != plus the `cgt.un` null idiom);
  out of scope unless a smoke test regresses.
- The direct-`brtrue` form of `x == null` (already fixed by child-11's
  `Brtrue_Ref`/`Brfalse_Ref`). This child closes the ceq/beq/bne.un form only.

## Decisions

### D1: Specialized opcodes (NOT a runtime check) -- mirror child-11

The Neo frame is untyped, so the ceq arm CANNOT distinguish a reference operand
from an int operand at runtime. child-11 established (D1) that the only sound
signal is the `registerTypes[]` dataflow in `TypeSpecializeNeoOpcodes`
(`JITCompiler.cs:777`), already used by `Move` (`:852`) and by child-11's
`Brtrue`/`Brfalse` -> `_Ref` rewrite (`:1271-1282`).

**Decision:** add `Ceq_Ref` / `Beq_Ref` / `Bne_Un_Ref` `OpCodeREnum` variants
and rewrite to them in `TypeSpecializeNeoOpcodes` when an operand is a reference
slot. This is the established Neo idiom (mirrors `Brtrue`->`Brtrue_Ref`,
`Ldfld`->`Ldfld_Ref`, `Stfld`->`Stfld_Ref`, `Ldind`->`Ldind_Ref`).

**Alternatives considered:**
- *Runtime type-tag check in the ceq arm.* REJECTED. There is no per-slot tag.
  An unconditional resolve is unsound: `ceq int 5, int 5` would treat `5` as an
  mStack index -> OOB/wrong object. The specialization is required.
- *Reuse `Cgt_Un`'s Step-15 runtime sentinel trick for `Ceq`.* REJECTED.
  `Cgt_Un`'s trick works because its ONLY reference use is the `ldnull; cgt.un`
  "not null" idiom (one operand is always `-1`), so a sentinel special-case
  suffices. `Ceq`/`Beq`/`Bne_Un` must handle two arbitrary references (identity)
  AND null, so they must genuinely resolve both operands -- the specialized arm
  is the clean path.

### D2: Detection -- either operand is a reference slot

`Ceq` operands are `Register2` (->`SrcOffset`) and `Register3` (->`OperandOffset`),
dest `Register1` (->`DstOffset`) (Legacy `Ceq` `Register2`/`Register3` src,
`Register1` dest). `Beq`/`Bne_Un` operands are `Register1` (->`DstOffset`) and
`Register2` (->`SrcOffset`) (Legacy `Beq`/`Bne_Un` `Register1`/`Register2`).

**Decision:** rewrite when EITHER operand is a reference slot:
- `Ceq`: `IsNeoReferenceSlot(rt[Register2]) || IsNeoReferenceSlot(rt[Register3])`.
- `Beq`/`Bne_Un`: `IsNeoReferenceSlot(rt[Register1]) || IsNeoReferenceSlot(rt[Register2])`.

Rationale: in valid CIL a reference equality compare has both operands
reference-typed (ref-vs-ref or ref-vs-`ldnull`; `Ldnull` seeds `ObjectType` at
`:843-845`, and child-11 seeds `Ldsfeld` at `:1243-1258`). "Either operand is
ref" is therefore equivalent to "this is a reference compare" for valid IL, and
is more robust against the single-pass `registerTypes` imprecision than
requiring both (if one operand's type was clobbered to stale-null at a join,
"either" still does the right reference compare). Valid IL never mixes ref and
int in `ceq`/`beq`/`bne.un`, so there is no false-positive-on-int risk for
well-formed code.

The specialization is inserted AFTER the existing typed-opcode rewrite in each
case (so `Ceq_I8`/`Ceq_R4`/`Ceq_R8`/`Beq_*`/`Bne_Un_*` long/float variants still
win for primitive operands; only a plain `Ceq`/`Beq`/`Bne_Un` whose operand
`InferPrimTag` fell back to `I4` because it is a reference is upgraded):

- In the `Ceq`/`Cgt`/`Cgt_Un`/`Clt`/`Clt_Un` case (`:902-908`): after
  `op.Code = GetTypedCompareOpcode(...)` + `SetRegisterType(dest, IntType)`, if
  `op.Code == Ceq` AND (ref-operand test) -> `op.Code = Ceq_Ref`. (Only `Ceq`;
  `Cgt`/`Clt` reference ordering is invalid CIL; `Cgt_Un` already handled.)
- In the conditional-branch case (`:910-930`): after
  `op.Code = GetTypedBranchOpcode(NormalizeBranchOpcode(op.Code), ...)`, if
  (`op.Code == Beq` || `op.Code == Bne_Un`) AND (ref-operand test) ->
  `Beq_Ref` / `Bne_Un_Ref`. (`Blt`/`Bgt`/... reference ordering is invalid CIL.)

The `Ceq_Ref` dest stays `IntType` (the arm writes a real 0/1 `int32`), so a
following `brtrue`/`brfalse` on the result correctly stays a plain
`Brtrue`/`Brfalse` (child-11 D3 parity) -- no interaction with `Brtrue_Ref`.

### D3: Runtime resolve-and-compare (one resolve helper, all encodings)

```csharp
// resolve a Neo reference slot int to its referenced object (or null)
object R(int v) => v >= 0 ? mStack[v] : null;
```
- `Ldnull`/CLR-static null (`-1`) -> `null`.
- IL-static null (valid index `N`, `mStack[N] == null`) -> `null`.
- a real object (index `N`, `mStack[N] == obj`) -> `obj`.

Then:
- `Ceq_Ref`: `*(int*)DstOffset = (R(a) == R(b)) ? 1 : 0;`  (a = `SrcOffset`, b = `OperandOffset`)
- `Beq_Ref`: branch when `R(a) == R(b)`  (a = `DstOffset`, b = `SrcOffset`)
- `Bne_Un_Ref`: branch when `R(a) != R(b)`

`R(a) == R(b)` is C# `object ==` reference equality: `null == null` -> true
(Legacy `Null`/`Null`), `obj == null` -> false (Legacy Object/Null), `obj1 ==
obj2` -> identity (Legacy `Object`/`Object` `mStack[a]==mStack[b]`). Note the
same object added to mStack at two indices still compares equal (both resolve to
the same instance) -- correct reference-identity semantics, matching Legacy.

No bounds guard (Legacy parity; the specialization guarantees the slot holds a
reference encoding). The offsets are real byte offsets because `Ceq` is lowered
by `LowerR1R2R3` and `Beq`/`Bne_Un` by `LowerR1R2` (see D4).

### D4: LowerNeoOffsets + opcode-list bookkeeping

`Ceq_Ref` joins the R1R2R3 case-list (`Optimizer.Neo.cs:459-503`, dest + 2 src
offsets, same shape as `Ceq`). `Beq_Ref`/`Bne_Un_Ref` join the R1R2 case-list
(`:654-695`, 2 operand offsets, same shape as `Beq`/`Bne_Un`).

`Optimizer.Utils.cs` categorization lists: child-11 added `Brtrue_Ref`/
`Brfalse_Ref` to 5 lists (the operand-count / branch-classification /
roundtrip-whitelist tables). Audit every list that currently contains
`Ceq`/`Beq`/`Bne_Un` and add the `_Ref` variant where the list's semantics apply
to a reference equality compare. **Do NOT** extend `SupportIntemediateValue`
(`:17`) -- a reference operand is never a foldable immediate constant (the
reference is a runtime mStack index), so the `_Ref` variants must NOT be
considered foldable; leaving them to `default: return false` is correct. Also
do NOT add an entry to the typed->base normalize function (`:1594`/`:1601`/
`:1602`) -- `_Ref` is itself a base reference variant, not a typed-primitive
variant.

### D5: The Call-case stale-ref clear (child-11) does NOT need extension

child-11's load-bearing regression repair (`JITCompiler.cs:1158-1177`) clears a
stale **reference** type on a `Call`'s dest register when the call does NOT
return a reference (e.g. a `bool`/`int` `op_Inequality` result). This repair
operates on the **producing Call**, not on any specific consumer, so it already
protects ALL downstream consumers of that register -- `brtrue` (child-11's case)
AND `ceq`/`beq`/`bne.un` (this child). **No extension is needed.** The only
known producer that failed to clear a stale ref type was `Call`; the other
producers (`Move`, `Ldc*`, `Ldfld*`, `Ldsfeld`, `Ldnull`) all `SetRegisterType`.
The ceq specialization's two-operand surface (vs brtrue's one) does not change
this: both ceq operands are the straight-line top-of-stack produced immediately
before the compare, so the linear `registerTypes` pass reliably types them. The
full NeoStep smoke re-verify (332 -> 332 + probes) is the safety net for any
residual stale-ref mis-specialization.

### D6: New enum members appended; dispatch guard already bounds-checks

`Ceq_Ref`/`Beq_Ref`/`Bne_Un_Ref` are appended to `OpCodeREnum` (implicit-
numbered; no values shift). Child-1's `ExecuteNeo` dispatch guard already
recognizes the full named range, so the new members dispatch correctly.

## Risks / Trade-offs

- **[registerTypes dataflow imprecision]** `registerTypes` is a single linear
  pass; at block joins a reused register could carry a stale type, mis-classifying
  a `Ceq`/`Beq`/`Bne_Un` operand (false-positive `_Ref` -> resolve a non-index
  int; false-negative -> keep the int compare on a ref -> null misclassified).
  -> **Mitigation:** both equality operands are the straight-line top-of-stack
  produced immediately before the compare (same block), so the linear pass is
  reliable for this shape. The full NeoStep smoke (332) + dedicated probes is the
  safety net. The ceq specialization has a TWO-operand surface (vs brtrue's one),
  slightly widening the stale-ref risk; the child-11 Call-case clear (D5) covers
  the one known stale-ref producer, and any residual surfaces as a smoke failure.
- **[ceq probe must genuinely FAULT on HEAD]** Unlike child-11's direct-brtrue
  probes (which the zero-initialized Neo frame accidentally passed on HEAD), the
  ceq-form lazy-init is independently broken post-child-11: `ldsfld` (F3-fixed)
  reads the real null reference, `ceq` then mis-compares it -> init skipped -> NRE.
  -> **Mitigation:** design the probe to force the ceq materialization (a `bool`
  local, or two-reference identity) and CONFIRM via a stash-toggle that it NREs
  on HEAD before claiming the fix; if Roslyn folds to the direct-brtrue form,
  restructure (verify via the JIT `OUTPUT_JIT_RESULT` dump that `Ceq` is emitted).
- **[Beq/Bne_Un may not be exercised by the smoke]** Roslyn often lowers `x != y`
  via `cgt.un` (already handled) rather than `bne.un`. The `Beq_Ref`/`Bne_Un_Ref`
  arms are the symmetric siblings of `Ceq_Ref` (trivially correct, identical
  resolve logic) and are implemented for completeness/parity; a dedicated
  two-reference `beq`/`bne.un` probe confirms at least one path. If no smoke
  pattern exercises them, that is acceptable (they cannot regress the int path;
  they only add a reference-resolve arm).
- **[Other ref-producer seeding gaps]** If some reference producer does not seed
  `registerTypes` (so a `ceq` on its result stays the plain int compare), the
  ceq-form null test on that producer would still misfire. -> **Mitigation:**
  `Ldnull` and `Ldsfeld` (the dominant feeders) are already seeded; the probes
  target those paths. Widen seeding only if a smoke pattern demands it.

## Open Questions

- Does any current NeoStep `Ceq`/`Beq`/`Bne_Un` on primitives rely on the raw-int
  behavior? No -- the specialization only upgrades reference-operand compares;
  primitive compares keep their typed opcodes unchanged. The full smoke re-verify
  answers this empirically; none expected.
- Whether a `beq`/`bne.un` reference form is reachable from the NeoStep smoke at
  all (Roslyn prefers `ceq`/`cgt.un`). The arms ship regardless (completeness);
  the ceq-form probe is the load-bearing fault evidence.
