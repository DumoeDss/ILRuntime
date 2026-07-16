# Full Neo Smoke Grounding -- 2026-07-16 FRESH re-cluster (6 failing, STEADY; no tractable singleton this child)

## STATUS: FRESH grounding (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch).
Full smoke is **6 failed** on entry AND after this child's investigation
(`Ran 948 tests, 6 failded, 20 ignored, 7 todos`, exit 127 = known graceful
Dict-NRE crash; summary emitted). This child FRESH-ran the smoke, re-clustered
the CURRENT 6, deep-diagnosed the most tractable singleton
(UnitTest_TestStackRegisterTransition3), PROVED a real missing piece (the
by-value IL-struct ref-region call-boundary copy) and shipped a verified-correct
fix for it -- but the test STILL fails for an UNRESOLVED additional reason (all
struct-field values verified correct at callee entry + the ref-field read
verified correct via ldfld.ref.inline, yet the line-339 assertion still throws).
The fix was VERIFIED NEUTRAL on the full smoke (6 -> 6, same set, no regression)
but did not drop the count, so it was REVERTED to keep the tree clean. NeoStep
**414/0** (no regression). Legacy-neutral (no source changes shipped -- all
experimental edits reverted; `git diff` clean on ILIntepreter.Neo.cs).

Build this child: CLI `Debug_Neo --no-incremental -p:UseSharedCompilation=false`
(0 errors), TestCases `Debug -p:UseSharedCompilation=false` (0 errors).

## Fresh ground (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch)
- PRE-this-child:  `Ran 948 tests, 6 failded, 20 ignored, 7 todos`.
- POST-investigation (experimental fix reverted): `Ran 948 tests, 6 failded, 20 ignored, 7 todos`.
- NeoStep: 414/0 (no regression throughout).

## The CURRENT 6 (re-confirmed FRESH, clustered by defect class + pinned root)

The same 6 as ground-07 (ground-07 fixed UnitTest_StaticTest05, leaving these 6).
Each was re-audited this child. All 6 remain DEEP singletons (the 12-for-12
"DEEP verdict can be disproven" lesson did NOT surface a tractable subset this
time -- re-audit CONFIRMED deep with SPECIFIC multi-mechanism evidence for each).

### Cluster A -- byref/ref struct write-back to a struct location (1 test)
- **StructTests.StructTest6 (Structs.cs:262)** -- `Dictionary.TryGetValue(strId,
  out cube)`; cube = StructTest {object objAsset; string type} = 0 prim + 2 refs
  (a PURE-REFERENCE struct). Dump: `cube.type = "123"` (expected "111"; the out
  param was NOT written back). TryGetValue is the reflection fallback. The out
  param's dest is sized as a single ILTypeInstance reference; the reflection
  write-back stores the result ILTypeInstance's mStack index at the dest prim
  slot, but cube's 2 fields live in the REF region which is never touched. DEEP
  (the byref encoding must carry the struct's ref base so the reflection out-
  param write-back propagates the ref slots; the 0-prim+2-ref shape has NO prim
  slot to receive the index -- the write-back target shape is fundamentally
  wrong). Sibling of the FIXED UnitTest_StaticTest05 but for a pure-reference
  struct (the ref-region half, which the pure-primitive Vector3 case did not
  exercise).

### Cluster B -- constrained-callvirt on a struct / generic-struct (2 tests)
- **StructTests.StructTest12 (Structs.cs:396)** -- `T ins = new T() { i = 10 }`
  where T : struct, ITestStruct (T = MyStruct2). Dump: `ins.i = 1` (expected 10).
  ROOT PINNED via JIT dump (confirmed still present post-child-22): Roslyn lowers
  `new T()` (generic struct T) to `call Activator.CreateInstance<T>()`, and the
  JIT resolves the generic param T to **ILTypeInstance** for the Activator call's
  type arg (JIT dump: `call.redirect r3, System.Activator::ILTypeInstance
  CreateInstance[ILTypeInstance]()`) while `initobj r0, MyStruct2` in the SAME
  method correctly resolves T to MyStruct2 -- inconsistent generic-param
  resolution. The Activator result is a bare ILTypeInstance whose mStack index
  (1) is stored in the struct local `ins`; the later get_i reads `ins`'s first 4
  bytes = the mStack index -> "1". NOTE: child-22's CreateInstanceNeo redirect
  does NOT fix this (the generic arg is mis-resolved to ILTypeInstance BEFORE the
  redirect body runs; CreateInstanceNeo reads method.GenericArguments[0] =
  ILTypeInstance). DEEP (JIT generic-param resolution for Activator in a generic
  method; AppDomain.GetMethod:2236 marks ContainsGenericParameter -> the fallback
  resolves T to ILTypeInstance; AND even with correct resolution, the redirect
  returns a heap ILTypeInstance, not a struct-by-value -- two coupled bugs).
