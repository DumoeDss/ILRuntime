# Review Report — implement-neo-step12b (Neo Step 12b: Move_Vt + LowerMove)

**Reviewer:** leaf REVIEWER (author != verifier), adversarial.
**Date:** 2026-07-04
**Base:** HEAD = `424b9730` (Step 12 complete); 12b changes uncommitted in working tree.
**Method:** openspec-gstack-review skill (read full diff, two-pass, evidence-based) + the 5 mandated priority checks + independent K1/K2 reproduction via `git stash` baseline probe.

---

## Executive verdict: CLEAN (with 2 accepted-known pre-existing bugs recorded as follow-up, NOT blockers)

The Step 12b change correctly implements whole-value-type copy semantics via a new
`Move_Vt` opcode and a `LowerMove` rewrite pass. The implementation matches the spec
and design. Pass placement, Operand encoding, ExecuteNeo body, BCP/FCP registration,
and the "only refCount>0 dests" minimization are all correct. All 4 shipped tests are
genuine green tests (verified `Move_Vt` is actually emitted and exercised). Full NeoStep
smoke is **41/41 green, 0 regressions**.

The 2 unshipped scenarios (copy-aliasing independence, VT-by-value param passing) are
**independently confirmed PRE-EXISTING bugs** (K1 in FCP, K2 in Step-8 call lowering) —
they fail identically with and without the 12b change. They are correctly out of 12b's
scope. Ship as-is.

**Severity counts:** Blocker 0 · Major 0 · Minor 0 · Trivial 2 (cosmetic/nits).
Plus 2 accepted-known follow-ups (K1, K2) — pre-existing, recorded, not attributed to 12b.

---

## Priority 1 — Move_Vt / LowerMove correctness: VERIFIED CORRECT

### 1a. LowerMove placement (the Step-12 union pitfall) — CORRECT
`LowerMove` runs **inside** `TypeSpecializeNeoOpcodes`, in the `case OpCodeREnum.Move`
block, immediately after the existing `IsNeoReferenceSlot` stamping and
`SetRegisterType` (`JITCompiler.cs:544-547`), well **before** `LowerNeoOffsets`
(which runs later and overwrites Register1/2 with byte offsets). At the LowerMove site
the dest register index `op.Register1` is still a valid register index, so the
`GetRegisterType(registerTypes, op.Register1)` lookup works. Confirmed pass order:
BCP/FCP at `JITCompiler.cs:310-312` → TypeSpecializeNeoOpcodes (Move case ~542) →
AllocateLocalStackSpaces → LowerNeoOffsets. This is exactly the placement the Step-12
round-2 lesson requires. **No union-pitfall regression.**

### 1b. Operand encoding — CORRECT
`LowerNeoOffsets` Move_Vt case (`Optimizer.Neo.cs:178-221`) stamps, all read from
authoritative `localInfos`:
- `Operand2` (offset 12, STANDALONE) = `primSize` (min(src,dst), same rule as plain Move)
- `Operand3` (offset 16, STANDALONE) = dst slot `RefOffset`
- `Operand` (offset 8, aliased with Register3) = src slot `RefOffset`
- `Operand4` (offset 20, STANDALONE) = dst `RefCount`
- `LowerR1R2` writes DstOffset/SrcOffset (offsets 4/6).

**Register3-alias safety CONFIRMED:** `Move_Vt`'s ExecuteNeo body
(`ILIntepreter.Neo.cs:470-484`) reads only `ip->Operand2/Operand3/Operand/Operand4/
DstOffset/SrcOffset` — it **never reads Register3**. LowerR1R2 writes only offsets 4/6
(not offset 8). So reusing `Operand` (offset 8, aliased with Register3) to carry the src
ref base is safe: nothing reads Register3 on a Move_Vt. This mirrors how plain `Move`
already stores its `isRefMove` flag in `Operand` without Register3 conflict. Verified.

### 1c. ExecuteNeo body — CORRECT
`ILIntepreter.Neo.cs:470-484`:
- `if (vtPrimSize > 0) Unsafe.CopyBlock(dst, src, primSize)` — primitive region, guarded
  for zero-primitive VTs.
- Loop `for i in 0..refCount: mStack[frameRefBase + vtDstRefBase + i] = mStack[frameRefBase + vtSrcRefBase + i]`.

