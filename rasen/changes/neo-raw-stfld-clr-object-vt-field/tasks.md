# Tasks — neo-raw-stfld-clr-object-vt-field

## 1. Implement the runtime branch (ILIntepreter.Neo.cs, Neo-gated)
- [x] In the raw `Stfld` CLR-value-type-owner arm, replace the `else` NIE with:
  - `else if (objIdx >= 0)`: null-check (NRE), IL-instance check (tagged
    deferred NIE for F-10 ManagedObjects storage), then the CLR-object-field
    box/mutate/unbox (`NeoReadClrObjectField` -> `f.SetValue` ->
    `NeoWriteClrObjectField`, using `off` = structFieldHash).
  - `else`: keep the unrecognized-shape NIE.

## 2. Host types + helper (ILRuntimeTestBase)
- [x] Add `NeoClrObjVtFieldProbe` (3 int fields a/b/c) + `NeoClrObjVtFieldOwner`
  (CLR class with `public NeoClrObjVtFieldProbe S;`) to `TestVector3.cs`.
- [x] Add `TestCLRBinding.NeoClrObjVtFieldProbeSum(NeoClrObjVtFieldOwner o)` to
  `TestClass3.cs` (host reflection read-back).

## 3. Probe (TestCases/NeoStepRawStfldClrObjVtFieldTest.cs)
- [x] TC1 `NeoStepRawStfldClrObjVtField_TC1`: single write `o.S.a = 111`; host
  sum == 111. Hand-checked: 111+0+0.
- [x] TC2 `NeoStepRawStfldClrObjVtField_TC2`: three writes a=111,b=222,c=333;
  host sum == 666. Hand-checked: 111+222+333. Proves field preservation.

## 4. Verify
- [x] Build CLI (`Debug_Neo --no-incremental`) + TestCases (`Debug`); killed
  build-server + `-p:UseSharedCompilation=false` for TestCases.
- [x] NeoStep smoke **375/0** (373 + TC1 + TC2), no regressions.
- [x] `UnitTest_Struct2` PROGRESSES (was NIE at LightTester1.cs:103
  `obj.Struct.value = 111`; now PASSES line 103, fails at line 104
  `obj.Struct.value += 111` -- the nested-ldflda `+=` sibling, NRE at
  ExecuteNeo:5450, out of scope).
- [x] Stash-toggle: stash ILIntepreter.Neo.cs -> 2/2 FAULT (Stfld NIE
  "unrecognized CLR value-type owner byref shape (objIdx=3). Field a on
  NeoClrObjVtFieldProbe") -> pop -> 375/0 PASS (airtight).
- [x] Write-back persistence: TC2 host read-back == 666 (all three values
  111/222/333 preserved across separate box/mutate/unbox writes).
- [x] Legacy-neutral: plain Debug + useRegister=true + NeoStep == 375 ran /
  18 failed (pre-existing Legacy set; was 373/18; both probes PASS under
  Legacy 2/0).
