# ship-log -- implement-neo-step13b

Change: `implement-neo-step13b`
One-line: Neo Step 13b, Area 5 core -- unify CLR value-type call ABI to flat
bytes (by-value param + return), remove the caller-temp-slot fallback, close K2.

## Verdict: SHIP

No Blocker. One pre-existing Major (F-MAJ-1) filed as accepted-known follow-up
(stash-proven not caused by 13b; 13b merely made it reachable where a NIE was
before). One Minor + 3 Info from review, all non-blocking.

## Verification evidence

- CLI build (`ILRuntimeTestCLI.csproj -c Debug_Neo`): 0 errors.
- TestCases build (`TestCases.csproj -c Debug`): 0 errors.
- FULL NeoStep smoke: 84 ran / 0 failed. +3 new NeoStep13b cases, 0 regression
  vs prior NeoStep baseline. CLR-binding canary green.
- Byte-consistency: single size source -- `GetNeoValueTypeManagedSize` is the
  one source of truth for CLR-struct flat-byte size; callee layout
  (`AllocateNeoCallParamSlot`), reflection reader, and autogen reader all
  advance by that size. ReadNeoValueType / WriteNeoValueType use cached
  DynamicMethod + Unsafe.ReadUnaligned/WriteUnaligned; verified byte-consistent
  across layout path / reflection path / autogen path. Ref-field structs are
  NIE-guarded (require a ValueTypeBinder).

## Review summary

Review-loop clean: 0 rounds, no fix needed. review-report.md verdict SHIP.
- 0 Blocker.
- F-MAJ-1 (Major, PRE-EXISTING): AllocateLocalStackSpaces slot-reuse / liveness
  bug for 2+ simultaneous CLR struct locals -> silent wrong result.
  Stash-proven: struct-specific, not introduced by 13b (13b unmasked a NIE the
  caller-temp-slot fallback had hidden). Target: next optimizer-hardening pass
  on AllocateLocalStackSpaces.
- 1 Minor, 3 Info: non-blocking, recorded.

## Delivered scope (Area 5 core, K2 closed)

- Removed caller-temp-slot fallback for CLR struct params; callee layout is now
  the single param path via AllocateNeoCallParamSlot.
- Added GetNeoValueTypeManagedSize (single source of truth for CLR-struct
  flat-byte size). This unmasked a GetPrimitiveSize NIE the fallback had hidden.
- Added ReadNeoValueType / WriteNeoValueType (cached DynamicMethod +
  Unsafe.ReadUnaligned / WriteUnaligned); byte-consistent across layout,
  reflection, and autogen paths; ref-field structs NIE-guarded.
- Filled CLRMethod.Invoke + reflection-return + autogen
  AppendArgumentCodeNeo / GetReturnValueCodeNeo NIEs.

Legacy CLR binding and Legacy codegen are UNTOUCHED (Neo-only `*Neo` variants).

## Deferred (accepted-known, recorded for follow-up)

- K2-FAM boxed-ref bridge (D3, Phase 3 safety valve): the Box/Initobj-source
  half of "CLR struct local passed by value" -- a 4-byte boxed-ref mStack-index
  local passed by value. Not exercised by any green scenario this pass; needs
  IL-side ldfld/stfld on CLR struct fields. The return-source shape already
  works without a bridge (flat bytes) and is covered by the by-value param
  scenario. Silent wrong-result only for the Box/Initobj-source shape; NOT a
  regression of a previously-green case.
- Area 4 value-type `this` direct-call (`Unsafe.Unbox<T>` / autogen wrapper).
- CLR-method ref/out parameters (byref Ref Slot into CLR `ref T`).
- CLR-object stind/ldind/stobj/ldobj via field hash.
- F-MAJ-1 (Major, pre-existing): AllocateLocalStackSpaces slot-reuse for 2+
  simultaneous CLR struct locals. Target: next optimizer-hardening pass.
- Carryover pre-existing: Step 17 constrained.-on-VT (callvirt byref-this ->
  13b/future); Stobj/Ldobj ref-slot loop; Q-STRUCT/Q-LONG (not reproducible).

## Files changed (LEAD stages only)

- ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs
- ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs
- ILRuntime/CLR/Method/CLRMethod.cs
- ILRuntime/Runtime/CLRBinding/BindingGeneratorExtensions.cs
- ILRuntimeTestBase/TestFramework/TestClass3.cs
- TestCases/NeoStep13bTest.cs
- openspec/ (this change dir, including ship-log.md)

## Git note

Uncommitted. LEAD-authoritative working set is the 6 source/test files above +
this openspec change dir (incl. deferred-items notes). NOT included in the
change: the .pdb churn under Dependencies/ and the .gitignore tweak -- those
are unrelated repo noise and are not part of 13b.
