# neo-optimizer delta: neo-raw-ldfld-stfld-cluster-e

## ADDED

### Requirement: TypeSpecializeNeoOpcodes seeds every in-frame value-type producer's dest register

`TypeSpecializeNeoOpcodes` MUST seed `registerTypes[dest]` with the producing
value-type's `ILType` for EVERY opcode whose dest register holds an in-frame
IL value type (flat managed bytes, not an mStack index). The typed
`Ldfld_*`/`Stfld_*` -> `_Inline` rewrite (`TryRewriteFieldAccessForInline`)
keys SOLELY on `registerTypes[ownerReg]` being an IL value type; an unseeded
in-frame-VT producer defeats the rewrite, the plain heap arm runs, and
`GetNeoILInstance` misreads the struct's first field-bytes as an mStack index
(NRE) or a boxed CLR object (NIE).

This requirement explicitly covers at minimum:
- `Ldloca`/`Ldloca_S` (address of a struct LOCAL) -- already seeded.
- `Ldarga`/`Ldarga_S` (address of a struct PARAMETER) -- MUST seed the byref
  dest with the source param's IL value type (mirrors `Ldloca`).
- `Ldflda` whose source is an in-frame IL value type -- already seeded.
- `Ldfld_Value` (whole-IL-VT field LOAD) -- MUST seed the dest (Register1)
  with the field's ILType (resolved from `Operand4` = field-type hash); the
  dest holds an in-frame copy of the field for BOTH a heap and an in-frame
  owner.

A primitive / reference / enum parameter or field is not an IL value type and
MUST NOT be seeded by these cases (a `ref int` byref stays untyped).

#### Scenario: a struct parameter field store via ldarga
- WHEN a method `void F(S a) { a.field = v; }` (S an IL value type) is
  JIT-lowered to `ldarga.s rDst, rParam; <load v>; stfld.* rDst, ...`
- THEN `TypeSpecializeNeoOpcodes` seeds `registerTypes[rDst]` with S's ILType
- AND the `stfld.*` is rewritten to its `_Inline` variant
- AND the runtime writes the field at `frameBase + rDst_offset + fieldOffset`
  (no `GetNeoILInstance` call, no NRE).

#### Scenario: a nested value-type field load via ldfld.value
- WHEN `a.C.x` (C is an IL value-type field of `a`) is JIT-lowered to
  `ldfld.value rC, rA, ...; ldfld.* rC, rC, ...`
- THEN `TypeSpecializeNeoOpcodes` seeds `registerTypes[rC]` with C's ILType
  (from the `ldfld.value` Operand4)
- AND the following `ldfld.*` is rewritten to its `_Inline` variant
- AND the runtime reads the field from the in-frame copy (no NRE).
