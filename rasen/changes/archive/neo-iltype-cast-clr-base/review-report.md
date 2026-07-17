# Review: neo-iltype-cast-clr-base (Wave-2 child C2)

Reviewer: independent (did NOT write this code). Verified against real code + real runs.
Branch: `features/object-model-overhaul`. Neo = `ExecuteNeo` under `ENABLE_NEO_MODE`.

## Scope reviewed
`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (+154 lines): three new
helpers `ProjectNeoClrCallRefArgs` / `ProjectNeoClrRefSlot` / `NeoClrPrimitiveSlotSize`,
called as the first statement of `InvokeNeoClrMethod` (the single dispatch shared by the
autogen-redirect path and the `CLRMethod.Invoke` reflection fallback).

## Verdict: APPROVE-WITH-FINDINGS

No Blocker, no Major. The fix is correct, minimal, and provably responsible
(stash-toggle airtight). Findings below are all Minor/Trivial (documented deferrals or
deliberate minimal-scope choices) and do not block merge.

---

## 1. Projection no-overfire (CRITICAL dimension) -- PASS

The fix mutates callee-frame mStack slot indices, so overfire is the top risk. Verified
each sub-claim against the code:

(a) Touches ONLY ILTypeInstance reference slots.
  - Primitive params: the `else` arm advances `curPrim += NeoClrPrimitiveSlotSize(t)` and
    does NOT call `ProjectNeoClrRefSlot` (Neo.cs:1174-1178). Confirmed.
  - CLR value-type params (non-prim, non-enum): advance by `GetNeoValueTypeManagedSize`
    and do NOT project (Neo.cs:1151-1155). Confirmed.
  - ILType params: advance by 4 and do NOT project (Neo.cs:1156-1161). Confirmed.
  - CLR reference params (by-value, non-delegate): the ONLY param arm that projects
    (Neo.cs:1162-1173). Confirmed.
  - Inside `ProjectNeoClrRefSlot`: rewrites only when `obj is ILTypeInstance && !(ili is
    ILEnumTypeInstance)` (Neo.cs:1212). ILEnumTypeInstance is correctly excluded (enum
    boxes are not adaptors). Confirmed.

(b) Unconditional `this` projection is safe.
  - A CLR method's declaring type is a CLR type; an ILTypeInstance reaches a CLR call only
    because the IL type inherits/implements the CLR base, so projecting to
    `ili.CLRInstance` (the CrossBindingAdaptor that IS-A the base) is always the correct
    receiver for CLR code. CLR code cannot consume a raw ILTypeInstance directly.
  - The recursion concern is real and correctly diagnosed (see #2).
  - Virtual dispatch on an IL object routes via `ResolveNeoGenericCallvirtTarget` ->
    `ResolveNeoCallvirtILTarget` when `thisObj is ILTypeInstance` (Neo.cs:1442-1443), or is
    JIT-resolved to `Callvirt_IL`/`Callvirt_Interface` -- NOT to the `System_Object_Binding`
    reached through `InvokeNeoClrMethod`. So projecting the Object binding's `this` only
    affects base/explicit `call Object::...` (the StackOverflow case) and pure-CLR `this`
    (no-op: obj is not an ILTypeInstance). Confirmed.

(c) Param `!IsInstanceOfType` guard correctly avoids the NeoStep14 regression.
  - Projects a param ONLY when `targetType.IsInstanceOfType(obj)` is false
    (Neo.cs:1214-1215), i.e. exactly when the autogen binding's direct cast
    `(T)ReadNeoReference(...)` would throw. For an ILTypeInstance-typed / `object`-typed
    param the cast succeeds, so no projection -> the NeoStep14_ILEx_GapB exception-ctor
    case keeps its ILTypeInstance arg. NeoStep 380/0 confirms no regression (see #5).

(d) Caller slots untouched.
  - `targetBase` is the CALLEE param region populated by `CopyNeoCallArguments`
    (Callvirt_CLR at Neo.cs:3778; Call arm via InvokeNeoCallTarget). The caller's view is
    `frameBase`, which is a distinct buffer. The helper rewrites `*(int*)(targetBase +
    slotOff)` only. The projected value goes into a FRESH `mStack.Add(clrInstance)` slot
    (Neo.cs:1219-1221); the caller's original mStack[idx] is never mutated, and the
    caller's frame index is never rewritten. Confirmed.

(e) Byref write-back safety.
  - Byref params have `ptRaw.IsByRef == true`; the reference arm's guard
    `!ptRaw.IsByRef` (Neo.cs:1170) skips projection. So a byref param's dest slot is left
    pointing at the caller's ILTypeInstance, and the post-call write-back
    (`CopyNeoCallThisBack` / `AppendNeoWriteBackCode`) writes the (possibly-mutated) value
    to the correct caller cell. Confirmed -- no write-back-to-fresh-slot corruption.

Projection-no-overfire verdict: PASS. Only ILTypeInstance (non-enum) by-value CLR-ref
slots + the non-VT `this` are rewritten; caller frame untouched; byref/ILType/primitive/
CLR-VT slots skipped; ILEnumTypeInstance excluded.

## 2. The base-call StackOverflow fix -- PASS

Mechanism verified: an IL override `TestCls5.ToString()` whose body is `return
base.ToString()` lowers to `call System.Object::ToString` -> the autogen
`System_Object_Binding.ToString_0_Neo` reads `this` via the direct cast
(MethodBindingGenerator.cs:302) and calls `instance.ToString()` with VIRTUAL dispatch. With
the raw ILTypeInstance as `this`, that re-enters `ILTypeInstance.ToString()` (host), which
`AppDomain.Invoke`s the IL override -> `base.ToString()` -> ... infinite recursion. The
unconditional `this` projection (isThis=true, no assignability guard) makes the binding
receive the adaptor and call `adaptor.ToString()` (the CLR base) instead. This mirrors
Legacy's `CheckCLRTypes`, which projects an ILTypeInstance to `CLRInstance` even for
`typeof(object)` (Extensions.cs:297-313). Confirmed by run: InheritanceTest07 no longer
StackOverflows (fails cleanly on its separate C14 TargetException -- see #5).

## 3. Callee-frame walk correctness -- PASS

`ProjectNeoClrCallRefArgs` must advance `curPrim` with the SAME stride the readers use.
Compared stride-vs-stride against `CLRMethod.Invoke` (CLRMethod.cs:334-542) and the
`ReadNeo*` family (Neo.cs:27-120):

| slot kind                 | reader stride (source)         | helper stride                 | match |
|---------------------------|--------------------------------|-------------------------------|-------|
| newobj retRefBase         | +4 (CLRMethod.cs:364)          | +4 (Neo.cs:1119)              | yes   |
| HasThis CLR-VT            | GetNeoValueTypeManagedSize     | same (Neo.cs:1130)            | yes   |
| HasThis ref               | +4 (CLRMethod.cs:414)          | +4 (Neo.cs:1135)              | yes   |
| param CLR-VT              | GetNeoValueTypeManagedSize     | same (Neo.cs:1154)            | yes   |
| param ILType / CLR ref    | +4 (CLRMethod.cs:525)          | +4 (Neo.cs:1160,1172)         | yes   |
| param bool/sbyte/byte     | ReadNeo* +=1 (Neo.cs:62,70,78) | NeoClrPrimitiveSlotSize=1     | yes   |
| param short/ushort        | +=2 (Neo.cs:46,54)             | =2                            | yes   |
| param int/uint/float/char | +=4 (Neo.cs:30,38,102,118)     | =4                            | yes   |
| param long/ulong/double   | +=8 (Neo.cs:86,94,110)         | =8                            | yes   |
| param enum                | ReadNeoInt32 +=4 (CLRMethod:529)| =4 (fallthrough)             | yes   |
| byref param               | de-byref then element stride   | de-byref then element stride  | yes   |

The autogen bindings read via the SAME `ReadNeo*` helpers
(BindingGeneratorExtensions.cs:223-250, MethodBindingGenerator.cs:298-302) with
`__curPrim` starting at 0 (`this` at slot 0, params j+1 per AppendNeoWriteBackCode comment
at BindingGeneratorExtensions.cs:278), so the autogen path is byte-consistent with this
walk by construction. De-byref mirrors CLRMethod.Invoke:447 exactly. No stride mismatch
found; the correct slot is targeted.

## 4. Full-smoke delta 140 -> 133 -- PASS (spot-verified)

- InheritanceTest01 under Neo: PASS (Ran 1, 0 failed).
- InheritanceTest03 under Neo: PASS (Ran 1, 0 failed).
- TestIs.TestInterface under Neo: PASS (Ran 1, 0 failed).
- Stash-toggle airtight (independently re-run): stash ILIntepreter.Neo.cs -> rebuild ->
  InheritanceTest01 FAILS with the exact targeted error `Unable to cast object of type
  'ILTypeInstance' to type 'ClassInheritanceTest'`; pop -> PASS. The fix is provably
  responsible.
- Legacy-neutral: plain `Debug` CLI build = 0 errors (new code is entirely inside
  `#if ENABLE_NEO_MODE`, file-level guard at Neo.cs:1); InheritanceTest01 PASS under plain
  Debug + useRegister=true.

