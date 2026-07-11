# Design: neo-step26-perf-validation

> Propose-phase design. Grounded in the per-sub-surface dump-gate on HEAD
> `f3be2788` (Step 25 S2 shipped). The dump-gate disproved the "Step 26 must
> land all 5 sub-surfaces" orientation; this design ships the ONE real
> additive deliverable + the ONE doc item + honest deferrals.

## 1. Per-sub-surface dump-gate verdict (HEAD `f3be2788`)

| Sub-surface | Verdict | Evidence (file:line) | SHIP / DEFER |
|---|---|---|---|
| **A** Benchmark suite | REAL GAP (SMALL, ADDITIVE) | No timing scaffold in `ILRuntimeTestCLI/Program.cs` (only the Step-22..25 special-mode hooks at lines 50-140 + the generic loop at 146-190). `TestCases/Test01.cs:54-417` has `UnitTest_Performance*` methods tagged `[ILRuntimeTest(IsPerformanceTest = true)]` that use an INTERPRETED `Stopwatch`, but the harness IGNORES them (probe: `UnitTest_Performance2` under `Debug_Neo` -> "Ran 1 tests, 0 failed, 1 ignored"; no `time:` line, no NIE). So no structured Neo-vs-Legacy ratio harness exists. | **SHIP** (core) |
| **B** Reflection edges (F-4 family) | DOC-ONLY (DEFER) | `ILTypeInstance.cs:27,86,94,379,404,449,...` -- the indexer + accessors are `#if !ENABLE_NEO_MODE` (compiled OUT under Neo). F-4's 4 broken read paths (neo-deferred-items.md F-4 master-table row) are each a separate mechanism. The working coverage (catch + isinst) is already guarded by `NeoStep14_ILEx_*` (neo-il-exception-throw). | **DEFER** (F-4 -> cross-binding-adaptor follow-up; NOT subsumed) |
| **C** Thread-safety / cross-domain | DOC-ONLY | `ILIntepreter.cs:56,151,2164,2174,4723` + `ILIntepreter.Neo.cs:829,4451` + `ILIntepreter.Register.cs:91,3044,3054,5362` all check `Thread.CurrentThread.ManagedThreadId == AppDomain.UnityMainThreadID` (cooperative coroutine pump). `DelegateAdapter.cs:979,1009-1013` uses a POOLED interpreter (per-callback isolation, NOT concurrency). No `new Thread()`/`QueueUserWorkItem`/`lock` on AppDomain maps in the interpreter core. | **SHIP** (doc: a spec requirement + a comment block) |
| **D** Debugger (variable inspection) | LARGE GAP (DEFER) | `DebugService.cs:207-209` `GetThisInfo` returns "Neo this inspection is not supported yet." under `ENABLE_NEO_MODE`; `DebugService.cs:268-271` `GetLocalVariableInfo` returns "Neo local variable inspection is not supported yet." The stacktrace instruction dump IS Neo-adapted (`DebugService.cs:143-148` reads `CompiledFrame.NeoExecuteBody`). The core variable-reading path is built around `StackObject*` + `intp.Stack.Frames.Peek().BasePointer` (`DebugService.cs:442-731`, `AddStackFrameInfoVariables`, `ResolveCurrentFrameBasePointer`, `DumpStack`, `GetValueExpandable`, `VisitValueTypeReference`, `GetStackObjectText`). | **DEFER** (deep `byte* frameBase` + `LocalInfos` work; graceful degradation already ships) |
| **E** CrossBindingAdapter | NO-OP (gap = F-4) | Step 19 wired `DelegateAdapter.NeoInvokeSub` (the CLR->IL callback). The `ExceptionAdaptor` (neo-il-exception-throw) works end-to-end (throw+catch green). The remaining gap is F-4 path 1 (`((CrossBindingAdaptorType)e).ILInstance` -> callvirt-on-CLR-interface -> `InvalidCastException`). | **DEFER** (= F-4 path 1; same cross-binding-adaptor follow-up) |

**Net: 1 SHIP-forge (A) + 1 SHIP-doc (C) + 3 DEFER (B / D / E).** This is
the expected partial-ship outcome.

## 2. Does Step 26 subsume F-4 (reflection-on-Neo)?

**NO.** F-4 (`NEO-IL-EX-FIELDACCESS`) is four INDEPENDENT broken read paths,
each blocked by a separate pre-existing Neo mechanism (callvirt-on-CLR-
interface; `callvirt.clr Object.GetType`; the `appdomain.Invoke` instance-
method re-entry path's Step-6 parameterless-only shim; the `ILTypeInstance`
Neo indexer). The deferred-items doc explicitly routes F-4 to "Step 13 Area 4 /
cross-binding-adaptor follow-up". Closing all four is a multi-mechanism
cross-binding-adaptor change, NOT a perf-validation step. Step 26 records F-4
as a deferred cross-reference in the spec delta and does NOT attempt the fix.
The working reflection coverage (catch matching via `CheckExceptionType` +
`isinst`) is already guarded by `NeoStep14_ILEx_*` (shipped by
neo-il-exception-throw).

