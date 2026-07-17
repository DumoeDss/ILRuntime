# Proposal: neo-lowerneoffsets-push-missing (Wave-2 C7)

## Why
Full Neo smoke (grounding 2026-07-13) had 3 tests failing during JIT with
`System.Exception: Neo lowering could not find expected Push instructions for
Call/Newobj.` (thrown from `Optimizer.Neo.cs:LowerNeoOffsets`):

- `TestCases.DelegateTest.DelegateTest24`
- `TestCases.StructTests.StructTest7`
- `TestCases.TestValueTypeBinding.UnitTest_10027`

These are JIT-correctness failures in the Push-deletion pass (the ONLY Neo pass
that changes instruction-body LENGTH). Child-1 fixed Leave/Leave_S remap;
child-14 plumbed the EH-table remap. C7 is the SAME function, a NEW shape where
the Push-deletion expectation is violated.

## Root cause (re-audited, JIT-dump-confirmed)
A CLR `Newobj` whose target has a redirect (e.g. `TestVector3::.ctor(float,float,float)`
with a ValueTypeBinder redirect) is rewritten by the JIT from `Newobj` to
`Call_Redirect`, marking the Newobj origin in `Operand4` bit `0x2`
(`JITCompiler.cs` Newobj case: `op.Operand4 = 0x2 | rCnt<<16`).

The JIT's Newobj path computes `pCnt` WITHOUT the implicit-`this` bump (the
`this` is the newobj RESULT, not an explicit arg) and emits `max(pCnt-3,0)`
synthetic `Push` instructions. For a 3-param ctor that is `0` Pushes.

`LowerNeoOffsets` saw `op.Code == Call_Redirect` (not `Newobj`) and re-applied
the `HasThis` bump: `pCnt = ParameterCount + 1 = 4` -> `pushCnt = max(4-3,0) = 1`.
It then scanned backwards for a Push the JIT never emitted, failed to find it,
and threw. Diagnostic confirmed for ALL 3 tests:
`code=Call_Redirect method=.ctor(Single,Single,Single) PC=3 HasThis=True pCnt=4
pushCnt=1 foundPushes=0 op4=196610(=0x30002)`.

## What changes
- `Optimizer.Neo.cs` `LowerNeoOffsets` Call/Newobj case: compute
  `isNeoNewobjShape = op.Code == Newobj || (op.Code == Call_Redirect &&
  (op.Operand4 & 0x2) != 0)` and use it INSTEAD of `op.Code == Newobj` at every
  param-layout decision site (pCnt bump, paramInfos 'this'-slot reservation,
  dstIndex, paramType, dstIsVtThisSlot, paramLogical, dest stamping). This makes
  a Newobj-originated Call_Redirect produce a byte-identical NeoCallParamMap +
  dest layout as a genuine Newobj, which is exactly what the runtime
  `Call_Redirect` arm (`crIsNewObj = (ip->Operand4 & 0x2)==0x2`) consumes --
  identical to the `Newobj` arm (`InvokeNeoClrMethod(targetMethod, isNewobj:true,
  ...)`).

## Out of scope (documented deferred siblings, NOT C7, unmasked by the fix)
- `StructTest7` now reaches a `Neo raw Stfld: ... F-10 ManagedObjects storage
  (deferred)` tagged NIE (child-27/29 F-10 IL-instance CLR-struct-field owner).
- `DelegateTest24` now reaches its own `res != 6` assertion because
  `new TestVector3(float,float,float)` still yields zero (child-28 struct-newobj
  retDst follow-up; explicitly documented out-of-scope in
  `NeoStepFloatVtReturnTest.cs:27`).

Both are pre-existing deferred issues that the C7 JIT throw was MASKING. Fixing
C7 unblocks the JIT; the remaining failures are separate children.

## Success criteria (truth = full-smoke number)
- The 3 C7 tests no longer throw the Push exception. (DONE -- 0 occurrences.)
- `UnitTest_10027` PASSES end-to-end. (DONE.)
- Full smoke failure count drops from 103. (DONE: 103 -> 101.)
- NeoStep 0-failures (no regression; LowerNeoOffsets is core). (DONE: 382/0.)
- Legacy-neutral (entire `Optimizer.Neo.cs` is `#if ENABLE_NEO_MODE`). (YES.)
