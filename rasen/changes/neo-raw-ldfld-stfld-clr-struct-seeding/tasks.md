# Tasks — neo-raw-ldfld-stfld-clr-struct-seeding

> Planner note: the re-audit is DONE (all 4 probes FAULT on HEAD fc2baa26; JIT
> dumps captured). The probe file `TestCases/NeoStepFloatSeedingProbe.cs` already
> exists in the working tree. The implementer starts at Task 2 (the fix). The
> NeoStep smoke baseline at HEAD is **354/0**; with the 4 probes it is
> "Ran 358 tests, 4 failded" (the 4 = the new probes, all DivideByZero).

## 1. Confirm the defect at HEAD (DONE by planner -- re-audit evidence)

- [x] 1.1 Build the dev subset: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental` and `dotnet build TestCases/TestCases.csproj -c Debug` (NEVER build TestCases with `Debug_Neo`).
- [x] 1.2 Probe `TestCases/NeoStepFloatSeedingProbe.cs` written (4 probes:
      TC1 Call-ret-float + const, TC2 Call-ret-double + const, TC3 raw-ldfld
      mul+add, TC4 raw-ldfld reg-reg add). Run filtered to `NeoStepFloatSeeding`.
      -- CONFIRMED: all 4 FAULT (DivideByZeroException). See `.tmp-floatseed-probe.log`.
- [x] 1.3 JIT-dump evidence captured. TC1: `call r7, GetOneF()` -> plain
      `addi r7,r7,1120403456` (0x42C80000 = bits of 100.0f). TC3: raw
      `ldfld r7,r0,...` -> plain `muli r7,r7,1073741824` (0x40000000 = bits of
      2.0f) + `addi r7,r7,1065353216` (0x3F800000 = bits of 1.0f). TC4 runtime
      dump: `a = (1.7014118E+38, 1, 1)` (0x3F800000+0x3F800000 = 0x7F000000).
- [x] 1.4 Full NeoStep smoke at HEAD with the probe present: "Ran 358 tests,
      4 failded" = 354/0 baseline + 4 faulting probes; ZERO regressions. See
      `.tmp-neostep-fullsmoke.log`.

## 2. The fix (Neo-only seeding, BOTH shapes)

- [ ] 2.1 **Shape 1 (Call):** in
      `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`, inside
      `TypeSpecializeNeoOpcodes`'s `Call`/`Callvirt`/`Callvirt_IL`/`Callvirt_CLR`/
      `Call_Redirect` case (the `if (op.Register1 >= 0) { ... }` block,
      `~JITCompiler.cs:1238-1280`), AFTER the existing stale-VT / stale-ref
      if/else-if chain, resolve the callee return type and seed the primitive
      return type:
      ```
      var cmr = appdomain.GetMethod(op.Operand2);
      IType rtr = cmr != null ? cmr.ReturnType : null;
      if (rtr is ILType rtil && rtil.IsByRef) rtr = null;
      if (rtr != null && rtr.IsPrimitive)
          SetRegisterType(registerTypes, op.Register1, rtr);
      ```
      Prefer hoisting the `appdomain.GetMethod(op.Operand2)` resolution (the
      current code calls it up to 2x, at `:1243` and `:1273`) so it is resolved
      once. See design.md D2 for the safety argument (a primitive return never
      conflicts with the stale-VT/stale-ref logic).
- [ ] 2.2 **Shape 2 (raw Ldfld):** in the same `switch (op.Code)` in
      `TypeSpecializeNeoOpcodes`, add a `case OpCodeREnum.Ldfld:` (place it among
      the other `Ldfld_*` seeding cases, `~JITCompiler.cs:1065`) that resolves
      the declaring CLRType + field from `OperandLong = (typeHash<<32)|fieldHash`
      and seeds `registerTypes[op.Register1]` with the field's PRIMITIVE type:
      ```
      case OpCodeREnum.Ldfld:
      {
          int typeHash = (int)((ulong)op.OperandLong >> 32);
          int fieldHash = (int)op.OperandLong;
          var declType = appdomain.GetType(typeHash);
          if (declType is CLRType ct)
          {
              var f = ct.GetField(fieldHash);
              if (f != null)
              {
                  IType ft = NeoClrPrimitiveTypeToIType(f.FieldType, appdomain);
                  if (ft != null)
                      SetRegisterType(registerTypes, op.Register1, ft);
              }
          }
          break;
      }
      ```
      Add the helper `NeoClrPrimitiveTypeToIType(Type clrType, AppDomain appdomain)`
      (`float`->FloatType, `double`->DoubleType, `long`/`ulong`->LongType, other
      primitive->IntType, NON-primitive->null). Check whether an existing
      `appdomain` helper already maps a `System.Type` to an `IType` and reuse it;
      otherwise a small private `switch` is fine. See design.md D3.
- [ ] 2.3 Do NOT touch `Optimizer.ELDC.cs`, `Optimizer.Utils.cs`, the Neo runtime
      arms (`ILIntepreter.Neo.cs`), or the object model. The fix is ENTIRELY in
      `TypeSpecializeNeoOpcodes` (Neo-only). Verify (Grep) the only engine diff
      is the new seeding code inside the `#if ENABLE_NEO_MODE`
      `TypeSpecializeNeoOpcodes` method (+ the helper).
