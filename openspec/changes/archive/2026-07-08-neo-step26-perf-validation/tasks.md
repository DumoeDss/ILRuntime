# Tasks: neo-step26-perf-validation

> Propose-phase task list. Scope = SHIP the benchmark suite (A) + the
> single-threaded-contract doc (C). DEFER D (debugger) + F-4 (reflection) +
> E deep (cross-binding). Author != verifier is hard-enforced at apply.

## 1. Pre-apply dump-gate (BLOCKING -- do this FIRST)

- [ ] 1.1 Confirm HEAD `f3be2788` builds clean:
      `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`
      (0 errors) and `dotnet build TestCases/TestCases.csproj -c Debug`.
- [ ] 1.2 Confirm the `NeoStep` smoke is green at HEAD (215/215) before any
      change (the regression baseline):
      `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
      TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep`
- [ ] 1.3 Re-confirm the dump-gate verdicts in design.md section 1 are still
      accurate on the apply-time HEAD (the `DebugService` "not supported yet"
      guards at `DebugService.cs:207-209,268-271`; the `ILTypeInstance`
      `#if !ENABLE_NEO_MODE` indexer at `ILTypeInstance.cs:27,86,94`). If
      anything changed, STOP and re-scope.

## 2. SHIP A -- the benchmark suite (the core deliverable)

- [ ] 2.1 Create `TestCases/NeoStep26BenchProbe.cs`: 5 parameterless static
      methods (`BenchFieldAccess`, `BenchMethodCall`, `BenchValueType`,
      `BenchVirtualDispatch`, `BenchArray`), each a tight N-iteration loop
      returning a primitive (int/long). N tuned so the suite is <1s per
      config (OQ2). NO `[ILRuntimeTest]` tag (avoid the ignored-perf-test
      path) and NO name containing the substring `NeoStep` (so the regression
      filter does not pick them up). The methods MUST be runnable on BOTH
      engines (no Neo dependency).
- [ ] 2.2 Create `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep26BenchCheck.cs`
      (gated `#if ENABLE_NEO_MODE && DEBUG`): a `Run(AppDomain appdomain)`
      static method returning a result struct `{ Passed, Failed, TotalCells,
      Failures[] }` (mirror `NeoStep25LoadExecCheck`). For each bench in a
      fixed table `(name, methodName, iterations, expectedReturn)`: invoke via
      `appdomain.Invoke("ILRuntimeTest.NeoStep26BenchProbe", methodName, null,
      null)`, time with a host `System.Diagnostics.Stopwatch`, assert
      `Equals(result, expectedReturn)` via the divide-assert pattern, emit
      `BENCH:<name>:<iterations>:<elapsedTicks>`.
- [ ] 2.3 Add the CLI special-mode hook in `ILRuntimeTestCLI/Program.cs`
      (mirror the Step-25 hook at lines 118-140): under
      `#if ENABLE_NEO_MODE`, `if (nameFilter == "NeoStep26Bench") { ... run
      NeoStep26BenchCheck ... return failed <= 0 ? 0 : -1; }`.
- [ ] 2.4 Create `scripts/run-neo-bench.ps1` and `scripts/run-neo-bench.sh`:
      run the CLI under `Debug_Neo` (filter `NeoStep26Bench`) AND plain
      `Debug` + `useRegister=true`, parse the `BENCH:` lines, print
      `name | neo_ms | legacy_ms | ratio`. For the Legacy-config timing,
      resolve OQ1 (interpret the probe-method emit vs a host wrapper). If the
      `NeoStep26BenchCheck` self-check is absent under plain `Debug` (it is
      Neo-gated), the script SHALL drive the bench probe methods via the
      generic test loop for the Legacy timing.
- [ ] 2.5 (OPTIONAL) `scripts/README-neo-bench.md`: one paragraph on how to
      read the ratios + the "this host is not the baseline host" caveat.

## 3. SHIP C -- single-threaded-contract documentation

- [ ] 3.1 Add ONE comment block near the `UnityMainThreadID` check in
      `ILIntepreter.cs` (around line 56) stating: a single AppDomain is NOT
      safe for concurrent multi-threaded access; the pool isolates
      per-callback frame state (it does not enable concurrency); a host
      needing multi-threaded execution SHALL use one AppDomain per thread. NO
      behavioral change (no lock, no branch altered).
- [ ] 3.2 Confirm the comment compiles under BOTH `Debug_Neo` and plain
      `Debug` (it is in shared `ILIntepreter.cs`).

## 4. DEFER -- record the deferrals honestly (NO code change)

- [ ] 4.1 In `.trae/documents/neo-deferred-items.md`: add a
      `neo-debugger-neo-frame` follow-up row (the D debugger deferral) with
      the suspect sites pinned (`DebugService.cs:203-261, 263-300, 442-731,
      678-740`) and the note that the graceful "not supported yet" guards
      already ship + the stacktrace instruction dump is already Neo-adapted.
- [ ] 4.2 Confirm the F-4 row in `neo-deferred-items.md` remains OPEN and is
      NOT marked resolved by Step 26 (Step 26 does NOT subsume F-4; it stays
      routed to a cross-binding-adaptor follow-up).

## 5. Verify (the author != verifier gate)

- [ ] 5.1 Full `NeoStep` smoke stays 215/215 (ZERO regressions): the bench
      self-check runs under `NeoStep26Bench`, NOT under `NeoStep`; the probe
      method names do NOT contain `NeoStep`.
- [ ] 5.2 The `NeoStep26Bench` self-check runs green (each bench returns its
      expected value; the divide-assert does NOT trip; BENCH lines emitted).
- [ ] 5.3 Legacy-neutral: build plain `Debug` + `useRegister=true`, confirm
      the NeoStep-filter smoke shows the SAME pre-existing failure set (the
      comment + the probe methods are Legacy-runnable; the self-check + CLI
      hook compile out).
- [ ] 5.4 Adversarial probe (the "green smoke does not prove a gate" lesson):
      confirm the correctness-of-measurement gate is load-bearing by
      temporarily mutating a bench's expected value in the self-check table
      -> the divide-assert MUST trip (the gate catches a bench that did the
      wrong work); revert.
- [ ] 5.5 The runner script produces a finite, positive `ratio` per bench on
      the dev host (no NaN / no infinity / no missing config).
- [ ] 5.6 Write `review-report.md` (the verifier's findings).

## 6. Ship + archive

- [ ] 6.1 Write `ship-log.md`: verification evidence (smoke counts +
      adversarial-probe result) + review verdict + delivered scope (A + C) +
      deferred scope (D + F-4 + E deep).
- [ ] 6.2 Sync the spec delta into `openspec/specs/neo-optimizer/spec.md`
      (merge the 3 ADDED requirements) + move the change to
      `openspec/changes/archive/`. Manual merge is fine if the validator
      false-positives (the `neo-optimizer` capability is the one that
      validates).
- [ ] 6.3 LEAD commits + pushes: stage the new source + test + scripts +
      openspec/ artifacts + the `neo-deferred-items.md` update. Commit
      message `Neo step26:`. Exclude `.pdb`/`.gitignore`/`nuget.config`/
      `.claude`/`.vscode`/`CLAUDE.md` churn.
