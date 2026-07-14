# Design: neo-delegate-vt-float-return

## The bug (from a REAL Neo run on HEAD, before the fix)

`DelegateTest24`:
```csharp
List<TestVector3> list = ...; // (1,2,3),(2,3,4),(3,4,5)
var res = list.Sum(v => v.X); // expect 6
if (res != 6) throw new Exception();
```
HEAD result: `res = 4E-45` (= `*(float*)&3`, a float-bit-reinterpret signature),
test throws. The `v => v.X` lambda is bridged by
`FunctionDelegateAdapter2<TestVector3, Single>`; when the HOST's `Enumerable.Sum`
invokes the selector, the CLR->IL delegate callback corrupts the result.

## Surfacing chain (each lower link FIXED by a prior child)
1. struct-newobj -> `list.Add(new TestVector3(...))` (child-28 + struct-newobj fix).
2. `List<TestVector3>.Add` struct-arg marshal (neo-callvirt-clr-struct-arg hand-port).
3. **`list.Sum(v => v.X)` -- THIS child.** The host iterates and calls the IL
   selector per element; the selector returns a corrupted float.

## Root cause (PINNED by a temporary diagnostic in `NeoInvokeSub`)

Under the Neo calling convention a **CLR value-type PARAMETER** is laid out as a
**BOXED REFERENCE**: `JITCompiler.AllocateSlotForType` `else` branch
(`Size=4`, `RefCount=1`, an mStack index of the boxed struct) -- NOT flat managed
bytes. (A CLR value-type LOCAL is the other way: flat bytes via
`AllocateLocalStackSpaces`; the asymmetry is the load-bearing fact.) So
`DelegateAdapter.NeoInvokeSub` correctly boxes the struct param when it builds
the callee frame (WriteNeoCallSlot `RefCount>0 && Size==4` path stores the struct
on mStack and writes the index).

The `v => v.X` lambda lowers to `ldarg <param>; ldfld X; ret` (JIT-confirmed):
```
Final Results for ...<...>b__N_0(TestVector3 v):
    0:ldfld r2, r1, 0x2000001A40C54605   ; raw Ldfld (CLRType declaring field)
    1:ret r2
```
The raw-`Ldfld` runtime arm (`ILIntepreter.Neo.cs`) dispatches on
`ct.TypeForCLR.IsValueType`. For a CLR-struct owner it had three sub-branches --
array-element byref (child-24, `0x1`), CLR-object-field byref (child-29, `0x2`),
and an ELSE that read the owner slot as **FLAT MANAGED BYTES** via
`ReadNeoValueType`. The boxed-ref param owner fell into that ELSE: it read the
4-byte mStack INDEX as the struct's first field -> a bit-reinterpret.

### Diagnostic evidence (HEAD, `NeoInvokeSub`, the `v => v.X` lambda)
```
[DVTFR-DIAG] method=<...>b__3_0 hasThis=True paramCnt=1 retSize=4 retRefCount=0 TotalStructSize=20 retType=System.Single
[DVTFR-DIAG] paramInfo[0] Off=0 Size=4 RefCount=1 RefOff=0 bytes=00 00 00 00   ; slot 0 = `this` (boxed ref)
[DVTFR-DIAG] paramInfo[1] Off=4 Size=4 RefCount=1 RefOff=1 bytes=01 00 00 00   ; slot 1 = TestVector3 param (boxed ref, mStack idx 1)
[DVTFR-DIAG] retDstOff=14 (= TotalStructSize)
[DVTFR-DIAG] post-ExecuteNeo retDst bytes=01 00 00 00 ...                     ; WRONG: the mStack index reinterpreted as the float field
```
After the fix: `post-ExecuteNeo retDst bytes=00 00 A0 40 ...` = `0x40A00000` = `5.0f`. Correct.

The probe `NeoStepDvtfr_TC2_VtParamFloatReturn` (`v => v.X` on `(5,6,7)`, host
returns `BitConverter.SingleToInt32Bits(sel(v))`) returned `bits = 1` on HEAD
(`*(float*)&1`) and `bits = 0x40A00000` after the fix -- airtight.

