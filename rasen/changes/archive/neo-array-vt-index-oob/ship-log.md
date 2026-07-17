# Ship Log — neo-array-vt-index-oob (Wave-2 child C10, partial)

**Change:** Stelem_Any on a CLR value-type-element array (TestVector3[] / generic float[]) read the value slot's first int as an mStack index, but a VT-element slot holds flat managed bytes -> ArgumentOutOfRangeException.
**Capability:** `neo-value-types` (ADDED). **Date:** 2026-07-14. **Base HEAD:** `85f39fbc`.

## Root cause + scope (C10 is MULTI-ROOTED; this fixes the largest sub-cluster)
The Stelem_Any "CLR object array" else-branch (`ILIntepreter.Neo.cs:~5552`) read `mStack[vIdx]`. For a value-type-element CLR array the slot holds the struct's FLAT MANAGED BYTES -> the leading int is the first field's IEEE bits (1.0f = 0x3F800000 = 1065353216) -> `mStack[garbage]` -> ArgumentOutOfRangeException. Fix = discriminate by `GetElementType().IsValueType`: VT -> `ReadNeoValueType` (flat bytes -> boxed) + `Array.SetValue` (mirrors child-26 Stobj array WRITE); reference element -> unchanged mStack-index path (byte-identical to HEAD, no regression). This is the value-type-element counterpart on the **Stelem store side** (prior children 19/24/26/27/29 touched load/byref, not store).

C10 is 6+ DISTINCT roots sharing only the exception type. This child fixes the Stelem_Any sub-cluster (4 tests). The rest are honest follow-ups: Ldfld_R4 bad owner index (StructTest8), Callvirt_CLR List<struct>.Add arg (StructTest11), Stsfld IL-static struct ref-field (UnitTest_10036), Ldind_I4 ins.Primitives OOB (UnitTest_Struct), LowerNeoOffsets "Push" (UnitTest_10051 = cluster C7). CLRBindingTest08 is order-dependent (passes alone, fails in suite -- a test-isolation flake, not an engine bug).

## Verification
- **FULL SMOKE: 122 -> 118 (-4).** ArrayTest05, UnitTest_10035, TestValueTypeBinding.Test03, Test03.TestUsingNested flipped green.
- **Stash-toggle airtight** (stash Neo.cs only): 2 probe TCs FAULT (ArgumentOutOfRangeException at stelem.any store); pop -> PASS.
- **NeoStep 382/0** (380 + 2 probes, 0 regression).
- **Legacy-neutral:** the 4 fixed tests + probes PASS under plain Debug+useRegister=true; diff is file-gated `#if ENABLE_NEO_MODE`.
- LEAD non-author delta-review: read the Stelem_Any branch -- VT-discriminate correct, reference-element path byte-identical to HEAD (no regression).

## Review
APPROVE (LEAD non-author delta-review of the diff + implementer's full-smoke 118/NeoStep 382/stash-toggle/Legacy-neutral). Multi-root honesty: implementer fixed the largest sub-cluster + reported the 5 distinct roots rather than forcing one fix.

## Delivery
local commit + push. No PR.

## Durable findings
1. The Stelem store side needed the same VT-vs-ref discrimination as the load/byref sides (child-19/24/26/27/29). Pattern: `ReadNeoValueType` flat bytes -> `Array.SetValue`.
2. `stelem.any !T` on a generic primitive `T[]` (e.g. float[]) lands in Stelem_Any, NOT Stelem_R4 -- so the VT branch covers generic-primitive stores too. A concrete `float[]` uses Stelem_R4.
3. C10 is genuinely multi-rooted -- the remaining 5 are distinct opcode arms / a C7 JIT bug. Don't assume one fix covers them.