- [ ] 2.4 **OPEN O1:** spot-check whether `appdomain.GetMethod(op.Operand2)`
      resolves a primitive `IType` return for a `Callvirt_CLR`/`Call_Redirect`
      callee returning `float`/`double`/`long` (the re-audit probes use IL
      callees, which is the load-bearing path). If a quick probe shows a
      CLR-callee float return still corrupts, extend the resolution (e.g. via the
      `CLRMethod` return type). If it already works, note it and move on.

## 3. Regression probes (already written; verify + keep)

- [x] 3.1 `TestCases/NeoStepFloatSeedingProbe.cs` exists (4 probes, namespace
      `TestCases`, `public static void` parameterless, names embed `NeoStep`).
      Uses `TestVector3.One` + `TestCLRBinding.SumTestVector3Fields`; on a wrong
      value trips `int z=1; int d=0; int _ = z/d;`. The two non-inlinable helpers
      `NeoStepGetOneF`/`NeoStepGetOneD` (`hasExceptionHandler` -> `canInline=false`)
      produce the Call-returning-primitive shape.
- [ ] 3.2 Rebuild TestCases after the fix (`dotnet build TestCases/TestCases.csproj -c Debug`).

## 4. Verify

- [ ] 4.1 **Stash-toggle FAIL-on-HEAD:** stash ONLY the `JITCompiler.cs` fix
      hunks (`git stash push -- ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`),
      keep the probe, rebuild CLI (`Debug_Neo --no-incremental`) + TestCases, run
      the 4 probes filtered (`... NeoStepFloatSeeding`), confirm each FAULTs on
      HEAD (DivideByZero). `git stash pop` to restore. Expected: "Ran 4 tests,
      4 failded".
- [ ] 4.2 **With the fix applied:** rebuild CLI + TestCases, run the 4 probes
      filtered -- all 4 pass. Expected: "Ran 4 tests, 0 failded". Capture the JIT
      dump showing `addi.r4`/`add.r4`/`muli.r4` (the typed *_R4 arms reading
      OperandFloat) AND that the raw `ldfld` now flows into a typed arith op.
- [ ] 4.3 **Full NeoStep smoke:** `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` -- MUST be green at the new baseline (354 + 4 probes = **358/0**; ZERO regressions). Note the exact count in the ship log.
- [ ] 4.4 **Legacy-neutral:** plain `Debug` build + `useRegister=true` + `NeoStep`
      filter -- MUST show the documented 17-failure baseline (the SAME set with
      and without the fix); the 4 new probes MUST also pass under Legacy (Legacy's
      `Addi`/`Add` re-dispatch on `ObjectType`).

## 5. Ship

- [ ] 5.1 `git status` to confirm the staged set (only `JITCompiler.cs` (M) +
      `TestCases/NeoStepFloatSeedingProbe.cs` (new)); no accidental partial commit.
- [ ] 5.2 Commit with trailer `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`;
      push needs `git config lfs.useslockfiles false`. (Per the LEAD mandate the
      implementer hands to rasen-ship for commit/push.)
- [ ] 5.3 Write the ship log (root cause for both shapes, the two seeding sites,
      scope over subi/muli/divi/remi + plain Add, probe FAULT evidence, NeoStep
      count, Legacy-neutral evidence). Write to
      `rasen/changes/neo-raw-ldfld-stfld-clr-struct-seeding/ship-log.md`.
