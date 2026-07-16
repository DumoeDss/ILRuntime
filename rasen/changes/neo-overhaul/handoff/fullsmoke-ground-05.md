# Full Neo Smoke Grounding -- 2026-07-16 FRESH re-cluster (5 -> 4; RegisterVMTest04 FIXED)

## STATUS: FRESH grounding (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch).
Full smoke is **4 failed** after this child (was 5 on entry).
`Ran 948 tests, 4 failded, 20 ignored, 7 todos` (exit 127 = known graceful
Dict-NRE crash; summary emitted). This child FRESH-ran the smoke, re-clustered
the CURRENT 5, deep-diagnosed the most tractable singleton (RegisterVMTest04),
PINNED the root cause (interface/abstract IL-callee param-layout alignment
mismatch), shipped a verified-correct fix, and confirmed the drop by re-running
the full smoke. NeoStep **414/0** (no regression). Legacy-neutral (plain Debug
build = 0 errors; the fix is `#if ENABLE_NEO_MODE`-gated, file-gated).

Build this child: CLI `Debug_Neo --no-incremental -p:UseSharedCompilation=false`
(0 errors), TestCases `Debug -p:UseSharedCompilation=false` (0 errors). After
touching ILRuntimeTestBase: kill `dotnet` build-server + `-p:UseSharedCompilation=false`.

## Fresh ground (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch)
- PRE-this-child (ground-06 baseline):  `Ran 948 tests, 5 failded, 20 ignored, 7 todos`.
- POST-fix (this child):                  `Ran 948 tests, 4 failded, 20 ignored, 7 todos`.
- Delta: **-1 (RegisterVMTest04 flipped green)**. The 4 survivors are a STRICT
  SUBSET of the entry 5 (no new failures, no regressions).
- NeoStep: 414/0 (no regression throughout).

## The CURRENT 5 (entry) -> pinned root + verdict (1 FIXED this child, 4 remain)

The 5 = ground-06's set MINUS UnitTest_TestStackRegisterTransition3 (fixed in a
prior child, hence ground-06's "6 -> 5"). Each was re-audited FRESH this child.

### RegisterVMTest04 -- FIXED this child (interface/abstract IL-callee param alignment)
- **Symptom:** `ArgumentOutOfRangeException @ List.get_Item` <- ExecuteNeo
  Stfld_Ref (Neo.cs:5320). Dump: `stfld.ref r0, r12, 0x00000000, 268435971(0,0)`
  -- stfld.ref reads the `action` param's slot as a ref index 0x10000013 (garbage).
  `viewRectEvent = action` inside `ILScrollRect2<T>.SetViewRect` (a virtual
  override, `[ILRuntimeJIT(NoJIT)]`, 4 params + this = 5 args; >3-arg threshold;
  default `action=null`). The C# comment in RegisterVMTest.cs confirms removing
  the `Action action = null` default makes the error stop.
- **ROOT PINNED (CopyNeoCallArguments diagnostic, JIT-dump-confirmed):** the
  callvirt.il static target is the ABSTRACT base `ILScrollRect2.SetViewRect`
  (no compiled frame), so the optimizer synthesizes the callee param layout via
  `AllocNeoParamInfosFromSignature` -> `AllocateNeoCallParamSlot`. That helper
  does NOT apply natural alignment (it is shared with the CLR-callee branch,
  whose autogen ReadNeo* reader expects a CONTIGUOUS no-alignment layout). The
  CONCRETE IL impl's frame is built by `JITCompiler.AllocateSlotForType`, which
  DOES apply natural alignment per slot. For the signature
  `(enum type, bool isAnim, bool isJudgeEmpty, Action action)`:
    - synthesized (no align): bools packed at 1 byte -> Action at prim offset **10**.
    - concrete (aligned): Action reference aligns to 4 -> Action at prim offset **12**.
  CopyNeoCallArguments wrote the Action arg (-1 = null sentinel) to offset 10,
  but the callee read offset 12 (stale frame residue 0x10000013) -> OOB. Diagnostic:
  `prim[4] srcOff=44 dstOff=10 size=4 srcVal=0xFFFFFFFF dstVal=0xFFFFFFFF` (before
  fix); `dstOff=12` (after fix).
