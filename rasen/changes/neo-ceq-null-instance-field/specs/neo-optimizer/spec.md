## ADDED Requirements

### Requirement: Neo direct-branch specialization MUST see a seeded reference registerTypes entry for an instance reference field load (`Ldfld_Ref`)

`TypeSpecializeNeoOpcodes` (`JITCompiler.cs`) SHALL seed
`registerTypes[op.Register1]` with a reference type for every instance
REFERENCE field load emitted as `OpCodeREnum.Ldfld_Ref`, so that a DIRECT
`Brtrue`/`Brfalse` (and a two-reference `Ceq`/`Beq`/`Bne_Un`) on that field's
result specializes to the `_Ref` variant. A reference-typed instance field
whose `Ldfld_Ref` dest is NOT seeded MUST NOT reach the runtime as a plain
integer `Brtrue`/`Brfalse` that tests the raw mStack-index int.

The Neo register VM's frame is UNTYPED (there is no per-slot `ObjectType` tag
the way Legacy `ExecuteR` has via `StackObject.ObjectType`). A reference is
encoded as an mStack index in the slot's primitive bytes, and NULL is either a
VALID index pointing to a null mStack entry (instance fields, IL-static fields)
or the `-1` sentinel (`ldnull`, CLR-static fields). The plain `Brtrue`/
`Brfalse` runtime arms test `*(int*)(frameBase + ip->DstOffset) != 0` /
`== 0` (`ILIntepreter.Neo.cs:2063`/`:2071`) -- correct for an int32 truth
value but WRONG for a reference: a null instance field is a NON-ZERO mStack
index -> reads truthy -> null mis-classified as non-null.

Because the frame carries no runtime type tag, the ref-vs-int distinction for
a branch condition MUST be made at JIT time. The `Brtrue`/`Brfalse` case in
`TypeSpecializeNeoOpcodes` (`JITCompiler.cs:1423-1434`) rewrites `op.Code` ->
`Brtrue_Ref`/`Brfalse_Ref` when
`IsNeoReferenceSlot(GetRegisterType(registerTypes, op.Register1))`, whose
runtime arm tests `mStack[idx] != null` (Legacy `mStack[v] != null` parity,
child-11). The `Ceq`/`Beq`/`Bne_Un` cases (`:956-961` / `:997-1002`) rewrite
to their `_Ref` variants when EITHER operand register is a reference slot
(child-12).

Child-11 seeded the STATIC-field producer (`Ldsfeld`, `:1395-1409`) but
explicitly DEFERRED the instance-field producer. There is NO
`case OpCodeREnum.Ldfld_Ref:` in the seeding switch today. An instance
reference field load therefore leaves `registerTypes[dest]` unseeded, a
following DIRECT `brtrue`/`brfalse` stays the plain integer branch, and -- per
the JIT dump on HEAD (`0aa3b4b7`) -- Roslyn's ternary lowering of
`(field == null) ? a : b` emits exactly `ldfld.ref; brfalse.s` (no `ceq`),
so the null field (valid index `N != 0`) reads truthy, the branch is not
taken, the ternary inverts, and the field stays null / downstream code NREs
(`Neo callvirt this is null`).

This requirement pins a `case OpCodeREnum.Ldfld_Ref:` seeding that sets
`registerTypes[op.Register1]` to a reference type. The seed is guarded to the
GENUINE reference-field case (`op.Operand4 == 0`): the F-10
boxed-CLR-struct-field-of-IL case stamps `op.Operand4 = fieldType.GetHashCode()`
at JIT body emission (`JITCompiler.cs:3072-3073`), and its runtime arm FLATTENS
the boxed struct into the dest flat-bytes region (the dest is NOT a reference
encoding) -- so it MUST be excluded to avoid `Brtrue_Ref` dereferencing flat
bytes as an index. The seed MUST use a reference type (e.g.
`appdomain.ObjectType`); the branch/compare specializations key on
`IsNeoReferenceSlot` alone, so a canonical reference type is sufficient and
correct. (`Ldfld_Ref` is ONLY emitted for non-primitive fields -- primitive
fields use `Ldfld_I4`/`R4`/`I8`/... -- so seeding its dest as a reference can
NEVER collide with an int-branch path; this is the key safety property.)

