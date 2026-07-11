# Review Report — neo-ret-vt-with-ref-fields

> Reviewer (verifier) = independent of the implementer (author != verifier).
> Date: 2026-07-09. Branch `features/object-model-overhaul`.
> Scope: the `Ret`-opcode value-type-with-reference-fields return fix
> (`Optimizer.Neo.cs` LowerNeoOffsets Ret-case + `ILIntepreter.Neo.cs` Ret arm)
> + the new `TestCases/NeoStepRetVtTest.cs`.

## Verdict: **APPROVE-WITH-FINDINGS**

The fix is correct, load-bearing, and Legacy-neutral. All five subtle
correctness points the review brief named (lowering-order, Operand3 collision,
ref-region contiguity, ref-only-struct edge, caller-side consumption) are
VERIFIED GOOD by independent gate re-runs AND by reading the lowering sequence
and the lowered IL. One permanent test probe was ADDED (ref-only struct edge —
none of the original 6 probes covered it); this is a strengthening addition the
LEAD should commit. Findings below are all Minor / Trivial (no Blocker/Major).

## Gate evidence (independently re-run, NOT the implementer's numbers)

### 1. NeoStep smoke (Debug_Neo CLI + Debug TestCases)
- Build: `dotnet build ILRuntimeTestCLI -c Debug_Neo` → 0 errors.
- Build: `dotnet build TestCases -c Debug` → 0 errors.
- Run: `dotnet run -c Debug_Neo -f net8.0 ... NeoStep` →
  **`Ran 247 tests, 0 failed, 0 ignored, 0 todos`** (implementer's 247/0/0
  REPRODUCED).
- `NeoStepRetVt` filter alone → **`Ran 6 tests, 0 failed`** (6/6 PASS).

### 2. Stash-toggle (load-bearing proof) — REPRODUCED
- `git stash` the two engine files only (test kept). Rebuild CLI (Debug_Neo, 0
  errors). Run `NeoStepRetVt` filter →
  **`Ran 6 tests, 5 failed`**, all 5 ref-field probes failing with the EXACT
  NIE `"Neo return with value-type reference fields requires Step 12/13 return
  layout support."` (OneRef / ManyRefs / ReadAfterReturn / ShallowCopyIndep /
  NestedVtWithRef). The 6th (`PurePrimitiveControl`) PASSES on HEAD as
  expected (pure-primitive path already worked).
- `git stash pop` → rebuild → `NeoStepRetVt` → **`Ran 6 tests, 0 failed`**.
- Conclusion: the fix is genuinely load-bearing for the 5 ref-field probes.
  If it were not, the stash would not reproduce the NIE. Confirmed.

### 3. Legacy-neutral — CONFIRMED
- `dotnet build ILRuntimeTestCLI -c Debug` → 0 errors (Neo files compile out
  via `#if ENABLE_NEO_MODE`; proves Legacy-neutral by construction).
- `dotnet run -c Debug -f net8.0 ... NeoStep` →
  **`Ran 247 tests, 10 failed`**. The 10 failures are ALL pre-existing /
  documented, NONE introduced by this change:
  NeoStep13 (2: ClrStruct box round-trip), NeoStep14 (4: ArgumentOutOfRangeException
  catch edge), NeoStep15 (1: castclass-caught), NeoStep16 (1: Stelem_I),
  NeoStep20 (1: TwoIncompleteAwaits), NeoStep6 (1: NeoNaNR8).
- The 6 `NeoStepRetVt_*` probes: zero failures on Legacy (all PASS).

## Subtle code-review points (the real value)

### 4a. LowerNeoOffsets Ret-case ordering — CORRECT (faithful mirror)
The brief's concern: does `retR1` hold a register INDEX (correct) or a byte
offset (would corrupt the `localInfos[retR1]` lookup) at the stamp point?
Verified by reading `Optimizer.Neo.cs:854-874`:

```
case OpCodeREnum.Ret:
    if (op.Register1 >= 0)
    {
        short retR1 = ResolveLiveAlias(op.Register1).Reg;   // still an INDEX
        op.Operand3 = localInfos[retR1].RefOffset;           // stamped from INDEX
        LowerR1(ref op, localInfos);                         // index -> byte offset HERE
    }
```

`LowerR1` (`:1470-1482`) reads `localInfos[r1].Offset` and sets `op.DstOffset`
— i.e. it is the pass that converts register INDEX → byte offset. The stamp
runs BEFORE `LowerR1`, while `retR1` is still a valid register index. This is
byte-identical in structure to the proven-correct `Initobj` case
(`:816-818`: `r1 = ResolveLiveAlias(...).Reg; op.Operand3 = localInfos[r1].RefOffset;
op.DstOffset = localInfos[r1].Offset;`). Mirror is faithful; Initobj/Move_Vt
are proven by Steps 12/12b green. **No issue.**

