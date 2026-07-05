# neo-arrays

Neo interpreter support for single-dimension (rank-1, `*`) array creation and
element access across the three Neo array representations: CLR primitive arrays,
IL reference-type arrays, and IL value-type arrays. This capability is
implemented in `ExecuteNeo` (`ILIntepreter.Neo.cs`) plus the Neo offset-lowering
pass (`Optimizer.Neo.cs` `LowerNeoOffsets`); it does NOT modify Legacy
(`ExecuteR`).

This capability covers `Newarr`, `Ldelem_*`, `Stelem_*`, and `Ldlen`.
Bounds enforcement relies on the underlying CLR typed array indexer's
`IndexOutOfRangeException` (matching Legacy, which has no interpreter-side
explicit check); a null array reference yields `NullReferenceException`. It is
distinct from `neo-value-types` (whose `CopyILToFrame`/`CopyFrameToIL` helpers
this capability reuses for IL value-type array elements) and from the future
byref/Ref-Slot capability (which will host `Ldelema`).

## Non-goals

Out of scope for this capability:

- **`Ldelema` (address-of-element)** -- `Ldelema` is implemented in
  `ExecuteNeo` and produces a Ref Slot; its only consumers are the
  `neo-byref` store/load-indirect opcodes (`stind_*`/`ldind_*`/`stobj`/`ldobj`).
  The full Ref Slot / byref model (`stind`/`ldind`, `ldloca`/`ldflda` unified
  8-byte `(objIdx, offset)`, `ref`/`out` parameters, `fixed`) belongs to the
  `neo-byref` capability; the `Ldelema` arm here is only meaningful together
  with those consumers.
- **Generic-with-token `Code.Ldelem` / `Code.Stelem` and native-int
  `Code.Ldelem_I` / `Code.Ldelem_U8`** -- LIFTED: these CIL codes are now
  enumerated by the JIT's `Translate` (the generic-token forms route to
  `Ldelem_Any`/`Stelem_Any`; `Code.Ldelem_I` routes to `Ldelem_I4`, native
  int being I4-width on this VM; `Code.Ldelem_U8` routes to `Ldelem_I8`). The
  native-int `Stelem_I` runtime arm is also implemented. (Note: in this
  Mono.Cecil fork the generic `Code.Ldelem`/`Code.Stelem`/`Code.Ldelem_U8`
  enums do not exist as distinct codes -- the generic form IS `*_Any` and
  `Ldelem_U8` is not a real ECMA opcode. The capability still documents the
  intended routing for a fork that does define them.)
- **Multi-dimensional arrays (`int[,]`)** -- this capability is single-dimension
  (`*` rank) only. Multi-dim element access uses different `Address`/element
  helpers and ABI. Multi-dim is owned by a separate child (`neo-array-multidim`)
  tracked in `.trae/documents/neo-deferred-items.md`.

## Requirements

### Requirement: Newarr allocates a single-dimension array

The Neo `ExecuteNeo` loop SHALL implement `OpCodeREnum.Newarr`. Given a count (read from
the source frame byte slot) and an element type (resolved from the instruction's
type-token operand), it SHALL allocate a rank-1 array and store its reference on the
managed stack, writing the managed-stack index into the destination frame byte slot.

For a CLR (non-ILTypeInstance) element type the allocation SHALL use the element type's
CLR array form (a typed CLR `Array`). For an IL value-type element type the allocation
SHALL create an `ILTypeInstance[]` and pre-instantiate each slot with a default
instance of the element type. For an IL reference-type element type the allocation
SHALL create an `ILTypeInstance[]` with null elements (C# default).

#### Scenario: new int array, store and read an element
- WHEN an IL method executes `int[] a = new int[10]; a[0] = 42; return a[0];`
- THEN the Neo interpreter SHALL allocate a 10-element `int[]`, store `42` at index 0,
  and yield `42` on read.

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

### Requirement: Ldlen yields the array length

The Neo loop SHALL implement `OpCodeREnum.Ldlen`. It reads the array reference from its
frame byte slot and writes the array's `Length` into the destination primitive frame
slot as a native integer.

#### Scenario: length of a freshly allocated array
- WHEN `Ldlen` is executed on a `new int[7]`
- THEN the destination primitive frame slot holds `7`.

### Requirement: Bounds violations throw IndexOutOfRangeException

Array element access SHALL rely on the underlying CLR typed array indexer
(`((T[])arr)[i]` for CLR primitive arrays; `ILTypeInstance[]` indexer for IL arrays) to
throw `IndexOutOfRangeException` on out-of-range indices. The Neo outer try/catch
routes the exception to a matching catch handler. No interpreter-side explicit bounds
check is added (matching Legacy semantics).

#### Scenario: out-of-range index is caught
- WHEN an IL method wraps an out-of-range `Stelem`/`Ldelem` in `try { } catch (IndexOutOfRangeException)`
- THEN the catch handler runs (the native CLR indexer throw is surfaced by the Neo
  outer exception machinery) and execution continues past the catch.

### Requirement: Null array reference throws NullReferenceException

Reading the array reference from its managed-stack slot and resolving it to null SHALL
throw `NullReferenceException` before any element access.

#### Scenario: indexing a null array
- WHEN `Ldelem`/`Stelem`/`Ldlen` resolves its array operand to a null reference
- THEN a `NullReferenceException` is thrown (surfaced through the Neo exception
  machinery) before any element access is attempted.

### Requirement: Ldelema produces an element-address Ref Slot

`OpCodeREnum.Ldelema` SHALL be implemented in `ExecuteNeo` (this requirement
supersedes the prior "Ldelema is out of scope" requirement). The arm SHALL
produce a Ref Slot `(arrayMStackIndex, elementByteOffset)` where
`elementByteOffset` is computed at runtime from the element index and the
array's element layout, resolved from the array's CLR type (mirroring the
existing `Ldelem`/`Stelem` array-kind resolution). `Ldelema`'s result SHALL be
consumed only by the `neo-byref` store/load-indirect opcodes
(`stind_*`/`ldind_*`/`stobj`/`ldobj`); the result is unexercisable without
those consumers, which the `neo-byref` capability provides.

#### Scenario: ldelema address consumed by stind then ldind
- WHEN `ldelema arr, i` produces a Ref Slot consumed by `stind_i4` and later by
  `ldind_i4`
- THEN the stored value SHALL be observable through the subsequent load, and
  through a direct `Ldelem_I4` of the same element.

#### Scenario: ldelema no longer reports unimplemented
- WHEN `OpCodeREnum.Ldelema` is reached in `ExecuteNeo`
- THEN it executes (producing a Ref Slot) instead of throwing a Step-tagged
  `NotImplementedException`.
