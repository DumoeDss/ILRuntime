# Design: neo-delegate-dispatch-arg-marshal (Wave-2 child D1 of neo-overhaul)

## The bug (3 tests, single root)
`DelegateExtTest01`, `DelegateExtTest02`, `DelegateTest01` all failed in the full Neo
smoke with the identical NIE:
`Step 17/13b: field/element access on a CLR object via the IL-instance path is deferred
(CLR field-hash plumbing lands in Step 13b). Owner type: System.Int32`.

Shape: a delegate bound to a static EXTENSION method `obj.IntTest(...)` (IL extension
`static void IntTest(this DelegateExtObj obj, int a)`) does not pass the bound `obj` as
the target's param 0 on invoke. The delegate's first explicit arg (the `int a`) landed
in the extension-`this` slot, so `obj.AddValue(1)` -> `this.Value` (ldfld.i4) ran with
an Int32 owner -> the Step-17/13b NIE. (`DelegateTest01` failed in the full smoke
because the static field `IntDelegateTest` accumulates across tests: DelegateExtTest01
leaves an extension delegate in it, and DelegateTest01 invokes the field, tripping the
same NIE. In isolation DelegateTest01 does not throw -- it only prints `a=0`.)

## Path RE-AUDIT (the batch-child diagnosis was DISPROVEN)
The batch child's handoff said the fix is in `NeoRunDelegateTargetOnThis` (the
same-frame delegate fast path) -- its `headShift` + the D2 mStack rebind. Diagnostic
prints in BOTH `NeoRunDelegateTargetOnThis` (gated on `target.IsExtend`) and
`NeoInvokeSub` (gated on `method.IsExtend`) showed that for DelegateExtTest01 ONLY
`NeoInvokeSub` fires:

`D1DBG NeoInvokeSub m=IntTest hasThis=False isExtend=True inst=True pCnt=2`
(`NeoRunDelegateTargetOnThis` never fired -- no log line.)

WHY: `IntDelegate` (`public delegate void IntDelegate(int a)`) is a CLR delegate type
(declared in the host ILRuntimeTestBase), so `IntDelegateTest(123)` lowers to
`callvirt.clr IntDelegate.Invoke`. The delegate `this` at `mStack[targetBase+0]` is a
REAL adapter (`MethodDelegateAdapter<int>` -- arity 1 IS registered for IntDelegate),
NOT a `DummyDelegateAdapter`. Per child C13 (neo-delegate-adapter-notfound), the
`Callvirt_CLR` delegate interception is Dummy-ONLY (intercepting a real adapter would
corrupt a reused mStack this-slot via the D2 rebind). So a real adapter flows
unintercepted to `ResolveNeoCallvirtCLRTarget` -> `InvokeNeoClrMethod` -> autogen
`Invoke` binding -> the adapter's per-arity `InvokeILMethod` -> under Neo,
`NeoInvoke(new object[]{ p1 })` -> `NeoInvokeSub`. The IL target runs on the
SEPARATE pooled interpreter via `NeoInvokeSub`, NOT the same-frame
`NeoRunDelegateTargetOnThis`.

NOTE: `DelegateExtTest03/04` use `Func<>` open-delegate shapes (`func = Extend;`) with
NO bound instance, so `extendBound` is false for them -- they already worked on HEAD
and are untouched by this fix.

## Root cause (Neo-vs-Legacy, pinned)
`NeoInvokeSub` (DelegateAdapter.cs) only writes the bound `instance` as the target's
slot 0 for an INSTANCE method (`if (hasThis)`). For a static EXTENSION method
(`!hasThis`, `IsExtend`, bound `instance != null`) it did NOT write `instance` at all,
and it iterated `for (int i = 0; i < paramCnt; i++)` writing `args[i]` into
`paramInfos[i]`. For IntTest, `paramCnt = ParameterCount = 2` (`obj` + `a`, IL-level),
but the delegate passes only 1 explicit arg (`a`). So:
- `args[0]` (=123, the `int a`) was written to `paramInfos[0]` = the `obj` slot ->
  `obj` became an Int32 box -> the NIE.
- `args[1]` (none) left `paramInfos[1]` = `a` at 0.

Legacy handles this explicitly in `ILInvokeSub` (DelegateAdapter.cs:1365-1376):
```
if (method.HasThis) PushObject(instance);
int paramCnt = method.ParameterCount;
if (method.IsExtend && instance != null) { PushObject(instance); paramCnt--; }
```
i.e. for a bound extension method it pushes `instance` as param 0 and decrements the
arg copy count. `NeoInvokeSub` was missing the `IsExtend` branch entirely.

