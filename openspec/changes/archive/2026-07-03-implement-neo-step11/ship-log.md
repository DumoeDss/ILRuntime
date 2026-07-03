# Ship Log - implement-neo-step11

Change: implement-neo-step11
One-line: Neo Step 11 - interface method dispatch (Neo mode `callvirt` to an
interface method via `Callvirt_Interface`, resolved through a per-type interface
offset map layered on the Step 10 class VTable).

## Ship verdict: CLEAN

No open Blocker. No open Major. All findings either resolved in review-loop
round 1 or accepted-known (deferred with an in-code contract enforcement).

## Verification evidence (build + tests)

Build (development subset only - sln cannot build whole; see CLAUDE.md):

- `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`
  -> 0 errors (transitively builds ILRuntime / ILRuntimeTestBase / LitJson).
- `dotnet build TestCases/TestCases.csproj -c Debug`
  -> 0 errors (produces TestCases/bin/Debug/netstandard2.1/TestCases.dll;
  TestCases is NEVER built with Debug_Neo).

NeoStep smoke (Debug_Neo CLI, useRegister=true):

- Filter `NeoStep`: 31 ran / 0 failed.
  Includes +5 new NeoStep11 cases (single IL interface, override via base-typed
  interface variable, multiple interfaces, interface inheritance chain, CLR
  interface IDisposable on the IL side). Existing NeoStep cases still green.

NeoStep10 regression (Step 10 virtual dispatch must not regress):

- Filter `NeoStep10`: 5 ran / 0 failed.

Commands used:

- dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll \
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep
- dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll \
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep10

## Review summary

Verdict from review-report.md: 0 Blocker / 0 Major / 3 Minor / 3 Trivial.

- M2 (Minor) - resolved in review-loop round 1 by a comment-only clarification
  on NeoStep11TestClrInterfaceIDisposable: the CLR-interface path uses the
  generic Callvirt arm, not Callvirt_Interface. Non-author re-reviewer
  confirmed clean.
- M3 (Minor) - resolved in review-loop round 1 by cross-referencing INVARIANT
  comments at BuildNeoSelfMethodSlots + AddNeoInterfaceEntry documenting the
  load-bearing slot-ordering equivalence. Non-author re-reviewer confirmed
  clean.
- M1 (Minor) - ACCEPTED-KNOWN, not a regression. See below.
- 3 Trivial findings: noted, no action required for ship.

No new findings surfaced in round 1. Review-loop closed clean.

## Accepted-known

- M1 (Minor): the negative failure-path scenario (object whose runtime type
  does not implement the target interface) has no green end-to-end test. The
  test harness lacks `Leave_S` (lands in Step 6), so an exception-asserting
  case cannot be expressed yet. The handler still enforces the clear-exception
  contract in code, throwing `MissingMethodException` with no instruction
  pointer overrun and no null dereference, at:
  - ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs:257
  - ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs:264
  - ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs:270
  Follow-up: add the negative test when Step 6 (`Leave_S`) lands.

## Files changed (7 source files: 6 runtime + 1 test)

Runtime:

- ILRuntime/CLR/TypeSystem/ILType.cs - InterfaceEntry struct, neoInterfaceMap /
  neoInterfaceOffsets / neoInterfaceMapBuilding fields, EnsureNeoInterfaceMap /
  BuildNeoInterfaceMap, query API (GetInterfaceVTableOffset,
  TryGetInterfaceVTableOffset, TryGetInterfaceMethodSlot,
  GetInterfaceMethodSlotSelf).
- ILRuntime/Runtime/Intepreter/OpCodes/OpCodeREnum.cs - Callvirt_Interface
  enum value.
- ILRuntime/Runtime/Intepreter/OpCodes/OpCode.cs - ToString arm for
  Callvirt_Interface.
- ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs - interface branch in
  InitializeCallvirtDispatch (emits Callvirt_Interface, encodes interface
  method slot), EncodeCallvirtInterface helper, thisArgOffset patching.
- ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs - Callvirt_Interface
  added to the call-ABI arm alongside the other Callvirt_* variants.
- ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Utils.cs - Callvirt_Interface
  enumerated wherever the optimizer lists Callvirt_*.
- ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs - case arm for
  Callvirt_Interface, ResolveNeoCallvirtInterfaceTarget (offset-map lookup +
  bounds check + MissingMethodException on failure).

Test:

- TestCases/NeoStep11Test.cs - NeoStep11 smoke cases (single interface, override
  via base-typed interface variable, multiple interfaces, interface inheritance
  chain, CLR interface IDisposable).

## Git status

All changes are uncommitted on branch `features/object-model-overhaul`. Git
commit / push is intentionally left as an explicit user decision and is NOT
part of this openspec finalization (no remote PR is created by the ship stage
in this repo - the openspec-gstack-ship Rails/JS machinery is a misfit here;
see auto-run.json ship stage note).

## Pipeline trace

propose done -> apply done (28/28 tasks) -> verify done (no Blocker/Major) ->
review-loop done (round 1, M2+M3 resolved, clean) -> ship (this log) ->
archive (sync delta + move to archive/).
