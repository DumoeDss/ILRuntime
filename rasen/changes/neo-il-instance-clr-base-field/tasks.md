# Tasks — neo-il-instance-clr-base-field

## 1. Implement the raw Ldfld IL-instance-owner branch
- [x] In `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`, raw `Ldfld` handler, CLR
      ref-type owner `else` (~line 3749): replace the
      `if (target is ILTypeInstance || target is CrossBindingAdaptorType) throw new
      NotImplementedException("Neo raw Ldfld: IL-instance owner with a CLR-base field is deferred ...")`
      with a read through `CLRInstance`:
      unwrap `ILTypeInstance il = target as ILTypeInstance ?? ((CrossBindingAdaptorType)target).ILInstance;`
      `fldVal = NeoReadClrObjectField(AppDomain, il.CLRInstance, fieldHash);`
      `if (fldVal is CrossBindingAdaptorType cba2) fldVal = cba2.ILInstance;`
- [x] Keep the `target is Array` deferred NIE (out of scope) and the plain-CLR-object
      `NeoReadClrObjectField(AppDomain, target, fieldHash)` fall-through intact.
- [x] Confirm the post-read dest marshalling (primitive / VT / ref) is unchanged and still follows.

## 2. Implement the raw Stfld IL-instance-owner branch
- [x] In the raw `Stfld` handler, CLR ref-type owner `else` (~line 3912): replace the
      `if (target is ILTypeInstance || target is CrossBindingAdaptorType) throw new
      NotImplementedException("Neo raw Stfld: IL-instance owner with a CLR-base field is deferred ...")`
      with a write through `CLRInstance`:
      unwrap as above; `NeoWriteClrObjectField(AppDomain, il.CLRInstance, fieldHash, value);`
      (`value` is already marshalled by field category earlier in the handler.)
- [x] Keep the `target is Array` deferred NIE and the plain-CLR-object
      `NeoWriteClrObjectField(AppDomain, target, fieldHash, value)` fall-through intact.
- [x] No writeback to the `ILTypeInstance` is needed (the CLR base is a class; `clrInstance` is
      not replaced).

## 3. Add the NeoStep probe
- [x] Create `TestCases/NeoStepIlClrBaseFieldTest.cs` with:
      - `public class NeoStepIlClrBaseHolder : ClassInheritanceTest` (IL holder, CLR base) exposing
        `ReadBase()` (ldfld `TestVal2`) and `WriteBase(int)` (stfld `TestVal2`).
      - `public static void NeoStepIlClrBase_TC1_RoundTrip()`: newobj the holder, `WriteBase(4242)`,
        read back, `if (v != 4242) { 1/0 }`.
      - (Optional) `NeoStepIlClrBase_TC2_ReadDefault`: read `TestVal2` default (200), assert == 200.
- [x] Both probe method names embed "NeoStep" so the `NeoStep` smoke filter picks them up.
- [x] Build TestCases with plain `Debug` (NEVER `Debug_Neo`):
      `dotnet build TestCases/TestCases.csproj -c Debug`.

## 4. Verify (Neo)
- [x] Build CLI: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental`.
- [x] Stash-toggle FAIL check: temporarily revert the two handler edits, run the probe filter, and
      confirm it FAULTs on the tagged NIE; restore the edits.
- [x] NeoStep smoke: `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build --
      TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep`
      -> expect 326 -> 327/328, 0 failures.
- [x] Confirm the typed arms and the CLR ref-type / CLR VT raw-owner arms are unregressed (the
      existing `NeoStepRawFld_*` probes still pass).

## 5. Verify (Legacy-neutral)
- [x] Plain `Debug` CLI + `useRegister=true` + `NeoStep` filter: the new probes pass under Legacy
      too, and the pre-existing failure set/count is unchanged (Legacy-neutral by construction --
      all edits are under `#if ENABLE_NEO_MODE`).

## 6. Full-smoke confirmation (optional, pre-crash)
- [x] Drop the `NeoStep` filter and confirm the ~5 "IL-instance owner with a CLR-base field is
      deferred" tagged-NIE occurrences are gone (the full run still NRE-crashes mid-stream on the
      known unrelated `GenericMethodTest` NRE -- counts are pre-crash).
