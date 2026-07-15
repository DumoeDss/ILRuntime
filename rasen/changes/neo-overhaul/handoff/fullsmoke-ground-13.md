# Full Neo Smoke Grounding -- 2026-07-16 FRESH re-cluster (13 failing)

## STATUS: FRESH grounding + 1 correctness fix shipped (does NOT reduce the 13).
Full smoke **13 failed** (verified by a FRESH no-filter run this child):
`Ran 938 tests, 13 failded, 20 ignored, 7 todos` (exit 0; no crash).
NeoStep **404/0** (no regression; +7 isolation probes from this child's TC1-TC7).

The 13 are a STRICT SUBSET of ground-15's 15: StructTest11 + TestStructDictionary
were FIXED by `neo-il-struct-box-call-boundary` (15 -> 13). All 13 here are the
DEEP singletons ground-15 already documented; this child RE-VERIFIED each against
a fresh run + captured clean per-test stack traces + DEEP-diagnosed the #4
priority (UnitTest_TestFCP) to its precise runtime location.

## Fresh ground (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch)
`Ran 938 tests, 13 failded, 20 ignored, 7 todos` (exit 0). NeoStep **404/0**.
Build: CLI Debug_Neo --no-incremental -p:UseSharedCompilation=false (0 errors);
TestCases Debug -p:UseSharedCompilation=false (0 errors).

## The CURRENT 13, clustered by PINNED root + per-test trace (fresh run)

Each row = exception type + the throwing site (clean stack from the run). The 10
"test-internal assertion" rows throw at the test's own `throw` (reaches the CIL
Throw handler @ Neo.cs:7141) -- each is a DISTINCT value-corruption root, NOT one
fix. 3 throw at a distinct Neo.cs line (concrete exception).

### Cluster A -- test-internal assertion (System.Exception throw@7141), 10 tests
Each needs its own JIT-dump triage; the surface is exhausted of shared roots.
- DelegateTest42 (DelegateTest.cs:662) -- locals `v2=F,v3=F,v4=T,v5=F`. delegate
  assertion (delegate.Target / dispatch wrong for an IL-instance-method delegate).
  Step-19. DEEP.
- ExpTest_20.UnitTest_TestInline01 (LightTester2.cs:143) -- locals `obj=null`.
  reference-arg aliasing on an INLINED plain Call: `Sub(object o){o=null;}`
  nullifies the caller's local. Same defect CLASS as child-13 (newobj arg
  aliasing) but on a plain inlined Call. DEEP (JIT inliner/allocator).
- ExpTest_20.UnitTest_TestFCP (LightTester2.cs:158) -- locals `color=(1,0,0)`
  (expected (1,1,0)). **DEEP-DIAGNOSED THIS CHILD -- see PINNED section below.**
  Root = a struct-Move/Ret interaction in ToColor's `move r; ret r` sequence.
- ExpTest_20.UnitTest_TestStackRegisterTransition3 (LightTester2.cs:339) --
  locals `v0=T,v1=F`. TransitionTest struct {int A; string B; float C;
  TransitionTestSub D} passed by value to TransitionTest2.Test: register-
  transition value corruption. DEEP (needs JIT dump).
- ReflectionTest25 (ReflectionTest.cs:680) -- locals `attr=null,v8=F,v9=F`. CLR
  attribute reflection (GetCustomAttribute wrong/null). DEEP.
- StaticTest.UnitTest_StaticTest05 (StaticTest.cs:106) -- `ref Vector3
  staticField` write-back lost. MULTI-BUG (ldsflda offset + read-back). DEEP.
- StructTests.StructTest6 (Structs.cs:262) -- locals `cube={objAsset=null,
  type=123}` (TryGetValue out-STRUCT not written back; cube.type stayed "123").
  byref out-STRUCT write-back. DEEP (the out-direction sibling of il-struct-box).
- StructTests.StructTest12 (Structs.cs:396) -- locals `ins.i=1` (expected 10).
  generic struct constrained to ITestStruct: `new T(){i=10}` -> constrained-
  callvirt property set on a generic struct param. DEEP.
- TestValueTypeBinding.UnitTest_10046 (TestValueTypeBinding.cs:470) -- `a=a+One2;
  a.X!=2` via a DELEGATE (TestDelegate2). delegate VT-arg marshal (Step-19).
  DEEP.
- TestValueTypeBinding.UnitTest_10051 (TestValueTypeBinding.cs:619) -- locals
  `v3=F`. `list[0].V2.x.RawValue==999`: constrained-callvirt property read on a
  nested struct field (Fixed64Vector2.x.RawValue), via list.Sort delegate.
  DEEP (F-10 handoff already noted this is a separate nested-struct-property-
  read gap).

