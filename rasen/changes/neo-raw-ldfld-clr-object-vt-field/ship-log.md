# Ship Log — neo-raw-ldfld-clr-object-vt-field (child 29)

**Change:** raw `Ldfld` READ of a field of a CLR-struct field of a CLR reference object (`x = obj.Struct.field`) —
the READ counterpart of child-27. COMPLETES the write/read pair for the CLR-object-struct-field shape AND the
write/read asymmetry for both the array-element (child-19/24) and CLR-object-struct-field (child-27/29) shapes.
**Capability:** `neo-value-types` (ADDED requirement).
**Pipeline:** small-feature (diagnose -> propose+apply -> verify -> review -> ship).
**Date:** 2026-07-13. **Branch:** `features/object-model-overhaul`. **Base HEAD:** `f1e5070c`.

## Diagnosis (silent corruption confirmed; marker mandatory)
`x = obj.Struct.field` (CIL `ldflda Struct(on obj); ldfld field`) silently corrupted under Neo — the raw-Ldfld
IsValueType arm reinterpreted the 8-byte byref `(objIdx, structFieldHash)` as the struct's first fields (HEAD:
`o.S.a` returned 4 = the owner's mStack index instead of 111; no crash, no NIE). Identical mechanism to child-24's
array-element read.

**A JIT marker is MANDATORY** (confirmed by review): the raw-Ldfld owner is EITHER flat bytes (`ldloc structByValue`)
OR a byref (`ldflda`/`ldelema`); a flat-bytes struct's first int can false-positive as an mStack objIdx pointing to a
real CLR object, AND the +4 half can coincidentally match a real `FieldInfo.GetHashCode()` on the wrong target. No
runtime discriminator exists (the owner representation is a JIT-time dataflow fact). This is the write/read
asymmetry: only the raw-Ldfld READ side needs a marker; every Stfld/ldobj/stobj/byref-marshal owner is always a byref.

## What shipped (JIT marker + runtime branch, Neo-gated, ~76 lines, Legacy-neutral)
- **`ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`**: const `NeoRawLdfldClrObjectFieldByRefMarker = 0x2`
  (~:228; a DISTINCT bit, NOT a reuse of child-24's 0x1 — keeps child-24's shipped const byte-untouched; clean
  single-branch semantics; the two byref shapes are mutually exclusive by CIL predecessor: Ldelema XOR Ldflda) +
  a stamp in `case Code.Ldfld` CLRType else-branch when `ins.Previous.OpCode.Code == Code.Ldflda` (~:3172).
- **`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`**: a runtime `else if` in the raw-Ldfld IsValueType
  branch (~:4003): decode `(objIdx, structFieldHash)`, defensive `objIdx==-1` flat-bytes read, else
  `NeoReadClrObjectField(target, off)` boxes the WHOLE struct field → `f.GetValue(boxedStruct)` reads the inner field
  → marshal to dest. F-10 IL-instance guarded with a tagged deferred NIE.
- **`ILRuntimeTestBase/TestFramework/TestClass3.cs`**: host helper `BuildNeoClrObjVtFieldOwner(a,b,c)` in `TestCLRBinding`.
- **`TestCases/NeoStepRawLdfldClrObjVtFieldTest.cs`** (new): TC1 single-field read (111), TC2 per-field read (111/222/333).

## Verification
- **NeoStep smoke: 380/0** (378 baseline + 2 probes), no regressions.
- **Stash-toggle (airtight):** stash the 2 engine files (probe + helper kept) → both probes FAULT (DivideByZero);
  pop → 380/0.
- **Read-back CORRECT (exact per-field values):** TC1=111; TC2 a=111/b=222/c=333.
- **Sibling families green:** child-4 (flat-bytes raw Ldfld/Stfld), child-9, child-19/24 (array-element), child-21
  (raw-Ldfld-CLR-struct seeding), child-27 (Stfld WRITE), child-28, NeoStep12/13/17.
- **Legacy-neutral:** both probes PASS under plain `Debug`+`useRegister=true`; 100% `#if ENABLE_NEO_MODE`/file-gated.

## Review
**APPROVE-WITH-FINDINGS** (reviewer != implementer; 0 Blocker/Major). Marker-mandatory (no runtime discriminator;
collision constructible on both axes) + 0x2-collision-free (audited every Operand4 write in JITCompiler.cs +
Optimizer.Neo.cs; raw-Ldfld Operand4 = {0x1 child-24, 0x2 this PR}; child-24's 0x1 byte-untouched) + branch-correct
+ regression-clean all confirmed.
- Minor M1 (doc/prose): design.md describes TC2 as a "sum" (666) but the actual probe does a stricter per-field OR
  (`x!=111 || y!=222 || z!=333`) — the implemented check is STRONGER than described.
- Trivial T1/T2: a decoded local comment + a line-ref.

## Delivery
**Mode:** local commit (portfolio per-child delivery; push per parent directive). Committed with the
portfolio-run.json + planning-context.md record updates. No PR.

## Durable findings (for future planning)
1. **The write/read asymmetry is now COMPLETE for both byref-owner shapes:** array-element (child-19 Stfld WRITE
   runtime + child-24 Ldfld READ marker) and CLR-object-struct-field (child-27 Stfld WRITE runtime + child-29 Ldfld
   READ marker). General rule: only the raw-Ldfld READ side needs a JIT marker (owner can be flat bytes OR a byref);
   every Stfld/ldobj/stobj/byref-marshal owner is ALWAYS a byref (runtime-safe).
2. **Distinct-bit marker encoding (0x1 child-24 / 0x2 child-29) is preferable to a generic "byref-owner" bit** — it
   keeps shipped siblings untouched, and the CIL predecessor makes the shapes provably mutually exclusive anyway.
3. **GOTCHA (child-25 build-server-cache, 2nd flavor):** after adding a host helper to `TestClass3.cs`, rebuild the
   **CLI** (not just TestCases) — the `--no-build` run loads the CLI's OWN stale `ILRuntimeTestBase.dll` from
   `ILRuntimeTestCLI/bin/Debug_Neo/net8.0/`, producing a misleading "Cannot find method" KeyNotFoundException.
   (Kill `dotnet` build-server + `-p:UseSharedCompilation=false` + rebuild the CLI.)
