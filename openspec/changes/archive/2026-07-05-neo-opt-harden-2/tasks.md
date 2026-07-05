# Tasks - neo-opt-harden-2

One implementer pass. The fix is DUMP-GATED: Block 1 confirms the reproducer;
Block 2 dumps and locks the root cause; Block 3 ships the dump-confirmed fix;
Block 4 is the Legacy-neutrality + adversarial-probe gate; Block 5 is the
fallback (defer) path if both candidates are refuted.

## Block 0 - Probe the dump BEFORE designing the fix (OPT-HARDEN K1 lesson)

> MANDATORY first step. The prior review's worded root cause
> ("AllocateLocalStackSpaces slot-reuse / liveness") was investigated at
> propose and DISPROVEN: the method allocates monotonic non-overlapping slots
> (no reuse logic). Confirm the leading candidate (D6 return-write / local-slot
> representation mismatch) or fall back.

- [x] 0.1 Re-confirm the F-MAJ-1 reproducer FAILS on current HEAD: build CLI
  `Debug_Neo` + TestCases `Debug`; temporarily add the probe
  `NeoOptHardTest_Fmaj1_TwoClrStructLocals` (two CLR struct locals `v`, `w`
  from `MakeTestVector3NoBinding`, `r1 = SumTestVector3NoBindingFields(v)`,
  `r2 = SumTestVector3NoBindingFields(w)`, `if (r1 != 600 || r2 != 3) { 1/0 }`);
  run under the `NeoOptHardTest_` filter -> DivideByZero (CONFIRMED). Also
  confirm the two isolation controls (`if (r1 != 600) {1/0}` alone;
  `if (r2 != 3) {1/0}` alone) each FAIL, and the two-CLR-int-return control
  PASSES.
- [x] 0.2 STASH-PROOF it is pre-existing: stash ALL runtime files this change
  will touch (none yet -- this is the baseline); the probe already FAILS on
  HEAD, so the bug exists independent of this change. (Do NOT re-litigate
  Step-13b reachability; it is documented pre-existing.)
- [x] 0.3 JIT DUMP: enable `OUTPUT_JIT_RESULT` (Debug_Neo already does); capture
  `frame.LocalInfos` for the probe method. Record for the two CLR struct
  locals' dest registers: `Offset`, `Size`, `RefOffset`, `RefCount`, and
  `localIsRef`. Record the D6 return-write size (`retSz =
  GetNeoValueTypeManagedSize(Vector3)` -- expect 12) at
  `ILIntepreter.Neo.cs:329`. CONFIRM candidate (1): dest slot is `Size=4,
  RefCount=1` (boxed-ref) AND `retSz > 4` (flat-bytes overflow) -- OR REFUTE
  it (slot already flat-bytes-sized, write fits).
- [x] 0.4 IF 0.3 refutes candidate (1), inspect `CleanupRegister`
  (`Optimizer.RegisterCleanup.cs`) output in the same dump: do the two live
  struct-local registers receive DISTINCT post-compaction indices? If they
  collide, candidate (2) is the defect. If they are distinct AND the write fits,
  both candidates are REFUTED -> go to Block 5 (defer).
- [x] 0.5 LOCK the fix option from the dump:
  - Candidate (1) confirmed -> Option A (write-side, Neo-only, PREFERRED) OR
    Option B (declare-side, SHARED -- needs Legacy-neutrality gate). Choose A
    unless the D2/Move_Vt consistency probe in 0.6 forces B.
  - Candidate (2) confirmed -> fix in `CleanupRegister` (SHARED; gate
    `#if ENABLE_NEO_MODE`).
  - Both refuted -> Block 5.
- [x] 0.6 D2/Move_Vt consistency probe (only if Option A is in play): confirm
  that a subsequent by-value PARAM read (`ReadNeoValueType`,
  `ILIntepreter.Neo.cs:209`) of a boxed-ref CLR-VT local reads the boxed ref
  correctly (the K2-FAM partial path). If D2 expects FLAT BYTES for a
  Make-sourced local, Option A breaks D2 -> take Option B instead.

## Block 1 - Ship the F-MAJ-1 reproducer + adversarial probes (test-only)

