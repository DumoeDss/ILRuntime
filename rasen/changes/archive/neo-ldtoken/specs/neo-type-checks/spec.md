## ADDED Requirements

### Requirement: ldtoken opcode executes under Neo

The `ldtoken` `ExecuteNeo` arm SHALL read the token-kind discriminator from
`ip->Operand` (set by the JIT: `1` = TypeReference, `0` = FieldReference) and the
packed token from `ip->OperandLong`, and push the result into the destination
register's primitive slot (`frameBase + ip->DstOffset`).

For the **type path** (`Operand == 1`, the `typeof(T)` case) the arm SHALL resolve
`IType type = AppDomain.GetType((int)ip->OperandLong)`, throw `TypeLoadException` if
the type resolves to null, and otherwise push `type.ReflectionType` as a Neo object
reference: store the object at the destination ref slot (`mStack[frameRefBase +
ip->Operand3]`) and write that ref index into the destination primitive slot. This
matches Legacy `ExecuteR`, which pushes `type.ReflectionType` (NOT a real
`RuntimeTypeHandle` struct); the registered `Type.GetTypeFromHandle` CLR redirect is
a no-op pass-through, so the `System.Type` flows straight to the consumer.

For the **field path** (`Operand == 0`) the arm SHALL mirror Legacy: resolve the
declaring type from `(int)(ip->OperandLong >> 32)` and, for an `ILType` declaring
type, read the static field value (indexed by `(int)ip->OperandLong`) into the
destination slot using the same primitive / value-type / reference category dispatch
as the `Ldsfld` arm (the `OperandLong` encoding is identical to `Ldsfld`). For a CLR
declaring type the arm SHALL throw a tagged `NotImplementedException`.

#### Scenario: typeof of a primitive type resolves under Neo
- **WHEN** an IL method under `ENABLE_NEO_MODE` evaluates `typeof(int)`
  (`ldtoken int32; call Type.GetTypeFromHandle`) and compares the result's name
- **THEN** the result is the non-null `System.Type` whose name is `"Int32"`, not a
  `NotImplementedException`, proving the `ldtoken` type path pushes the resolved
  `ReflectionType` and the no-op `GetTypeFromHandle` passes it through.

#### Scenario: typeof of a string/reference type resolves under Neo
- **WHEN** an IL method under `ENABLE_NEO_MODE` evaluates `typeof(string)` and
  compares the result's name
- **THEN** the result is the non-null `System.Type` whose name is `"String"`,
  without throwing.

#### Scenario: typeof of an ILRuntime-defined type resolves under Neo
- **WHEN** an IL method under `ENABLE_NEO_MODE` evaluates `typeof(SomeILType)` where
  `SomeILType` is a type defined in the hotfix DLL and uses the resulting `Type`
  (e.g. feeds it to an API that reads its name)
- **THEN** the `ldtoken` arm resolves the `ILType` via `AppDomain.GetType` and pushes
  its `ReflectionType`, yielding a non-null `Type` whose name matches the IL type,
  without throwing.

#### Scenario: typeof result is consumed by a downstream reflection call
- **WHEN** an IL method under `ENABLE_NEO_MODE` obtains `typeof(T)` and uses the
  `System.Type` to drive further behaviour (e.g. `Activator.CreateInstance` via a CLR
  redirect, or compares two `typeof` results for equality)
- **THEN** the consumed value is the correct `System.Type` instance, proving the
  pushed reference flows through the call boundary, not a stale/null slot.

#### Scenario: ldtoken no longer hits the Step-6 default
- **WHEN** the Neo JIT compiles and executes a method containing `typeof(...)`
- **THEN** the `Ldtoken` arm runs (not the switch `default`
  `NotImplementedException`), because the offset-lowering pass has stamped the
  operands the arm requires.

### Requirement: Neo offset-lowering stamps ldtoken ref slot

The Neo offset-lowering pass (`LowerNeoOffsets`) SHALL handle `Ldtoken` explicitly:
it SHALL stamp the destination register's ref-slot index into the spare `Operand3`
field (because `Operand` is already the 0/1 token-kind discriminator and cannot host
the ref slot) and SHALL call `LowerR1` to set `DstOffset` from `Register1`. Without
this case the opcode hits the lowering `default:` (`handled = false`), leaving
`DstOffset` and the ref slot at zero defaults and causing the ExecuteNeo arm to read
and write the wrong slot.

#### Scenario: LowerNeoOffsets stamps DstOffset and Operand3 for ldtoken
- **WHEN** the Neo JIT compiles a method containing `ldtoken` and `LowerNeoOffsets`
  runs over the body
- **THEN** the `Ldtoken` instruction leaves the pass with a valid `DstOffset`
  (derived from `Register1`) and its `Operand3` set to the destination register's
  `RefOffset`, so the ExecuteNeo arm can locate both the primitive destination and
  the reference slot, and `WarnUnhandledNeoLoweringOpcode` does not flag it as
  unhandled.
