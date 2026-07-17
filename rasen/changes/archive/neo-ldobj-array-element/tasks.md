# Tasks: neo-ldobj-array-element

## 1. Implement the Array branches (Ldobj READ + Stobj WRITE) -- DONE

- [x] In `ILIntepreter.Neo.cs`, `case OpCodeREnum.Ldobj:` (~:5771): added
      `else if (mStack[objIdx] is Array ldobjArr)` between the `NeoIsClrObject` arm and the `else`
      (GetNeoILInstance) arm. Body: `object elemVal = ldobjArr.GetValue(off);` -> if non-null
      `ILIntepreter.WriteNeoValueType(elemVal, frameBase + ip->DstOffset, primSize);` else
      `Unsafe.InitBlock(frameBase + ip->DstOffset, 0, (uint)primSize);`. `off` == elementIdx.
- [x] In `ILIntepreter.Neo.cs`, `case OpCodeREnum.Stobj:` (~:5672): added
      `else if (mStack[objIdx] is Array stobjArr)` between the `NeoIsClrObject` arm and the `else` arm.
      Body: `int stobjSrcCur = ip->SrcOffset; object stobjBoxed = ILIntepreter.ReadNeoValueType
      (t.TypeForCLR, frameBase, ref stobjSrcCur, primSize); stobjArr.SetValue(stobjBoxed, off);`.
      (BOTH arms are needed: `arr[i] += x` is a read-modify-write -- ldobj READ then stobj WRITE-back.)
- [x] Both branches are inside the file-level `#if ENABLE_NEO_MODE` (Neo-gated -> Legacy-neutral).

## 2. Probe struct + host helpers -- DONE

- [x] `ILRuntimeTestBase/TestFramework/TestVector3.cs`: added `One` + int `operator +` to
      `NeoArrElemIntProbe` (int fields A, B) -- PURE int arithmetic, isolating ldobj/stobj from the
      pre-existing float bugs.
- [x] Reused the existing child-24 host helpers `BuildNeoArrElemProbeArray` + `NeoArrElemFieldSum`
      (HOST-side element read-back; no new host helper needed).

## 3. Add the NeoStep probe -- DONE

- [x] New `TestCases/NeoStepLdobjArrayElementTest.cs`, class `NeoStepLdobjArrayElementTest`:
  - TC1: `arr[0] += One` (host-built arr[0]=(100,200)); HOST read-back `NeoArrElemFieldSum(arr,0)` ==
        302 (101+201). Hand-checked.
  - TC2: `arr[0] += One; arr[1] += One`; combined `NeoArrElemFieldSum(arr,0)+NeoArrElemFieldSum(arr,1)`
        == 307 (302 + 5). Element-index decode. Hand-checked.
  - Read-back passes the ARRAY + index (NOT the element by value -- a plain `a = arr[i]` lowers to
        `ldelem.any`, a separate broken path). Both FAULT on HEAD (ldobj NIE); PASS after.

## 4. Verify -- DONE

- [x] NeoStep smoke **373/0** (371 baseline + TC1 + TC2), no regressions.
- [x] `UnitTest_10047` PROGRESSES (ldobj NIE gone; the test now reaches its own value assertion; the
      residual is the pre-existing float `op_Addition`-return bug, out of scope -- proven by the int
      probe which exercises the same ldobj/stobj arms and PASSES).
- [x] Stash-toggle FAIL->PASS: stash `ILIntepreter.Neo.cs` ONLY -> rebuild -> **2/2 FAULT** (exact
      ldobj NIE, "Owner type: NeoArrElemIntProbe[]"); pop -> **373/0** (airtight).
- [x] Read-back correctness: probes assert EXACT int field-value sums (302, 307), not just non-throwing.
- [x] Legacy-neutral: plain `Debug` + `useRegister=true` + `NeoStep` = **373 ran / 18 failed**
      (documented pre-existing Legacy set; both probes PASS under Legacy).

## Surfaced follow-up (out of scope)

- A TestVector3 (float-field) `+=` probe cannot PASS end-to-end: `new TestVector3(float,float,float)`
  yields a ZERO struct (float ctor args do not reach the fields), and `TestVector3.op_Addition`'s
  VT-with-float-fields return does not write back. Same defect class as the child-16/21 float-producer
  seeding family but in the ctor-arg / VT-return path. Candidate sibling child. The int probe
  sidesteps it and proves ldobj + stobj correct.
- `ldelem.any` / `stelem.any` on a CLR-struct array (a plain `a = arr[i]` / `arr[i] = v`) returns
  garbage under Neo -- a separate broken path (NOT ldobj/stobj).
