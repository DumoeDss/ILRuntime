# Triage Batch 2 — neo-overhaul (4 low-frequency candidate gaps)

> Re-audit of 4 framed gaps. HEAD `5b980cb8`. Methodology: built CLI/TestCases
> (Debug_Neo + Debug), ran the full Neo smoke (NO filter) to completion (902 ran /
> 192 failed, exit 0 -- NO pre-crash this time, so counts are COMPLETE not
> pre-crash estimates), grepped the 3 tagged NIE messages, read each triggering
> test site, and ran the child-18 TC4-equivalent under plain Debug+useRegister=true
> for L1. NeoStep baseline re-confirmed 368/0.
>
> HEADLINE: the 12-for-12 "framed gaps are disproven" streak is OVER for this
> batch -- 3 of the 4 candidates are REAL+TRACTABLE on HEAD (children 21-24 were
> real too; the recent trend holds). Only L1 (Legacy-only) is low-value. R1
> splits into TWO shapes: one delegate artifact (Step-19-blocked), one genuine
> ldobj array-element gap.

## Smoke frequency on HEAD (full Neo run, complete, 902 ran / 192 failed)

| cand | tagged NIE message | hits | distinct failing tests |
|------|--------------------|------|------------------------|
| R1 | "Step 17/13b ... IL-instance path is deferred" | 8 | 2 (DelegateExtTest02, UnitTest_10047) |
| R2 | "Step 13 Area 4c: a CLR-array-element byref param is not handled" | 4 | 1 (CLRBindingTest01) |
| R3 | "Neo raw Stfld: unrecognized CLR value-type owner byref shape" | 4 | 1 (UnitTest_Struct2) |

(The task's "~4/~2/~2" were PRE-CRASH estimates from the earlier crashing run;
the run now completes, so the real counts are 8/4/4. Every hit is a real test
failure, not noise.)

---

## R1 -- Step 17/13b "CLR object via the IL-instance path" -- SPLIT VERDICT

Exact site: `ILIntepreter.Neo.cs:6334` (the defensive tail of `GetNeoILInstance`,
`:6302`). Reached by EVERY JIT-typed IL-instance field arm (`ldfld.i4` /
`ldfld.ref` / `stfld.*` / `ldobj` / `stind`/`ldind` -- ~40 call sites). The
helper handles `ILTypeInstance` (return) and `CrossBindingAdaptorType` (unwrap to
`.ILInstance`, child-9); anything else throws. Children 9/10 closed the CBA shape;
the remaining NIE is the "any other CLR shape" guard. It fires for TWO distinct
shapes on HEAD -- with DIFFERENT verdicts:

### R1-Shape-A (delegate-path artifact) -- NOT REAL as a field-access gap
- Trigger: `TestCases.DelegateExtTest.DelegateExtTest02` (`DelegateExtTest.cs:64`):
  `Action<int> a = null; a += obj.IntTest; a += obj.IntTest2; ...` -- delegate
  Combine + invoke. The NIE site is `ILIntepreter.Neo.cs:3989` (a `ldfld.i4` typed
  arm reading `DelegateExtObj.Value`), reporting **Owner type: System.Int32**.
- A boxed `System.Int32` as the receiver of `ldfld.i4 Value` is a malformed
  delegate-dispatch product: delegates are NOT implemented under Neo (Step 19 is
  the next planned step per CLAUDE.md / neo-implementation-steps). The delegate
  invoke stuffed the wrong object into the receiver slot. This is a Step-19
  symptom, NOT an independent field-access gap.
- VERDICT: **unreachable-as-framed / blocked behind Step 19 (delegates)**. No
  field-access child. It will resolve (or transmute) when delegate dispatch lands.

### R1-Shape-B (ldobj of a CLR-struct array element) -- REAL + TRACTABLE
- Trigger: `TestCases.TestValueTypeBinding.UnitTest_10047` (`:473`):
  `TestVector3[] arr2 = new TestVector3[10]; arr2[0].X = 1243; arr2[0] += TestVector3.One;`
  The failing op is `ldobj TestVector3` (JIT `ldobj r20, r8`) at
  `ILIntepreter.Neo.cs:5704`, reporting **Owner type: TestVector3[]**.