### Cluster B -- autogen Neo binding stub / enumerator dispatch, 1 test
- MyTest.Test (Test01.cs:618) -- `InvalidCastException String->IEnumerator<KVP
  <int,int>>` at autogen `IEnumerator_1..._Binding.get_Current_0_Neo:48` <-
  InvokeNeoClrMethod Neo.cs:1331 <- ExecuteNeo Neo.cs:4293. The Dictionary
  enumerator `this` is mis-marshalled (a String ends up in the `this` slot on a
  later loop iteration). DEEP (Step-19 / boxed-CLR-struct enumerator).

### Cluster C -- >3-arg virtual-IL-call param-map / frame-offset, 1 test
- RegisterVMTest.RegisterVMTest04 (RegisterVMTest.cs:107) --
  `ArgumentOutOfRangeException @ List.get_Item` (AutoList) <- ExecuteNeo
  Neo.cs:5166 (`stfld.ref` reads `mStack[srcIdx]` where srcIdx=65535 garbage).
  The store is `viewRectEvent = action` inside `ILScrollRect2<T>.SetViewRect`
  (override in a GENERIC INSTANCE, called virtually with >3 args + default
  `action=null`). RE-PINNED (ground-16, the cleanest DEEP call-marshalling
  child): the generic instance field layout is CORRECT; the CALLEE reads
  `action` (param) at a frame offset whose value is 65535 (garbage), NOT -1
  (ldnull) the caller produced -> a >3-arg virtual-IL-call NeoCallParamMap /
  frame-offset bug. DEEP (call marshalling / param map / calling convention).

### Cluster D -- ldlen on null reflection array, 1 test
- ReflectionTest14 (ReflectionTest.cs:465) -- `ldlen NRE @ Neo.cs:6070`
  (`((Array)mStack[srcIdx]).Length` where the array is null). The test calls
  `TestTypeAssignableFrom(typeof(PlayerInfo))` which iterates GetProperties/
  GetFields (PlayerInfo has `string[] tags_F` + `Detail[] Details_F` fields +
  matching properties) and does `typeof(ICollection).IsAssignableFrom(
  property/field.FieldType)`. The null array is an internal framework reflection
  array returned for an IL-array property/field type (`string[]`/`Detail[]`).
  NOT a clean ILRuntimeType fix. DEEP.

## PINNED: UnitTest_TestFCP root (DEEP-diagnosed this child, the #4 priority)

ToColor("#FF00FF00") returns `new TestVector3NoBinding(num1, num3, num4)` =
expected (1,1,0); the caller receives (1,0,0) (only x lands). Isolation probes
(TestCases/NeoStepRecluster13Probe.cs TC1-TC7) PROVED:
- TC1/TC2: `new TestVector3NoBinding(1f,2f,3f)` INTO A LOCAL (initobj + call.ctor)
  works -> the reflection struct-ctor path is correct for literal args.
- TC3: `Convert.ToInt64("FF"/"00"/"FF",16)` 3x -> 255,0,255. Works (not a stale
  autogen stub).
- TC4: `(float)ToInt64/255` divi.r4 chain 3x -> 1,0,1. Works (divi.r4 itself is
  fine; the empty JIT-dump operands are just a printer gap).
- TC5/TC6: the FULL ToColor body (StartsWith/Remove + branch + ctor into a local)
  works -> the bug is NOT in ToColor's method body when checked in-place.
- TC7: `return new TestVector3NoBinding(a,b,c)` (as-value newobj, literal args)
  FAILED on HEAD (caller got (0,0,0)) -> a CLR-struct newobj via the REFLECTION
  fallback stored a REFERENCE (boxed index) instead of flat bytes. **FIXED this
  child** (see Ship section). TC7 PASSES after the fix.
- TC8 (removed): `return new TestVector3NoBinding(num1,num3,num4)` (as-value
  newobj, divi.r4 args) STILL FAILS after the fix -> the residual is the struct
  RETURN path (move/ret), not the newobj retDst.

Runtime instrumentation (since removed) on the REAL ToColor path (call.ctor into
local, NOT newobj) proved:
1. The ctor RECEIVES correct args: a=1, b=1, c=0 (the divi.r4 results ARE correct
   at the call boundary -- NOT an arg-passing bug).
2. Post-Invoke, targetBase[0..12] = (1,1,0) (the reflection box-mutation +
   WriteNeoValueType write-back is CORRECT).
3. CopyNeoCallThisBack copies 12 bytes (1,1,0) to the caller offset 56 (r12) --
   CORRECT size + offset.
4. BUT the caller's `color` = (1,0,0). The y/z loss happens AFTER step 3, in
   ToColor's `46:move r13, r12; 53:ret r13` sequence.