**dst/src ref bases are correct and authoritative:** `frameRefBase` is `mStack.Count` at
frame entry (`ILIntepreter.Neo.cs:341`) — the absolute base of this frame's ref region.
`vtDstRefBase`/`vtSrcRefBase` are the slots' `RefOffset` (relative frame-ref offsets).
So `frameRefBase + RefOffset + i` is the absolute mStack index of ref field `i`. This is
**identical to the established `CopyFrameToIL` helper** (`ILIntepreter.Neo.cs:2134-2140`,
`mStack[srcBase + i]` where `srcBase = frameRefBase + refOffset`), which is the
authoritative pattern for reading in-frame VT refs. Move_Vt is consistent with it.

**refCount is bounded** by `localInfos[dstReg].RefCount` (a compile-time layout value),
and the dst ref run was reserved at frame setup (`mStack.Add(null)` × `totalRefSize`,
line 342-343). No OOB write. The src run has the same length (same-type assignment — the
JIT only emits Move_Vt for a same-type Move; C# does not emit cross-type struct Moves),
so the src read is also in-bounds.

**Null convention:** Unlike plain `Move` (which writes an index cell `*(int*)(dst) =
dstIdx/-1` into the byte region), Move_Vt does NOT write any index cell. This is
**correct** for in-frame VTs: their byte region holds ONLY primitives (Step 12 design
1.3, Finding E; `StackSlotInfo.Size = il.TotalPrimitiveSize`). There is no index cell in
the byte region for a VT dest, so there is nothing to write back. The mStack ref write
alone is the complete, correct copy. Verified no consumer (`Box`/`CopyFrameToIL`,
`Ldfld`/`Stfld` inline) reads an index cell from a VT's byte region.

### 1d. The "only refCount>0 dests" gate — CORRECT
`JITCompiler.cs:565-570`: only `dstIl.IsValueType && !dstIl.IsEnum && dstIl.TotalReferenceCount > 0`
rewrites to Move_Vt. Pure-primitive VTs (refCount 0) keep plain `Move`.

**Plain Move's CopyBlock is correct for pure-primitive VTs:** the plain Move arm
(`ILIntepreter.Neo.cs:440-458`) does `CopyBlock(dst, src, Operand2)` with
`Operand2 = min(src,dst) size` and `Operand = isRefMove(0 for a non-ref primitive VT)`.
With `Operand == 0` the ref-copy branch is skipped, so it copies exactly the primitive
bytes. For a `Vector3` (12 bytes, refCount 0) this is a complete value copy. Verified
by `NeoTestPurePrimitiveVtCopy` passing (it asserts the sum and aliasing independence
of the primitive copy). Reference-type Moves and enum Moves are also correctly excluded
(`dstIl.IsValueType` false / `IsEnum` true → not rewritten). **Minimization is sound.**

---

## Priority 2 — ADJUDICATION of K1 (FCP mis-propagates value-type Moves)

**CLAIM:** Pre-existing FCP bug; out of 12b scope.

**INDEPENDENT VERDICT: CONFIRMED PRE-EXISTING, out of 12b scope. Accept as known follow-up.**

### (a) Reproduction — confirmed pre-existing
Wrote a reviewer probe `NeoProbeVtCopyAliasingK1` (`ProbeVt x; x.a=42; ProbeVt y=x; x.a=-5; int r=y.a; assert r==42`):

- **Baseline (12b stashed, Step-12 only):** `DivideByZeroException` — assert FIRED, i.e.
  `y.a` returned `-5`, not `42`. Bug present.
- **With 12b applied:** identical `DivideByZeroException`. 12b does NOT fix it.

Both runs reproduce the bug. It is not introduced by 12b.

### (b) Root-cause reasoning — matches implementer's account
FCP (`Optimizer.FCP.cs`) operates on register-index form, before LowerMove. For a
whole-VT `Move y, x`, FCP's propagation loop (lines 47-183, 187-328) treats it like any
register Move: a later use of register `y` is rewritten to read register `x`
(`ReplaceOpcodeSource(ref Y, 0, xSrc)`, lines 91/245). FCP's kill condition is only
`yDst == xDst` (a write to the **whole** register `y`, lines 159/295) — it does NOT
recognize a field write to `x` via `ldloca x; stfld` as a kill (that writes through an
address, not register `x`). So after `y = x; x.a = -5;`, FCP rewrites the `y.a` read to
`x.a`, reading the mutated `-5`. This is a genuine value-semantics violation.

This is an FCP defect, not a Move_Vt defect. Move_Vt is never even visible to FCP (FCP
runs before LowerMove). Fixing it requires FCP to invalidate VT-Move-derived propagations
on any field write to the source — that is an optimizer change squarely outside 12b's
"VT copy semantics via Move_Vt" scope and outside the roadmap's explicit "ensure
BCP/FCP unaffected" framing.

### (c) Severity call — my honest independent judgment
I considered whether "VT copy semantics" (the spec title) arguably **requires** FCP to
respect value-copy independence, which would make this a Step-12b Major. It does not:
the spec's ADDED requirements (`spec.md` lines 5-29, 73-95) scope the copy semantics to
the **Move_Vt execution** (CopyBlock + per-ref mStack copy) and the **LowerMove
placement**, and explicitly state BCP/FCP "SHALL only ever observe the surviving Moves"
(lines 90-95) — i.e., 12b's contract is with the copy opcode, not with FCP's
propagation correctness. The roadmap item 12b.3 says "ensure BCP/FCP **unaffected**"
— 12b leaves FCP untouched, which honors "unaffected." The bug is FCP's pre-existing
inability to model field-write kills, not a 12b regression.

**However:** this is a serious latent correctness bug. Real-world code `b = a; mutate(a);
read(b.field)` silently returns the wrong value. It will bite any future VT-heavy test.
It must be tracked. **Recommendation: defer (file as a follow-up step/issue against the
optimizer). Do NOT block 12b.** Filing it here as ACCEPTED-KNOWN.

---

## Priority 3 — ADJUDICATION of K2 (Step-8 VT-by-value param copy)

**CLAIM:** Pre-existing Step-8 call-lowering bug; out of 12b scope.

**INDEPENDENT VERDICT: CONFIRMED PRE-EXISTING, out of 12b scope. Accept as known follow-up.**

### Reproduction — confirmed pre-existing
Wrote probe `NeoProbeVtPassedByValueK2` (`ProbeVt x; x.a=77; int r = CalleeK2(x);` where
`CalleeK2(ProbeVt p) => p.a`):

- **Baseline (12b stashed):** `ArgumentOutOfRangeException` at
  `ILIntepreter.Neo.cs:449` — the plain `Move` arm's `mStack[srcIdx]` with a bogus index.
  The call param-setup emits a plain `Move` for the VT argument; that Move reads
  `*(int*)(frameBase + SrcOffset)` (line 445) as a ref index, but for an in-frame VT the
  byte region holds the primitive value `77`, so `srcIdx = 77` → mStack OOB. Exactly the
  implementer's account.
- **With 12b applied:** identical `ArgumentOutOfRangeException` at the same line. 12b
  does NOT fix it.

The call param path does not route through Move_Vt (it uses `NeoCallParamMap` +
`CopyNeoCallArguments`, design §5), so 12b's Move_Vt is mechanically irrelevant to it.
This is Step-8 call-lowering territory. Out of 12b scope. **Defer as ACCEPTED-KNOWN
follow-up against Step 8 / call lowering.**

---

## Priority 4 — BCP/FCP registration of Move_Vt in all 4 helpers: COMPLETE

`Optimizer.Utils.cs` — all 4 helper switches updated with `case OpCodeREnum.Move_Vt:`
falling through with `Move` (src = R2, dst = R1), mirroring how `Move` is registered:
- `GetOpcodeSourceRegister` (`Optimizer.Utils.cs:412`) ✓
- `GetOpcodeDestRegister` (`Optimizer.Utils.cs:727`) ✓
- `ReplaceOpcodeSource` (`Optimizer.Utils.cs:1023`) ✓
- `ReplaceOpcodeDest` (`Optimizer.Utils.cs:1341`) ✓

**No missed switch.** Also registered in `OpCode.cs:85` (`ToString` switch, harmless
diagnostic). Confirmed via `git diff` that these are the only `case OpCodeREnum.Move:`
fallthrough groups in the file and each got a Move_Vt sibling.

As the design notes, BCP/FCP run before LowerMove so they never observe Move_Vt — these
registrations are **defensive** for the inliner and any future body-walking pass. They
return the same src=R2/dst=R1 as Move, which is correct for Move_Vt's
Register1=dst/Register2=src layout. **No regression risk.** (The Step-12 top
regression-risk flag — a missed switch silently mis-treating Move_Vt as having no
src/dst — is fully addressed.)

---

## Priority 5 — Regression + scope: VERIFIED

### Full NeoStep smoke: 41/41 green, 0 regressions
```
Ran 41 tests, 0 failed, 0 ignored, 0 todos   (filter "NeoStep")
Ran 4 tests,  0 failed   (filter "NeoStep12b")
```
Was 37/37 after Step 12; +4 new (NeoStep12b), ZERO existing cases regressed. Matches
implementer's Finding J.

### Scope-fence check — no creep
- No Box/Unbox changes (Step 13): `Box`/`Unbox` lowering cases and ExecuteNeo arms
  untouched. ✓
- No byref (Step 17): `Ldloca`/`Ldflda` no-op arms untouched. ✓
- No VT newobj (Step 18): untouched. ✓
- All new runtime code is behind `ENABLE_NEO_MODE` (the whole TypeSpecializeNeoOpcodes /
  LowerNeoOffsets Move_Vt case / ExecuteNeo arm are in `#if ENABLE_NEO_MODE` regions;
  the OpCodeREnum entry and the 4 Utils switches + ToString are unconditional but are
  pure enum/switch additions with no Legacy behavioral change). ✓

### The 4 shipped tests are VALID green tests (not probing K1/K2)
- `NeoTestPurePrimitiveVtCopy` (refCount 0 → plain Move): asserts field sum = 13 AND
  aliasing independence (`a.x=999; ` re-assert `b` sum still 13). The aliasing mutation
  here is on `a` **before any later `a` use that FCP could misroute** — verified it
  passes on both baseline and 12b, so it does NOT depend on FCP correctness. Genuine.
- `NeoTestVtWithOneRefCopy` (refCount 1): asserts `y.a==42`, `y.b=="hello"`, AND
  `x.a=-1; y.a still 42`. **This DOES mutate source after copy and read dest** — yet it
  passes. Why isn't this K1? Because the field access here lowers to an
  address-based (`ldloca`+`ldfld`/`stfld`) path whose source operand is NOT the whole
  register `y`/`x`, so FCP's `ySrc == xDst` register match does not fire — FCP does not
  rewrite it. K1 only triggers when the dest read is a whole-register read that FCP can
  rewrite. So this test exercises Move_Vt's copy genuinely (the copy itself must be
  correct) without depending on the broken FCP path. Valid green.
