## ADDED Requirements

### Requirement: Neo branch condition tests a reference operand by its referenced object's nullness

The Neo register VM's compact `byte*` frame is untyped: a reference value is
stored in a slot's primitive bytes as an mStack index, and a **null** reference
is encoded as a non-zero mStack index (IL-static `Ldsfld`, which does
`mStack.Add(null)` then stores the index) or as the sentinel `-1` (CLR-static
`Ldsfld`; `Ldnull`). A `Brtrue`/`Brfalse` whose condition operand is a reference
type MUST test the **referenced object's** nullness (`mStack[idx] != null`), not
the raw mStack-index `int32`. Testing the raw index `!= 0` misclassifies a null
reference as truthy (a non-zero index or `-1` is non-zero), which silently skips
the canonical C# lazy-init / delegate-cache pattern `if (x == null) { x = new
...; }` (Roslyn lowers `x == null` on a reference to a direct `ldsfld x; brtrue
skipInit` with no `ceq`), leaving `x` null and NRE-ing downstream.

Because the Neo frame carries no per-slot runtime type tag (unlike Legacy
`StackObject.ObjectType`), the reference-vs-primitive distinction SHALL be made
at JIT time: `TypeSpecializeNeoOpcodes` MUST rewrite a `Brtrue`/`Brfalse` whose
condition register's tracked type is a reference slot (`IsNeoReferenceSlot` —
non-primitive and non-value-type) to the `Brtrue_Ref`/`Brfalse_Ref` variant. A
`ceq`-normalized condition (whose dest is `IntType`, a real 0/1 `int32`) MUST
remain on the plain `Brtrue`/`Brfalse`. To make the rewrite fire for the
`ldsfld ref; brtrue` chain, the JIT MUST seed the `Ldsfld` destination register
type from the static field's type.

The `Stsfld`/`Ldsfld` opcodes are not offset-lowered by `LowerNeoOffsets`, so
their IL-static arms MUST resolve the operand register's byte offset via the
frame `LocalInfos` (exactly as the CLR-static arms do) before reading or writing
the slot — otherwise the `Brtrue_Ref` deref arm reads garbage bytes and crashes.

#### Scenario: Lazy-init of a null IL-static reference field initializes the field
- **WHEN** IL code performs `if (x == null) { x = new T(); }` where `x` is a
  null IL-static reference field (Roslyn emits `ldsfld x; brtrue skipInit;
  newobj; stsfld x; skipInit:`), and the Neo JIT type-specializes the `brtrue`
  to `Brtrue_Ref` because the `ldsfld` dest is a reference type
- **THEN** the `Brtrue_Ref` arm MUST evaluate `mStack[idx] != null` as **false**
  for the null field, MUST take the init path, and `x` MUST be non-null
  afterward (no downstream NRE).

#### Scenario: Non-null reference branch condition is truthy
- **WHEN** a `Brtrue_Ref` operand holds a non-null reference (a valid mStack
  index pointing at a non-null object)
- **THEN** the arm MUST evaluate `idx >= 0 && mStack[idx] != null` as **true**
  and take the branch (parity with Legacy `mStack[reg1->Value] != null`).

#### Scenario: ceq-normalized reference equality stays on the integer branch
- **WHEN** IL code performs `ld x; ld y; ceq; brfalse` (the `ceq` form of a
  reference equality test), where `ceq`'s dest is `IntType`
- **THEN** the JIT MUST NOT rewrite this `brfalse` to `Brfalse_Ref`; it MUST
  remain a plain `Brfalse` testing the `ceq` result `int32` (0/1).

#### Scenario: Delegate-cache lazy-init creates and caches the delegate
- **WHEN** a compiler-generated delegate cache `if (cached == null) { cached =
  new Delegate(...); }` (an IL-static reference field read via `ldsfld` then a
  direct `brtrue`/`brfalse`) executes, on the first and second invocation
- **THEN** the cached delegate MUST be created on the first invocation and
  reused on the second (the null cache MUST test falsey so the init runs), and
  `NeoStep20_Tr2` / `NeoStep20_Tr5` MUST pass.

#### Scenario: IL-static Stsfld/Ldsfld operand resolved via LocalInfos
- **WHEN** an IL-static `Stsfld`/`Ldsfld` reads or writes its operand register
- **THEN** the arm MUST resolve the register's byte offset from the frame
  `LocalInfos` (not read the raw `DstOffset` register index as a byte offset),
  so a following `Brtrue_Ref`/`Brfalse_Ref` dereferences a valid mStack index
  rather than garbage frame bytes.
