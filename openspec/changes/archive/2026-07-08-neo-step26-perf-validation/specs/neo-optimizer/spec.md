# Spec Delta: neo-optimizer (for change neo-step26-perf-validation)

> Delta against `openspec/specs/neo-optimizer/spec.md`. Adds Step-26
> perf-validation requirements. PURE ASCII (a single non-ASCII byte breaks the
> openspec validator). Every requirement body starts with "... SHALL ..." on
> the first hard-wrapped line.

## ADDED Requirements

### Requirement: A host-side benchmark self-check measures Neo interpreter throughput on a fixed workload via appdomain.Invoke

The Neo code-path SHALL ship a host-side benchmark self-check
(`NeoStep26BenchCheck.Run(appdomain)`, gated `#if ENABLE_NEO_MODE && DEBUG`,
driven via the existing `ILRuntimeTestCLI` special-mode hook
`if (nameFilter == "NeoStep26Bench")`) that invokes a fixed set of
parameterless static bench methods on a dedicated probe type
(`TestCases/NeoStep26BenchProbe.cs`) through `appdomain.Invoke` and times each
invocation with a HOST-side `System.Diagnostics.Stopwatch` (NOT an interpreted
`Stopwatch`, because the generic test harness marks
`[ILRuntimeTest(IsPerformanceTest)]` methods IGNORED and emits no clean
timing line). The bench set SHALL cover five workload shapes: instance field
access + write (`BenchFieldAccess`), a static IL method call
(`BenchMethodCall`), a value-type compute (`BenchValueType`), a virtual or
interface dispatch (`BenchVirtualDispatch`), and a rank-1 array element
read + write (`BenchArray`). Each bench method SHALL be parameterless and
SHALL return a primitive (`int` or `long`) carrying the accumulated work, so
the self-check can assert the bench performed the correct computation (the
Step-6 parameterless-Run shim constraint and the F-12 reference-return
limitation are both avoided by the parameterless + primitive-return shape).
For each bench, the self-check SHALL assert the returned value EQUALS a
known-expected value via the divide-assert pattern
(`if (!Equals(result, expected)) int x = 1 / 0;`) -- this is the
correctness-of-measurement gate (the bench did the RIGHT WORK, not just some
work). The self-check SHALL emit one structured line per bench of the form
`BENCH:<benchName>:<iterations>:<elapsedTicks>` to stdout. The self-check
SHALL NOT assert a Neo-vs-Legacy ratio threshold, because the development
host is not the performance-baseline host and absolute timings are noise
across machines and load; the ratio is computed by a SEPARATE runner script
(`scripts/run-neo-bench.{ps1,sh}`) across two CLI invocations, not by the
self-check. A green self-check proves measurement works AND each bench
computed its expected value; it does NOT prove Neo is fast.

The bench probe type + the self-check SHALL be additive and Neo-only: the
self-check file SHALL be gated `#if ENABLE_NEO_MODE && DEBUG`, and the CLI
special-mode hook SHALL be gated `#if ENABLE_NEO_MODE`. The probe methods
SHALL be plain parameterless static methods with NO Neo dependency (they are
runnable by BOTH `ExecuteNeo` and `ExecuteR`, which is what makes the
Neo-vs-Legacy comparison apples-to-apples on the SAME `TestCases.dll`).

#### Scenario: The bench self-check runs the five workloads and emits BENCH lines
- **WHEN** `NeoStep26BenchCheck.Run(appdomain)` is invoked host-side
  (DEBUG+Neo) via the `NeoStep26Bench` CLI hook
- **THEN** it SHALL invoke each of `BenchFieldAccess`, `BenchMethodCall`,
  `BenchValueType`, `BenchVirtualDispatch`, and `BenchArray` via
  `appdomain.Invoke`, time each with a host `Stopwatch`, and emit a
  `BENCH:<name>:<iterations>:<ticks>` line per bench
- **AND** each bench SHALL return its known-expected primitive value (the
  divide-assert does NOT trip), proving the bench computed the correct result