- Mechanism: `arr2[0]` (read for `+=`) lowers to `ldelema TestVector3; ldobj`.
  `ldelema` on a CLR-struct array encodes the byref `(arrIdx, elementIdx)` per
  `ILIntepreter.Neo.cs:5797-5798`. The `ldobj` value-type arm (`:5685-5720`)
  handles IL-VT-with-refs (`:5685`), `NeoIsClrObject` (`:5694` -> 4d field-hash
  read), and `GetNeoILInstance` (`:5704`). An Array is NOT a CLR object per
  `NeoIsClrObject`, so it falls to `GetNeoILInstance` -> NIE.
- VERDICT: **REAL + TRACTABLE**. This is the READ counterpart of child-24's
  `Ldfld`-array-element fix and child-19's `Stfld`-array-element fix, for the
  `ldobj` opcode. Next action: add an `mStack[objIdx] is Array` branch to the
  `ldobj` VT arm (decode `(arrIdx, elementIdx)`, `((Array)mStack[arrIdx]).GetValue
  (elementIdx)`, `WriteNeoValueType` to dest -- symmetric to the existing stind/
  ldind array path). Scope sketch ~10-15 lines, single site, Neo-gated. Capability
  `neo-value-types` (siblings child-19/24). Probe MUST FAULT (DivideByZero via a
  host float-sum helper, mirroring child-24 TC1 which asserts 4259).

---

## R2 -- Step 13 Area 4c "CLR-array-element byref param" -- REAL + TRACTABLE (HIGH VALUE)

Exact site: `ILIntepreter.Neo.cs:515` (inside `NeoMarshalByrefFieldToSlot`,
`:453`). Trigger: `TestCases.CLRBindingTest.CLRBindingTest01` (`:16`):
```csharp
byte[] mAllMissionData = new byte[10];
int missionID = 2;
TestClass3.setBit(ref mAllMissionData[(missionID - 1) >> 2], (missionID - 1) & 3, 1);
```
JIT (`IL_001c`): `call setBit(Byte ByRef, Int32, Int32)` with the first arg a
`ref byteArr[idx]`.

- Mechanism: `ref arr[i]` to a CLR call lowers to `ldelema; call`. `ldelema`
  encodes `(arrIdx, elementIdx)`. `CopyNeoCallArguments` (`:391`) sees the byref
  param (`byRefSrc[i]`), objIdx=arrIdx >= 0, and routes to
  `NeoMarshalByrefFieldToSlot` (`:424`, isWrite:false) to deref the referent into
  the callee slot. `target = mStack[arrIdx]` is an `Array` -> NIE at `:515`. The
  post-call write-back (`:577+`, the reverse copy for `ref`/`out`) calls the SAME
  helper with isWrite:true and would hit the same NIE.
- VERDICT: **REAL + TRACTABLE + HIGH VALUE**. `ref arr[i]` into a method is a
  pervasive real-world pattern (this single test is the canary; many production
  hotfix paths do it). Next action: add an `if (target is Array)` branch to
  `NeoMarshalByrefFieldToSlot`: read = `((Array)target).GetValue(off)` flattened
  to slot (off IS the element index per the ldelema convention -- NOT a byte
  offset, NOT a field hash); write = box the slot by `elemType`, `((Array)target)
  .SetValue(boxed, off)`. ONE branch covers BOTH the forward call-arg deref AND
  the post-call write-back (they share this helper). Mirror child-19/24's
  Array.GetValue/SetValue. Scope sketch ~15-20 lines, single helper, Neo-gated.
  Capability `neo-byref` (owns Step-13 Area-4c; sibling child-15/19). Probe MUST
  FAULT (NIE on HEAD) + assert the array element is mutated end-to-end through
  the `ref` param (a `ref byte` increment round-trip).

---

## R3 -- "unrecognized CLR value-type owner byref shape" (raw Stfld) -- REAL + TRACTABLE

