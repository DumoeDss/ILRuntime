# Ship Log — neo-ldobj-array-element (child 26)

**Change:** `ldobj`/`stobj` on a CLR-struct array element (`arr2[0] += TestVector3.One`, CIL `ldelema; ldobj;
op_Addition; stobj`) — add `Array` branches to BOTH the ldobj READ and stobj WRITE-back VT arms. READ counterpart
of child-24; sibling of child-25. Triage batch-2 R1-Shape-B.
**Capability:** `neo-value-types` (ADDED requirement).
**Pipeline:** small-feature (triage-re-audit -> propose+apply -> verify -> review-clean -> ship).
**Date:** 2026-07-13. **Branch:** `features/object-model-overhaul`. **Base HEAD:** `123c44f6`.

## Re-audit + the marker-vs-runtime crux
`ldobj` on a CLR-struct array element (`UnitTest_10047`, `arr2[0] += TestVector3.One`) hit the `ldobj` VT-arm
NIE ("Owner type: NeoArrElemIntProbe[]") — the arm handled `NeoIsClrObject` but not an `Array` owner.

**Runtime detection is SAFE here — NO JIT marker needed** (decisive contrast with child-24's raw-Ldfld): CIL
`ldobj`/`stobj` operands are ALWAYS managed pointers (ECMA-335 III.4.26/4.29 — no by-value form); the
`objIdx == -1` frame-native case is dispatched by the FIRST branch in each arm BEFORE the Array check;
`NeoIsClrObject` returns false for `Array`; `Ldelema` rejects non-value-type elements → no ref-type collision.
Child-24's raw-Ldfld needed the `NeoRawLdfldArrayElementByRefMarker` because its owner REGISTER held the value
directly (flat bytes vs byref ambiguity) — ldobj/stobj have no such ambiguity.

**A read-modify-write `+=` needs BOTH arms fixed** (symmetric gaps): the lowering is `ldelema; ldobj; op_Addition;
stobj`; fixing only ldobj would just progress to the stobj NIE.

## What shipped (Neo-only, single engine file + helpers + probe, Legacy-neutral)
- **`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`** (+38): a **Stobj (WRITE-back) Array branch**
  (~:5681) + a **Ldobj (READ) Array branch** (~:5794), each between `NeoIsClrObject` and `GetNeoILInstance`.
  Decode `(arrIdx, elementIdx)` from the byref (`DstOffset` for stobj, `SrcOffset` for ldobj); ldobj READ =
  `Array.GetValue(off)` boxes the VT element → `WriteNeoValueType` to dest; stobj WRITE-back = `ReadNeoValueType`
  boxes src → `Array.SetValue(boxed, off)`.
- **`ILRuntimeTestBase/TestFramework/TestVector3.cs`** (+8): int `One` + `operator +` on `NeoArrElemIntProbe` (~:409).
- **`TestCases/NeoStepLdobjArrayElementTest.cs`** (new): TC1 (`arr[0] += One` round-trip) + TC2 (element-index decode).

## Verification
- **NeoStep smoke: 373/0** (371 baseline + 2 probes), no regressions. Sibling families green: child-19/24/25.
- **`UnitTest_10047` PROGRESSES** (was ldobj NIE → now reaches its own value assertion; residual = the pre-existing
  float `op_Addition`-return bug, out of scope, proven unrelated by the int-struct probe).
- **Stash-toggle (airtight):** stash `ILIntepreter.Neo.cs` (probe + int operator+ kept) → 2/2 FAULT (the
  "Owner type: NeoArrElemIntProbe[]" NIE); pop → 373/0.
- **Read-back CORRECT (exact int sums via host):** TC1=302 (101+201), TC2=307 (302+5).
- **Legacy-neutral:** plain `Debug`+`useRegister=true`+NeoStep = 373 ran/18 failed (pre-existing Legacy set; both
  probes PASS under Legacy). 100% `#if ENABLE_NEO_MODE`.

## Review
**APPROVE-WITH-FINDINGS** (reviewer != implementer; 0 Blocker/Major). Runtime-detection-safety verdict:
**GENUINELY SAFE** (verified objIdx==-1-first + NeoIsClrObject-false-for-Array + ECMA-335 always-pointer +
ldelema-rejects-reftype; no silent-corruption window). Both-arms-correct + decode-offsets-correct (SrcOffset
ldobj / DstOffset stobj) + regression-clean all confirmed.
- Minor M1: TC2's combined-sum assertion (307) is invariant to element targeting under a uniform `+= One` (each
  `+=` adds +2 to whichever element; code is correct, TC1 proves index-0 round-trip, realistic decode bugs crash).
  Optional hardening: assert a per-element sum.
- Trivial T1: the `InitBlock`-on-null `else` in the Ldobj Array branch is unreachable (a boxed VT is never null);
  harmless, matches the F-10 pattern. T2: cosmetic local-name prefix.
- Pre-existing (out of scope): float-ctor / float `op_Addition`-return bug (blocks a TestVector3 `+=` probe;
  candidate sibling child); `ldelem.any`/`stelem.any` CLR-struct-array path is separately broken.

## Delivery
**Mode:** local commit (portfolio per-child delivery; push per parent directive). Committed with the
portfolio-run.json + planning-context.md record updates. No PR.

## Durable findings (for future planning)
1. **ldobj/stobj CLR-struct array element: runtime `mStack[objIdx] is Array` detection is SAFE — no JIT marker.**
   The operand is always a byref (ECMA-335; no by-value form); `objIdx == -1` is dispatched first; `NeoIsClrObject`
   is false for Array; `Ldelema` rejects non-value-type elements. This is the decisive contrast with child-24's
   raw-Ldfld (whose owner register held the value directly → flat-bytes-vs-byref ambiguity → marker required).
2. **A read-modify-write `+=` on a CLR-struct array element requires fixing BOTH the ldobj READ and the stobj
   WRITE-back** (symmetric gaps, same `(arrIdx, elementIdx)` decode).
3. **PLAIN `a = arr[i]` on a CLR-struct array lowers to `ldelem.any` (NOT ldobj) and is SEPARATELY broken.**
   `ldobj` is emitted only by the address-taking read-modify-write (`+=`). An ldobj probe MUST force the `+=` shape.
4. **PRE-EXISTING FLOAT BUG (candidate sibling):** `new TestVector3(float,float,float)` yields a ZERO struct and
   `TestVector3.op_Addition`'s float VT-return doesn't write back — blocks a float-field `+=` probe. The int-struct
   probe sidesteps it. (Possibly the same float-corruption/VT-return class as child-21, or a distinct float-ctor
   gap — re-audit if tackled.)