- **THE FIX (Optimizer.Neo.cs, +52/-6, Neo-gated -> Legacy-neutral):** new helper
  `AllocateNeoIlCalleeParamSlot` applies the SAME per-slot natural alignment as
  `AllocateSlotForType` (byref=4, primitive=GetPrimitiveSize, IL-VT/IL-enum=
  NaturalAlignment, else 4) BEFORE sizing via `AllocateNeoCallParamSlot`;
  `AllocNeoParamInfosFromSignature` routes both the `this` slot and each param
  through it. The CLR-callee branch is UNCHANGED (it still calls
  `AllocateNeoCallParamSlot` directly, preserving the contiguous autogen layout).
  This is the Step-11 interface-dispatch param-layout alignment gap (a sibling
  of the C7 LowerNeoOffsets param-map work, but in the map's callee-layout sizer,
  not the Push-deletion path).
- **Verify:** stash-toggle airtight -- stash Optimizer.Neo.cs ONLY -> rebuild ->
  RegisterVMTest04 FAILS (1 failded, exit 127) -> pop -> rebuild -> PASS (0/0).
  Full smoke 5 -> 4. NeoStep 414/0. Legacy-neutral.

### StructTest6 -- REMAINS (byref out-STRUCT ref-region write-back)
- **Structs.cs:262.** `Dictionary.TryGetValue(strId, out cube)`; cube = StructTest
  {object objAsset; string type} = 0 prim + 2 refs (a PURE-REFERENCE struct).
  Dump: `cube.type = "123"` (expected "111"; the out param was NOT written back --
  cube keeps its pre-call "123"). TryReturnValue=true (v3=False) so the call
  succeeded but the out struct was not propagated.
- **DEEP (architectural):** the out param's dest is sized as a single ILTypeInstance
  reference; the reflection write-back stores the result ILTypeInstance's mStack
  index at the dest prim slot, but cube's 2 fields live in the REF region which is
  never touched. The byref encoding must carry the struct's ref base so the
  reflection out-param write-back propagates the ref slots; the 0-prim+2-ref shape
  has NO prim slot to receive the index -- the write-back target shape is
  fundamentally wrong. (Sibling of the FIXED UnitTest_StaticTest05 but for a
  pure-reference struct -- the ref-region half, which the pure-primitive Vector3
  case did not exercise.)

### StructTest12 -- REMAINS (Activator.CreateInstance<T> generic-param mis-resolution)
- **Structs.cs:396.** `T ins = new T() { i = 10 }` where T : struct, ITestStruct
  (T = MyStruct2). Dump: `ins.i = 1` (expected 10).
- **ROOT PINNED via JIT dump (confirmed still present):** Roslyn lowers `new T()`
  (generic struct T) to `call Activator.CreateInstance<T>()`, and the JIT resolves
  the generic param T to **ILTypeInstance** for the Activator call (JIT dump:
  `call.redirect r1, System.Activator::ILTypeInstance CreateInstance[ILTypeInstance]()`)
  while `initobj r0, MyStruct2` in the SAME method correctly resolves T to
  MyStruct2 -- inconsistent generic-param resolution. The Activator result is a
  bare ILTypeInstance whose mStack index (1) ends up in the struct local `ins`;
  `ins.i` reads ins's first 4 bytes = the mStack index -> "1".
- **DEEP (JIT generic-param resolution under a generic-method context):**
  `AppDomain.GetMethod` marks the Activator call's T `ContainsGenericParameter` ->
  the fallback resolves T to ILTypeInstance. child-22's CreateInstanceNeo redirect
  does NOT fix this (the generic arg is mis-resolved to ILTypeInstance BEFORE the
  redirect body runs; CreateInstanceNeo reads method.GenericArguments[0] =
  ILTypeInstance). The `constrained MyStruct2` token is RIGHT THERE in the IL, so
  the JIT knows T=MyStruct2 -- the substitution for the Activator call's type arg
  is the gap. Even with correct resolution, the redirect returns a heap
  ILTypeInstance, not a struct-by-value -- two coupled bugs. Candidate future child
  = JIT generic-param propagation into Activator.CreateInstance<T> inside a generic
  method + a struct-returning CreateInstance path.

