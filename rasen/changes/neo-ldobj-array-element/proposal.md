# Proposal: neo-ldobj-array-element

> Child 26 of the `neo-overhaul` portfolio. Capability: `neo-value-types`.
> Branch: `features/object-model-overhaul`. Neo-gated (`#if ENABLE_NEO_MODE` -> Legacy-neutral).

## Why

`ldobj` on a CLR-struct ARRAY ELEMENT fails under Neo. The real-world shape is the compound
read-modify-write on a struct array element:

```csharp
TestVector3[] arr2 = new TestVector3[10];
arr2[0].X = 1243;
arr2[0] += TestVector3.One;   // <-- fails
```

Roslyn lowers `arr2[0] += TestVector3.One` to `ldelema TestVector3; ldobj TestVector3; <op_Addition>;
stobj TestVector3`. The `ldobj` (load the WHOLE struct element by address into a temp) and the `stobj`
(store the result back to the element address) are the value-type-sized copy-through-pointer opcodes.
Both decode the `ldelema`-produced byref `(arrIdx, elementIdx)`.

On HEAD, the `ldobj` value-type arm (`ILIntepreter.Neo.cs`, the `Ldobj` case) handles the frame-native
byref (`objIdx == -1`), the CLR-object field (`NeoIsClrObject`, Area 4d), and the IL instance
(`GetNeoILInstance`, incl. the F-10 boxed-CLR-struct-field sub-case) -- but NOT an `Array` owner. An
`Array` is neither a frame-native byref nor a `NeoIsClrObject` (the helper explicitly excludes `Array`),
so it falls to `GetNeoILInstance`, whose defensive tail throws the tagged NIE:

```
Step 17/13b: field/element access on a CLR object via the IL-instance path is deferred
(CLR field-hash plumbing lands in Step 13b). Owner type: ILRuntimeTest.TestFramework.TestVector3[]
```

RE-AUDIT CONFIRMED on HEAD (run of `UnitTest_10047` under Neo): exactly this NIE, 1 test failed.
NeoStep baseline 371/0 (child-25 state) re-confirmed. The `stobj` arm has the IDENTICAL gap (same
branch structure); after fixing `ldobj`, the `+=` chain proceeds to `stobj`, which would NIE the same
way -- so the COMPLETE fix for the read-modify-write pattern covers BOTH `ldobj` (READ) and `stobj`
(WRITE-back).

This is the byref-address counterpart of child-24 (raw `Ldfld` array element) and child-19 (raw `Stfld`
array element), and a sibling of child-25 (`ref arr[i]` byref param marshal). The triage batch-2
R1-Shape-B verdict: REAL + TRACTABLE, ~10-15 lines per arm, single file, Neo-gated.

## What Changes

- **`ILIntepreter.Neo.cs`** -- add an `Array` branch to BOTH the `Ldobj` and `Stobj` value-type arms
  (between the `NeoIsClrObject` arm and the `GetNeoILInstance` fallback):
  - `Ldobj` (READ): decode `(arrIdx, elementIdx)` from the byref at `ip->SrcOffset`;
    `((Array)mStack[arrIdx]).GetValue(elementIdx)` boxes the VT element; `WriteNeoValueType` flattens
    it into `frameBase + ip->DstOffset`.
  - `Stobj` (WRITE): decode `(arrIdx, elementIdx)` from the byref at `ip->DstOffset`;
    `ReadNeoValueType(t.TypeForCLR, ...)` boxes the src flat bytes at `ip->SrcOffset`;
    `((Array)mStack[arrIdx]).SetValue(boxed, elementIdx)`. (The `+=` is a read-modify-write, so BOTH
    the read and the write-back must work for the real trigger `UnitTest_10047` to progress.)
- **`ILRuntimeTestBase/TestFramework/TestVector3.cs`** -- add an int `One` + `operator +` to
  `NeoArrElemIntProbe` (the child-24 int-field CLR struct) so the probe can drive a struct-array
  read-modify-write with PURE int arithmetic.
- **`TestCases/NeoStepLdobjArrayElementTest.cs`** (new) -- NeoStep probes (TC1 `arr[0] += One`, TC2
  element-index decode) using `NeoArrElemIntProbe`, asserting the mutated element's exact int field
  values via the HOST helper `NeoArrElemFieldSum` (passes the ARRAY + index, NOT the element by value
  -- a plain `a = arr[i]` lowers to `ldelem.any`, a separate broken path). MUST FAULT on HEAD (the
  ldobj NIE); PASS after. The int struct is used (not TestVector3) because TestVector3's float
  constructor + float op_Addition-return are pre-existing broken (out of scope; see design).

## Marker vs runtime detection (the crux -- RESOLVED)

**VERDICT: RUNTIME DETECTION SUFFICES -- NO JIT marker needed.** This matches child-25
(`NeoMarshalByrefFieldToSlot` `target is Array`, no marker) and child-19 (raw-`Stfld` array, runtime
`mStack[objIdx] is Array`, no marker). It DIFFERS from child-24 (raw-`Ldfld` array, which REQUIRED a
JIT marker `NeoRawLdfldArrayElementByRefMarker`).

Reasoning (the asymmetry child-24 documented, applied to ldobj/stobj):
- `ldobj`/`stobj`'s pointer operand is ALWAYS a byref (managed pointer). CIL `ldobj`/`stobj` copy a
  value type to/from an ADDRESS -- the operand is inherently an address, never a direct value.
- The byref's `objIdx` half (decoded at runtime) is, when `>= 0`, ALWAYS a genuine mStack index set by
  a byref producer (`ldelema` -> Array, `ldflda` IL-field -> ILTypeInstance, `ldflda` CLR-object-field
  -> CLR object, `ldflda` F-10 -> ILTypeInstance). It is NEVER a struct's first-field value.
- The `objIdx == -1` case (frame-native byref -> flat bytes in the frame) is handled by the FIRST branch
  in each arm, BEFORE the `Array` check. So when the `Array` check runs, `objIdx >= 0` is guaranteed and
  `mStack[objIdx]` is the byref's referent. `mStack[objIdx] is Array` is true IFF the byref was produced
  by `ldelema` on an array -- unambiguous. No silent-corruption false-positive vector exists.
- Contrast child-24 raw-`Ldfld`: the owner REGISTER held the VALUE directly. For `ldloc`/`ldsfld`-by-
  value, the owner was FLAT BYTES, and the "objIdx" decoded from the first 4 bytes was the struct's
  first field value -- which could coincidentally index into an mStack slot holding an Array -> false
  positive -> silent corruption. That ambiguity FORCED a JIT marker. ldobj/stobj have no such ambiguity
  (their operand is always a byref, never flat bytes that get reinterpreted as an index).

## Impact

- Neo-only; `ILIntepreter.Neo.cs` is file-gated `#if ENABLE_NEO_MODE`. Legacy compiles none of it.
- NeoStep smoke **373/0** (371 baseline + TC1 + TC2), no regressions. `UnitTest_10047` PROGRESSES (the
  ldobj NIE is gone; the test now reaches its own value assertion -- the residual is the pre-existing
  float `op_Addition`-return bug, out of scope). The sibling regression families (child-19 Stfld-array,
  child-24 Ldfld-array, child-25 byref-marshal, child-15 ldind/stind, NeoStep12/13/17 VT invocations)
  stay green. Legacy-neutral: plain Debug + useRegister=true + NeoStep = 373 ran / 18 failed (the
  documented pre-existing Legacy set; both probes PASS under Legacy).