Exact site: `ILIntepreter.Neo.cs:4116` (the `else` tail of the raw `Stfld`
CLR-value-type-owner branch, `:4081-4116`). Trigger:
`TestCases.ExpTest_10.UnitTest_Struct2` (`LightTester1.cs:100`):
```csharp
TestClass3 obj = new TestClass3();
obj.Struct.value = 111;     // <-- fails here
obj.Struct.value += 111;
```
JIT (`IL_000f`): `stfld TestStruct::value` with owner byref `objIdx=4`.
Local vars: `TestClass3 obj, StructTest2 obj2`.

- Mechanism: `obj.Struct.value = x` (a CLR-struct FIELD `Struct` on a CLR
  REFERENCE object `TestClass3`, then an int field `value` on that struct) lowers
  to `ldflda obj.Struct; stfld value`. The `ldflda` produces a byref whose objIdx
  parks the CONTAINING CLR object (`TestClass3`), NOT -1 (frame local) and NOT an
  Array. The raw `Stfld` VT-owner handler (`:4081`) handles only `objIdx == -1`
  (frame-local struct, box/mutate/unbox via `ReadNeoValueType`/`WriteNeoValueType`,
  child-4) and `objIdx >= 0 && mStack[objIdx] is Array` (array element, child-19).
  The third shape -- `objIdx >= 0 && mStack[objIdx]` is a CLR object whose struct
  FIELD is the owner -- is the unrecognized NIE.
- VERDICT: **REAL + TRACTABLE (medium)**. Next action: add a third branch to the
  raw-`Stfld` VT-owner handler for `objIdx >= 0 && mStack[objIdx]` is a non-Array
  object: the byref's `off` half identifies the struct field on the containing
  object; box/mutate/unbox ONE LEVEL UP -- read the struct field via the
  containing object's CLRType (`GetFieldValue(structFieldHash)`, or recover from
  the ldflda encoding), `f.SetValue(boxedStruct, value)` for the inner `value`
  field, write the mutated struct back via `SetFieldValue`. Mirror child-19/24's
  box/mutate/unbox, but the "array element" is replaced by "CLR-object field."
  OPEN (diagnose-first): the exact meaning of the byref `off` half for a
  `ldflda <CLR-struct-field-of-CLR-object>` (field hash vs byte offset) must be
  confirmed before the apply -- inspect the `ldflda` JIT stamp for this shape
  (likely the same field-hash plumbing as Area-4d, since the struct is a CLR
  field on a CLR object). Scope sketch ~20-30 lines once the encoding is pinned,
  single handler, Neo-gated. Capability `neo-value-types`. Probe MUST FAULT (NIE
  on HEAD) + assert `obj.Struct.value` round-trips through both the direct set and
  the `+=` forms. NOTE the pre-existing float `addi`/`conv.i4` bugs (child-15/16
  gotchas) if the probe uses a float struct field -- use an int field (as
  `TestStruct.value` is) to keep the probe clean.

---

## L1 -- `neo-legacy-ilruntime-type-getenumvalues-dispatch` -- REAL but LOW VALUE (defer)

- Re-audit CONFIRMS the framed Legacy gap exists: ran the child-18 probes under
  plain `Debug` + `useRegister=true` with filter `NeoStepIlEnumGetValues` =>
  **Ran 4 tests, 1 failed**: TC4 fails with `DivideByZero` at
  `NeoStepIlEnumGetValuesTest.cs:111` (the `values.GetValue(0) != 0` guard). TC1/
  TC2/TC3 PASS under Legacy. (TC4 is the negative-member enum `{N=-1,Z=0,P=1}`
  expecting unsigned-binary-value sort `[Z,P,N]`.)
- The `ILRuntimeType` overrides DO exist and are CORRECT for unsigned sort
  (`ILRuntimeType.cs:637-710`: `IsEnum`, `GetEnumUnderlyingType`,
  `GetEnumValues` via `GetSortedEnumMembers` with `EnumValueAsUnsignedBits`
  reinterp-sort). They are NOT Neo-gated -- shared reflection code. Under NEO the
  override IS effectively reached (TC4 PASS, in the 368 baseline). Under LEGACY
  the same call produces declaration/signed order -> TC4 FAIL.
