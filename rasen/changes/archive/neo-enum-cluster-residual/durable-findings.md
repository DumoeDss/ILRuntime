# Durable findings: neo-enum-cluster-residual (Wave-2 C3)

## Headline
- Full Neo smoke **101 -> 95** (-6). The 6 residual EnumTest failures flipped green;
  0 EnumTest failures remain. NeoStep 388/0 (no regression). Legacy-neutral.

## The IL-enum-is-not-System.Enum defect class (3 sites, all Neo-gated)
A boxed IL enum is an `ILEnumTypeInstance` (internal, in ILRuntime) wrapping a
`byte[]` of the underlying value -- there is NO real `System.Enum`/`System.Type` for
an IL enum. Three Neo sites wrongly assumed otherwise:
1. **`ILTypeInstance.Equals`/`GetHashCode`** -- the Legacy enum value-equality branch
   is `#if !ENABLE_NEO_MODE`. Under Neo, `base.Equals` = reference equality. Fix:
   override Equals+GetHashCode on `ILEnumTypeInstance` (Neo). Covers the Object.Equals
   redirect (Equals_3_Neo) AND static Object.Equals (Equals_4_Neo) AND reflection,
   because `Equals_3_Neo` calls `instance.Equals(obj)` which virtually dispatches to
   the override. (EnumTest Test30/32/33.)
2. **The constrained-IL-VT box path** (`ILIntepreter.Neo.cs`) boxed an IL enum via
   `Instantiate(false)` -> a plain `ILTypeInstance` whose ToString returns the type
   full name. Fix: `if (ilBoxType.IsEnum)` -> `new ILEnumTypeInstance` + copy bytes
   (mirror the Box opcode arm). (EnumTest Test11; also the precondition for Test22's
   CompareTo receiver, which arrived here as a plain ILTypeInstance.)
3. **The autogen `System_Enum_Binding` Neo stubs** (HasFlag_2_Neo / CompareTo_4_Neo)
   cast the boxed enum to `System.Enum`. Fix: hand-port (child-28 precedent) -- detect
   an IL enum via the PUBLIC surface and compute on the underlying bits. (Test20/22.)

## Reusable facts for future children
- **The autogen-binding accessibility wall:** `ILEnumTypeInstance` is INTERNAL to
  ILRuntime, so a stub in `ILRuntimeTestBase/AutoGenerate/*` CANNOT reference it. Use
  the public surface: `raw is ILTypeInstance ili && ili.Type != null && ili.Type.IsEnum`
  + `ili.Primitives` (the underlying-value byte[] for an enum). This is the pattern
  for ANY autogen stub that must special-case an IL enum.
- **`$"{enumValue}"` vs `enumValue.ToString()` lower differently** under Roslyn: the
  interpolation uses the Box opcode (correct `ILEnumTypeInstance`); a direct
  `.ToString()` uses `constrained.callvirt` (the runtime constrained box path). A bug
  in ONLY the constrained path shows up as "interpolation prints the value, ToString
  prints the type name" -- a tell-tale for the Root-C box-type mismatch.
- **`ProjectNeoClrRefSlot` deliberately does NOT project an ILEnumTypeInstance**
  (`obj is ILTypeInstance ili && !(ili is ILEnumTypeInstance)`). So an IL enum
  receiver reaches Object-method bindings as a raw `ILEnumTypeInstance`, and the
  binding's `instance.Equals(obj)` / `.ToString()` virtually dispatches to the
  `ILEnumTypeInstance` override -- which is WHY overriding Equals/GetHashCode/ToString
  on the derived class is the correct single fix point.
- **`ILType.IsEnum` is public** (`ILType.cs:2033`); `ILType.IsValueType` returns
  `definition.IsValueType` (= true for an enum). So an IL enum satisfies BOTH the
  constrained-IL-VT box arm's `IsValueType` gate and an `IsEnum` discriminator.

## Scope / out-of-scope
- CompareTo of large UNSIGNED enum values (e.g. `ulong` with the high bit set) is
  sign-extended by `NeoEnumRawLong` -- may mis-compare. Not exercised by any test;
  documented in the helper. A future child can consult `Type.TypeForCLR` to read the
  exact unsigned width if a test surfaces it.
- Legacy `#else` branches in `System_Enum_Binding` are untouched (Legacy already
  works via `CheckCLRTypes`); only the `#if ENABLE_NEO_MODE` Neo stubs were ported.

## Files
- `ILRuntime/Runtime/Intepreter/ILTypeInstance.cs` (+46: ILEnumTypeInstance
  Equals + GetHashCode Neo overrides).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (+27/-5: enum branch
  in the constrained-IL-VT box path).
- `ILRuntimeTestBase/AutoGenerate/System_Enum_Binding.cs` (+66/-6: NeoEnumRawLong
  helper + hand-ported HasFlag_2_Neo + CompareTo_4_Neo).
