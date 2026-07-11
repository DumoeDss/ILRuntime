# Tasks - neo-step23-24-roundtrip-regression

- [x] 1. Read handoff §1/§4 + prior-art (step22/23/24 archived ship-logs/designs).
- [x] 2. Read failing cells: NeoStep23RoundtripCheck (GenericProbe method cell +
      FullModel cell) + NeoStep24CliRoundtripCheck (Cell1 driver / .cctor).
- [x] 3. ESTABLISH ROOT CAUSE by dump (no guessing):
   - [x] 3a. #1: `Console.Error` loop-diag confirmed
         `GenericProbe retType=ILGenericParameterType`; `CompileFresh OK` then
         `BuildMethodDef` throws -> the `(CLRType)retType` cast at
         NeoAssemblyWriter.cs:759 (S3-2 commit f32f816e).
   - [x] 3b. #2: temp diag in NeoCompiler.CompileCore skip-catch dumped the
         full stack -> `Optimizer.LowerR1` (Optimizer.Neo.cs:1456) OOBs on
         `localInfos[r1]`; `LowerR1` OOB-diag confirmed `code=Ret r1=0
         localInfos.Length=0` (phantom register from JIT Code.Ret's void path).
- [x] 4. Apply MINIMAL Neo-gated / Legacy-neutral fixes:
   - [x] 4a. Fix #1: `ResolveReturnTypeCecilRef` helper handling
         ILType / ILGenericParameterType / CLRType; call from BuildMethodDef.
   - [x] 4b. Fix #2: bounds-check in `LowerR1` (out-of-range -> offset 0,
         mirroring the existing defensive pattern).
   - [x] 4c. Fix #3: mirror the driver in the Cell3/Cell5 replication loop
         (append GetStaticConstroctor).
- [x] 5. Remove all temporary diagnostics; confirm clean diff.
- [x] 6. VERIFY (dev subset only):
   - [x] 6a. `dotnet build ILRuntimeTestCLI -c Debug_Neo` -> 0 errors.
   - [x] 6b. `dotnet build TestCases -c Debug` -> 0 errors.
   - [x] 6c. NeoStep23Roundtrip -> 15/15.
   - [x] 6d. NeoStep24CliRoundtrip -> 5/5.
   - [x] 6e. NeoStep smoke -> 238/0/0 (no regression).
- [x] 7. STASH-TOGGLE PROOF: stash the 3 fix files -> NeoStep23 13/15 +
      NeoStep24 4/5 (RED); pop -> 15/15 + 5/5 (GREEN).
- [x] 8. Legacy-neutrality: all 3 touched files are `#if ENABLE_NEO_MODE`
      (plain `Debug` compiles them out) -> no Legacy impact possible.
- [x] 9. Write artifacts: proposal.md / design.md / tasks.md / ship-log.md
      (+ specs note).
