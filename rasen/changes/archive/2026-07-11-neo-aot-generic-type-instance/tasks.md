# Tasks — neo-aot-generic-type-instance

**Date:** 2026-07-11  **Capability:** neo-optimizer (AOT)  **Status:** DONE

- [x] **1. Reproducer.** Add `TestCases/NeoStep25CecilFreeGenFieldProbe.cs`: an IL
  class with generic-instance instance field types — `List<int>` (CLR arg),
  `List<string>` (CLR ref arg), `List<NeoStep25GenFieldItem>` (IL arg),
  `Dictionary<int,string>` (two CLR args) — + parameterless wrappers that Add +
  return Count / read an element. Plus the top-level `NeoStep25GenFieldItem` IL
  ref type (the IL generic arg).
- [x] **2. Capstone.** Add `NeoStep25CecilFreeGenFieldCheck.cs` (host-side,
  DEBUG+Neo): compile the probe + Item type to `.neo` in A (Cecil), Cecil-free
  load into FRESH B per cell, invoke each wrapper, assert == known-expected (A
  JIT reference). The F cell probes `fieldTypes[i]` DIRECTLY via `GetField(name)`
  (the observable gap surface — the functional wrappers pass on HEAD because a
  reference field is reference-slotted + callvirt operand is self-typed).
- [x] **3. CLI mode.** Wire `NeoStep25CecilFreeGenField` in `ILRuntimeTestCLI/
  Program.cs` (mirrors the child-8 `NeoStep25CecilFreeGeneric` block).
- [x] **4. Confirm gap on HEAD.** F cell FAILS on HEAD (0/4 field types
  non-null; `intItems=NULL; strItems=NULL; ilItems=NULL; map=NULL`). Functional
  wrappers PASS on HEAD (reference-slotted + self-typed callvirt). DIAG
  instrumentation confirmed `info.Name='' IsGI=True resolved=NULL`.
- [x] **5. Root cause.** `ResolveNamedIType` (ILType.cs, `#if ENABLE_NEO_MODE`)
  read ONLY `info.Name`; the HybridPatch serializer never sets `Name` for a
  generic-instance `TypeReferencePatchInfo` (sets `ElementType` +
  `GenericArguments`). Cecil path handled it via `GetType(TypeReference, ...)`
  GenericInstanceType branch — the Cecil-free path had no counterpart.
- [x] **6. Fix.** Extend `ResolveNamedIType` to handle `IsGenericInstance`
  (resolve `ElementType` + each `GenericArguments[i].Value` recursively, then
  `MakeGenericInstance`) + `IsArray` (`MakeArrayType(1)`) + `IsByReference`
  (`MakeByRefType`). Mirrors the Cecil path. Neo-gated, additive, no `.neo`
  format change. Single load-bearing site (functional field access was never
  broken; `newobj` field-init resolves via its own operand).
- [x] **7. Stash-toggle.** Stash `ILType.cs` -> F cell FAILS (11/12); pop ->
  12/12 PASS. Load-bearing confirmed.
- [x] **8. Regression.** NeoStep25CecilFreeGeneric 15/15 (child-8 held); NeoStep
  smoke 289/0/0; NeoStep25 11/0; NeoStep25LoadExec 28/28 (no `.neo` format
  regression). Legacy-neutral: plain `Debug` build 0 errors.