## The fix (Neo-gated, single file, +29/-8)
Mirror Legacy's `IsExtend` branch inside `NeoInvokeSub` (DelegateAdapter.cs, inside the
`#if ENABLE_NEO_MODE` block 977-1356):
- `bool extendBound = !hasThis && method.IsExtend && instance != null;`
- Write `instance` to `paramInfos[0]` and start `argIdx = 1` when `hasThis || extendBound`
  (the bound instance consumes the first target slot, exactly like `this`).
- `int argCount = extendBound ? paramCnt - 1 : paramCnt;` (ParameterCount INCLUDES the
  bound-this param for an extension method -- verified `pCnt=2` for IntTest -- so subtract
  one; mirrors Legacy's `paramCnt--`). Loop `for (int i = 0; i < argCount; i++)`.
- Align the child-14 byref-scratch indexing: `byRefScratchOff`/`byRefElemType` are
  indexed by TARGET param slot, so use `brIdx = i + (extendBound ? 1 : 0)` and
  `slotIdx = argIdx` (was `(hasThis ? 1 : 0) + i`, byte-identical for non-extend).

NON-EXTEND BEHAVIOR IS BYTE-IDENTICAL: for `!extendBound`, `argCount == paramCnt`,
`argIdx` starts at `hasThis?1:0` and increments in lockstep with `i`, so `slotIdx ==
argIdx == (hasThis?1:0)+i` and `brIdx == i` -- the original expressions. Instance
method, plain static, and marshalByRef-byref paths are all unchanged.

## Why not also fix NeoRunDelegateTargetOnThis (the batch-child target)
`NeoRunDelegateTargetOnThis` reaches IL delegate targets via (a) `Callvirt_IL`
(an IL-defined delegate type's Invoke) and (b) `Callvirt_CLR` + DummyDelegateAdapter.
Its `headShift = HasThis ? 0 : 4` and D2 rebind (`mStack[thisIdx]=instance`) have the
SAME `IsExtend` blind spot. The 3 D1 tests do NOT exercise it (real CLR-delegate
adapter -> NeoInvokeSub), so it is a LATENT sibling, not the D1 root. Per the mandate
("HIGH-RISK: a NeoRunDelegateTargetOnThis change affects ALL delegate invokes ... Scope
narrowly"), it is LEFT UNTOUCHED (the C13 doc explicitly warns the D2 rebind corrupts
reused slots for real adapters -- touching it is regression-prone and gains nothing for
the D1 tests). Documented as a deferred follow-up: if a future test binds an IL-defined
delegate type to an extension method, `NeoRunDelegateTargetOnThis` would need the same
`IsExtend && instance != null` headShift=0 + D2 treatment.

## Surfaced follow-up (OUT OF SCOPE, pre-existing, masked by the D1 NIE)
After the fix, DelegateExtTest01/02 print `dele a=0` (the explicit `int a` arg reads as
0 inside the target). Frame dump proved the arg IS written correctly
(`frameBase+4 == 123`, `ParamPrimitiveSize=8` so ExecuteNeo's local-zeroing does not
touch it). DelegateTest01's PLAIN-STATIC delegates print `a=0` too -- WITHOUT this fix
touching them. So this is a pre-existing latent bug in the `ldarga.s` + primitive-arg
ToString/read path for delegate-invoked args, NOT caused by this fix and NOT the D1
root. The D1 tests do not assert on the printed `a`, so they pass. Candidate sibling:
`neo-delegate-invoked-arg-read` (investigate ldarga.s / the autogen Invoke binding's
arg read for a delegate-invoked primitive arg).

## Verify (truth = full-smoke number)
- Name-filter: DelegateExtTest01 PASSES after fix (was NIE).
- FULL SMOKE: baseline `31 -> 28` (935 ran; the 28 are a STRICT SUBSET of the 31;
  exactly the 3 D1 tests flipped; ZERO regressions via `comm -13`). EXIT=127 = the
  known graceful pre-existing Dict-NRE crash (summary still emitted).
- NeoStep broad smoke: `401/0` (NO regression -- all delegate tests incl. NeoStep19/20,
  F-7/F-7B byref, C1/C13 real-adapter + Dummy paths green).
- Stash-toggle (airtight): `git stash push DelegateAdapter.cs` -> rebuild ->
  DelegateExtTest01 FAILS (1 failed, the NIE) -> `git stash pop` -> rebuild -> PASSES.
- Legacy-neutral: plain `Debug` build (ENABLE_NEO_MODE off) = 0 errors (the change is
  inside the `#if ENABLE_NEO_MODE` block in DelegateAdapter.cs).

## Files (NOT committed -- LEAD commits)
- `ILRuntime/Runtime/Intepreter/DelegateAdapter.cs` (+29/-8 in NeoInvokeSub, Neo-gated).
