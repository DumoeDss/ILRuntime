# Full Neo Smoke Grounding -- 2026-07-16 FRESH re-cluster (10 failing) + 1 flip shipped (10 -> 9)

## STATUS: FRESH grounding + 1 root pinned and FIXED (full smoke 10 -> 9).
Full smoke **10 failed** pre-fix (verified by a FRESH no-filter run this child):
`Ran 944 tests, 10 failded, 20 ignored, 7 todos` (exit 127; known graceful Dict-NRE crash).
Post-fix full smoke **9 failed**: `Ran 948 tests, 9 failded, 20 ignored, 7 todos`
(+4 = this child's isolation probes, all pass). NeoStep **414/0** (no regression).
Legacy-neutral (plain Debug build 0 errors; all changes `#if ENABLE_NEO_MODE`-gated).

The pre-fix 10 are a STRICT SUBSET of ground-13's 13 (3 fixed since: DelegateTest42
+ UnitTest_TestFCP + UnitTest_10046). This child FRESH-ran the smoke, re-clustered
the CURRENT 10, DEEP-diagnosed UnitTest_TestInline01 to its precise byte-level root
(IsInvalidMethodReference caches null for System.Object..ctor -> `new object()` was
silently skipped -> result was null), fixed it, and fixed the two siblings the fix
unmasked (Optimizer dest-stamp + initobj-on-ldflda-byref).

## Fresh ground (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch)
Pre-fix: `Ran 944 tests, 10 failded, 20 ignored, 7 todos` (exit 127). NeoStep 404/0.
Build: CLI Debug_Neo --no-incremental -p:UseSharedCompilation=false (0 errors);
TestCases Debug -p:UseSharedCompilation=false (0 errors).

## The CURRENT 10 (pre-fix), clustered by PINNED root + per-test trace (fresh run)

Each row = exception type + the throwing site (clean stack from the run).

### Cluster A -- test-internal assertion (System.Exception throw@7197), 8 tests
Each is a DISTINCT value-corruption root.
- **UnitTest_TestInline01 (LightTester2.cs:143)** -- `Object obj = null`. **FIXED
  this child.** Root PINNED: `new object()` returned null. See PINNED section.
- StructTests.StructTest6 (Structs.cs:262) -- locals `cube={objAsset=null,
  type=123}` (TryGetValue out-STRUCT not written back; cube.type stayed "123").
  byref out-STRUCT write-back. DEEP (out-direction sibling of il-struct-box).
- StructTests.StructTest12 (Structs.cs:400) -- locals `ins.i=1` (expected 10).
  generic struct constrained to ITestStruct: `new T(){i=10}` -> constrained-
  callvirt property set on a generic struct param. DEEP.
- StaticTest.UnitTest_StaticTest05 (StaticTest.cs:106) -- `ref Vector3 staticField`
  write-back lost. MULTI-BUG (ldsflda offset + read-back). DEEP.
- ReflectionTest25 (ReflectionTest.cs:680) -- locals `attr=null,v8=F,v9=F`. CLR
  attribute reflection (GetCustomAttribute wrong/null). DEEP.
- ExpTest_20.UnitTest_TestStackRegisterTransition3 (LightTester2.cs:356) --
  locals `v0=T,v1=F`. TransitionTest struct passed by value to TransitionTest2.Test:
  register-transition value corruption. DEEP (needs JIT dump).
- TestValueTypeBinding.UnitTest_10051 (TestValueTypeBinding.cs:619) -- locals
  `Fixed64Vector2 v5=...`. `list[0].V2.x.RawValue==999`: constrained-callvirt
  property read on a nested struct field. DEEP (F-10 handoff noted this).
- ReflectionTest.ReflectionTest14 (ReflectionTest.cs:438 entry, throws at :465) --
  `ldlen NRE` (Object reference not set) inside TestTypeAssignableFrom's
  `foreach (field in targetType.GetFields())`. The null array is the Neo CALL
  RESULT of GetFields()/GetProperties() not being written back, OR a field
  FieldType resolution gap (the local dump `FieldInfo[] v5=null` is the foreach
  array). NOT a clean ILRuntimeType.GetFields override (that returns res.ToArray,
  never null). DEEP.

### Cluster B -- autogen Neo binding stub / enumerator dispatch, 1 test
- MyTest.Test (Test01.cs:618) -- `InvalidCastException String->IEnumerator<KVP
  <int,int>>` at autogen `get_Current_0_Neo:48` <- InvokeNeoClrMethod Neo.cs:1331
  <- ExecuteNeo Neo.cs:4317. The Dictionary enumerator `this` is mis-marshalled
  (a String ends up in the `this` slot on a later loop iteration). DEEP (Step-19 /
  boxed-CLR-struct enumerator).