- [x] 1.1 Extend `TestCases/NeoOptHardeningTest.cs` with the F-MAJ-1 probes
  under the `NeoOptHardTest_Fmaj1_*` prefix (run under the `NeoOptHardTest_`
  filter, mirroring K1):
  - `NeoOptHardTest_Fmaj1_TwoClrStructLocals` -- the exact reproducer (FAILS
    on HEAD).
  - `NeoOptHardTest_Fmaj1_IsolationR1` / `...R2` -- the isolation controls
    (each FAILS on HEAD).
  - `NeoOptHardTest_Fmaj1_ThreeStructLocals` -- 3+ simultaneous structs.
  - `NeoOptHardTest_Fmaj1_LiveRangeOverlapAcrossCall` -- struct local live
    across an intervening method call.
  - `NeoOptHardTest_Fmaj1_ScopedReuseNoFrameBloat` -- disjoint-scope reuse
    (regression guard: fix must not balloon the frame).
  - `NeoOptHardTest_Fmaj1_StructSize4` / `...Size8` -- boundary struct sizes.
  - `NeoOptHardTest_Fmaj1_TwoClrIntReturns` -- the documented control (PASSES
    on HEAD).
- [x] 1.2 Promote the exact reproducer to `TestCases/NeoStep13bTest.cs` as
  `NeoStep13bTwoClrStructLocalsRegression` so the `NeoStep` smoke catches a
  future regression (it FAILS on HEAD; PASSES after the fix). Tag the
  F-MAJ-1 probes in 1.1 to NOT match `NeoStep` (use the `NeoOptHardTest_`
  prefix) so the smoke stays green pre-fix.
- [x] 1.3 Rebuild TestCases `Debug` -> 0 errors. Run the `NeoOptHardTest_`
  filter on HEAD (pre-fix) -> record which probes FAIL (expected: the
  reproducer, isolation controls, 3+-local, live-range-overlap, struct-size-8;
  the int-return control and struct-size-4 PASS).

## Block 2 - Apply the dump-locked fix (root-cause fix)

> The fix site depends on Block 0's dump. The tasks below cover Option A
> (leading); Option B / candidate (2) variants are bracketed.

- [x] 2.1 OPTION A (write-side, Neo-only) -- NOT TAKEN. The Block-0.6 D2
  consistency probe REFUTED Option A: the by-value param read
  (`CLRMethod.Invoke` D2) + the optimizer's caller-local -> callee-param copy
  (`CopyNeoCallArguments`, `primSize = dstInfo.Size`) ALREADY byte-copies N
  flat bytes from the caller local's Offset. Option A (boxed-ref write) would
  break D2 (it would copy a 4-byte mStack index + garbage). Option B taken
  instead. See design.md "Apply outcome".
- [x] 2.2 OPTION B (declare-side, SHARED -- only if 0.6 forces it): DONE. The
  Block-0.6 D2-consistency probe FORCED Option B. In
  `JITCompiler.cs` `AllocateLocalStackSpaces` (the CLR-VT-local branch), a CLR
  value-type LOCAL is now declared flat bytes (`Size =
  GetNeoValueTypeManagedSize(ivt)`, `RefCount = 0`, `localIsRef=false`),
  mirroring `AllocateNeoCallParamSlot`. Gated `#if ENABLE_NEO_MODE` so Legacy
  keeps `Size=4, RefCount=1`. The `Move_Vt` / by-value-param copy paths
  already treat the local as flat bytes (verified via the probe sweep).
- [x] 2.3 CANDIDATE (2) -- NOT TAKEN. Block 0.4 REFUTED candidate (2): the
  dump showed the two CLR struct locals received DISTINCT, non-overlapping
  regions (Offset 0 vs 4, distinct RefOffset). No `CleanupRegister`
  compaction collision; no fix shipped there.
- [x] 2.4 Rebuild CLI `Debug_Neo` -> 0 errors. Re-run the `NeoOptHardTest_`
  filter -> all F-MAJ-1 probes PASS (including the reproducer + isolation
  controls).

## Block 3 - Regression + Legacy-neutrality gate (the second gate)

