# Proposal: neo-clr-struct-newobj-retdest-null

> Wave-2 child of `neo-overhaul`. Branch `features/object-model-overhaul`.
> Neo = `ExecuteNeo` under `ENABLE_NEO_MODE`. Neo-gated -> Legacy-neutral by construction.

## Problem (root cause, re-audit-confirmed)
A CLR value-type constructor that has an autogen ValueTypeBinder redirect (the
canonical case: `ILRuntimeTest.TestFramework.TestVector3::.ctor(float,float,float)`)
constructs a ZERO struct when the C# local-assignment lowering is used:

```csharp
TestVector3 v = new TestVector3(1f, 2f, 3f);   // v == (0,0,0) under Neo (HEAD)
```

Roslyn lowers a struct `new VT(args)` assigned to a local to the `call .ctor`
shape (NOT a `newobj` opcode):

```
initobj   r0, TestVector3          ; zero-init the local
ldloca.s  r6, r0                   ; byref to the local
ldc.r4    r7, 1 ; ldc.r4 r8, 2 ; ldc.r4 r9, 3
push      r6                       ; overflow-arg: the byref `this`
call.redirect -, r7, r8, r9, TestVector3::.ctor(Single,Single,Single)   ; dest = `-`
```

The JIT emits this as a `Call_Redirect` (the ctor has a redirect) with
`Register1 = -1` (dest `-`, because a ctor is void and mutates through the byref
`this`) and `Operand4 & 0x2 == 0` (NOT a Newobj origin -- it came from a `call`,
so `crIsNewObj` is false). JIT-dump + byte-dump confirmed (see design).

Two compounding defects on this path:

1. **The autogen Neo ctor stub left the `!isNewObj` branch as a `// TODO`.**
   `Ctor_0_Neo` did `if (isNewObj) __curPrim += 4; else { /* TODO */ }`, then read
   `x/y/z` starting at offset 0. For the `call .ctor` shape the param-region layout
   is `[this struct (12B)][x][y][z]` (the byref `this` is dereffed into the first
   `__thisSz` bytes by `CopyNeoCallArguments`). So the stub read the zero-init
   `this` slot as `x/y/z` -> constructed `(0,0,0)`.

2. **The runtime `Call_Redirect` arm never called `CopyNeoCallThisBack`.** Even
   with a correct stub write, the `retDst` is `null` (dest `-`), so the stub's
   `if (__retDst != null)` write no-ops. The constructed struct has nowhere to
   land: the only way it reaches the caller's local is the byref-`this` write-back
   (`CopyNeoCallThisBack`), which the `Call_Redirect` arm did not invoke (unlike
   the sibling `Call` and `Callvirt_CLR` arms, which already do).

## Fix (Neo-only, mirrors Legacy)
- **Stub (`Ctor_0_Neo` + generator template):** for `!isNewObj`, skip the in-frame
  `this` struct (`__curPrim += __thisSz`) before reading the args, and write the
  constructed struct into the `this` slot at offset 0 (`WriteNeoValueType(result,
  __frameBase, __thisSz)`). This mirrors Legacy `Ctor_0`'s `!isNewObj` ->
  `WriteBackInstance` path.
- **Runtime (`Call_Redirect` arm):** mirror the `Call` arm's snapshot +
  `CopyNeoCallThisBack` so the byref-`this` slot is propagated back to the
  caller's local. A redirect with no byref slots (`Delegate.Combine` etc.) is a
  no-op (`PrimitiveByRefSrc == null`).

## Scope + honesty
The fix is verified correct (3 probes PASS, stash-toggle airtight, NeoStep 385/0
no regression, Legacy-neutral). The full Neo smoke delta is **0 (101 -> 101)**:
`DelegateTest24` (the test the Wave-2 plan named) has a SECOND, separate blocker
-- the `callvirt.clr` struct-ARG marshalling to `List<TestVector3>.Add` delivers a
zero struct to the host (host-side inspection of `list[0]` after
`list.Add(new TestVector3(1,2,3))` returns 0). That is a distinct gap (CLR-generic
instance-method struct-arg marshalling, sibling of the child-26 `stelem.any`/
`ldelem.any` note), NOT the newobj-retdest-null contract fixed here. Flipping
`DelegateTest24` end-to-end needs a separate child for that path.

## Capability
`neo-newobj` (the newobj dest contract; child-13 lineage). The runtime change also
touches the `Call_Redirect` dispatch arm (`neo-dispatch`), but the load-bearing
semantic is the newobj/ctor dest convention.
