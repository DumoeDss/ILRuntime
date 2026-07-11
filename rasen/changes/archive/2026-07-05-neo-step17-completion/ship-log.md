# Ship Log -- neo-step17-completion

**Date:** 2026-07-05
**Verdict:** SHIP (APPROVED round 1; 0 open Blocker/Major; F3/F4 accepted-known Minors)
**Working tree:** UNCOMMITTED (LEAD commits after archive)

## Scope shipped this cohort

- **(a) `constrained.`-on-value-type full dispatch (D-CONSTRAINED).**
- **(d) CLR primitive-array `ldelema` (D-LDELEMA remainder).**
- **(M2) `F-5 / NEO-CALLARG-BOXED-SRC` closure (the area4 boxed-source obligation).**

**Out of scope (separate follow-up child `neo-step17-stobj-refloop`, portfolio
task #18):**

- **(b)** `Stobj`/`Ldobj` ref-slot loop (VT-with-ref-fields copy through
  stobj/ldobj; primitive-field VTs are fully supported this step).
- **(c)** generic-byref (`ref T`/`out T` with `T` a generic parameter), `fixed`
  unmanaged-pinning blocks, and interface-on-VT-constrained beyond the common
  shape -- remain Step-17-tagged NIEs.
- **IL-VT-with-ref-fields constrained** sub-case NIE-defers to the same
  follow-up (the byref source does not carry the struct's ref-region mStack
  base).

## (a) Constrained dispatch -- Option F' (NOT Option F)

The design's Option F fusion premise was DISPROVEN by the apply-phase JIT body
dump. The dump showed the final JIT order for `struct v; v.ToString()` is
`[Push..., Constrained T, Callvirt M]` -- **Constrained runs BEFORE the
callvirt** (the `lst.Add(old)` for Constrained happens during the Callvirt case
at `JITCompiler.cs:1963`, but the callvirt `op` is only `lst.Add`-ed at the end
of `Translate` at `:2412`, so Constrained ends up before it).

So the runtime `Constrained` arm OWNS the dispatch:

1. It carries the type token (`ip->Operand`).
2. It reads the trailing callvirt at `ip+1` for the method token
   (`cv->Operand2`), the param map (`cv->Operand`), and the return-slot info
   (`cv->Register1` / `cv->DstOffset` / `cv->Operand3`).
3. It dispatches and skips the callvirt (`ip += 2`).

**NO JIT change, NO operand stamping** -> the existing `Operand4` flag
semantics (`0x1`/`0x2`/`0x4`/slot/`thisArgOffset<<16`) are byte-identical for
non-constrained callvirts (the Constrained arm only fires for
`OpCodeREnum.Constrained`). Risk 1 (operand-non-collision) dissolved.

### Discriminator (IL-vs-CLR, refined by the round-1 F1 fix)

A constrained discriminator MUST enumerate these shapes:

1. **IL value type + ILMethod override -> direct-call (no box).** Deref the
   byref, copy flat primitive bytes into the callee slot-0 frame region
   (`targetBase + ParamInfos[0].Offset`), ExecuteNeo. The override reads
   `this.field` via in-frame `Ldfld_Inline`. REUSES area4's flat-bytes-`this`
   shape. (A Neo IL-struct method body CANNOT consume a boxed `ILTypeInstance`
   `this` -- its JIT uses in-frame layout.)
2. **IL value type + inherited CLRMethod -> box into ILTypeInstance.** Added by
   the F1 review-fix (see below).
3. **CLR value type (primitive or struct) -> box-once.** `NeoBoxReturnValue`
   (primitive) / `ReadNeoValueType` (struct); park on mStack; write the index to
   the callee slot-0 ref slot; dispatch via `GetVirtualMethod`-resolved CLR
   override.
4. **Already-boxed / ref-type `this` (`objIdx >= 0`) -> no-op box.** Reachable
   only via an interface-typed local holding a boxed struct; falls out for free.

The override resolves via `constrainedType.GetVirtualMethod(targetMethod)` --
the constrained TYPE's concrete override (not the static call-site method's).