- [x] 3.1 Full `NeoStep` smoke: `dotnet run -c Debug_Neo -f net8.0 --project
  ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep` -> 99/99 all-green, ZERO
  regressions (the promoted `NeoStep13bTwoClrStructLocalsRegression` now
  passes). F-MAJ-1 probes under `NeoOptHardTest_` -> all PASS.
- [x] 3.2 Legacy-neutrality (for any SHARED-engine edit -- Option B or
  candidate 2): build a plain-`Debug` CLI + `useRegister=true`; run the
  NeoStep-filter smoke. Stash the fix and re-run -- the SAME pre-existing
  failures (the ~7 Neo-feature tests) appear with and without the fix.
  Record the failure-set is identical. (For Option A -- Neo-only -- a
  code-read argument that the D6 arm is in `ILIntepreter.Neo.cs` which
  Legacy compiles out suffices; the stash-toggle is still run as
  belt-and-braces.)
- [x] 3.3 Adversarial-probe sweep: re-run the 8 F-MAJ-1 probes from Block 1
  AFTER the fix. All PASS. Specifically verify the scoped-reuse probe
  (`NeoOptHardTest_Fmaj1_ScopedReuseNoFrameBloat`) shows NO frame-size
  increase vs HEAD (the monotonic allocator is unchanged; the fix is
  representation-consistency, not slot reuse). Verify struct-size-4 PASSES
  (no overflow; fix does not over-correct).

## Block 4 - Ship-log + spec sync

- [x] 4.1 Write `ship-log.md`: record the dump-confirmed root cause (candidate
  1 / 2 / other), the chosen fix option (A / B / candidate-2), the
  Legacy-neutrality proof (Neo-only OR stash-toggle failure-set identical),
  the F-MAJ-1 probe results, and the explicit note that
  `AllocateLocalStackSpaces` has NO slot-reuse logic (so a future
  liveness-allocator is not mis-attributed).
- [x] 4.2 Sync the spec delta into `openspec/specs/neo-optimizer/spec.md`:
  the F-MAJ-1 requirement goes ACTIVE (fix shipped) or stays PROVISIONAL ->
  DEFERRED (Block 5). The `AllocateLocalStackSpaces`-monotonic-allocation
  record-keeping stays regardless.
- [x] 4.3 Commit + push: stage the runtime fix file(s) + the extended
  `NeoOptHardeningTest.cs` + `NeoStep13bTest.cs` + the openspec/ artifacts +
  (if updated) `.trae/documents/neo-deferred-items.md` (move F-MAJ-1 to
  Resolved). Message: `Neo opt-harden-2: CLR struct local return-write
  representation fix (F-MAJ-1)`. End with the Co-Authored-By trailer. Push
  `origin features/object-model-overhaul`.

## Block 5 - Fallback: both candidates refuted (DEFER, ship NO guessed fix)

- [ ] 5.1 IF Block 0.3 refutes candidate (1) AND Block 0.4 refutes candidate
  (2) (offsets distinct, write fits, no compaction renumber, yet symptom
  persists): capture the full JIT dump (`frame.LocalInfos` for the probe
  method) + the reproducer source into the change directory.
- [ ] 5.2 Mark the F-MAJ-1 requirement PROVISIONAL -> DEFERRED in the spec
  delta (the Q-STRUCT / Q-LONG / Q-NEWOBJ outcome). Do NOT ship a guessed
  fix to the shared frame-allocation / return-write path.
- [ ] 5.3 Ship-log: record that the prior worded root cause was disproven,
  both candidates were refuted by the dump, and F-MAJ-1 is DEFERRED with
  the dump + reproducer pinned for a future reproducing case. Update
  `.trae/documents/neo-deferred-items.md` F-MAJ-1 row to reflect the new
  finding (no liveness allocator; candidates refuted; reproducer pinned).
- [ ] 5.4 Commit + push the test-only change (the reproducer under
  `NeoOptHardTest_`, NOT promoted to `NeoStep` so the smoke stays green) +
  the spec delta + the dump artifacts. Message: `Neo opt-harden-2: F-MAJ-1
  deferred (both candidates refuted by dump; reproducer pinned)`. Push.
