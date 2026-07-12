## 1. Confirm the fix is already on HEAD (no engine edit needed)

- [ ] 1.1 Read `ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs` and
      confirm `AsyncValueTaskMethodBuilder_T_SetResult_Neo` (line ~449) and
      `AsyncTaskMethodBuilder_T_SetResult_Neo` (line ~287) both skip via
      `curPrim += BuilderThisManagedSize(method)` (NOT a hardcoded `+= 8`).
- [ ] 1.2 Confirm `AsyncValueTaskMethodBuilder_T_SetException_Neo` delegates to
      `AsyncTaskMethodBuilder_T_SetException_Neo`, which skips via
      `BuilderThisManagedSize` (line ~317).
- [ ] 1.3 Confirm `BuilderThisManagedSize` (line ~1702) resolves to
      `Optimizer.GetNeoValueTypeManagedSize(decl)` = `Unsafe.SizeOf<T>` for a
      value-type declaring builder (defensive fallback 8 only for a non-VT `this`).
- [ ] 1.4 Confirm `git diff HEAD -- ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs`
      is EMPTY (the fix is committed, not a working-tree/stash artifact) and that
      no `VTDBG2` diagnostics remain (`grep -rn VTDBG2 ILRuntime/ TestCases/` is empty).

## 2. Verify the ValueTask async cluster is green (runtime evidence)

- [ ] 2.1 Run the VT-focused smoke:
      `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep20_VT`
      -> expect `Ran 7 tests, 0 failed` (VT1 suspend+resume, VT2 sync, VT3 faulted,
      VT4 ref-T string, VT5 async-void suspend, VT6 sync control, VT_ZeroAlloc).
- [ ] 2.2 Run the full NeoStep smoke (drop the `_VT` suffix -> filter `NeoStep`)
      -> expect `Ran 354 tests, 0 failed` (no regression; includes the VT probes).
- [ ] 2.3 Run the full Neo run (no `NeoStep` filter) and grep the pre-crash NIE
      surface for `ValueTask` / `AsyncMethodBuilder` -> expect ZERO matches
      (remaining NIEs are the unrelated Step 17/13b / raw Stfld / Ldsflda /
      IL-delegate / bare-NIE surface). NOTE the run crashes (exit 127, the known
      unrelated `HotfixTestInheritanceTestCases.Test03` / Dict-NRE); that is
      pre-existing and NOT a regression.

## 3. Spec delta (the only artifact edit)

- [ ] 3.1 Confirm the ADDED requirement "Async builder byref-`this` call-arg
      marshalling skip" is present in `specs/neo-async/spec.md` with the 4
      `#### Scenario` blocks (ValueTask SetResult, Task SetResult, SetException,
      AwaitUnsafeOnCompleted) using exactly 4 hashtags.
- [ ] 3.2 Stash-toggle the spec invariant (mental check): if
      `BuilderThisManagedSize` were reverted to a hardcoded `8`, the
      "SetResult reads the real T result for a ValueTask<T> builder" scenario
      would be violated (`frameBase[8]` stale residue instead of
      `frameBase[16]`). The scenario is the regression guard.

## 4. Close-out

- [ ] 4.1 Mark the change `isComplete` (all `applyRequires` artifacts done) and
      record the durable finding in `rasen/changes/neo-overhaul/planning-context.md`
      (child 20 DONE): the framed bug was already fixed at `9c9b795d`; VT1-VT6 +
      VT_ZeroAlloc pass; the durable rule is the builder byref-`this` flat-managed-
      bytes skip pinned in `neo-async`.
- [ ] 4.2 Flag for the LEAD: the stash `child4-valuetask-blocked-partial` is
      obsolete (its fix + probes are committed; its `VTDBG2` diagnostics are
      absent from HEAD) and should be dropped after archival -- it must NOT be
      popped (it would re-introduce pre-fix diagnostics over the committed fix).
