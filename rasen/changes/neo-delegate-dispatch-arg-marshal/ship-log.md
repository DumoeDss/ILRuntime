# Ship Log: neo-delegate-dispatch-arg-marshal (Wave-2 D1)

Branch: `features/object-model-overhaul`. Not committed (LEAD commits).

## What shipped
Single-file Neo fix in `ILRuntime/Runtime/Intepreter/DelegateAdapter.cs` (`NeoInvokeSub`,
inside `#if ENABLE_NEO_MODE`): handle a delegate bound to a static EXTENSION method --
write the bound `instance` as the target's param 0 (`extendBound` branch) and subtract
one from the explicit-arg copy count. Mirrors Legacy `ILInvokeSub`'s
`if (method.IsExtend && instance != null) { PushObject(instance); paramCnt--; }`.
+29/-8. Non-extend paths byte-identical.

## Verification (real run)
- Build: CLI `Debug_Neo --no-incremental -p:UseSharedCompilation=false` = 0 errors.
  TestCases `Debug` = 0 errors. Plain `Debug` CLI (Legacy) = 0 errors.
- FULL SMOKE: **31 -> 28** (935 ran / 28 failed / 20 ignored / 7 todos; EXIT=127 = known
  graceful Dict-NRE crash, summary emitted). The 28 are a STRICT SUBSET of the 31
  (`comm -13` = empty); exactly DelegateExtTest01, DelegateExtTest02, DelegateTest01
  flipped green; ZERO regressions.
- NeoStep broad smoke: **401/0** (no regression; NeoStep19/20 delegate + F-7/F-7B byref
  + C1/C13 adapter paths all green).
- Stash-toggle: `git stash push DelegateAdapter.cs` -> DelegateExtTest01 FAILS (NIE) ->
  `stash pop` -> PASSES.

## Diagnosis disproof (load-bearing for the next worker)
The batch-child handoff said the fix was in `NeoRunDelegateTargetOnThis` (headShift + D2
rebind). DISPROVEN by diagnostic: DelegateExtTest01 reaches the IL target via
`NeoInvokeSub` (separate-interpreter path, real CLR-delegate adapter -> autogen Invoke
binding -> NeoInvoke), NOT `NeoRunDelegateTargetOnThis`. `NeoRunDelegateTargetOnThis`'s
same `IsExtend` blind spot is a LATENT sibling (left untouched -- high regression risk,
zero gain for the D1 tests).

## Surfaced follow-ups (out of scope)
1. `NeoRunDelegateTargetOnThis` IsExtend headShift/D2 gap (latent; only matters for an
   IL-defined delegate type bound to an extension method -- no current test).
2. `neo-delegate-invoked-arg-read`: delegate-invoked primitive arg reads as 0 inside the
   target (`ldarga.s` + ToString path). Pre-existing, masked by the D1 NIE, affects
   DelegateTest01 plain-static delegates too. The D1 tests do not assert on it so they
   pass. Distinct root.

## Durable lesson
A delegate bound to a static extension method has `HasThis=false, IsExtend=true`,
ParameterCount INCLUDES the bound-this param, and the delegate's explicit arg count is
ParameterCount-1. Any delegate-dispatch path (NeoInvokeSub AND
NeoRunDelegateTargetOnThis) must mirror Legacy's `IsExtend && instance != null` push-
instance-as-param0 + paramCnt-- for the bound `obj` to land in the extension-`this` slot.
