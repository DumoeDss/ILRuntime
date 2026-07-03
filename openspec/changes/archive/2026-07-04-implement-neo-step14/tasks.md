# Tasks -- implement-neo-step14

Single-implementer pass. All runtime edits go in
`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`, behind
`#if ENABLE_NEO_MODE`. DO NOT modify Legacy (`ILIntepreter.Register.cs`,
`ILIntepreter.cs`) -- the shared `HandleException` /
`GetCorrespondingExceptionHandler` / `FindExceptionHandlerByBranchTarget` are
REUSED as-is.

Each phase ends with: rebuild CLI
(`dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` -> 0
errors) + rebuild TestCases
(`dotnet build TestCases/TestCases.csproj -c Debug` -> 0 errors; NEVER
Debug_Neo) + full NeoStep smoke
(`dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build --
TestCases/bin/Debug/netstandard2.1/TestCases.dll
HotfixAOT/Patched/HotfixAOT.patch true NeoStep` -> all green; was 49/49 after
Step 13). `-f net8.0` mandatory. A test >10s = infinite loop (exception-unwind
bugs loop) -- kill and investigate. `Debug_Neo` prints lots of JIT/optimizer
output -- normal.

Key constraints (from proposal.md "throw/newobj dependency"):
- `throw new T(...)` for CLR types is NOT testable (Neo CLR newobj is
  `NotImplementedException` Step 9). Throw via `int x = 1/0;` (raises
  `DivideByZeroException`) or null IL-instance field access (raises
  `NullReferenceException`).
- IL-type `newobj` IS available, but IL-thrown exceptions need a CLR
  exception base to be useful in catch -- so prefer the `1/0` / null-deref
  sources for all test cases.

---

## Phase 1 -- Map exact current state (read-only)

- [x] 1.1 Confirm `Throw`/`Leave`/`Leave_S`/`Endfinally`/`Rethrow`/`Endfilter`
      have NO arm in the `ExecuteNeo` switch (they fall to `default` ->
      `NotImplementedException("... (Step 6)")` at `ILIntepreter.Neo.cs:2021`).
- [x] 1.2 Confirm the outer try-catch + shared `HandleException` call is LIVE
      (`ILIntepreter.Neo.cs:2025-2058`) and the `isCatch` mStack truncation
      (`2033-2037`) already runs; the only catch-entry gap is the
      `TODO: write exception object into the catch handler's slot (Step 14)`
      at `2038`.
- [x] 1.3 Confirm `ehs = method.ExceptionHandlerRegister` is populated for any
      method with try/catch/finally (`ILMethod.cs:721-761`), shared with Legacy.
- [x] 1.4 Confirm cross-frame `return null` leak sites:
      `Call` (1396-1397), `Newobj` (1433-1434), `Callvirt_IL` (1457),
      `Callvirt_Interface` (1503), `Callvirt_CLR`/generic (1527) -- each does
      `if (!InvokeNeoCallTarget(...)) return null;` on callee-unhandled, which
      bypasses the caller's outer catch AND the bottom cleanup (`2062-2071`).
      NOTE (see §8.8): for IL callees the Neo unhandled path THROWS (C#
      propagation), so these `return null` sites are only reached in debugger
      mode; functional cross-frame propagation already worked via C# throw +
      the shared HandleException frame-pop loop. Phase 4 made the unhandled
      path self-cleaning (cleanup runs before re-throw).

## Phase 2 -- Throw / Rethrow arms (single-frame catch)

- [x] 2.1 Add a `GetNeoException(AutoList mStack, int idx)` helper (or inline):
      `idx < 0` -> `throw new NullReferenceException()`; else return
      `mStack[idx] as Exception` (unwrap `ILRuntimeException` is handled
      downstream by `HandleException`, so throw the object as-is).
- [x] 2.2 Implement `case OpCodeREnum.Throw:` reading
      `*(int*)(frameBase + ip->Register1)` as the ref-slot index and
      `throw`-ing the exception into the outer try-catch. NOTE: `Throw` is
      NOT lowered by LowerNeoOffsets, so `Register1` is a raw register index;
      resolve via `localInfos[Register1].Offset` (the byte slot holds the
      mStack index), matching how `Ret` reads its source.
