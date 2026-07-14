# Design: neo-recluster-60 -- Neo unbox.any reference-type T

## Root cause (pinned, Neo-vs-Legacy)
Roslyn lowers a cast on a generic type parameter T to `unbox.any T` whenever T
is not class-constrained (interface-constrained or unconstrained -- T may be a
value type, so the emitter must use the form valid for both). Common shapes:
- `(T)result` in `T CreateInterface<T>() where T : IFoo` (GenericMethodTest9).
- `res = p as T;` in a generic `TryGetObject<T>(out T res)` (RefOutNull2).
- generic `return (T)x;` (GenericMethodTest15, InheritanceTest06/18).

For a reference-type T, `unbox.any` is semantically `castclass`: a reference
copy; a null source passes through as null. Legacy implements exactly this:
`ILIntepreter.Register.cs:4147-4150` -- the `else` (non-primitive, non-valuetype)
branch is `AssignToRegister(ref info, ip->Register1, obj)`, and `obj == null` is
"Nothing to do with null" (no throw).

The Neo arm (`ILIntepreter.Neo.cs:5441-5548` Unbox/Unbox_Any) did NOT:
1. It read `srcIdx` and threw `NullReferenceException` at :5447 when `srcIdx < 0`
   BEFORE consulting the type -- so a null source for a ref-type T threw instead
   of yielding null.
2. With `ilType != null` (IL class/interface), it only had enum / primitive /
   valuetype sub-arms and fell to `throw new InvalidCastException()` at :5484.

## The fix (Neo-only, ~22 lines, single arm)
Add a reference-type branch at the TOP of the Unbox/Unbox_Any arm, right after
`t = AppDomain.GetType(ip->Operand)`:

```
if (t != null && !t.IsValueType)
{
    srcIdx = *(int*)(frameBase + ip->SrcOffset);
    obj = srcIdx >= 0 ? mStack[srcIdx] : null;
    if (obj != null) {
        dstIdx = frameRefBase + dstRefOffset;
        mStack[dstIdx] = obj;
        *(int*)(frameBase + ip->DstOffset) = dstIdx;
    } else
        *(int*)(frameBase + ip->DstOffset) = -1;
    break;
}
```

- Discriminator `!t.IsValueType` EXACTLY mirrors Legacy's `t.IsValueType` branch
  boundary. So every value-type / enum / primitive path (IL enum at :5457, IL
  primitive :5466, IL valuetype :5475, CLR primitive :5495, CLR VT :5528) is
  UNTOUCHED -- the new branch returns first only for a reference-type T.
- Reference-write convention mirrors the sibling Isinst/Castclass arms (:5569-
  5576 / :5571-5573): object to `mStack[frameRefBase + dstRefOffset]`, index to
  `*(int*)(frameBase + ip->DstOffset)`, `-1` sentinel for null. `dstRefOffset`
  (`ip->Operand3`) is the SAME dest ref offset the value-type path uses
  (CopyILToFrame at :5478).
- Null source (`srcIdx < 0` or `mStack[srcIdx] == null`) writes `-1` (null) --
  mirrors Legacy's "Nothing to do with null".

## Why this is sound / regression-free
- The branch fires ONLY for reference-type T. For value-type T the existing
  logic (and its NRE-on-null, which is CORRECT for a value-type unbox) runs
  unchanged.
- `t == null` falls through to the existing logic (which throws InvalidCastException
  at :5492 for a null CLRType); not in the failing set, left as-is.
- The `unbox.any` reference-type result is consumed exactly like a castclass
  result (a reference slot) -- every downstream consumer (field stores, calls,
  null-compare branches) already handles reference slots.
- No JIT / optimizer / object-model change. Purely an ExecuteNeo arm addition.

## Verification (truth = full-smoke number)
- Stash-toggle: stashing ONLY `ILIntepreter.Neo.cs` -> 3/3 probes FAULT (TC1/TC3
  InvalidCastException @ :5484, TC2 NullReferenceException @ :5447) -> pop ->
  3/3 PASS. Airtight.
- Name-filter: GenericMethodTest9/15/StaticMethodTest19, InheritanceTest06/18
  PASS after fix (were the exact cluster-B exceptions on HEAD).
- NeoStep smoke: 397/0 (no regression; +3 new probes).
- FULL SMOKE: 60 -> 55 (-5, 0 new failures, 0 regressions). Exit 0 (graceful).
- Legacy-neutral: all 6 cluster-B tests PASS on plain Debug+useRegister=true
  (Neo-specific regressions confirmed); the change is inside file-gated
  `ILIntepreter.Neo.cs` (ENABLE_NEO_MODE).

## Honest residual
`RefOutTest.UnitTest_RefOutNull2` PROGRESSED past the unbox.any (line 90) and now
fails at `stobj T` (Neo.cs:6434) -- a SEPARATE pre-existing Neo bug (cluster G's
stobj family, alongside UnitTest_GenericsRefOut/GenericsRefOut2 at :6407/:6422).
It PASSes on Legacy. Candidate follow-up: `neo-stobj-generic-byref`.
