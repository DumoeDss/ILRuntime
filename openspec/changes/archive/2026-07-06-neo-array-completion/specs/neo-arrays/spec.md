## ADDED Requirements

### Requirement: Stelem_I stores a native integer into an array element

The Neo loop SHALL implement `OpCodeREnum.Stelem_I`. It reads the array
reference, index, and a native-integer value from their frame byte slots and
writes the value into the array element. Native integers (`nint`/`nuint`,
i.e. `IntPtr`/`UIntPtr`) are pointer-sized; on this VM they occupy the same
width as `I4`, so the arm SHALL store via the typed CLR array indexer of the
runtime array type (`IntPtr[]` / `UIntPtr[]` / compatible), mirroring the
`Stind_I` -> `Stind_I4` idiom.

#### Scenario: native-int array store and load round-trip
- WHEN an IL method executes `nint[] a = new nint[4]; a[2] = (nint)0x1234; return (int)a[2];`
- THEN the Neo interpreter SHALL store the native integer at index 2 and yield
  `0x1234` on the subsequent load.

#### Scenario: UIntPtr array store
- WHEN an IL method stores into a `UIntPtr[]` via `stelem.i`
- THEN the value SHALL be stored via the typed CLR indexer and observable on a
  later load.

### Requirement: Store/load-indirect of a CLR array element address works at all widths

The Neo `Stind_*` and `Ldind_*` opcodes SHALL handle a `Ldelema`-produced
element address whose managed-stack object is a CLR primitive (non-IL) array
at every value width. When a `Ldelema`-produced element address targets a CLR
primitive (non-IL) array, the `Stind_*` and `Ldind_*` opcodes SHALL detect
that the address's
managed-stack object is a CLR `Array` (the `mStack[objIdx] is Array` branch)
and dispatch via `Array.SetValue` / `Array.GetValue` for the value width,
rather than treating the object as an `ILTypeInstance`. This SHALL hold for
every width a `Ldelema` address can flow into: `Stind_I1/I2/I4/I8/R4/R8`,
`Ldind_I1/U1/I2/U2/U4/I8/R4/R8`, and `Stind_Ref`/`Ldind_Ref`. (`Stind_I` and
`Ldind_I` inherit the array branch via their existing `goto Stind_I4` /
`Ldind_I4`.)

#### Scenario: stind/ldind of an I8 element through a ldelema address
- GIVEN a `long[]` and a `ldelema` address of element `i` produced for a
  `ref long` / `fixed` consumer
- WHEN `stind.i8` stores a value through the address and `ldind.i8` later reads it
- THEN the stored value SHALL be observable through both the load and a direct
  `Ldelem_I4`-equivalent read of the same element, without an
  `InvalidCastException`.

#### Scenario: stind/ldind of an R4 / R8 / Ref element through a ldelema address
- WHEN `stind.r4`, `stind.r8`, or `stind.ref` stores through a CLR-array
  element address, and the matching `ldind.*` reads it back
- THEN the round-trip SHALL succeed via the `Array` branch (no
  `InvalidCastException`, no `GetNeoILInstance` attempted on a CLR `Array`).

## MODIFIED Requirements

### Requirement: Ldelem loads an array element into the frame

The Neo loop SHALL implement the primitive-typed `Ldelem_*` opcodes (`Ldelem_I1`,
`Ldelem_U1`, `Ldelem_I2`, `Ldelem_U2`, `Ldelem_I4`, `Ldelem_U4`, `Ldelem_I8`,
`Ldelem_R4`, `Ldelem_R8`) and the object/any opcodes (`Ldelem_Ref`, `Ldelem_Any`).
Each reads the array reference and the index from their frame byte slots and writes the
element to the destination frame byte slot.

The JIT `Translate` SHALL enumerate the generic-token `Code.Ldelem` (routing it
to `Ldelem_Any`, whose runtime arm resolves the element type from the type
token), the native-int `Code.Ldelem_I` (routing it to `Ldelem_I4`, native int
being I4-width on this VM), and `Code.Ldelem_U8` (routing it to `Ldelem_I8`,
the unsigned-8 / `ulong` width). These CIL codes SHALL no longer throw a
JIT-time `NotImplementedException`.

For a CLR primitive array, the element SHALL be read via the typed CLR array indexer
into the destination primitive slot. For an IL reference-type array, the element object
SHALL be stored on the managed stack with its index written to the destination slot.
For an IL value-type array, the element's primitive bytes and reference slots SHALL be
copied into the destination frame region (primitive + reference copy).

