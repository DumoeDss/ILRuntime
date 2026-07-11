## ADDED Requirements

### Requirement: Neo primitive-size resolution sizes enums and CLR value types without a bare throw

The Neo primitive-size helper `AppDomain.GetPrimitiveSize(IType)` (Neo-gated,
`#if ENABLE_NEO_MODE`) is the shared sizing entry point for the Neo byte layout:
the JIT back-half call-param-slot allocator
(`Optimizer.AllocateNeoCallParamSlot`, invoked from `LowerNeoOffsets`) calls it
for any type that `IsPrimitive` or whose `TypeForCLR.IsEnum`, and the
`ExecuteNeo` `Stobj`/`Ldobj` arms call it for a value-type token when the token
is NOT an `ILType` (`ilType == null`). The helper historically handled ONLY the
primitive singletons (int/long/byte/.../IntPtr) and threw a BARE
`throw new NotImplementedException()` (no message) for anything else.

The helper SHALL size every Neo-layout value-type category that reaches it:

- An **enum** IType (`TypeForCLR.IsEnum`) SHALL be sized as its underlying
  primitive. This is delegated to
  `Optimizer.GetNeoValueTypeManagedSize(TypeForCLR)`, which maps an enum to its
  underlying type (`Enum.GetUnderlyingType`) and returns the primitive size.
- A **CLR value type** (a struct that is not an `ILType`, e.g. a registered- or
  unregistered-`ValueTypeBinder` CLR struct such as `TestVector3`) SHALL be sized
  by its flat managed byte size via
  `Optimizer.GetNeoValueTypeManagedSize(TypeForCLR)` (`Unsafe.SizeOf`).

IL value types (`fieldType is ILType`) SHALL continue to be sized by their
CALLERS (`ilType.TotalPrimitiveSize` / `ilType.TotalReferenceCount`) BEFORE
reaching this helper; GetPrimitiveSize is therefore NOT required to size them,
and an ILType that does reach the residual throw is a caller bug, not a sizing
gap.

Because GetPrimitiveSize currently throws for ALL non-primitives, broadening it
to handle enums and CLR value types is STRICTLY ADDITIVE: every working caller
only ever passes a primitive singleton (handled byte-identically), and every
caller that previously hit the throw was already broken (a whole-method JIT
failure or a runtime opcode throw). No previously-working path changes shape.

The residual `else` (a type that is not a primitive, not an enum, not a CLR
value type, and not an ILType — e.g. a raw reference type that should never reach
this helper) SHALL throw a TAGGED `NotImplementedException` whose message names
the offending type and the helper — NEVER the default `"The method or operation
is not implemented."` message.

Separately, the Neo-reachable `else`-branch throws that guard an unhandled
shape in resolution / typed-opcode selection SHALL each carry a tagged message
(no bare default-message NIE). These did not fire in the captured pre-crash
smoke, but they are Neo-reachable diagnostic hazards:

- `JITCompiler.GetLdfldCodeForType` residual (`JITCompiler.cs:3013`, Neo-gated):
  an IL field whose type has no typed `Ldfld_*` opcode.
- `JITCompiler.GetStfldCodeForType` residual (`JITCompiler.cs:3098`, Neo-gated):
  an IL field whose type has no typed `Stfld_*` opcode.
- `AppDomain` type-resolution unhandled-token-shape `else` (`AppDomain.cs:1717`,
  shared) and method-reference param-list `else` (`AppDomain.cs:2162`, shared).
- `JITCompiler` token-resolution `else` (`JITCompiler.cs:2915`, shared).

Tagging these is fail-loud hygiene: if any fires, the message identifies the
site and shape (parity with the Step-tagged NIEs in `ILIntepreter.Neo.cs`),
instead of the anonymous default message.

#### Scenario: an enum-typed call parameter compiles and runs

- **WHEN** an IL method invokes a CLR method (or a delegate / reflection API)
  whose parameter is a CLR enum (e.g. `Enum.GetValues`, a `BindingFlags` arg, or
  a method taking a `TestCLREnum`), and the method is JIT-compiled under Neo
- **THEN** `AllocateNeoCallParamSlot` calls `GetPrimitiveSize(enumType)`, the
  helper returns the enum's underlying-primitive size (e.g. 4 for an `int`-backed
  enum), the call-param slot is allocated and the method COMPILES (no
  `NotImplementedException`), and the call executes with the enum argument
  passed correctly. Without the fix the method fails to JIT with the bare
  default-message NIE.

#### Scenario: a CLR value type copied via stobj/ldobj sizes correctly

- **WHEN** an IL method performs a `stobj`/`ldobj` (a value-type-sized copy
  through a pointer) whose token type is a CLR struct (e.g. `TestVector3`,
  `ilType == null`), and executes under Neo
- **THEN** the `Stobj`/`Ldobj` arm's `GetPrimitiveSize(clrStruct)` returns the
  struct's flat managed byte size (via `GetNeoValueTypeManagedSize`), the
  `Unsafe.CopyBlock` copies the correct number of bytes, and no bare
  `NotImplementedException` is thrown. Without the fix the opcode throws the
  bare default-message NIE at runtime.

#### Scenario: GetPrimitiveSize stays byte-identical for primitives

- **WHEN** any existing caller passes a primitive singleton (int, long, float,
  IntPtr, etc.) to `GetPrimitiveSize` after this change
- **THEN** the returned size is identical to before (the enum / CLR-value-type
  branches are only reached for non-primitive `IsEnum` / `IsValueType` inputs),
  and every previously-green NeoStep case (NeoStep6 through NeoStep18 +
  prior-children probes) remains green.

#### Scenario: the residual throw is tagged, never the default message

- **WHEN** a type that is not a primitive, enum, CLR value type, or ILType
  reaches `GetPrimitiveSize`, OR one of the Neo-reachable resolution/splitter
  `else` branches fires (unhandled field type / token / method-reference shape)
- **THEN** the thrown `NotImplementedException` carries a message that names the
  site and the offending type/shape — it is NEVER the bare default
  `"The method or operation is not implemented."` message.

#### Scenario: the autogen Enum.GetValues framework NIE is deferred, not in scope

- **WHEN** IL code calls `System.Enum.GetValues(typeof(<IL-defined enum>))` under
  Neo and the IL-enum surfaced as `System.Type` does not support
  `GetEnumValues()`
- **THEN** the framework `System.Type.GetEnumValues()` throws an NIE (NOT an
  ILRuntime bare throw), surfaced through the autogen `GetValues_0_Neo` redirect.
  This change does NOT fix it; it is recorded as a surfaced follow-up (an IL-
  enum-`System.Type`-representation / binding investigation, sibling to the
  autogen-binding follow-ups). It is out of scope because the throw is not an
  ILRuntime bare-NIE site.
