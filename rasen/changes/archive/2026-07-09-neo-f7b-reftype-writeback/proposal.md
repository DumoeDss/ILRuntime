# Proposal - neo-f7b-reftype-writeback (F-7B TRUE COMPLETION)

## Why

F-7 shipped the same-frame delegate-Invoke fast path for primitive `ref`/`out`
params (`ref int` / `out int` / multicast write-back is observable and green).
The reference-type half was recorded as a follow-up: when the callee
REASSIGNS the referent of a `ref <reference-type>` param (`s = s + "!"`), the
caller's variable ends up holding a DANGLING mStack index -- the new object
lands in the CALLEE's frame ref region, which the nested `ExecuteNeo` pops on
return, so the index the byref write-back stored no longer resolves. The
caller then crashes with `Index was out of range` in a later CLR binding.

This is the TRUE-COMPLETION of the F-7 byref work for reference-typed
referents. Scope-aware: isolate the mStack-lifetime root cause first, then
decide SHIP vs SEQUENCE.

## What Changes

- Isolate the dangling-index mechanism (reproduced 1/1 on HEAD `12d9e809` via
  the binding probe `NeoStep19_ByRef_StringWriteBack`): the relativized
  frame-native byref makes the callee's `Stind_Ref` write the new object's
  CALLEE-frame mStack index into the CALLER's frame cell; the callee's `Ret`
  pop (`ILIntepreter.Neo.cs:2719`) then deletes that index.
- Promote the written-back object into a CALLER-owned mStack slot before the
  callee pop, reusing the existing single-reference RETURN promotion pattern
  (`ILIntepreter.Neo.cs:2689-2694`). Recommended site D1a: in
  `NeoRunDelegateTargetOnThis`, convert a reference-typed frame-native byref
  to an mStack-slot byref so the callee's `Stind_Ref`/`Ldind_Ref` read+write
  the object through a caller-owned slot that survives the pop.
- Gate the promotion on the referent being a reference type; leave the F-7
  primitive-byref relativization path byte-identical (regression guard).
- Keep the `NeoStep19_ByRef_StringWriteBack` probe (caller asserts
  `r == 4 && s == "abc!"`) as the binding regression test, plus
  multicast-with-ref-string and temp-source-byref adversarial probes.

## Impact

- Affected files: `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`
  (`NeoRunDelegateTargetOnThis` + the `Stind_Ref`/`Ldind_Ref` mStack-object
  arms); `TestCases/NeoStep19Test.cs` (the probe, already added).
- Neo-only (`#if ENABLE_NEO_MODE`); Legacy-neutral (Legacy's mStack is not
  frame-truncated, so it has no dangling-index class -- this brings Neo to
  parity within Neo's per-frame model, no model change).
- Regression risk: the F-7 primitive-byref delegate cases and the
  `ref string` marshal+READ path MUST stay green; `NeoStep` smoke MUST stay
  233/0/0.
- Sequenced (NOT shipped here): the direct `Call_IL`/`CopyNeoCallThisBack`
  sibling path has the SAME mechanism but is unreachable today (the inliner
  folds small direct targets); a non-inlinable direct-call probe + the
  analogous `CopyNeoCallThisBack` promotion is a follow-up.