- `NeoTestVtWithManyRefsCopy` (refCount 3, the core regression): verified the final
  lowered body contains `13:move.vt r1, r0` — **Move_Vt is actually emitted and run**.
  Asserts all three refs copied. Genuine. (This is the case that was BROKEN before 12b:
  the old single-ref Move dropped 2 of 3 refs. With Move_Vt it copies all 3.)
- `NeoTestNestedVtCopy`: reads only the top-level field `y` of the copy (whole-nested
  reads would hit unimplemented `Ldfld_Value`, design non-goal §6). Genuine for what it
  asserts.

**No shipped test is a no-op or a pass-by-luck on a broken path.** The two genuinely
broken scenarios (K1 aliasing, K2 param-passing) were correctly NOT shipped as red
tests; they are documented inline in the test file (lines 158-181) and adjudicated here.

---

## Minor / Trivial findings (non-blocking)

### T1 — Trivial: `Move_Vt` ExecuteNeo arm doesn't hoist `frameRefBase`/`mStack` (cosmetic)
`ILIntepreter.Neo.cs:471-482`: the block re-reads `frameRefBase` and `mStack` per
iteration implicitly (they're method-scoped locals, so the JIT will keep them in
registers; no perf issue). The existing plain `Move` arm uses the shared `srcIdx/dstIdx`
locals declared at `ILIntepreter.Neo.cs:374`. Move_Vt uses its own `vt*` locals to avoid
clobbering those shared slots mid-case — this is a **correct** choice (avoids aliasing
the shared locals across the CopyBlock+loop), not a defect. **No action.** Noted only
because a future reader may wonder.

### T2 — Trivial: `sz` fallback branch in Move_Vt lowering is dead in practice (nit)
`Optimizer.Neo.cs:210-211`: `int sz = (srcSz>0 && dstSz>0) ? min : (srcSz>0 ? srcSz : dstSz)`.
For a Move_Vt both src and dst are the same VT type, so both `Size`s are equal and > 0;
the ternary's else-branch (the `srcSz>0 ? srcSz : dstSz`) never executes for a valid
Move_Vt. It's a defensive mirror of plain Move's identical fallback (`Optimizer.Neo.cs:168-171`)
and is harmless. **No action.**

---

## Conclusion

Step 12b is **correct, minimal, well-placed, and regression-free**. The implementation
honors the spec's copy-semantics requirements and the design's union-safety and
BCP/FCP-non-interference decisions. The two pre-existing bugs (K1 FCP, K2 Step-8 call
param) are independently confirmed pre-existing and correctly left out of scope, with
honest documentation in both `planning-context.md` Finding K and the test file.

**Ship it.** Track K1 (FCP field-write kill) and K2 (VT-by-value param copy) as
follow-ups for the optimizer / Step-8 call-lowering work.
