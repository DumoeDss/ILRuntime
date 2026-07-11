# Tasks - implement-neo-opt-hardening

One implementer pass. The K1 block is the only fix; Q-STRUCT / Q-LONG are
confirmation-only (deferred unless reproduced).

## Block 1 - K1: FCP ldloca-kill (THE FIX — corrected re-attempt)

> NOTE: the original Block 1 (a `Stfld_*_Inline.Register1 == xSrc/xDst` kill)
> was a no-op and was reverted (see `planning-context.md` §9). The field store
> reaches the base local indirectly through a `ldloca.s` address handle, so
> the correct kill signal is the `ldloca` itself, not the stfld.

- [x] 1.1 Confirm the K1 repro fails on HEAD: build CLI `Debug_Neo` +
  TestCases `Debug`, run `NeoOptHardTest_K1_FcpVtPropagation` ->
  DivideByZero (the `b.n` read returns 999). CONFIRMED (2 of 3 K1 tests
  fail on HEAD).
- [x] 1.2 In `Optimizer.FCP.cs`, add a Neo-only (`#if ENABLE_NEO_MODE`)
  kill to the IN-BLOCK propagation loop. When `Y` is `Ldloca`/`Ldloca_S`
  and its source register (`ySrc = op.Register2`, the base local) equals
  `xSrc` OR `xDst`, set `postPropagation = false`, `ended = true`, break.
  (No helper needed; `GetOpcodeSourceRegister` already enumerates the
  Ldloca source.)
- [x] 1.3 Add the SAME kill to the CROSS-BLOCK pending-FCP loop: when the
  ldloca base matches `xSrc` or `xDst`, set `cannotRemove = true` and break.
- [x] 1.4 Rebuild CLI `Debug_Neo` -> 0 errors. Re-run
  `NeoOptHardTest_K1_*` -> all PASS (3/3, no DivideByZero).
- [x] 1.5 Shipped `TestCases/NeoOptHardeningTest.cs` with three K1
  regression tests (`NeoOptHardTest_K1_FcpVtPropagation`,
  `NeoOptHardTest_K1_MutateDestAfterCopy`,
  `NeoOptHardTest_K1_SourceAndDestAfterMutation`). Q-STRUCT / Q-LONG probes
  NOT shipped (they pass trivially on HEAD; deferred per change scope).
- [x] 1.6 Full NeoStep smoke: `dotnet run -c Debug_Neo -f net8.0 --project
  ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep` -> 72/72 all-green, ZERO
  regressions. (K1 tests live under the `NeoOptHardTest_` prefix, run
  separately: 3/3 green.)

## Block 2 - Q-STRUCT: confirm not-reproducible (DEFERRED, no fix)

- [ ] 2.1 Run `ProbeQStruct_ElementReadAfterMutation` and
  `ProbeQStruct_BranchAfterMutation` on HEAD -> confirm both PASS (they did
  during propose). If a future variation FAILS, capture it and open a follow-up
  change; do NOT guess a BCP/copy-prop fix here.
- [ ] 2.2 Leave the Q-STRUCT probes as non-asserting documentation cases in
  `NeoOptHardeningTest.cs` (per task 1.5).

## Block 3 - Q-LONG: confirm not-reproducible (DEFERRED, no fix)

- [ ] 3.1 Run the three Q-LONG probes
  (`ProbeQLong_ConvI8ZeroCompare`, `ProbeQLong_ScalarZeroCompare`,
  `ProbeQLong_DefaultFieldZeroCompare`) on HEAD -> confirm all PASS (they did
  during propose). If a future variation FAILS, capture it and open a
  follow-up; do NOT guess a conv/compare fix here.
- [ ] 3.2 Leave the Q-LONG probes as non-asserting documentation cases in
  `NeoOptHardeningTest.cs` (per task 1.5).

## Block 4 - Legacy-neutrality gate (the second gate)

- [x] 4.1 Code-read argument: the K1 fix is entirely inside
  `#if ENABLE_NEO_MODE`. A plain-`Debug` (Legacy) build compiles it out, so
  the Legacy FCP control flow is byte-identical to before. (The `Ldloca`
  source enumeration it relies on is already present in the shared
  `GetOpcodeSourceRegister`; only the kill branch is gated.)
- [x] 4.2 Empirically confirmed: Legacy `Debug` NeoStep smoke = 7 failures,
  IDENTICAL with and without the fix (verified by stashing the FCP change and
  re-running on HEAD — same 7). Those 7 are Neo-feature tests (try/catch,
  CLR-struct boxing, NaN compare) that don't run correctly on Legacy
  `ExecuteR`; none relate to copy propagation.
- [x] 4.3 Ship-log note: K1 fixed (Neo-only FCP ldloca kill — corrected
  re-attempt; the original stfld-Register1 kill was a no-op, reverted);
  Q-STRUCT / Q-LONG deferred (not reproducible on HEAD, tracked in spec).
