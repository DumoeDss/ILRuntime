# Tasks -- neo-step25-s3-full-decoupling

> Implementation tasks for the S3 PARTIAL slice (sub-surface 1: ILType AOT-init
> rebuild + structural-equivalence self-check). Sub-surfaces 2/3/4/5 are DEFERRED
> (design.md dump-gate table). All engine edits Neo-only (`#if ENABLE_NEO_MODE`);
> the self-check is `#if ENABLE_NEO_MODE && DEBUG`. Legacy is the reference.

## 1. Probe readiness (OQ1)

- [x] 1.1 Read `TestCases/NeoStep25LoadProbe.cs`. Decide whether it declares 2+
      instance fields of differing widths + a base-class virtual + an interface
      impl (enough to make the layout + VTable comparison meaningful).
      -> The existing probe is a STATIC-only container (no instance fields, no
      virtuals, no interface) -> too thin. Added `TestCases/NeoStep25S3Probe.cs`.
- [x] 1.2 If the existing probe is too thin, ADD a dedicated small probe type
      (e.g. `TestCases/NeoStep25S3Probe`): 2+ instance fields of differing
      primitive widths (e.g. an int + a long + a ref field), a `virtual` method
      overriding a base, and an `interface` implementation. Keep it NON-NESTED
      + within the BCL-refs-only boundary (the S1/S2 constraint). Rebuild
      `TestCases/bin/Debug/netstandard2.1/TestCases.dll` (`dotnet build
      TestCases/TestCases.csproj -c Debug`).

## 2. The ILType layout + VTable rebuild builder (engine, Neo-only)

