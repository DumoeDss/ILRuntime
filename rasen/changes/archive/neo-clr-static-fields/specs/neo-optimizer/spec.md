## ADDED Requirements

### Requirement: Stsfld and Ldsfld on a CLR static field execute under Neo

The `Stsfld` and `Ldsfld` `ExecuteNeo` arms SHALL handle a CLR declaring type
(the Step-25 S3-4 capstone handled only the IL-static path). When the declaring
type resolved from `(int)(ip->OperandLong >> 32)` is a `CLRType` (not an
`ILType`), the arms SHALL resolve the static field by the hash
`(int)ip->OperandLong` via `CLRType.GetField(hash)` (which returns the
`System.Reflection.FieldInfo`, walking the base-type chain) and read or write it
via `CLRType.GetFieldValue(hash, null)` / `CLRType.SetStaticFieldValue(hash,
value)` (`FieldInfo.GetValue(null)` / `FieldInfo.SetValue(null, value)`). The
operand encoding is identical to the IL-static path and to Legacy
(`GetStaticFieldIndex`: type hash in the high 32 bits, field hash in the low 32
bits), so no JIT change is required.

The value SHALL be marshalled between the Neo frame slot
(`frameBase + ip->DstOffset`; `Stsfld`/`Ldsfld` carry only `Register1`, the
`DstOffset` alias -- no separate ref-slot operand) and the boxed CLR `object`, by
the field's `FieldType` category:

- **CLR primitive** (`FieldType.IsPrimitive`): `Stsfld` SHALL box the source
  slot's sized bytes via `NeoBoxPrimitiveByType(FieldType, srcSlot)` before
  writing; `Ldsfld` SHALL write the boxed object's bytes into the dest slot via
  `NeoWritePrimitiveToFrame(obj, dstSlot)`.
- **CLR value type** (`FieldType.IsValueType && !FieldType.IsPrimitive`):
  `Stsfld` SHALL read the source flat bytes via `ReadNeoValueType(FieldType,
  frameBase, ref off, GetNeoValueTypeManagedSize)`; `Ldsfld` SHALL write the boxed
  object via `WriteNeoValueType(obj, dstSlot, GetNeoValueTypeManagedSize)`.
- **CLR reference type** (`!FieldType.IsValueType`): `Stsfld` SHALL read the
  source slot as an mStack index (`int idx = *(int*)srcSlot; obj = idx >= 0 ?
  mStack[idx] : null`); `Ldsfld` SHALL push the object via the same `mStack.Add`
  temp-reference convention the IL-static Ldsfld reference branch uses
  (`mStack.Add(obj); *(int*)dstSlot = obj != null ? mStack.Count - 1 : -1`).

On `Ldsfld`, when the read value is a `CrossBindingAdaptorType` the arm SHALL
unwrap it to its `ILInstance` before pushing (Legacy `ExecuteR` parity). This
behavior SHALL be Legacy-neutral (gated under `ENABLE_NEO_MODE`; Legacy `ExecuteR`
is unchanged).

#### Scenario: CLR static primitive field round-trips through IL under Neo
- **WHEN** an IL method under `ENABLE_NEO_MODE` writes a known value to a CLR
  type's public static `int` field (`stsfld`) and reads it back (`ldsfld`)
- **THEN** the read value equals the written value (the round-trip assertion
  passes), proving both the `Stsfld` and `Ldsfld` CLR-primitive branches execute,
  rather than either arm throwing `"Neo Stsfld/Ldsfld: CLR static field not
  implemented"`.

#### Scenario: CLR static reference field reads under Neo (string.Empty)
- **WHEN** an IL method under `ENABLE_NEO_MODE` reads the CLR static reference
  field `System.String.Empty` (`ldsfld` on a CLR declaring type) and inspects the
  result
- **THEN** the result is the empty string (length 0), proving the `Ldsfld`
  CLR-reference branch resolves the field via `CLRType.GetFieldValue` and pushes
  it with the `mStack.Add` temp-reference convention, without throwing.

#### Scenario: CLR static value-type field reads under Neo (IntPtr.Zero)
- **WHEN** an IL method under `ENABLE_NEO_MODE` reads the CLR static value-type
  field `System.IntPtr.Zero` (`ldsfld` on a CLR declaring type) and compares the
  result
- **THEN** the result equals `IntPtr.Zero`, proving the `Ldsfld` CLR-value-type
  branch writes the boxed struct's flat bytes into the dest slot via
  `WriteNeoValueType`, without throwing.

#### Scenario: Stsfld on a CLR static field no longer throws the capstone NIE
- **WHEN** the Neo JIT compiles and executes an IL method that stores into a CLR
  type's static field
- **THEN** the `Stsfld` CLR-declaring-type branch runs (not the
  `"Neo Stsfld: CLR static field not implemented (Step 25 S3-4; the capstone is
  IL-only)"` branch), because the arm resolves the `CLRType` and writes via
  `SetStaticFieldValue`.

#### Scenario: Ldsfld on a CLR static field unwraps a CrossBindingAdaptor value
- **WHEN** an IL method under `ENABLE_NEO_MODE` reads a CLR static field whose
  value is a `CrossBindingAdaptorType` (an IL instance cross-bound to a CLR type)
- **THEN** the `Ldsfld` arm unwraps it to the underlying `ILInstance` before
  pushing, matching Legacy `ExecuteR`.
