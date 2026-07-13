# Tasks: neo-raw-ldfld-clr-object-vt-field

## 1. JIT marker (JITCompiler.cs) — `ENABLE_NEO_MODE`
- [ ] Add `public const int NeoRawLdfldClrObjectFieldByRefMarker = 0x2;` next to
      the existing `NeoRawLdfldArrayElementByRefMarker = 0x1` (~:227).
- [ ] In the `case Code.Ldfld` CLRType `else` branch (~:3125-3147), AFTER the
      existing `Ldelema` stamp, add:
      `if (ins.Previous != null && ins.Previous.OpCode.Code == Code.Ldflda)
         op.Operand4 |= NeoRawLdfldClrObjectFieldByRefMarker;`
      (Mutually exclusive with the Ldelema stamp: one predecessor per CIL
      instruction. Brief comment naming this change + the mutual-exclusivity
      rationale.)

## 2. Runtime branch (ILIntepreter.Neo.cs) — file-gated Neo
- [ ] In the raw-Ldfld `if (ct.TypeForCLR.IsValueType)` branch (~:3981), insert
      a new `else if` between the child-24 array-element marker check (~:3983)
      and the flat-bytes `else` (~:4003):
      - decode `objIdx = *(int*)(frameBase + ownerOff)`,
        `off = *(int*)(frameBase + ownerOff + 4)`.
      - if `objIdx == -1`: read flat bytes at `frameBase+off` via
        `ReadNeoValueType` + `f.GetValue` (defensive: nested ldflda-on-frame-
        local; struct flat bytes ARE at that frame address).
      - else: `target = mStack[objIdx]`; null -> NRE; ILTypeInstance /
        CrossBindingAdaptorType -> tagged deferred NIE (F-10 sibling);
        otherwise `boxedStruct = NeoReadClrObjectField(AppDomain, target, off)`,
        `fldVal = f.GetValue(boxedStruct)`.
      - leave the existing dest marshalling (primitive / VT / ref) unchanged.
      - comment naming this change + the box-read-direction mirror of child-27.

## 3. Probe (TestCases/NeoStepRawLdfldClrObjVtFieldTest.cs) + host helper
- [ ] Host helper `BuildNeoClrObjVtFieldOwner(int a, int b, int c)` in
      `TestCLRBinding` (`TestClass3.cs`) — writes struct field VALUES on the CLR
      side (isolates the READ from the sibling Stfld WRITE / Neo float arith).
- [ ] TC1 single-field read: `o.S.a` host-set 111 -> IL reads `o.S.a` -> assert
      == 111 (1/0 on mismatch). FAULT on HEAD (reads objIdx).
- [ ] TC2 three-field read + IL sum: read `o.S.a/b/c`, sum -> assert == 666.
      FAULT on HEAD (garbage sum).

## 4. Verify
- [ ] Build: `dotnet build ILRuntimeTestCLI -c Debug_Neo --no-incremental` (0
      errors); `dotnet build TestCases -c Debug` (0 errors). KILL build-server +
      `-p:UseSharedCompilation=false` after touching TestClass3.cs (child-25
      cache gotcha); rebuild the CLI too so its bin picks up the fresh
      ILRuntimeTestBase.
- [ ] Smoke: `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI
      --no-build -- TestCases/.../TestCases.dll HotfixAOT/Patched/HotfixAOT.patch
      true NeoStep` -> 380/0 (378 baseline + TC1 + TC2).
- [ ] Stash-toggle: stash the TWO engine files (JITCompiler.cs +
      ILIntepreter.Neo.cs), keep probe + helper -> TC1/TC2 FAULT (DivideByZero)
      -> pop -> 380/0.
- [ ] Sibling families green: child-4 (NeoStepRawFld_TC1/TC2 flat-bytes),
      child-9 (IlClrBase), child-19/24 (array-element Stfld/Ldfld), child-21
      (FloatSeeding incl raw-Ldfld-CLR-struct), child-27 (RawStfldClrObjVtField
      TC1/TC2), child-28 (FloatVtReturn), NeoStep12/13/17 (VT).
- [ ] Read-back CORRECT (exact values: TC1 x==111, TC2 sum==666).
- [ ] Legacy-neutral: plain `Debug` + `useRegister=true` + `NeoStep` ==
      pre-existing set; both probes PASS under Legacy.
