# Design: neo-clr-struct-newobj-retdest-null

## Evidence (JIT dump + byte dump, all from a REAL Neo run on HEAD)

### The JIT lowering of `var v = new TestVector3(1f, 2f, 3f)` (Roslyn `call .ctor` shape)
```
0:initobj   r0, TestVector3
2:ldloca.s  r6, r0
3:ldc.r4    r7, 1
4:ldc.r4    r8, 2
5:ldc.r4    r9, 3
6:push      r6
7:call.redirect -, r7, r8, r9, TestVector3::.ctor(Single,Single,Single)(m)
8:move      r6, r0
```
- dest register is `-` (Register1 = -1). The ctor is void; the result lands via the
  byref `this` (r6 -> r0), not a dest slot.
- `crIsNewObj = (Operand4 & 0x2) == 0x2` is FALSE: this is a `call`-originated
  `Call_Redirect`, not a Newobj origin (bit 0x2 is set only by the JIT's genuine
  `Newobj`-with-redirect rewrite at `JITCompiler.cs:2684`).

### The runtime `Call_Redirect` arm (HEAD) on this shape
```
crRetDstPtr = ip->Register1 >= 0 ? frameBase + ip->DstOffset : null;   // null (Register1 = -1)
crRetRefBase = ip->Register1 >= 0 ? ... : -1;                          // -1
InvokeNeoClrMethod(targetMethod, crIsNewObj=false, targetBase, mStack, null, -1);
// (no CopyNeoCallThisBack -- the byref `this` slot is never propagated back)
```

### The autogen `Ctor_0_Neo` stub (HEAD)
```csharp
int __curPrim = 0;
if (isNewObj) { __curPrim += 4; }              // newobj: skip retRefBase
else { /* TODO */ }                            // <-- the gap: no `this`-slot skip
float x = ReadNeoFloat(__frameBase, ref __curPrim);   // reads offset 0 = `this` slot
float y = ReadNeoFloat(__frameBase, ref __curPrim);   // offset 4
float z = ReadNeoFloat(__frameBase, ref __curPrim);   // offset 8
var result = new TestVector3(x, y, z);                // (0,0,0): read the zero `this`
if (__retDst != null) WriteNeoValueType(result, __retDst, 12);   // no-op: __retDst is null
```

### Byte-dump proof (diagnostic added to `Ctor_0_Neo`, then removed)
`Ctor_0_Neo` invoked with `isNewObj=False`, `__retDst=null`, `__retRefBase=-1`.
First 24 bytes of `__frameBase` (the param region):
```
00 00 00 00 00 00 00 00 00 00 00 00 | 00 00 80 3F | 00 00 00 40 | 00 00 40 40
<-------- the `this` struct (12B) --->| <-- x=1.0f ->| <-- y=2.0f ->| <-- z=3.0f
```
So the layout is `[this struct 12B][x][y][z]` (the byref `this` dereffed into
offset 0 by `CopyNeoCallArguments`). The HEAD stub reads `x/y/z` from offset 0 ->
reads the zero `this` slot. CONFIRMED.

