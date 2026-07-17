## Context

`System.Enum.GetValues(typeof(IlEnum))` throws a bare
`NotImplementedException` ("The method or operation is not implemented.") when
`IlEnum` is an IL-defined enum surfaced as `System.Type`. The path:

1. IL code calls `System.Enum.GetValues(Type)`. Under Neo this is redirected to the
   autogen `System_Enum_Binding.GetValues_0_Neo` (`System_Enum_Binding.cs:72`).
2. That redirect does NOT throw -- it reads `@enumType` and calls the framework
   `System.Enum.GetValues(@enumType)` (`:77`). `GetNames_1_Neo` mirrors this for
   `GetNames` (`:111`); `Enum.GetUnderlyingType` is not redirected at all (the IL call
   goes straight to the framework method).
3. The framework `System.Enum.GetValues/GetNames/GetUnderlyingType` delegate to the
   `Type.GetEnumValues()` / `GetEnumNames()` / `GetEnumUnderlyingType()` virtuals on
   the passed `System.Type`.
4. ILRuntime's IL-enum `System.Type` wrapper is `ILRuntimeType`
   (`ILRuntime/Reflection/ILRuntimeType.cs`, created by `ILType.ReflectionType`
   `ILType.cs:1985-1993`; its `BaseType` getter returns `typeof(Enum)` for an IL enum
   `:146-159`). `ILRuntimeType` does NOT override any of the `GetEnum*` virtuals (nor
   `IsEnum`) -- confirmed by grep. They fall through to the base `System.Type`
   implementation, which throws a bare `NotImplementedException()` (only the
   framework's own `RuntimeType` overrides those virtuals). First stack frame
   `System.Type.GetEnumValues()` with no file:line -- established by the `neo-bare-nie`
   child's investigation (its design.md site 7 / D3), which DEFERRED this exact site as
   "a binding/IL-enum-Type-representation investigation."

The member set of an IL enum and each member's value are directly available:
- Members = the enum's `IsLiteral` / `HasConstant` fields in Cecil
  `type.TypeDefinition.Fields` (the named constants; the special instance `value__`
  field is excluded because it is a non-literal instance field).
- Per-member value = `FieldDefinition.Constant`, which returns the boxed underlying
  value (`Mono.Cecil/.../FieldDefinition.cs:139`, `get => HasConstant ? constant : null`).
  This is the SAME accessor `ILRuntimeFieldInfo.GetRawConstantValue()` (`:168`) and
  `ILRuntimeFieldInfo.GetValue()` for a static-with-constant (`:180-181`) already use.
- Underlying type = `ILType.enumType` (the underlying primitive `IType`, assigned in
  `InitializeFields` at `ILType.cs:2977-2979`), exposed via the `ILType.TypeForCLR`
  enum branch (`ILType.cs:1966-1970`, `return enumType.TypeForCLR`).

## Goals / Non-Goals

**Goals:**
- Make `System.Enum.GetValues(IlEnumType)`, `GetNames`, and `GetUnderlyingType` work
  for an IL-defined enum surfaced as `System.Type` (the Neo-reachable surface; also
  fixes the identical Legacy path).
- A NeoStep probe that FAULTS on HEAD (the bare NIE) and PASSES after the fix.

**Non-Goals:**
- Change any autogen binding, JIT/optimizer pass, object model, or `ExecuteNeo` arm.
- `Enum.IsDefined`, `Enum.Parse`, `Enum.GetName(Type, value)`, `Enum.GetUnderlyingType`
  beyond what the framework virtual provides, or flags-attribute parsing. (Some of
  these already work via other redirects or are not in the failing surface.)
- Touch `ILRuntimeWrapperType` (the CLR-side wrapper); the failing surface is IL enums
  specifically, which use `ILRuntimeType`.

## Decisions

### D1: Implement the virtuals on the shared `ILRuntimeType`, NOT in a binding

The autogen `GetValues_0_Neo` / `GetNames_1_Neo` redirects are already correct: they
delegate to the framework, which is the right design (the framework owns enum semantics
-- ordering by value, creating the typed array, etc.). The gap is purely that
ILRuntime's `System.Type` wrapper cannot answer the framework's follow-up questions.
Implementing the virtuals on `ILRuntimeType` is the minimal, correct fix -- and it is
SHARED code (not `#if ENABLE_NEO_MODE`), so it fixes the identical Legacy redirect
(`GetValues_0` `System_Enum_Binding.cs:88` makes the same call) at the same time. This
matches how the framework expects a non-`RuntimeType` `Type` subclass to participate
(`System.Type` declares these virtuals precisely so such subclasses can implement them).
- Alternative considered: a Neo `RedirectMapNeo` redirect that special-cases IL enums.
  Rejected -- it would duplicate the framework's enum-member-enumeration logic, leave
  the Legacy path broken, and still need an underlying-type story for
  `GetUnderlyingType` (which has no redirect at all).

### D2: Source members + values from Cecil `TypeDefinition.Fields` directly

Read `type.TypeDefinition.Fields`, select the `IsLiteral && HasConstant` fields (the
enum's named constants), and read `FieldDefinition.Constant` for each value. This is
exactly how the framework `RuntimeType` implements these virtuals, and reuses the same
Cecil accessor `ILRuntimeFieldInfo` already trusts. `GetEnumValues` builds a typed
`Array.CreateInstance(GetEnumUnderlyingType(), n)` and `SetValue`s each constant (so
the element type is the underlying type, matching `Enum.GetValues`'s documented return
contract); `GetEnumNames` collects the names.
- Alternative considered: drive it through the wrapper's own `GetFields(BindingFlags)`
  + `ILRuntimeFieldInfo.GetRawConstantValue()`. Rejected as the primary path because
  it depends on how `ILType.InitializeFields` categorizes an enum's static-literal
  members into the wrapper's `fields[]` (enum fields are a special shape); the Cecil-
  direct path is self-contained and obviously correct. (The two paths return the same
  data; the apply worker may use either, but Cecil-direct is the recommended default.)

### D3: Override `IsEnum` defensively

Add `public override bool IsEnum => type.IsEnum;`. The framework's
`System.Enum.GetValues/GetNames/GetUnderlyingType` guard on `Type.IsEnum` and reject
non-enum types with `ArgumentException`. `ILRuntimeType` currently does not override
`IsEnum`; the NIE evidence (the call reaches `GetEnumValues()`, not an
`ArgumentException`) implies the base `IsEnum` already resolves true for an IL enum on
the target framework -- but making it explicit is correct, cheap, and removes any
doubt. (If, at apply time, the stash-toggle shows an `ArgumentException` rather than
the NIE, the `IsEnum` override is the lever -- it is included pre-emptively.)

### D4: `GetEnumUnderlyingType` via the existing `ILType` enum branch

Return `type.TypeForCLR` (for an enum, `ILType.TypeForCLR` already returns
`enumType.TypeForCLR`, the underlying CLR primitive -- `ILType.cs:1966-1970`). This
matches `RuntimeType.GetEnumUnderlyingType` returning the `value__` field's type. The
underlying primitive is guaranteed non-null once `InitializeFields` has run (the
`TypeForCLR` getter lazy-inits `enumType`).

## Risks / Trade-offs

- **[Member enumeration includes a compiler-generated `<PrivateImplementationDetails>`
  or nested type field]** -> Mitigation: filter on `IsLiteral && HasConstant` (a literal
  constant field is precisely an enum member); compiler helpers are not literals. Also
  guard `if (!type.IsEnum) throw new ArgumentException(...)` to match the base contract
  for a misuse on a non-enum.
- **[Ordering / value collisions]** -> `Enum.GetValues`/`GetNames` return members in
  declaration (Cecil) order; the framework does not sort. We enumerate Cecil fields in
  declaration order, matching. Duplicate values (aliases) are returned as-is, matching
  the framework. No special handling needed.
- **[Legacy-neutrality]** -> The change is purely additive in shared code: overrides
  that currently fall through to a bare throw now return data. A non-enum IL type is
  unaffected (`IsEnum` false; the `GetEnum*` methods throw `ArgumentException` only
  when called -- they are never called by the happy path). Verify with the documented
  Legacy 17-failure baseline after the probe is added.
- **[Probe must FAULT]** -> The pre-fix failure is a THROW (the bare NIE from
  `System.Type.GetEnumValues()`), so the probe faults loudly on HEAD by construction.
  After the fix, assert the exact values / names / underlying type and throw on a
  mismatch (child-1/child-2 discipline -- a wrong-value probe must also fail).

## Depth assessment

**TRACTABLE -- a focused wrapper-method implementation.** ~50-70 lines in a single
file (`ILRuntime/Reflection/ILRuntimeType.cs`), all shared reflection code. No JIT,
no optimizer pass, no object model, no new opcode, no binding change. The metadata
source (Cecil `FieldDefinition.Constant`) is already in use by `ILRuntimeFieldInfo`.
The `neo-bare-nie` D3 framing ("an IL-enum-Type-representation investigation") made it
sound deep; on inspection it is four well-specified `System.Type` virtual overrides
with an obvious, precedent-backed implementation. DO NOT defer.
