# Proposal: neo-step26-perf-validation

## Why

Step 26 is the LAST numbered AOT-chain step: the Neo **perf-validation +
hardening** capstone. The Neo JIT path now covers Steps 1-20 (sync) + the
Step 22-25 AOT toolchain (generic templates, the `.neo` format, the
`ilrt_neoc` precompile CLI, the runtime loader -- S1 non-generic + S2
generic). What is MISSING is a structured **Neo-vs-Legacy performance
comparison harness** (the codebase has `UnitTest_Performance*` methods that
run INSIDE the interpreter, but no harness that runs the SAME workload under
both `ExecuteNeo` and `ExecuteR` and reports a ratio), plus an honest
accounting of the remaining Neo adaptation edges (reflection-on-Neo,
thread-safety, the debugger, CrossBindingAdapter).

Per the binding dump-gate discipline, each of the 5 sub-surfaces (A
benchmarks / B reflection / C thread-safety / D debugger / E
CrossBindingAdapter) was probed on HEAD `f3be2788` before designing. The dump
found exactly ONE real additive deliverable (A), ONE small documentation item
(C), and THREE deferral candidates (B / D / E) that are either already-working
or routed to separate follow-ups. This is the expected partial-ship outcome
for a child this broad (precedent: array 2/3 no-ops, byref 3/4 no-ops,
controlflow fully disproven).

## What changes

### SHIP (the proven slice)

1. **A Neo-vs-Legacy benchmark suite** (the core deliverable, additive +
   Neo-only + Legacy-neutral):
   - A host-side self-check `NeoStep26BenchCheck.Run(appdomain)` (gated
     `#if ENABLE_NEO_MODE && DEBUG`), driven via the existing CLI special-mode
     hook (`if (nameFilter == "NeoStep26Bench")`), mirroring the Step-22..25
     self-check pattern.
   - A dedicated `TestCases/NeoStep26BenchProbe.cs` with 5 parameterless
     static bench methods (field access / method call / value-type / virtual
     dispatch / array), each a tight N-iteration loop returning a primitive
     (avoids the F-12 ref-return limitation + the Step-6 parameterless-Run
     shim constraint).
   - The self-check invokes each method via `appdomain.Invoke` (re-entry),
     times it with a HOST `Stopwatch`, asserts the return equals the known-
     expected value (correctness-of-measurement gate), and emits structured
     `BENCH:<name>:<iterations>:<elapsedTicks>` lines. It does NOT assert a
     ratio threshold (this env is not the perf baseline host).
   - A runner script (`scripts/run-neo-bench.{ps1,sh}`) that runs the CLI
     under `Debug_Neo` AND plain `Debug` (`useRegister=true`), parses the
     `BENCH:` lines, and prints `name | neo_ms | legacy_ms | ratio`.

2. **C Single-threaded-contract documentation** (small, in-spec): a spec
   requirement + a comment block near the `UnityMainThreadID` checks stating
   that a single AppDomain is NOT safe for concurrent multi-threaded access
   (the pool isolates per-callback, it does not enable concurrency). No code
   change.

### DEFER (documented in the spec delta + neo-deferred-items.md)

3. **D Debugger variable inspection under Neo** -- `DebugService` already
   Neo-gates `GetThisInfo` / `GetLocalVariableInfo` to graceful "not supported
   yet" degradation; the stacktrace instruction dump IS Neo-adapted. A real
   fix needs deep `byte* frameBase` + `AutoList mStack` + `CompiledFrame.
   LocalInfos` work across ~8 methods. DEFER to a dedicated
   `neo-debugger-neo-frame` follow-up.

4. **B / E Reflection-on-Neo (F-4) + CrossBindingAdapter deep** -- the F-4
   four-mechanism reflection-on-Neo fix (callvirt-on-CLR-interface;
   callvirt.clr GetType; `appdomain.Invoke` instance re-entry; `ILTypeInstance`
   Neo indexer) is a cross-binding-adaptor follow-up, NOT a perf-validation
   step. Step 26 does **NOT** subsume F-4. The working reflection coverage
   (catch + isinst) is already guarded by `NeoStep14_ILEx_*`.

## Impact

- **Neo `ExecuteNeo` / JIT / optimizer / object model**: UNCHANGED. The
  benchmark is a new caller of `appdomain.Invoke`; C is a comment.
- **Legacy `ExecuteR`**: byte-identical (everything new is Neo-gated or
  config-agnostic; the bench probe methods are plain parameterless static
  methods runnable on both engines).
- **Regression risk: LOW.** Additive only. Gate: full `NeoStep` smoke
  (215/215) + Legacy-neutral stash-toggle. The bench self-check is a SEPARATE
  filter (`NeoStep26Bench`); it does NOT run under the `NeoStep` regression
  filter.
- **Partial-ship: EXPECTED** (3 of 5 sub-surfaces are defer/doc-only). The
  core deliverable (A) is substantive and completes the numbered AOT chain.