## Why `Func<int,int>` works (control) -- the VT-param divergence

`NeoStepDvtfr_TC1_ControlPrimPrim` (`Func<int,int>`, `v => v+1`) PASSES on HEAD.
A primitive param is laid out as flat bytes (`AllocateSlotForType` `IsPrimitive`
branch, `RefCount=0`); the lambda body is `ldarg; ldc.i4; add; ret` -- NO `ldfld`,
so the broken raw-Ldfld arm is never reached. The bug is specific to a CLR
value-type PARAMETER whose field is read via `ldfld` (the `ldarg; ldfld` shape).

## The fix (JIT marker + runtime branch with flat-bytes fallback; Neo-gated)

The untyped Neo frame cannot distinguish a flat-bytes local owner from a
boxed-ref param owner at runtime (a flat-bytes struct's first int field can
coincidentally index a real boxed struct in mStack -> a constructible collision,
the SAME reason child-24/29 used a JIT marker rather than a runtime check). So
mark the `ldarg; ldfld` shape at JIT time, then (because the param slot can ALSO
hold flat bytes after an in-method `starg` -- see the regression below)
runtime-distinguish the two representations within the marker branch.

1. **JIT (`JITCompiler.cs`, `case Code.Ldfld`, CLRType branch):** stamp a new
   `NeoRawLdfldBoxedRefOwnerMarker = 0x4` on the raw-Ldfld `Operand4` when
   `ins.Previous.OpCode.Code` is any `ldarg` variant (a static helper
   `IsLdargCode`). Mutually exclusive with the existing `0x1` (Ldelema) and `0x2`
   (Ldflda) markers -- a CIL instruction has exactly one immediate predecessor.
   The field is on a CLR value type (we are in the CLRType branch) and the owner
   is the ldarg'd param, so the param IS a CLR value-type parameter.

2. **Runtime (`ILIntepreter.Neo.cs`, raw-Ldfld `IsValueType` block):** a new
   `else if ((ip->Operand4 & NeoRawLdfldBoxedRefOwnerMarker) != 0)` branch BEFORE
   the flat-bytes ELSE. It reads `objIdx = *(int*)(ownerOff)` and tests whether
   the slot is a boxed reference: `target = mStack[objIdx]` (guarded by
   `0 <= objIdx < mStack.Count`) and `ct.TypeForCLR.IsInstanceOfType(target)`.
   If YES -> `fldVal = f.GetValue(target)` (the boxed-ref / caller-passed case;
   the delegate-lambda trigger). If NO -> fall through to the SAME flat-bytes
   `ReadNeoValueType` + `f.GetValue(boxedOwner)` read the plain ELSE uses (the
   `starg`-overwrite case). The existing dest marshalling (primitive->
   `NeoWritePrimitiveToFrame`, VT->`WriteNeoValueType`, ref->mStack add) handles
   `fldVal` unchanged. This is the value-type-owner analogue of the
   reference-type-owner branch (`else` at the same nesting level) which already
   dereferences a boxed-ref owner via `NeoReadClrObjectField`.

### The regression that forced the flat-bytes fallback (UnitTest_10039)

A first draft of the runtime branch assumed the marker ALWAYS meant a boxed-ref
owner (`mStack[objIdx]` else NRE). The full-smoke diff caught a regression:
`TestValueTypeBinding.UnitTest_10039`, whose body is
```csharp
void UnitTest_10039Sub(TestVector3 arg) {
    arg = TestVector3.One2;   // starg.0 -- reassigns the param slot
    if (arg.X != 1) throw ... // ldarg.0; ldfld X
}
```
The in-method `starg` overwrites the param slot with FLAT MANAGED BYTES (ldsfld
of a CLR-struct static uses `WriteNeoValueType`, the child-3/8 box-roundtrip),
so `ldarg.0; ldfld X` presents a FLAT-BYTES owner (One2's X = 1.0f =
0x3F800000). The first-draft branch read that as `objIdx` (huge -> out of
mStack range -> NRE). The fix is the runtime type-check + flat-bytes fallback:
for UnitTest_10039 the IsInstanceOfType guard fails (the huge int is out of
range) -> flat-bytes read -> X = 1.0f -> PASS. So a CLR value-type parameter
slot can hold EITHER a boxed reference (caller-passed) OR flat bytes (after
starg); the marker narrows to the `ldarg; ldfld` param shape and the runtime
check disambiguates the representation.