#### Scenario: The self-check gates on measurement correctness, not on a ratio
- **WHEN** the self-check runs on the development host (which is NOT the
  performance-baseline host)
- **THEN** it SHALL NOT fail on the absolute timing or on any Neo-vs-Legacy
  ratio (no ratio threshold is asserted)
- **AND** it SHALL fail ONLY if a bench throws, returns a wrong value (the
  divide-assert trips), or produces a non-positive timing
- **AND** the Neo-vs-Legacy ratio SHALL be reported by the separate runner
  script, not asserted by the self-check

#### Scenario: The runner script compares Neo and Legacy on the same workload
- **WHEN** `scripts/run-neo-bench.{ps1,sh}` runs the CLI under `Debug_Neo`
  and under plain `Debug` with `useRegister=true` on the SAME
  `TestCases.dll`
- **THEN** it SHALL parse the `BENCH:` lines from both configs and print a
  per-bench `name | neo_ms | legacy_ms | ratio` table
- **AND** the bench probe methods SHALL be identical across both configs (the
  comparison is the SAME workload interpreted by `ExecuteNeo` vs `ExecuteR`)

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and runs the Legacy
  register VM
- **THEN** the `NeoStep26BenchCheck` self-check and the CLI special-mode hook
  SHALL compile out (they are gated `#if ENABLE_NEO_MODE`)
- **AND** the bench probe methods SHALL remain compilable plain static methods
  (they have no Neo dependency); the Legacy NeoStep-filter smoke SHALL show
  the SAME pre-existing failure set with and without this change

#### Scenario: NeoStep regression smoke stays green
- **WHEN** the benchmark self-check + the probe type + the CLI hook are added
  (`Debug_Neo`)
- **THEN** the full `NeoStep` smoke SHALL stay 215/215 (ZERO regressions),
  because the bench self-check runs under a SEPARATE filter (`NeoStep26Bench`)
  and does NOT run under the `NeoStep` regression filter
