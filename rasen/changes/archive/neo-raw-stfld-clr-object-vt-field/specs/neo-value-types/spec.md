# Spec Delta — neo-value-types (neo-raw-stfld-clr-object-vt-field)

## MODIFIED: Raw Stfld on a CLR value-type field whose owner is a CLR-object struct field

### Requirement
When the Neo `ExecuteNeo` raw `Stfld` handler (`OpCodeREnum.Stfld`, reached for
a field whose declaring type is a CLRType) decodes a CLR value-type owner byref
and the owner's `objIdx >= 0` with `mStack[objIdx]` being a non-Array, non-IL-
instance CLR REFERENCE object, the handler SHALL perform the field write by a
box/mutate/unbox ONE LEVEL UP on the containing object:

1. Read the struct field: `NeoReadClrObjectField(appdomain, mStack[objIdx],
   off)` where `off` (the byref's +4 half) IS the struct field's
   `FieldInfo.GetHashCode()` on the containing CLRType -- yielding the boxed
   current struct.
2. Reflection-write the leaf field: `f.SetValue(boxedStruct, value)` where `f`
   is the leaf field's `FieldInfo` (resolved from the struct's CLRType) and
   `value` is the source boxed by the field's CLR type category.
3. Write the mutated struct back: `NeoWriteClrObjectField(appdomain,
   mStack[objIdx], off, boxedStruct)`.

A null containing object SHALL throw `NullReferenceException`. An IL-instance
owner (`ILTypeInstance` / `CrossBindingAdaptorType`, F-10 ManagedObjects
storage) SHALL throw a tagged `NotImplementedException` naming the deferred
shape (separate sibling). The change is Neo-gated (`#if ENABLE_NEO_MODE`) and
Legacy-neutral by construction.

### Scenario: direct assignment to a primitive field of a CLR-struct field of a CLR object
- WHEN IL code executes `obj.StructField.primitiveField = N` where `obj` is a
  CLR reference object, `StructField` is a CLR-struct field of `obj`, and
  `primitiveField` is a primitive field of the struct (CIL `ldflda StructField;
  stfld primitiveField`),
- THEN the Neo runtime SHALL persist `N` into `primitiveField` of the struct
  stored in `obj.StructField`, observable by a host-side reflection read of
  `obj.StructField.primitiveField`.

### Scenario: field preservation across separate writes
- WHEN multiple separate raw `Stfld` writes target DIFFERENT primitive fields of
  the SAME struct field on the SAME CLR object,
- THEN each write SHALL preserve the values written by the prior writes (the
  box/mutate/unbox reads the current struct before mutating; it SHALL NOT
  recreate the struct from default).

### Scenario: no regression of sibling owner shapes
- The frame-local struct owner (`objIdx == -1`), the array-element owner
  (`mStack[objIdx] is Array`), and the CLR-reference-type declaring-type owner
  (the `else` Area-4d branch) SHALL continue to behave as before (child-4 /
  child-19 / child-9 respectively).