- [x] 2.1 In `ILRuntime/CLR/TypeSystem/ILType.cs`, behind `#if ENABLE_NEO_MODE`,
      add an internal static (or internal instance-on-ILType) builder that
      reconstructs, from a `NeoTypeDefRecord` + a Cecil-loaded `ILType` (for
      reference comparison) + the `NeoAssemblyModel`:
      - instance field layout: `TotalPrimitiveSize`, `TotalReferenceCount`, and
        a per-field `ILTypeFieldOffset[]` (from `record.Fields[]`).
      - `naturalAlignment` RE-DERIVED from the resolved field types (each
        `FieldRefIdx` -> `FieldRefTable` -> field type -> natural size; the max).
      - the Neo VTable `IMethod[]` slot array (resolve each
        `record.VTableMethodRefIdxs[i]` -> `MethodRef` -> live `IMethod`) +
        the re-derived slot-key map (each slot method's `SignatureString`).
      - the interface offset map (each `record.Interfaces[k]` ->
        `ResolveTypeRefToIType` + `VTableOffset` + `MethodSlotKeys` +
        `ClassSlotRemap`).
- [x] 2.2 The builder SHALL NOT install the rebuild on a live `ILType` (D1). It
      returns the rebuilt structs for the self-check to compare. Confirm the
      Cecil init path (`InitializeFields` / `BuildNeoVTable`) is byte-identical
      when the builder is not called.
- [x] 2.3 In `ILRuntime/Runtime/NeoAOT/NeoAssemblyLoader.cs`, add a small
      helper `ResolveVTableFromRecord(iltype, model, rec)` (D4) that resolves
      `VTableMethodRefIdxs[]` -> `IMethod[]`, reusing the existing
      `MatchMethod`. Interface entries reuse the existing
      `ResolveTypeRefToIType`. No change to the S1/S2 `Attach` flow.

## 3. The structural-equivalence + mutation self-check (DEBUG + Neo)

- [x] 3.1 In `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep25LoadExecCheck.cs`,
      ADD cells driven by the existing `NeoStep25LoadExec` hook:
      - **Structural-equivalence (layout):** rebuild the instance layout from
        the probe's deserialized `NeoTypeDefRecord`; assert field-by-field +
        totals + `naturalAlignment` EQUAL the Cecil-computed values.
      - **Structural-equivalence (VTable + interface map):** rebuild the VTable
        + slot-key map + interface offsets; assert slot-by-slot / key-by-key /
        interface-by-interface EQUALITY (OQ2 -- compare BOTH slots and keys).
      - **Mutation (load-bearing):** mutate a `PrimitiveOffset` (or swap two
        `VTableMethodRefIdxs`) in an INDEPENDENT deserialized record (`model2`)
        BEFORE rebuild; assert the rebuilt layout/VTable DIVERGES exactly
        where mutated. A rebuild that ignored the record would still equal
        Cecil -> this cell proves it reads the record.
- [x] 3.2 Use the existing `RecordCell` / `Result` reporting. Each cell prints
        `[NeoStep25] <name>: PASS` / `[FAIL] ...`. Assert via the value/divide
        path (no test-framework dependency).

## 4. Build + regression gates (CRITICAL -- always `-f net8.0`)

- [x] 4.1 `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`
      (0 errors). Build CLI with `Debug_Neo`; NEVER `TestCases` with `Debug_Neo`.
- [x] 4.2 `dotnet build TestCases/TestCases.csproj -c Debug` (rebuild the DLL
      after any probe edit in task 1.2).
- [x] 4.3 `NeoStep25LoadExec` capstone: the S1/S2 21/21 PLUS the new S3 cells,
      0 failed. The mutation cell returns the DIVERGENCE (a non-mutated rebuild
      would PASS equality -- the mutation cell proves the rebuild reads the
      record). -> 28/28 (21 S1/S2 + 7 S3: compile + TypeDef-located + layout /
      VTable / interface structural-equiv + field-offset + VTable-slot-swap
      mutation), 0 failed.
- [x] 4.4 NeoStep smoke (`Debug_Neo` + `useRegister=true`, filter `NeoStep`):
      215+/215+, 0 new failures (the change is additive; the probe type may add
      a few parameterless methods counted by the NeoStep filter). -> 218/0/1
      unchanged (the probe declares no static parameterless methods -> +0 tests).
- [x] 4.5 `NeoStep22SelfCheck` 55/55, `NeoStep23Roundtrip` 15/15,
      `NeoStep24CliRoundtrip` 5/5 -- UNCHANGED (the Step-22/23/24 layers are
      not modified).
- [x] 4.6 Legacy-neutral: plain `Debug` build of `ILRuntimeTestCLI` = 0 errors
      (the builder + helper + self-check compile out under plain `Debug`;
      `ILType` byte-identical to before). -> 0 errors; plain-Debug NeoStep filter
      218/8-pre-existing/1 identical WITH vs WITHOUT the probe type (the 8 are
      NeoStep6/13/14/15/16, unrelated to S3).

## 5. Sub-surface 5 stretch (OPTIONAL -- ship IFF a clean seam exists)

- [ ] 5.1 Probe the AppDomain CLR-resolution path: can a failing reference
      assembly be registered on the CLR side (`Assembly.LoadFrom(rp)`) so
      `appdomain.GetType(aqname)` resolves the `TestCLREnum` host enum?
      -> DEFERRED at apply: sub-surface 5 is a Step-24 CLI ergonomics concern
      (tangential to the S3 ILType-decoupling theme); the design dump-gate
      verdict was "lean DEFER, ship IFF a clean <=1-call seam exists". No apply
      budget spent probing it; no clean seam shipped.
- [ ] 5.2 IFF a <=1-call seam exists: add it to `NeoCompiler.Compile`
      (`NeoCompiler.cs:119-136`) + a `TestCLREnum`-style regression. ELSE:
      leave DEFERRED (note in the ship-log + `neo-deferred-items.md`).
      -> DEFERRED (the ELSE branch): recorded in `neo-deferred-items.md`
      STEP-25-PARTIAL (sub-surface 5) + the spec delta's deferral requirement.

## 6. Docs + spec honesty

- [x] 6.1 Update `.trae/documents/neo-deferred-items.md` STEP-25-PARTIAL row:
      S3 PARTIAL SHIPPED (sub-surface 1: ILType layout + VTable rebuild,
      structural-equivalence-proven); sub-surfaces 2/3/4 (and 5 if not shipped)
      remain deferred with the APPROACH 1 + Cecil-free-load + `.neo` Version
      bump notes.
- [x] 6.2 Update `.trae/documents/neo-handoff.md` HEAD + status line.
- [x] 6.3 Confirm the spec delta (`specs/neo-optimizer/spec.md`) is honest: the
      rebuild is the ONLY ILType-decoupling delivered; sub-surfaces 2/3/4/5 stay
      deferred (not promoted to "met"). -> Confirmed: the 2 ADDED requirements
      accurately describe the sub-surface-1 rebuild (builder does NOT install on
      a live ILType; naturalAlignment re-derived) + the sub-surface 2/3/4/5
      deferral. No promotion.

## Out of scope (DEFERRED -- recorded, NOT implemented)

- Sub-surface 2: Cecil-free AppDomain load (`LoadAssembly(Stream)` Cecil-free
  path + an ILType factory from `NeoTypeDefRecord`).
- Sub-surface 3: cross-AppDomain token-hash re-resolution (APPROACH 1: record
  the compile-time `GetHashCode()` per ref entry under a `.neo` Version bump;
  Approaches 2/3 REJECTED).
- Sub-surface 4: static `.cctor` seeding via `.neo` (needs the Cecil-free load
  + per-static-field offsets in the record).
- F-12 (parametrized `ILIntepreter.Run` / reference-type return): does NOT
  block this slice (host-side structural comparison; no Run-shim invocation).