### 4b. Operand3 collision — NO COLLISION (opcode-scoped)
`Operand3` is read by many opcodes (Move_Vt, Box, Isinst, Ldfld family, Call
arms, etc.), but each read is WITHIN its own `case` arm and reads the SAME
opcode instance its own lowering pass stamped. No non-Ret opcode ever reads a
Ret instruction's Operand3, and the Ret opcode's runtime arm is the sole
consumer of a Ret's Operand3. The Ret opcode uses only Register1/DstOffset at
runtime (the primitive byte offset); Operand/Operand2/Operand3/Operand4 are
all spare for Ret. The convention is identical to Initobj (Operand3 = target
RefOffset). **No issue.**

### 4c. Ref-region contiguity — RELIES ON SAME INVARIANT AS Move_Vt
The Ret arm copies `returnRefCount` contiguous slots from
`frameRefBase + ip->Operand3` (= the return register's `RefOffset`).
`Move_Vt` (`:577-579`) reads `localInfos[dstReg].RefOffset` as the dst ref-run
base and copies `dstRefCount` contiguous slots — the SAME assumption: a
register's ref slots are a contiguous run starting at its `RefOffset`. This is
the frame-layout invariant (`AllocateLocalStackSpaces` allocates a contiguous
ref run per value-type register). The Ret fix shares Move_Vt's assumption
exactly; if a struct's ref fields could be non-contiguous, Move_Vt would also
break (and Step 12b is green). **No issue.**

### 4d. ref-only struct edge (primSize 0, refCount > 0) — VERIFIED PASSING
A struct with ONLY reference fields (e.g. `struct S { string a; string b; }`,
primSize 0, refCount 2). Runtime arm entry guard
`retDst != null && (returnPrimitiveSize > 0 || returnRefCount > 0)` is TRUE
(`returnRefCount > 0`). The `isSingleReferenceReturn` test is FALSE
(`returnType.IsValueType` is true). The new branch:
`if (returnPrimitiveSize > 0) CopyBlock(...)` correctly SKIPS the zero-size
byte copy; the ref loop runs `returnRefCount` (2) times. `retDst` is non-null
(caller passed a dest) and `retRefBase` is valid.

**Constructive verification:** I ADDED a temporary-then-kept probe
`NeoStepRetVt_RefOnly` (`struct { string a; string b; }`) to
`NeoStepRetVtTest.cs`. Ran it standalone on Debug_Neo →
**`Ran 1 test, 0 failed`** (PASS). Both ref fields survived the return copy
with correct values. **Decision: KEPT as a permanent guard** — none of the
original 6 probes cover the primSize==0 edge (probes 1-5 have a non-zero
primitive payload; probe 6 is pure-primitive with zero refs). This strengthens
the suite; the LEAD should commit it. Full NeoStep smoke with the probe added:
**`Ran 248 tests, 0 failed`** (247 + 1). No regression.

### 4e. Caller-side consumption — VERIFIED (copy-prop eliminates post-call move)
The design's D3 claim (copy-prop eliminates the post-call `move r1, r14`, so
the call dest IS the typed local) is VERIFIED by inspecting the lowered IL of
the ref-field probes:
- `NeoStepRetVt_OneRef`: `call r0, r9, r10, MakeOneRef(...)` writes the return
  DIRECTLY into `r0`; subsequent `ldfld.i4.inline r9, r0, 0x0` (primitive
  read) and `ldfld.ref.inline r9, r0, 0x4` (ref read) both read from `r0`. NO
  intermediate `move`.
- `NeoStepRetVt_NestedVtWithRef`: same — `call r0, r5, r6, r7, MakeNested(...)`
  → `r0`; `ldfld.i4.inline r5, r0, ...` reads `top` from `r0`.
- `NeoStepRetVt_PurePrimitiveControl`: `call r0, r5, r6, MakePurePrim(...)` → `r0`.

