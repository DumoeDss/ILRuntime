# Tasks — neo-raw-stfld-array-element

## 1. Implement the raw Stfld array-element branch (value-type declaring, the reachable hits)
- [x] In `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`, raw `Stfld` handler, CLR
      value-type declaring branch (~line 4076): replace the
      `else if (objIdx >= 0 && mStack[objIdx] is Array) throw new
      NotImplementedException("Neo raw Stfld: array-element field write is deferred ...")` with the
      box/mutate/unbox round-trip:
      ```csharp
      else if (objIdx >= 0 && mStack[objIdx] is Array cArr)
      {
          // CLR-struct ARRAY ELEMENT owner: the byref is (arrIdx, elementIdx) where off == elementIdx
          // (the ldelema encoding, ILIntepreter.Neo.cs:5747-5748; same convention stind/ldind use).
          // Mirror Legacy's ObjectTypes.ArrayReference writeback (Register.cs:3154-3158): box the
          // element, reflection-write the field, write the mutated struct back.
          object boxedElem = cArr.GetValue(off);
          f.SetValue(boxedElem, value);
          cArr.SetValue(boxedElem, off);
      }
      ```
      (`objIdx` and `off` are already decoded at ~line 4066-4067; `value` is already boxed by field
      category at ~4044-4057; `f` is the resolved FieldInfo at ~4036. No new locals except `cArr` and
      `boxedElem`.)
- [x] Keep the `objIdx == -1` frame-byref branch (~4068-4075, child-4's box/mutate/unbox on frame
      bytes) and the `else` unrecognized-byref NIE (~4078-4079) intact.

## 2. Implement the raw Stfld array-element branch (ref-type declaring, symmetric/defensive)
- [x] In the raw `Stfld` handler, CLR ref-type declaring `else` branch (~line 4102): replace the
      `else if (target is Array) throw new NotImplementedException("Neo raw Stfld: array-element
      field write is deferred ...")` with the same box/mutate/unbox, decoding the element index from
      the byref's +4 half:
      ```csharp
      else if (target is Array cArr2)
      {
          int elementIdx2 = *(int*)(frameBase + ownerOff + 4);
          object boxedElem2 = cArr2.GetValue(elementIdx2);
          f.SetValue(boxedElem2, value);
          cArr2.SetValue(boxedElem2, elementIdx2);
      }
      ```
      (`ownerOff` is `ip->DstOffset`, decoded at ~4040. This branch is unreachable via `ldelema` on a
      ref-type-element array -- which throws at ~5744-5746 -- but routing it through the same helper
      is symmetric and removes the dead NIE.)
- [x] Keep the plain-CLR-object `NeoWriteClrObjectField(AppDomain, target, fieldHash, value)`
      fall-through (~4104-4105) and the IL-instance-CLR-base branch (~4090-4100, child-9) intact.

## 3. Add the host test infra (int-field CLR struct + read-back helper)
- [x] In `ILRuntimeTestBase/TestFramework/TestVector3.cs`: add a small host CLR struct
      `public struct NeoArrElemIntProbe { public int A; public int B; }` (blittable, default
      LayoutKind.Sequential; int fields to avoid the pre-existing addi-on-float / conv.i4-float Neo
      bugs). No ValueTypeBinder needed (the fix uses reflection, not the binder flat-byte path).
- [x] In `ILRuntimeTestBase/TestFramework/TestClass3.cs`, class `TestCLRBinding`: add a host helper
      `public static int NeoArrElemFieldSum(NeoArrElemIntProbe[] arr, int i) { return arr[i].A +
      arr[i].B; }` (the read-back is CLR-side so the probe does NOT depend on Neo Ldfld or Neo float
      arithmetic).
- [x] Build TestCases with plain `Debug` (NEVER `Debug_Neo`):
      `dotnet build TestCases/TestCases.csproj -c Debug`.

## 4. Add the NeoStep probe (must FAULT on HEAD)
- [x] Create `TestCases/NeoStepRawStfldArrElemTest.cs` with:
      - `public static void NeoStepRawStfldArrElem_TC1()`: `NeoArrElemIntProbe[] arr = new
        NeoArrElemIntProbe[4]; arr[1].A = 4242; arr[1].B = 17; int s = TestCLRBinding.
        NeoArrElemFieldSum(arr, 1); if (s != 4259) { int z = 0; int _ = 1 / z; }`.
        (FAULTs on HEAD: `arr[1].A = 4242` throws the :4077 tagged NIE.)
      - `public static void NeoStepRawStfldArrElem_TC2()`: `NeoArrElemIntProbe[] arr = new
        NeoArrElemIntProbe[8]; arr[0].A = 10; arr[0].B = 20; arr[5].A = 30; arr[5].B = 40; int s =
        TestCLRBinding.NeoArrElemFieldSum(arr, 0) + TestCLRBinding.NeoArrElemFieldSum(arr, 5);
        if (s != 100) { int z = 0; int _ = 1 / z; }`.
        (Proves element-index decode across multiple indices; FAULTs on HEAD.)
- [x] Both probe method names embed "NeoStep" so the `NeoStep` smoke filter picks them up. Methods are
      `public static`, parameterless (the self-test harness contract).

## 5. Verify (Neo)
- [x] Build CLI: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental`.
- [x] Stash-toggle FAIL check: temporarily revert the two handler edits (tasks 1-2), run the probe
      filter (`... true NeoStepRawStfldArrElem`), confirm BOTH TCs FAULT on the tagged NIE; restore
      the edits.
- [x] NeoStep smoke: `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build --
      TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep`
      -> expect 352 -> ~354, 0 failures.
- [x] Confirm the typed arms and the sibling raw-owner arms (CLR ref-type / CLR VT frame-byref /
      IL-instance-CLR-base) are unregressed (the existing `NeoStepRawFld_*` / `NeoStepIlClrBase_*`
      probes still pass).

## 6. Verify (Legacy-neutral)
- [x] Plain `Debug` CLI + `useRegister=true` + `NeoStep` filter: the new probes pass under Legacy
      too, and the pre-existing failure set/count is unchanged (Legacy-neutral by construction -- all
      edits are under `#if ENABLE_NEO_MODE`; the host struct/helper are plain CLR usable by both).

## 7. Full-smoke confirmation (optional, pre-crash)
- [x] Drop the `NeoStep` filter and confirm the ~4 "array-element field write is deferred (stfld on a
      CLR array element)" tagged-NIE occurrences are gone (the full run still NRE-crashes mid-stream
      on the known unrelated `GenericMethodTest` NRE -- counts are pre-crash).
