# Review Report -- neo-step17-completion (adversarial, NON-AUTHOR reviewer)

**Reviewer:** independent (review-loop). **Verdict: CHANGES-REQUESTED.**

One **Blocker** (a native segfault, reachable from extremely common C#, and a
REGRESSION over HEAD's clean NIE) was uncovered by adversarial probing. The
green smoke (NeoStep 123/123, NeoOptHard 16/16) does NOT exercise the failing
path -- exactly the "green smoke MISSED it" failure mode the portfolio's Step-17
B1 / OPT-HARDEN K1 lessons warn against. The implementer's self-report
(replaced here) claimed APPROVE; that claim is not supported.

Scope reviewed: `{(a) constrained.-on-VT, (d) CLR primitive-array ldelema,
(M2) F-5 boxed-source NIE-guard}`. Working tree UNCOMMITTED throughout.

---

## MANDATORY PROBE 1 -- Constrained-arm dispatch correctness (HIGHEST PRIORITY)

The reviewer wrote 6 independent discriminator probes (`RevStep17_*`,
`TestCases/RevStep17Probe.cs`, temporary, removed after the review) and ran each
in isolation. The JIT ordering claim (D1 = Option F') was re-verified
independently:

- `JITCompiler.cs:1954-1963`: when `hasConstrained`, `op.Operand4 = 1` is
  stamped on the callvirt, the earlier `Constrained` op is removed
  (`lst.RemoveAt(constrainIdx)`) and `lst.Add(old)` re-appends it. The callvirt
  `op` itself is `lst.Add`-ed only at `JITCompiler.cs:2410` (end of `Translate`).
  So the runtime order IS `[Push..., Constrained T, Callvirt M]` -- Constrained
  runs FIRST. **D1 = F' is correct; the design-doc premise ("JIT moves
  Constrained AFTER") was indeed wrong, and the implementer's disproof is
  verified.** `ip += 2` correctly skips both opcodes (no double-dispatch, no
  skipped opcode).

Discriminator-path table (each probed independently):

| Path | Probe | Outcome |
|---|---|---|
| (a) IL-VT direct-call (interface method resolves to ILMethod) | `RevStep17_a_IlVtDirectCall` | PASS |
| (b) CLR-VT box-once (TestVector3NoBinding.ToString override) | `RevStep17_b_ClrVtBoxOnce` | PASS |
| (c) interface-method on IL struct (direct-call, no box) | `RevStep17_c_AlreadyBoxed` | PASS |
| (d) override resolves constrained TYPE (CLR struct ToString) | `RevStep17_d_OverrideResolves` | PASS |
| (e) value-type return (Int32.CompareTo -> int) | `RevStep17_e_ValueTypeReturn` | PASS |
| **(f) IL struct calling an INHERITED Object/ValueType method (no override)** | `RevStep17_f_IlVtGetHashCode`, `RevStep17_SingleConstrained_ReadBack` | **SEGFAULT / NRE -- BLOCKER** |

**Root cause of (f).** For `someIlStruct.ToString()` / `.GetHashCode()` /
`.Equals()` where the IL struct does NOT override the method,
`constrainedType.GetVirtualMethod(targetMethod)` returns the inherited
**CLRMethod** (`Object.ToString` / `ValueType.GetHashCode`), NOT an ILMethod.
The discriminator at `ILIntepreter.Neo.cs:3271-3273` gates the safe direct-call
path on `actualMethod is ILMethod`; an inherited CLRMethod falls through to the
**box-once branch** (`:3299+`). There, `constrainedType.TypeForCLR` for an IL
value type is `ILTypeInstance` (a CLASS, not a value type), so
`ReadNeoValueType(clrT = ILTypeInstance, frameBase + thisByteOff, ...)` reads
the IL struct's flat bytes AS an `ILTypeInstance` -- producing a corrupt boxed
receiver. Dispatching `Object.ToString` / `GetHashCode` on it then either
**segfaults** (exit 139, `ToString`) or **NREs** (`GetHashCode`).

**This is a REGRESSION.** Stash-toggle proof:
- With this change applied: `RevStep17_SingleConstrained_ReadBack` (an IL struct
  calling `ToString()` via a generic constrained caller) -> **native segfault,
  exit 139**.
- With the runtime change stashed (HEAD `d3ef84c4`, the Step-17 NIE): the SAME
  probe throws the tagged `NotImplementedException` ("Step 17:
  constrained.callvirt on a value type is deferred ...") -> **clean failure, no
  crash** (1 test, 1 failed-by-exception).

So HEAD degrades gracefully (tagged NIE); this change turns a common case
(`anyIlStruct.ToString()`, the default `object.ToString()` for any IL struct
without an override -- the MAJORITY of IL structs) into a native crash. The
implementer's own IL-VT probe (`NeoStep17_ConstrainedIlVtDirectCall`) uses an
**interface** method (resolves to ILMethod -> direct-call -> works) and so
dodges this path; the smoke is green precisely because no probe exercises an
IL-struct inherited-method call. See Finding F1.

The remaining discriminator paths (a)-(e) are correct. The `ip += 2` skip is
sound. The ret-info read (`cv->Register1 >= 0 ? frameBase + cv->DstOffset` ...)
matches the alias semantics used by the other callvirt arms (Register1/DstOffset
alias @offset 4; `-1` = no return).

---

## MANDATORY PROBE 2 -- Stind/Ldind `is Array` branch blast radius

The shared `Stind_I4` / `Ldind_I4` arms gained an `else if (mStack[objIdx] is
Array cArr)` branch (`:2827`, `:2902`) that routes to `cArr.SetValue(v, off)` /
`(int)cArr.GetValue(off)` where `off` is the element INDEX (per D4 resolution).
Each existing path was probed independently:

| Path | Probe | Outcome |
|---|---|---|
| (a) frame-native byref (`objectIndex == -1`) | `RevStep17_StindLdindFrameNative` | PASS -- byte-identical (falls to `*(int*)(frameBase + off) = v`) |
| (b) IL-instance-field byref (`ILTypeInstance`) | `RevStep17_StindLdindIlField` | PASS -- `is Array` false -> `GetNeoILInstance` path |
| (c) new CLR-array-element path | `RevStep17_ClrPrimitiveArrayLdelema_StindLdind` (implementer) + `RevStep17_ArrayLdelemaBounds` | PASS |
| (d) byref to a CLR OBJECT field (not array) | (covered by existing NeoStep smoke; the `is Array` branch is false for a non-array mStack obj) | PASS (smoke 123/123) |

Bounds + element-size: `RevStep17_ArrayLdelemaBounds` (OOB index 5 on a
length-2 array) throws `IndexOutOfRangeException` CLEANLY (caught; in-bounds
elements untouched). No silent corruption. The CLR `Array.SetValue/GetValue`
performs the bounds + element-size handling natively.

**Caveat (Minor, NOT a blocker):** only `Stind_I4` and `Ldind_I4` gained the
`is Array` branch. The other stind/ldind variants (`Stind_I1/I2/I8/R4/R8`,
`Ldind_I1/U1/I2/U2/U4/I8/R4/R8`, `Stind_I` which falls through to I4) do NOT
have it. For a CLR array element of those widths, the consumer arm will hit
`GetNeoILInstance(mStack, objIdx)` on a CLR `Array` -> `InvalidCastException` /
NRE (NOT silent corruption). This matches the design's explicit scoping
("green target = Stind_I4/Ldind_I4 on a CLR primitive array; other variants
remain NIE-tagged"). The Ldelema arm validates `elemClrType.IsValueType` before
encoding, so a ref-element array is NIE-tagged at production. Acceptable per
the scoped design -- but the consumer-side NIE is implicit (a cast failure),
not a tagged NIE message. See Finding F4.

---

## MANDATORY PROBE 3 -- addrAlias COEXIST gate (Step-17-B1 silent-corruption class)

The implementer's `NeoStep17_AddrAliasRegisterReuseRegression` (TC15) passes.
The reviewer wrote a DIFFERENT variant (`RevStep17_AddrAliasTwoConstrained`:
two IL structs with folded field writes, two constrained callvirts whose boxed
temps reuse the register region, then a byref dest-reuse window, then re-read
the folded fields). **This probe segfaulted -- but the segfault is F1 (the
inherited-Object-method box-once bug), NOT an addrAlias gate failure.** The
addrAlias COEXIST gate itself is NOT perturbed by this change: the Constrained
arm touches NO optimizer/lowering code (D1 = F' = no JIT change), the
addrAlias/liveAliasMap machinery is byte-identical, and the non-constrained
callvirt paths are unchanged (the Constrained arm only fires for
`OpCodeREnum.Constrained`). The Step-17-B1 register-reuse class is therefore
NOT regressed by this change (the existing TC15 + the full NeoOptHard 16/16
confirm). The reviewer's probe could not independently confirm the gate because
F1 crashed it first; once F1 is fixed, this probe should be re-run.

**addrAlias COEXIST gate verdict: not perturbed by this change (no optimizer
edits); the F1 crash blocks independent confirmation.**

---

## MANDATORY PROBE 4 -- M2 NIE-guard reachability

The boxed-source branch of `CopyNeoCallArguments` (`ILIntepreter.Neo.cs:258-270`,
`PrimitiveByRefSrc` with `objIdx >= 0`) was replaced with a tagged NIE-guard.
**Confirmed genuinely unreachable:**
- `PrimitiveByRefSrc` is set ONLY in the call-lowering
  (`Optimizer.Neo.cs:1229`, `primByRef.Add(dstIsVtThisSlot)`), and
  `dstIsVtThisSlot` (`:1217-1222`) is true ONLY for a VT instance `this` direct
  `call` (area4 path), where the source is a frame-native byref (`objIdx == -1`,
  NOT `>= 0`).
- The Constrained arm does NOT route slot-0 through `CopyNeoCallArguments` --
  it manually skips slot 0 (`for (int i = 1; ...)`) and writes the boxed
  receiver's mStack index directly into `cmap.PrimitiveDst[0]`.
- `CopyNeoCallArguments` is invoked from the Call/Newobj arms
  (`:1661,1719,1792,...`), none of which carry a boxed-`this` source through
  `PrimitiveByRefSrc`.

So the NIE-guard is correct defensive coding for a currently-unreachable
future-callers case; it will NOT fire spuriously. **M2 obligation closure
verified.**

---

## MANDATORY PROBE 5 -- The `ldflda`-on-in-frame-VT gap (pre-existing?)

The implementer claims `ldflda`-on-in-frame-VT is PRE-EXISTING (not introduced
by this change) and that the constrained dispatch is independently validated
by the interface direct-call probe (which uses `Ldfld_I4_Inline`, not
`ldflda`). Code-grounded verification: the `Ldflda` arm reads
`*(frameBase + operandSlotOff)` as an mStack objIdx; an in-frame VT slot holds
flat bytes -> garbage. There is no `Ldflda_Inline`. This defect is independent
of the Constrained arm (it lives in the Ldflda opcode handler, untouched by
this change) and would reproduce on HEAD for any IL-struct method taking a
field address. **Pre-existing confirmed** (the change neither introduces nor
fixes it). The interface direct-call probe (`NeoStep17_ConstrainedIlVtDirectCall`,
`FetchId()` reads `id` via `Ldfld_I4_Inline`) DOES validate the constrained
direct-call dispatch without depending on `ldflda`. **Constrained dispatch
itself is validated** for the ILMethod-direct-call shape. Route: new follow-up
`neo-vt-ldflda-inline` (correctly scoped out).

---

## MANDATORY PROBE 6 -- Legacy-neutral + smoke reproduction

- **NeoStep smoke: 123/123 green** (reproduced by the reviewer, with the
  reviewer's probe file isolated in a separate class so the `NeoStep` filter
  does not match it).
- **NeoOptHardening: 16/16 green** (reproduced).
- **Plain `Debug` CLI builds clean** (0 errors) -- all runtime edits are Neo-only
  (the changed file `ILIntepreter.Neo.cs` is `#if ENABLE_NEO_MODE`-gated).
  Legacy-neutral confirmed at the build level.

The 7 pre-existing Legacy NeoStep failures are unrelated (documented in the
implementer's report; not investigated -- out of scope).

---

## MANDATORY PROBE 7 -- Probe validity (the 6 `NeoStep17_*` probes)

Each of the 6 implementer probes exercises its path end-to-end (not trivially
passing):
- `NeoStep17_ConstrainedIlVtDirectCall` -- IL struct + interface method ->
  ILMethod direct-call (validates the direct-call shape). GOOD.
- `NeoStep17_ConstrainedClrPrimitiveToString` -- `int.ToString()` box-once on a
  CLR primitive. GOOD.
- `NeoStep17_ConstrainedClrStructToString` -- CLR struct override box-once. GOOD.
- `NeoStep17_ConstrainedOverrideResolvesConstrainedType` -- asserts the
  constrained TYPE's override fires (not Object.ToString). GOOD.
- `NeoStep17_ClrPrimitiveArrayLdelema_StindLdind` -- `int[]` ldelema round-trip.
  GOOD.
- `NeoStep17_AddrAliasRegisterReuseRegression` -- Step-17-B1 register-reuse
  reconstruction. GOOD.

**COVERAGE GAP (the F1 blind spot):** NONE of the 6 probes exercises an
**IL struct calling an inherited Object/ValueType method** (ToString/
GetHashCode/Equals/GetType) via constrained. The implementer's IL struct
(`NeoStep17NamedStruct`) DOES override ToString, but the probe was REPLACED
with the interface direct-call probe because of the `ldflda` gap (the override
body does `id.ToString()` -> `ldflda`). The replacement inadvertently dropped
the only probe that would have exercised the IL-VT + inherited-CLRMethod path
-- leaving the F1 segfault uncovered. See Finding F1 (fix includes adding a
keeper probe).

---

## Findings

### F1 -- BLOCKER. Constrained dispatch segfaults on an IL value type calling an inherited Object/ValueType method (REGRESSION over HEAD's clean NIE).

- **File:line:** `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs:3271-3273` (the discriminator) + `:3307-3320` (the box-once branch's IL-struct mishandling).
- **Probe:** `RevStep17_SingleConstrained_ReadBack` (IL struct `RevPair { int a; int b; }`, no ToString override; `v.ToString()` via a generic constrained caller) -> segfault exit 139. `RevStep17_f_IlVtGetHashCode` (IL struct `.GetHashCode()`) -> NRE. With the runtime change stashed (HEAD), both throw the tagged Step-17 NIE cleanly.
- **Root cause:** for an IL value type with NO override, `constrainedType.GetVirtualMethod(targetMethod)` returns the inherited CLRMethod (`Object.ToString`/`ValueType.GetHashCode`). The discriminator gates the safe direct-call path on `actualMethod is ILMethod`, so this falls to the box-once branch, which calls `ReadNeoValueType(constrainedType.TypeForCLR == ILTypeInstance, ...)` -- reading the IL struct's flat bytes as an `ILTypeInstance` (a class) -> corrupt boxed receiver -> crash/NRE on dispatch.
- **Impact:** a native crash reachable from extremely common C# (any IL struct's default `ToString()`/`GetHashCode()` through a generic or interface constrained call site). REGRESSION: HEAD gracefully NIE'd. The green smoke missed it because no probe covers "IL struct + inherited Object method".
- **Fix:** extend the discriminator to handle `constrainedType is ILType && IsValueType && actualMethod is CLRMethod` (inherited Object/ValueType method) -- box the IL struct into an `ILTypeInstance` (the IL box representation, via the existing Step-13 Box-arm / `CopyFrameToIL` machinery -- NOT `ReadNeoValueType` on `ILTypeInstance`) and dispatch the CLRMethod on the boxed ILTypeInstance's `CLRInstance`/`Object` view. OR, if that plumbing is non-trivial, NIE-guard this sub-case explicitly (a tagged NIE is acceptable and recovers HEAD's graceful-degradation behavior -- no regression). Add a keeper probe (`NeoStep17_ConstrainedIlVtInheritedObjectMethod`) that calls `ToString()`/`GetHashCode()` on a no-override IL struct via a generic constrained caller.

### F2 -- Major. The box-once branch's `constrainedType.IsValueType && clrT != null` sub-branch is unsound for an IL value type (the F1 mechanism).

- **File:line:** `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs:3307-3320`.
- **Probe:** same as F1 (the box-once branch is where F1 crashes).
- **Root cause:** for an IL value type, `clrT = constrainedType.TypeForCLR = ILTypeInstance` (a reference type). `ReadNeoValueType(ILTypeInstance, ...)` interprets the struct's flat bytes as an `ILTypeInstance` shape -- undefined. The branch was written for a CLR value type (where `clrT` is the CLR struct's `Type`); it does not correctly handle an IL value type whose `TypeForCLR` is `ILTypeInstance`.
- **Impact:** the box-once branch is only correct for CLR value types (the implementer's tested path). For an IL value type it corrupts memory.
- **Fix:** gate the `ReadNeoValueType` sub-branch on `!(constrainedType is ILType)` (i.e. a genuine CLR value type), and handle the IL-value-type box separately (box into ILTypeInstance) or NIE it. This is the same fix surface as F1.

### F3 -- Minor. `slot0RefCount` computation reads the call-site method's declaring type, but the box-once writes a boxed receiver for a DIFFERENT resolved method -- verify the ref-skip is correct for the override-resolves-constrained-type case.

- **File:line:** `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs:3219-3227`.
- **Probe:** not isolated (F1 blocked deeper probing). The implementer's override-resolves probe (`NeoStep17_ConstrainedOverrideResolvesConstrainedType`, a CLR struct) passes, so for a CLR-VT override the slot0RefCount=1 skip is empirically correct.
- **Concern:** `slot0RefCount` is computed from `targetMethod.DeclearingType` (the call-site method, e.g. `Object`), but `actualMethod` may be the constrained TYPE's override (a different declaring type with a different ref count). For the common case (Object/ValueType `this` -> RefCount=1) this is correct, but if a struct override somehow had a different callee `this` ref count than the call-site declaring type, the skip could be wrong. Low risk for the green target; flag for a dump-confirm during the F1 fix.
- **Fix:** dump-confirm `slot0RefCount` against `actualMethod.DeclearingType` (the resolved override) rather than `targetMethod.DeclearingType`, OR document why the call-site declaring type is the correct source.

### F4 -- Minor. Stind/Ldind CLR-array consumer path: only I4 is handled; other widths fail with an untagged cast/NRE (not a tagged NIE).

- **File:line:** `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs:2821-2830` (Stind_I4), `:2897-2905` (Ldind_I4). The other stind/ldind arms (`Stind_I1/I2/I8/R4/R8`, `Ldind_I1/U1/I2/U2/U4/I8/R4/R8`) lack the `is Array` branch.
- **Probe:** not isolated (documented boundary). `RevStep17_ArrayLdelemaBounds` (I4 path) throws cleanly.
- **Impact:** a `long[]`/`float[]`/etc. element via the matching `ldind` variant would hit `GetNeoILInstance(mStack, objIdx)` on a CLR `Array` -> an untagged `InvalidCastException`/NRE (not silent corruption; the Ldelema arm's `IsValueType` guard prevents a ref-element array from reaching this). Matches the design's explicit scoping but the consumer-side failure is untagged.
- **Fix (optional):** add a tagged NIE in the unhandled stind/ldind arms when `mStack[objIdx] is Array` (so the failure names the unhandled variant), OR document the scope in a comment. Not a blocker (the design explicitly NIE-defers these).

### F5 -- Trivial. Reviewer's addrAlias two-constrained probe could not independently confirm the gate because F1 crashed it.

- **Probe:** `RevStep17_AddrAliasTwoConstrained` -> segfault (F1 mechanism, not an addrAlias failure).
- **Impact:** the addrAlias COEXIST gate is NOT perturbed by this change (no optimizer/lowering edit -- D1 = F'), so the Step-17-B1 silent-corruption class is not regressed; but independent confirmation is blocked until F1 is fixed.
- **Fix:** after F1, re-run a two-simultaneous-constrained-callvirts reuse probe and confirm the folded field values survive.

---

## Smoke results (reproduced by the reviewer)

- **NeoStep: 123/123 green** (0 failed).
- **NeoOptHardening: 16/16 green** (0 failed).
- **Plain `Debug` CLI: builds clean** (0 errors -- Legacy-neutral at the build level).
- **F1 regression proof:** with the change applied -> segfault (exit 139); with the runtime change stashed (HEAD) -> clean tagged NIE (1 test, 1 failed-by-exception).

## Verdict

**CHANGES-REQUESTED.** The cohort's tested paths (IL-VT interface direct-call,
CLR-VT box-once, CLR-primitive box-once, override resolution, CLR-array
ldelema->stind/ldind, addrAlias register-reuse) are correct and green, and the
M2 NIE-guard is genuinely unreachable. BUT the discriminator misses the
**IL-value-type + inherited-Object/ValueType-method** sub-case (F1/F2), which
segfaults and is a regression over HEAD's clean NIE -- reachable from extremely
common C# (`anyIlStruct.ToString()`). The green smoke missed it because the
implementer's IL-struct-ToString probe was replaced (due to the unrelated
`ldflda` gap) and no keeper probe covers the inherited-method path.

Required before ship:
1. **F1/F2:** handle (or tagged-NIE) the IL-value-type + inherited-CLRMethod
   box-once sub-case. Even a tagged NIE recovers HEAD's graceful degradation
   and removes the regression (acceptable for this cohort's scope).
2. **F1:** add a keeper probe (`NeoStep17_ConstrainedIlVtInheritedObjectMethod`)
   so the path stays guarded.
3. Re-run F5's two-constrained reuse probe after F1.

The other findings (F3, F4) are Minor/Trivial and can land as comments or
follow-ups.

---

## Re-review round 1

**Reviewer:** independent (review-loop round 1, NON-AUTHOR, != the F1 fixer).
**Verdict: APPROVE.** The F1 Blocker is correctly fixed; no new Blocker/Major
uncovered. The IL-VT box mechanism is correct (CopyFrameToIL width, the
inherited-CLRMethod dispatch, and the int/bool/string return-value semantics
are all independently verified). The recurrence guard prevents F1 and excludes
no legitimate case. The (a) IL-VT-direct-call, (b) CLR-VT box-once, and (c)
already-boxed paths are independently re-confirmed byte-identical. F5 (addrAlias
COEXIST) is independently reconstructed and green. Smoke reproduced: NeoStep
130/130 + NeoOptHard 16/16.

Scope reviewed: ONLY the round-1 F1 fix delta (the uncommitted changes since
the implementer's round-0 apply) -- i.e. the new `else if (constrainedType is
ILType ilBoxType && ilBoxType.IsValueType)` sub-branch in the Constrained arm's
box-once `else`, the `&& !(constrainedType is ILType)` recurrence guard on the
generic CLR-VT branch, the IL-VT-with-ref-fields NIE-defer, the K1-K7 keeper
probes, and the smoke. Working tree UNCOMMITTED throughout.

### 1. The IL-VT box mechanism -- CORRECT

The fix's box mechanism is, end to end:

1. `ilBoxType.Instantiate(false)` -> a fresh `ILTypeInstance` (or
   `ILEnumTypeInstance` for an IL enum, which IS-A ILTypeInstance -- the branch
   correctly handles IL enums too, since `IsValueType` is true for enums and
   `TotalPrimitiveSize`=4, `TotalReferenceCount`=0).
2. `CopyFrameToIL(frameBase, thisByteOff, 0, ilBoxType.TotalPrimitiveSize, 0,
   mStack, frameRefBase, ilBox)` copies the struct's flat primitive bytes into
   the box's `Primitives` (verified `CopyFrameToIL` at
   `ILIntepreter.Neo.cs:3702-3719`: it does `Unsafe.CopyBlock(ref dstP, ref
   *(frameBase + primOffset), primSize)` -- exactly `TotalPrimitiveSize` bytes
   from the byref's offset half into `dst.Primitives`). The width IS correct --
   it copies the WHOLE flat primitive region (the struct's complete primitive
   footprint), not a field-by-field subset. The `refOffset`/`refCount` are 0
   because the IL-VT-with-ref-fields sub-case is NIE-deferred just above (a
   correct deferral: the byref source does not carry the struct's ref-region
   mStack base, mirroring the direct-call path's deferral to
   `neo-step17-stobj-refloop`).
3. `ilBox.Boxed = true` marks the IL box representation (matches Step 13's Box
   arm shape).
4. The boxed `ILTypeInstance` is parked on `mStack` and its index written into
   the callee slot-0 ref slot (`*(int*)(targetBase + cmap.PrimitiveDst[0]) =
   boxedIdx`) -- byte-identical to the CLR-VT box-once parking.
5. The inherited CLRMethod is dispatched via `InvokeNeoClrMethod` on the boxed
   ILTypeInstance.

The dispatch correctness is grounded: for `Object.ToString` / `GetHashCode` /
`Equals` the resolved `actualMethod` is the inherited CLRMethod whose
`DeclearingType` is `Object`/`ValueType` (a CLRType, NOT a value type). In
`CLRMethod.Invoke(byte* targetBase, ...)` (`CLRMethod.cs:365-404`) the `this`
is therefore read as a REFERENCE-type `this` (`int thisIdx = *(int*)(targetBase
+ curPrim); instance = mStack[thisIdx]`) -- which fetches the parked boxed
ILTypeInstance. Reflection then invokes the host override:
- `Object.ToString` -> `ILTypeInstance.ToString()` (`ILTypeInstance.cs:874-889`)
  returns `type.FullName` when no IL `ToStringMethod` exists -- a non-null,
  non-empty string. CORRECT (K1, K4 assert non-null/non-empty).
- `GetHashCode` -> in Neo mode (`#if !ENABLE_NEO_MODE`-gated, falls through to
  `base.GetHashCode()`) returns a stable int. The int return-value is written by
  `InvokeNeoClrMethod`'s `retType.TypeForCLR == typeof(int)` branch
  (`ILIntepreter.Neo.cs:374`) -> `*(int*)retDstPtr = (int)res`. CORRECT (K2 +
  my R1 probe read the int back cleanly).
- `Equals` -> Neo-mode `base.Equals(obj)` reference equality on the box ->
  bool. CORRECT (K3 + my R3 probe).
- `string` return (ToString) -> the `else` branch (`:404-412`) stores `res`
  into `mStack[targetRetRefBase]` and writes the index to `retDstPtr`. CORRECT.

Independent probe (DIFFERENT variant from K1-K4): an IL struct with a
NON-TRIVIAL mixed-width primitive region (`int a; long c; int b;` -- 16 bytes
with padding) calling the inherited `GetHashCode` / `ToString` / `Equals`
(`Rev17r1_NestedVtGetHashCode`, `Rev17r1_NestedVtToString`,
`Rev17r1_InheritedEqualsIsBoxRef`). All three PASS -- confirming CopyFrameToIL's
width copy is correct for a non-trivially-sized flat primitive region. (NOTE: a
struct with a NESTED VALUE-TYPE field would be the stronger probe, but storing
into a nested-VT field lowers to `Stfld_Value`, which is a pre-existing Step-6
NIE documented in the round-0 review's PROBE 5 as the ldflda/Stfld_Value gap;
the constructor NIEs before the constrained path is reached. The mixed-width
PRIMITIVE struct exercises CopyFrameToIL's width without tripping that
unrelated gap.)

### 2. The recurrence guard -- CORRECT (prevents F1, excludes no legitimate case)

The generic CLR-VT branch gained `&& !(constrainedType is ILType)`
(`ILIntepreter.Neo.cs`, the box-once `else if (constrainedType.IsValueType &&
clrT != null && !(constrainedType is ILType))`). Verified:
- PREVENTS F1: an IL value type now CANNOT reach `ReadNeoValueType(clrT =
  ILTypeInstance, ...)` -- the new IL-VT sub-branch (which fires BEFORE the
  CLR-VT branch) is the SOLE handler for an IL value type in the box-once arm.
  Stash-toggle proof re-confirmed: with the runtime change stashed (HEAD), the
  F1 case `NeoStep17_ConstrainedIlVtInheritedToString` throws the HEAD Step-17
  tagged NIE cleanly (1 test, 1 failed-by-exception, NO crash); with the fix
  applied, the same case PASSES (no crash). So the fix transitions the path
  HEAD-clean-NIE -> apply-round-0-crash -> round-1-WORKS, exactly the desired
  outcome.
- EXCLUDES NO LEGITIMATE CASE: a genuine CLR value type is NOT an `ILType`, so
  the guard's `!(constrainedType is ILType)` is true for it and the CLR-VT
  branch still fires (K6 + my R5 probe confirm). There is no
  CLR-VT-via-ILType path that should hit the CLR-VT branch -- an ILType's
  `TypeForCLR` is `ILTypeInstance` (a class), which is precisely the unsound
  case the guard blocks. The guard is sound.
- The IL-VT sub-branch catches ALL IL value types: IL structs
  (`IsValueType==true`), IL enums (`IsValueType==true` for Cecil enum defs;
  `ILEnumTypeInstance : ILTypeInstance` so `Instantiate(false)` + the box
  mechanism works), and IL-VT-with-ref-fields (which hit the NIE-defer INSIDE
  the new branch, NOT the guard -- confirmed: the ref-fields NIE is checked
  before the Instantiate). No IL value type slips past to the guard.

### 3. Working paths -- independently re-confirmed byte-identical

- (a) IL-VT-direct-call (override) path: the discriminator's `actualMethod is
  ILMethod && thisObjIdx < 0 && constrainedType is ILType && IsValueType` gate
  is byte-identical (the new branch is in the `else` box-once arm, AFTER this
  gate). Independent probe `Rev17r1_IlVtDirectCallReadsField` (an IL struct
  override that READS a field and computes `seed*2+1`, dispatched via a generic
  constrained caller) -> returns 21. PASS. Plus K5 (`Named:42`) PASS.
- (b) CLR-VT box-once: the generic CLR-VT branch is byte-identical (only the
  guard was added; the body is unchanged). Independent probe
  `Rev17r1_ClrVtBoxOnceGetHashCode` (a CLR struct `GetHashCode` via constrained
  -- a DIFFERENT flavor from K6's ToString; exercises the int ret-slot) -> PASS.
  Plus K6 PASS.
- (c) Already-boxed / ref-type `this` (`thisObjIdx >= 0`): the
  `boxedReceiver = mStack[thisObjIdx]` branch is byte-identical (it is the FIRST
  `if` inside the box-once `else`, before the new IL-VT sub-branch). Unchanged.

### 4. F5 (addrAlias COEXIST gate) -- independently reconstructed, GREEN

Independent reconstruction `Rev17r1_AddrAliasTwoConstrainedWideReuse`: two
SIMULTANEOUS constrained inherited-method calls (the F1 shape) with WIDER
primitive regions (16 bytes each -- `int a; long c; int b`) than K7's 8-byte
pairs, whose boxed temps reuse the register region, an intervening byref
dest-reuse window (`Rev17r1Bump(ref x, 77)`), then re-read the folded fields.
All folded fields (`wp.a==1000, wp.b==2000, wp.c==3000`), the byref dest
(`x==77`), and both boxed-receiver strings survive. PASS. The Constrained arm
touches NO optimizer/lowering code (D1 = F' = no JIT change), so the addrAlias
/ liveAliasMap machinery is byte-identical; the gate is NOT perturbed by the F1
fix. (The round-0 gap -- F5 blocked by F1's crash -- is now resolvable and
resolved.) Plus K7 PASS.

### 5. Smoke reproduced

- NeoStep: **130/130 green** (123 baseline + 7 keepers K1-K7), exit 0.
- NeoOptHardening: **16/16 green**, exit 0.
- Plain `Debug` CLI builds clean (0 errors) -- Legacy-neutral confirmed (the
  changed file `ILIntepreter.Neo.cs` is `#if ENABLE_NEO_MODE`-gated).

### 6. Probe validity (K1-K7)

Each of K1-K7 exercises its path end-to-end:
- K1 (`NeoStep17_ConstrainedIlVtInheritedToString`) IS the F1 segfault case: an
  IL struct with NO ToString override, dispatched via a generic constrained
  caller (`<T> string S<T>(T v) where T:struct { return v.ToString(); }`). Pre-fix
  (round-0 apply) this segfaulted (exit 139); with the F1 fix it returns a
  non-null string. The keeper genuinely would have crashed pre-fix -- VERIFIED
  via the stash-toggle (HEAD runtime -> clean NIE; round-0 apply -> crash per
  the round-0 report; round-1 fix -> PASS).
- K2 (GetHashCode NRE case), K3 (Equals), K4 (interpolation) -- each a distinct
  inherited-method variant. GOOD.
- K5 (IL-VT override still works), K6 (CLR-VT box-once still works) -- the
  not-perturbed guards. GOOD.
- K7 (F5 reconstruction) -- two-simultaneous-constrained addrAlias reuse. GOOD.

### New findings

NONE at Blocker/Major. The F1 fix is correct, minimal-surface (one new sub-
branch + one guard + one NIE-defer), reuses Step 13's Box-arm machinery
correctly (NOT `ReadNeoValueType` on `ILTypeInstance` -- the unsound reader),
and the blast-radius tightening (the `!(constrainedType is ILType)` guard)
prevents recurrence without excluding any legitimate case. F3 (slot0RefCount
source) and F4 (Stind/Ldind CLR-array only-I4) remain Minor/Trivial and are
acceptable as documented boundaries (the F1 dump confirmed the slot-0 ref-skip
is sound for the box-once shape; F4 is the design's explicit scope).

### Note on the working-tree state during review

The reviewer's temporary adversarial probes (`Rev17r1_*`, 6 probes) were added
to `TestCases/NeoStep17Test.cs` during the review, run in isolation (all 6
PASS), and REMOVED before finalizing -- the keeper set is restored to exactly
the fixer's 130 (123 baseline + K1-K7), confirmed by NeoStep 130/130 + a
`Rev17r1_` filter returning 0 tests. The TestCases diff is byte-identical to
the fixer's (306 insertions). No fixer artifact was altered.

### Verdict

**APPROVE.** Ship the round-1 F1 fix. The Blocker is closed; the keeper probes
guard the path; the working paths and the addrAlias gate are independently
re-confirmed green; smoke is reproduced. F3/F4 remain as documented Minor/
Trivial (no action required for this cohort).