- **AND** the `NeoStep` filter SHALL NOT match the bench probe methods (the
  probe methods' names do not contain the substring `NeoStep`)

### Requirement: A single AppDomain and its interpreter pool are not safe for concurrent multi-threaded access (single-threaded cooperative contract)

The ILRuntime `AppDomain` + its `ILIntepreter` pool SHALL be documented as
NOT safe for concurrent multi-threaded access. The engine is single-threaded
cooperative: the `Thread.CurrentThread.ManagedThreadId ==
AppDomain.UnityMainThreadID` checks in the interpreter
(`ILIntepreter.cs:56,151,2164,4723`, `ILIntepreter.Neo.cs:829,4451`,
`ILIntepreter.Register.cs:91,3044,5362`) drive a cooperative coroutine pump
(the `Thread.Sleep(10)` yield at `ILIntepreter.cs:62`), NOT thread-safety.
The delegate and async paths (`DelegateAdapter.NeoInvokeSub`,
`ILAsyncContext` resumption) allocate a FRESH pooled interpreter per callback
or resumption to ISOLATE frame state across sequential callbacks; they do NOT
enable concurrent execution against a single AppDomain's token maps
(`mapTypeToken`, `mapMethod`, `LoadedTypes`, the string interner), which have
NO synchronization. A host that needs multi-threaded execution SHALL use one
AppDomain per thread. This requirement is DOCUMENTATION-only: the change
SHALL add a comment block near the `UnityMainThreadID` check in
`ILIntepreter.cs` stating the contract; it SHALL NOT add synchronization
(the contract is single-threaded, not lock-based) and SHALL NOT alter any
runtime behavior.

#### Scenario: The contract is documented at the thread-check site
- **WHEN** a maintainer reads the `UnityMainThreadID` check in
  `ILIntepreter.cs` (around line 56)
- **THEN** a comment block SHALL state that a single AppDomain is not safe
  for concurrent multi-threaded access, that the pool isolates per-callback
  frame state (it does not enable concurrency), and that a host needing
  multi-threaded execution SHALL use one AppDomain per thread
- **AND** no runtime behavior SHALL change (no lock added, no branch altered)

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE`
- **THEN** the comment SHALL remain (it is in shared `ILIntepreter.cs`), and
  the Legacy register VM SHALL be byte-identical to before this change

### Requirement: Neo debugger variable inspection and reflection-on-Neo field reads are deferred follow-ups (NOT Step 26 scope)

Step 26 SHALL NOT deliver Neo variable inspection in the debugger NOR the
reflection-on-Neo field-read fix (the F-4 / NEO-IL-EX-FIELDACCESS family).
These are explicitly DEFERRED follow-ups, recorded here so a future change
finds them. (1) The debugger variable-inspection paths in
`DebugService.cs` (`GetThisInfo` at :203-261, `GetLocalVariableInfo` at
:263-300, and the frame-variable readers at :442-731 and :678-740) are built
around the Legacy `StackObject*` + `BasePointer` frame; under
`ENABLE_NEO_MODE` they ALREADY degrade gracefully -- `GetThisInfo` returns
"Neo this inspection is not supported yet." (`DebugService.cs:207-209`) and
`GetLocalVariableInfo` returns "Neo local variable inspection is not
supported yet." (`DebugService.cs:268-271`). A real fix SHALL read the Neo
frame (`byte* frameBase` + `AutoList mStack` + `CompiledFrame.LocalInfos`)
and SHALL touch the variable-reading methods; this is substantial deep
debugger-protocol work routed to a dedicated `neo-debugger-neo-frame`
follow-up. The stacktrace instruction dump is ALREADY Neo-adapted (it reads
`CompiledFrame.NeoExecuteBody` at `DebugService.cs:143-148`) and is NOT
deferred. (2) The F-4 reflection-on-Neo fix (four independent broken read
paths: callvirt-on-CLR-interface for the `ILInstance` bridge; `callvirt.clr`
on `Object.GetType`; the `appdomain.Invoke` instance-method re-entry path
whose Step-6 shim handles only parameterless static methods; and the
`ILTypeInstance` Neo indexer which returns null because the indexer +
accessors are `#if !ENABLE_NEO_MODE` at `ILTypeInstance.cs:27,86,94,379,...`)
SHALL be routed to a cross-binding-adaptor follow-up; Step 26 does NOT
subsume F-4. The working reflection coverage (catch matching via
`CheckExceptionType` + `isinst`) is already guarded by the `NeoStep14_ILEx_*`
probes shipped by `neo-il-exception-throw` and SHALL NOT be re-tested here.
Step 26 SHALL NOT ship a fix for either deferred surface; a future change
SHALL own them.

#### Scenario: Step 26 does not touch the debugger variable-inspection paths
- **WHEN** Step 26 ships
- **THEN** `DebugService.cs` SHALL be unmodified (the existing "not supported
  yet" graceful-degradation guards SHALL remain verbatim)
- **AND** no Neo variable-inspection code SHALL be added by this change

#### Scenario: Step 26 does not subsume F-4
- **WHEN** Step 26 ships
- **THEN** no fix for the four F-4 reflection-on-Neo read paths SHALL be
  shipped (the `ILTypeInstance` Neo indexer stays absent; the callvirt-on-
  CLR-interface + `appdomain.Invoke` instance-reentry + `callvirt.clr GetType`
  paths stay unfixed)
- **AND** the F-4 deferred item SHALL remain open in
  `.trae/documents/neo-deferred-items.md`, routed to a cross-binding-adaptor
  follow-up (NOT marked resolved by Step 26)

#### Scenario: The deferrals are cross-referenced in the deferred-items tracker
- **WHEN** Step 26 archives
- **THEN** `neo-deferred-items.md` SHALL record the `neo-debugger-neo-frame`
  follow-up (the D debugger deferral) with the suspect sites pinned
  (`DebugService.cs:203-261, 263-300, 442-731, 678-740`)
- **AND** the F-4 row SHALL remain intact and SHALL NOT be marked resolved
  by Step 26