## The two-shape distinction (why TC3 already passed on HEAD)
- **Into-local** (`var v = new VT(args);`): Roslyn `call .ctor` lowering, dest `-`,
  `crIsNewObj=false`. BROKEN on HEAD (this child's target). Fixed.
- **As-value** (`f(new VT(args))`, e.g. `SumTestVector3Fields(new VT(...), ...)`):
  Roslyn emits a genuine `newobj`, JIT rewrites to `Call_Redirect` with dest=rN and
  `Operand4 & 0x2` (crIsNewObj=true). The stub's `isNewObj` branch skips retRefBase,
  reads args at offset 4+, writes to `__retDst` (non-null). This shape ALREADY
  WORKED on HEAD (TC3 is a control -- passes both before and after).

## The fix

### 1. Stub (`ILRuntimeTest_TestFramework_TestVector3_Binding.cs` `Ctor_0_Neo`)
```csharp
int __thisSz = ILIntepreter.GetNeoValueTypeManagedSize(typeof(TestVector3));
if (isNewObj) { __curPrim += 4; }                      // skip retRefBase (unchanged)
else { __curPrim += __thisSz; }                        // skip the in-frame `this` struct
float x = ReadNeoFloat(...); float y = ...; float z = ...;   // now reads the real args
var result = new TestVector3(x, y, z);
if (isNewObj) { if (__retDst != null) WriteNeoValueType(result, __retDst, __thisSz); }
else { WriteNeoValueType(result, __frameBase, __thisSz); }   // write back to the `this` slot
```

### 2. Generator (`ConstructorBindingGenerator.cs` `GenerateConstructorWraperCode_Neo`)
Mirror the stub for future regen: declare `__thisSz` for value types; the `else`
branch does `__curPrim += __thisSz`; the return-write splits `isNewObj` (write to
`__retDst`) vs `!isNewObj` (write to `__frameBase`). Reference-type ctors keep the
existing path (the `else` branch is unreachable for classes -- Roslyn uses `newobj`).

### 3. Runtime (`ILIntepreter.Neo.cs` `Call_Redirect` arm)
Mirror the `Call` arm's snapshot + `CopyNeoCallThisBack`:
```csharp
bool[] crWbFlags = crMap.PrimitiveByRefWriteBack;
bool crNeedSnap = crWbFlags != null && crWbFlags.Length > 0;
int[] crSnapArr = crNeedSnap ? new int[...] : null;
if (crNeedSnap) {
    fixed (int* crSnap = crSnapArr) {
        int crCaptured = SnapshotNeoCallByRefSources(ref crMap, frameBase, crSnap);
        int* crByRefSnap = crCaptured > 0 ? crSnap : null;
        InvokeNeoClrMethod(targetMethod, crIsNewObj, crTargetBase, mStack, crRetDstPtr, crRetRefBase);
        CopyNeoCallThisBack(ref crMap, frameBase, crTargetBase, mStack, AppDomain, crByRefSnap);
    }
} else {
    InvokeNeoClrMethod(targetMethod, crIsNewObj, crTargetBase, mStack, crRetDstPtr, crRetRefBase);
}
```
`CopyNeoCallThisBack` is a no-op when `PrimitiveByRefSrc == null` (most redirects:
`Delegate.Combine`, static redirects), so non-struct-ctor redirects are unaffected.

## Why this is safe (risk assessment)
- **The runtime change is additive.** `CopyNeoCallThisBack` only writes slots that
  are flagged for write-back (`PrimitiveByRefWriteBack[i]`), and only after passing
  the `byRefSrc[i]` gate. For a redirect with no byref slots it returns immediately.
  The snapshot (mirror of `Call`/`Callvirt_CLR`) prevents a dest-aliasing clobber
  from corrupting the write-back read. So a `Call_Redirect` that previously had no
  write-back either gains a correct write-back (struct ctor / ref-out param) or is
  unchanged (no byref slots).
- **The stub change is `!isNewObj`-only.** The `isNewObj` branch is byte-identical
  to HEAD (the as-value shape TC3 stays green). The `!isNewObj` branch was a TODO
  (unreachable-for-correctness before); it now mirrors Legacy `Ctor_0`.
- **Neo-gated / file-gated.** All three sites are `#if ENABLE_NEO_MODE` (or in
  `ExecuteNeo`). Legacy `ExecuteR` + Legacy `Ctor_0` are byte-unchanged.

## Discriminators / collision-freedom
- `crIsNewObj` (`Operand4 & 0x2`) is the SAME bit the JIT stamps for a Newobj-origin
  `Call_Redirect` and the runtime reads in the `Call_Redirect` arm (C7 pinned this).
  This child does NOT touch that bit; it only adds the write-back the arm was missing.
- The `Call_Redirect` write-back mirrors the EXISTING `Call` and `Callvirt_CLR` arms
  (same snapshot idiom, same `CopyNeoCallThisBack` helper). No new helper, no new
  opcode, no JIT/optimizer/object-model change.

## Out of scope (DelegateTest24's second blocker)
`DelegateTest24` also does `list.Sum(v => v.X)` over a `List<TestVector3>`. After
this fix the `new TestVector3(...)` ctor constructs correctly, but
`list.Add(new TestVector3(1,2,3))` still stores ZERO: a host-side diagnostic
(`HostInspectListTestVector3First`, added temporarily then removed) read `list[0]`
entirely in the host and got `0`. So the struct ARG marshalling to the CLR-generic
instance method `List<TestVector3>.Add` delivers a zero struct. That is a separate
`callvirt.clr` struct-arg marshalling gap (sibling of child-26's `stelem.any`/
`ldelem.any` note), NOT the newobj-retdest-null contract. Needs its own child.