## 3. Benchmark design (sub-surface A -- the core deliverable)

### 3.1 Why host-side timing (not interpreted Stopwatch)

The probe (`UnitTest_Performance2` under `Debug_Neo`) showed interpreted
`Stopwatch` does NOT NIE, BUT the harness marks `IsPerformanceTest` methods
IGNORED (no pass/fail, no clean timing line). Reusing the ignored perf-test
path gives no structured result. The host-side-timing approach (a self-check
that invokes bench methods via `appdomain.Invoke` and times them with a real
host `Stopwatch`) is robust and matches the Step-22..25 self-check pattern.

### 3.2 The bench probe (`TestCases/NeoStep26BenchProbe.cs`)

5 parameterless static methods, each a tight N-iteration loop returning a
primitive (`int`/`long`) -- the return value is the accumulated work, used as
a correctness-of-measurement check. Parameterless + primitive-return avoids
the Step-6 parameterless-Run shim limitation (F-11/F-12 family).

- `BenchFieldAccess()` -- N iterations of read+write an instance field on an
  IL class (exercises `Ldfld_*_Inline` / `Stfld_*_Inline` + the heap path).
- `BenchMethodCall()` -- N iterations of a static IL method call (exercises
  the Neo call ABI + `InvokeNeoCallTarget`).
- `BenchValueType()` -- N iterations of a value-type compute (an IL struct
  field read + arithmetic; exercises `Move_Vt` + the in-frame VT path).
- `BenchVirtualDispatch()` -- N iterations of a virtual / interface call
  (exercises the Neo VTable + `Callvirt` dispatch).
- `BenchArray()` -- N iterations of a rank-1 array element read+write
  (exercises `Ldelem_*` / `Stelem_*`).