## (d) CLR primitive-array ldelema (D-LDELEMA)

The `Ldelema` arm's `else` branch (a non-IL-VT array) now encodes
`(arrIdx, elementIdx)` for a CLR value-type-element array, where `off` (the
offset half) IS the element index (NOT a byte offset). A reference-type-element
array NIEs at production (the arm validates `elemClrType.IsValueType`).

Consumer side: `Stind_I4` / `Ldind_I4` gained a `mStack[objIdx] is Array`
branch (`cArr.SetValue(v, off)` / `(int)cArr.GetValue(off)`). The existing
`objectIndex >= 0` arm calls `GetNeoILInstance`, which assumes an
`ILTypeInstance` -- a CLR `Array` is NOT an ILTypeInstance, so the new branch
is required. Scoped to the green target (`int[]` ldelema -> stind/ldind of int);
other stind/ldind variants remain NIE-tagged in their existing arms.

## (M2) F-5 / NEO-CALLARG-BOXED-SRC closure

The constrained box-once BYPASSES `CopyNeoCallArguments` (the Constrained arm
writes the boxed mStack index directly into the callee slot, manually skipping
slot 0 in the map copy). So the boxed-source branch of `CopyNeoCallArguments`
(`PrimitiveByRefSrc` with `objIdx >= 0`) is NOT reached by the constrained
path -- it is genuinely unreachable today (`PrimitiveByRefSrc` is set ONLY for
a VT instance `this` direct `call`, where the source is a frame-native byref
`objIdx == -1`, NOT `>= 0`).

The defensive `CopyBlock` that was there was WRONG for a boxed source (`offset`
would be an mStack FIELD offset, not a struct address) -> replaced with a
tagged NIE-guard (no silent mis-copy). The map's slot-0 ref entry is
meaningless for the constrained byref source -- the Constrained arm's ref-copy
loop SKIPS the leading `slot0RefCount` ref entries (Object `this` ->
RefCount=1).

The `CopyNeoCallThisBack` comment was tightened: it covers MUTATING INSTANCE
METHODS, NOT constructors -- the newobj path (VT-THIS-ADDR) performs its own
slot-0 -> caller-dest copy-back in `ExecuteNeo`'s Ret arm and does NOT invoke
this. The stale `Constrained` NIE text (T1) was replaced with an accurate
description of the new arm.

## F1 review-fix (round 1)

The non-author review-loop round 0 found a **Blocker REGRESSION** the green
apply-phase smoke (123/123) MISSED: `anyIlStruct.ToString()` / `.GetHashCode()`
/ `.Equals()` (and the `$"{ilStruct}"` interpolation -- the common C# default
`object.ToString` for any IL struct with NO override) reached the box-once
branch with `actualMethod` = the inherited CLRMethod (`Object.ToString` /
`ValueType.GetHashCode`), and the generic `constrainedType.IsValueType && clrT
!= null` sub-branch called `ReadNeoValueType(clrT = ILTypeInstance, ...)` --
reading the struct's FLAT BYTES as an `ILTypeInstance` (a class) -> corrupt
boxed receiver -> native segfault (exit 139, ToString) / NRE (GetHashCode).
Stash-toggle proved it a REGRESSION over HEAD's clean Step-17 tagged NIE.

**Fix (CORRECT-FIX, not tagged-NIE):** the dump showed the correct fix was
clean + low-risk, so the PREFERRED path was taken -- the IL-VT + inherited-
CLRMethod box-once sub-case now WORKS (high-value: `anyIlStruct.ToString()`
returns a useful non-null string instead of crashing).

- A NEW Constrained-arm box-once sub-branch `else if (constrainedType is ILType
  ilBoxType && ilBoxType.IsValueType)` was inserted BEFORE the generic CLR-VT
  branch.