### UnitTest_10051 -- REMAINS (constrained-callvirt property read on a nested struct field)
- **TestValueTypeBinding.cs:619.** `list[0].V2.x.RawValue != 999`. V2 is a property
  get returning Fixed64Vector2 (struct) by value; `.x` reads a Fixed64 struct
  field; `.RawValue` is a property read on Fixed64. A constrained-callvirt property
  read on a nested struct field (the `.x.RawValue` chain).
- **DEEP:** progresses PAST the F-10 NIE (child F-10: the SetPos WRITE works); the
  residual is the struct-field-property-read chain. Candidate future child =
  `neo-struct-field-property-read` (same family as the nested-ldflda-on-byref gap
  child-27 surfaced, but for the constrained-callvirt property-getter form).

### MyTest.Test -- REMAINS (boxed-CLR-struct enumerator interface dispatch, Step-19)
- **Test01.cs:618.** `InvalidCastException: String -> IEnumerator<KVP<int,int>>`
  at autogen `get_Current_0_Neo:48`. A Dictionary enumerator `this` is mis-
  marshalled (a String ends up in the `this` slot on a later loop iteration).
  Dump shows `IEnumerator e = ` (empty) and the callvirt get_Current at IL_0044.
- **DEEP (Step-19 delegate/enumerator family):** boxed-CLR-struct enumerator
  interface dispatch with this-register aliasing across loop iterations. The
  enumerator struct is boxed; its IEnumerator get_Current's `this` slot holds a
  String on iteration N>0. Candidate future child = Step-19 boxed-enumerator
  this-marshalling.

## The fix this child (NOT committed, LEAD commits)
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` (+52/-6):
  `NeoAlignUp` + `AllocateNeoIlCalleeParamSlot` (mirrors
  `JITCompiler.AllocateSlotForType` natural alignment) + `AllocNeoParamInfosFromSignature`
  routes `this` + each param through it. Neo-gated (file is `#if ENABLE_NEO_MODE`)
  -> Legacy-neutral by construction. NO change to ILIntepreter.Neo.cs (diagnostics
  added during investigation were fully removed; `git diff` clean on that file).
- `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-05.md` (THIS file).

## Remaining 4 (reported honestly -- each a distinct deep singleton, re-confirmed)
- **StructTest6** -- byref out-STRUCT REF-REGION write-back (0-prim+2-ref struct;
  the write-back must propagate ref slots, not store an index in a non-existent
  prim slot). Architectural.
- **StructTest12** -- JIT generic-param resolution for `Activator.CreateInstance<T>`
  in a generic method (T resolves to ILTypeInstance instead of the enclosing T) +
  the redirect returns a heap ILTypeInstance not a struct (two coupled bugs).
- **UnitTest_10051** -- constrained-callvirt property read on a nested struct
  field (`.x.RawValue` chain).
- **MyTest.Test** -- boxed-CLR-struct enumerator interface dispatch (Step-19,
  this-register aliasing across loop iterations).

## Lesson
The 12-for-12 "re-audit a DEEP verdict" discipline held AGAIN and yielded a fix:
RegisterVMTest04 was filed DEEP in ground-06 (">3-arg virtual-IL-call param map
through a generic instance"), but a focused CopyNeoCallArguments diagnostic
DISPROVED the ">3-arg / generic-instance / calling-convention" framing and pinned
a NARROW, single-root cause -- the synthesized interface/abstract IL-callee param
layout lacked natural alignment, diverging from the concrete impl's frame ONLY at
a bool->reference boundary. The defect is NOT about >3 args (the 5-arg Push map
was correct) NOR about the generic instance (the signature has no T params); it is
the Step-11 interface/abstract-callee layout sizer omitting alignment. The
diagnostic was decisive: `prim[4] dstOff=10` (synthesized) vs the concrete reading
offset 12. When a "param holds garbage" symptom appears on a virtual/interface IL
call, instrument CopyNeoCallArguments to dump the per-param dstOff + dstVal and
compare against the concrete frame's AllocateSlotForType offsets -- an unaligned
synthesis is the first suspect. The fix mirrors the concrete sizer exactly and is
additive (AlignUp of an already-aligned offset is a no-op), so it cannot regress
the already-aligned interface-dispatch cases.