N is chosen so a single bench takes a measurable-but-bounded wall-clock
(e.g. 1e6 iterations; tuned at apply so the whole suite is sub-second per
config -- the handoff's >10s = infinite-loop heuristic).

### 3.3 The host-side self-check (`NeoStep26BenchCheck.cs`)

`NeoStep26BenchCheck.Run(appdomain)` (gated `#if ENABLE_NEO_MODE && DEBUG`):

1. For each `(benchName, methodName, iterations, expectedReturn)` in a fixed
   table: `var sw = System.Diagnostics.Stopwatch.StartNew(); var result =
   appdomain.Invoke("ILRuntimeTest.NeoStep26BenchProbe", methodName, null,
   null); sw.Stop();`
2. Correctness-of-measurement gate: `if (!Equals(result, expectedReturn))
   int x = 1/0;` (the divide-assert pattern; the bench did the RIGHT WORK,
   not just some work). Record a failure if the divide-assert trips.
3. Emit a structured line: `BENCH:<benchName>:<iterations>:<sw.ElapsedTicks>`.
4. Return a result struct `{ Passed, Failed, TotalCells, Failures[] }`
   mirroring the Step-22..25 self-check shape.

The self-check does **NOT** assert a ratio threshold: this env is not the perf
baseline host (absolute timings are noise across machines / load). The
load-bearing gate is that each bench COMPLETED + returned the expected value
+ produced a finite positive timing. The Neo-vs-Legacy RATIO is computed by
the runner script across two CLI invocations. This respects "a green smoke
does not prove a gate correct" -- the gate proves measurement works + the
bench did the right work, NOT "Neo is fast" (which would be a false gate on a
non-baseline host).

### 3.4 The CLI hook (`ILRuntimeTestCLI/Program.cs`)

Add a `NeoStep26Bench` special-mode branch (mirrors the Step-25 hook at
lines 118-140): `if (nameFilter == "NeoStep26Bench") { var r =
NeoStep26BenchCheck.Run(session.Appdomain); ... return failed <= 0 ? 0 : -1; }`
under `#if ENABLE_NEO_MODE`.

### 3.5 The runner script (`scripts/run-neo-bench.{ps1,sh}`)

A small script that:
1. Builds the CLI under `Debug_Neo` + plain `Debug` (or assumes pre-built).
2. Runs `dotnet run ... -c Debug_Neo ... NeoStep26Bench`, captures stdout,
   parses the `BENCH:` lines into a `name -> ticks` map (the Neo timings).
3. Runs the same under plain `Debug` + `useRegister=true` (the Legacy
   timings). NOTE: the `NeoStep26BenchCheck` self-check is `#if
   ENABLE_NEO_MODE && DEBUG`, so under plain `Debug` the special-mode hook
   is absent. The runner instead invokes the bench probe methods via the
   GENERIC test loop (the `UnitTest`-style path) under plain `Debug` -- the
   probe methods are plain parameterless static methods, so they run under
   both engines. To get Legacy TIMINGS under plain `Debug`, the probe methods
   themselves emit a `BENCH:`-tagged line via a guarded `Console.WriteLine`
   when run through the generic loop. (Design detail finalized at apply: the
   probe methods write `BENCH:<name>:<iters>:<sw>` via an interpreted
   `Stopwatch` ONLY for the Legacy-config timing; under Neo the host-side
   self-check overrides with host timing. The dual-emit keeps both configs
   parsable by one script.)
4. Prints `name | neo_ms | legacy_ms | ratio` per bench.

(The dual-emit in 3.5/3 is an apply-time refinement; the core design is:
host-side timing under Neo, a parsable BENCH line under both configs, ratio
reported by the script.)

## 4. Single-threaded-contract documentation (sub-surface C)

A spec requirement (in the delta) stating: a single ILRuntime `AppDomain` +
its `ILIntepreter` pool is NOT safe for concurrent multi-threaded access.
The engine is single-threaded cooperative: the `UnityMainThreadID` checks
drive a coroutine pump (the `Thread.Sleep(10)` yield at `ILIntepreter.cs:62`),
NOT thread-safety. The delegate/async pool (`DelegateAdapter`,
`ILAsyncContext`) allocates a FRESH pooled interpreter per callback/resume to
isolate frame state -- it does NOT enable concurrent execution against one
AppDomain's maps (`mapTypeToken` / `mapMethod` / `LoadedTypes`). A host that
needs multi-threaded execution SHALL use one AppDomain per thread.

Code change: ONE comment block near the `UnityMainThreadID` check in
`ILIntepreter.cs` (around line 56) documenting this contract. No behavioral
change.

## 5. Deferrals (recorded in the spec delta + neo-deferred-items.md)

- **D `neo-debugger-neo-frame`** (new follow-up): Neo variable inspection in
  `DebugService`. Replace the "not supported yet" guards in `GetThisInfo` /
  `GetLocalVariableInfo` with reads of `byte* frameBase` + `AutoList mStack`
  + `CompiledFrame.LocalInfos` (the Neo frame layout). Touches ~8 methods
  (`DebugService.cs:203-261, 263-300, 442-731, 678-740, 1616-1847`). LARGE.
- **F-4 `NEO-IL-EX-FIELDACCESS`** (existing deferred item, NOT subsumed): the
  4-mechanism reflection-on-Neo fix. Route: a cross-binding-adaptor follow-up
  (callvirt-on-CLR-interface + `appdomain.Invoke` instance re-entry +
  `ILTypeInstance` Neo indexer).
- **E deep** = F-4 path 1 (the `ILInstance` cross-binding bridge). Same
  follow-up as F-4.

## 6. Capability + Legacy-neutrality

- **Capability:** `neo-optimizer` (primary; it already houses the Step-22..25
  AOT/self-check requirements -- templates, `.neo` format, loader, self-checks.
  The Step-26 benchmark self-check + the single-threaded-contract requirement
  + the deferral cross-references fit here).
- **Legacy `ExecuteR`:** byte-identical. The self-check is `#if
  ENABLE_NEO_MODE && DEBUG`; the CLI hook is `#if ENABLE_NEO_MODE`; the probe
  methods are plain parameterless static methods (runnable on both engines,
  no Neo dependency); C is a comment; the runner script is config-agnostic.
- **The binding lesson:** the benchmark compares Neo-vs-Legacy on the SAME
  workload (the SAME `TestCases.dll` bench probe methods), NOT just "Neo
  runs". A green bench self-check proves measurement works + the bench did the
  right work; it does NOT prove Neo is fast (the ratio script is the perf
  signal, and only on a dedicated baseline host).

## 7. Open questions (resolve at apply)

- **OQ1:** the dual-emit timing under plain `Debug` (3.5/3) -- finalize
  whether the probe methods emit a `BENCH:` line via interpreted `Stopwatch`
  for the Legacy config, OR the runner script drives the generic test loop +
  parses a host-side wrapper. Pick the simpler at apply (the interpreted-
  Stopwatch path is known-not-to-NIE from the probe).
- **OQ2:** N (iterations) tuning -- pick so the whole 5-bench suite is <1s
  per config (avoids the >10s infinite-loop heuristic while giving measurable
  timings). Confirm at apply on the dev host.
