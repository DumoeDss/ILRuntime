# Full Neo Smoke Grounding -- 2026-07-16 FRESH re-cluster (9 failing)

## STATUS: FRESH grounding (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch).
Full smoke **9 failed**: `Ran 948 tests, 9 failded, 20 ignored, 7 todos` (exit 127;
known graceful Dict-NRE crash; summary emitted). NeoStep **414/0** (no regression
vs ground-10). Legacy-neutral build clean.

Build this child: CLI `Debug_Neo --no-incremental -p:UseSharedCompilation=false`
(0 errors), TestCases `Debug -p:UseSharedCompilation=false` (0 errors). Build-server
shutdown first (child-25 gotcha).

This child FRESH-ran the smoke, re-clustered the CURRENT 9, and DEEP-diagnosed the
most tractable candidates to byte/IL-level roots. The 9 are a STRICT SUBSET of
ground-10's 9 (UnitTest_TestInline01 was fixed by recluster-10 and stays fixed).
**All 9 surviving are DISTINCT DEEP singletons** -- none yielded a contained,
verifiable fix in this budget. Honest report below; each row has a pinned/presumed
root + a fix-scope verdict for the next dedicated child.

The wave has exhausted the mechanical quick-win surface (the neo-remaining-34-batch
lesson reaffirmed): further gains need per-root deep children. The 9 cluster into 5
defect classes, each already worked by prior children but with one residual shape
each.

## Fresh ground (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch)
`Ran 948 tests, 9 failded, 20 ignored, 7 todos` (exit 127). NeoStep 414/0.

## The CURRENT 9, clustered by defect class + per-test pinned root (fresh run)

### Cluster A -- byref/ref struct write-back to a struct location (2 tests)
Both: a byref to an IL-struct location is passed to a method; the (possibly-mutated)
struct is NOT written back to the caller's struct fields. The Neo byref encoding
`(-1, primOffset)` (ldloca, ILIntepreter.Neo.cs:2163-2164) carries ONLY the primitive
byte offset -- the struct's reference-region mStack base is NOT in the byref.
CopyNeoCallThisBack objIdx==-1 (Neo.cs:797-801) does a flat `CopyBlock` of
`PrimitiveSize[i]` bytes, so the ref region is never propagated. The Constrained
direct-call path (Neo.cs:7410-7424) ALREADY recovers the ref base via a localInfos
scan -- the SAME recovery is needed in the byref write-back.

- **StructTests.StructTest6 (Structs.cs:262)** -- `Dictionary.TryGetValue(strId, out
  cube)` where cube = StructTest {object objAsset; string type} = 0 prim + 2 refs.
  Dump: `cube = {objAsset=null, type="123"}` (expected type="111"; the out param was
  NOT written back -- cube stayed at its pre-call value). TryGetValue is the
  reflection fallback (no autogen Dictionary_2_String_StructTest binding). The out
  param's dest is sized as ILTypeInstance (4 prim + 1 ref, a single reference); the
  reflection write-back (CLRMethod.cs:704-716) stores the result ILTypeInstance's
  mStack index at the dest prim slot; CopyNeoCallThisBack copies those 4 bytes to
  cubePrimOffset -- but cube has 0 prim bytes and its 2 fields live in the REF
  region (frameRefBase+cubeRefOffset), which is never touched. DEEP/LARGE (needs
  localInfos-threaded unbox in CopyNeoCallThisBack, or a representation rethink so
  an out-IL-struct byref carries its ref base).
- **StaticTest.UnitTest_StaticTest05 (StaticTest.cs:106)** -- `ref testVal2` (testVal2
  = static IL-struct Vector3 = TestCases.Vector3, pure-primitive 3-field struct) to
  UnitTest_StaticTest05Sub which does `i = Vector3.Zero`. Dump: `v0=True`
  (testVal2.x != 0 after the call; write-back to the static field lost). This is the
  IL-static `ldsflda` byref path (neo-nested-ldflda-byref handoff explicitly DEFERRED
  "Neo Ldsflda: CLR static field address deferred"; this is the IL-static sibling).
  The ldsflda-produced byref for an IL static must feed the raw Stfld/Ldfld consumers
  AND the byref write-back. DEEP.

### Cluster B -- constrained-callvirt on a struct / generic-struct (2 tests)
Both involve a constrained-callvirt property get/set on a struct field/param.

