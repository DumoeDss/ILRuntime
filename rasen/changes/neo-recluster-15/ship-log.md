# neo-recluster-15 ship-log (2026-07-16)

## Outcome: GROUNDING + ROOT-PIN shipped; NO engine fix shipped (all 15 are deep
singletons; the one 2-for-1 root needs a calling-convention change beyond safe
singletons-batch scope). Smoke delta **15 -> 15** (no regression). NeoStep
**404/0**.

## Fresh grounding (Phase 1)
FRESH no-filter full smoke (Debug_Neo, rebuilt CLI + TestCases this child):
`Ran 938 tests, 15 failded, 20 ignored, 7 todos` (exit 0; no crash this run).
NeoStep **404/0**. The 15 are a STRICT SUBSET of ground-17's 17
(ReflectionTest10 fixed in recluster-17; GenericMethodTest11 fixed in
recluster-16). Cluster table written to
`rasen/changes/neo-overhaul/handoff/fullsmoke-ground-15.md`.

The 15 (exception type -> site):
- Cluster A (10 test-internal assertions, System.Exception throw@7115):
  DelegateTest42, ExpTest_20.UnitTest_TestInline01, ExpTest_20.UnitTest_TestFCP,
  ExpTest_20.UnitTest_TestStackRegisterTransition3, ReflectionTest25,
  StaticTest.UnitTest_StaticTest05, StructTests.StructTest6,
  StructTests.StructTest12, TestValueTypeBinding.UnitTest_10046,
  TestValueTypeBinding.UnitTest_10051.
- Cluster B (2 autogen-binding/upstream marshal): StructTests.StructTest11
  (IndexOOB @ List_1_ILTypeInstance_Binding.Add_0_Neo:98 <- Neo.cs:4212),
  MyTest.Test (InvalidCast String->IEnumerator @ IEnumerator_..._Binding:48 <-
  Neo.cs:4267).
- Cluster C (2 call/value materialization): Test05.TestStructDictionary (NRE @
  Neo.cs:4817 Ldfld_I4), RegisterVMTest04 (IndexOOB @ Neo.cs:5140 Stfld_Ref).
- Cluster D (1 reflection): ReflectionTest14 (NRE @ Neo.cs:6044 Ldlen).

## PINNED 2-for-1 root (StructTest11 + TestStructDictionary) -- the actionable find
BOTH are the SAME root, pinned with an instrumented diagnostic this child:
**a value-type IL struct is NOT boxed to ILTypeInstance when passed as a
reference-typed param at a Callvirt_CLR boundary.**

- StructTest11: `List<Anim>.Add(new Anim(...))`; Anim is an IL struct. The
  autogen `List_1_ILTypeInstance_Binding.Add_0_Neo` reads the item via
  ReadNeoReference; the fresh Anim is flat bytes (not boxed) -> ReadNeoReference
  reads a garbage mStack index -> AutoList OOB (List.get_Item).
- TestStructDictionary: `Dictionary<int,TestStruct>` (= `Dictionary<int,
  ILTypeInstance>`). `dicts.Add(i, def)` does NOT box the TestStruct local def
  -> the dict stores garbage -> `dicts.Values` enumerator returns null ->
  `lists.Add(null)` -> `lists[i]` returns null -> `item.id` (Ldfld_I4 heap arm)
  -> `GetNeoILInstance(mStack, 12)` (diagnostic: objIndex=12, mStack.Count=17,
  slot 12 is NULL) -> NRE. The cascade confirms the boxing is lost at the dict
  Add boundary.

Mechanism: `CopyNeoCallArguments` (ILIntepreter.Neo.cs:405-458) raw-CopyBlocks
the param bytes; the resolved CLR param type is ILTypeInstance (reference) but
the source is a value-type IL struct local/temp -> no boxing occurs. The CIL
call has NO `box` (Add's C# signature takes the struct by value; the
TestStruct->ILTypeInstance representation conversion is ILRuntime-internal, not
a CIL box). Legacy boxes implicitly via StackObject.ToObject; Neo's
ReadNeoReference does not.

FIX PATH (for the next child -- NOT shipped here): the boxing primitive already
exists (Box arm ILIntepreter.Neo.cs:4485-4505 -- `ilType.Instantiate(false)` +
`CopyFrameToIL`). Thread a per-param flag through `NeoCallParamMap`
(`JITCompiler.cs:26`) set in the optimizer's map-build (`Optimizer.Neo.cs:1365`
-- `srcInfo = localInfos[srcRegs[p]]` has the source type; `paramType`
:1311-1313 has the dest type; detect srcInfo is value-type IL struct AND
paramType is ILTypeInstance reference) and do the boxing in
`CopyNeoCallArguments` (Instantiate + CopyFrameToIL, write the ILTypeInstance
mStack index to the dest ref slot). This is a 3-site calling-convention change
-> high regression surface -> requires careful stash-toggle + full NeoStep +
full-smoke verification (per the CLAUDE.md child-history discipline). Beyond
safe singletons-batch scope for this child's budget. Expected to flip BOTH
StructTest11 and TestStructDictionary (2-for-1).

## Why no fix shipped
All 15 are deep singletons (the shallow surface is exhausted -- 48 prior
wave-2 children). The 5 non-assertion candidates were each triaged to a deep
root:
- StructTest11 + TestStructDictionary: the 2-for-1 boxing root above (deep,
  calling-convention).
- RegisterVMTest04: >3-arg virtual-IL-call param-map through a generic instance
  (deep call marshalling; re-pinned ground-16).
- MyTest.Test: boxed-CLR-struct enumerator interface dispatch (deep Step-19).
- ReflectionTest14: null internal framework reflection array for an IL-array
  property (deep reflection).
The 10 Cluster-A assertions are each a distinct value-corruption root needing
individual JIT-dump triage children.

## Verify
- Fresh full smoke: `Ran 938 tests, 15 failded` (exit 0). Baseline log
  `.tmp-r15-baseline.log`; per-test traces `.tmp-st11.log` / `.tmp-tsd.log` /
  `.tmp-one.log`.
- NeoStep: `Ran 404 tests, 0 failded` (no regression; source reverted to clean
  after the instrumented diagnostic).
- Legacy-neutral: N/A (no engine change shipped; source tree matches HEAD).
- Source diff vs HEAD: empty (the instrumented GetNeoILInstance NRE-message
  diagnostic was added then reverted; tree clean).

## Files this child (NOT committed, LEAD commits)
- `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-15.md` (FRESH 15 cluster
  table + per-test traces + recommended priority).
- `rasen/changes/neo-recluster-15/ship-log.md` (this file).
