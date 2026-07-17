# Tasks: neo-raw-ldfld-stfld-cluster-e

## Phase 1 -- RE-AUDIT (DONE)
- [x] Baseline full smoke = 51 failed (932 ran). Confirmed.
- [x] Extract cluster-E tests hitting Neo.cs:4533/4549/4794/4810/4818/4839.
- [x] Sub-cluster by exact frame + message + post-specialization JIT dump.

## Sub-clusters pinned (at 51)
- Ldarga-of-struct-param (no seed): ExpTest_10.UnitTest_1008, ExpTest_10.UnitTest_10022 [2]
- ldfld.value dest (no seed): ExpTest_10.UnitTest_10023 [1]
- delegate boxed-Int32 this (Step 19 scope): DelegateExtTest01/02, DelegateTest01 [3]
- stfld.ref heap-instance NRE: Test01.UnitTest_Generics, Test01.UnitTest_Generics2 [2]
- callvirt.clr-return owner NRE: Test05.TestStructDictionary [1]
- stfld.ref ArgOOB: RegisterVMTest04 [1]

## Phase 2 -- implement + verify (DONE)
- [x] Add `Ldarga`/`Ldarga_S` seeding case (mirror Ldloca).
- [x] Add `Ldfld_Value` dest seeding case (resolve field ILType from Operand4).
- [x] Build CLI (Debug_Neo --no-incremental) + TestCases (Debug) -- 0 errors.
- [x] Name-filter `ExpTest_10.UnitTest_100`: 12 ran / 0 failed (3 targets PASS).
- [x] Stash-toggle: HEAD (fix stashed) = 3 NRE failures; fix = 0 failures.
- [x] Full smoke delta: 51 -> 48 (exactly the 3 targets dropped, 0 new failures).
- [x] NeoStep smoke: 398 ran / 0 failed (no Neo regression).
- [x] Legacy-neutral: plain Debug + useRegister=true + NeoStep = 398 ran / 18
      failed (matches documented Legacy baseline; change is 100% Neo-gated).
- [x] Artifacts finalized (proposal/design/specs/tasks).

## Result
3 tests fixed (UnitTest_1008, UnitTest_10022, UnitTest_10023). Remaining 6
cluster-E tests are separate roots (delegate dispatch, heap-instance static
init, CLR-call return materialization, generic-owner ArgOOB) -- reported in
design.md, out of scope.
