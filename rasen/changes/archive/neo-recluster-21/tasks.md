# Tasks: neo-recluster-21

## Phase 1 -- FRESH grounding [DONE]
- [x] Build CLI (Debug_Neo --no-incremental, UseSharedCompilation=false) + TestCases (Debug). 0 errors.
- [x] Run the full smoke (no filter). Result: `Ran 935 tests, 21 failded, 20 ignored, 7 todos`.
- [x] Extract + classify each of the 21 failures (JIT opcode + Neo.cs frame + test site).
- [x] Re-cluster. Written to `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-21.md`.
- [x] Legacy (plain Debug) build verified clean (0 errors).

## Phase 2 -- fix the largest sub-cluster / batch the most tractable [ATTEMPTED, NO FIX SHIPPED]
The 21 are deeply fragmented (12 in a throw@7098 grab-bag of DISTINCT roots; the
rest deep singletons). The single most tractable pinned root is the
initobj-byref gap (UnitTest_NestedGenericRefOut). Three+ fix approaches
attempted; all aborted as net-negative or non-functional (see design.md).

- [~] Attempt 1: runtime-only deref (NeoInitobjReferenceThroughByref helper).
      Fixed NestedGenericRefOut but regressed ActivatorCreateInstanceWithArgsTest
      + InheritanceTest20 (21 -> 22). REVERTED.
- [~] Attempt 2: JIT marker gated on ResolveLiveAlias discriminator. The static
      addrAlias stale-reuse entries made the discriminator mis-fire. REVERTED.
- [~] Attempt 3: JIT marker gated on liveAliasMap.ContainsKey. Mis-fires for the
      Activator ref-temp case. REVERTED.
- [~] Attempt 4: JIT-only forward-walk map (refLocalByrefSource). `localIsRef`
      does not mark the reference local's register (register reuse + stale alias
      -> the guard never fired). REVERTED.
- [x] Codebase reverted to clean HEAD (git diff empty for ILRuntime/ + TestCases/).

## Verify [DONE -- truth = full-smoke number]
- [x] FULL SMOKE: `21 -> 21` (no regression; codebase is clean HEAD). Confirmed
      by a fresh full smoke run after all reverts: `Ran 935 tests, 21 failded`.
- [x] NeoStep 0-failures: 401/0 (baseline, unchanged code).
- [x] Legacy-neutral: by construction (no source change shipped).

## Deferred (recommended next child: neo-initobj-ref-byref)
Implement Option A from design.md: CIL-producer scan at JIT emission (case
Code.Initobj in JITCompiler.cs) -- follow `ins.Previous` to the producing
ldloca/ldarga/ldflda; if its source is reference-typed, stamp an Operand4
byref-marker so the runtime reference-type initobj arm derefs. CIL Previous
links are stable (unlike the register-VM body), so this sidesteps the
unreliable alias/localIsRef state that defeated every approach in this child.
Expected: 21 -> 20 (UnitTest_NestedGenericRefOut).
