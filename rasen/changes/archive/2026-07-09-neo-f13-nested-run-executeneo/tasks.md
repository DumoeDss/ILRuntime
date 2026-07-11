# Tasks - neo-f13-nested-run-executeneo

> The dump-gate ISOLATED the root cause: F-13 is NOT reproducible on HEAD
> `7a0f4cbd` (the four candidate mechanisms are impossible by construction AND
> disproven by a live adversarial probe; see design.md section 1). There is NO
> engine source change. The work below ships the adversarial probe as a permanent
> regression guard. The probe + scaffolding are implemented and PASSING; the
> remaining tasks are verification + documentation.

## 1. Isolation (DONE -- the binding dump-gate)

- [x] **1.1** Static-evidence elimination of the four candidate mechanisms:
  `ip` is a per-frame local (`ILIntepreter.Neo.cs:927-929`); the `fixed` pin on
  `NeoExecuteBody` is GC-stable (pinned arrays are not relocated); the frame is
  on `AllocHGlobal` unmanaged memory (`RuntimeStack.cs:35`); the pool is fully
  isolated (`AppDomain.cs:1855-1907`, each interpreter owns its own stack +
  mStack). Recorded in design.md section 1.
- [x] **1.2** Live adversarial probe confirms NOT reproducible: nested
  `appdomain.Invoke` from BOTH a plain mid-body CLR call AND a catch handler,
  with forced `GC.Collect(MaxGeneration, Forced, blocking)` x2 + 2000
  allocations + depth-2 nesting, returns the correct result (13) on both cells;
  a temp runaway-`ip` diagnostic NEVER fired. design.md section 0.

## 2. Regression-guard probe (DONE -- implemented + passing)

- [x] **2.1** CLR bridge type: `ILRuntimeTestBase/TestFramework/NeoF13Bridge.cs`
  (`NeoF13Bridge.NestedInvoke()` -- the CLR static the IL probe calls; its body
  is unreachable, the redirect supplies the re-entry).
- [x] **2.2** IL probe methods in `TestCases/NeoStep14Test.cs`: `F13_Inner`
  (inner, returns 7); `NeoStep14_F13_NestedInvokeProbe` (plain mid-body nesting,
  returns 13); `NeoStep14_F13_NestedInCatchProbe` (the F-4 #3 catch-handler
  shape, returns 13).
- [x] **2.3** Neo redirect + driver: `ILRuntimeTestCLI/NeoF13NestedProbe.cs`.
  Registers `NestedInvoke_Neo` (GC stress + depth-1 `appdomain.Invoke(F13_Inner)`
  + depth-2 `appdomain.Invoke(EchoRef)` + writes the inner int to the caller
  dest). `Run(appdomain)` drives both cells and reports PASS/FAIL.
- [x] **2.4** CLI hook: `NeoF13Nested` nameFilter in `ILRuntimeTestCLI/Program.cs`
  (mirrors `NeoF4ParamRun`).
- [x] **2.5** csproj: `ILRuntimeTestCLI/ILRuntimeTestCLI.csproj` `Debug_Neo`
  PropertyGroup -- add `DEBUG;TRACE` to `DefineConstants` +
  `AllowUnsafeBlocks=true` (the `unsafe` Neo redirect delegate needs it; the
  constants normalize the config to what the SDK normally provides).

## 3. Verification (the binding gate -- a green smoke does NOT prove it)

- [x] **3.1** `NeoF13Nested` probe: BOTH cells return 13 on HEAD `7a0f4cbd`
  (Cell 1 plain-nested PASS; Cell 2 catch-nested PASS). Run:
  `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build --
  TestCases/bin/Debug/netstandard2.1/TestCases.dll
  HotfixAOT/Patched/HotfixAOT.patch true NeoF13Nested`
- [x] **3.2** Probe passes WITHOUT the temp runaway-`ip` diagnostic (the
  diagnostic was removed; the unmodified interpreter still passes -- the probe
  itself, not the diagnostic, is the guard).
- [x] **3.3** NeoStep regression: 229/0/0 (226 baseline + 3 probe methods, all
  pass). Run the `NeoStep` filter per the planning-context build/test block.
- [x] **3.4** Legacy-neutrality: plain-`Debug` build of TestCases + CLI = 0
  errors (the bridge is plain C#; the redirect + probe are Neo-gated).

## 4. Documentation (DONE)

- [x] **4.1** `proposal.md`, `design.md` (the ISOLATED root cause + eliminated
  hypotheses, file:line-cited), `specs/neo-dispatch/spec.md` (delta, PURE ASCII,
  SHALL-first), `tasks.md`.
- [ ] **4.2** (post-ship) Update `.trae/documents/neo-deferred-items.md` F-13 row
  from "accepted-known (pre-existing; SEQUENCED)" to "RECHARACTERIZED 2026-07-09
  (neo-f13-nested-run-executeneo): NOT reproducible on HEAD -- the four candidate
  mechanisms eliminated by static evidence + a live adversarial probe; the
  originally-observed corruption was the Step-6 `Run` shim (fixed by
  neo-f4-parametrized-run-entry + neo-f4-surfaced-gaps). Regression-guarded by
  the `NeoF13Nested` CLI probe." (Sequenced to the ship/apply step.)

## 5. Out of scope

- Any engine source change (none needed; the probe passes on the unmodified
  interpreter).
- Re-enabling the IL-side F-4 #3 probe (the host-side `NeoF4ParamRun` cells +
  this nested guard are the cleaner gates).