ROOT (the remaining UnitTest_TestFCP gap): ToColor's lowering ends with
`move r13, r12` (a PLAIN `OpCodeREnum.Move`, NOT `Move_Vt`) before `ret r13`.
The Neo `Move` arm copies `ip->Operand2` bytes (`Unsafe.CopyBlock(...,
(uint)ip->Operand2)`, Neo.cs:2086). For a 12-byte struct return the JIT must
stamp Operand2=12 (or emit Move_Vt); in ToColor's register-allocation context
it copies only the leading 4 bytes (x=1), leaving r13.y/r13.z as stale residue
(0,0) -> `ret r13` returns (1,0,0). This is the SAME "Neo untyped frame needs
JIT-time struct-size facts" theme as child-11/16/21/24/29 + the il-struct-box
curVtTypes tracker: the Move for a value-type-typed register must carry the full
struct size. The fix is in the JIT Move-emission / register-type tracking
(stamp Operand2 = the VT's primitive size for a Move whose src/dst is a value
type, or rewrite to Move_Vt), NOT in the runtime Move arm. TC7 (newobj return)
works because the newobj dest is written directly by InvokeNeoClrMethod (no
intervening Move); TC8/ToColor fail because they route through `move r;ret r`.
NOTE: TC1/TC5/TC6 (ctor into a local, in-place check) do NOT hit this because
they read the local directly (no move-before-ret). Candidate child:
`neo-vt-return-move-size` (JIT Move size for value-type registers).

## Recommended next-batch priority (the 13, by coverage / confidence / THIS CHILD's pinning)
ALL 13 are DEEP singletons (the shallow surface is exhausted -- verified by 49
prior wave-2 children). Ordered by a mix of coverage + fix-confidence + the new
pinning:
1. **UnitTest_TestFCP** (PINNED this child): JIT `Move` size for a value-type
   register in the `move r;ret r` return sequence. The MOST precisely-diagnosed
   root now; a focused JIT child. Could also affect StructTest6/StructTest12 if
   they share the vt-return/vt-move path (re-audit).
2. RegisterVMTest04 (re-pinned ground-16: >3-arg virtual-IL-call param-map /
   frame-offset through a generic instance). The cleanest DEEP call-marshalling
   child.
3. MyTest.Test (boxed-CLR-struct enumerator interface dispatch, Step-19).
4. StructTest6 (byref out-STRUCT write-back -- the out-direction sibling of
   il-struct-box).
5. UnitTest_TestInline01 (inlined-call reference-arg aliasing -- JIT-level Neo
   frame-layout fix for inlined calls).
6. ReflectionTest14 (null internal reflection array for an IL-array field type).
7. StaticTest05 / StructTest12 -- each a DISTINCT deep root.
8. The Cluster A grab-bag (UnitTest_TestStackRegisterTransition3 /
   ReflectionTest25 / UnitTest_10046 / UnitTest_10051 / DelegateTest42) -- each
   needs its own JIT-dump triage child. DelegateTest42 + UnitTest_10046 +
   UnitTest_10051 + MyTest.Test all touch Step-19 delegate/enumerator dispatch
   (4 tests, possibly a shared Step-19 arg-marshalling root -- worth a re-audit
   for a multi-test flip).

## Lesson reaffirmed (the 12-for-12 / 17-for-17 lineage)
Every prior "foundational gap" verdict was disproven on re-audit. UnitTest_TestFCP
here looked like "struct-ctor arg loss" (ground-15's framing) but was THREE
distinct bugs stacked: (a) the reflection struct-ctor path is FINE (TC1), (b)
the as-value newobj retDst wrote a reference not flat bytes (FIXED, TC7), (c) the
residual is a JIT Move-size issue in the return sequence (PINNED, needs its own
child). Trace the PRODUCER chain to the byte, not the throwing reader.

## Ship (this child, NOT committed -- LEAD commits)
- FIX (InvokeNeoClrMethod isNewobj branch, ILIntepreter.Neo.cs ~line 1344): a CLR
  value-type newobj routed through the reflection fallback (no redirect -- e.g. a
  struct with no ValueTypeBinder ctor) now writes the struct's FLAT managed bytes
  to the dest register via WriteNeoValueType (mirrors the struct-return store +
  the autogen Ctor_Neo !isNewObj write-back), instead of storing a boxed-
  reference index. The reference-index path is kept for reference-type newobj.
  This is the reflection-fallback analog of `neo-clr-struct-newobj-retdest-null`
  (which fixed the autogen-stub Call_Redirect path). Neo-gated -> Legacy-neutral.
  Proven by TC7 (struct newobj return with literal args: HEAD (0,0,0) -> fixed
  (1,2,3)). Does NOT reduce the 13 (none of the 13 use a CLR-struct reflection
  newobj; UnitTest_TestFCP uses call.ctor-into-local, hitting the SEPARATE
  Move/ret gap above), but is a real latent correctness fix.
- PROBE (TestCases/NeoStepRecluster13Probe.cs, TC1-TC7): permanent isolation +
  regression probes. TC1-TC6 document that the struct ctor / Convert.ToInt64 /
  divi.r4 / ToColor-body work in isolation; TC7 is the load-bearing regression
  probe for the newobj fix. TC8 removed (the unfixed Move/ret gap, documented
  here for the next child).
