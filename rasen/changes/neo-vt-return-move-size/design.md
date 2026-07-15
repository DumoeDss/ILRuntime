# neo-vt-return-move-size -- design

Wave-2 child of neo-overhaul. Fixes the residual `UnitTest_TestFCP` failure
(ToColor returns a CLR value type; caller received only the leading field).

## Re-audit DISPROVED the framed root (the handoff's "Move copies 4 bytes")

The fullsmoke-ground-13 handoff pinned the root as: "ToColor's `move r13,r12;
ret r13` -- the Neo `Move` arm copies `ip->Operand2` bytes; in ToColor's
register-allocation context it copies only the leading 4 bytes (x=1)." That
pinning was made in the recluster-13 child via instrumentation that was SINCE
REMOVED, and it pre-dates children 21-29 (several of which seeded producers in
`TypeSpecializeNeoOpcodes`).

A LIVE diagnostic on the `move r13, r12` instruction (temporary, now removed)
showed the OPPOSITE of the pinning:

    VTRETDBG move DstOff=68 SrcOff=56 Operand2(sz)=12 Operand(isRef)=0 SrcBytes=1,1,0

The Move copies 12 bytes (sz=12), is NOT a ref move (isRef=0), and the source
r12 correctly holds (1,1,0). So the Move is CORRECT -- the residual is NOT the
Move size. The pinning was stale.

## The REAL root: the RETURN slot is sized as a boxed reference (4/1)

A diagnostic on the `ret r13` arm showed:

    VTRETDBG ret DstOff=68 retPrimSize=4 retRefCnt=1 srcBytes=1,1,0

`returnPrimitiveSize=4` and `returnRefCount=1` for a 3-float (12-byte) CLR
struct return. The Ret handler (`ILIntepreter.Neo.cs:4327`, the
`returnRefCount != 0` path) then copies only `returnPrimitiveSize` = 4 bytes
to the caller's dest (just x), dropping y/z. ToColor returned (1,0,0) instead
of (1,1,0).

`ReturnPrimitiveSize`/`ReturnRefCount` are computed in `AllocateLocalStackSpaces`
(JITCompiler.cs:2649-2660) via `AllocateSlotForType(retType, ...)`.
`AllocateSlotForType`'s final `else` branch (JITCompiler.cs, pre-fix) lumped a
CLR value type together with reference types: `Size=4, RefCount=1` (boxed
mStack index). That is WRONG under the Neo object model, where a CLR value type
param/return/local is FLAT MANAGED BYTES.

## The three CLR-struct sizing sites (the F-MAJ-1 principle, not fully closed)

There are three places that size a CLR value type's frame slot. Two were
already correct; the third (the bug) was not:

1. CLR-struct LOCAL declaration -- `AllocateLocalStackSpaces` varCnt loop,
   JITCompiler.cs:2521-2527 (the F-MAJ-1 fix, Neo-gated): flat bytes
   (`clrVtSize = Optimizer.GetNeoValueTypeManagedSize(...)`, RefCount=0).
   CORRECT.
2. Call-frame param/return -- `AllocateNeoCallParamSlot`,
   Optimizer.Neo.cs:1742-1764 (Step 13b D1): flat bytes
   (`GetNeoValueTypeManagedSize`, RefCount=managedCount-or-0). CORRECT. This is
   the layout the caller's `CopyNeoCallArguments` writes.
3. Own-frame param/return -- `AllocateSlotForType`, JITCompiler.cs (the `else`
   branch): boxed reference (Size=4, RefCount=1). **BUG.** This is the layout
   the callee's own Ret reads (`returnPrimitiveSize`).

The F-MAJ-1 comment (JITCompiler.cs:2502-2528) states the principle "a local
passed by value and a param agree" -- but F-MAJ-1 only fixed the LOCAL
declaration; `AllocateSlotForType` (params + return) was left at 4/1. This
child closes that gap: the own-frame param/return layout now AGREES with the
local declaration and the call-frame layout.

## The fix (single file, Neo-gated, +27/-1)

Add a CLR-value-type branch to `AllocateSlotForType`'s if/else chain (between
the CLR-enum branch and the boxed-reference `else`), mirroring the LOCAL
declaration (site 1) exactly: `Size = clrVtSize`, `RefCount = 0`. The chain
order means the new branch only catches a CLR value type that is NOT an ILType
(caught earlier) and NOT a CLR enum (caught immediately above) -- i.e. a plain
CLR struct. RefCount stays 0: a CLR struct WITH a registered ValueTypeBinder is
owned by the autogen redirects (which use `AllocateNeoCallParamSlot` for both
caller and callee reads), so this own-frame reflection-fallback path only ever
sees no-binder structs (RefCount 0). This matches the LOCAL declaration.

Effect on the Ret handler: a CLR-struct return now yields
`returnPrimitiveSize=clrVtSize, returnRefCount=0`, so the Ret handler takes the
`returnRefCount == 0` path and `CopyBlock`s the full `clrVtSize` bytes to the
caller's dest. The caller's return-dest slot is already sized >= clrVtSize
(it is a declared CLR-struct local sized by site 1, OR an eval temp sized to
`maxSize` which is grown by `GatherValueTypes` -- and a following `ldfld` of
the returned struct gathers the declaring type, so `maxSize >= clrVtSize`; for
UnitTest_TestFCP the `ldfld r2, r0, ...` gathers TestVector3NoBinding).

Effect on params: by the same change, a CLR-struct by-value param's own-frame
slot is now flat bytes, AGREEING with the caller's `CopyNeoCallArguments` write
layout (AllocateNeoCallParamSlot). This makes the caller-write/callee-read
offsets consistent for CLR-struct params too (a latent correctness gain; no
test in the smoke exercises a >1-param CLR-struct-by-value IL call, so the
full-smoke delta is the 2 return-path flips).

## Why the bonus flip (UnitTest_10046)

`TestValueTypeBinding.UnitTest_10046` does `a = a + One2` via a delegate
(`op_Addition(TestVector3, TestVector3)` returns a TestVector3 CLR struct).
The ground-13 handoff attributed it to "delegate VT-arg marshal (Step-19)", but
the real residual was the same VT-RETURN sizing: the delegate-invoked
`op_Addition` returns a CLR struct whose return slot was sized 4/1, so the sum
came back truncated. The delegate framing was the wrong stack frame. The fix
flips it for free. (Lesson reaffirmed: trace the PRODUCER chain to the byte --
the 12-for-12 lineage.)

## Capability

`neo-value-types` (the Neo frame slot layout for value types; sibling of the
F-MAJ-1 local-declaration fix and the Step-13b AllocateNeoCallParamSlot
param/return sizing).

## Verify (truth = full-smoke number)

- Name-filter `UnitTest_TestFCP`: PASS after fix (stash-toggle: 1 failed on
  HEAD -> 0 after). Confirmed color value is (1,1,0) (the failure-only
  diagnostic print no longer appears).
- FULL SMOKE: **12 -> 10** (`Ran 944 tests, 10 failded, 20 ignored, 7 todos`,
  exit 127 = known graceful Dict-NRE crash; the 10 are a STRICT SUBSET of the
  baseline 12 -- UnitTest_TestFCP + UnitTest_10046 flipped, NO new failures).
  Verified by a stash-toggle full smoke: fix-stashed baseline = 12, fix-applied
  = 10, `Test name:` extraction confirms strict subset.
- NeoStep **410/0** (no regression -- a frame-layout change touches every
  method with a CLR-struct param/return).
- Legacy-neutral: `AllocateLocalStackSpaces` + `AllocateSlotForType` are inside
  an `#if ENABLE_NEO_MODE` block (depth 1; they call the Neo-only
  `GatherValueTypes`/`Optimizer.GetNeoValueTypeManagedSize`, so they cannot
  compile under Legacy otherwise). Empirically: plain Debug Legacy NeoStep =
  410 ran / 19 failed BOTH with and without the fix (identical) -- the 19 is
  HEAD's pre-existing Legacy set.

## Files (NOT committed -- LEAD commits)

- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` (+27/-1): new
  CLR-value-type branch in `AllocateSlotForType`.