- **TestValueTypeBinding.UnitTest_10051 (TestValueTypeBinding.cs:619)** --
  `list[0].V2.x.RawValue != 999`. V2 is a property get returning Fixed64Vector2
  (struct) by value; `.x` reads a Fixed64 struct field; `.RawValue` is a property
  read on Fixed64. A constrained-callvirt property read on a nested struct field.
  Progressed PAST the F-10 NIE (child F-10); the residual is the
  struct-field-property-read chain. DEEP.

### Cluster C -- call marshalling through a generic instance / register transition (2 tests)
- **RegisterVMTest.RegisterVMTest04 (RegisterVMTest.cs:107)** --
  `ArgumentOutOfRangeException @ List.get_Item` <- ExecuteNeo stfld.ref
  (Neo.cs:5253). Dump: `stfld.ref r0, r12, 0x00000000, 268435971(0,0)` -- stfld.ref
  reads a ref index of 268435971 (0x10000013, garbage). `viewRectEvent = action`
  inside `ILScrollRect2<T>.SetViewRect` (override in a GENERIC INSTANCE
  ILScrollRect2<ScrollItem2>, `[ILRuntimeJIT(NoJIT)]`, called virtually with >3
  args + default `action=null`; the C# source comment confirms: removing the
  `Action action = null` default makes the error stop -> the >3-arg threshold is
  the trigger). The `action` param's frame slot holds garbage instead of null.
  DEEP (>3-arg virtual-IL-call param map / calling convention through a generic
  instance; the overflow-Push arg layout mis-routes the last register arg).
- **ExpTest_20.UnitTest_TestStackRegisterTransition3 (LightTester2.cs:356)** --
  `System.Exception` (test assertion at line 339). TransitionTest struct {int A;
  string B; float C; TransitionTestSub D} (16 prim + 2 refs) passed BY VALUE to
  TransitionTest2.Test(TransitionTest arg). DEEP -- INVESTIGATED THIS CHILD (see
  below): TWO coupled gaps (ref-region call-boundary copy + an UNRESOLVED
  second issue where all values are verified correct yet the assertion throws).

### Cluster D -- boxed-CLR-struct enumerator interface dispatch (1 test)
- **MyTest.Test (Test01.cs:618)** -- `InvalidCastException: String -> IEnumerator<
  KVP<int,int>>` at autogen `get_Current_0_Neo:48`. A Dictionary enumerator `this`
  is mis-marshalled (a String ends up in the `this` slot on a later loop
  iteration). Step-19 / boxed-CLR-struct enumerator interface dispatch (this-
  register aliasing across loop iterations). DEEP (Step-19 delegate/enumerator
  family).

## Deep investigation this child: UnitTest_TestStackRegisterTransition3 (the most
## tractable-looking singleton -- DISPROVEN tractable, two coupled bugs)

### What was PROVEN (a real, verified-correct missing piece)
A by-value IL-struct param whose value type has reference fields (TransitionTest
= 16 prim + 2 refs) stores those refs in the REF region (mStack[frameRefBase +
refOff], per Stfld_Ref_Inline at ILIntepreter.Neo.cs:5893-5896), NOT in the prim
bytes that CopyNeoCallArguments CopyBlock'd. The optimizer BUILDS map.RefSrc/
RefDst for every ref-bearing param slot (Optimizer.Neo.cs:1543-1547, gated on
dstInfo.RefCount > 0), but CopyNeoCallArguments (ILIntepreter.Neo.cs:405-484)
NEVER consumes them -- it only does the prim CopyBlock. So the callee's arg.<ref
field> read mStack at its own (zeroed, reserved-at-entry) slot -> null. This is a
GENUINE gap (verified: map.RefSrc.Length=3 for the Test call; the callee's
ParamInfos[arg].RefOffset=1 matches map.RefDst exactly).

A fix was implemented + verified correct: pre-reserve the IL callee's whole ref
region in the caller (after F-7B promotion), shallow-copy each ref entry
(map.RefSrc -> map.RefDst), and hand the pre-reserved base to ExecuteNeo so it
skips its own reservation. Diagnostics PROVED the copy lands correctly:
- callee entry: frameRefBase=11, slot[12]=String ("2" = arg.B) -- the ref copy
  works; map.RefDst matches ParamInfos.RefOffset exactly.