#### Scenario: read a CLR primitive array element
- GIVEN an `int[]` populated by `Stelem_I4`
- WHEN `Ldelem_I4` reads index `i`
- THEN the value stored at `i` is written to the destination primitive frame slot.

#### Scenario: read an IL value-type array element
- GIVEN a `MyStruct[]` (an `ILTypeInstance[]`) whose element at `i` was populated by
  `Stelem_Any`
- WHEN `Ldelem_Any` reads index `i`
- THEN the element's primitive bytes and reference fields are copied into the
  destination frame region, so subsequent field reads see the stored struct values.

#### Scenario: generic-token ldelem in a generic method
- GIVEN a generic method `T Get<T>(T[] a, int i)` whose body indexes `a[i]`,
  which the C# compiler emits as the generic-token `Code.Ldelem`
- WHEN the method is invoked with `T = int` on an `int[]`
- THEN the JIT SHALL enumerate `Code.Ldelem` (routing to `Ldelem_Any`) and the
  runtime SHALL return the element at `i`, instead of throwing a JIT-time
  `NotImplementedException`.

#### Scenario: native unsigned-8 (ulong) ldelem
- WHEN `Code.Ldelem_U8` reads an element of a `ulong[]`
- THEN the JIT SHALL route it to `Ldelem_I8` and the runtime SHALL yield the
  8-byte element, instead of throwing a JIT-time `NotImplementedException`.

### Requirement: Stelem stores a frame value into an array element

The Neo loop SHALL implement the primitive-typed `Stelem_*` opcodes (`Stelem_I`,
`Stelem_I1`, `Stelem_I2`, `Stelem_I4`, `Stelem_I8`, `Stelem_R4`, `Stelem_R8`)
and the object/any opcodes (`Stelem_Ref`, `Stelem_Any`). Each reads the array
reference, index, and value from their frame byte slots and writes the value
into the array element. `Stelem_I` is the native-integer store (`nint`/
`nuint`, i.e. `IntPtr`/`UIntPtr` element arrays); it SHALL store via the typed
CLR array indexer at the I4 native-int width.

The JIT `Translate` SHALL enumerate the generic-token `Code.Stelem` (routing it
to `Stelem_Any`, whose runtime arm resolves the element type from the type
token). This CIL code SHALL no longer throw a JIT-time `NotImplementedException`.
(`Code.Stelem_I` is already enumerated and lowers to `OpCodeREnum.Stelem_I`;
this change adds the previously-missing runtime arm.)

For a CLR primitive array, the value SHALL be written via the typed CLR array indexer.
For an IL reference-type array, the value's managed-stack object SHALL be stored into
the array element. For an IL value-type array, the value's primitive bytes and
reference slots SHALL be copied from the frame into the element instance's primitive
and reference storage.

#### Scenario: store into an IL value-type array element
- WHEN `Stelem_Any` stores an in-frame `MyStruct` into index `i` of a `MyStruct[]`
- THEN the element instance at `i` holds a copy of the struct's primitive bytes and
  reference fields (value semantics; later mutation of the source does not affect the
  stored element).

#### Scenario: generic-token stelem in a generic method
- GIVEN a generic method `void Set<T>(T[] a, int i, T v)` whose body stores
  `a[i] = v`, emitted as the generic-token `Code.Stelem`
- WHEN the method is invoked with `T = int`
- THEN the JIT SHALL enumerate `Code.Stelem` (routing to `Stelem_Any`) and the
  runtime SHALL store the value, instead of throwing a JIT-time
  `NotImplementedException`.

## Non-goals delta

Multi-dimensional arrays (`int[,]`, rank-2+) remain OUT of scope for this
capability. They are owned by a separate child (`neo-array-multidim`) tracked
in `.trae/documents/neo-deferred-items.md`. Multi-dim element access uses the
rank-aware `Address`/`Get`/`Set` `callvirt` ABI and `new T[n,m]` constructor
dispatch, none of which fall out of the rank-1 work.

The generic-token / native-int `Ldelem`/`Stelem` non-goal is LIFTED: those
CIL codes are now enumerated by the JIT (this change). The native-int
`Stelem_I` runtime arm non-goal is LIFTED (this change adds the arm).
