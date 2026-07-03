# neo-type-checks

Runtime type-check instructions (`isinst` / `castclass`) for IL code executed by
the Neo register VM (`ENABLE_NEO_MODE`). `isinst` backs the C# `is` and `as`
operators; `castclass` backs explicit parenthesized casts. This capability is
implemented in `ExecuteNeo` (`ILIntepreter.Neo.cs`) plus the Neo offset-lowering
pass (`Optimizer.Neo.cs` `LowerNeoOffsets`); it does NOT modify Legacy
(`ExecuteR`).

This capability is distinct from `neo-dispatch` (vtable/interface selection),
`neo-boxing` (value-type boxing, which produces the references that type checks
consume), and `neo-exceptions` (catch dispatch uses the shared handler engine,
not `isinst` -- see "Non-goals"). Type checking reuses the assignability logic
already present on `ILType.CanAssignTo` (which walks `BaseType` and the Step 11
`Implements` interface table) and the CLR `Type.IsAssignableFrom` rules.

## Non-goals

Out of scope: the compile-time `box T; isinst U` peephole fusion and a generic-
parameter patch table (`PatchKind.IsinstResult`) -- neither exists in this
codebase today; the runtime arms are sufficient for correctness on validated
type operands. Also out of scope: IL-typed catch dispatch (a `neo-exceptions`
concern).

## Requirements

### Requirement: isinst opcode executes under Neo

The `isinst` `ExecuteNeo` arm SHALL resolve the target type from the
instruction's type-token operand via `AppDomain.GetType(ip->Operand)`, read the
source object from the instruction's register-2 reference slot (the mStack index
stored at `frameBase + ip->SrcOffset`), and write the result back into the
register-1 reference slot in place. A `null` source object (ref index `-1`) SHALL
produce a `null` result. For a non-null source, the arm SHALL test assignability
and keep the original reference on success or write `null` on failure:
- `ILTypeInstance` source -> `ILTypeInstance.CanAssignTo(type)`.
- CLR object source (including boxed value types) -> `type.TypeForCLR.IsAssignableFrom(obj.GetType())`.
`isinst` SHALL NOT throw on a type mismatch; it SHALL write `null`.

#### Scenario: An inheritance-chain `is` check returns true
- **WHEN** an IL method under `ENABLE_NEO_MODE` holds a `Derived` instance in a
  `Base`-typed local and evaluates `local is Derived`
- **THEN** the result is the non-null reference (the C#-emitted null-check then
  yields `true`), not `null` and not a `NotImplementedException`.

#### Scenario: An unrelated `is` check returns false (null)
- **WHEN** an IL method under `ENABLE_NEO_MODE` evaluates `obj is UnrelatedType`
  where the runtime type does not assign to the target
- **THEN** `isinst` writes `null` (so the emitted null-check yields `false`),
  without throwing.

#### Scenario: A null operand checks as not-the-type
- **WHEN** an IL method under `ENABLE_NEO_MODE` evaluates `null is T` (or
  `null as T`)
- **THEN** `isinst` produces `null` (yielding `false` / `null` respectively),
  without throwing.

### Requirement: isinst matches interfaces and boxed value types

For a non-null source, the `isinst` arm SHALL report a successful match when the
target is an interface the source's type implements (resolved via
`CanAssignTo`'s `Implements` walk for `ILTypeInstance`, or CLR
`IsAssignableFrom` for CLR objects) and when the source is a boxed value type
(produced by the `Box` opcode) whose underlying type assigns to the target.

#### Scenario: `as Interface` returns the instance when implemented
- **WHEN** an IL method under `ENABLE_NEO_MODE` evaluates `obj as IFoo` on an
  instance whose type implements `IFoo`
- **THEN** the result is the same non-null reference.

#### Scenario: `as Interface` returns null when not implemented
- **WHEN** an IL method evaluates `obj as IFoo` on an instance whose type does
  not implement `IFoo`
- **THEN** the result is `null`, without throwing.

#### Scenario: A boxed value type checks as its type
- **WHEN** an IL method under `ENABLE_NEO_MODE` boxes a value type `V` (via the
  `Box` opcode) and then evaluates `boxed is V`
- **THEN** the result is non-null (the boxed reference), proving the boxed
  object flows through the assignability check.

### Requirement: castclass opcode succeeds or throws InvalidCastException

The `castclass` `ExecuteNeo` arm SHALL use the same type resolution, source-read,
and assignability dispatch as `isinst`, with two differences: on a failed
assignability check it SHALL throw `System.InvalidCastException` (message naming
the source and target types), and a `null` source SHALL pass through as `null`
(castclass of null is permitted). On success the arm SHALL write the original
reference into the result slot.

#### Scenario: A successful cast round-trips the reference
- **WHEN** an IL method under `ENABLE_NEO_MODE` casts a `Base`-typed local
  holding a `Derived` instance to `Derived` and reads a `Derived` field
- **THEN** the cast yields the same reference and the field read returns the
  expected value (no `InvalidCastException`).

#### Scenario: A failed cast throws InvalidCastException
- **WHEN** an IL method under `ENABLE_NEO_MODE` evaluates
  `(UnrelatedType)someRef` where the runtime type does not assign to the target
- **THEN** `castclass` throws `System.InvalidCastException` (which the method may
  catch), rather than writing `null` or a `NotImplementedException`.

### Requirement: Neo offset-lowering stamps isinst/castclass ref slots

The Neo offset-lowering pass (`LowerNeoOffsets`) SHALL stamp `DstOffset`,
`SrcOffset`, `Operand3` (destination reference offset), and `Operand4` (source
reference offset) for `Isinst` and `Castclass` using the same scheme already used
for `Box`/`Unbox`/`Unbox_Any`, so the `ExecuteNeo` arms can read the source
reference and write the in-place result. The JIT sets `Register1 == Register2`
for these opcodes, so the result overwrites the operand slot.

#### Scenario: isinst/castclass no longer hit the Step-6 default
- **WHEN** the Neo JIT compiles a method containing `is`/`as`/cast and the
  method is executed
- **THEN** the `isinst`/`castclass` arms run (not the switch `default`
  `NotImplementedException`), because the offset-lowering pass has stamped the
  reference-slot operands the arms require.
