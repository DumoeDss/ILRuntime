## MODIFIED Non-goals

The "Multi-dimensional arrays (`int[,]`)" bullet is REMOVED from the Non-goals
section. Multi-dim (rank-2+) allocation + element Get/Set + metadata are now
DELIVERED (see the ADDED Requirements below). The remaining Non-goals
(`Ldelema`/byref-only consumers owned by `neo-byref`; the generic-token /
native-int variants already lifted by neo-array-completion) are unchanged.

Rationale (dump-confirmed on HEAD `0aafdb34`): the autogen binder path
(`System_Int32_Array2/Array3_Binding` in the test harness) registers Neo
redirects for the multi-dim ctor + `Set`; the reflection fallback
(`CLRMethod.Invoke`) handles ctor + `Set` + `Get` + metadata for unregistered
element types (e.g. `long[,]`). Rank-2/rank-3 round-trip + metadata probes
PASS on HEAD. The reflection-fallback exception/reference-marshaling gaps that
blocked the exception contract are closed by this change (Gap 1/2/3 in
design.md).

## ADDED Requirements

### Requirement: Multi-dimensional array allocation via newobj on the array ctor

The Neo `ExecuteNeo` loop SHALL support multi-dimensional (rank-2+) array
allocation. The C# compiler lowers `new T[n, m]` to a `newobj` on the array
type's synthetic constructor `T[0...,0...]::.ctor(int32, int32)`. The Neo
`Newobj` arm SHALL route this CLR-type newobj to `InvokeNeoClrMethod(isNewobj:
true)`, which dispatches to EITHER a registered Neo redirect (the autogen
binder's `Ctor_*_Neo` delegate, which runs `new T[a1, a2]` and writes the
result to the dest mStack ref slot) OR, when no redirect is registered, the
reflection fallback `ConstructorInfo.Invoke` (`CLRMethod.Invoke` ->
`cDef.Invoke(param)`), which allocates the multi-dim array. The allocated
array reference SHALL be stored into the dest managed-stack slot with its
index written to the dest byte offset.

#### Scenario: rank-2 int array allocation via autogen redirect
- WHEN an IL method executes `int[,] a = new int[2, 3];`
- THEN the Neo interpreter SHALL dispatch the array ctor newobj to the
  registered `System_Int32_Array2_Binding.Ctor_*_Neo` redirect, allocate a
  `int[2,3]`, and yield a usable array reference.

#### Scenario: rank-2 array allocation via reflection fallback (no binder)
- WHEN an IL method executes `long[,] a = new long[2, 2];` (no registered
  autogen binder for `long[,]`)
- THEN the Neo interpreter SHALL dispatch the array ctor newobj to the
  reflection fallback `ConstructorInfo.Invoke`, allocate a `long[2,2]`, and
  yield a usable array reference.

#### Scenario: rank-3 array allocation
- WHEN an IL method executes `int[,,] a = new int[2, 2, 2];`
- THEN the Neo interpreter SHALL allocate the rank-3 array (via redirect or
  reflection) and yield a usable reference.

### Requirement: Multi-dimensional element Get/Set via call to the array Get/Set methods

The C# compiler lowers `a[i, j] = v` to `call instance void Set(int, int, T)`
and `a[i, j]` to `call instance T Get(int, int)` on the multi-dim array type.
The Neo `Call` arm SHALL dispatch these to EITHER a registered Neo redirect
(the autogen binder's `Set_*_Neo` / `Get_*_Neo` delegate, which runs
`instance[a1, a2] = a3` / `instance[a1, a2]`) OR, when no redirect is
registered, the reflection fallback `MethodInfo.Invoke` (`def.Invoke(instance,
param)`). The store SHALL land the value at `[i, j]`; the load SHALL return
the value at `[i, j]` to the dest slot.

For a reference-type element, the reflection fallback SHALL correctly marshal
the reference param (Set) and the reference return (Get) so a round-trip
returns the stored reference (mirrors Legacy; closes Gap 1).

#### Scenario: rank-2 int get/set round-trip via redirect + reflection
- GIVEN an `int[,]` allocated via the autogen redirect
- WHEN `a[1, 2] = 42` (autogen `Set_*_Neo`) and `a[1, 2]` is read (reflection
  `Get`, no redirect registered)
- THEN the read SHALL yield `42`.

#### Scenario: multi-cell distinct values
- GIVEN an `int[2,3]` populated with distinct values at all 6 cells
- WHEN each cell is read back
- THEN each read SHALL yield the exact stored value (no coincidental default-0
  / stale-value pass).

#### Scenario: reference-element get/set round-trip (reflection fallback)
- GIVEN a `string[,]` (no registered binder; ctor + Set + Get all via
  reflection)
- WHEN `a[0, 0] = "alpha"; a[1, 1] = "omega";` and `a[0, 0]`, `a[1, 1]`,
  `a[0, 1]` are read
- THEN the reads SHALL yield `"alpha"`, `"omega"`, and `null` respectively
  (closes Gap 1; Legacy parity).

### Requirement: Multi-dimensional array metadata (Rank, Length, GetLength)

A multi-dim array's `Rank`, `Length`, and `GetLength(int)` SHALL be reachable
via the Neo Call arm (reflection fallback, no redirect) and SHALL return the
correct values.

#### Scenario: rank-2 metadata
- GIVEN an `int[2, 3]`
- WHEN `a.Rank`, `a.Length`, `a.GetLength(0)`, `a.GetLength(1)` are read
- THEN they SHALL yield `2`, `6`, `2`, `3` respectively.

### Requirement: Multi-dimensional element access surfaces the real exception unwrapped from TargetInvocationException

The reflection-fallback `MethodInfo.Invoke` / `ConstructorInfo.Invoke` SHALL
NOT surface a wrapped `TargetInvocationException` to user `catch` handlers.
When the underlying target throws (e.g. the CLR multi-dim indexer throws
`IndexOutOfRangeException` on an out-of-range index), `CLRMethod.Invoke` SHALL
unwrap the `TargetInvocationException` and rethrow its `InnerException`
(preserving the original stack via `ExceptionDispatchInfo`), so the Neo outer
exception machinery routes the REAL exception type to a matching catch handler.
This SHALL hold on BOTH the Neo and Legacy `CLRMethod.Invoke` overloads (shared
fix; closes Gap 3).

#### Scenario: out-of-range index is caught as IndexOutOfRangeException
- WHEN an IL method wraps an out-of-range multi-dim `Get`/`Set` in
  `try { } catch (IndexOutOfRangeException)`
- THEN the catch handler runs (the `TargetInvocationException` is unwrapped to
  the underlying `IndexOutOfRangeException` before it reaches user catch
  filters).

### Requirement: A null multi-dimensional array reference throws NullReferenceException

Reading the array reference for a multi-dim element access and resolving it to
null SHALL throw `NullReferenceException` before the reflection `Invoke`
proceeds. The Neo `CLRMethod.Invoke` `HasThis` branch SHALL detect the null
`this` (the Neo null-ref sentinel, `thisIdx < 0`) and throw
`NullReferenceException`, mirroring Legacy's explicit null-`this` guard (closes
Gap 2).

#### Scenario: indexing a null multi-dim array
- WHEN an IL method with `int[,] a = null;` wraps `a[0, 0]` in
  `try { } catch (NullReferenceException)`
- THEN the catch handler runs (a `NullReferenceException` -- not an
  `ArgumentOutOfRangeException` from an unguarded `mStack[-1]` read -- is
  surfaced).
