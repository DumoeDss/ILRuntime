# Ship Log -- neo-recluster-5 (wave-2 child of neo-overhaul)

Branch: `features/object-model-overhaul`. NOT committed (LEAD commits).

## Goal
FRESH-ground the current 5 Neo full-smoke failures, re-cluster, batch-fix the
most tractable singleton. Truth = the full-smoke number.

## Outcome: 5 -> 4 (RegisterVMTest04 FIXED). SUCCESS.

## Fresh grounding (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch)
- PRE:  `Ran 948 tests, 5 failded, 20 ignored, 7 todos`.
- POST: `Ran 948 tests, 4 failded, 20 ignored, 7 todos`.
- The 4 survivors are a STRICT SUBSET of the entry 5 (no new failures).

## The fix: RegisterVMTest04 (interface/abstract IL-callee param-layout alignment)

### Root cause (pinned via a CopyNeoCallArguments diagnostic + JIT dump)
A `callvirt.il` whose static target is an interface/ABSTRACT IL method (no
compiled frame) synthesizes the callee param layout via
`Optimizer.AllocNeoParamInfosFromSignature` -> `AllocateNeoCallParamSlot`. That
sizer does NOT apply natural alignment (it is shared with the CLR-callee branch,
whose autogen ReadNeo* reader expects a CONTIGUOUS no-alignment layout). The
CONCRETE IL impl's frame is built by `JITCompiler.AllocateSlotForType`, which DOES
apply natural alignment. For `ILScrollRect2.SetViewRect(enum, bool, bool, Action)`:
- synthesized: bools at 1 byte -> Action at prim offset 10.
- concrete:    Action reference aligns to 4 -> Action at prim offset 12.

CopyNeoCallArguments wrote Action (-1) to offset 10; the callee read offset 12
(stale residue 0x10000013) -> `ArgumentOutOfRangeException @ List.get_Item` in
Stfld_Ref. Diagnostic proof: `prim[4] dstOff=10` (before) -> `dstOff=12` (after).

This is NOT a >3-arg issue (the 5-arg Push map was correct) NOR a generic-instance
issue (the signature has no T params). It is the Step-11 interface/abstract-callee
layout sizer omitting alignment.

### Fix (1 file, Optimizer.Neo.cs, +52/-6, Neo-gated -> Legacy-neutral)
- New `NeoAlignUp(offset, alignment)` helper.
- New `AllocateNeoIlCalleeParamSlot(type, ref offset, ref refOffset, domain)`:
  applies the SAME per-slot natural alignment as `AllocateSlotForType`
  (byref=4, primitive=GetPrimitiveSize, IL-VT/IL-enum=NaturalAlignment, else 4),
  THEN sizes via the existing `AllocateNeoCallParamSlot`.
- `AllocNeoParamInfosFromSignature` routes the `this` slot (hasThis) and each
  param through `AllocateNeoIlCalleeParamSlot`. The `isNewobj` slot is unchanged.
- The CLR-callee branch is UNCHANGED (still calls `AllocateNeoCallParamSlot`
  directly, preserving the contiguous autogen-reader layout).
- Additive/safe: `AlignUp` of an already-aligned offset is a no-op, so the
  already-aligned interface-dispatch cases (all-4-byte params) are byte-identical.

### Verify
- Stash-toggle airtight: stash Optimizer.Neo.cs ONLY -> rebuild -> RegisterVMTest04
  FAILS (1 failded, exit 127) -> pop -> rebuild -> PASS (0/0).
- Full smoke: 5 -> 4 (RegisterVMTest04 flipped; 4 survivors = strict subset).
- NeoStep: 414/0 (no regression).
- Legacy-neutral: plain `Debug` build = 0 errors (the change is `#if ENABLE_NEO_MODE`
  file-gated; Legacy compiles none of it).
- ILIntepreter.Neo.cs: zero net change (investigation diagnostics removed; `git diff` clean).

## Remaining 4 (each a distinct deep singleton, re-confirmed FRESH; reported honestly)
1. **StructTest6** -- byref out-STRUCT REF-REGION write-back (0-prim+2-ref struct;
   the reflection out-param write-back stores an mStack index in a non-existent
   prim slot; the ref region is never touched). Architectural.
2. **StructTest12** -- JIT generic-param resolution for `Activator.CreateInstance<T>`
   inside a generic method (T resolves to ILTypeInstance instead of MyStruct2) +
   the redirect returns a heap ILTypeInstance not a struct (two coupled bugs).
3. **UnitTest_10051** -- constrained-callvirt property read on a nested struct
   field (`list[0].V2.x.RawValue`; the `.x.RawValue` chain).
4. **MyTest.Test** -- boxed-CLR-struct enumerator interface dispatch (Step-19;
   this-register aliasing across loop iterations; a String ends up in the
   get_Current `this` slot).

Deep cluster table + per-test pinned roots in
`rasen/changes/neo-overhaul/handoff/fullsmoke-ground-05.md`.

## Files (NOT committed, LEAD commits)
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` (+52/-6).
- `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-05.md` (FRESH re-cluster).
- `rasen/changes/neo-recluster-5/ship-log.md` (this file).

Capability = `neo-optimizer` (owns AllocNeoParamInfosFromSignature / the
call-param-map callee-layout sizer; consistent with child-C7 / child-11 lineage).