- **StructTests.StructTest12 (Structs.cs:400)** -- `T ins = new T() { i = 10 }` where
  T : struct, ITestStruct (T = MyStruct2 {int i {get;set;}}). Dump: `ins.i = 1`
  (expected 10). ROOT PINNED via JIT dump (StructTest12Sub Final, smoke log
  131486-131511): Roslyn lowers `new T() { i = 10 }` (generic struct T) to
  `call Activator.CreateInstance<T>()` in the CIL, and ILRuntime resolves the
  generic param T to **ILTypeInstance** for the Activator call's type arg (while
  `constrained T` in the SAME method correctly resolves T to MyStruct2 -- inconsistent
  generic-param resolution). So `Activator.CreateInstance<ILTypeInstance>()` returns a
  bare ILTypeInstance whose mStack index (1) is stored in the struct local `ins`; the
  later `get_i` reads `ins`'s first 4 bytes = the mStack index (1) -> prints "1".
  The set_i(10) mutation is also lost (constrained-callvirt direct-call path
  Neo.cs:7386-7434 has NO slot-0 write-back for a mutating IL-struct method -- a
  SECOND latent gap). DEEP (generic-param resolution for Activator + struct
  representation; the Activator redirect cannot emit flat struct bytes).
- **TestValueTypeBinding.UnitTest_10051 (TestValueTypeBinding.cs:619)** --
  `list[0].V2.x.RawValue != 999`. V2 is a property get returning Fixed64Vector2
  (struct) by value; `.x` reads a Fixed64 struct field; `.RawValue` is a property
  read on Fixed64. A constrained-callvirt property read on a nested struct field
  (F-10 handoff explicitly noted this residual: "reading a struct field's PROPERTY
  via `.x.RawValue` is a SEPARATE pre-existing Neo gap"). DEEP.

### Cluster C -- call marshalling through a generic instance / register transition (2 tests)
Both: arg/param marshalling corrupts a value at a call boundary.

- **RegisterVMTest.RegisterVMTest04 (RegisterVMTest.cs:107)** --
  `ArgumentOutOfRangeException @ List.get_Item` <- ExecuteNeo stfld.ref (Neo.cs:5222).
  Dump: `stfld.ref r0, r12, 0x00000000, 268435971(0,0)` -- the stfld.ref reads a
  ref index of 268435971 (0x10000013, garbage). The store `viewRectEvent = action`
  inside `ILScrollRect2<T>.SetViewRect` (override in a GENERIC INSTANCE
  ILScrollRect2<ScrollItem2>, `[ILRuntimeJIT(NoJIT)]`, called virtually with >3 args
  + default `action=null`). The `action` param's frame slot holds garbage (0x10000013)
  instead of null. DEEP (>3-arg virtual-IL-call param map / calling convention through
  a generic instance; default-value param slot not zeroed).
- **ExpTest_20.UnitTest_TestStackRegisterTransition3 (LightTester2.cs:356)** --
  `System.Exception` (test assertion). Dump: `v0=True, v1=False`. TransitionTest
  struct {int A; string B; float C; TransitionTestSub D} (16 prim + 2 refs) passed BY
  VALUE to TransitionTest2.Test(TransitionTest arg); the assertion `arg.A != 1`
  fires (v0=True) -- arg.A arrived corrupted. `[ILRuntimeJIT(NoJIT)]`. DEEP
  (struct-by-value param marshalling to an IL callee; register-transition / Move_Vt
  flat-copy of a mixed-field struct).

### Cluster D -- reflection bridge (2 tests)
Both: a System.Type / attribute reflection query returns the wrong value under Neo.

- **ReflectionTest.ReflectionTest25 (ReflectionTest.cs:680)** -- `System.Exception`
  (test assertion at :680 `attr.Parameters == null || attr.Parameters.Length != 1`).
  `[TestCLRAttribute2("ttt","Test func","ggg")]` on FuncCall2. IsDefined(mi) returns
  True (v1=True) but the constructed TestCLRAttribute2 has Parameters null / wrong
  length. The attribute ctor is `TestCLRAttribute2(string, string, params string[])`.
  ROOT: ILRuntimeMethodInfo.InitializeCustomAttribute (ILRuntimeMethodInfo.cs:38-58)
  calls `attribute.CreateInstance(at, appdomain)`; the `params string[]` ("ggg")
  ctor-arg construction from Cecil mishandles the params array (caught -> attribute
  null, OR constructed with Parameters null). DEEP (Cecil CustomAttribute ->
  CLR-instance construction bridge for params-array ctors).
- **ReflectionTest.ReflectionTest14 (ReflectionTest.cs:465)** -- NOT a reflection bug
  (investigated + DISPROVED this child). `NullReferenceException` at `ldlen`
  (Neo.cs:6126). Dump: `FieldInfo[] v5 = null`. Diagnostics PROVED `GetFields_11_Neo`
  (the autogen Neo stub for no-arg `Type.GetFields()`, System_Type_Binding.cs:587)
  returns a valid 2-element FieldInfo[] (instance=ILRuntimeType, result len=2). The
  FIRST foreach iteration runs fine (`tags_F|False` prints); the NRE is on the SECOND
  iteration's ldlen. An ldlen diagnostic showed the GetFields dest slot (r6, frame
  offset 20) holds mStack idx=5 (the FieldInfo[]) on the first ldlen, then idx=0
  (null) on the second -- i.e. **r6's slot is CLOBBERED 5->0 mid-loop-body** by one of
  the body's calls (get_FieldType / IsAssignableFrom / ToString / Concat). A separate
  IsAssignableFrom diagnostic showed the stub writes result=True but the test prints
  False -> the IsAssign return ALSO does not reach its dest cleanly. So ReflectionTest14
  is a **register-transition / frame-slot-clobbering bug in the foreach body** (same
  defect class as UnitTest_TestStackRegisterTransition3 / RegisterVMTest04), NOT a
  reflection-bridge gap. DEEP (register allocator / stack-management in loops with
  mixed call sequences).