This is the sibling of the child-11 `Ldsfeld` seeding requirement (the
STATIC-field form of this same gap) and of the child-16/21 primitive-producer
seeding requirements. It is Neo-only: the seeding runs inside
`TypeSpecializeNeoOpcodes`, which is `#if ENABLE_NEO_MODE`. Legacy `ExecuteR`
is the REFERENCE and is unaffected -- Legacy's `Brtrue`/`Brfalse`/`Ceq` arms
re-dispatch on `StackObject.ObjectType`, so Legacy was never dependent on the
JIT-time seed.

#### Scenario: a null instance reference field compared via a direct ternary branch reads as null
- **WHEN** a Neo method executes `int r = (d.RefField == null) ? 1 : 0;` where
  `d` is a `new` IL instance whose `string RefField` is null, and Roslyn
  lowers the ternary to a DIRECT `ldfld.ref; brfalse` (NO `ceq`)
- **THEN** `r` MUST equal `1` (the null field is correctly classified), and
  the JIT dump MUST show the branch specialized to `brfalse.ref` /
  `Brfalse_Ref` (NOT plain `brfalse.s`)
- **AND** on HEAD with the fix stashed the probe MUST FAULT (DivideByZero on
  `r != 1`), because the plain `brfalse.s` tests the field's non-zero mStack
  index and does not branch -- CONFIRMED at HEAD (`0aa3b4b7`): JIT emits
  `ldfld.ref r7, r0, ...` then plain `brfalse.s r7, 5`, and the runtime
  computes `r = 0` (field null mis-read as non-null)

#### Scenario: a non-null instance reference field compared via a direct ternary branch reads as non-null
- **WHEN** a Neo method executes `int r = (d.RefField == null) ? 1 : 0;` after
  `d.RefField = "hello"` (a non-null reference)
- **THEN** `r` MUST equal `0`, proving the fix did not make every field read as
  null, and the JIT dump MUST show `brfalse.ref` (or `brtrue.ref`) -- the same
  specialization as the null case, resolving `mStack[idx] != null` -> truthy

#### Scenario: the ceq form of instance-field null comparison stays correct (no regression)
- **WHEN** a Neo method executes `bool b = (d.RefField == null);` (Roslyn
  lowers to `ldfld.ref; ldnull; ceq`) on a null field
- **THEN** `b` MUST be `true` and the JIT dump MUST show `ceq.ref` (the
  `Ceq_Ref` specialization already fires on HEAD via the `ldnull` operand's
  `ObjectType` seed -- this scenario MUST NOT regress)

#### Scenario: two instance reference fields compared by identity specialize via Ceq_Ref
- **WHEN** a Neo method executes `bool b = (d.A == d.B)` where `A` and `B` are
  both instance reference fields (loaded via two `ldfld.ref`; NO `ldnull`)
- **THEN** the JIT dump MUST show `ceq.ref` (BOTH operands are now seeded
  references after the `Ldfld_Ref` seed) -- on HEAD this stayed plain `ceq`
  (both operands unseeded -> `Ceq_Ref` no-oped) and compared raw mStack indices
- **AND** for two distinct non-null instances the comparison MUST yield
  `false`; for the same instance (alias) it MUST yield `true`

#### Scenario: the F-10 boxed-CLR-struct field is NOT seeded as a reference
- **WHEN** an IL instance has a CLR-struct field (the F-10 / `IsClrStructField
  OfIL` case, where `Ldfld_Ref` runs with `op.Operand4 != 0`) and a branch
  tests its dest
- **THEN** the dest MUST be left UNSEEDED (the `case Ldfld_Ref:` seed is
  guarded by `op.Operand4 == 0`), so `Brtrue`/`Brfalse` does NOT specialize to
  the `_Ref` variant -- the runtime `Ldfld_Ref` arm flattens the boxed struct
  into the dest flat-bytes region (the dest is NOT a reference encoding), and
  `Brtrue_Ref` MUST NOT dereference those flat bytes as an mStack index

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and runs the Legacy
  register VM under the NeoStep filter
- **THEN** the Legacy NeoStep-filter smoke MUST show the SAME pre-existing
  failure set with and without this change (stash-toggle proof), because the
  new `case Ldfld_Ref:` seeding is inside `TypeSpecializeNeoOpcodes`, which
  compiles out under plain `Debug`, and Legacy's `Brtrue`/`Brfalse` arms
  re-dispatch on `StackObject.ObjectType` regardless

#### Scenario: NeoStep regression smoke stays green
- **WHEN** the seeding is applied (`Debug_Neo`) and the full NeoStep smoke is run
- **THEN** the NeoStep smoke MUST stay green at its current baseline (ZERO
  regressions; the change only ADDS a correct reference seed that makes the
  direct-branch specialization fire where it already should have)