- **Box mechanism** (reuses Step 13's Box-arm machinery, NOT `ReadNeoValueType`):
  - If the IL VT has reference fields, NIE-defer (-> `neo-step17-stobj-refloop`;
    the byref source does not carry the struct's ref-region mStack base).
  - Otherwise `ilBoxType.Instantiate(false)` -> a fresh `ILTypeInstance`, then
    `CopyFrameToIL(frameBase, thisByteOff, ..., ilBoxType.TotalPrimitiveSize,
    0, ..., ilBox)` copies the struct's flat primitive bytes into the box's
    `Primitives` array. `ilBox.Boxed = true`.
  - The boxed `ILTypeInstance` is parked on `mStack` and its index written into
    the callee slot-0 ref slot, exactly like the CLR-VT box-once path.
  - The inherited CLRMethod is dispatched via `InvokeNeoClrMethod` on the boxed
    ILTypeInstance.
- **Blast-radius tightening:** the generic CLR-VT branch
  (`constrainedType.IsValueType && clrT != null`) gained a
  `&& !(constrainedType is ILType)` guard. Without it, an IL value type would
  still fall through and recur the F1 segfault. The guard makes the new IL-VT
  branch the SOLE handler for an IL value type in the box-once arm (and a
  genuine CLR value type is NOT an `ILType`, so it still hits the CLR-VT
  branch -- excludes no legitimate case).

The (a) IL-VT-direct-call (override), (b) CLR-VT box-once, (c) already-boxed
paths are byte-identical (only a new branch was inserted + one guard added;
none of their inputs changed).

Round-1 re-review verdict: **APPROVE** (0 new Blocker/Major). The F1 Blocker is
correctly fixed; the IL-VT box mechanism (CopyFrameToIL width, inherited-
CLRMethod dispatch, int/bool/string return-value semantics) is independently
verified. F5 (addrAlias COEXIST) is independently reconstructed and green.

## Verification

- **NeoStep: 130/130 green** (117 baseline + 6 implementer probes + 7 F1
  keeper probes K1-K7), exit 0.
- **NeoOptHardening: 16/16 green**, exit 0.
- **Legacy-neutral:** plain `Debug` CLI builds clean (0 errors -- all runtime
  edits Neo-only / in `#if ENABLE_NEO_MODE`-gated files; Legacy's Constrained
  arm is the REFERENCE, NOT modified). The constrained probes pass on Legacy.
  The 7 pre-existing Legacy NeoStep failures (NeoTestClrStruct..., NeoStep14
  TC1/TC5/TC8, NeoStep15 TC6, NeoNaNR8) reproduce identically with the change
  applied and stashed -- NOT caused by this change.

### Adversarial probes

- 6 implementer probes (`NeoStep17_ConstrainedIlVtDirectCall`,
  `_ConstrainedClrPrimitiveToString`, `_ConstrainedClrStructToString`,
  `_ConstrainedOverrideResolvesConstrainedType`,
  `_ClrPrimitiveArrayLdelema_StindLdind`, `_AddrAliasRegisterReuseRegression`).
- 7 F1 keeper probes (K1 `NeoStep17_ConstrainedIlVtInheritedToString`,
  K2 `_InheritedGetHashCode`, K3 `_InheritedEquals`,
  K4 `_InheritedInterpolate`, K5 `_OverrideStillWorks`,
  K6 `_ClrVtBoxOnceStillWorks`, K7 `_AddrAliasTwoConstrainedReuse`).
- Stash-toggle proofs: the constrained probes threw the HEAD Constrained NIE
  during development; the CLR-array probe threw the HEAD Ldelema NIE; K1
  segfaulted under the round-0 apply. With all fixes applied, every probe
  passes.
- Review adversarial probes included the Step-17-B1 register-reuse
  reconstruction (F5, green) + a box-mechanism width probe (a non-trivial
  mixed-width primitive region `int a; long c; int b;` -- 16 bytes with
  padding -- confirming `CopyFrameToIL`'s width copy is correct), each run in
  isolation then removed before finalizing (keeper set = exactly 130).

## Accepted-known findings (recorded, not dropped)

- **F3 (slot0RefCount source, Minor):** `slot0RefCount` is computed from
  `targetMethod.DeclearingType` (the call-site method, e.g. `Object`,
  RefCount=1), but `actualMethod` may be the constrained TYPE's override. For
  the green target the call-site declaring type is the correct source -- the
  box-once writes exactly one boxed receiver ref into slot 0, and the skip of
  the leading `slot0RefCount` ref entries correctly drops the (meaningless)
  source byref ref bytes. The F1 dump confirmed the slot-0 ref-skip is sound
  for the box-once shape (K6 + the implementer's override-resolves probe
  empirically correct). Left as a comment-level note.
- **F4 (Stind/Ldind CLR-array only-I4, Minor):** LEFT AS-IS. Matches the
  design's explicit scoping ("green target = Stind_I4/Ldind_I4 on a CLR
  primitive array; other variants remain NIE-tagged"). The Ldelema arm's
  `IsValueType` guard prevents a ref-element array from reaching the consumer;
  the untagged cast failure for an unhandled width is the documented edge, not
  silent corruption. Route any future widening to `neo-step17-stobj-refloop`
  / `array-completion`.

## Closes

- **D-CONSTRAINED** (for the {a, d, M2} scope; (b)/(c) -> follow-up
  `neo-step17-stobj-refloop`).
- **D-LDELEMA** (fully -- the CLR primitive-array remainder is now done; the
  IL VT array path shipped in Step 17).
- **F-3 / NEO-BYREF-THIS callvirt caveat** (resolved -- `constrained.callvirt`
  on a CLR struct now dispatches; the direct-`call` shape was closed in
  `neo-step13-area4`).
- **the area4 M2 obligation** (the boxed-source NIE-guard).

## New follow-ups surfaced

- **`neo-vt-ldflda-inline`** (`ldflda`-on-in-frame-VT, pre-existing -- surfaced
  here, portfolio task #19 created). The Ldflda arm reads the operand slot as
  an mStack objIdx; an in-frame VT slot holds flat bytes -> garbage. There is
  no `Ldflda_Inline`. Any IL-struct method taking a field address
  (`field.ToString()`, `ref field`, `fixed`) is broken on Neo REGARDLESS of
  constrained. The constrained DISPATCH is validated by the interface direct-
  call probe (which uses `Ldfld_I4_Inline`, not `ldflda`).
- **IL-VT-with-ref-fields constrained** -> folds into `neo-step17-stobj-refloop`
  (the byref source does not carry the struct's ref-region mStack base).

## Files touched (all Neo-only; Legacy is the REFERENCE, NOT modified)

- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`:
  - `case OpCodeREnum.Constrained:` -- replaced the NIE with the dispatch (reads
    `ip->Operand` + `cv = ip+1`; IL-VT direct-call / IL-VT inherited-CLRMethod
    box / CLR box-once / already-boxed shapes; `ip += 2`). Round-1 F1 fix:
    NEW IL-VT box sub-branch + `!(constrainedType is ILType)` guard on the
    generic CLR-VT branch.
  - `case OpCodeREnum.Ldelema:` `else` branch -- encode `(arrIdx, elementIdx)`
    for a CLR value-type-element array; NIE a ref-element array.
  - `case OpCodeREnum.Stind_I4:` / `case OpCodeREnum.Ldind_I4:` -- added a
    `mStack[objIdx] is Array` branch.
  - `CopyNeoCallArguments` boxed-source branch -- NIE-guard (was defensive
    `CopyBlock`).
  - `CopyNeoCallThisBack` comment -- tightened (mutating INSTANCE METHODS, not
    ctors).
  - Stale `Constrained` NIE text (T1) -- replaced with accurate description.
- `TestCases/NeoStep17Test.cs` -- 6 `NeoStep17_*` implementer probes + 7 F1
  keeper probes K1-K7 + an IL-VT-interface struct + generic constrained callers
  + `NeoStep17PlainStruct` (no-override IL VT).

**Did NOT git commit/push** (per process discipline; LEAD commits after
archive).
