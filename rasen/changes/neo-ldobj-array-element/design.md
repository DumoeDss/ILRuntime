# Design: neo-ldobj-array-element

## The gap (re-audit-confirmed)

`UnitTest_10047` (TestValueTypeBinding.cs:473):
```csharp
TestVector3[] arr2 = new TestVector3[10];
arr2[0].X = 1243;
arr2[0] += TestVector3.One;
if (Math.Abs(arr2[0].X - 1244) > 0.001f) throw new Exception();
```

JIT dump (Final Results) for UnitTest_10047 on HEAD:
```
3:ldelema r2,r0,r3       # &arr2[0] for arr2[0].X = 1243 (Stfld array element, child-19 -- works)
7:ldelema r2,r0,r3       # &arr2[0] for the += read
8:ldobj r3, r2           # load arr2[0] whole struct (THE NIE on HEAD)  <- TestValueTypeBinding.cs:477
11:stobj r2, r3          # store op_Addition result back to arr2[0]     <- same Array gap
13:ldelema r2,r0,r3      # &arr2[0] for the final Math.Abs read check
```

The NIE fired at `ldobj` (JIT_0008) on HEAD:
```
Step 17/13b: field/element access on a CLR object via the IL-instance path is deferred
(CLR field-hash plumbing lands in Step 13b). Owner type: ILRuntimeTest.TestFramework.TestVector3[]
```

This is `GetNeoILInstance` (`ILIntepreter.Neo.cs:6411`). It is reached because `NeoIsClrObject`
(`:6489`) explicitly returns FALSE for an `Array` (`!(o is Array)` at `:6496`), so the `ldobj` arm's
`else` branch (`:5779` `ins = GetNeoILInstance(...)`) handles an Array owner -- and throws.

After the `ldobj` read is fixed, the `+=` chain proceeds to `stobj` (`:5631`), whose `else` branch
(`:5681`) has the SAME `GetNeoILInstance` call and the SAME Array gap. So the COMPLETE fix for the
read-modify-write pattern covers BOTH arms.

## The fix point (both arms)

### Ldobj arm (READ) -- `ILIntepreter.Neo.cs`, `case OpCodeREnum.Ldobj:` (~:5734)

Current branch order:
```
if (objIdx == -1) { ... frame-native CopyBlock ... }                       // :5742
else if (NeoIsClrObject(mStack, objIdx)) { ... 4d read ... }               // :5771
else { ins = GetNeoILInstance(...); ... F-10 / IL-instance ... }           // :5779  <-- NIE for Array
```

ADD an `Array` branch between `NeoIsClrObject` and the `else`:
```csharp
else if (mStack[objIdx] is Array ldobjArr)
{
    // neo-ldobj-array-element: ldobj of a CLR value-type ARRAY ELEMENT. The byref
    // source (ip->SrcOffset) is the ldelema-produced (arrIdx, elementIdx); `off`
    // IS the element index (NOT a byte offset, NOT a field hash) -- the convention
    // at ILIntepreter.Neo.cs:5874-5875, the same one stind/ldind, the raw
    // Stfld/Ldfld array-element arms (children 19/24), and the byref-param marshal
    // (child-25) consume. Array.GetValue boxes the VT element; WriteNeoValueType
    // flattens it into dest. READ counterpart of child-19's raw-Stfld array-element
    // WRITE, sibling of child-25's forward-deref arm. Runtime detection is SAFE
    // here (unlike child-24's raw-Ldfld, which needed a JIT marker): ldobj's source
    // is ALWAYS a byref, so `off>=-1 check above` guarantees mStack[objIdx] is the
    // byref's referent and `is Array` is unambiguous (no flat-bytes ambiguity).
    object elemVal = ldobjArr.GetValue(off);
    if (elemVal != null)
        ILIntepreter.WriteNeoValueType(elemVal, frameBase + ip->DstOffset, primSize);
    else
        Unsafe.InitBlock(frameBase + ip->DstOffset, 0, (uint)primSize);
}
```

