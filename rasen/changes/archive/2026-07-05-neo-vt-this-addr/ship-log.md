# Ship Log — neo-vt-this-addr

**Change:** neo-vt-this-addr ([VT-THIS-ADDR] / Q-VT-NEWOBJ — IL value-type `newobj`)
**Date:** 2026-07-05
**Branch:** features/object-model-overhaul
**Pipeline:** auto-decompose child (small-feature), Tier A

## Verification evidence

- **Full NeoStep smoke: 99/99 green** (91 baseline + 8 new TC8-TC15), reproduced by
  BOTH the implementer and the independent reviewer.
  Command: `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build
  -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep`
- **Legacy-neutral:** plain `Debug` build compiles clean (0 errors); no shared optimizer
  pass touched. All changes are Neo-only (`#if ENABLE_NEO_MODE` / Neo-only file
  `ILIntepreter.Neo.cs`).
- **Adversarial probes (the MANDATORY set, since a green smoke does NOT prove the gate):**
  - TC9 newobj-result field-read AFTER intervening heap writes / register reuse (the
    Step 17 B1 silent-corruption class) — green.
  - TC10 nested VT — green (adapted to avoid the separate `Stfld_Value` deferred item).
  - TC11 VT with a reference field — green (the only currently-reachable Ret-arm
    ref-copy shape).
  - TC12 VT returned from a method then field-read — green.
  - TC13 the common local form `VT x = new VT(args)` (compiles to `ldloca; call ctor`) — green.
  - Reviewer independently re-probed fix-A blast radius (a VT INSTANCE METHOD with 4
    `this.field=` writes; patched the helper to the buggy form -> probe FAILED ->
    fix A confirmed load-bearing, not a regression).
- **F-1 gate fix re-reviewed:** reviewer constructed the `NeoStep18_TC16` ref-only-VT
  probe (prim-size 0), exonerated the F-1 fix by reverting it, confirmed F-1 RESOLVED.

## Review verdict

- **Round 0 (verify): APPROVE** — one Minor (F-1: Ret-arm copy-back gate too narrow,
  skipped ref-half for 0-prim-size VT) + one Trivial (T-1).
- **Round 1 (F-1 delta re-review): APPROVE** — F-1 RESOLVED, T-1 closed (inner prim
  guard now load-bearing). 99/99 smoke held. F-2 surfaced (see Deferred).
- No open Blocker / Major. Author != verifier held across all rounds (planner /
  implementer / reviewer / re-reviewer all distinct workers; LEAD applied the trivial
  F-1 fix inline, re-confirmed by a fresh non-author reviewer).

## Delivered scope

IL value-type `newobj` (`new MyILStruct(args)`) and the common local form
(`VT x = new VT(args)`). A VT ctor's `this.field=` writes land correctly in the
caller's frame dest; multi-field ctors, nested VT, VT-with-ref-field, returned VT,
base-ctor chain, and default ctor all work. NO heap ILTypeInstance, NO mixed
inline/heap representation.

Four Neo-only fixes:
1. **(load-bearing, pre-existing) inline-stfld owner-type clobber** —
   `TypeSpecializeNeoOpcodes` now seeds the dest-temp type ONLY for inline `Ldfld`
   (`IsInlineLdfldDestSeedable`); previously it clobbered the OWNING VT's type after
   every inline `Stfld`, so the 2nd+ `this.field=` fell back to the heap arm.
2. **D1 Newobj-dest typing** — `case Newobj:` in the type-spec pass types the dest
   as the constructed IL VT.
3. **Newobj dest temp sizing** — `GatherValueTypes` `case Newobj:` sizes a >8-byte VT
   newobj dest temp correctly (was truncated to 8 bytes via the subsequent Move).
4. **D2 runtime copy-back** — `Newobj` IL-VT branch (replaces the Step-18 NIE):
   zero-init caller dest; copy dest prim INTO callee slot-0 (pre-call); copy ctor
   args; invoke; copy callee slot-0 BACK. Ref-half runs in the Ret arm BEFORE the
   mStack pop (4 new optional `vtNewobjCallerDst*` params on `ExecuteNeo`; defaults
   preserve all existing callers). D3 (addrAlias VT-`this` root) was NOT needed.

Files: `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` (+90),
`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (+144),
`TestCases/NeoStep18Test.cs` (+244, TC8-TC15 + helper types).

## Deferred scope (recorded, not silently dropped)

- **Delegate `newobj`** (`new Action(foo)`) — separate child `neo-step19-delegate`.
  The existing `NotImplementedException("Neo Newobj delegate is not implemented")` stays.
- **CLR value-type `newobj` with reference fields, no ValueTypeBinder** (reflection path) —
  Step 13b `NeoClrStructHasReferenceField` NIE guard stays.
- **Generic-parameter VT newobj** spanning IL/CLR — with `neo-step17-completion` (generic-byref).
- **`Stfld_Value`** (whole-VT-into-VT-field store, a Step 12b deferred item) — TC10 adapted
  to avoid it; not claimed fixed here.
- **F-2 / [INLINER-REFONLY-VT]** — ref-only VT (prim-size 0) local `new S(refArgs)` inliner
  mis-compile (inlined `stfld.ref.inline` writes don't survive to `ldfld.ref`). Pre-existing,
  latent (blocked by the separate return-NIE). Recorded in `neo-deferred-items.md` §2/§3;
  fold into the K2-FAM bridge child or a future [OPT-HARDEN-3]. No failing test shipped.

## Downstream unblocked

`neo-step13-area4` (value-type-`this` direct-call), `neo-step17-completion`
(constrained.-on-VT byref-this), `neo-k2fam-bridge` (clean IL-side ldfld/stfld reproducer).