## 5. "Remaining C2 tests have distinct roots" honesty -- PASS

Spot-confirmed two of the listed remaining failures have DIFFERENT errors (genuinely
progressed past the original cast, not regressed):
- InheritanceTest07: previously StackOverflow; now fails cleanly (1 failed) on its
  separate C14 TargetException -- no crash, no InvalidCastException. Confirms #2.
- InheritanceTest16: fails with `Not supported opcode Muli_R4` (a JIT typed-arithmetic
  gap) -- NOT the C2 cast. It got past the original InvalidCastException.

These are downstream sub-bugs, each its own future child, as the design honestly states.

## 6. Root-cause framing -- ACCURATE (independently confirmed)

The task's castclass/isinst framing was disproven by the implementer. Confirmed:
Neo castclass/isinst keep the raw obj on success (byte-identical to Legacy). The real site
is the autogen binding's DIRECT cast on `this` (MethodBindingGenerator.cs:302) and on
non-delegate CLR-ref params (BindingGeneratorExtensions.cs:250); delegates use
CheckCLRTypes (MethodBindingGenerator.cs:298 / BindingGeneratorExtensions.cs:246). Legacy's
bindings call `typeof(T).CheckCLRTypes(...)` (Extensions.cs:297-313 returns
`ins.CLRInstance`). The engine-level choke point at `InvokeNeoClrMethod` covers both the
autogen-redirect and the reflection-fallback paths with one projection -- correct design.

