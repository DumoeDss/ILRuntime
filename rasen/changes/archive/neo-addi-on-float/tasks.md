# Tasks — neo-addi-on-float

## 1. Confirm the defect at HEAD

- [x] 1.1 Build the dev subset: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental` and `dotnet build TestCases/TestCases.csproj -c Debug` (NEVER build TestCases with `Debug_Neo`).
- [x] 1.2 Temporarily add the TC1 probe body (`a.X += 100`; assert `SumTestVector3Fields(a,a)==206` else `1/0`) to a throwaway NeoStep method, rebuild TestCases, run the NeoStep smoke filtered to that method, and confirm it FAULTs on HEAD (DivideByZero on the wrong value) -- proves the defect is live before any fix. (Optional but recommended: capture the JIT line showing `addi r,r,0x42C80000` in the `OUTPUT_JIT_RESULT` dump.) -- CONFIRMED: HEAD JIT emitted `5:addi r7,r7,1120403456` (0x42C80000 = bits of 100.0f) after `4:ldind.r4 r7, r6`; result `a=(-1.47e-37,1,1)` -> DivideByZero. See .tmp-addifloat-head.log.

## 2. The fix (Neo-only seeding)

- [x] 2.1 In `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`, inside `TypeSpecializeNeoOpcodes`'s `switch (op.Code)` (immediately after the existing `Ldfld_R4`/`Ldfld_R8` block, ~line 1074), add cases that seed `registerTypes[op.Register1]`: `Ldind_R4`/`Ldelem_R4` -> `appdomain.FloatType`; `Ldind_R8`/`Ldelem_R8` -> `appdomain.DoubleType`; `Ldind_I8`/`Ldelem_I8` -> `appdomain.LongType`; `Ldind_I4`/`Ldelem_I4` -> `appdomain.IntType`. Mirror the exact `SetRegisterType(registerTypes, op.Register1, appdomain.XxxType)` form used by the neighboring `Ldc_*`/`Ldfld_*` cases. -- DONE (30-line insertion, 8 cases in 4 groups).
- [x] 2.2 Do NOT touch `Optimizer.ELDC.cs`, `Optimizer.Utils.cs`, the Neo runtime arms, or the object model. Verify (Grep) the only diff in `JITCompiler.cs` is the new seeding cases inside the `#if ENABLE_NEO_MODE` `TypeSpecializeNeoOpcodes` method. -- VERIFIED: `git diff` shows exactly +30 lines, single hunk at line 1074 inside TypeSpecializeNeoOpcodes; no other source file changed.

## 3. Regression probes

- [x] 3.1 Create `TestCases/NeoStepAddiOnFloatTest.cs` (namespace `TestCases`, `public static void` parameterless methods, class/method names embed `NeoStep` so the smoke filter picks them up). Use `TestVector3` (`TestVector3.One == (1,1,1)`) and assert via `TestCLRBinding.SumTestVector3Fields(a,a)`; on a wrong value trip `int z=1; int d=0; int _ = z/d;`.
- [x] 3.2 TC1 `NeoStepAddiOnFloat_TC1_FloatPlusEqConst`: `a.X += 100;` assert `Sum==206`.
- [x] 3.3 TC2 `NeoStepAddiOnFloat_TC2_FloatMinusEqConst`: `a.X -= 100;` assert `Sum==-194`.
- [x] 3.4 TC3 `NeoStepAddiOnFloat_TC3_CompoundMulAdd`: `a.X = a.X*2 + 1;` assert `Sum==10`. -- NOTE/DEVIATION: the single expression `a.X = a.X*2 + 1` lowers the READ through a raw `ldfld` of the CLR-struct field (the child-4 escaping shape, an OUT-OF-SCOPE producer not covered by this change's Ldind/Ldelem seeding), so it would NOT exercise the fix. Reformulated as two compound assignments `a.X *= 2; a.X += 1;` -- each lowers via `ldloca;ldflda;ldind.r4` (the in-scope producer, identical to TC1/TC2). Same expected Sum==10; still exercises muli + addi. JIT dump confirms `muli.r4` + `addi.r4`.
- [x] 3.5 Rebuild TestCases (`dotnet build TestCases/TestCases.csproj -c Debug`).

## 4. Verify

- [x] 4.1 Stash ONLY the `JITCompiler.cs` fix hunk (`git stash push -- ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`), keep the probe, rebuild CLI (`Debug_Neo --no-incremental`) + TestCases, run the 3 probes filtered, and confirm each FAULTs on HEAD (DivideByZero). `git stash pop` to restore. -- CONFIRMED: stashed fix -> HEAD JIT -> "Ran 3 tests, 3 failded" (3x DivideByZeroException). Popped, rebuilt with-fix.
- [x] 4.2 With the fix applied: rebuild CLI + TestCases, run the 3 probes filtered -- all 3 pass. -- CONFIRMED: "Ran 3 tests, 0 failded"; JIT dump shows `addi.r4`/`subi.r4`/`muli.r4` (the typed *_R4 arms reading OperandFloat).
- [x] 4.3 Full NeoStep smoke: `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` -- must be green at the current baseline (343/0 + 3 new probes = 346/0; ZERO regressions). Note the exact count in the ship log. -- CONFIRMED: "Ran 346 tests, 0 failded" (343 baseline + 3 probes), EXIT 0.
- [x] 4.4 Legacy-neutral: plain `Debug` build + `useRegister=true` + `NeoStep` filter -- MUST show the documented 17-failure baseline (the SAME set with and without the fix); the 3 new probes MUST also pass under Legacy (Legacy's `Addi` re-dispatches on `ObjectType`). -- CONFIRMED: "Ran 346 tests, 17 failded" == the documented 17-failure baseline; none of the 3 AddiOnFloat probes are in the failure set (all pass under Legacy).

## 5. Ship

- [x] 5.1 `git status` to confirm the staged set (only `JITCompiler.cs` + new `TestCases/NeoStepAddiOnFloatTest.cs`); no accidental partial commit. -- CONFIRMED: source changes are exactly `JITCompiler.cs` (M, +30) + `TestCases/NeoStepAddiOnFloatTest.cs` (new). (The `rasen/` planning docs + binary .pdb deps are separate bookkeeping/noise.)
- [ ] 5.2 Commit with trailer `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`; push needs `git config lfs.useslockfiles false`. -- DEFERRED TO SHIPPER: the LEAD implementer mandate says "Do NOT commit". Source fix + probes are staged in the working tree and verified; hand to rasen-ship for commit/push.
- [x] 5.3 Write the ship log (root cause, the seeding site, scope over subi/muli/divi/remi + plain Add, probe FAULT evidence, NeoStep count, Legacy-neutral evidence). -- Written to `rasen/changes/neo-addi-on-float/ship-log.md`.
