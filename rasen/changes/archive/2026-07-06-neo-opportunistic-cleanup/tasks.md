## 1. N-CGTUN — Cgt_Un divergence comment tighten

- [x] 1.1 In `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`, edit
      the `case OpCodeREnum.Cgt_Un:` arm's divergence comment (~lines 1029-1032)
      so it names BOTH sentinel divergences from raw unsigned semantics:
      (a) the operand case `cgt.un x, (uint)0xFFFFFFFF` (the `cguB == -1` clause
      short-circuits the compare to `true`) AND (b) the symmetric source case
      `cgt.un (uint)0xFFFFFFFF, x` (the leading `cguA != -1` clause forces the
      result to `false`). Note both are the same sentinel-collision class
      (`-1 == 0xFFFFFFFF`, the null sentinel colliding with the integer bit
      pattern) and neither is exercised by the validated TestCases suite.
- [x] 1.2 Confirm the runtime comparison expression on ~line 1035
      (`cguRes = cguA != -1 && ((uint)cguA > (uint)cguB || cguB == -1);`) is
      byte-identical (unchanged) — this is a comment-only edit.

## 2. N-TC2 — Step 14 TC2 tighten (type-check-in-catch via isinst)

- [x] 2.1 In `TestCases/NeoStep14Test.cs`, edit the
      `NeoStep14_TC2_CatchObjectAccess` body (~lines 47-50): replace the weak
      `if (e != null) return 7;` with a type/identity assertion via the `is`
      operator (which lowers to `isinst`, the Step 15 opcode). Use
      `if (e is DivideByZeroException && e.Message != null) return 7;` — the
      `is DivideByZeroException` exercises `isinst` on the caught exception
      (the type-check-in-catch shape that was Step-15-blocked when TC2 was
      authored); the `Message != null` keeps a secondary non-null reachability
      check.
- [x] 2.2 Keep the catch clause type as `DivideByZeroException` (do NOT broaden
      to `Exception`) so the test stays scoped to its original intent, and keep
      the `return -1;` fallback at the end of the method.

## 3. Build + smoke gate

- [x] 3.1 Build the CLI with `Debug_Neo`:
      `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`
      (0 errors expected — comment-only source change).
- [x] 3.2 Build TestCases with plain `Debug` (NOT `Debug_Neo`):
      `dotnet build TestCases/TestCases.csproj -c Debug --no-incremental`
      (use `--no-incremental` to avoid the stale-DLL gotcha; verify the DLL
      mtime is newer than the source).
- [x] 3.3 Run the full `NeoStep` smoke:
      ```
      dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
        TestCases/bin/Debug/netstandard2.1/TestCases.dll \
        HotfixAOT/Patched/HotfixAOT.patch true NeoStep
      ```
      Expected: 154/154 green (the HEAD baseline). TC2 itself stays green
      (tightened, not loosened — it asserts strictly more). No NEW failures.
- [ ] 3.4 (Optional, Legacy-neutral confirm) Build plain `Debug` + run
      `useRegister=true` filtered to `NeoStep14_TC2` to confirm the tightened
      assertion passes on Legacy too (the `isinst` arm is shared-engine).
      SKIPPED (apply scope): the `isinst` arm is shared-engine and already
      exercised by the broader Step 15 NeoStep suite; this change is a
      Neo-comment + a test-source tighten that compiles identically on both
      engines.

## 4. Closeout

- [ ] 4.1 Update `.trae/documents/neo-deferred-items.md`: move the `N-CGTUN`
      and `N-TC2` rows from §2 master table + §3 detail into the §4 Resolved
      section, citing this change.
- [ ] 4.2 Stage precisely: `ILIntepreter.Neo.cs`, `NeoStep14Test.cs`, the four
      openspec artifacts, the deferred-items doc update. EXCLUDE .pdb/.gitignore/
      nuget.config/.claude/.vscode/CLAUDE.md churn. Commit message:
      `Neo opportunistic-cleanup: cgt-un comment + N-TC2 tighten` + the
      `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
      trailer. `git push origin features/object-model-overhaul`.
