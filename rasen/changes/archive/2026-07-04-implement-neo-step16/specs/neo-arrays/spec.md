# neo-arrays

Neo interpreter support for single-dimension (rank-1, `*`) array creation and element
access across the three Neo array representations: CLR primitive arrays, IL
reference-type arrays, and IL value-type arrays. Scope: `Newarr`, `Ldelem_*`, `Stelem_*`,
`Ldlen`. `Ldelema`, multi-dimensional arrays, and generic-with-token array variants are
out of scope.

## ADDED Requirements

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

### Requirement: Stelem stores a frame value into an array element

The Neo loop SHALL implement the primitive-typed `Stelem_*` opcodes (`Stelem_I1`,
`Stelem_I2`, `Stelem_I4`, `Stelem_I8`, `Stelem_R4`, `Stelem_R8`) and the object/any
opcodes (`Stelem_Ref`, `Stelem_Any`). Each reads the array reference, index, and value
from their frame byte slots and writes the value into the array element.

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

### Requirement: Ldelema is out of scope

The Neo loop SHALL NOT implement `OpCodeREnum.Ldelema` in this capability. It SHALL
remain a Step-tagged `NotImplementedException`. The Ref Slot / byref consumers it
depends on (`stind`, `ldind`, `ref`/`out` parameters, `fixed`) belong to a later
capability; implementing `Ldelema` without them would be unexercisable.

#### Scenario: ldelema still reports unimplemented
- WHEN `OpCodeREnum.Ldelema` is reached in `ExecuteNeo`
- THEN it throws a Step-tagged `NotImplementedException` (deferred to the later
  byref/Ref-Slot capability).
