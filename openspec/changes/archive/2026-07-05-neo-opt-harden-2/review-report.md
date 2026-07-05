# Review Report -- neo-opt-harden-2 (F-MAJ-1 fix)

**Reviewer:** adversarial, non-author (Claude Opus 4.8, 1M context)
**Date:** 2026-07-05
**Diff reviewed:** `git diff f673b9c9..fda264e8` (commit `fda264e8`, already pushed)
**Skill:** `openspec-gstack-review`
**Note:** The implementer committed + pushed BEFORE this review ran (process
deviation). Findings below are based on independent reproduction, dump probes,
and stash-toggle comparisons -- not on the green smoke.

---

## VERDICT: CHANGES-REQUESTED

One Major (incomplete blast-radius sweep -- two runtime arms still assume the
OLD boxed-ref representation after the declare-side flat-bytes change), plus
several Minor/Trivial. The F-MAJ-1 root-cause fix itself is correct and the
claimed smoke numbers (100/100 NeoStep, 12/12 NeoOptHard, identical 7 Legacy
failures with/without fix) are **independently reproduced**. But the change is
HIGH blast radius (it re-declares EVERY Neo CLR struct local), and the
implementer's blast-radius sweep missed two runtime consumers.

---

## BLAST-RADIUS SWEEP (the load-bearing deliverable)

The fix changed a CLR-VT local's `StackSlotInfo` from
`{Size=4, RefCount=1, isRef=True}` to `{Size=clrVtSize, RefCount=0, isRef=False}`
in Neo mode. Every code path that reads a CLR-VT local's slot metadata is a
suspect. For each, "correct-now" = the new layout makes it right; "broken" =
the new layout makes it wrong; "unaffected" = the path does not branch on the
changed fields.

