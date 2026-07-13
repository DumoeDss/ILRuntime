# design -- neo-target-exception-mismatch (Wave-2 C14)

## The byref write-back mechanism (how it is SUPPOSED to work)
Neo marshals a `ref`/`out` arg through TWO cooperating halves:

1. **Forward (CopyNeoCallArguments, ILIntepreter.Neo.cs:405):** the caller's
   byref `(-1, frameOff)` (frame-native) or `(objIdx, fieldHash)` (mStack-object
   field) is DEREFERENCED and the referent bytes are copied into the callee
   param region `targetBase` at the param's dest slot. The byref source location
   + a write-back flag are recorded in the per-call `NeoCallParamMap`
   (`PrimitiveByRefSrc` / `PrimitiveByRefWriteBack`).

2. **The CLR call mutates the value.** For the reflection fallback,
   `CLRMethod.Invoke` (CLRMethod.cs:599-631) re-flattens the mutated `param[i]`
   box back into `targetBase`. For an autogen Neo redirect, the STUB must write
   the mutated local back into `__frameBase` (== `targetBase`) -- the generator
   emits this via `AppendNeoWriteBackCode` (BindingGeneratorExtensions.cs:265).

3. **Reverse (CopyNeoCallThisBack, ILIntepreter.Neo.cs:703):** for each
   write-back-flagged byref slot, copy the (now-mutated) `targetBase` dest bytes
   BACK through the source byref to the caller's frame local / object field.

The IL `Call` path performs step 3 (ILIntepreter.Neo.cs:3367/3399). The
`Callvirt_CLR` path (ILIntepreter.Neo.cs:3766) was MISSING step 3. And the
committed `VMethod3_1_Neo` stub was missing step 2. Both gaps together mean a
`ref`/`out` arg to a Callvirt_CLR target never reaches the caller.

## Fix 1 -- Callvirt_CLR write-back (ILIntepreter.Neo.cs, ~:3864)
After `InvokeNeoClrMethod`, mirror the IL Call path's snapshot + write-back:
- Snapshot every write-back-flagged byref source's `(objIdx, off)` BEFORE the
  call into a heap `int[]` (pinned via `fixed`). The call dest may ALIAS a byref
  source register (e.g. `x = obj.M(ref x)`); without the snapshot, the result
  overwrites the byref bytes before CopyNeoCallThisBack re-reads them.
- `CopyNeoCallThisBack(ref map, frameBase, targetBase, mStack, AppDomain, snap)`
  propagates `targetBase` -> `frameBase`.
- No-op when the method has no ref/out params (`PrimitiveByRefSrc == null` ->
  CopyNeoCallThisBack returns immediately). F-7B IL-method rebasing excluded.

Safety (no regression): adding the write-back only affects Callvirt_CLR calls
WITH ref/out params. For such a call, if the redirect did NOT write the mutated
value into `targetBase` (a stale stub), `targetBase` retains the pre-call value,
so the write-back stores the SAME value the caller already has -> identical to
the prior (no-write-back) behavior. A correct stub makes the write-back
observable. Either way, no currently-passing test changes behavior.

## Fix 2 -- VMethod3_1_Neo stub port (TestClass2_Binding.cs:111)
Hand-port the stale stub to the current generator template:
```csharp
int __off_2 = __curPrim;                                   // capture arg slot offset
System.Int32 @arg = (System.Int32)ILIntepreter.ReadNeoInt32(__frameBase, ref __curPrim);
instance_of_this_method.VMethod3(ref @arg);
int __wb_sz_2 = ILIntepreter.GetNeoValueTypeManagedSize(typeof(System.Int32));
ILIntepreter.WriteNeoValueType(@arg, __frameBase + __off_2, __wb_sz_2);   // write-back epilogue
```
Both helpers are `public static` on ILIntepreter (WriteNeoValueType @ Neo.cs:258,
GetNeoValueTypeManagedSize @ Neo.cs:276). Neo-gated (`#if ENABLE_NEO_MODE`).
This is the child-28 stale-stub pattern; a future GUI regen will emit identical
code (the generator already does -- MethodBindingGenerator.cs:440).

## Why IT07 and IT18 are out of scope (distinct clusters)
- **IT07**: `TestCls5.AbMethod2` (`arg1 + 1.2f`) runs via `ILIntepreter.ExecuteR`
  (Register.cs:5325) -- a LEGACY executor reached through the cross-binding
  adaptor's IL-invocation path. ExecuteR cannot dispatch the Neo-only opcode
  `Addi_R4` (child-16's typed float-add). Root cause = adaptor callbacks using
  the Legacy executor in Neo mode. Deep; unrelated to C14's ref write-back.
- **IT18**: `TestClass2.Alloc() as TestCls5` returns the CLR adaptor wrapping
  the IL instance; casting back to the IL type fails. Neo `unbox.any` on an IL
  reference type THROWS (Neo.cs:5159 / :5187); Legacy KEEPS the adaptor
  (Register.cs:4147) and unwraps it at field access. A multi-site Neo
  adapter-unwrap gap (reverse of C2). Distinct cluster.

## Capability home
`neo-byref` -- the change is a byref write-back fix in the Callvirt_CLR handler
(a sibling of the Step-13-Area-4c / child-25 byref work) + a stale autogen stub
that implements the Area-4c write-back epilogue.