### Cluster E -- boxed-CLR-struct enumerator interface dispatch (1 test)
- **MyTest.Test (Test01.cs:618)** -- `InvalidCastException: String -> IEnumerator<
  KVP<int,int>>` at autogen `get_Current_0_Neo:48` <- InvokeNeoClrMethod Neo.cs:1331
  <- ExecuteNeo Neo.cs:4349. A Dictionary enumerator `this` is mis-marshalled (a
  String ends up in the `this` slot on a later loop iteration). Step-19 /
  boxed-CLR-struct enumerator interface dispatch (this-register aliasing across loop
  iterations). DEEP (Step-19 delegate/enumerator family).

## Re-cluster verdict: NO batch this child (all 9 are distinct deep singletons)
Unlike recluster-10 (which had a tractable `new object()` quick-win), the CURRENT 9
have NO shared root and NO mechanical fix. Each is a distinct deep gap:
- Cluster A (2): byref-struct ref-region write-back -- the byref encoding must carry
  the struct's ref base (LARGE; representation change or localInfos-threaded write-back).
- Cluster B (2): constrained-callvirt on struct -- generic-`new T()` Activator
  mis-resolution (StructTest12) + nested-struct-field property read (UnitTest_10051).
- Cluster C (2): call marshalling -- >3-arg generic-instance virtual call
  (RegisterVMTest04) + struct-by-value to IL callee (TransitionTest3).
- Cluster D (2): reflection bridge -- params-array attribute construction
  (ReflectionTest25) + GetFields() null dispatch (ReflectionTest14, most likely to
  yield to a focused probe).
- Cluster E (1): boxed-CLR-struct enumerator dispatch (MyTest.Test, Step-19).

## Recommended next-child priority (by fix tractability + coverage)
1. **Register-transition / frame-clobbering defect class** (HIGHEST COVERAGE -- likely
   a SHARED root). ReflectionTest14 (DISPROVEN as a reflection bug this child -- it is a
   foreach-body frame-slot clobber), UnitTest_TestStackRegisterTransition3 (struct-by-
   value to IL callee), and possibly RegisterVMTest04 (>3-arg generic-instance virtual
   call) all show a value/register being CLOBBERED at a call boundary inside a loop or
   sequence of mixed calls. A dedicated child investigating the Neo register-allocator /
   loop stack-management (why a live register's frame slot gets overwritten by a body
   call's return or arg marshalling) could flip MULTIPLE tests. ReflectionTest14's
   pinned signal: GetFields dest r6 (frame offset 20) goes mStack-idx 5 -> 0 between
   the first and second ldlen, clobbered by a body call (get_FieldType / IsAssignableFrom
   / ToString / Concat). This is the cleanest lead into the allocator bug.
2. **StructTest6 + UnitTest_StaticTest05** (neo-byref-struct-writeback) -- the byref
   must carry the struct ref base; the Constrained arm's localInfos recovery is the
   template. High coverage (general byref-struct ref-region write-back gap).
3. **StructTest12** (neo-generic-newobj-activator) -- generic-param resolution for
   `new T()` -> must produce a flat struct, not Activator.CreateInstance<ILTypeInstance>.
4. **UnitTest_10051** (neo-struct-field-property-read) -- constrained-callvirt
   property read on a nested struct field.
5. **ReflectionTest25 / MyTest.Test** -- each its own dedicated deep child (attribute
   construction bridge / Step-19 enumerator dispatch).

## Verify (truth = full-smoke number)
- FULL SMOKE: **9 -> 9** (no fix shipped this child; the 9 are reported honestly as
  distinct deep singletons. A FRESH no-filter run confirmed the count: `Ran 948
  tests, 9 failded`).
- NeoStep **414/0** (no regression; nothing changed in the engine this child).
- Legacy-neutral: no engine changes this child.

## Lesson reaffirmed (12-for-12 / 17-for-17 / neo-remaining-34-batch)
The wave has exhausted the mechanical quick-win surface. The 9 survivors are ALL
distinct DEEP roots, each requiring a dedicated per-root child (byref-struct-writeback,
generic-new T, call-marshalling through generic instances, reflection bridges, Step-19
enumerator). FRESH grounding + honest reporting is the correct outcome when no
contained fix exists -- shipping an unverifiable "looks fixed" change would violate
the mandate ("No 'looks fixed'").

## Files this child (NOT committed, LEAD commits)
- `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-09.md` (THIS file -- the fresh
  re-cluster + deep-root table).
- `rasen/changes/neo-recluster-9/handoff/` (artifact dir).
