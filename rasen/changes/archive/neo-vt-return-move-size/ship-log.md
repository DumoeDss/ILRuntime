# neo-vt-return-move-size -- ship-log

Shipped (NOT committed -- LEAD commits): the CLR-value-type return/param slot
sizing fix. Wave-2 child of neo-overhaul.

## Result
- UnitTest_TestFCP: PASS (was ToColor returned (1,0,0) instead of (1,1,0)).
- UnitTest_10046: PASS (bonus flip -- `a=a+One2` via delegate returns a CLR
  struct; same VT-return root; ground-13's "delegate VT-arg marshal" framing
  was the wrong stack frame).
- FULL SMOKE: 12 -> 10 (`Ran 944 tests, 10 failded, 20 ignored, 7 todos`,
  exit 127 = known graceful Dict-NRE crash; the 10 are a STRICT SUBSET of the
  baseline 12 -- the 2 flips above, NO new failures).
- NeoStep 410/0 (no regression -- a frame-layout change).
- Legacy-neutral: AllocateLocalStackSpaces + AllocateSlotForType are Neo-gated
  (depth 1; they call Neo-only GatherValueTypes/GetNeoValueTypeManagedSize).
  Empirically plain-Debug Legacy NeoStep = 410 ran / 19 failed BOTH with and
  without the fix (identical).

## Stash-toggle (airtight)
Stash JITCompiler.cs ONLY (the single changed engine file) -> rebuild ->
UnitTest_TestFCP FAIL (color (1,0,0)); full smoke 12 failed (the documented
baseline) -> pop -> rebuild -> UnitTest_TestFCP PASS (color (1,1,0)); full
smoke 10 failed. `Test name:` extraction confirms the post-fix 10 is a strict
subset of the baseline 12 (no regression).

## The framed root was WRONG; re-audit pinned the real root
The fullsmoke-ground-13 handoff pinned the residual as "the Neo Move copies
only 4 bytes in `move r13,r12; ret r13`". That pinning pre-dated children
21-29 and was based on instrumentation that had been removed. A LIVE
diagnostic on the Move arm proved the Move is CORRECT (copies 12 bytes, source
holds (1,1,0)). The real root is the RETURN slot sizing: AllocateSlotForType
sized a CLR value type return as a boxed reference (4 bytes + 1 ref), so the
Ret handler copied only 4 bytes (the leading field). (Lesson: re-verify a
stale pinning against a live run before building on it.)

## The fix (single file, Neo-gated, +27/-1)
AllocateSlotForType's final `else` branch lumped a CLR value type with
reference types (Size=4, RefCount=1). Added a CLR-value-type branch mirroring
the CLR-struct LOCAL declaration (the F-MAJ-1 fix, JITCompiler.cs:2521-2527)
and the call-frame layout (AllocateNeoCallParamSlot, Step 13b D1): flat managed
bytes (`clrVtSize`, RefCount=0). This closes the F-MAJ-1 principle ("local and
param agree") which F-MAJ-1 only applied to the LOCAL declaration, not to
AllocateSlotForType (params + return). Now all three CLR-struct sizing sites
agree.

## Capability
neo-value-types (Neo frame slot layout for value types).

## Files
`ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` (+27/-1, the new
CLR-value-type branch in AllocateSlotForType).
