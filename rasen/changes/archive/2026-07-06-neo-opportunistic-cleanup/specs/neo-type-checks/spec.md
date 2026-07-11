## MODIFIED Requirements

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
`isinst` SHALL NOT throw on a type mismatch; it SHALL write `null`. The arm
SHALL also function correctly when the source object is the exception stored in
a catch handler's ref slot (the caught-exception object), so an `is`/`as` check
inside a catch body SHALL resolve assignability against the caught exception's
runtime type.

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

#### Scenario: An `is` check on a caught exception resolves its type
- **WHEN** an IL method under `ENABLE_NEO_MODE` catches a `DivideByZeroException`
  into a catch-clause variable `e` and evaluates `e is DivideByZeroException`
  inside the catch body
- **THEN** `isinst` resolves the assignability check against the caught
  exception's runtime type and yields the non-null reference (so the C#-emitted
  null-check yields `true`), proving the type-check-in-catch shape works (this is
  the shape the Step 14 TC2 tighten exercises, now that Step 15 has landed
  `isinst`).
