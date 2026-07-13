# Design: neo-enum-cluster-residual

## The IL enum box representation (shared context)
Under Neo, an IL enum value boxed to `object` is an `ILEnumTypeInstance`
(`ILTypeInstance.cs:91`, internal). Its `byte[] fields` (= `Primitives`) holds the
underlying value. There is NO real `System.Type`/`System.Enum` for an IL enum -- the
boxed form is this wrapper, never a CLR `System.Enum`. Three call sites assumed the
opposite and broke.

## Root B -- boxed-enum Equals/GetHashCode reference equality (Test30/32/33)
- **Mechanism:** `TestClass2._testValue` is `object = TestEnumFlag.Feature3` (boxed
  via the Box opcode arm -> `new ILEnumTypeInstance`). `_testValue.Equals(arg)` is
  `callvirt Object::Equals(object)` -> the autogen redirect `Equals_3_Neo`
  (`System_Object_Binding.cs`) reads the receiver via `ReadNeoReference` (the
  `ProjectNeoClrRefSlot` helper SKIPS projection for an ILEnumTypeInstance) and calls
  `instance.Equals(obj)` -> virtual dispatch on the CLR object lands in
  `ILTypeInstance.Equals(object)`. The arg `Feature3` is boxed separately at the call
  site -> a SECOND `ILEnumTypeInstance`.
- **The bug:** `ILTypeInstance.Equals` has an `ILEnumTypeInstance` value-equality
  branch, but the entire branch is `#if !ENABLE_NEO_MODE`. Under Neo it falls to
  `return base.Equals(obj)` (reference equality) -> two distinct boxed enums are
  never equal -> false -> the test's `throw` fires. Test33 (`object.Equals(a,b)`) is
  the same path via the static `Equals_4_Neo` redirect, which internally calls
  `a.Equals(b)`.
- **Fix:** add `#if ENABLE_NEO_MODE` `Equals` + `GetHashCode` overrides ON
  `ILEnumTypeInstance` (`ILTypeInstance.cs`). Equals: value-compare `type` identity +
  the `byte[] fields` byte-for-byte. GetHashCode: hash the underlying value (so
  Equals/GetHashCode stay consistent for enum-valued dict keys; mirrors Legacy's
  `fields[0].Value.GetHashCode()`). Overriding on the derived class covers EVERY
  dispatch path (redirect / reflection / direct), since the boxed receiver IS an
  ILEnumTypeInstance at the CLR level.

## Root C -- constrained.callvirt on an IL enum boxes to the wrong type (Test11)
- **Mechanism:** `enumValue.ToString()` lowers to `constrained. TEnum; callvirt
  Object::ToString`. The Neo constrained-IL-value-type box path
  (`ILIntepreter.Neo.cs`, the `constrainedType is ILType ilBoxType && ilBoxType.IsValueType`
  arm) created `ilBox = ilBoxType.Instantiate(false)` -- a PLAIN `ILTypeInstance` --
  and dispatched Object.ToString on it -> `ILTypeInstance.ToString` returns the
  type's FULL NAME. (An IL enum IS a value type: `ILType.IsValueType` returns
  `definition.IsValueType` = true.) The asymmetry that revealed it: `$"{enumValue}"`
  WORKED because interpolation uses the Box opcode arm (`new ILEnumTypeInstance`,
  correct ToString), while direct `.ToString()` used the constrained runtime path.
- **The bug:** an IL enum's constrained-callvirt box must produce an
  `ILEnumTypeInstance` (value-name ToString / value Equals), not a plain
  `ILTypeInstance`.
- **Fix:** in that arm, branch `if (ilBoxType.IsEnum)` -> `new ILEnumTypeInstance`
  + copy the underlying bytes (`GetPrimitiveSize(FieldTypes[0])` + `CopyBlock` from
  `frameBase + thisByteOff`), mirroring the Box opcode arm (~4157). The non-enum
  IL-VT path is unchanged. `ilBox.Boxed = true; boxedReceiver = ilBox;` is shared.
- **Also a precondition for Test22:** Test22's `constrained.callvirt CompareTo`
  receiver was boxed here too (the original error said `ILTypeInstance`, not
  `ILEnumTypeInstance`). After Root C it becomes an `ILEnumTypeInstance`, which Root
  A then handles.

## Root A -- autogen Enum binding stubs cast IL enum to System.Enum (Test20, Test22)
- **Mechanism:** `System_Enum_Binding.HasFlag_2_Neo` / `CompareTo_4_Neo` do
  `(System.Enum)ILIntepreter.ReadNeoReference(...)`. A boxed IL enum is an
  `ILEnumTypeInstance`/`ILTypeInstance`, not a `System.Enum` -> InvalidCastException.
  (Test20's receiver arrived via the Box opcode arm -- already an ILEnumTypeInstance;
  Test22's via the constrained path -- a plain ILTypeInstance until Root C.)
- **Fix:** hand-port both stubs (the recurring "stale autogen Neo stub" defect class
  -- child-28 precedent). Detect an IL enum via the PUBLIC surface
  (`ILTypeInstance` + `.Type.IsEnum` + `.Primitives`; `ILEnumTypeInstance` itself is
  internal to ILRuntime and not reachable from the binding assembly) and compute the
  result directly on the underlying value bits -- no real `System.Enum` exists for an
  IL enum:
  - `NeoEnumRawLong(byte[])` -- sign-extended long (1/2/4 switch + 8-byte default).
  - HasFlag: same type? else false; `(a & b) == b` (bitwise, value-bit-correct).
  - CompareTo: same type? else throw ArgumentException (mirrors framework); signed
    `a.CompareTo(b)` (correct for signed underlying; HasFlag is bit-only so the
    signed/unsigned edge only touches CompareTo of large unsigned values, untested).
- The CLR-enum fallback (`((System.Enum)...)`) is retained for a real boxed
  `System.Enum` (unreachable for IL enums, but keeps the stub correct if a CLR enum
  ever flows through).

## Why three fixes and not one
The unifying theme is "a boxed IL enum is not interchangeable with a real
System.Enum under Neo", but it manifests at three DIFFERENT sites (the Object.Equals
override, the autogen Enum binding stubs, the constrained-callvirt box path). No
single edit covers all three; each is a small, surgical, Neo-gated change.

## Discriminators / blast radius
- Root B: override only fires on an `ILEnumTypeInstance` receiver (`obj is
  ILEnumTypeInstance`); plain ILTypeInstance / CLR objects use `base.Equals`.
- Root C: `if (ilBoxType.IsEnum)` -- enum-only; the IL-struct path is in the `else`.
- Root A: `ILTypeInstance && Type.IsEnum` detection; CLR enums use the original cast.
All three are additive inside `#if ENABLE_NEO_MODE` (or file-gated) -> Legacy-neutral.
