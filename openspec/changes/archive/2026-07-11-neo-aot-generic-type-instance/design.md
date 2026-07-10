# Design — neo-aot-generic-type-instance

**Date:** 2026-07-11  **Capability:** neo-optimizer (AOT)  **Wave:** completion-3, child-8 follow-up (generic-TYPE-instance counterpart)
**Status:** DONE (re-audit: a SPECIFIC resolution miss in `ResolveNamedIType`, NOT a deep ILType-layout rework — the 7-for-7 lesson holds)

## The gap (re-audited)
A Cecil-free IL class whose INSTANCE (or STATIC) FIELD TYPE is a generic
instantiation — `List<int>`, `List<string>`, `List<ILType>`, `Dictionary<K,V>`
— had the field TYPE resolve to NULL at Cecil-free load. The serialized
`TypeReferencePatchInfo` for a generic instance carries `IsGenericInstance=true`
+ `ElementType` (the generic def) + `GenericArguments[]` (the type args), but
NOT `Name` (the HybridPatch serializer `Create(TypeReference)` /
`Create(IType)` never sets `Name` for the generic-instance branch). The
Cecil-free ILType factory (`CreateFromNeoRecord`) resolved each field type via
`ResolveNamedIType(info)`, which read ONLY `info.Name` — so a generic-instance
field type resolved to NULL.

This is the generic-TYPE-instance counterpart to child-8's generic-METHOD
instance (Cecil-free generic machinery: `BuildAndRegisterGenericDefShell`,
`MakeGenericMethodShell`, shell-aware `ResolveVariableType`). Child-8 covered
the method surface; the field-type surface was an unaddressed miss.

## Re-audit verdict: SPECIFIC, NOT deep (the 7-for-7 lesson holds)
Two probes confirmed this is a single resolution miss, NOT an ILType-layout
rework:

1. **The functional wrappers PASS on HEAD.** A `List<int>` field's `.Add(10)`
   + `.Count` round-trips Cecil-free even with `fieldTypes[i] == NULL` — because
   a REFERENCE-typed generic-instance field is reference-SLOTTED (the field
   access uses the recorded `ReferenceOffset`, NOT the field type) + the
   `callvirt List.Add` operand resolves via its OWN declaring type in the
   MethodRef token (independent of the field type). So the functional cells
   alone do NOT expose the gap.

2. **A VALUE-TYPE generic-instance field (`KeyValuePair<int,int>`) FAILS even in
   the Cecil-loaded JIT reference** (returns 0/6, not the expected 11) — that is
   an ENGINE-level ldfld.vt/stfld.vt-of-CLR-VT-generic-instance gap, NOT a
   Cecil-free regression (out of scope; documented). It proved the functional
   surface is NOT the Cecil-free gap.

3. **The OBSERVABLE Cecil-free gap is reflective field-type query.** `ILType.
   GetField(name, out _)` returns `fieldTypes[i]` directly — so a reflective
   field-type query returned NULL on a Cecil-free load (vs a non-null generic
   instance on the Cecil-loaded JIT path). The F cell probes this directly.

DIAG instrumentation confirmed every generic-instance field type resolved NULL
on HEAD (`info.Name='' IsGI=True resolved=NULL` for intItems/strItems/ilItems/map).

## The fix (1 engine file, Neo-gated, additive)

### `ILType.cs` — `ResolveNamedIType` now handles the generic-instance ( + array + byref) shape

