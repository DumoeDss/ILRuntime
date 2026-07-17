# Tasks — neo-clr-static-vt-slot-overflow

Size the Neo eval-temp register file to accommodate gathered CLR value types (not
only IL value types), so `ldsfld TestVector3.One` into an eval temp stops tripping
the (correct) slot-overflow guard -- the 14 full-smoke NIEs. The fix is one
`else if (i is CLRType ct)` arm in `AllocateLocalStackSpaces`; the guard stays
byte-for-byte. Neo-gated; Legacy-neutral by construction. See `proposal.md`,
`design.md`, `specs/neo-optimizer/spec.md`.

Build/test contract: ALWAYS `-f net8.0`; CLI = `Debug_Neo --no-incremental`; NEVER
build `TestCases` with `Debug_Neo` (its output path is unchanged; use plain
`Debug`). NeoStep baseline after child 16 = **346/0**.

The planner already verified the fix end-to-end then reverted it to leave the tree
clean; the expected results below are from that verification run.

## 1. Baseline + reproducer

- [x] 1.1 Build the CLI: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental` (0 errors). Build TestCases: `dotnet build TestCases/TestCases.csproj -c Debug` (0 errors).
- [x] 1.2 Confirm the NeoStep baseline is green: `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` => 346 ran / 0 failed.
- [x] 1.3 Confirm the defect reproduces in the FULL (un-filtered) Neo smoke: the message `"Neo Ldsfld: CLR static value-type field One of type ...TestVector3 not supported under Neo"` appears (14 distinct throws / 28 incl rethrows). Record the exact count for the before/after delta. (Run crashes at the known pre-existing Dict-NRE, exit 127 -- the NIE hits are pre-crash.) [EXPECTED: 14 distinct.] OBSERVED: 28 message lines = 14 distinct TestVector3.One throws (x2 throw+rethrow).

## 2. The temp-slot fix (the root cause)

- [x] 2.1 In `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`, function `AllocateLocalStackSpaces`, locate the `maxSize`/`maxAlignment` consumer loop over `valueTypes` (the `foreach (var i in valueTypes) { if (i is ILType il) { ... } }` block, ~line 2209-2223). The loop is INSIDE the file-level `#if ENABLE_NEO_MODE` Neo block (the big 807..2347 region), so the edit is automatically Neo-gated.
- [x] 2.2 Add an `else if (i is CLR.TypeSystem.CLRType ct)` arm to that loop (a sibling of the `is ILType il` arm), mirroring the design D1 recipe EXACTLY:
  ```csharp
  else if (i is CLR.TypeSystem.CLRType ct)
  {
      // Neo (neo-clr-static-vt-slot-overflow): a gathered CLR value type can flow
      // through an eval TEMP (e.g. the dest of ldsfeld on a CLR-VT static field
      // such as TestVector3.One). The temp file is sized to maxSize (default 8),
      // which previously grew only for ILType -- a CLR struct > 8 bytes got an
      // undersized temp and the slot-overflow guard (NeoClrVtStaticFieldIsUnsafe)
      // rejected the write as an OOB. Grow maxSize (and maxAlignment, mirroring
      // the CLR-VT LOCAL declaration). maxRefCount is left alone: the reachable
      // set is blittable (a ref-field CLR struct is refused upstream by
      // NeoClrStructHasRefFields), so its ref count is 0.
      int size = Optimizer.GetNeoValueTypeManagedSize(ct.TypeForCLR);
      if (size > maxSize)
          maxSize = size;
      int align = size >= 8 ? 4 : size;
      if (align > maxAlignment)
          maxAlignment = align;
  }
  ```
  Do NOT touch the `is ILType il` arm, `maxRefCount`, the temp-slot allocation loop below, or anything else.
- [x] 2.3 Do NOT modify `NeoClrVtStaticFieldIsUnsafe` or the Stsfld/Ldsfeld arms in `ILIntepreter.Neo.cs`. The slot-overflow guard stays byte-for-byte (it is the real AV protection; after the fix it simply no longer fires for gathered blittable CLR VTs whose dest temp is now correctly sized). Confirm by reading -- no edit.

## 3. NeoStep probes (FAULT-to-fail; TEMP-dest shape; names embed "NeoStep")

