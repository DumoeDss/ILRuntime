# neo-exceptions delta -- neo-f4-surfaced-gaps

## ADDED requirements

### Requirement: A null reference operand read by an autogen Neo CLR binding SHALL yield null, not throw

The autogen Neo CLR method redirections (e.g.
`System_Type_Binding.op_Equality_1_Neo`, `System_String_Binding.op_Equality_*_Neo`,
and every autogen Neo binding that reads a reference-typed operand) read their
reference operands via `ILIntepreter.ReadNeoReference`. When an operand is the
Neo null sentinel (the mStack index `-1`, emitted by `Ldnull` and by any
expression producing a null reference), `ReadNeoReference` SHALL return `null`
rather than indexing `mStack[-1]`. This SHALL be implemented by applying the
established Neo null-sentinel convention (`(idx >= 0) ? mStack[idx] : null`,
already used at the `CLRMethod.Invoke` Neo arg read and at the `Ldelem_Ref`
null encoding) inside `ReadNeoReference` itself, so the fix covers the entire
autogen-binding defect class in one place. This requirement is Neo-only;
Legacy uses `StackObject.ToObject` (a different path) and is byte-identical.

#### Scenario: Type.op_Equality with a null operand does not throw
- **WHEN** an IL method obtains a non-null `System.Type t` (e.g. via
  `new MyEx("x").GetType()`) and evaluates `t == null`, which lowers to
  `Type.op_Equality(t, null)` (the right operand is the null sentinel).
- **THEN** the `op_Equality` redirection SHALL execute without throwing
  (no `ArgumentOutOfRangeException` from `mStack[-1]`).
- **AND** the result SHALL be `false` (`non-null == null` per CLR semantics).

#### Scenario: Type.op_Equality both-null and non-null operands
- **WHEN** an IL method evaluates `Type.op_Equality(null, null)`.
- **THEN** the result SHALL be `true`.
- **AND** **WHEN** it evaluates `Type.op_Equality(a, b)` with both non-null.
- **THEN** the result SHALL follow CLR reference/type equality semantics.