### Cluster C -- >3-arg virtual-IL-call param-map / frame-offset, 1 test
- RegisterVMTest.RegisterVMTest04 (RegisterVMTest.cs:107) --
  `ArgumentOutOfRangeException @ List.get_Item` (AutoList) <- ExecuteNeo
  (stfld.ref reads an out-of-range index). The store is `viewRectEvent = action`
  inside `ILScrollRect2<T>.SetViewRect` (override in a GENERIC INSTANCE, called
  virtually with >3 args + default `action=null`). DEEP (call marshalling /
  param map / calling convention through a generic instance).

## PINNED: UnitTest_TestInline01 root (DEEP-diagnosed this child, FIXED)

The recluster-34 handoff framed this as "by-ref aliasing on a plain Call: Sub(
object o){o=null;} nullifies the caller's local." THAT FRAMING WAS WRONG (the
12-for-12 / 17-for-17 lineage holds again). Isolation probes
(TestCases/NeoStepRecluster10Probe.cs) PROVED:
- TC1 `object obj = new object(); if(obj==null) throw` FAILS with NO call at all.
- TC3 `object obj = new object(); ReadSub(obj); if(obj==null) throw` (callee does
  NOT nullify) FAILS -> obj was null BEFORE the call.
- TC4 `string s="hello"; NullifyStr(s); if(s==null||s!="hello") throw` PASSES ->
  ldstr args and the call survive; the bug is specific to `new object()`.

ROOT: `new System.Object()` produced null under Neo. `AppDomain.IsInvalidMethodReference`
(AppDomain.cs:2156-2167) EXPLICITLY flags `System.Object..ctor()` (and
`System.Attribute..ctor()`) as "invalid" and caches null for the method token, so
`GetMethod(tokenHash)` returns null at runtime. The Neo Newobj arm then did
`if (targetMethod == null) { ip++; continue; }` -- SILENTLY SKIPPING the newobj,
leaving the dest register unwritten (null). Legacy handles this case explicitly:
ILIntepreter.Register.cs:3529-3536 `cm = (CLRMethod)m; if (cm == null) { esp =
PushObject(esp, mStack, new object()); }` ("Means new object();"). The Neo arm
was missing the Legacy parity.

THE FIX (3 parts, all Neo-gated -> Legacy-neutral):
1. ILIntepreter.Neo.cs Newobj arm: when targetMethod==null, construct a real
   `new object()` and write it to the dest as a reference-type newobj result
   (mStack[dstRefSlot] = obj; *(int*)retDstPtr = dstRefSlot). Mirrors Legacy.
   The null-method signal is EXCLUSIVELY the IsInvalidMethodReference path (any
   other resolution failure throws KeyNotFoundException upstream instead of
   caching null), so this is safe.
2. Optimizer.Neo.cs LowerNeoOffsets Newobj case: the `if (targetMethod == null)
   break;` previously skipped the ENTIRE newobj lowering, including the dest-
   offset stamping at the case tail (op.DstOffset/Operand3 from localInfos[r1]).
   So DstOffset retained the register INDEX (not a byte offset) and the runtime
   wrote the result to a garbage frame location, clobbering an adjacent register
   (UnitTest_1013: `tc.tValue = new object()` corrupted the owner tc). Fix: stamp
   the dest offset for the null-method case too (0 params -> only dest stamping
   needed; no Push scan, no NeoCallParamMap).
3. JITCompiler.cs initobj marker: the `NeoInitobjByRefOperandMarker` was stamped
   ONLY for a `ldarg` predecessor (a `ref T` param). Fix #1 unmasked
   UnitTest_1013, whose SetNull() lowers `tValue = null` (T=object) to
   `ldflda <tValue>; initobj System.Object`. The ldflda-produced byref is a
   genuine managed pointer (NOT addr-alias-folded), but the marker did NOT fire
   for ldflda -> initobj direct-wrote -1 to the byref TEMP and the field stayed
   non-null. Fix: also stamp the marker for a `Code.Ldflda` predecessor. A
   value-type field's ldflda+initobj never reaches the marker (`!initT.IsValueType`
   gate), so the fold-vs-genuine discrimination is unchanged. The runtime
   NeoWriteNullThroughByref then clears ManagedObjects[refOff] (consistent with
   Stfld_Ref writes + Ldfld_Ref reads, which both use ManagedObjects[refOff]).