- arg.A (prim) at esp+4 = 1, arg.C (prim) at esp+8 = 1077936128 = 3.0f.
- the callee's Ldfld_Ref_Inline for arg.B: ip->Operand=1, srcSlot=12, obj=String
  ("2") -- the inline read is CORRECT.

### Why the test STILL failed (the UNRESOLVED second issue -- a paradox)
Despite ALL THREE field values verified correct at callee entry AND arg.B's
ldfld.ref.inline read verified correct (Operand=1 -> slot 12 -> "2"), the line-
339 assertion `arg.A != 1 || arg.B != "2" || arg.C != 3` STILL throws. The fix
was verified NEUTRAL on the full smoke (6 -> 6, identical set, NO regression --
the pre-reservation is layout-neutral: frameRefBase is the same position whether
reserved by the caller or the callee; only the previously-null ref slots get
populated, which is harmless for reference params that read via the prim-byte
mStack index). Because the fix did not drop the count and the paradox could not
be resolved, it was REVERTED. The unresolved second issue is likely in the
String.op_Inequality CLR-call marshalling for the arg.B != "2" comparison, OR a
subtle float-comparison / control-flow detail in the arg.C check -- but extensive
diagnostics (entry values + the ldfld.ref.inline read) could not pin it. This is
a candidate for a future child that instruments the COMPARISON path (the
op_Inequality CopyNeoCallArguments + ReadNeoReference, and the ceqi.r4 float
fold), NOT just the field-read path.

### NOTE for the future child: the ref-region call-boundary copy IS needed
Whoever tackles TransitionTest3 should re-apply the ref-region copy (it is
verified correct + layout-neutral) AND then chase the second issue. The copy
alone is insufficient but necessary. The blast radius is "every IL call with
ref-bearing params" (most calls) but it is layout-neutral (frameRefBase position
unchanged); the full-smoke 6->6 neutral result confirms no regression.

## Remaining 6 (reported honestly -- each a distinct deep singleton, re-confirmed)
- **StructTest6** -- byref out-struct REF-REGION write-back (0-prim+2-ref struct;
  the write-back must propagate ref slots, not store an index in a non-existent
  prim slot).
- **StructTest12** -- JIT generic-param resolution for `Activator.CreateInstance<T>`
  in a generic method (T resolves to ILTypeInstance instead of the enclosing T) +
  the redirect returns a heap ILTypeInstance not a struct (two coupled bugs).
- **UnitTest_10051** -- constrained-callvirt property read on a nested struct
  field (`.x.RawValue` chain).
- **RegisterVMTest04** -- >3-arg virtual-IL-call param map through a generic
  instance (default-value `action=null` slot holds garbage 0x10000013).
- **UnitTest_TestStackRegisterTransition3** -- struct-by-value param to an IL
  callee: ref-region call-boundary copy (PROVEN missing + fix verified correct,
  this child) + an UNRESOLVED second issue (all values verified correct yet the
  line-339 assertion still throws -- likely in the op_Inequality comparison
  marshalling).
- **MyTest.Test** -- boxed-CLR-struct enumerator interface dispatch (Step-19,
  this-register aliasing across loop iterations).

## Lesson
The 12-for-12 "re-audit a DEEP verdict" discipline held but did NOT yield a fix
this child: re-auditing UnitTest_TestStackRegisterTransition3 DID surface a real,
verified-correct missing piece (the ref-region call-boundary copy) -- but the
test has a SECOND, unresolved issue that the copy alone cannot reach. A "correct
partial fix that does not drop the count" is NOT a success per the mandate
("truth = full-smoke number"); it was reverted. The honest signal: when extensive
diagnostics prove all values are correct at the read site yet an assertion still
throws, the bug is DOWNSTREAM of the read (in the comparison / call-marshall /
control-flow path), and the field-read fix -- however correct -- will be neutral.
Instrument the COMPARISON path next, not the read path. The wave has reached the
point where the remaining singletons each need a per-root deep child that fixes
ALL coupled bugs in one shot, not one at a time.

## Files this child (NOT committed, LEAD commits)
- Experimental edits to `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`
  were ALL REVERTED (`git diff` clean) -- the ref-region copy + ExecuteNeo
  pre-reservation + InvokeNeoCallTarget passthrough were verified neutral then
  removed to keep the tree clean (no measurable benefit).
- `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-06.md` (THIS file).
