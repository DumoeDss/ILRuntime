# Ship Log — neo-raw-ldfld-array-element (child 24)

**Change:** raw `Ldfld` on a CLR-struct array element (`x = clrStructArray[i].field;`, CIL `ldelema; ldfld`)
— fix the silent corruption child-19 deferred (the Ldfld counterpart of its Stfld array-element fix).
**Capability:** `neo-value-types` (ADDED requirement).
**Pipeline:** small-feature (re-audit -> propose -> apply -> verify -> review-clean -> ship).
**Date:** 2026-07-13. **Branch:** `features/object-model-overhaul`. **Base HEAD:** `7689c27c`.

## Re-audit CONFIRMED silent corruption + REFUTED the "runtime detection suffices" hypothesis
`x = clrStructArray[i].field;` silently corrupted under Neo — the raw-Ldfld value-type-owner branch
(`ILIntepreter.Neo.cs:3890-3902`) read the owner slot as flat managed bytes via `ReadNeoValueType`, but for
an array element that slot holds the `ldelema`-produced 8-byte byref `(arrIdx, elementIdx)` — the two byref
ints are reinterpreted as the struct's first two fields. No crash, no NIE. The deferred NIE (`:3928`) is in
the REF-type branch (unreachable for a struct-array element) — invisible to NIE-frequency scans.

The framed hypothesis "runtime `mStack[objIdx] is Array` detection suffices (symmetric to child-19 Stfld)"
was REFUTED: a value-type-owner Stfld is ALWAYS a byref (child-19's runtime check is safe), but a value-
type-owner Ldfld is EITHER flat bytes OR a byref — for the flat-bytes shape, dereferencing the first int as
an mStack index is a *constructible* silent-corruption collision (small-non-negative-int field + same-typed
array at that index). So a JIT-time marker is the provably-correct fix.

## What shipped (JIT marker + runtime branch, Neo-gated, Legacy-neutral)
- **`ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`**: const `NeoRawLdfldArrayElementByRefMarker
  = 0x1` (~:207; Operand4 is FREE for raw Ldfld — only OperandLong is set; child-21 seeding reads
  OperandLong only) + a stamp in the `case Code.Ldfld` CLRType else-branch (~:3104):
  `if (ins.Previous != null && ins.Previous.OpCode.Code == Code.Ldelema) op.Operand4 |= ...`. The
  `ldelema` dest register IS the `ldfld` owner register (verified); `readonly.`/`constrained.` are CIL
  prefixes (never between ldelema and ldfld).
- **`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`**: raw-Ldfld value-type-owner branch
  (~:3890) — when the marker is set, decode `(arrIdx, elementIdx)`, `cArr.GetValue(elementIdx)` +
  `f.GetValue(boxedElem)` + marshal to dest by field category (the symmetric READ of child-19's Stfld WRITE);
  the existing flat-bytes `ReadNeoValueType` path moved to `else` (unchanged).
- **`TestCases/NeoStepRawLdfldArrayElementTest.cs`** (3 TCs) + host helper `BuildNeoArrElemProbeArray` in
  `ILRuntimeTestBase/TestFramework/TestClass3.cs`.

## Verification
- **NeoStep smoke: 368/0** (365 baseline + 3 probes), no regressions. Regression families green: child-4
  (raw Ldfld/Stfld CLR-owner, the flat-bytes path now under `else`), child-9, child-19 (Stfld WRITE),
  child-21 (raw-Ldfld-CLR-struct seeding + float probes), child-23, child-13, NeoStep12/13/17 (102 VT).
- **Stash-toggle (airtight):** stash the 2 engine files (probe + helper kept) → 3/3 FAULT (DivideByZero;
  TC1 `a=3,b=1,s=4` = byref-as-struct-bytes); pop → 368/0.
- **Read-back CORRECT (exact values):** TC1=4259 (4242+17), TC2=100 (10+20+30+40, indices 0 and 5),
  TC3=7777 (7+70+700+7000). (The implementer caught+fixed a planner typo: TC3 asserted 1477 but inputs sum
  to 7777 — a FAULTING probe gives no info about whether its PASS constant is reachable.)
- **Marker reliability + Operand4 survival:** verified analytically (ldelema is the immediate CIL
  predecessor; LowerNeoOffsets raw-Ldfld case doesn't touch Operand4; the Push-deletion remap's
  `>removedIndex` guard skips 0x1; TypeSpecialize seeding reads OperandLong only) AND empirically (the
  stash-toggle proves the marker reaches runtime).
- **Legacy-neutral:** structural (100% `#if ENABLE_NEO_MODE`; ILIntepreter.Neo.cs is file-gated) + empirical
  (plain Debug+useRegister=true+NeoStep = 368 ran/18 failed == pre-existing Legacy set; 3 probes PASS under
  Legacy).

## Review
**APPROVE-WITH-FINDINGS** (reviewer != implementer; independent re-run of smoke + stash-toggle +
marker/Operand4/read-back cross-checks).
- **0 Blocker / 0 Major.**
- Minor M1: the 3 probes use int fields only; float/VT/ref field categories on the array-element READ are
  not directly exercised (low risk — reuses the shared dest marshalling child-21 green-covered).
- Minor M2: the array-element READ handles ref-field CLR structs via reflection WITHOUT NIE-ing, whereas the
  flat-bytes path NIEs (Step 13b) — strictly more permissive, not a regression, unreached by probes.
- Trivial T1: an `unaligned.` prefix could theoretically sit between ldelema and ldfld (Roslyn never emits
  it for managed struct-array field access); a HEAD-faithful coverage gap, not a regression.
- Trivial T2 (doc): planning-context called BuildNeoArrElemProbeArray "child-19-added" but it's in this PR.
  T3 (pre-existing, unrelated): `Optimizer.Neo.cs:1234` bare `op.Operand4 == 1;` no-op — out of scope.

## Delivery
**Mode:** local commit (portfolio per-child delivery; push per parent directive). Committed with the
portfolio-run.json + planning-context.md record updates. No PR.

## Durable findings (for future planning)
1. **The Stfld-vs-Ldfld asymmetry is why a JIT marker (not runtime detection) is mandatory for raw Ldfld on
   an array element.** A value-type-owner Stfld is ALWAYS a byref (child-19 used runtime `mStack[objIdx] is
   Array`); a value-type-owner Ldfld is EITHER flat bytes OR a byref — the flat-bytes shape has a
   constructible silent-corruption collision (small-int field + same-typed array). Mark the owner
   representation at JIT time (the recurring Neo pattern: child-11/15/16/21/23).
2. **`Operand4` of the raw `Ldfld` is a safe, free marker slot, disjoint from the four Ldflda markers.** It
   survives every Neo pass — the right tool for any future raw-Ldfld shape discriminator.
3. **A planner-left probe that "FAULTS on HEAD" gives NO information about whether its PASS constant is
   arithmetically reachable.** The implementer MUST hand-check each probe's expected constant against its
   inputs before trusting FAIL→PASS, else a typo'd constant hides behind the HEAD corruption indefinitely.

## Coverage gap (documented, not a regression)
`ins.Previous == Ldelema` covers the direct `arr[i].field` shape; ref-local indirection
(`ref var p = ref arr[i]; p.field`) and an `unaligned.`-prefixed ldfld fall to the flat-bytes path = HEAD's
current behavior (not a new vector).