- The Legacy `System_Enum_Binding.GetValues_0` (`:88-102`) and the Neo
  `GetValues_0_Neo` (`:72-86`) BOTH call the framework `System.Enum.GetValues
  (@enumType)`; Legacy recovers `@enumType` via `StackObject.ToObject(...) ->
  CheckCLRTypes`, Neo via `ReadNeoReference`. The dispatch divergence is in what
  Type object / framework path Legacy produces vs Neo -- NOT a missing override
  (the override is present and correct).
- VERDICT: **REAL-but-LOW-VALUE -- defer (no child now)**. Rationale: (a) Neo is
  already correct; this is Legacy PARITY only; (b) the fix is a Legacy
  typeof-dispatch investigation ("why does Legacy's ldtoken/GetTypeFromHandle
  path for an IL enum not effectively dispatch to `ILRuntimeType.GetEnumValues`
  the way Neo does"), NOT a one-line quick fix -- the override is already there;
  (c) the Neo-overhaul portfolio scope is the Neo surface (CLAUDE.md: Legacy is
  the stable back-compat path with its own 519/1 baseline); (d) the affected
  surface is narrow (enum GetValues/GetNames order for enums with NEGATIVE
  members -- TC1/TC2/TC3 already pass under Legacy). If Legacy parity becomes a
  goal, the investigation entry point is: compare the `System.Type` object Legacy
  vs Neo produce for `typeof(ILenum)` and trace why the framework
  `System.Enum.GetValues` diverges (likely the Legacy `@enumType` is not an
  `ILRuntimeType` at the call, or net8.0 `Enum.GetValues` takes a
  non-virtual-dispatch fast path for the Legacy-produced type). No engine change
  recommended in this portfolio.

---

## Summary table

| cand | verdict | site (file:line) | distinct tests | next action |
|------|---------|------------------|----------------|-------------|
| R1-Shape-A | blocked behind Step 19 (delegates) | ILIntepreter.Neo.cs:3989 (via :6334) | DelegateExtTest02 | no child; resolves with delegate dispatch |
| R1-Shape-B | REAL + TRACTABLE | ILIntepreter.Neo.cs:5704 (ldobj arm) | UnitTest_10047 | Array branch in ldobj VT arm (~10-15 lines) |
| R2 | REAL + TRACTABLE (HIGH) | ILIntepreter.Neo.cs:515 (NeoMarshalByrefFieldToSlot) | CLRBindingTest01 | Array branch, covers forward+write-back (~15-20) |
| R3 | REAL + TRACTABLE (medium) | ILIntepreter.Neo.cs:4116 (raw Stfld VT-owner) | UnitTest_Struct2 | CLR-object-field owner branch (~20-30, pin ldflda encoding first) |
| L1 | REAL-but-LOW-VALUE (defer) | ILRuntimeType.cs:648 (shared; Legacy dispatch divergence) | NeoStepIlEnumGetValues_TC4 under Legacy | no child now; Legacy typeof-dispatch investigation if parity wanted |

## Notes for the lead
- R1-Shape-B, R2, and R3 are ALL "access a CLR value-type that lives somewhere
  other than a frame local" -- three opcode sites (ldobj / byref-param / raw-Stfld),
  two primitives (Array.GetValue/SetValue for R1-B+R2; reflection
  GetFieldValue/SetFieldValue box/mutate/unbox for R3). They COULD be batched as
  one "Neo CLR-array/byref element materialization" child (R1-B + R2 share the
  Array primitive and the ldelema `(arrIdx, elementIdx)` encoding) with R3 as a
  sibling, OR shipped as 2-3 small children. R2 is the highest value (`ref arr[i]`
  is pervasive); recommend R2 first.
- All three REAL sites have direct green sibling precedents (child-19 Stfld-array,
  child-24 Ldfld-array, child-15 ldind/stind) -- low-risk, mirror-and-marshal.
- No engine source was modified. No TestCases probes were added (all evidence
  came from EXISTING test methods: DelegateExtTest02, UnitTest_10047,
  CLRBindingTest01, UnitTest_Struct2, NeoStepIlEnumGetValues_TC4). Nothing to
  remove from TestCases/.
- Re-confirmed NeoStep baseline **368/0** on HEAD after the re-audit.