- [x] 2.3 Implement `case OpCodeREnum.Rethrow:` as `throw lastCaughtEx;`.
- [x] 2.4 Determine the catch-handler exception-variable ref slot. Resolved via
      a JIT-side annotation (see §8.9): the catch exception register
      (baseRegStart = paramCnt + locCnt) is protected from CleanupRegister
      compaction and its post-compaction index + byte/ref offsets are stamped
      onto CompiledFrame (`NeoCatchExceptionRegIndex` /
      `NeoCatchExceptionByteOffset` / `NeoCatchExceptionRefOffset`). Mirrors
      Legacy `AssignToRegister(exReg = paramCnt + locCnt, ex)`.
- [x] 2.5 In the `isCatch` branch, write `ex` (already unwrapped by
      HandleException) into the stamped catch slot.
- [x] 2.6 Build + add TC1/TC2. Green + no regression. (TC2 uses
      `catch (DivideByZeroException e)` + `e != null` instead of
      `e is DivideByZeroException`, since isinst/castclass are Step 15;
      catch-type matching is done by the shared engine, no isinst needed.)

## Phase 3 -- Leave / Endfinally arms (finally guarantee)

- [x] 3.1 Implement `case OpCodeREnum.Leave: case OpCodeREnum.Leave_S:`
      mirroring Legacy (`FindExceptionHandlerByBranchTarget` + finallyEndAddress
      sentinel). Ported verbatim; only `ip` arithmetic differs (ptr-relative).
- [x] 3.2 Implement `case OpCodeREnum.Endfinally:` mirroring Legacy
      (finallyEndAddress<0 -> throw lastCaughtEx; else route via
      FindExceptionHandlerByBranchTarget or jump to finallyEndAddress).
- [x] 3.3 Add TC3 (finally on exception) and TC4 (finally on normal Leave).
      Green + no regression.

## Phase 4 -- Nested + cross-frame propagation (the structural fix)

- [x] 4.1 Add TC5 (nested try-catch innermost wins) -- passes via shared
      nearest-match engine.
- [x] 4.2 (Merged into 4.4) The callee throws `pendingThrow` (an
      ILRuntimeException wrapping the original) via C# propagation; no separate
      `out Exception` plumbing needed because the C# throw already carries the
      object into the caller's per-iteration catch.
- [x] 4.3 The `return null` sites are kept as a debugger-mode safety net (they
      are unreachable for IL callees in non-debug runs because the callee now
      always THROWS on unhandled, never returns false). Functional cross-frame
      propagation is via the C# throw -> caller's catch -> HandleException
      against the caller's ehs at the call-site address. See §8.8.
- [x] 4.4 Restructured the unhandled path: `pendingThrow` is stashed in the
      per-iteration catch, the loop breaks, the bottom cleanup (frame pop +
      mStack truncate to frameRefBase) runs, and `pendingThrow` is re-thrown
      AFTER cleanup (outside the `fixed` block). Every Neo frame is now
      self-cleaning; the caller's HandleException frame-pop loop finds the
      caller's frame on top (callee already popped) and needs no cleanup of
      the callee.
- [x] 4.5 Add TC6 (cross-frame catch) and TC7 (cross-frame caller finally).
      Green + no regression (58/58 NeoStep).

## Phase 5 -- NRE catch + final smoke

- [x] 5.1 Add TC8 (NullReferenceException caught from null IL-instance field
      access). Green.
- [x] 5.2 `Endfilter` remains NIE (Step-tagged) with a one-line "out of scope
      for Step 14" comment at its arm.
- [x] 5.3 Final full NeoStep smoke: 58/58 green (49 baseline + 9 new), no
      pre-existing case regressed. (Added TC9 rethrow beyond the spec list.)
- [x] 5.4 No NeoStep14 test exceeds 10s (full 58-test NeoStep suite completes
      well within the 300s timeout; no infinite-loop symptom).

## Out of scope (do NOT pull in)

- async exception propagation (Step 20).
- CLR `newobj` (Step 18) -- so no `throw new T(...)` test for CLR types.
- IL `filter` / `Endfilter`.
- stack-overflow guard (`ILIntepreter.Neo.cs:324`; Step 26).
- Any change to Legacy `ExecuteR` or the shared `HandleException`.