## Discriminators / collision-freedom

- `0x4` is a DISJOINT bit from the raw-Ldfld markers `0x1`/`0x2`. The raw-Ldfld
  `Operand4` is written in exactly ONE place (the CLRType else-branch of `case
  Code.Ldfld`), occupied by `{0x1, 0x2, 0x4, 0}`. `LowerNeoOffsets` raw-Ldfld case
  does not touch `Operand4`; the push-deletion remap never decrements a literal
  `0x4`; `TypeSpecializeNeoOpcodes` raw-Ldfld seeding reads only
  `OperandLong`/`Register1` (child-21). So `0x4` survives to runtime (empirically
  confirmed: the branch fires only with the fix; the stash-toggle FAULTS without it).
- The marker is set ONLY when `ins.Previous` is an `ldarg` AND the field's
  declaring type is a CLRType (the `else` of `if (type is ILType)`). An `ldarg`
  loads a PARAMETER; a CLR value-type parameter is ALWAYS a boxed reference
  (`AllocateSlotForType`). So the marker never mislabels a flat-bytes owner.
- A CLR reference-type declaring field (the runtime `else` branch) ignores the
  marker entirely (the `IsValueType` block does not run). An IL-instance owner of
  a CLR-base field (child-9) routes through the reference-type `else` branch too;
  unaffected.

## Why this is NOT a delegate-marshalling redesign

`NeoInvokeSub` is CORRECT: it boxes the CLR value-type param exactly as the
callee's JIT-laid-out frame expects (`paramInfo[1]` = `Size=4/RefCount=1`). The
normal Call path (`CopyNeoCallArguments`) boxes it the same way (it copies the
slot bytes verbatim, including the mStack index). The defect is entirely in the
raw-Ldfld READ arm, which mishandled the boxed-ref owner representation. The fix
is one JIT marker + one runtime `else if` -- the same shape and size as
child-24/29.

## Scope / out of scope

- IN: `ldarg <clrStructParam>; ldfld <field>` (the delegate-lambda field-read
  shape, the DelegateTest24 trigger). Covers primitive / VT / ref field
  categories (the dest marshalling is shared).
- OUT (documented siblings, NOT regressed): `ldarg; ldflda; ldind` (the
  address-taking read of a boxed-ref param field -- a different opcode path,
  `Ldflda` + `Ldind_*`); a CLR value-type param read through a copy to a local
  (`ldarg; stloc; ldloc; ldfld` -- the local is flat bytes, already handled).
  These are follow-ups if a real test hits them.

## Verify

- Stash-toggle (the two engine files): TC1 (control) PASSES either way; TC2
  (`v => v.X` single struct) + TC3 (list.Sum mirror) FAULT (DivideByZero on the
  wrong value) with the fix stashed -> PASS with it restored. Airtight.
- `DelegateTest24`: 4E-45 -> 6. PASS.
- NeoStep smoke: **394/0** (391 baseline + 3 probes; 0 regression). Sibling
  families green (the change only alters behavior for raw Ldfld with
  `Operand4 & 0x4`; every other raw-Ldfld site is byte-identical).
- Full smoke delta: recorded in `tasks.md` (expect 71 -> lower).
- Legacy-neutral: the JIT marker const + stamp + helper and the runtime branch
  are ALL `#if ENABLE_NEO_MODE`-gated (the runtime file is file-gated; the JIT
  sites are inside `#if ENABLE_NEO_MODE` blocks). Legacy compiles none of it.