NOTE: Fix #1 (new object()) is the load-bearing correctness fix. Fix #2 (dest
stamp) and Fix #3 (initobj marker) are the two siblings #1 unmasked (the C7
"unmasked sibling" pattern). All three are needed for UnitTest_TestInline01 to
flip WITHOUT regressing UnitTest_1013.

## Verify (truth = full-smoke number)
- FULL SMOKE: **10 -> 9** (UnitTest_TestInline01 flipped; UnitTest_1013 passes
  both pre- and post-fix; the 9 survivors are a strict subset of the pre-fix 10).
  Pre-fix `Ran 944 tests, 10 failded` -> post-fix `Ran 948 tests, 9 failded`
  (+4 probes, all pass). Exit 127 (known graceful Dict-NRE crash; summary emitted).
- NeoStep **414/0** (no regression; +4 = this child's probes).
- Legacy-neutral: plain Debug build 0 errors; all 3 engine changes are
  `#if ENABLE_NEO_MODE`-gated (ILIntepreter.Neo.cs file-gated; Optimizer.Neo.cs
  file-gated; JITCompiler.cs initobj marker under #if). Legacy NeoStep smoke
  414 ran / 19 failed (the pre-existing Legacy-specific set; my Neo-gated changes
  do not compile into Legacy). All 4 probes PASS under Legacy too.
- Stash-toggle airtight: each fix was isolated by probes (TC1/TC3/TC4 for fix #1;
  UnitTest_1013 for fixes #2+#3).

## The 9 REMAINING (post-fix), all DEEP singletons (report honestly)
1. RegisterVMTest04 -- >3-arg virtual-IL-call param-map/frame-offset through a
   generic instance. The cleanest DEEP call-marshalling child.
2. MyTest.Test -- boxed-CLR-struct enumerator interface dispatch (Step-19).
3. StructTest6 -- byref out-STRUCT write-back (TryGetValue out cube).
4. UnitTest_10051 -- nested-struct-field constrained-callvirt property read.
5. UnitTest_TestStackRegisterTransition3 -- register-transition value corruption.
6. ReflectionTest25 -- CLR attribute reflection (GetCustomAttribute).
7. ReflectionTest14 -- null reflection array (GetFields/GetProperties call result
   or field FieldType resolution under Neo).
8. StructTest12 -- generic struct constrained-callvirt property set.
9. UnitTest_StaticTest05 -- ref Vector3 staticField write-back (multi-bug).

## Lesson reaffirmed (the 12-for-12 / 17-for-17 lineage)
The recluster-34 "by-ref aliasing on a plain Call" framing for UnitTest_TestInline01
was DISPROVEN by isolation probes: the callee operates on its OWN copy register
(r2), never the caller's (r0). The REAL root was upstream -- `new object()` was
null because IsInvalidMethodReference caches null and the Neo newobj arm silently
skipped it. ALWAYS isolate with a no-call probe before accepting an "aliasing"
diagnosis; trace the PRODUCER (newobj), not the throwing reader.

## Ship (this child, NOT committed -- LEAD commits)
- FIX 1 (ILIntepreter.Neo.cs, Newobj arm, targetMethod==null branch): construct
  `new object()` + write to dest (reference-type newobj result). Mirrors Legacy
  Register.cs:3529-3536.
- FIX 2 (Optimizer.Neo.cs, LowerNeoOffsets Newobj case, targetMethod==null
  branch): stamp op.DstOffset/Operand3 from localInfos[op.Register1] (the dest
  byte/ref offsets) so the runtime writes the result to the correct frame slot.
- FIX 3 (JITCompiler.cs, initobj marker): extend NeoInitobjByRefOperandMarker to
  also fire for a `Code.Ldflda` predecessor (the `refField = null/default(T)`
  lowering on a heap reference field).
- PROBE (TestCases/NeoStepRecluster10Probe.cs, TC1-TC4): permanent isolation +
  regression probes. TC1 (new object() alone) is the load-bearing regression
  probe for fix #1; TC2 (the call) + TC3 (no-nullify control) + TC4 (string arg)
  document the aliasing-disproving isolation.

## Files this child (NOT committed, LEAD commits)
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (Newobj null-method
  branch -- construct + write `new object()`).
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` (Newobj null-method
  dest-offset stamping in LowerNeoOffsets).
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` (initobj marker
  extended to Ldflda predecessor).
- `TestCases/NeoStepRecluster10Probe.cs` (NEW, 4 isolation/regression probes).