- [x] 3.1 Create `TestCases/NeoStepClrVtStaticSlotOverflowTest.cs` (`public class NeoStepClrVtStaticSlotOverflowTest`). Assertion mechanism: a passing test returns; a logic failure is a deliberate `1/0` (DivideByZero; the Neo VM cannot yet `new Exception(...)`). Tests are `public static void`, parameterless. The load-bearing step is the `ldsfld TestVector3.One` into a TEMP (by-value call arg, NO intervening named local) -- child-8 TC1/TC2 are direct-to-LOCAL and do NOT cover this shape.
- [x] 3.2 TC1 (ldsfeld into temp, by-value arg): `NeoStepClrVtStaticSlot_TC1_LdsfeldIntoTempByValueArg` -- `int s = TestCLRBinding.SumTestVector3Fields(TestVector3.One, TestVector3.One); if (s != 6) { int z=1,d=0; int _=z/d; }`. Must FAULT on HEAD (the ldsfeld-into-temp guard NIE); pass after (One+One -> 6).
- [x] 3.3 TC2 (ldsfeld into temp, mixed with default): `NeoStepClrVtStaticSlot_TC2_LdsfeldIntoTempByValueArgDefault` -- `int s = TestCLRBinding.SumTestVector3Fields(TestVector3.One, default(TestVector3)); if (s != 3) { int z=1,d=0; int _=z/d; }`. Must FAULT on HEAD; pass after (One+default -> 3). Assert via the host helper `SumTestVector3Fields` (CLR-side float arithmetic) to sidestep the open `conv.i4`-float-bit-reinterpret gap.
- [x] 3.4 Rebuild TestCases (`dotnet build TestCases/TestCases.csproj -c Debug`) and the CLI (`Debug_Neo --no-incremental`). No new host infra is needed (`SumTestVector3Fields` already exists in `TestCLRBinding`).

## 4. Verify

- [x] 4.1 Stash-toggle FAIL-on-HEAD: `git stash push -- ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` (revert ONLY the fix; keep the new probes), rebuild CLI, run the two probes by filter (`... true NeoStepClrVtStaticSlot`). Expect BOTH to FAIL with the tagged NIE (`"Neo Ldsfld: CLR static value-type field One ... not supported ... slot-size overflow"`). Restore the fix (`git stash pop`); confirm tree restored. OBSERVED: 2/2 failed with the exact NIE (ILIntepreter.Neo.cs:4521); tree restored.
- [x] 4.2 PASS-after: rebuild, run the two probes by filter => both PASS. [EXPECTED: Ran 2, 0 failed.] OBSERVED: Ran 2, 0 failed.
- [x] 4.3 NeoStep smoke: `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` => **348 ran / 0 failed** (346 + TC1 + TC2). Zero regressions across NeoStep12/13/13b/17/clr-static/raw-stfld-ldfld/misc-opcodes/clr-vt-static/eh-table/ldind-stind/addi-on-float. OBSERVED: Ran 348, 0 failed.
- [x] 4.4 Full (un-filtered) Neo smoke: drop the `NeoStep` filter; confirm the `"Neo Ldsfld: CLR static value-type field One ...TestVector3 not supported"` count goes 14 (step 1.3) -> 0. (Pre-existing unrelated full-smoke Dict-NRE crash, exit 127, is out of scope and remains; the run still crashes once pre-completion as before.) OBSERVED: 28 message lines -> 0 (14 distinct eliminated; exit 127 unchanged).
- [x] 4.5 Legacy-neutral spot check: the fix is inside `#if ENABLE_NEO_MODE` (the `AllocateLocalStackSpaces` Neo block), so Legacy compiles none of it. Confirm via a plain-`Debug` + `useRegister=true` NeoStep-filter run that the Legacy failure set is unchanged. [EXPECTED: 348 ran / 17 failed == documented baseline; both new probes pass under Legacy.] OBSERVED: 348 ran / 17 failed == baseline; new probes 2/0 under Legacy.

## 5. Ship (LEAD/shipper)

- [ ] 5.1 `git status` (confirm staged set; `JITCompiler.cs` + new `TestCases/NeoStepClrVtStaticSlotOverflowTest.cs`), then commit with trailer `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`. Push with `git config lfs.useslockfiles false` if needed.
- [ ] 5.2 Write `ship-log.md` (what shipped, the stash-toggle evidence, the 346->348 + 14->0 deltas, the "guard stays, temp-sizing fix" framing). Then archive the change.
