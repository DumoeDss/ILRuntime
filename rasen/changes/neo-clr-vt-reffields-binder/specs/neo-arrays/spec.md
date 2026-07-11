## ADDED Requirements

### Requirement: RuntimeHelpers.InitializeArray has a Neo redirect

Under `ENABLE_NEO_MODE`, a CLR-method call to
`System.Runtime.CompilerServices.RuntimeHelpers.InitializeArray(Array, RuntimeFieldHandle)`
(the C# array-initializer lowering for a sufficiently large `new T[]{ ... }`) SHALL be
served by a Neo redirect registered on `RedirectMapNeo` (`RegisterCLRMethodRedirectionNeo`
in the `AppDomain` constructor, `#if ENABLE_NEO_MODE`), so that `InvokeNeoClrMethod`
(`ILIntepreter.Neo.cs`) dispatches to it instead of falling through to the reflection
`CLRMethod.Invoke(byte*)`.

The Neo redirect (`CLRRedirections.InitializeArrayNeo`, signature
`CLRRedirectionDelegateNeo`) SHALL read param 0 (the destination `Array`) as a Neo object
reference and SHALL obtain the initializer `byte[]` for param 1, then bulk-copy the
initializer bytes into the array via `GCHandle.Alloc(..., Pinned)` +
`Marshal.UnsafeAddrOfPinnedArrayElement` + `Marshal.Copy` (mirroring the Legacy
`CLRRedirections.InitializeArray` body). The method is `void`; the redirect SHALL NOT
write a return value.

Because `CopyNeoCallArguments` copies each call argument by the CALLEE's declared size,
param 1's `RuntimeFieldHandle` slot (~the managed size of the struct) cannot carry the
full N-byte initializer blob as flat bytes. The redirect SHALL therefore consume param 1
as a Neo object reference (the leading 4-byte managed-stack index) pointing at the
initializer `byte[]`. The Neo `ldtoken` field path SHALL surface the
`<PrivateImplementationDetails>` RVA-initialized blob field as that `byte[]` reference
(the blob is read from the Cecil `FieldDefinition.InitialValue`; the compiler-generated
`.size N` blob struct has zero declared instance fields, so its computed
`TotalPrimitiveSize`/`TotalReferenceCount` are both 0 and the static instance
`ManagedObjects` is null for a blob-only type, leaving the Cecil `InitialValue` as the
authoritative source), so it flows through param 1 to the redirect exactly as the Legacy
redirect's `data = param[1] as byte[]` expects.

A non-large array initializer (one the C# compiler lowers to individual `stelem` stores)
SHALL be unaffected (it never calls `InitializeArray`). This redirect is Neo-gated and
does not modify Legacy (`ExecuteR` / the Legacy `InitializeArray` redirect).

#### Scenario: a large int array initializer is populated correctly under Neo
- **WHEN** an IL method under `ENABLE_NEO_MODE` executes `new int[]{ 100, 101, ... }`
  with at least 32 distinct elements (128+ bytes, forcing the compiler to the
  `newarr; dup; ldtoken <blob field>; call RuntimeHelpers.InitializeArray` form) and
  reads the elements back
- **THEN** every element equals its compile-time constant (e.g. `arr[i] == 100 + i`),
  not a `NotImplementedException` ("CLR value type with reference fields and no
  ValueTypeBinder (Step 13b): ... System.RuntimeFieldHandle"), proving the Neo redirect
  ran and the initializer `byte[]` reached it.

#### Scenario: the Step-13b RuntimeFieldHandle NIE no longer fires for InitializeArray
- **WHEN** the full Neo smoke (no `NeoStep` filter) runs over the `TestCases` DLL
- **THEN** the message `"CLR value type with reference fields and no ValueTypeBinder
  (Step 13b): register a binder. Type: System.RuntimeFieldHandle"` occurs ZERO times
  (previously ~18 pre-crash occurrences, all from array initializers), because the call
  is intercepted by the Neo redirect before the reflection-fallback param marshal.

#### Scenario: the redirect is Legacy-neutral
- **WHEN** the same array-initializer test method runs under the Legacy register VM
  (`useRegister=true`, plain `Debug`)
- **THEN** it behaves as before (Legacy's own `InitializeArray` redirect serves the call);
  the Neo-gated redirect registration and `CLRRedirections.InitializeArrayNeo` do not
  alter Legacy dispatch.

#### Scenario: a non-int element-type array initializer is supported
- **WHEN** an IL method under `ENABLE_NEO_MODE` executes a large `new double[]{ ... }`
  (or `long[]`/`byte[]`/`float[]`) initializer lowered to `InitializeArray` and reads it
  back
- **THEN** the elements are correct, proving the redirect's `Marshal.Copy` bulk copy is
  element-type-agnostic (it copies raw bytes into the pinned CLR array), not limited to
  `int[]`.

### Requirement: ldtoken surfaces the array-initializer blob as a byte[] reference under Neo

The Neo `ldtoken` field path (`ExecuteNeo`, `Operand == 0`) SHALL, for a static field
whose Cecil `FieldDefinition.InitialValue` is a non-empty `byte[]` (the
`<PrivateImplementationDetails>` RVA blob; that blob-only declaring type has a null static
`ManagedObjects` because the `.size N` blob struct contributes zero to
`StaticTotalReferenceCount`, so the Cecil `InitialValue` is the source), push that
`byte[]` as a Neo object reference into the destination slot so that a subsequent
`call RuntimeHelpers.InitializeArray` receives it in param 1. This mirrors Legacy, where
the `ldtoken` field path reads the static field value (Legacy stores the `InitialValue`
as an `Object` slot) and the Legacy `InitializeArray` redirect reads param 1 as `byte[]`.

#### Scenario: ldtoken feeding InitializeArray delivers the blob
- **WHEN** an IL method under `ENABLE_NEO_MODE` executes the array-initializer sequence
  `ldtoken <blob field>; call RuntimeHelpers.InitializeArray`
- **THEN** the `byte[]` the redirect reads from param 1 is non-null and its length equals
  the array's element count times the element size (e.g. 128 bytes for 32 ints), so the
  bulk copy fully initialises the array rather than reading a truncated or zero-length
  blob.
