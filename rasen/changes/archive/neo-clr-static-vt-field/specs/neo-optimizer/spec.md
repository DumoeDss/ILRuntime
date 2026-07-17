## ADDED Requirements

### Requirement: A registered ValueTypeBinder does not by itself make a CLR value-type static field unsafe to marshal under Neo

The Neo `Stsfld`/`Ldsfld` CLR-static value-type branches marshal a CLR value-type
static field via a flat-byte box-roundtrip: `Ldsfld` reads the boxed struct from
`CLRType.GetFieldValue(hash, null)` (`FieldInfo.GetValue(null)`) and writes its flat
managed bytes into the dest frame slot via `WriteNeoValueType(obj, dstSlot,
GetNeoValueTypeManagedSize(ft))`; `Stsfld` reads the source flat bytes via
`ReadNeoValueType(ft, frameBase, ref off, GetNeoValueTypeManagedSize(ft))` and
writes the boxed struct via `CLRType.SetStaticFieldValue(hash, value)`
(`FieldInfo.SetValue(null, value)`). The flat-byte readers/writers
(`ReadNeoValueType`/`WriteNeoValueType`, `ILIntepreter.Neo.cs:227/243`) use
`Unsafe.ReadUnaligned<T>`/`WriteUnaligned<T>` and do NOT consult the registered
`ValueTypeBinder` (the binder exposes only Legacy `StackObject*` marshalling;
there is no Neo `byte*` binder API). Therefore a registered `ValueTypeBinder` on
the field's CLR type SHALL NOT by itself cause the field to be refused.

The guard `NeoClrVtStaticFieldIsUnsafe(ft, slotSize)` SHALL refuse a CLR value-type
static field ONLY when either (a) the type has reference fields
(`NeoClrStructHasRefFields(ft)`, recursive -- a Neo frame VT slot stores GC refs as
mStack indices, but `FieldInfo.GetValue` returns real GC pointers, so the flat-byte
round-trip would corrupt the ref region / leave the mStack ref slot empty), or (b)
the type's flat managed size overflows the dest/source register's eval-slot size
(`Optimizer.GetNeoValueTypeManagedSize(ft) > slotSize`, AccessViolation
protection). A blittable binder struct that fits the slot (e.g.
`TestVector3.One` -- 3 floats, 12 bytes, with a registered binder) SHALL pass the
guard and marshal through the flat-byte box-roundtrip. This behavior SHALL be
Legacy-neutral (the guard and both arms are under the `ILIntepreter.Neo.cs`
file-level `#if ENABLE_NEO_MODE` gate; Legacy `ExecuteR` is unchanged).

#### Scenario: Ldsfld reads a blittable binder CLR value-type static (TestVector3.One)
- **WHEN** an IL method under `ENABLE_NEO_MODE` reads the CLR static field
  `TestVector3.One` (a `TestVector3` whose type has a registered `ValueTypeBinder`,
  is blittable -- 3 floats -- and fits the dest slot) via `ldsfld`
- **THEN** the read SHALL return the struct `(X=1, Y=1, Z=1)`, proving the
  `Ldsfld` CLR-value-type branch ran `WriteNeoValueType` after the guard passed
  (the binder clause no longer fires), rather than throwing
  `"Neo Ldsfld: CLR static value-type field One ... not supported under Neo ..."`

#### Scenario: Stsfld writes then Ldsfld reads back a blittable binder CLR value-type static
- **WHEN** an IL method under `ENABLE_NEO_MODE` stores a `TestVector3` value
  (sourced from `TestVector3.One`) into a CLR type's writable `public static
  TestVector3` field via `stsfld`, then a HOST CLR read of that field returns the
  field's current value, and the IL method reads the field back via `ldsfld`
- **THEN** the host read SHALL observe the stored `(1,1,1)` value (sum 3, proving
  the `Stsfld` write landed), AND the IL read-back SHALL observe the same value
  (sum 6 when summed with the source via `SumTestVector3Fields`), proving BOTH the
  `Stsfld` and `Ldsfld` binder-struct paths execute, rather than the
  `Stsfld`/`Ldsfld` guard throwing

#### Scenario: A binder CLR value-type static WITH reference fields is still refused
- **WHEN** an IL method under `ENABLE_NEO_MODE` reads or writes a CLR static field
  whose `TestVector3`-like type has a registered `ValueTypeBinder` AND has a
  managed reference-typed field (so the flat-byte round-trip cannot track its GC
  refs)
- **THEN** the guard SHALL still refuse it via the `NeoClrStructHasRefFields`
  clause with the tagged NIE (removing the binder clause does NOT open a hole for
  ref-field structs, binder or not)

#### Scenario: A binder CLR value-type static that overflows the eval slot is still refused
- **WHEN** an IL method reads or writes a CLR value-type static whose flat managed
  size exceeds the dest/source register's eval-slot size
- **THEN** the guard SHALL still refuse it via the slot-overflow clause with the
  tagged NIE (AccessViolation protection is preserved after the binder clause is
  removed)

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and runs the Legacy
  register VM
- **THEN** `NeoClrVtStaticFieldIsUnsafe`, the `Stsfld`/`Ldsfld` CLR-static arms,
  and the flat-byte readers/writers SHALL all compile out (file-level
  `#if ENABLE_NEO_MODE`), and a stash-toggle plain-`Debug` + `useRegister=true`
  NeoStep-filter run SHALL show the SAME pre-existing Legacy failure set with and
  without this change
