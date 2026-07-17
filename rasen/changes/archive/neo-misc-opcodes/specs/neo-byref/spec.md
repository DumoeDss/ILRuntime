## ADDED Requirements

### Requirement: ExecuteNeo implements Ldsflda (load static field address) for an IL static field

Under `ENABLE_NEO_MODE`, the CIL `ldsflda` opcode (`OpCodeREnum.Ldsflda`) for an
IL static field SHALL be served by a `case OpCodeREnum.Ldsflda:` arm in
`ExecuteNeo` (`ILIntepreter.Neo.cs`), mirroring the Legacy arm at
`ILIntepreter.Register.cs:3348-3355`.

Legacy represents the result as an `ObjectTypes.StaticFieldReference` `StackObject`
carrying `(typeHash, fieldHash)`. Neo has no such type; a managed address is the
8-byte byref pair `(objIdx, off|flag)` consumed by the `Stind_*`/`Ldind_*`/
`Stobj`/`Ldobj`/`CopyNeoCallArguments` arms. Because those arms ALREADY dispatch
`mStack[objIdx] is ILTypeInstance` to `ins.Primitives[off]` (primitive field) /
`ins.ManagedObjects[off]` (reference field) (`ILIntepreter.Neo.cs:4863-4906` and
`:5143-5165`), the `Ldsflda` arm for an IL static field SHALL produce a byref that
addresses the declaring IL type's `StaticInstance`: materialize
`ilType.StaticInstance` into `mStack` and write the byref
`(mStack.Count - 1, fieldOffset)` into the destination's two frame ints, where
`fieldOffset` is the static field's `PrimitiveOffset` for a primitive-typed static
field, or its `ReferenceOffset` for a reference-typed static field (reusing the
same offset resolution the Neo `Ldsfld` IL-static path uses).

The arm SHALL decode the field identity from `ip->OperandLong` (encoding
`(typeHash<<32)|fieldHash`, identical to `Ldsfld`/`Stsfld`,
`JITCompiler.cs:2506-2510`). The arm SHALL NOT stamp `Operand3` (it aliases the
high dword of `OperandLong`).

A following `stind`/`ldind` through this byref SHALL therefore write/read the IL
static field's storage with no consumer-arm change (e.g.
`stind.i4 42` -> `StaticInstance.Primitives[fieldOffset] = 42`).

#### Scenario: ldsflda + stind writes an IL static field under Neo
- **WHEN** an IL method under `ENABLE_NEO_MODE` takes the address of an IL static
  field (e.g. via a `ref int` helper `static void Set(ref int x, int v) { x = v; }`
  invoked as `Set(ref Sf, 7)`, or an equivalent `ldsflda; stind` pattern that the
  C# compiler emits) and afterwards reads `Sf`
- **THEN** `Sf == 7` (the value written through the static-field address), proving
  the Neo `Ldsflda` arm produced a byref the existing `Stind`/`CopyNeoCallArguments`
  consumer resolved to the IL static field, rather than throwing
  `NotImplementedException` ("Neo: opcode ... not yet implemented").

#### Scenario: Ldsflda is Legacy-neutral
- **WHEN** the same `ldsflda`-emitting pattern runs under the Legacy register VM
- **THEN** it behaves as before (Legacy's own `Ldsflda` arm serves it); the
  Neo-gated arm does not alter Legacy dispatch.

### Requirement: Neo Ldsflda on a CLR static field fails with a tagged deferral

Under `ENABLE_NEO_MODE`, `ldsflda` on a **CLR** static field (where the declaring
type is a `CLRType`) currently has no Neo representation: a CLR static field has
no heap object (its storage is `FieldInfo.GetValue(null)`/`SetValue(null, v)`),
so it cannot be addressed by the existing `(objIdx, off)` object-field consumer
arms. Rather than silently mis-behave, the Neo `Ldsflda` arm SHALL throw a tagged
`NotImplementedException` naming the deferral (e.g. "Neo Ldsflda: CLR static field
address deferred (follow-up)") for the CLR-static shape, UNLESS the diagnose-first
step surfaces a live CLR-static hit that justifies implementing a dedicated
static-field byref sentinel + consumer arm in a follow-up.

This tagged NIE is distinct from the Step-6 default and keeps the gap honestly
labeled (the same discipline as child-4's deferred IL-base / array-element shapes).

#### Scenario: a CLR static field ldsflda is tagged, not silently wrong
- **WHEN** an IL method under `ENABLE_NEO_MODE` executes `ldsflda` on a CLR static
  field and the CLR-static shape has NOT been implemented
- **THEN** the arm throws a `NotImplementedException` carrying the Ldsflda-CLR
  deferral message (not the generic Step-6 message, and not a silent wrong
  address), so the gap is measurable.
