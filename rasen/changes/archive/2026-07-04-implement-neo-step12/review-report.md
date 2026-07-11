# Review Report — implement-neo-step12 (In-frame value types + Inline field access)

**Reviewer:** REVIEWER agent (author != verifier). Adversarial verify pass.
**Date:** 2026-07-04
**Branch:** features/object-model-overhaul
**Method:** spec/design/planning-context read → full diff read → focused scrutiny
on the 9 priority areas → Debug_Neo build + TestCases Debug build + full NeoStep
smoke + targeted JIT/optimizer dumps for the highest-risk patterns.

## Executive verdict: **CLEAN (with Minor findings)**

The implementation is correct for the scope Step 12 claims (field-level access
on in-frame value-type locals, including nested VT writes, VT-with-ref-field,
Initobj-memset, and the heap-path-no-regression guarantee). The single
highest-risk mechanism — the `addrAlias` ldloca/ldflda folding — is correct for
the patterns the C# compiler actually emits and that Step 12 tests. The
deviation from design (discriminator in `TypeSpecializeNeoOpcodes`; call-param
buffer kept contiguous) is sound and the rationale holds under scrutiny.

All findings below are **Minor** (latent / defense-in-depth / untested-edge),
not Blocker or Major. No silent-corruption path was found within the tested
scope. I recommend addressing the top two Minor items (test gaps for escaped-
address and VT-with-long-field) before archiving, because value types are
pervasive and these are exactly the patterns a future step could silently break.

**Severity counts:** Blocker 0 · Major 0 · Minor 5 · Trivial 1.

---

## Verification evidence

- **Builds:** `dotnet build ILRuntimeTestCLI -c Debug_Neo` → 0 errors.
  `dotnet build TestCases -c Debug` → 0 errors.
