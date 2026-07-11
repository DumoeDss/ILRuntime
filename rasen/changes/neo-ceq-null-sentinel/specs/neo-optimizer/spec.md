## ADDED Requirements

### Requirement: Neo reference equality compares (ceq/beq/bne.un) test the referenced objects, not the raw mStack-index ints

The Neo register VM's compact `byte*` frame is untyped: a reference value is
stored in a slot's primitive bytes as an mStack index, and a **null** reference
is encoded as a non-zero mStack index (IL-static `Ldsfld`, which does
`mStack.Add(null)` then stores the index) or as the sentinel `-1` (CLR-static
`Ldsfld`; `Ldnull`). A `Ceq`/`Beq`/`Bne_Un` whose operand is a reference type
MUST compare the **referenced objects'** identity/nullness
(`R(a) == R(b)`, where `R(v) = v >= 0 ? mStack[v] : null`), not the raw
mStack-index `int32`s. Comparing the raw index integers mis-handles the null
sentinel: for a null IL-static reference (index `N`) compared to `ldnull` (`-1`),
`N != -1` so `Ceq` yields `0` ("not equal"), making the C# condition
`x == null` evaluate FALSE even when `x` IS null. This silently skips the
canonical lazy-init pattern `if (x == null) { x = new ...; }` in its **ceq
lowering** (`ldsfld x; ldnull; ceq; brfalse skipInit` -- the form Roslyn emits
when the comparison result is materialized into a `bool` local/field/return, or
for two-reference identity), leaving `x` null and NRE-ing downstream.

Because the Neo frame carries no per-slot runtime type tag (unlike Legacy
`StackObject.ObjectType`), the reference-vs-primitive distinction SHALL be made
at JIT time: `TypeSpecializeNeoOpcodes` MUST rewrite a `Ceq`/`Beq`/`Bne_Un`
whose operand register's tracked type is a reference slot (`IsNeoReferenceSlot`
-- non-primitive and non-value-type) to the `Ceq_Ref`/`Beq_Ref`/`Bne_Un_Ref`
variant. A primitive compare (whose operand `InferPrimTag` is in
{I4, I8, U8, R4, R8}) MUST remain on the plain/typed opcode. The reference
producers `Ldnull` (seeds `ObjectType`) and `Ldsfeld` (child-11 dest-type
seeding) MUST remain seeded so the rewrite fires for the `ldsfld ref; ldnull;
ceq` chain. The `Ceq_Ref` dest MUST be `IntType` (a real 0/1 `int32`) so a
following `brtrue`/`brfalse` on the result stays a plain branch (not
`Brtrue_Ref`). `Cgt_Un` (the `ldnull; cgt.un` "not null" idiom) is handled by
its existing Step-15 runtime arm and is NOT affected.

The `_Ref` variants MUST be offset-lowered by `LowerNeoOffsets` (`Ceq_Ref` via
the R1R2R3 case-list; `Beq_Ref`/`Bne_Un_Ref` via the R1R2 case-list) so their
runtime arms read real byte offsets.

#### Scenario: ceq-form lazy-init of a null IL-static reference initializes the field
- **WHEN** IL code performs `if (x == null) { x = new T(); }` where `x` is a
  null IL-static reference field, lowered via the ceq form (`ldsfld x; ldnull;
  ceq; stloc b; ldloc b; brfalse skipInit; newobj; stsfld x; skipInit:`) because
  the comparison result is materialized into a `bool`, and the Neo JIT
  type-specializes the `ceq` to `Ceq_Ref` because the `ldsfld` dest is a
  reference type
- **THEN** the `Ceq_Ref` arm MUST evaluate `R(N) == R(-1)` as **true** for the
  null field (`R(N) = mStack[N] = null`, `R(-1) = null`, `null == null`), the
  `brfalse` MUST NOT skip the init, and `x` MUST be non-null afterward (no
  downstream NRE).

#### Scenario: ceq-form non-null reference compares not-equal to null
- **WHEN** a `Ceq_Ref` compares a non-null reference (a valid mStack index
  pointing at a non-null object) against `ldnull` (`-1`)
- **THEN** the arm MUST evaluate `R(obj) == R(-1)` as `obj == null` -> **false**
  (writes `0`), so `x == null` is correctly FALSE for a non-null `x`.

#### Scenario: two-reference identity equality via ceq/beq
- **WHEN** IL code performs `x == y` / `x != y` on two reference operands
  (lowered via `ceq`/`beq`/`bne.un`), where both operands are reference slots
- **THEN** the `_Ref` arm MUST compare the resolved objects' identity
  (`R(a) == R(b)`): the same instance at two different mStack indices compares
  EQUAL; two distinct instances compare NOT EQUAL; two nulls compare EQUAL
  (parity with Legacy `mStack[reg1->Value] == mStack[reg2->Value]`).

#### Scenario: primitive equality compare is unchanged
- **WHEN** a `Ceq`/`Beq`/`Bne_Un` compares primitive operands (int/long/float,
  whose `InferPrimTag` is in {I4, I8, U8, R4, R8})
- **THEN** the JIT MUST NOT rewrite it to a `_Ref` variant; it MUST keep the
  plain/typed opcode and the runtime MUST compare the raw primitive bytes (no
  mStack dereference).

#### Scenario: ceq result feeding a branch stays a plain branch
- **WHEN** a `Ceq_Ref` writes its 0/1 result to a dest register consumed by a
  following `brtrue`/`brfalse`
- **THEN** the dest register's tracked type is `IntType`, so the JIT MUST keep
  the branch a plain `Brtrue`/`Brfalse` (testing the 0/1 `int32`); it MUST NOT
  rewrite it to `Brtrue_Ref`/`Brfalse_Ref` (which would wrongly dereference the
  0/1 int as an mStack index).
