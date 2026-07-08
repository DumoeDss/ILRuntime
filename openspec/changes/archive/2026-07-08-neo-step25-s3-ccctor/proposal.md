# Proposal: neo-step25-s3-ccctor (S3-4 TRUE COMPLETION)

> Static `.cctor` seeding at Cecil-free AOT load. TRUE-COMPLETION: a type's static
> constructor runs when a `.neo` Cecil-free-loads, so a static field a `.cctor`
> writes reads back the `.cctor`-set value (not the default).

## Why

S3-2 (`2026-07-08-neo-step25-s3-cecil-free-load`) shipped the Cecil-free AppDomain
load (sub-surface 2) + the cross-AppDomain token-hash re-registration (sub-surface
3, APPROACH 1). It deliberately SEQUENCED sub-surface 4 -- the static `.cctor`
seeding + the per-static-field offsets -- as a non-goal (the S3-2 capstone probe
declares no static fields + no `.cctor`). The S3-2 spec delta states this explicitly
("Static .cctor seeding stays deferred", `2026-07-08-neo-step25-s3-cecil-free-load/
specs/neo-optimizer/spec.md:41-46`).

Today, on a Cecil-free-loaded type that DOES declare static fields + a `.cctor`:
- the `.cctor` NEVER runs (it is suppressed under `ENABLE_NEO_MODE` at `ILType.cs:
  203-207` and `:2110-2113`, both carrying a stale "TODO Step 7" -- Steps 7/12
  shipped the Stfld/Ldfld handlers long ago); and
- a static-field access would crash, because the Cecil-free `ILType` factory sets
  only the static TOTALS (`CreateFromNeoRecord` `ILType.cs:1292-1293`), leaving
  `staticFieldOffsets` / `staticFieldTypes` / `staticFieldMapping` NULL, and the
  `ILTypeStaticInstance` ctor reads `type.TypeDefinition.Fields` (`ILTypeInstance.cs:
  62`), which is NULL on a Cecil-free ILType.

This change closes sub-surface 4 so a Cecil-free-loaded type's `.cctor`-set static
state initializes correctly.

## What Changes

1. **Record:** the `NeoTypeDefRecord` gains a parallel `NeoFieldLayoutRecord[]
   StaticFields[]` (per-static-field name + type + offsets), alongside the existing
   instance `Fields[]`. This is a `.neo` Version bump (V2 -> V3). The
   `StaticCtorMethodRefIdx` field ALREADY exists in the record (recorded at
   `NeoAssemblyWriter.cs:883` via `BuildStaticCtorRef`, read at
   `NeoAssemblyReader.cs:248`) but is CURRENTLY UNUSED by the loader -- this change
   finally consumes it.

2. **Factory:** `ILType.CreateFromNeoRecord` installs the static-field layout
   (`staticFieldOffsets` / `staticFieldTypes` / `staticFieldMapping`) from the new
   `StaticFields[]`, RE-DERIVING the byte offsets the same way the Cecil
   `InitializeFields` static path does (`ILType.cs:2560-2601`), and tracks the
   `.cctor` as `staticConstructor` (from `rec.StaticCtorMethodRefIdx`, resolved by
   name + the `.cctor`-name match already in `CreateFromNeoRecord` at `:1349`).

3. **Static-instance ctor:** the `ILTypeStaticInstance` ctor
   (`ILTypeInstance.cs:50-74`) is taught the Cecil-free path (it MUST NOT read
   `type.TypeDefinition.Fields`, which is NULL on a Cecil-free type) -- it sizes
   the `byte[]` / `AutoList` from the static totals + per-field offsets already on
   the Cecil-free ILType, and skips the `InitialValue` replay (a `.neo` carries no
   raw initial-value byte arrays; the `.cctor` is the initializer).

4. **Seed at load:** `LoadNeoAssembly` runs each Cecil-free type's `.cctor`
   (resolved via `StaticCtorMethodRefIdx` -> the live `.cctor` ILMethod) AFTER the
   bodies are bound by `Attach`, via `appdomain.Invoke(cctor, null, null)` (the
   SAME call the Legacy path uses at `ILType.cs:209/2114`). A type with no `.cctor`
   (`StaticCtorMethodRefIdx == -1`) is skipped.

5. **Stale suppression:** the two `TODO Step 7` `.cctor`-suppression sites remain
   untouched on the Cecil ctor path (Legacy reference, `#if ENABLE_NEO_MODE` guard
   already there). This change does NOT lift that suppression for the Cecil-loaded
   same-AppDomain Neo path (that is a SEPARATE concern -- the capstone here is the
   Cecil-free load, which seeds explicitly at `LoadNeoAssembly`, NOT via the
   lazy `StaticInstance` getter).

## Capstone + Adversarial Gate

A dedicated probe (`TestCases/NeoStep25S3CctorProbe`) declares a static field +
a `.cctor` that writes a constant to it + a reader method that returns the field.
The host-side self-check (`NeoStep25CecilFreeLoadCheck`, extended) compiles it,
Cecil-free-loads the `.neo`, and asserts the reader returns the `.cctor`-set value
(not the default zero). The **adversarial** cell mutates the `.cctor` body's
`Ldc_I4` constant in an INDEPENDENT `model2` BEFORE load + asserts the read
reflects the MUTATED constant (proves the `.cctor` genuinely ran on the
deserialized body, not a Cecil/JIT fallback nor a default).

A green smoke does NOT prove the gate -- the body-mutation cell is mandatory
(the record's values all come from Cecil at serialize; a Cecil-fallback or a
default-read would pass a non-mutated capstone trivially).

## Scope Decision

**SHIP (SMALL).** The `.cctor` body IS in the `.neo` MethodDef table (the
`.cctor` is a non-generic method, force-compiled at `NeoCompiler.cs:330` +
enumerated via `GetConstructors()` at `:289`). The `StaticCtorMethodRefIdx` is
ALREADY recorded. The only format change is the parallel per-static-field layout
array (a Version bump). The seeding is a single `appdomain.Invoke(cctor, null,
null)` per type -- the Legacy-equivalent call. The static-instance ctor fix is a
guarded Cecil-free branch. All additive, Neo-only, Legacy-neutral. Sequence: none
of the remaining Neo deferred items are blocked by this.

## Impact

- Affected files: `NeoAssembly.cs` (record), `NeoAssemblyWriter.cs` (serialize
  static layout), `NeoAssemblyReader.cs` (deserialize), `ILType.cs`
  (`CreateFromNeoRecord` static install + `.cctor` tracking),
  `ILTypeInstance.cs` (`ILTypeStaticInstance` Cecil-free ctor branch),
  `AppDomain.cs` (`LoadNeoAssembly` `.cctor` seeding step), the probe
  (`TestCases/NeoStep25S3CctorProbe.cs`), the self-check
  (`NeoStep25CecilFreeLoadCheck.cs`), its CLI hook.
- `.neo` Version bump V2 -> V3 (additive + guarded; the Cecil-free loader rejects
  a V2 `.neo` for the static-field path; the same-AppDomain path ignores the new
  array).
- Regression risk: LOW. All new code is `#if ENABLE_NEO_MODE`. The Legacy
  `ExecuteR` path is byte-identical (the whole namespace compiles out of plain
  `Debug`). The Cecil ctor + lazy inits are UNCHANGED. Same-AppDomain S1/S2/S3
  loads ignore the new static-layout array (the live Cecil init provides it).