- `primSize = ilType != null ? ilType.TotalPrimitiveSize : AppDomain.GetPrimitiveSize(t)` is already
  computed above. For a CLR struct (ilType == null), `GetPrimitiveSize` returns
  `Optimizer.GetNeoValueTypeManagedSize(t.TypeForCLR)` = `Unsafe.SizeOf<TestVector3>()` = 12
  (AppDomain.cs:2335-2353). Correct.
- `WriteNeoValueType(value, dst, sz)` uses `sz` ONLY as a `> 0` guard; the per-type writer writes
  exactly `sizeof(T)` bytes via `Unsafe.WriteUnaligned`. So `primSize` just needs to be > 0 (it is).

### Stobj arm (WRITE) -- `ILIntepreter.Neo.cs`, `case OpCodeREnum.Stobj:` (~:5631)

Current branch order:
```
if (objIdx == -1) { ... frame-native CopyBlock ... }                       // :5639
else if (NeoIsClrObject(mStack, objIdx)) { ... 4d write ... }              // :5672
else { ins = GetNeoILInstance(...); ... F-10 / IL-instance ... }           // :5681  <-- NIE for Array
```

ADD an `Array` branch between `NeoIsClrObject` and the `else`:
```csharp
else if (mStack[objIdx] is Array stobjArr)
{
    // neo-ldobj-array-element: stobj of a CLR value-type ARRAY ELEMENT (the WRITE
    // counterpart of the ldobj READ above). The byref DEST (ip->DstOffset) is the
    // ldelema-produced (arrIdx, elementIdx); `off` IS the element index. Read the
    // src flat bytes into a boxed struct via ReadNeoValueType and store via
    // Array.SetValue. WRITE counterpart of child-19's raw-Stfld array-element arm
    // and child-25's write-back arm. Same runtime-detection soundness as ldobj.
    int srcCur = ip->SrcOffset;
    object boxed = ILIntepreter.ReadNeoValueType(t.TypeForCLR, frameBase, ref srcCur, primSize);
    stobjArr.SetValue(boxed, off);
}
```

- `stobj` decodes the byref from `ip->DstOffset` (the WRITE target address) and writes FROM
  `ip->SrcOffset` (the value). `ldobj` is the mirror (byref from `ip->SrcOffset`, write TO
  `ip->DstOffset`).
- `ReadNeoValueType(clr, frameBase, ref curPrim, sz)` reads `sz` managed bytes at `frameBase+curPrim`
  into a boxed object, advancing curPrim. `t.TypeForCLR` = `typeof(TestVector3)`.

## Why no JIT marker (the crux -- full reasoning)

child-24 (raw `Ldfld` array element) NEEDED a JIT marker (`NeoRawLdfldArrayElementByRefMarker = 0x1`
stamped in `Operand4`) because the raw-`Ldfld` owner REGISTER held the value DIRECTLY:
- For `ldloc`/`ldsfld`-by-value of a CLR struct, the owner slot is FLAT MANAGED BYTES. The "objIdx"
  decoded from the first 4 bytes is the struct's FIRST FIELD VALUE -- a small non-negative int could
  land in mStack range and `mStack[objIdx] is Array` FALSE-POSITIVES -> silent corruption.
- For `ldelema` of an array element, the owner slot is the byref `(arrIdx, elementIdx)`.
- The two shapes are INDISTINGUISHABLE at runtime (same register, same decode) -> the JIT-time
  dataflow fact (was the immediate CIL predecessor `ldelema`?) must be stamped as a marker.

`ldobj`/`stobj` do NOT have this ambiguity:
- The pointer operand is ALWAYS a byref. CIL `ldobj`/`stobj` copy a value type to/from an ADDRESS.
  There is no "by-value" form of ldobj/stobj -- the operand is inherently a managed pointer.
