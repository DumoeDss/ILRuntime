# Proposal: neo-il-instance-clr-struct-field-f10

## Why
An IL instance has a field whose TYPE is a CLR struct (e.g.
`class ILClass { public TestVector3 V; }`). Under the Neo object model a
CLR-struct field of an IL instance is stored as a reference slot holding the
BOXED struct at `ManagedObjects[ReferenceOffset]` (NOT flat Primitives bytes).
`ldflda <clrStructField>` on the IL instance produces the byref
`(objIdx, ReferenceOffset | NeoF10ByrefOffsetFlag)` (the F-10 byref, set by the
ldflda `clrStructFieldMarker` branch at `ILIntepreter.Neo.cs:2092-2106`).

A following LEAF-field access on that struct -- `obj.V.X = value` (raw `Stfld`)
or `var x = obj.V.X;` (raw `Ldfld`) -- previously hit a tagged F-10 NIE in the
raw Stfld/Ldfld value-type-owner arm (`ILIntepreter.Neo.cs:4601` Stfld /
`:4364` Ldfld): the arm NIE'd whenever the owner `mStack[objIdx]` was an
ILTypeInstance / CrossBindingAdaptorType. child-27 fixed the CLR-OBJECT owner
(box/mutate/unbox via NeoReadClrObjectField/NeoWriteClrObjectField); child-29
the raw-Ldfld CLR-object read; F-10 is the IL-INSTANCE owner (the struct lives
in ManagedObjects, a sibling storage). child-27/29 explicitly DEFERRED it.

Confirmed failing on HEAD (full-smoke baseline 74 failed):
- `StructTests.StructTest7` -- `Neo raw Stfld: ... Field X on ...TestVector3`
  (the F-10 NIE at :4601).
- `TestValueTypeBinding.UnitTest_10051` -- `Neo raw Stfld: ... Field x on
  ...Fixed64Vector2` (the F-10 NIE at :4601).

## What changes
- Interpreter-only, Neo-gated (=> Legacy-neutral by construction). Mirror the
  existing F-10 consumers (the Stobj arm `ILIntepreter.Neo.cs:6199-6211` and
  `NeoMarshalByrefFieldToSlot:470-514`) which already route the F-10-flagged
  byref to the IL instance's ManagedObjects-boxed struct.
- In the raw `Stfld` value-type-owner arm and the raw `Ldfld` value-type-owner
  (byref-owner) arm: when the owner `mStack[objIdx]` is an ILTypeInstance /
  CrossBindingAdaptorType, route to a box/mutate/unbox ONE LEVEL UP via the IL
  instance's `ManagedObjects[refOff & ~flag]` (read boxed struct, mutate the
  leaf field via `FieldInfo.SetValue`, write back) -- the IL-instance-storage
  sibling of child-27's heap-CLR-object-field fix. Do NOT route through
  NeoReadClrObjectField/NeoWriteClrObjectField (those re-resolve the OWNER's
  CLRType and would treat `off` as a field hash on the IL instance).

## Soundness (the load-bearing detail)
- The target-TYPE check (`target is ILTypeInstance || CrossBindingAdaptorType`)
  MUST be the discriminator, NOT the F-10 flag bit. The CLR-OBJECT owner's byref
  offset is the struct `FieldInfo.GetHashCode()` (child-27), which can have the
  flag bit (`0x40000000`) set by chance -- testing the flag first would mis-route
  a CLR object into the F-10 branch (InvalidCast to CrossBindingAdaptorType).
  This is exactly what the Stobj arm (`:6199`, type-first via `GetNeoILInstance`
  after excluding CLR-object/Array) and `NeoMarshalByrefFieldToSlot` (`:470`,
  `target is ILTypeInstance` first) already do. (An initial flag-first draft
  regressed the 4 child-27/29 NeoStep probes `NeoStepRaw(Ldfld|Stfld)ClrObjVtField`
  whose struct-field hash carries the flag bit; the type-first structure fixed
  it -- NeoStep 391/0.)
- An IL instance reached in this arm is ALWAYS the F-10 shape (a CLR-struct
  field of the IL instance); the raw Stfld/Ldfld declaring type is a CLRType,
  so an IL-struct field would use Stfld_Value/Ldfld_Value, not the raw opcodes.
- `off & ~flag` recovers the ReferenceOffset (a small ManagedObjects slot index,
  never carries bit 0x40000000). A null slot (uninitialized default) is seeded
  with `Activator.CreateInstance(ct.TypeForCLR)` before mutating.
- `FieldInfo.SetValue` on a boxed value type mutates it in place (child-19/27
  precedent); the explicit ManagedObjects write-back is required for the
  null-seeded case and a safe no-op otherwise.

## Out of scope (surfaced follow-ups)
- `UnitTest_10051` progresses PAST the F-10 NIE (the SetPos WRITE now works)
  but still fails its `list[0].V2.x.RawValue == 999` assertion. That readback
  hits a SEPARATE pre-existing Neo gap: reading a struct field's PROPERTY via
  `.x.RawValue` on a Fixed64Vector2 returns the wrong value with NO F-10
  involvement (a CONTROL `new Fixed64Vector2(555,0).x.RawValue` faults the same
  way -- a constrained-callvirt / nested-struct-field-property read gap). NOT
  F-10. Candidate follow-up: `neo-struct-field-property-read`.
- The nested-ldflda-on-byref gap (`ldflda Fv; ldflda x` for a method call on a
  struct field) that child-27 surfaced -- also blocks a direct
  `h.Fv.x.RawValue` readback; same family.

## Capability home
`neo-value-types` (owns the in-frame/heap VT storage + the Stfld/Ldfld arms;
this change extends child-4/27/29's raw Stfld/Ldfld value-type-owner arms).

## Verify (truth = full-smoke number)
- Name-filter: StructTest7 PASS after fix (was the F-10 NIE on HEAD). Confirmed.
- FULL SMOKE: 74 -> 73 (StructTest7 flipped; F-10 NIE count 0; child-27/29
  probes intact; my 3 F-10 probes pass). UnitTest_10051 residual = the separate
  `.x.RawValue` gap (above).
- NeoStep 388 -> 391/0 (+3 F-10 probes, 0 regression).
- Legacy-neutral: change is inside `ExecuteNeo` (`#if ENABLE_NEO_MODE`).