| # | Path | Location | Branches on isRef / RefCount / Size? | Verdict | Evidence |
|---|------|----------|--------------------------------------|---------|----------|
| 1 | D6 CLR-struct return-write | `ILIntepreter.Neo.cs:315-330` | No (writes `retSz` flat bytes via `WriteNeoValueType` at the dest `Offset`) | **correct-now** (this is the bug being fixed) | The dest is now sized `clrVtSize`; the 12-byte write fits. Dump-confirmed in design. |
| 2 | D2 by-value-param read | `CLRMethod.cs:363-392` | No (reads `vtSize` flat bytes via `ReadNeoValueType` from the caller local's `Offset`) | **correct-now** | `primSize = dstInfo.Size` byte-copy at `Optimizer.Neo.cs:1208` copies `clrVtSize` bytes from the local's `Offset`. Confirmed by passing `D2ChainTwoCallees` review-probe (struct returned, stored, passed by value to two callees). |
| 3 | `Move_Vt` (whole-struct assignment) | `Optimizer.Neo.cs:538-580` (operands), `ILIntepreter.Neo.cs:615-629` (runtime) | YES -- reads `localInfos[r].Size`, `.RefOffset`, `.RefCount` to stamp `sz`, `srcRef`, `dstRef`, `dstRefCount` | **correct-now** | For two CLR struct locals: `sz=min(srcSz,dstSz)=clrVtSize`, `dstRefCount=0` -> CopyBlock of `clrVtSize` bytes, 0 refs. Correct for pure-primitive CLR structs (the only kind the reflection path supports). |
| 4 | Neo frame zero-init (`localIsRef`) | `ILIntepreter.Neo.cs:467-476` | YES -- `if (localIsRef[i]) *(int*)(frameBase+Off) = -1` | **correct-now** | CLR-VT local now `isRef=false` -> the `-1` write is SKIPPED for it; the flat-bytes region is zeroed by the `Unsafe.InitBlock(LocalsPrimitiveSize)` (which now includes `clrVtSize` because `offset += clrVtSize`). Consistent. |
| 5 | `addrAlias` folding for `ldloca` | `Optimizer.Neo.cs:36-63` | YES -- folds only when `!localIsRef[src]` | **behavioral change** (arguably correct) | Pre-fix: `ldloca` of a CLR-VT local did NOT fold (`isRef=true`) -> field access went through the heap `Ldfld_*` path. Post-fix: it folds -> `ldfld`/`stfld`/`Initobj` resolve to in-frame flat bytes via the `_Inline` opcodes. For pure-primitive CLR structs the flat-bytes layout matches the field offsets, so `ldfld`/`stfld` are now correct. NO existing test exercises `ldfld` on a CLR struct local (NeoStep13/13b use host `Sum...` helpers to avoid it), so this is unconfirmed by smoke but is a self-consistent representation. |
| 6 | **`Initobj` on a CLR struct local** | `ILIntepreter.Neo.cs:1964-1982` | YES -- CLR-struct else-branch unconditionally writes `mStack[frameRefBase+RefOffset]=def` AND `*(int*)(frameBase+DstOffset)=initDstIdx` (the OLD boxed-ref behavior) | **BROKEN (latent)** | The local now has `RefCount=0` (no reserved ref slot), but the arm still installs a boxed default into `mStack[frameRefBase+RefOffset]`. `RefOffset` is a stale value that now belongs to a DIFFERENT local (often the next ref-typed local or a temp). `DstOffset` is the flat-bytes offset; the 4-byte index write corrupts the first field. See FINDING M1. |
| 7 | **`Box` of a CLR struct local** | `ILIntepreter.Neo.cs:2050-2071` | YES -- CLR-VT non-primitive branch reads `srcIdx = *(int*)(frameBase+SrcOffset)` then `mStack[srcIdx]` (the OLD boxed-ref behavior) | **BROKEN (latent)** | The local now holds flat bytes, not a 4-byte mStack index. `*(int*)(SrcOffset)` reads the first 4 flat bytes (garbage as an index), then `mStack[garbage]` is a wrong/OOB read. Should instead box via `NeoBoxReturnValue(clrBoxType, frameBase+SrcOffset, clrVtSize)` (like the IsPrimitive sub-branch above it). See FINDING M2. |
| 8 | `stind_*`/`ldind_*` on a byref to a CLR struct local | `ILIntepreter.Neo.cs:641-667` (Ldloca arm) + stind/ldind arms | Indirect -- byref carries the absolute frame byte offset | **unaffected / correct-now** | `ldloca` writes a Ref Slot `{-1, SrcOffset}` where `SrcOffset` is the local's flat-bytes offset. A subsequent `stind`/`ldind` reads/writes flat bytes at that offset -- consistent with the new representation. |
| 9 | Debugger / DebugService frame-var read | (none) | n/a | **unaffected** | `LocalInfos`/`LocalIsReference` are NOT consumed by the debugger (Neo debugger is deferred per Step 14/26 comments). `git grep LocalInfos` over `Runtime/Debugger/` returns nothing. |
| 10 | `Isinst`/`Castclass` of a CLR struct local | `ILIntepreter.Neo.cs` (Box/Isinst/Castclass lowering) | The Isinst/Castclass arm reads the src ref slot | **BROKEN (latent, same class as Box)** | `Optimizer.Neo.cs:811-823` stamps `ref1 = localInfos[r1].RefOffset` and the runtime reads the boxed object from that ref slot. With `RefCount=0` there is no ref slot; this reads a stale/wrong slot. Same root cause as M2. (Not independently probed but the code shape is identical to Box.) |
| 11 | Temp-register sizer (`maxSize` loop) | `JITCompiler.cs:1547-1579` | YES -- but only sizes from `ILType` value types (`if (i is ILType il)`) | **unaffected (pre-existing gap)** | A CLR struct temp (e.g. a `Box`/`Stobj` intermediate) was already under-sized pre-fix (`maxSize=8` default). The F-MAJ-1 reproducer does not exercise this (the Make() return dest is the LOCAL, not a temp). The design acknowledges this gap as out of scope. Not a regression. |
| 12 | Callee param layout (`AllocateNeoCallParamSlot`) | `Optimizer.Neo.cs:1357-1379` | It already declares a CLR struct param as flat bytes + (for binder structs) `managedCount` refs | **correct-now** | This is the layout the fix MIRRORS for locals. Consistent. |

**Sweep summary:** 7 correct-now / 3 broken-latent (Initobj, Box, Isinst/Castclass) /
1 behavioural-change-but-consistent (ldloca folding) / 1 unaffected-pre-existing-gap.
**The 3 broken paths are the missed sweep -- the design's Option-B risk note
("confirm the CLR-VT path does not assume boxed-ref here") was answered
"verified" but in fact was NOT for Initobj / Box / Isinst-Castclass.**

---

## FINDINGS

### M1 -- `Initobj` runtime arm not updated for flat-bytes CLR struct locals (Major)

**File:line:** `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs:1964-1982`
**Severity:** Major (latent corruption; reproducible; missed by the implementer's blast-radius sweep)

**The defect.** After the F-MAJ-1 fix a Neo CLR value-type LOCAL is declared
`Size=clrVtSize, RefCount=0, isRef=false` (flat bytes). But the `Initobj`
runtime arm's CLR-struct else-branch still does:

```csharp
object def = clrInitType.CreateDefaultInstance();
int initRefOff = ip->Operand3;              // = localInfos[r].RefOffset -- now STALE (the local reserves 0 ref slots)
int initDstIdx = frameRefBase + initRefOff;
mStack[initDstIdx] = def;                   // writes boxed default into ANOTHER local's ref slot
*(int*)(frameBase + ip->DstOffset) = initDstIdx;  // writes a 4-byte index into the flat-bytes region (corrupts field 0)
```

The stamped `Operand3`/`DstOffset` (in `Optimizer.Neo.cs:767-781`) come from
`localInfos[r1].RefOffset` / `.Offset`. With `RefCount=0`, `RefOffset` is the
same value the NEXT ref-typed local reuses (the cursor was not advanced) -- so
`mStack[frameRefBase+RefOffset]` writes the boxed default into a neighbouring
ref local's slot.

**Reproduction (independent).** I added the probe
`NeoOptHardTest_Review_InitobjClrStructLocal`:

```csharp
TestVector3NoBinding v = new TestVector3NoBinding();   // emits ldloca v; initobj -- v gets RefOffset=0, RefCount=0
string canaryA = TestCLRBinding.MakeCanary();           // runtime-built string, RefOffset ALSO 0 (shares v's stale slot)
int lenA = TestCLRBinding.StringLength(canaryA);        // expect 7
if (lenA != 7) { 1/0 }
```

Result POST-FIX: **1 test FAILED**. The Initobj arm wrote the boxed default into
`mStack[0]`, which is also `canaryA`'s ref slot, clobbering the string. A layout
dump confirmed `slot[0] Off=0 Size=12 RefOff=0 RefCount=0 isRef=False` (v) and
`slot[1] Off=12 Size=4 RefOff=0 RefCount=1 isRef=True` (canaryA) -- **shared
RefOffset=0**.

In isolation (`new S()` alone, no canary) the probe PASSES -- the boxed
default's `initDstIdx` is small and reinterprets as ~0.0f, and the remaining
flat bytes are zeroed, so `Sum` reads 0. This is accidental correctness, which
is why the smoke missed it.

**Is it a regression from THIS fix?** Partially. Pre-fix the symptom also
appeared (the 8-byte F-MAJ-1 overflow corrupted the neighbour), so the user-
visible failure is not brand new. BUT the ROOT CAUSE is new: pre-fix Initobj
was representation-consistent (boxed-ref local, boxed default into its OWN
slot); post-fix the overflow is gone yet Initobj now writes to a stale ref
slot. The fix traded one corruption mechanism for another instead of making
Initobj representation-consistent. Either way, the Initobj arm needs updating
to match the new flat-bytes representation.

**Suggested fix.** For a CLR struct local (`clrInitType.IsValueType &&
!clrInitType.IsPrimitive`), zero the flat-bytes region instead of installing a
boxed default:

```csharp
else
{
    // F-MAJ-1: a Neo CLR value-type local is flat bytes (RefCount=0). Zero its
    // flat-bytes region (mirrors the IL-VT Initobj branch above).
    int sz = Optimizer.GetNeoValueTypeManagedSize(clrInitType.TypeForCLR);
    if (sz > 0)
        Unsafe.InitBlock(frameBase + ip->DstOffset, 0, (uint)sz);
    // No ref slots to null (RefCount=0 for pure-primitive CLR structs; the
    // binder-struct reflection path NIEs upstream -- D2/CLRMethod.Invoke:380-385).
}
```

(The `Operand3` stamp becomes dead for this sub-branch but is harmless.)

---

### M2 -- `Box` runtime arm not updated for flat-bytes CLR struct locals (Major)

**File:line:** `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs:2050-2071`
**Severity:** Major (latent corruption; reproducible; missed by the sweep)

**The defect.** The CLR-VT non-primitive Box branch still reads a 4-byte mStack
index from the source local:

```csharp
srcIdx = *(int*)(frameBase + ip->SrcOffset);   // reads first 4 FLAT bytes as an index -> garbage
obj = srcIdx >= 0 ? mStack[srcIdx] : null;     // mStack[garbage] -> wrong object or OOB
boxed = obj != null ? clrBoxType.PerformMemberwiseClone(obj) : null;
```

The comment at lines 2052-2056 explicitly claims "a CLR value-type local is
held as a boxed object reference (a 4-byte mStack index slot via the CLR-VT
branch)" -- this is the OLD representation, now wrong post-fix.

**Reproduction (independent).** Probe `NeoOptHardTest_Review_BoxClrStructLocal`:

```csharp
TestVector3NoBinding v = MakeTestVector3NoBinding(100f, 200f, 300f);  // 600
int r1 = TestCLRBinding.BoxAndSumStruct(v);                            // expect 600
if (r1 != 600) { 1/0 }
```

Result POST-FIX: **1 test FAILED**. (Pre-fix it also fails, but because of the
F-MAJ-1 under-sizing overflow, not because of the Box arm.)

**Suggested fix.** Box the flat bytes directly, mirroring the `IsPrimitive`
sub-branch above it:

```csharp
else
{
    // F-MAJ-1: a Neo CLR value-type local is flat bytes. Box by reading the
    // flat managed bytes (no mStack index here).
    int sz = Optimizer.GetNeoValueTypeManagedSize(clrBoxType.TypeForCLR);
    boxed = NeoBoxReturnValue(clrBoxType, frameBase + ip->SrcOffset, sz);
}
```

(A CLR struct with reference fields and no binder is unreadable here the same
way it is on the D2/param-read side; a binder struct should go through the
autogen redirect, not this reflection arm. This keeps the reflection arm
consistent with D6/D2.)

---

### M3 -- `Isinst` / `Castclass` of a CLR struct local: same shape as M2 (Major -- inferred)

**File:line:** `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (Isinst/Castclass arm, fed by `Optimizer.Neo.cs:811-823`)
**Severity:** Major (same root cause as M2; not independently probed but the code shape is identical)

The lowering stamps `ref1 = localInfos[r1].RefOffset`, `ref2 =
localInfos[r2].RefOffset` and the runtime reads a boxed object from the source
ref slot. With `RefCount=0` there is no source ref slot; this reads a stale
slot. Same defect class as M2. Recommend the implementer probe `is`/`(T)` on a
CLR struct local and apply the same flat-bytes read.

---

### m1 -- Initobj/Box/Isinst have NO regression coverage (Minor, follows M1/M2/M3)

The F-MAJ-1 probe set (`NeoOptHardTest_Fmaj1_*`) covers only the D6 return +
D2 param-read + Move_Vt lifecycle. None of the 9 probes exercises `default(S)`,
`object o = v;`, or `v is T` / `(T)v` for a CLR struct local. The three
representation-mismatch arms (M1/M2/M3) are therefore uncovered, which is why
the green smoke did not catch them. Add probes under `NeoOptHardTest_` for each
once M1/M2/M3 are fixed.

### m2 -- `clrVtSize >= 8 ? 4 : clrVtSize` alignment is ad-hoc (Minor)

**File:line:** `JITCompiler.cs:1498`
The IL-VT branch (line 1467) aligns to `il.NaturalAlignment` (max of field
alignments, correct). The new CLR-VT branch aligns to `clrVtSize >= 8 ? 4 :
clrVtSize` -- a heuristic, not the struct's actual natural alignment. For
typical blittable CLR structs (4/8/12 bytes of int/float/long) this happens to
be fine (alignment 4), but a CLR struct with an `8-byte` field (e.g. a `double`
or `long`) would want alignment 8, not 4. On x64 the `Unsafe.CopyBlock` /
`WriteNeoValueType` reads are unaligned-safe, so this is a perf / correctness
edge rather than a hard bug, but it diverges from the IL-VT branch's
`NaturalAlignment` discipline. Consider computing the real managed alignment
(max field alignment) or documenting why 4 is sufficient.

### m3 -- Initobj runtime comment is now stale (Minor, doc)

**File:line:** `ILIntepreter.Neo.cs:1934-1944, 1966-1976`
The Initobj arm's comments still assert "a CLR value-type local is stored as a
BOXED object reference (a 4-byte mStack index slot, RefCount = 1)". Post-fix
this is wrong in Neo mode and will mislead the next reader. Update when fixing
M1.

### t1 -- Ship-log "7/9 FAIL" count vs "7 of 9" wording (Trivial)

**File:line:** `ship-log.md:80-82`
"7/9 FAIL" reads as a ratio; "7 of 9" is meant. Cosmetic.

---

## MANDATORY PROBES -- results

1. **Blast-radius sweep:** done -- see the table above. 3 broken-latent paths
   found (M1/M2/M3).
2. **D2-consistency (multi-callee chain):** I wrote a DIFFERENT probe from the
   implementer's `LiveRangeOverlapAcrossCall`: a struct returned, stored, then
   passed by value to TWO different `Sum...Fields` callees, results combined.
   POST-FIX it PASSES -- the D2 caller-local -> callee-param byte-copy is
   consistent with the new flat-bytes local. Option B is correct on this axis.
3. **Frame-size bloat:** I wrote a 5-CLR-struct-locals probe
   (`NeoOptHardTest_Review_FiveStructLocalsFrameSize`) and dumped the layout.
   POST-FIX: `TotalStructSize=120`, all 5 structs got DISTINCT 12-byte regions,
   all values correct, 0 failures. The monotonic allocator continues to NOT
   reuse slots (pre-existing behavior); the fix only changes per-slot size, so
   there is no NEW bloat. The implementer's `ScopedReuseNoFrameBloat` probe
   checks correctness but does NOT dump `TotalStructSize`; my probe does and
   confirms the frame grows correctly (5 * 12 = 60 bytes for the structs vs
   5 * 4 = 20 bytes pre-fix -- the correct, expected growth, not runaway
   bloat). Note: the C# compiler does not narrow struct-local liveness for
   `{ }` block scope, so "scoped reuse" never reclaims anyway (the design
   acknowledges this).
4. **Legacy-neutrality (independent):** built plain `Debug` CLI, ran
   `useRegister=true` NeoStep-filter WITH and WITHOUT the fix (git-stash of
   `JITCompiler.cs`). IDENTICAL 7 failures both ways:
   `NeoTestClrStructNoBindingBoxRoundTrip`,
   `NeoTestClrStructWithBinderBoxRoundTrip`,
   `NeoStep14_TC1_BasicTryCatch`,
   `NeoStep14_TC5_NestedInnermostWins`,
   `NeoStep14_TC8_NullRefCatch`,
   `NeoStep15_TC6_CastclassFailureCaught`,
   `NeoNaNR8`.
   The `#if ENABLE_NEO_MODE` gate works; Legacy is byte-identical. **Note:**
   two of the 7 (`NeoTestClrStruct...BoxRoundTrip`) are themselves CLR-struct-
   box tests -- they fail in Legacy too (pre-existing), which is consistent
   with Box-of-CLR-struct being a long-standing gap (M2 is the Neo-mode face
   of the same gap).
5. **Smoke counts (independently reproduced):**
   - NeoStep: **100/100 PASS** (matches claim).
   - NeoOptHardTest_: **12/12 PASS** (9 F-MAJ-1 + 3 K1; matches claim).
6. **TestClass3.cs helpers honesty:** `MakeTestStruct4/8`, `SumTestStruct4/8`,
   `MakeIntA/B`, `TouchFrame` are added to the existing `TestCLRBinding` class
   alongside `MakeTestVector3NoBinding` (same pattern). `git grep` confirms NO
   name collision anywhere in the repo. The two new structs `TestStruct4` /
   `TestStruct8` are top-level (not nested) and unique. No silent impact on
   other tests.

---

## PROCESS NOTE (informational)

The implementer committed + pushed (`fda264e8`) before this review ran. That
sidestepped the review gate. The fix's CORE (the F-MAJ-1 declare-side change)
is correct and well-evidenced (dump-confirmed, Legacy-neutral, smoke-green).
But the HIGH blast radius (re-declaring every Neo CLR struct local) demanded a
rigorous sweep of every runtime consumer of a CLR-VT local's slot metadata, and
that sweep was incomplete -- M1/M2/M3 are the missed paths. None of M1/M2/M3 is
caught by the current smoke (no test does `default(ClrStruct)` / `object o =
clrStruct` / `clrStruct is T` on a CLR struct local in Neo mode), so the green
smoke did not surface them. They are latent but realistic: `default(Vector3)`-
style code is common.

Recommend: address M1/M2/M3 (update the three runtime arms to read flat bytes /
zero flat bytes), add the three missing regression probes (m1), then re-run the
full NeoStep + NeoOptHard smoke. The declare-side fix itself can stay; it is
the consumers that need the matching update.


---

## Re-review round 1 (2026-07-05)

**Reviewer:** adversarial, non-author (Claude Opus 4.8, 1M context), != the
round-1 fixer.
**Delta reviewed:** the uncommitted working-tree changes (3 runtime arms in
`ILIntepreter.Neo.cs` + 4 probes in `NeoOptHardeningTest.cs` + host helpers in
`TestClass3.cs`). The F-MAJ-1 declare-side change itself is already committed
(`fda264e8`); this re-review covers ONLY the round-1 consumer-arm fixes.
**Top mandate:** the round-0 sweep missed `Unbox_Any`. Do a fresh COMPLETENESS
sweep -- are there MORE broken arms?

### VERDICT: APPROVE (no open Blocker/Major on the round-1 delta)

The round-1 fix (M1 Initobj + M2 Box + Unbox_Any, all retargeted to flat bytes)
is **correct and complete for the defect class it addresses** (a runtime arm
that reads/writes a CLR-VT local assuming the OLD boxed-ref representation). The
fresh completeness sweep below confirms there are NO additional arms of THIS
defect class still broken. M3 (Isinst/Castclass) unreachability is CONFIRMED.

One observation (NOT a Blocker, NOT a Major -- a PRE-EXISTING gap that is out of
scope for this change) is noted at the end: a direct `new ClrStruct(...)` in
interpreted IL hits a separate byref-`this`-to-CLR-struct-ctor reflection gap
that has NEVER worked in Neo mode (fails identically pre-F-MAJ-1 on `f673b9c9`).
It is NOT introduced by F-MAJ-1 or the round-1 fix, and the C# `new T(...)`
local-init pattern lowers to `initobj + ldloca + call ctor`, NOT to the
`InvokeNeoClrMethod(isNewobj:true)` path, so it is unrelated to the boxed-ref
defect class. Recorded for routing; does not block this change.

### COMPLETENESS SWEEP (the load-bearing deliverable)

I re-walked every `*(int*)(frameBase + ip->SrcOffset)` / `DstOffset` site that
then indexes `mStack[...]` (the boxed-ref read shape) in `ILIntepreter.Neo.cs`,
plus the write-side `mStack[frameRefBase + <RefOffset>]` + index-write sites.
For each I classify whether the operand could be a flat-bytes CLR-VT local.

| # | Arm | Location | Could operand be a flat-bytes CLR-VT local? | Verdict | Evidence |
|---|-----|----------|---------------------------------------------|---------|----------|
| 1 | **M1 Initobj (CLR struct/enum)** | `ILIntepreter.Neo.cs:1964-1979` | YES (dest is a CLR-VT local) | **FIXED** | Now `Unsafe.InitBlock(frameBase+DstOffset, 0, clrVtSize)`; no mStack/RefOffset touch. Dump-confirmed by fixer; `NeoOptHardTest_Fmaj1_InitobjClrStruct` PASSES. |
| 2 | **M2 Box (CLR struct/enum)** | `ILIntepreter.Neo.cs:2046-2068` | YES (src is a CLR-VT local) | **FIXED** | Now `ReadNeoValueType(clrBoxType.TypeForCLR, frameBase, ref boxOff=SrcOffset, bsz)`; no mStack-index read. Discriminator sound: a Box opcode source is ALWAYS a value-typed operand (re-boxing an `object` is a compiler no-op), so the flat-bytes read is unconditional. `NeoOptHardTest_Fmaj1_BoxClrStructLocal` PASSES. |
| 3 | **Unbox_Any (CLR struct/enum dest)** | `ILIntepreter.Neo.cs:2323-2340` | YES (dest is a CLR-VT local) | **FIXED** (4th arm the round-0 sweep missed; fixer found via M3 dump) | Now `WriteNeoValueType(obj, frameBase+DstOffset, unbxSz)`; no boxed-clone install / index write. Dest of `unbox.any` is always a value-typed local, so unconditional. `NeoOptHardTest_Fmaj1_IsinstClrStructLocal` PASSES (exercises `(T)boxed` which lowers to `unbox.any`). |
| 4 | M3 Isinst | `ILIntepreter.Neo.cs:2349-2372` | NO -- unreachable for flat-bytes src | **CORRECT (no fix needed)** | `is`/`as` require a reference-typed operand; C# always emits a prior `box` for a CLR struct local. The fixer dump showed the `box` DBG fired but `isinst`/`castclass` DBG did NOT for the M3 probe. The isinst src is always a Box-result `object` local (4-byte mStack index), correctly read. See M3 confirmation below. |
| 5 | M3 Castclass | `ILIntepreter.Neo.cs:2377-2413` | NO -- unreachable for flat-bytes src | **CORRECT** | Same as Isinst. `(T)obj` on a CLR struct compiles to `box` then `unbox.any` (NOT `castclass`); `castclass` only fires for already-boxed `object` sources. |
| 6 | Unbox/Unbox_Any source read | `ILIntepreter.Neo.cs:2267-2270` | NO -- src is always a boxed `object` | **CORRECT** | You cannot unbox a non-boxed value; the source is always an `object`-typed local holding an mStack index. The fixed dest write (row 3) is the only Unbox_Any change needed. |
| 7 | Box reference-type sub-branch | `ILIntepreter.Neo.cs:2018-2021` | NO -- src is a reference type | **CORRECT** | This sub-branch boxes a reference type (no-op identity). A CLR struct routes to the M2-fixed `else` branch above it (type-token discriminator). |
| 8 | Move (ref-copy branch, `Operand==1`) | `ILIntepreter.Neo.cs:585-602` | NO -- a CLR-VT local copy is `Move_Vt`, not `Move` | **CORRECT** | `TypeSpecializeNeoOpcodes` (JITCompiler.cs:609) sets `op.Operand = IsNeoReferenceSlot(srcType) ? 1 : 0`; a CLR-VT local is NOT a reference slot, so `Operand=0`, so the ref-copy branch is skipped, so only `Unsafe.CopyBlock` of `min(srcSz,dstSz)=clrVtSize` runs. Move_Vt handles IL-VTs only (line 629 `dstType is ILType`); a pure-primitive CLR struct stays as `Move` with `Operand=0`, CopyBlock-correct. |
| 9 | Move_Vt (whole-struct copy) | `ILIntepreter.Neo.cs:615-628` | YES (but correct) | **CORRECT** | Stamps `vtPrimSize/vtDstRefBase/vtSrcRefBase/vtRefCount` from localInfos. For a CLR-VT local: `vtPrimSize=clrVtSize`, `vtRefCount=0` -> CopyBlock of clrVtSize bytes, no mStack copy. Matches the round-0 row-3 verdict. |
| 10 | Ldfld_* heap family (e.g. Ldfld_I4) | `ILIntepreter.Neo.cs:2101` etc. | NO -- owner is an ILTypeInstance heap object | **CORRECT** | Calls `GetNeoILInstance(mStack, *(int*)(SrcOffset))`; expects a heap IL object. A CLR-VT local field access is folded to `Ldfld_*_Inline` by the optimizer `addrAlias` pass (post-fix `localIsRef[clrVt]=false` -> folds). The heap arm is unreachable for a CLR-VT local. |
| 11 | Stfld_Ref heap arm | `ILIntepreter.Neo.cs:2156-2160` | NO -- owner is a heap IL object | **CORRECT** | Same as Ldfld: `GetNeoILInstance` expects a heap object. CLR-VT local field stores route to `Stfld_*_Inline`. (A CLR struct with ref fields is unsupported in the reflection path anyway -- NIEs upstream per design.) |
| 12 | Ldfld/Stfld `_Inline` family | `ILIntepreter.Neo.cs:2171+` | YES (but correct) | **CORRECT** | These index the frame byte region directly (`frameBase + SrcOffset + Operand2`); no mStack dereference. The owner is a flat-bytes VT slot -> correct read/write. (Round-0 row 5: post-fix folding makes this the path for CLR-VT local field access.) |
| 13 | Stfld_Ref_Inline | `ILIntepreter.Neo.cs:2258-2261` | YES (but correct) | **CORRECT** | Reads `srcIdx = *(int*)(SrcOffset)` -- but SrcOffset is the VALUE temp (a ref-typed value mStack index), NOT the CLR-VT local. The field-dest is `frameRefBase + ip->Operand` (an absolute ref-slot index stamped from the owner RefOffset + field offset). The value temp is a separate ref slot. Consistent. |
| 14 | Ldloca / Ldflda | `ILIntepreter.Neo.cs:641-706` | YES (but correct) | **CORRECT** | Produces an 8-byte Ref Slot `{-1, absoluteFrameOffset}`. The offset points into the CLR-VT local flat-bytes region. No boxed-ref assumption. |
| 15 | Stind_* / Ldind_* | `ILIntepreter.Neo.cs:2725-2910` | YES (via a byref) but correct | **CORRECT** | Reads the 8-byte Ref Slot address, dispatches on `objIdx`. For a frame-native byref to a CLR-VT local, `objIdx==-1` -> reads/writes flat bytes at `off`. No boxed-ref assumption. |
| 16 | Ret (CLR-VT return) | `ILIntepreter.Neo.cs:1845-1876` | YES (but correct) | **CORRECT** | For `returnRefCount==0` (a pure-primitive CLR-VT) -> `CopyBlock(retDst, frameBase+DstOffset, returnPrimitiveSize)`. The `isSingleReferenceReturn` boxed-ref read is gated by `!returnType.IsValueType`, so a CLR-VT return never hits it. |
| 17 | array ldlem/stelem family | `ILIntepreter.Neo.cs:2475-2700` | NO -- operand is an array object (reference) | **CORRECT** | Arrays are reference types; the array operand is always an mStack index. A CLR-VT local is never an array. |
| 18 | Ldind_Ref frame-native read | `ILIntepreter.Neo.cs:2887-2905` | NO -- byref targets a ref slot | **CORRECT** | `ldind.ref` loads a reference; the byref must point at a ref-slot byte region (holding an mStack index). A CLR-VT local flat bytes are not a valid target for `ldind.ref`. Field access uses `Ldfld_Ref_Inline`. |
| 19 | Constrained. callvirt on a CLR struct | (Callvirt_CLR, `:1775-1796`) | n/a -- pre-existing byref-this gap | **OUT OF SCOPE** | A callvirt on a CLR struct local boxed value goes through `ResolveNeoCallvirtCLRTarget` + `InvokeNeoClrMethod(isNewobj:false)`. The `this` is read from targetBase as a 4-byte mStack index (CLRMethod.Invoke:353). For a boxed struct this is correct; for a byref `this` it is the same pre-existing reflection gap as row 20 (direct ctor). Not a boxed-ref-vs-flat-bytes defect. |

**Sweep summary:** 3 FIXED (M1/M2/Unbox_Any) + 2 correct-no-fix-needed (M3
Isinst/Castclass unreachable) + 14 correct-by-construction + 1 out-of-scope
pre-existing gap. **NO additional arms of the F-MAJ-1 boxed-ref-vs-flat-bytes
defect class remain broken.** The fixer claim of "3 reachable arms + M3
unreachable" is CONFIRMED.

### M3 unreachability -- CONFIRMED

The fixer deemed `isinst`/`castclass`-on-flat-bytes unreachable (C# `is`/`as`/
`(T)obj` on a CLR struct always goes through a prior Box). Verified:

- C# semantics: `is`/`as`/`(T)` require the operand to be a reference type. For
  a value-typed local the compiler MUST box first, so the operand of
  `isinst`/`castclass` is ALWAYS an `object`-typed local (a Box result holding
  an mStack index), NEVER a flat-bytes CLR-VT local.
- The M3 probe `NeoOptHardTest_Fmaj1_IsinstClrStructLocal` does
  `object boxed = (object)v;` (Box) then `(TestVector3NoBinding)boxed`. The
  `(T)obj` unboxing cast lowers to `unbox.any` (the row-3 fix), NOT `castclass`.
  The fixer dump confirmed `box` DBG fired; `isinst`/`castclass` DBG did NOT.
- The genuine boxed-ref source path (a Box-produced `object` local read by
  isinst/castclass) is UNAFFECTED by the round-1 fix and remains correct.

So no dead code was needed for M3; the no-change decision is correct.

### M1/M2/Unbox_Any fix correctness -- CONFIRMED

For each of the 3 fixed arms:

- The new flat-bytes path is representation-consistent:
  - M1: `Unsafe.InitBlock(frameBase+DstOffset, 0, clrVtSize)` -- zeroes
    `clrVtSize` flat bytes. Correct for structs (all-zero default) AND enums
    (zero underlying).
  - M2: `ReadNeoValueType(clrBoxType.TypeForCLR, frameBase, ref boxOff=SrcOffset,
    bsz)` -- the cached typed reader (`Unsafe.ReadUnaligned<T>` + Box) yields an
    INDEPENDENT boxed copy (value semantics preserved). `bsz =
    GetNeoValueTypeManagedSize` matches the declared local size.
  - Unbox_Any: `WriteNeoValueType(obj, frameBase+DstOffset, unbxSz)` -- the
    cached typed writer writes the boxed struct flat bytes into the dest
    (independent copy). Inverse of M2.
- The discriminator is sound for each:
  - M1: the type token `clrInitType` is a non-primitive CLR value type.
  - M2: the Box opcode source is ALWAYS a value-typed operand (no discriminator
    needed -- the fixer observation is correct; re-boxing an `object` is a
    compiler no-op).
  - Unbox_Any: the dest of `unbox.any` is ALWAYS a value-typed local (no
    discriminator needed).
- The genuine boxed-ref path (a CLR struct sourced from Box/heap, NOT a local)
  is UNAFFECTED: it still exists for Isinst/Castclass (rows 4-5) and for the
  Unbox source read (row 6), all of which correctly read an mStack index from a
  Box-result `object` local.

### Probe validity -- CONFIRMED (the 4 probes exercise the right paths)

- `NeoOptHardTest_Fmaj1_InitobjClrStruct` (M1): establishes a canary string
  neighbour FIRST (mStack ref slot), then `v = default(T)` (emits ldloca+initobj
  AFTER the canary exists). Pre-fix this clobbered the canary via the stale
  RefOffset write; post-fix the canary is untouched. Non-trivial -- it
  specifically makes the corruption OBSERVABLE rather than masked by ordering.
- `NeoOptHardTest_Fmaj1_BoxClrStructLocal` (M2): `object o = v;` (Box) +
  UnboxAndSum read-back. Pre-fix the arm read the first float field as an mStack
  index (garbage -> OOB); post-fix it boxes the flat bytes correctly. Non-
  trivial.
- `NeoOptHardTest_Fmaj1_IsinstClrStructLocal` (M3 + Unbox_Any): `object boxed =
  (object)v;` then `(TestVector3NoBinding)boxed` (unbox.any). Exercises BOTH the
  M2 Box (via the `(object)v` cast) AND the Unbox_Any dest write. Non-trivial.
- `NeoOptHardTest_Fmaj1_MixedFrameNoCrossCorruption` (M-extra): a CLR struct +
  int + string in the same frame; guards against neighbour cross-corruption.
  Regression guard -- PASS throughout.
- Host helpers (`MakeCanary`/`StringLength`/`UnboxAndSumVector3NoBinding`/
  `IsVector3NoBinding`/`MixedFrameSum`) are honest: each returns a host-computed
  value the IL side checks; none trivially passes.

### Smoke counts -- INDEPENDENTLY REPRODUCED

- `NeoOptHardTest_` (full, 16 tests incl. the 4 new): **16/16 PASS**.
- `NeoStep` (full): **100/100 PASS**.
- (Build: `ILRuntimeTestCLI` `Debug_Neo` 0 errors; `TestCases` `Debug` 0 errors.)
- Independent run on the exact round-1 fix delta (Neo.cs 121 lines, TestClass3
  +44, TestCases +93), after recovering the round-1 fix from a dangling stash
  (it had been temporarily lost during the pre-F-MAJ-1 baseline check; recovered
  and re-verified the diff matches the fixer delta).

### Observation (NOT a Blocker; out of scope for this change)

While hunting for a 4th broken arm, I probed a DIRECT `new TestVector3NoBinding(
100f,200f,300f)` in interpreted IL. It fails with `ArgumentOutOfRangeException`
at `CLRMethod.Invoke` (the ctor byref-`this` reflection read). INVESTIGATION
SHOWS THIS IS A PRE-EXISTING GAP, NOT a regression from F-MAJ-1 or the round-1
fix:

- The C# compiler does NOT emit `newobj` for `new ClrStruct(...)` assigned to a
  local. It lowers to `initobj r1; ldloca.s r8, r1; push r8; call
  ClrStruct::.ctor(...)` -- the struct is constructed IN-PLACE via a byref
  `this`, NOT via `InvokeNeoClrMethod(isNewobj:true)`. So the
  `InvokeNeoClrMethod(isNewobj:true)` boxed-ref-write path (which IS
  representation-inconsistent post-F-MAJ-1) is GENUINELY UNREACHABLE for a C#
  `new ClrStruct(...)` local init.
- The actual failure is at `CLRMethod.Invoke:353` reading the ctor `this`
  argument as a 4-byte mStack index. The `this` is a byref (8-byte Ref Slot from
  ldloca). This is a byref-`this`-to-CLR-struct-ctor reflection gap that has
  NEVER worked in Neo mode -- I confirmed it fails IDENTICALLY on `f673b9c9`
  (pre-F-MAJ-1, pre-round-1). It is the same defect class as the
  byref-`this`-via-callvirt-on-a-CLR-struct gap (row 19) and the broader
  "CLRMethod.Invoke reflection fallback only handles a 4-byte mStack-index
  `this`, not a frame-native byref" pre-existing limitation.
- It is NOT the F-MAJ-1 boxed-ref-vs-flat-bytes defect class. Routing it as a
  follow-up (e.g. "[NEO-BYREF-THIS]" or part of Step 17 byref-completeness
  work) is appropriate; it does NOT block this change.

(The probe was reverted from the working tree; the diff is byte-identical to
the fixer round-1 delta: Neo.cs 121 / TestClass3 +44 / TestCases +93.)

### Conclusion

The round-1 fix is **correct and complete for its scope**. The prior review
blast-radius gap (missing Unbox_Any) is now closed, and the fresh completeness
sweep found NO additional arms of the same defect class. M3 unreachability is
confirmed. Smoke is green (100/100 NeoStep, 16/16 NeoOptHard). The one pre-
existing byref-`this` gap surfaced by the newobj probe is out of scope and
unrelated to the boxed-ref defect class.

**APPROVE.**