- The byref's `objIdx` half is ALWAYS a genuine mStack index (set by a byref producer) OR `-1`
  (frame-native, set by `ldloca`). It is NEVER a struct field value reinterpreted as an index.
- The `objIdx == -1` frame-native case is dispatched by the FIRST branch in each arm, BEFORE the
  `Array` check. So at the `Array` check, `objIdx >= 0` and `mStack[objIdx]` is the referent. `is Array`
  is unambiguous.

This is the SAME asymmetry child-24 documented: "A value-type-owner `Stfld` is ALWAYS a byref"
(child-19, runtime detection OK) vs "A value-type-owner `Ldfld` is EITHER flat bytes OR a byref"
(child-24, marker needed). `ldobj`/`stobj`'s pointer operand is, like `Stfld`'s owner, ALWAYS a byref
-> runtime detection is safe.

## Probe design (final -- int-field struct; see the float-bug finding below)

`TestCases/NeoStepLdobjArrayElementTest.cs` (new), method names embed "NeoStep". The probe uses
`NeoArrElemIntProbe` (an INT-field CLR struct, `int A; int B;`, to which an int `operator +` + `One`
were added in `TestVector3.cs:409`) -- PURE int arithmetic, isolating ldobj/stobj from the pre-existing
float machinery bugs (see "Out of scope"). The CIL `arr[i] += <struct>` shape lowers identically for an
int struct and a float struct (`ldelema; ldobj; op_Addition; stobj`), so the int probe exercises the
SAME ldobj/stobj arms.

- **TC1** (the faithful `+=` read-modify-write trigger): host-build `arr = BuildNeoArrElemProbeArray
  (100,200,1,2)` so `arr[0]=(100,200)` (host-side seed, no IL constructor/Stfld); `arr[0] += One`
  (ldelema + ldobj READ + int op_Addition + stobj WRITE-back) -> `(101,201)`; HOST read-back
  `NeoArrElemFieldSum(arr, 0)` = 302. FAULTS on HEAD (ldobj NIE).
- **TC2** (element-index decode): the same `+=` at TWO distinct indices (`arr[0]` and `arr[1]`);
  HOST read-back `NeoArrElemFieldSum(arr,0) + NeoArrElemFieldSum(arr,1)` = (101+201) + (2+3) = 307.
  A wrong element-index decode yields a different combined sum.

Read-back passes the ARRAY + index to a HOST helper (`NeoArrElemFieldSum`, child-24, already exists),
NOT the element by value. REASON: a PLAIN IL-side `a = arr[i]` lowers to `ldelem.any` (a direct typed
load), NOT `ldelema; ldobj` -- and `ldelem.any` on a CLR-struct array is a SEPARATE broken path (it
returns garbage, e.g. `(6E-45,0,0)`), which would corrupt the assertion independent of this fix. Only
the ADDRESS-TAKING read-modify-write (`+=`) emits `ldobj`/`stobj`. So the probe reads back on the host
(mirrors child-24/25's `NeoArrElemFieldSum`/`NeoByrefArrElemMutate...` pattern). Each probe's expected
constant is hand-checked against its inputs (child-24 lesson: a FAULTING probe gives no info about
whether its PASS constant is reachable).

NOTE on the `+=` JIT shape (confirmed via dump, `Final Results`):
```
2:ldelema r5,r0,r6   # &arr[0] -> r5 (byref (arrIdx,0))
3:ldobj r6, r5       # r6 = arr[0]            [Ldobj Array branch, READ]
5:call.redirect op_Addition r6, r6, r7   # r6 = r6 + One
6:stobj r5, r6       # arr[0] = r6            [Stobj Array branch, WRITE-back]
```
op_Addition's dest (r6) aliases its param1 (r6); this is NOT a problem for a by-value VT param (the
callee gets its own copy in CopyNeoCallArguments before the return write).

