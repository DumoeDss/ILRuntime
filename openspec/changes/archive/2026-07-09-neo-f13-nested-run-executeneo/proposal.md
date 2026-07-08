## Why

F-13 / NEO-NESTED-RUN-EXECUTENEO was recorded as an open defect: nesting
`appdomain.Invoke(...)` (-> a 2nd `Run` -> a 2nd `ExecuteNeo` on a fresh pooled
interpreter) from WITHIN an in-flight `ExecuteNeo` was believed to corrupt the
OUTER method's instruction pointer -- the outer `ip` would run off the end of
its body into a garbage opcode (`NotImplementedException: Neo: opcode <random>
not yet implemented`, `ipOff` past the body length). The suspected root cause was
"a process-static shared across per-interpreter frames" (a `fixed` pin
invalidated by a GC the inner JIT triggers, or a shared `Stack`/
`ValueTypePointer`, or incomplete interpreter-pool isolation).

The binding task on HEAD `7a0f4cbd` was to ISOLATE the root cause before any fix.
Isolation via an adversarial probe (a method that nests `appdomain.Invoke`
mid-`ExecuteNeo`, plus the exact F-4 #3 catch-handler shape, plus forced GC
`GC.Collect(MaxGeneration, Forced, blocking)` + 2000 allocations during the
nested call) shows **F-13 is NOT reproducible**: the outer `ip` survives intact,
both cells return the correct result (13), and the runaway-`ip` signature never
fires. The four candidate mechanisms (process-static `ip`; stale `fixed` pin on
`NeoExecuteBody`; GC relocation of the pinned frame; pool-isolation gap) are each
eliminated by static evidence + the live probe (see `design.md` section 1).

The corruption originally observed in `neo-f4-parametrized-run-entry` was a
SYMPTOM of the same Step-6 parameterless-only `Run` shim that caused F-4 #3 and
F-12 -- NOT a re-entrancy engine bug. That shim was fixed by
`neo-f4-parametrized-run-entry` (2026-07-08) + `neo-f4-surfaced-gaps`
(2026-07-09, the `ReadNeoReference` null-sentinel + `ILType` base-field
accumulation). With those shipped, nested `appdomain.Invoke` is correct.

## What Changes

- **No engine source change.** The interpreter (`ILIntepreter.Neo.cs`
  `ExecuteNeo`), the pool (`AppDomain.RequestILIntepreter`/`FreeILIntepreter`),
  and `ILIntepreter.Run` are UNCHANGED -- the isolated root cause is "no defect
  on HEAD; the candidates are all impossible by construction" (design section 1).
- **Ship the adversarial probe as a permanent regression guard** so the (now-
  proven-correct) re-entrancy cannot silently regress. The probe exercises the
  exact F-4 #3 / F-13 nesting shape: an IL method that calls a CLR bridge (whose
  Neo redirect performs `appdomain.Invoke` on a FRESH pooled interpreter while
  the outer `ExecuteNeo` is in flight), driven both as a plain mid-body call AND
  from inside a catch handler, with GC stress inside the redirect. CLI mode
  `NeoF13Nested` runs it; both cells SHALL return 13.
- **Document the eliminated hypotheses** in `design.md` so a successor does not
  re-explore the dead ends (a `fixed` pin on `NeoExecuteBody` cannot be relocated
  by a GC; `StackBase` is `AllocHGlobal` unmanaged; `ip` is a per-frame local;
  the pool hands out independent interpreters with independent stacks).
- **Resolve the deferred-items F-13 row** to RECHARACTERIZED (was "accepted-
  known, sequenced"; now "no defect -- regression-guarded").

## Capabilities

### New Capabilities

_(none -- this extends the existing Neo re-entrancy/dispatch contract)_

### Modified Capabilities

- `neo-dispatch`: the host -> IL re-entrancy contract under Neo mode. Currently
  the spec covers the single-level host -> IL `Run` re-entry; this adds the
  requirement that nesting `appdomain.Invoke` (a 2nd `Run`/`ExecuteNeo` on a
  fresh pooled interpreter) from within an in-flight `ExecuteNeo` (whether as a
  plain mid-body CLR call or from inside a catch handler) SHALL leave the outer
  frame's instruction pointer intact, and SHALL be guarded by an adversarial
  regression probe.

## Impact

- **Source (Neo-only test surface):** NEW `ILRuntimeTestCLI/NeoF13NestedProbe.cs`
  (the CLI hook driver + the Neo redirect that performs the nested
  `appdomain.Invoke` with GC stress); NEW
  `ILRuntimeTestBase/TestFramework/NeoF13Bridge.cs` (the CLR bridge type the IL
  probe calls); NEW IL probe methods `F13_Inner`, `NeoStep14_F13_NestedInvokeProbe`,
  `NeoStep14_F13_NestedInCatchProbe` in `TestCases/NeoStep14Test.cs`; NEW CLI
  hook `NeoF13Nested` in `ILRuntimeTestCLI/Program.cs`; CLI csproj
  `Debug_Neo` `DefineConstants` now includes `DEBUG;TRACE` and
  `AllowUnsafeBlocks=true` (needed for the `unsafe` Neo redirect delegate).
- **Engine source:** UNCHANGED (`ILIntepreter.Neo.cs`, `ILIntepreter.cs`,
  `AppDomain.cs` -- no edits).
- **Legacy:** byte-identical (all additions are Neo-mode-gated or test-only;
  the bridge is plain C# usable by both, the redirect is `#if ENABLE_NEO_MODE`).
- **Regression surface:** the probe IS the regression gate. Full `NeoStep` smoke
  stays green (229/0/0 on HEAD with the 3 temp probe methods counted; 226/0/0
  baseline minus the temp methods). Legacy plain-`Debug` build = 0 errors
  confirms Legacy-neutrality.
- **Deferred-items resolution:** closes the F-13 / NEO-NESTED-RUN-EXECUTENEO row
  (RECHARACTERIZED: no defect; regression-guarded) and unblocks any future IL-
  side `appdomain.Invoke` re-entry probe that was previously parked behind F-13.
