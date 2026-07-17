# neo-il-struct-box-call-boundary -- ship-log

Shipped (NOT committed -- LEAD commits): the 3-site IL-struct-box-at-call-
boundary fix. Wave-2 child of neo-overhaul.

## Result
- StructTest11: PASS (was ArgumentOutOfRangeException @ List.get_Item).
- TestStructDictionary: PASS (was NRE @ Ldfld_I4 heap arm, downstream of
  the same Add-boxing bug). The "2-for-1" held.
- FULL SMOKE: 15 -> 13 (`Ran 938 tests, 13 failded, 20 ignored, 7 todos`,
  exit 127 = known graceful Dict-NRE crash; the 13 are a strict subset of
  the baseline 15 -- StructTest11 + TestStructDictionary flipped, no new
  failures).
- NeoStep 404/0 (no regression -- load-bearing for a CopyNeoCallArguments
  calling-convention change).
- Legacy-neutral: plain Debug build 0 errors (all changes Neo-gated).

## Stash-toggle (airtight)
Stash the 3 engine files (JITCompiler.cs + Optimizer.Neo.cs +
ILIntepreter.Neo.cs) -> rebuild -> StructTest11 FAIL + TestStructDictionary
FAIL (HEAD) -> pop -> rebuild -> both PASS (fix). Proves the fix is
load-bearing for both tests.

## Key durable finding (D2 -- re-audit vindicated, handoff refined)
The handoff's discriminator ("srcInfo indicates a flat-bytes IL-struct AND
paramType is ILTypeInstance") was IMPRECISE on BOTH halves:
1. srcInfo shape is ambiguous: Anim = {string;float} = 4 prim/1 ref ==
   a reference slot. StackSlotInfo cannot tell an IL-struct from a ref.
2. frame.NeoRegisterTypes is last-write-wins: StructTest11's reused r5
   reads Int32 at the call (the final ldc.i4.s write), NOT the mid-pass
   Anim the Newobj case seeded. The naive read NEVER fires.

The sound fix is a POSITION-CORRECT tracker (`curVtTypes`) maintained
inside the LowerNeoOffsets main loop (which already walks the body in
order): seeded from NeoRegisterTypes (correct for declared locals/params)
+ updated per-instruction (Newobj/Unbox seed, Move/Ldloc*/Ldarg*
propagate, else clear). At the call it reflects the most-recent writer
BEFORE the call -- so r5 = Anim at body 8 (last writer = the body-7
newobj), not the final Int32.

LESSON (recurring): the Neo untyped frame means any "what does this slot
hold" question is a JIT-time DATAFLOW fact. A single forward pass without
phi-merge gives the LAST write, not the value live at a given site. For a
call-site decision, the tracker MUST be advanced in lockstep with the
body walk and READ at the site. The same shape bit child-11 (Brtrue_Ref),
child-16/21 (typed-arith seeding), child-24/29 (the marker-vs-runtime
crux). Future "box/branch/specialize at a site" children should use a
position-correct tracker, not the final registerTypes.

Conservative by design: the tracker clears (misses) struct producers it
does not model (Ldfld/Ldelem/Call returning a struct). A struct-field or
struct-call-result source passed to a reference param would still be
unboxed -- candidate follow-up if a test surfaces it. Never a false
positive (only Newobj/Unbox/Move/Ldloc seed; everything else clears).

## Capability
neo-optimizer (LowerNeoOffsets call-param map-build + the position-correct
type tracker; child-7/11/16/21/23 precedent).