## Read-back correctness (not just non-throwing)

The probes assert EXACT int field-value sums computed on the HOST side, not just "ran without throwing":
- TC1: after `arr[0] += One` on `(100,200)`, `NeoArrElemFieldSum(arr,0)` = 101+201 = 302.
- TC2: `(101+201) + (2+3)` = 307. A wrong element-index decode or an unfaithful flat-byte copy produces
  a different sum -> DivideByZero (deliberate `1/0` on mismatch).

## Verify (RESULTS)

- NeoStep smoke **373/0** (371 baseline + TC1 + TC2). No regressions.
- Stash-toggle of `ILIntepreter.Neo.cs` ONLY (probe + int operator+ kept) -> **2/2 FAULT** (the exact
  ldobj NIE, "Owner type: NeoArrElemIntProbe[]") -> pop -> **373/0** (airtight).
- `UnitTest_10047` (the real trigger): on HEAD, the ldobj NIE; AFTER the fix, the NIE is GONE -- the
  test now reaches its OWN value assertion (`Math.Abs(arr2[0].X - 1244) > 0.001f`). So it PROGRESSES
  (NIE eliminated) but does not fully pass, because the residual is the pre-existing float
  `op_Addition`-return/constructor bug (Out of scope). The int probe proves ldobj + stobj are correct.
- Legacy-neutral: plain `Debug` + `useRegister=true` + `NeoStep` = **373 ran / 18 failed** (the
  documented pre-existing Legacy 18-failure set; +2 probes PASS under Legacy). The fix is 100%
  `#if ENABLE_NEO_MODE` (ILIntepreter.Neo.cs is file-gated).

## Out of scope

- A multi-dimensional CLR-struct array element via ldobj/stobj (the byref `off` half is a single
  element index for a 1-D array per the ldelema convention; multi-dim ldelema on a CLR-struct array is
  an unexercised edge). The single-int `Array.GetValue(int)`/`SetValue(obj, int)` mirror the
  established child-19/24/25 pattern.
- A reference-TYPE-element array via ldobj/stobj (UNREACHABLE: ldelema rejects a non-value-type
  element at `:5873` with a tagged NIE). The `InitBlock`-on-null arm is kept for symmetry but never
  fires for the value-type path.
- **The pre-existing float bug that blocks a TestVector3 (float-field) `+=` probe from PASSING.** A
  `new TestVector3(float,float,float)` CLR-struct constructor produces a ZERO struct under Neo (the
  float args do not reach the ctor fields), and `TestVector3.op_Addition`'s VT-with-float-fields return
  does not write back (the `+=` leaves the element unchanged) -- so a TestVector3 `+=` probe cannot
  PASS end-to-end even with the ldobj/stobj fix. This is the SAME defect class as the child-16/21 float-
  producer seeding family but in the ctor-arg / VT-return path (a candidate sibling child). The int
  probe sidesteps it entirely and proves the ldobj/stobj arms correct. `UnitTest_10047` (which uses
  TestVector3) PROGRESSES (NIE gone) but hits this residual float bug.
- `ldelem.any` / `stelem.any` on a CLR-struct array (a PLAIN `a = arr[i]` or `arr[i] = v` lowering --
  a separate broken path, NOT ldobj/stobj; out of scope for this child).

## Files

- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- `Array` branch in the `Ldobj` arm +
  `Array` branch in the `Stobj` arm (Neo-gated by the file-level `#if ENABLE_NEO_MODE`).
- `ILRuntimeTestBase/TestFramework/TestVector3.cs` -- added `One` + int `operator +` to
  `NeoArrElemIntProbe` (so the probe can drive an int struct-array read-modify-write).
- `TestCases/NeoStepLdobjArrayElementTest.cs` (new) -- TC1 + TC2 (int struct, HOST read-back via the
  existing `NeoArrElemFieldSum` + `BuildNeoArrElemProbeArray` child-24 helpers).