---

## Findings

### Minor-1: behavioral parity gap for `object`/base-typed params (deliberate, documented)
The param guard `!targetType.IsInstanceOfType(obj)` (Neo.cs:1214) means an `object`-typed
(or any base the raw ILTypeInstance satisfies at the CLR level -- note: an ILTypeInstance
does not satisfy a CLR-base `IsInstanceOfType`, so this mainly concerns `object` and
`ILTypeInstance`-typed params) CLR param receiving an ILTypeInstance is NOT projected.
Legacy's `CheckCLRTypes` WOULD project it to `CLRInstance` unconditionally. This is NOT a
regression (pre-fix Neo also passed the raw ILTypeInstance, and the direct cast
`(object)x` succeeds), and the minimal "project only when the direct cast would throw"
policy is a sound scope choice for this child. But full Legacy parity for `object`-typed
CLR params is not achieved. If a CLR method stores such a param and later Neo code reads it
back via a CLR-base-typed binding, a secondary cast failure could still occur. Not a
blocker for the C2 cluster; worth a note for future parity work.

### Minor-2: constrained.callvirt-to-CLR layout assumption (deferred, safe)
`ProjectNeoClrCallRefArgs` walks from offset 0 assuming the standard (`this`=slot 0)
layout. The constrained.callvirt-to-CLR path writes the boxed receiver to
`cmap.PrimitiveDst[0]` (Neo.cs:6544), which is not guaranteed to be offset 0. The
projection is SAFE here (the `idx < 0 || idx >= mStack.Count` and `obj is ILTypeInstance`
guards make it a read-only no-op if offset 0 does not hold an ILTypeInstance -- no
overfire, no corruption) but does not HELP the constrained case. GenericMethodTest11
remains failing for this reason; the design honestly flags it as a separate deferred
edge case. Acknowledged, not a regression.

### Minor-3: mStack slot reclamation
Each projected slot is a `mStack.Add(clrInstance)` append (Neo.cs:1219-1220) that is never
explicitly reclaimed; the slot becomes unreferenced garbage when the callee frame pops.
This matches the established append pattern already used by the newobj return store
(Neo.cs:1271), the constrained boxed-receiver (Neo.cs:6543), and the IL-VT array element
box (Neo.cs:1537), so whatever reclamation mechanism (or lack thereof) the Neo mStack model
uses applies uniformly -- this change introduces no new class of leak. Flagging only so the
growth characteristic is on the record if mStack is never compacted in long-running
production workloads.

### Trivial
- High comment density (~70% of the +154). Accurate and useful; no action.
- `DeclearingType` typo is a pre-existing CLRMethod field name, not introduced here.

## Spot-test + NeoStep confirmations (all independent re-runs)
- InheritanceTest01 (Neo): PASS. InheritanceTest03 (Neo): PASS. TestIs.TestInterface (Neo):
  PASS.
- NeoStep (Neo): Ran 380, 0 failed -- no regression.
- InheritanceTest01 (Legacy, plain Debug + useRegister=true): PASS.
- Stash-toggle: without fix -> InheritanceTest01 FAILS (InvalidCastException); with fix ->
  PASS. Airtight.
- InheritanceTest07 (Neo): no StackOverflow (fails cleanly on C14 TargetException).
- InheritanceTest16 (Neo): distinct `Muli_R4` error (not the C2 cast).
- Builds: Debug_Neo CLI 0 errors; plain Debug CLI 0 errors (Legacy-neutral).

## Report path
`rasen/changes/neo-iltype-cast-clr-base/review-report.md` (this file).
