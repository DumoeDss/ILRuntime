# Proposal: neo-delegate-dispatch-arg-marshal (Wave-2 D1 of neo-overhaul)

## Problem
Full Neo smoke = 31 failed. D1 cluster (3 tests, single root): DelegateExtTest01,
DelegateExtTest02, DelegateTest01 fail with `Step 17/13b: ... Owner type: System.Int32`.
A delegate bound to a static EXTENSION method (`obj.IntTest(...)`) does not pass the
bound `obj` as the target's param 0; the delegate's first explicit arg lands in the
extension-`this` slot -> `obj.AddValue` ldfld.i4 sees an Int32 owner -> NIE.

## Root cause (re-audited, diagnosis disproof)
The batch-child handoff pointed at `NeoRunDelegateTargetOnThis` (same-frame delegate
fast path). DISPROVEN: diagnostic prints prove DelegateExtTest01 reaches the IL target
via `NeoInvokeSub` (the separate-interpreter path). `IntDelegate` is a CLR delegate
type -> `callvirt.clr Invoke` -> the real `MethodDelegateAdapter<int>` adapter is NOT
intercepted (Callvirt_CLR interception is Dummy-only per child C13) -> autogen Invoke
binding -> `NeoInvoke` -> `NeoInvokeSub`. `NeoInvokeSub` had no `IsExtend` branch
(Legacy `ILInvokeSub` does: `if (method.IsExtend && instance != null) { PushObject(
instance); paramCnt--; }`).

## Fix
Mirror Legacy's `IsExtend` branch in `NeoInvokeSub` (DelegateAdapter.cs, Neo-gated):
write the bound `instance` to `paramInfos[0]` and copy `paramCnt - 1` explicit args
(ParameterCount INCLUDES the bound-this param for an extension method). +29/-8, single
file. Non-extend paths byte-identical (no regression). `NeoRunDelegateTargetOnThis`'s
same IsExtend blind spot is a LATENT sibling, left untouched (high regression risk,
zero gain for the D1 tests).

## Success criteria (truth = full-smoke number)
- The 3 D1 tests pass (stash-toggle airtight).
- Full smoke 31 -> lower (target -3).
- NeoStep broad green (no delegate regressions).
- Legacy-neutral.

## Verify result
31 -> 28 (strict subset, 0 regressions). NeoStep 401/0. Legacy build 0 errors.
