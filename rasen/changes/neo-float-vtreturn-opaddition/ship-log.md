# Ship Log — neo-float-vtreturn-opaddition (child 28)

**Change:** fix the float-ctor / op_Addition VT-return gap surfaced by child-26 (`new TestVector3(float,float,
float)` and `TestVector3.op_Addition` returned zero when CALLED from IL). HIGHEST-value newly-surfaced candidate.
**Capability:** `neo-value-types` (ADDED requirement).
**Pipeline:** small-feature (triage-reaudit -> diagnose -> propose+apply -> verify -> review -> ship).
**Date:** 2026-07-13. **Branch:** `features/object-model-overhaul`. **Base HEAD:** `c75c28d9`.

## Re-audit RECONCILED the suspicious "ctor yields zero" claim (it IS real, but context-specific)
- TC1 (ctor alone): `new TestVector3(1f,2f,3f)` → **(0,0,0)**. CONFIRMED.
- TC2 (op_Addition return alone, no ctor — `One+One`, One host-precomputed): → **(0,0,0)**. CONFIRMED.
- **Why the suspicion was wrong:** the green smoke uses `TestVector3.One` — a HOST-PRECOMPUTED static FIELD read via
  `ldsfld` (the precomputed bytes cross unchanged, NEVER calling the broken path). The bug manifests ONLY when IL
  CALLS the TestVector3 ctor/operators (→ `call.redirect` → the broken stubs). Children 3/8/15/16/21 passed because
  they read `.One`/fields, not because the ctor/operators worked. LESSON: a struct used pervasively in green smoke
  can STILL have broken ctors/operators if the smoke only reads precomputed statics — re-audit the CALL path.

## Root cause = STALE COMMITTED autogen binding stubs (child-2 defect class), NOT engine/generator
The Step-13b GENERATOR is correct (emits ReadNeoValueType/WriteNeoValueType); the RUNTIME reflection-fallback is
correct; but the COMMITTED `ILRuntimeTest_TestFramework_TestVector3_Binding.cs` stubs were NEVER REGENERATED
post-Step-13b → `op_Addition_2_Neo`/`op_Multiply_1_Neo` leave VT args as `default(...)` and never write the return;
`Ctor_0_Neo` discards the result. Every binder-VT method silently returns zero via `call.redirect`. Full regen is
GUI-bound (TestMainForm WinForms), so the stubs were hand-ported.

## What shipped (4 hand-ported stubs + a generator latent-bug fix, Neo-gated, Legacy-neutral)
- **`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`** (+14): a PUBLIC facade `GetNeoValueTypeManagedSize`
  (the generator's Step-13b emission referenced the INTERNAL `Optimizer` class — a latent compile bug in the
  consumer; no InternalsVisibleTo).
- **`ILRuntime/Runtime/CLRBinding/BindingGeneratorExtensions.cs`** (4 sites) + **`MethodBindingGenerator.cs`** (1 site):
  repointed to the facade (the matching enabler for future regen).
- **`ILRuntimeTestBase/AutoGenerate/ILRuntimeTest_TestFramework_TestVector3_Binding.cs`** (4 stubs: `get_One2_0_Neo`,
  `op_Multiply_1_Neo`, `op_Addition_2_Neo`, `Ctor_0_Neo`): hand-ported from `default(...)` stubs to
  ReadNeoValueType/WriteNeoValueType (faithful to the post-Step-13b generator template).
- **`ILRuntimeTestBase/TestFramework/TestClass3.cs`** (+24, in `TestCLRBinding`): host helpers.
- **`TestCases/NeoStepFloatVtReturnTest.cs`** (new): 3 probes (op_Addition=9, op_Multiply=18, child-26 `arr[0]+=One`=6).

## Verification
- **NeoStep smoke: 378/0** (375 baseline + 3 probes), no regressions, EXIT=0.
- **Stash-toggle (airtight):** revert ONLY the binding file to HEAD's stale stubs → 3/3 FAULT (DivideByZero);
  restore → 3/3 PASS. Proves the binding stubs are load-bearing (the engine + generator changes are enabling).
- **Probe arithmetic host-side-asserted** (sidesteps the conv.i4-float bug); 9/18/6 hand-checked reachable.
- **Legacy-neutral:** plain `Debug`+`useRegister=true`+NeoStep = 378 ran/18 failed (pre-existing Legacy set; the 3
  probes PASS under Legacy). All changes Neo-gated/file-gated/generator-only.

## Review
**APPROVE-WITH-FINDINGS** (reviewer != implementer; 0 Blocker/Major). Root-cause-diagnosis-sound (stale stubs, not
engine) + generator-change-justified (Optimizer internal, facade load-bearing) + stubs-faithful-to-template all
confirmed (independent re-run of smoke + stash-toggle + HEAD-stub inspection).
- Minor F1: `get_One2_0_Neo`/`Ctor_0_Neo` are byte-faithful to the template but NOT directly exercised by the 3
  probes (they use the `One` field, not `One2`; never call the ctor) — coverage gap, not a defect (the stash-toggle
  proves the two OPERATOR stubs fire).
- Minor F2: `Ctor_0_Neo`'s added return-write is effectively dead on the only path that reaches it today (struct
  newobj passes `retDst=null` per symptom-1). Correct by template (fires post-symptom-1); design is upfront.
- Trivial: generator's 5 repointed sites not directly tested (justified); design line-citations drifted; wording nit.

## Delivery
**Mode:** local commit (portfolio per-child delivery; push per parent directive). Committed with the
portfolio-run.json + planning-context.md record updates. No PR.

## Durable findings (for future planning)
1. **The recurring "stale generated binding stub" defect class (child-2 lineage):** the Step-13b generator is
   correct, but committed `AutoGenerate/*_Binding.cs` files were never regenerated → every binder-VT method
   silently returns zero via `call.redirect`. Full regen is GUI-bound (TestMainForm WinForms). A struct pervasive
   in green smoke can still have broken ctors/operators if the smoke only reads precomputed statics — re-audit the
   CALL path, not just the field-read path.
2. **The generator's Step-13b emission referenced the internal `Optimizer` class** — a latent compile bug (no
   InternalsVisibleTo). Any future regen MUST use the new public `ILIntepreter.GetNeoValueTypeManagedSize` facade
   (now wired into the generator).
3. **A CLR struct `newobj` lowers to `initobj+ldloca+push(this byref)+call.redirect.ctor` with `retDst=null`** — a
   distinct dest convention from regular calls; the struct-ctor result has nowhere to land on the autogen path.
   This is symptom-1, deferred as the `neo-clr-struct-newobj-retdest-null` follow-up (the Ctor_0_Neo stub-side
   write is now correct; the retDst-null contract is the remaining gap).

## Surfaced follow-up
**`neo-clr-struct-newobj-retdest-null`** (symptom 1): the struct-ctor `newobj` dest convention (`retDst=null`).
The other half of the original framed gap.
