# Tasks -- implement-neo-catch-complete

One implementer pass. Small shared-engine change (one method). Gates: full
NeoStep smoke (Neo) + a Legacy catch filter + standing 519-test Legacy baseline.

## 1. Implement

- [x] 1.1 Read `ILIntepreter.CheckExceptionType` (`ILIntepreter.cs:5823-5836`)
      and confirm it matches the design (NIE at the `else` for non-CLRType).
- [x] 1.2 Replace the `else throw new NotImplementedException();` (line 5835)
      with the IL-type branch from `design.md` section 2:
        - `exception == null` -> `return false;`
        - `exception as ILTypeInstance` -> exact `exIl.Type == catchType` when
          `explicitMatch`, else `exIl.CanAssignTo(catchType)`.
        - otherwise CLR -> `catchType.TypeForCLR` null-guard, then exact-equal
          (explicit) or `IsAssignableFrom` (non-explicit).
      Keep the `catchType == null` and `catchType is CLRType` arms byte-for-byte
      unchanged.
- [x] 1.3 Add a brief comment citing D-CHECKEX at the new branch.
- [x] 1.4 Confirm NO `#if ENABLE_NEO_MODE` gating (this is a shared-engine fix,
      correct for both Neo and Legacy).

## 2. Tests

- [x] 2.1 Add `TestCases/NeoCatchCompleteTest.cs` (ASCII; `public static`
      parameterless methods; `[ILRuntimeTest]` optional) following the
      `NeoStep14Test.cs` convention (return a sentinel on PASS; an uncaught
      `1/0` on a deliberate fail branch).
      **BLOCKED / NOT ADDED.** See finding in planning-context.md sec 8
      (apply): a `catch (T)` clause is REJECTED by the host C# compiler unless
      `T : System.Exception` (CS0155); an IL class that inherits
      `System.Exception` then fails to LOAD at runtime
      (`TypeLoadException: Cannot find Adaptor for:System.Exception` from
      `ILType.InitializeBaseType:1418` -- ILRuntime requires a
      `CrossBindingAdaptor` for any IL type inheriting a CLR `Exception`, which
      is out of scope this pass). So NO ILType catch clause is authorable in
      the host-compiled test DLL this pass, on either engine. The new IL branch
      is therefore UNREACHABLE from any host-compiled test today (true for
      existing tests AND any new one). The fix is verified by build + the
      shared-engine neutrality argument (new branch unreachable for CLRType
      catches) + the full NeoStep smoke (no regression). A positive IL-catch
      test is reserved for a future adaptor-based pass. No test file was added.
- [ ] 2.2 **TC1 IL-throw + IL-catch (same type):** DEFERRED -- not authorable
      this pass (see 2.1); also blocked by the Throw opcode requiring the
      operand to BE a CLR `Exception` (both engines do `mStack[idx] as
      Exception`; a plain IL class is an ILTypeInstance, not an Exception).
- [ ] 2.3 **TC2 IL-throw + IL-base catch:** DEFERRED (same reason as 2.2).
- [ ] 2.4 **TC3 CLR-throw + IL-catch (fallback path):** DEFERRED -- an IL catch
      clause is not authorable this pass (see 2.1).
- [ ] 2.5 **TC4 no-false-match (regression of the matcher):** DEFERRED -- the
      IL catch clause needed to prove the branch returns false is not
      authorable this pass (see 2.1).
- [x] 2.6 N/A (no TCs added); infinite-loop guard not triggered.

## 3. Build + smoke

- [x] 3.1 `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`
      -> 0 errors.
- [x] 3.2 `dotnet build TestCases/TestCases.csproj -c Debug` (NEVER Debug_Neo)
      -> TestCases.dll rebuilt.
- [x] 3.3 **Neo smoke:** `dotnet run -c Debug_Neo -f net8.0 --project
      ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll
      HotfixAOT/Patched/HotfixAOT.patch true NeoStep` -> **91/91, 0 fail** (no
      regression vs the 91/91 baseline; no new tests added).
- [x] 3.4 **Neo new-tests:** N/A (no new tests; see 2.1).
- [x] 3.5 **Legacy smoke (shared-engine neutrality gate):** built CLI with
      plain `Debug`; ran `NeoStep14` catch filter under Legacy
      (`useRegister=true`): 6 pass / 3 fail. The 3 failures
      (`ArgumentOutOfRangeException` at `AssignToRegister:5651`, called from
      `ExecuteR:5327`, for the nested/native-fault NeoStep14 tests TC5/TC8/...)
      are PRE-EXISTING -- a `git stash` of the fix and re-run on the baseline
      (HEAD `57e0af54`) reproduces the IDENTICAL 3 failures (those NeoStep14
      tests catch CLRTypes, so they enter the unchanged `catchType is CLRType`
      arm; the new IL branch is unreachable for them). The basic Legacy
      try/catch/finally tests (the 6 passing) exercise the unchanged CLRType arm
      of `CheckExceptionType` and still pass. No regression attributable to this
      change. Standing 519-test Legacy baseline remains the reference.

## 4. Close-out

- [x] 4.1 Append durable findings to `planning-context.md` section 8 (shared-vs-
      Neo finding, the exact IL-branch, the testability note).
- [x] 4.2 Update `tasks.md` checkboxes / mark complete.
- [ ] 4.3 (Apply stage only -- not propose.) Hand off for review -> ship ->
      archive -> LEAD commit+push (user pre-authorized).