The call dest `r0` is typed by the existing JIT (`BuildInitialRegisterTypes` +
`AllocateLocalStackSpaces`) as the IL VT (primSize + N ref slots). The Ret
arm writes primitive bytes to `retDst` (= `r0`'s byte region) and refs to
`retRefBase` (= `r0`'s ref region at `frameRefBase + call.Operand3`). The
caller's `ldfld.ref.inline` reads from the SAME region. **The ref half is NOT
lost** — there is no post-call Move that would need rewriting to Move_Vt. D3
confirmed for the ref-field case end-to-end.

### 4f. Probe adequacy — ADEQUATE (with the added RefOnly probe)
- Ref-field read off the returned struct: YES — probe 1 reads `r.s`,
  probe 2 reads `r.str` + `r.obj`, probe 3 reads `s.s`, probe 4 reads
  `a.s`/`b.s`, RefOnly reads `r.a`/`r.b`. These prove the ref-slot copy
  (not just the primitive) via `ldfld.ref.inline`.
- NON-NULL ref values: YES — `"hello"`, `"world"`, `"expected"`,
  `"first"`/`"second"`, `"alpha"`/`"beta"`, a boxed `123`. A null ref would
  pass even if the ref copy were broken; the probes use non-null interned
  strings / a distinct boxed object, so a broken copy (null/garbage) would
  FAIL the `!=` assertion.
- Identity preservation: probe 2 uses a DISTINCT shared boxed object passed
  in by the caller (not re-boxed at the helper) and asserts `(int)r.obj != 123`
  after unbox — proves the SAME object survived the return copy. (The
  `object.ReferenceEquals` CLR-static Neo gap is correctly worked around per
  the design's Apply findings; unbox + value-compare is the right substitute.)
- Per-call independence (no callee-aliasing): probe 4 makes TWO returns and
  asserts the first is unaffected by the second — proves the return copy
  writes a per-call ref slot into the caller dest (not aliasing the callee
  mStack region that Ret pops).
- The 6th probe (`PurePrimitiveControl`) is a correct regression guard for the
  already-working `returnRefCount==0` CopyBlock arm.

Adequacy is good. The one gap I closed: the primSize==0 edge (probe RefOnly).

## Findings

| ID | Severity | File:line | Description | Suggested fix |
|---|---|---|---|---|
| F1 | Minor (test addition) | `TestCases/NeoStepRetVtTest.cs` (new method `NeoStepRetVt_RefOnly` + struct `NeoStepRetVtRefOnly` + helper `MakeRefOnly`) | Reviewer added a permanent ref-only-struct probe (primSize 0, refCount 2) — none of the original 6 probes covered this edge. Verified PASS (1/1) and full NeoStep 248/0/0. Strengthens the suite. | LEAD to commit this probe alongside the change. (Already in the file — do NOT remove.) |
| F2 | Trivial | `ILIntepreter.Neo.cs:2959` (`int retSrcRefOff = ip->Operand3;`) | The local `retSrcRefOff` is read once and used in the loop; fine. Naming is clear. No correctness issue. | None. |
| F3 | Trivial (doc hygiene, not this change) | `design.md` "Apply findings" mentions the `Ldfld_Value` Step-6 NIE blocks whole-nested-struct-field reads off returned/local structs. | This is a real cross-cutting gap (any nested-struct field read hits `Ldfld_Value`), correctly scoped out of this change and noted as a future Step-12b follow-up. No action for THIS change; tracked in `neo-deferred-items.md`. | None for this change. Confirm the follow-up is recorded in deferred-items (it is, per design.md). |
| F4 | Trivial | `ILIntepreter.Neo.cs:2963` | The ref-slot loop `mStack[retRefBase + i] = mStack[frameRefBase + retSrcRefOff + i]` is a shallow copy (refs shared) — matches C# struct-copy semantics and is documented in the comment. Correct. | None. |

No Blocker. No Major. The fix is correct and the gate evidence is solid.

## Accepted-known notes (not introduced by this change)
- `object.ReferenceEquals` has its own Neo gap (CLR static call) — probes use
  `==`/`!=` on interned strings or unbox + value-compare instead. Documented
  in `design.md` Apply findings.
- `Ldfld_Value` (whole-nested-struct load) is a pre-existing Step-6/12b NIE —
  probe 5 (`NestedVtWithRef`) asserts only the top-level primitive `r.top`
  (which proves the nested return copy populated the outer's primitive span);
  inner-field reads are deferred. Documented in `design.md`.
- The async `get_Task` redirect for `ValueTask<T>` still needs the value-type
  return path (child 4 scope) — explicitly out of scope for this change per
  `design.md` D4; this change UNBLOCKS it.

## Files touched by the change under review
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` (Ret case in
  `LowerNeoOffsets`, ~854-874: stamps return register's RefOffset into spare
  Operand3 before LowerR1).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (Ret arm
  ~2939-2964: replaces the NIE with a Move_Vt-style byte CopyBlock + ref-slot
  loop for value-type-with-ref-fields returns; single-ref-return and
  pure-primitive paths unchanged).
- `TestCases/NeoStepRetVtTest.cs` (NEW: 6 original probes + 1 reviewer-added
  `NeoStepRetVt_RefOnly` = 7 probes total).

## Summary
APPROVE-WITH-FINDINGS. The `Ret` value-type-with-ref-fields fix is correct
(faithful mirror of proven Initobj/Move_Vt lowering + runtime copy), load-
bearing (stash-toggle reproduces the NIE on exactly the 5 ref-field probes),
Legacy-neutral (plain Debug 0 errors; 6 probes PASS on Legacy; 10 pre-existing
Legacy failures unchanged), and adequately probed (ref-field reads + non-null
values + identity + per-call independence; reviewer closed the primSize==0
edge with a kept permanent probe). NeoStep 248/0/0 (247 original + 1 added).
Recommend the LEAD commit the change INCLUDING the `NeoStepRetVt_RefOnly`
probe.
