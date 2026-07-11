## ADDED Requirements

### Requirement: ExecuteNeo Unbox handles a boxed IL enum to its underlying primitive

Under `ENABLE_NEO_MODE`, the `Unbox`/`Unbox_Any` arm in `ExecuteNeo`
(`ILIntepreter.Neo.cs`) SHALL handle the case where the boxed source object is an
`ILEnumTypeInstance` (a boxed IL enum) and the target type is a CLR primitive
(the enum's underlying type), mirroring the Legacy Unbox arm at
`ILIntepreter.Register.cs:4046` (`!isEnumObj` guard) and `:4129`
(`if (res is ILEnumTypeInstance) res.CopyToRegister(0, ...)`).

The current Neo CLR-primitive branch
(`ILIntepreter.Neo.cs:4390-4394`, `if (clrUnboxType.IsPrimitive)
NeoWritePrimitiveToFrame(obj, ...)`) does NOT exclude `ILEnumTypeInstance`, so it
throws `NotImplementedException("Neo: unsupported CLR primitive for Unbox: " +
clr.FullName)` at `:6339` when `obj` is an `ILEnumTypeInstance`. The fix SHALL
detect `obj is ILEnumTypeInstance` before that primitive write and copy the enum's
underlying value (the enum's underlying-primitive bytes) into
`frameBase + ip->DstOffset` (the dest primitive slot). The value storage (whether
the `ILEnumTypeInstance`'s `Primitives` byte array or its `fields` byte array is
authoritative) SHALL be confirmed empirically (the existing ILType-enum Unbox arm
at `:4352-4359` reads `ins.Primitives`; prefer that, fall back to `fields`).

This change SHALL NOT alter Legacy (`ExecuteR` already handles the enum case). It
is Neo-gated (`#if ENABLE_NEO_MODE`).

#### Scenario: a boxed IL enum unboxes to its underlying primitive under Neo
- **WHEN** an IL method under `ENABLE_NEO_MODE` boxes an IL enum value (e.g.
  `E.B` where `enum E { A = 1, B = 2 }`, producing an `ILEnumTypeInstance`) and
  then unboxes it to the underlying `int` (e.g. via `(int)(object)E.B` or an
  equivalent reflection/convert path) and reads the result
- **THEN** the result is `2` (the enum's underlying constant), proving the Neo
  Unbox arm extracted the enum value, rather than throwing
  `NotImplementedException` ("Neo: unsupported CLR primitive for Unbox:
  ILEnumTypeInstance").

#### Scenario: the enum Unbox is Legacy-neutral
- **WHEN** the same box-then-unbox-to-underlying runs under the Legacy register VM
- **THEN** it behaves as before (Legacy's own enum Unbox path serves it); the
  Neo-gated guard does not alter Legacy dispatch.
