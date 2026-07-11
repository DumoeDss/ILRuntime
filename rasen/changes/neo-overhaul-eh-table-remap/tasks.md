## 1. Reproduce / confirm the fault on HEAD (diagnose-first)

- [x] 1.1 Write the THROWING-EH probe in a new `TestCases/NeoStep*EhTableRemap*Test.cs`
      (TC1: `try`/`catch` whose try body calls a >3-register-param static method that
      throws; catch returns sentinel 42; assert `== 42`). Name embeds `NeoStep` so the
      smoke filter picks it up. Build TestCases with plain `Debug`.
- [x] 1.2 Run the probe on HEAD (`Debug_Neo` CLI, `-f net8.0`, `--no-build`) and
      confirm it FAULTS (the throw escapes the catch through the stale EH boundary ->
      unhandled). If it does NOT fault, increase the param count to 5-6 and/or add a
      statement before the call so the deleted Push sits before the try boundary;
      iterate until HEAD faults. If no shape faults, STOP and re-investigate (do not
      ship an unverified probe).
- [x] 1.3 Confirm the NeoStep smoke is otherwise still 339/0 on HEAD with the new
      probe file added (the new probe is the only failure).

## 2. Extract the EH-table build into an ILMethod helper (the ordering fix)

- [x] 2.1 In `ILRuntime/CLR/Method/ILMethod.cs`, extract the body of the
      `InitCodeBody` EH build (`:959-1000`, the `for` over
      `def.Body.ExceptionHandlers` writing `exceptionHandlerR` from `addr`) into a
      new helper, e.g. `void BuildExceptionHandlerRegister(Dictionary<Instruction,int>
      addr)`, Neo-gated (`#if ENABLE_NEO_MODE`) for the `exceptionHandlerR`
      (Register/Neo) target. Verbatim move -- no logic change.
- [x] 2.2 Make the `:959` site idempotent: wrap the `exceptionHandlerR` build in
      `if (exceptionHandlerR == null) BuildExceptionHandlerRegister(addr);` (Legacy
      `exceptionHandler` build stays as-is). For paths that reach `:959` without a
      Neo back-half, it still builds; for the Neo path it skips (already built in
      step 3).

## 3. Build the EH table before the back-half, at both RunNeoBackHalf funnels

- [x] 3.1 In `JITCompiler.cs` `Compile`, just BEFORE the `RunNeoBackHalf` call
      (`:664`), call `method.BuildExceptionHandlerRegister(addr)` (Neo-gated). Both
      `addr` and `this.method` are in scope.
- [x] 3.2 In `GenericMethodTemplate.cs` `CloneAndPatch`, AFTER the delta-shift block
      (`:677-717`, which finalizes `addr`) and BEFORE the `jit.RunNeoBackHalf` call
      (`:727`), call `instance.BuildExceptionHandlerRegister(addr)` (Neo-gated).
      `addr` and `instance` are in scope.
- [x] 3.3 Verify the EH table is now non-NULL during `LowerNeoOffsets` for Neo
      methods with protected regions (quick instrumented check or by the green smoke
      in step 6).

## 4. Re-map the EH table in FixBranchTargetsAfterRemove (the core fix)

- [x] 4.1 In `Optimizer.Neo.cs`, add an `ExceptionHandler[] ehs` parameter to
      `FixBranchTargetsAfterRemove` (trailing param). At the end of the function
      (after the branch/intermediate/Switch/symbols blocks), if `ehs != null`, loop
      over each entry and decrement `TryStart`, `TryEnd`, `HandlerStart`,
      `HandlerEnd` whenever the value is strictly `> removedIndex`. (No
      `FilterStart` -- the struct has none.)
- [x] 4.2 Add the same `ExceptionHandler[] ehs` parameter to `LowerNeoOffsets`
      (`:14`) and thread it into the `FixBranchTargetsAfterRemove` call inside the
      deletion loop (`:1256`). Neo-gated.
- [x] 4.3 In `JITCompiler.cs` `RunNeoBackHalf` (`:701`), pass
      `method.ExceptionHandlerRegister` as the new arg to `LowerNeoOffsets`
      (`this.method` is in scope; non-null after step 3.1).

## 5. Finalize the probe (TC1 + TC2)

- [x] 5.1 Finalize TC1 (the >3-arg throwing call first in the try -> mis-routes
      without the fix).
- [x] 5.2 Add TC2: a variant guarding the off-by-one direction and the handler
      range -- e.g. a statement BEFORE the throwing call in the try, and/or a
      `try`/`catch`/`finally` + >3-arg throwing newobj inside the try. Both must
      fault on HEAD with the fix stashed and pass with it.
- [x] 5.3 Confirm TC1/TC2 pass under Legacy too (plain `Debug` + `useRegister=true` +
      `NeoStep` filter) -- they are plain C# try/catch and must work everywhere.

## 6. Verify (gates)

- [x] 6.1 Build: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
      --no-incremental` (0 errors) and `dotnet build TestCases/TestCases.csproj -c
      Debug`.
- [x] 6.2 Stash-toggle: with the fix stashed (only the probe + the build-order
      plumbing reverted far enough that the EH table is stale again), TC1 AND TC2
      FAULT on HEAD; with the fix applied, both PASS.
- [x] 6.3 NeoStep smoke green and grown: `dotnet run -c Debug_Neo -f net8.0 --project
      ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll
      HotfixAOT/Patched/HotfixAOT.patch true NeoStep` -> 339 -> 341/0 (339 + TC1 +
      TC2; if a probe name matches the filter twice, account for the x2).
      `NeoStep14` and all EH-touching tests MUST be in the passing set.
- [x] 6.4 Legacy-neutral: plain `Debug` + `useRegister=true` + `NeoStep` filter ->
      same ran/failed baseline as before (both new probes pass under Legacy; the
      17-failure baseline holds).
- [x] 6.5 Confirm no `FilterStart` was introduced and no EH-dispatch logic
      (`CheckExceptionType`, catch/finally/fault handling) was modified.

## 7. Ship

- [ ] 7.1 SKIPPED by impl (LEAD/shipper owns commit; mandate says Do NOT commit). `git status` (confirm staged set), commit with trailer
      `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`, push
      (`git config lfs.useslockfiles false` if the push hits the locks endpoint).
- [x] 7.2 Append durable findings to
      `rasen/changes/neo-overhaul/planning-context.md` (EH-table representation +
      the four body-indexed fields; the lockstep per-deletion remap; the build-order
      fix at both `RunNeoBackHalf` funnels; the trigger shape; the "no FilterStart"
      note).