The Cecil-free field-type resolver (`#if ENABLE_NEO_MODE`, used by
`CreateFromNeoRecord` instance + static field-type install AND the
`ReResolveCrossAssemblyRefs` cross-assembly re-resolve at :1694/:1711) now
mirrors the Cecil path's `appdomain.GetType(field.FieldType, this, null)` shape
handling:
- `IsGenericInstance`: resolve `info.ElementType` (the generic def, recursively
  -- it is itself a named `TypeReferencePatchInfo`, e.g.
  `System.Collections.Generic.List`1`) + each `info.GenericArguments[i].Value`
  (recursively), then `defType.MakeGenericInstance(args)`. The generic PARAM
  key is the recorded `GenericArguments[i].Key` (the def's generic-param name,
  e.g. "T"; informational for IL defs + unused by `CLRType.MakeGenericInstance`
  which keys by position).
- `IsArray`: resolve the element type + `MakeArrayType(1)`.
- `IsByReference`: resolve the element type + `MakeByRefType()`.
- plain named type: unchanged (LoadedTypes / `GetType(name)`).

`GetType("System.Collections.Generic.List`1")` resolves the open CLR def via
the live-assembly CLR fallback (`System.AppDomain.CurrentDomain.GetAssemblies()`
-> `Assembly.GetType` resolves an open generic by its backtick name; `List<>`
lives in the always-present BCL). `CLRType.MakeGenericInstance` then builds
`List<int>` etc. The IL generic arg (`List<NeoStep25GenFieldItem>`) resolves
via LoadedTypes (the .neo loaded the Item type). NO `.neo` format change, NO
new V-bump, NO writer/reader change — the data was already there.

### Why no other site
The functional field access (`ldfld.ref` / `stfld.ref` / `callvirt` on a
reference field) was NEVER broken (reference-slotted + callvirt operand is
self-typed). The `.ctor`'s `new List<int>()` newobj resolves via its OWN
operand (the constructed type in the token), NOT via the field type. So the
ONLY load-bearing change is `ResolveNamedIType`. `NaturalSizeOfFieldType`
(reference field -> 4) was already null-tolerant (`if (ft == null) return 1`),
so a null generic-instance field type did not NRE the alignment calc.

## Scope notes / out of scope
- **A VALUE-TYPE generic-instance field** (`KeyValuePair<int,int>`, a CLR VT
  generic instance stored INLINE) fails the ldfld.vt/stfld.vt path EVEN in the
  Cecil-loaded JIT reference (an engine-level gap, NOT a Cecil-free
  regression). The fix makes `fieldTypes[i]` resolve correctly for it, but the
  VT-move engine path is separately broken. Out of scope; the probe uses
  reference-typed generic-instance fields (`List<>`, `Dictionary<,>`) + a
  reflective field-type cell. A `KeyValuePair<int,int>` VT-field probe is
  documented as the engine-gap follow-up.
- **A generic-parameter field type** (`class C<T> { T field; }` -- an IL type
  with its OWN generic params + a generic-param field) reaches `ResolveNamedIType`
  with `info.IsGenericParameter=true` + `Name="T"`. A Cecil-free ILType has no
  open generic def to bind "T" on, so it resolves to null (falls through). This
  is a deeper surface (an open-generic IL class Cecil-free); out of scope.
- **Nested generic instances** (`Dictionary<int, List<string>>`) resolve
  RECURSIVELY (the type-arg resolution recurses), so they work if each level's
  def + args resolve. Not separately probed (the recursive path is uniform).
- **MethodToken T-identity** (still rejects) -- unchanged from the T-identity
  follow-up.

## Verification
- **NeoStep25CecilFreeGenField capstone: 12/12** (the F field-type-resolution
  cell + 5 A-JIT references + compile + 5 B functional cells).
- **Stash-toggle (load-bearing):** stash `ILType.cs` -> F cell FAILS (11/12,
  the HEAD state, 0/4 field types non-null); pop -> 12/12 PASS.
- **NeoStep25CecilFreeGeneric (child-8) held: 15/15** (no method-surface
  regression).
- **NeoStep smoke: 289/0/0** (no regression). **NeoStep25: 11/0.**
  **NeoStep25LoadExec: 28/28.** (no `.neo` format regression — no V-bump.)
- **Legacy-neutral:** plain `Debug` build of `ILRuntime.csproj` 0 errors (the
  fix is entirely inside `#if ENABLE_NEO_MODE`).