- **Full NeoStep smoke:** `Ran 36 tests, 0 failed, 0 ignored, 0 todos`
  (reproduced the implementer's claim).
- **Heap-path discriminator verified empirically:**
  `NeoStep12TestHeapObjectFieldAccessUnchanged` JIT dump emits `stfld.i4` /
  `ldfld.i4` (NOT `_Inline`) for `c.x` on a heap class — the operand-value-
  category discriminator correctly leaves heap objects on the heap path.
- **Inline rewrite verified empirically:**
  `NeoStep12TestVector3FieldAccess` emits `stfld.r4.inline` / `ldfld.r4.inline`;
  `NeoStep12TestNestedValueType` emits `stfld.i4.inline` for the
  `ldloca→ldflda→stfld` chain (absolute nested offset folded correctly);
  `NeoStep12TestValueTypeWithReferenceField` emits `stfld.ref.inline` /
  `ldfld.ref.inline` and the null round-trip passes.
- **IL→IL vs CLR call-param layout split verified by reading the call-lowering
  code** (`Optimizer.Neo.cs:560-662`): IL→IL calls copy via the callee's own
  `ilm.CompiledFrame.ParamInfos` (aligned) into the callee frame base; CLR
  calls build a contiguous buffer via `AllocateNeoCallParamSlot`. Both sides
  agree within each path.

---

## Findings

### Minor 1 — Address-alias folding is silently wrong if a `ldloca`/`ldflda` dest ESCAPES (untested, documented out-of-scope but no guard)
**File:** `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs:21-79`
(addrAlias pre-scan), `ILIntepreter.Neo.cs:479-484` (`Ldloca`/`Ldflda` no-op arms).

The `addrAlias` map unconditionally records every `ldloca`/`ldflda` dest whose
source is a non-reference local, and the ExecuteNeo arms for `Ldloca`/`Ldloca_S`
/`Ldflda` are unconditional no-ops. If such a dest is ever consumed by an
instruction the alias model does NOT fold (a `Call` taking a byref/ref param,
`Stind_*`/`Ldind_*`, `fixed`, or stored into a field), that consumer reads the
dest temp's stale/uninitialized bytes as a pointer — silent corruption, not a
throw.

The implementer explicitly documents genuine-byref as out-of-scope (Finding G,
planning-context.md §8). That is an acceptable scope boundary, BUT:
- There is no assertion/guard that DETECTS an escaped address (e.g. a `Ldloca`
  dest that is read by any non-`Ldfld`/`Stfld`/`Initobj`/`Ldflda` consumer).
  Such a method silently mis-executes instead of throwing `NotImplementedException`.
- The `NeoStep12Test` suite has no case that exercises an escape, so a future
  compiler pattern or a Step-12b/13 change could regress this with no signal.

**Why it matters:** this is the #1 silent-corruption vector for value types.
The current code happens to be safe only because the tested methods never
escape a VT address; nothing enforces that invariant.

**Fix (defense-in-depth, low cost):** in the `addrAlias` pre-scan, after
building the map, do a second pass: for every register that is an alias key,
scan its consumers; if any consumer is NOT in {`Ldfld_*`, `Stfld_*`, `Initobj`,
`Ldflda`, `Ldfld_*_Inline`, `Stfld_*_Inline`} (i.e. the address would be
treated as a real pointer), either (a) remove that alias entry and let the
existing Step-6 `NotImplementedException` fire for the byref use, or (b) throw
a clear Step-12-tagged `NotImplementedException("escaped VT address not
supported until byref pointer model")`. Either way the failure becomes loud.
At minimum, add a NeoStep12 test that constructs an escaped-byref pattern and
asserts it throws (so the behavior is pinned, not accidental).

### Minor 2 — No test covers a value type containing a `long`/`double` field (alignment-8 VT)
**File:** `TestCases/NeoStep12Test.cs` (test suite gap).

`NeoStep12Vector3` is three `float` (alignment 4). `NeoStep12WithRef` is
`int` + `string` (alignment 4). `NeoStep12Outer`/`Inner` are `int`-only
(alignment 4). No Step 12 test exercises a VT whose `NaturalAlignment` is 8
(a struct containing `long`/`double`), which is precisely the case the
alignment work (§1.2) was added to make correct, and the case where a missed
`AlignUp` site would misalign a `*(long*)`/`*(double*)` cast in the `_Inline`
arms (`ILIntepreter.Neo.cs:1769-1780`).

**Why it matters:** the alignment machinery in `ILType.NaturalAlignment`,
`AllocateLocalStackSpaces`, and `AllocateSlotForType` are exercised for
alignment ≤ 4 only; alignment 8 is the differentiating case and is untested.
A regression here is silent on x64 (misaligned access works, just slower) and
would only surface on ARM/strict-alignment targets — exactly the "silent
corruption" failure mode the review brief flags as worst-case.

**Fix:** add a `NeoStep12TestLongFieldStruct` with `struct S { long a; int b;
long c; }`, write/read all three fields (incl. the trailing `long` after the
`int` gap, which forces padding + an 8-aligned slot at a non-zero offset), and
assert round-trip. This directly probes §1.2 and §6.4.

### Minor 3 — `ldflda` dest type is not propagated; the inline discriminator relies on temp-reuse coincidence
**File:** `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs:668-681`
(`TypeSpecializeNeoOpcodes` handles `Ldloca`/`Ldloca_S` but NOT `Ldflda`).

The discriminator (`TryRewriteFieldAccessForInline`) keys on the operand
register's value-category. For a nested chain `ldloca V → ldflda f → stfld x`,
the `stfld` operand is the `ldflda` dest. There is NO `case Ldflda:` that
propagates a VT type to that dest. The nested test passes ONLY because Roslyn
reuses the SAME temp register for the `ldloca` dest and the `ldflda` dest, so
the VT type stamped by the `Ldloca` case survives into the `ldflda` dest. If a
future JIT change (or a different compiler/codegen shape) ever emits `ldflda`
into a fresh temp that was not previously a `ldloca` dest, that temp's type
stays default → the discriminator does NOT rewrite → the heap `Stfld_*`/`Ldfld_*`
arm runs on a raw-bytes slot → `GetNeoILInstance` throws `InvalidCastException`
(or, worse, if the stale bytes happen to be a valid index, silent corruption).

**Why it matters:** correctness depends on an undocumented JIT temp-reuse
invariant. The alias-resolution in the optimizer (`Optimizer.Neo.cs:62-72`)
DOES fold `ldflda` chains independently of type, so the OFFSET is always
correct — but the OPCODE SELECTION (inline vs heap) depends on the type
propagation, which is incomplete.

**Fix:** add a `case OpCodeREnum.Ldflda:` in `TypeSpecializeNeoOpcodes` that,
when the source (Register2) is an in-frame VT (resolved through the same
registerTypes, or by noting the source is a VT-typed register), propagates the
field's declared VT type (or the source VT type) to the dest. This makes the
discriminator self-contained rather than dependent on temp reuse. (The
optimizer alias folding already handles the offset; this just makes opcode
selection robust.)

### Minor 4 — `addrAlias` is built in a single forward pass over the linearized body; cross-block register reuse can alias-stamp a register for the wrong block
**File:** `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs:21-61`.

The pre-scan iterates `body` once in linear order, stamping
`addrAlias[dest] = {Reg, Offset}`. If the same temp register is reused as a
`ldloca`/`ldflda` dest in two different basic blocks addressing different
locals, the later stamp wins globally, and a consumer in the earlier block
(that was lowered using `ResolveAddressAlias` AFTER the full map is built)
would resolve to the WRONG local. The lowering loop
(`Optimizer.Neo.cs:443-513`) reads `addrAlias` after the pre-scan completes, so
it sees the final (last-write-wins) map for every instruction regardless of
block.

**Why it matters:** for straight-line code (all current NeoStep tests) the
producer always precedes its consumer in the same block, so the alias is
correct at the consumer. The failure mode requires (a) register reuse across
blocks AND (b) the reused register being an alias dest in both — uncommon but
possible after BCP/FCP copy-elision renumbers registers. This is the same
linear-slot-reuse assumption the existing offset-lowering already makes, so it
is consistent with prior art, but it is worth noting that the new alias map
inherits the assumption without a block-scoped guard.

**Fix (optional, if register reuse across blocks becomes a problem):** scope
`addrAlias` entries to their basic block (clear or chain per-block), or record
the instruction index of the stamping `ldloca` and only honor an alias for
consumers after that index within the same block. Low priority — matches
existing behavior; flag for the Step 12b/pointer-model rework.

### Minor 5 — `NaturalAlignment` for a reference type (class) is computed but meaningless; no harm, just dead code path
**File:** `ILRuntime/CLR/TypeSystem/ILType.cs:423-440, 2167-2174`.

`NaturalAlignment` is computed for every ILType including reference types (the
accumulator runs over instance fields of classes too). The JIT only consults
it for value-type slots (`AllocateLocalStackSpaces` VT branches,
`AllocateSlotForType` `t.IsValueType` branch). For a class, the result is "max
alignment of its instance fields" which is never used. The getter doc-note
says "Only meaningful for value types, but computed for all ILTypes ...
harmless." Confirmed harmless. Mentioning only for completeness.

**Fix:** none required; optionally guard the property with a debug assert, or
document that callers must only read it for value types. Trivial.

### Trivial 1 — `OpCodeREnum` ordering: the 22 new opcodes are inserted between `Ldfld_Value` and the "Step 6 三槽算术" block, not adjacent to their heap siblings
**File:** `ILRuntime/Runtime/Intepreter/OpCodes/OpCodeREnum.cs:958-984`.

Purely cosmetic — the enum value numeric ordering has no behavioral meaning
(the VM switches on enum name). The Ldfld_Inline block sits before the Stfld
heap block in source order. No action needed; noted only because it slightly
obscures the heap/inline pairing when reading the enum.

---

## Priority scrutiny — item-by-item verdict

1. **`addrAlias` address-resolution** — Correct for single-level and the
   tested nested chain (verified by dump + green test). Escape case is the one
   real risk → Minor 1. BCP/FCP order is safe: aliases are computed in
   `LowerNeoOffsets`, which runs AFTER BCP/FCP (those operate on register-index
   form; `LowerNeoOffsets` is the offset-lowering pass). The new opcodes are
   registered in all four BCP/FCP helpers (see item 7). No conflict.
2. **`AllocateNeoCallParamSlot` non-alignment** — Principled and correct.
   IL-callee frame params are aligned (callee reads via its own aligned
   `ParamInfos`); the CLR call-param buffer is contiguous (CLR redirects read
   sequentially via `ReadNeo*`). IL→IL calls copy caller-temp → callee-frame
   using the callee's aligned `ParamInfos` offsets into the callee frame base
   (`Optimizer.Neo.cs:619-662`) — both sides aligned consistently. The
   "leave params unaligned" deviation applies ONLY to the CLR call-param
   buffer, where it is correct. No contradiction with the design's IL-frame
   alignment goal.
3. **`NaturalAlignment` correctness** — Recursion into nested VT, enum-via-
   backing-field, ref→4, floor 1, and exclusion of static fields all verified
   by reading `InitializeFields` (ILType.cs:2027-2174). `AlignUp` applied at
   every listed site: HasThis VT (1256), VT local (1310), CLR-VT-as-ref local
   (1325, align 4), reference local (1333, align 4), primitive local (1349),
   temp loop (1383, maxAlignment), `AllocateSlotForType` (1414-1428). No
   missed site found. (Caveat: the temp loop's `maxAlignment` floor of 4 means
   primitive-only methods have 4-aligned temps — pre-existing, see Minor 2
   context, not a Step 12 regression.)
4. **Operand discriminator** — Correctly keys on the OPERAND register's value-
   category (`operandType is ILType && IsValueType && !IsEnum`), verified in
   `TryRewriteFieldAccessForInline` (JITCompiler.cs:719-736). Heap ILTypeInstance
   operand → no rewrite (verified empirically). Enum operand excluded. The one
   gap is the `ldflda`-dest type propagation (Minor 3).
5. **`_Inline` arm correctness** — Primitive Ldfld reads
   `frameBase + SrcOffset + Operand2` → dest `DstOffset`; Stfld writes dest
   `frameBase + DstOffset + Operand2` from `SrcOffset`. Verified against
   design §2.3. Ref variant: `Ldfld_Ref_Inline` reads source from
   `mStack[frameRefBase + Operand]`, installs into dest temp ref slot
   `mStack[frameRefBase + Operand4]`, writes index/-1 into dest byte region
   (no truncation — design §2.3 note satisfied). `Stfld_Ref_Inline` copies
   `mStack[srcIdx]`/null into `mStack[frameRefBase + Operand]`. Operand
   encoding matches the lowering (`Optimizer.Neo.cs:493-513`). Verified
   end-to-end green.
6. **Initobj ref sub-case** — `Operand3` stamped with slot RefOffset at
   lowering (`Optimizer.Neo.cs:356-358`); ExecuteNeo nulls
   `mStack[frameRefBase + Operand3 + i]` for i in 0..refCnt; primitive memset
   still runs (`ILIntepreter.Neo.cs:1554-1565`). refCnt sourced from
   `ilType.TotalReferenceCount`, sz from `TotalPrimitiveSize`. Correct.
7. **BCP/FCP registration** — All 22 opcodes registered in
   `GetOpcodeSourceRegister` (Ldfld_Inline: single src R2; Stfld_Inline: R1+R2),
   `GetOpcodeDestRegister` (Ldfld_Inline: dest R1; Stfld_Inline: no dest),
   `ReplaceOpcodeSource` (Ldfld_Inline idx0→R2; Stfld_Inline idx0→R1, idx1→R2),
   `ReplaceOpcodeDest` (Ldfld_Inline: R1). Stfld_Inline correctly absent from
   `ReplaceOpcodeDest` (no dest). Verified complete and consistent with the
   heap family registration. This was the top regression-risk flag and it is
   clean; the 36/36 smoke (which includes the small-primitive CLR binding test
   that caught the Finding-I regression during implementation) confirms it.
8. **Regression confidence** — 36/36 is reasonable for the scope claimed, but
   value-type pervasiveness means the two untested patterns (escaped address,
   alignment-8 VT) are real gaps — Minor 1 and Minor 2. The existing NeoStep6-
   11 tests (which use VT locals/params/fields) all remain green, which is the
   strongest regression signal and it holds. Recommend the two added tests
   before archive.
9. **Scope discipline** — CLEAN. No `Move_Vt`/whole-VT copy, no CLR VT
   Box/Unbox/Initobj (CLR Initobj branch still throws "Step 13" at
   `ILIntepreter.Neo.cs:1570`, CLR Box still throws at 1627), no
   `constrained.` handling, no IL VT `newobj`. `Ldfld_Value`/`Stfld_Value`
   correctly NOT given inline variants (12b). All new code is Neo-path only.

---

## Recommendation

**Land the change.** The implementation is correct and the full smoke is
green. Before archive, address **Minor 1** (escaped-address guard/test — the
one silent-corruption vector) and **Minor 2** (alignment-8 VT test — the
differentiating case for the alignment work). Minor 3 (ldflda type
propagation) is a robustness improvement worth doing in the same pass since it
cheaply removes a hidden dependency. Minor 4/5 and Trivial 1 can defer.
