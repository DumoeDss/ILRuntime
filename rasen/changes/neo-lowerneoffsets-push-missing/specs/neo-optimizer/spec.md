## ADDED Requirements

### Requirement: LowerNeoOffsets MUST treat a Newobj-originated Call_Redirect as a Newobj for call-parameter layout

`Optimizer.Neo.cs` `LowerNeoOffsets` (the Neo Push-deletion + call-param-map
pass, the ONLY Neo pass that changes instruction-body LENGTH) SHALL compute a
Newobj-origin flag

    bool isNeoNewobjShape = op.Code == OpCodeREnum.Newobj
        || (op.Code == OpCodeREnum.Call_Redirect && (op.Operand4 & 0x2) != 0);

and SHALL use `isNeoNewobjShape` -- NOT the raw `op.Code == Newobj` -- at every
call-parameter-layout decision site in the Call/Newobj case: the implicit-`this`
`pCnt` bump, the callee `paramInfos` 'this'-slot reservation (size + index
`dstIndex = p+1`), the `paramType`/`Parameters[]` index adjustment, the
`dstIsVtThisSlot` detection, the byref `paramLogical` index, and the newobj-dest
(`DstOffset`/`Operand3`) stamping.

#### Why
The JIT (`JITCompiler.cs` Newobj case) lowers a CLR `Newobj` whose target has a
redirect (e.g. a ValueTypeBinder struct ctor such as
`TestVector3::.ctor(float,float,float)`) by first emitting it as a `Newobj`
(`pCnt = ParameterCount`, NO implicit-`this` bump -- the `this` is the newobj
RESULT, not an explicit arg -- and `max(pCnt-3,0)` synthetic `Push`
instructions), and THEN rewriting `op.Code` from `Newobj` to `Call_Redirect`
with `op.Operand4 = 0x2 | (rCnt << 16)`. Bit `0x2` is the JIT's exclusive marker
that this `Call_Redirect` originated from a `Newobj`; the Call/Callvirt redirect
path never sets bit `0x2` (it uses `0x1` constrained / `0x4` hasReturn /
`rCnt<<16`).

Without recognizing the Newobj-origin shape, `LowerNeoOffsets` would re-apply
the implicit-`this` bump (`pCnt = ParameterCount + 1`) on seeing
`op.Code == Call_Redirect`, compute a `pushCnt` one larger than the JIT emitted,
fail to find the nonexistent `Push` during the backwards scan, and throw
`Neo lowering could not find expected Push instructions for Call/Newobj.`
(observed for any CLR redirect ctor with >= 3 declared params, e.g.
`TestVector3(float,float,float)`).

The runtime `Call_Redirect` arm (`ILIntepreter.Neo.cs`) derives
`crIsNewObj = (ip->Operand4 & 0x2) == 0x2` and calls
`InvokeNeoClrMethod(targetMethod, crIsNewObj, ...)` -- identical to the
`Newobj` arm's `InvokeNeoClrMethod(clrCtor, true, ...)` -- and both arms consume
the `NeoCallParamMap` + `DstOffset`/`Operand3` identically. Therefore the map +
dest layout `LowerNeoOffsets` builds for a Newobj-originated `Call_Redirect`
MUST be byte-identical to a genuine `Newobj`.

#### SHALL
- `LowerNeoOffsets` SHALL NOT apply the implicit-`this` `pCnt` bump for a
  Newobj-originated `Call_Redirect` (it has no explicit `this` arg).
- The `NeoCallParamMap` (PrimitiveSrc/Dst/Size, RefSrc/Dst, PrimitiveByRefSrc,
  PrimitiveByRefWriteBack, PrimitiveByRefElemType) for a Newobj-originated
  `Call_Redirect` SHALL be identical to the map a genuine `Newobj` of the same
  target would produce.
- A Newobj-originated `Call_Redirect` SHALL reserve the callee `paramInfos[0]`
  'this' slot and offset declared-param `dstIndex` by +1, exactly as a genuine
  `Newobj` does.

#### MUST NOT
- `LowerNeoOffsets` MUST NOT throw "could not find expected Push" for a
  Newobj-originated `Call_Redirect` whose declared-parameter count is >= 3.
- A Call/Callvirt-originated `Call_Redirect` (Operand4 bit `0x2` clear) MUST be
  unaffected -- it keeps the implicit-`this` bump and the existing layout.

### Scenario: a CLR redirect struct ctor with 3 params JITs and runs under Neo
WHEN IL constructs `new TestVector3(1f, 2f, 3f)` (a CLR struct whose ctor has a
Neo redirect) inside any Neo-compiled method,
THEN `LowerNeoOffsets` SHALL compute `pCnt = 3`, `pushCnt = 0`, build a
`NeoCallParamMap` with the 3 float declared params (no phantom Push), and
SHALL NOT throw "could not find expected Push instructions for Call/Newobj.",
and the full Neo smoke failure count SHALL drop (baseline 103 -> 101 observed;
`UnitTest_10027` flips to PASS; `StructTest7`/`DelegateTest24` progress to
their own pre-existing deferred-sibling failures, documented out-of-scope).

### Scenario: no regression on Neo methods with protected regions
WHEN the NeoStep smoke (incl. NeoStep14 EH 26/26) runs after the change,
THEN it SHALL remain 0-failures (observed 382/0), because the change only
alters behavior for `Call_Redirect` instructions whose `Operand4` has bit `0x2`
set; for every other Call/Newobj/Call_Redirect, `isNeoNewobjShape` evaluates
identically to the prior `op.Code == Newobj`.
